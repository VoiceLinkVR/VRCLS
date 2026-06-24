using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Audio;
using Serilog;

namespace VRCLS.Services.Tts
{
    /// <summary>
    /// 服务器 TTS 服务
    /// 调用开发者服务器 /func/tts API
    /// </summary>
    public class ServerTtsService : ITtsService, IDisposable
    {
        public const string HttpClientName = "VRCLS.TtsApi";
        private static readonly ILogger _logger = Log.ForContext<ServerTtsService>();
        private readonly HttpClient _httpClient;
        private readonly AppConfig _config;
        private readonly IBackendAuthService _authService;
        private IWavePlayer? _waveOut;
        private WaveStream? _audioReader;
        private Stream? _audioStream; // buffered audio (or direct stream if seekable)
        private IDisposable? _streamingBuffer;
        private readonly object _lock = new();
        private CancellationTokenSource? _playbackCts;
        private readonly SemaphoreSlim _speakGate = new(1, 1);
        private readonly SemaphoreSlim _engineCatalogGate = new(1, 1);
        private TtsEngineCatalog? _engineCatalogCache;
        private DateTimeOffset _engineCatalogCacheExpiresUtc;
        private long _playbackVersion;
        private const int QwenPcmSampleRate = 24000;
        private const int QwenPcmBitsPerSample = 16;
        private const int QwenPcmChannels = 1;
        private const int MossPcmSampleRate = 48000;
        private const int MossPcmBitsPerSample = 16;
        private const int MossPcmChannels = 2;
        private const int OmniVoicePcmSampleRate = 24000;
        private const int OmniVoicePcmBitsPerSample = 16;
        private const int OmniVoicePcmChannels = 1;
        private const int QwenCloneReferenceOpusSampleRate = 24000;
        private const int QwenCloneReferenceOpusChannels = 1;
        private const int QwenCloneReferenceOpusFrameMilliseconds = 20;
        private const int QwenCloneReferenceOpusBitrate = 48000;
        private const int QwenStableChunkSize = 64;
        private static readonly TimeSpan QwenCloneReferenceMinDuration = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan QwenCloneReferenceRecommendedMinDuration = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan QwenCloneReferenceRecommendedMaxDuration = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan QwenCloneReferenceMaxDuration = TimeSpan.FromSeconds(60);
        private const int StreamingDesiredLatencyMilliseconds = 220;
        private const int StreamingNumberOfBuffers = 4;
        private const double QwenStableTemperature = 0.65d;
        private static readonly TimeSpan StreamingBufferDuration = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan StreamingStartBufferDuration = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan QwenStreamingStartBufferDuration = TimeSpan.FromMilliseconds(1000);
        private const int MossVoiceCloneMaxTextTokens = 220;
        private const double MossInterChunkPauseSeconds = 0d;
        private const double MossMaxSilenceSeconds = 0.08d;
        private const double MossSilenceThreshold = 0.003d;
        private const string MossStableSampleMode = "fixed";
        private const bool MossStableDoSample = true;
        private const int MossStableSeed = 1234;
        private const int OmniVoiceNumStep = 32;
        private const double OmniVoiceGuidanceScale = 2.0d;
        private const double OmniVoiceTShift = 0.1d;
        private const double OmniVoiceLayerPenaltyFactor = 5.0d;
        private const double OmniVoicePositionTemperature = 5.0d;
        private const double OmniVoiceClassTemperature = 0.0d;
        private static readonly TimeSpan EngineCatalogCacheDuration = TimeSpan.FromSeconds(60);
        private const string MissingVoicePresetMessage = "声音克隆未选择可用的声音配置。请到“语音合成 - 声音预设 - 预设市场”下载任意音色，或新增自定义音色。";
        private const string TtsOpusResponseFormat = "opus";
        private const string TtsPcmResponseFormat = "pcm";
        private const string LengthPrefixedOpusEncoding = "length_prefixed_opus";
        private const int DefaultOpusSampleRate = 24000;
        private const int DefaultOpusChannels = 1;
        private const int DefaultOpusFrameMilliseconds = 20;
        private const int DefaultOpusBitsPerSample = 16;
        private const int MaxOpusPacketBytes = 1275;

        public bool IsSpeaking { get; private set; }

        public event EventHandler? SpeakCompleted;
        public event EventHandler<string>? SpeakError;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        public ServerTtsService(
            AppConfig config,
            IBackendAuthService authService,
            IHttpClientFactory? httpClientFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));

            _httpClient = httpClientFactory?.CreateClient(HttpClientName) ?? CreateFallbackHttpClient();

            _logger.Debug("服务器 TTS 服务已初始化");
        }

        private static HttpClient CreateFallbackHttpClient()
        {
            return new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public Task SpeakAsync(string text, string? voiceName = null)
        {
            // 默认使用中文
            return SpeakAsync(text, "zh", voiceName, null);
        }

        public async Task SpeakAsync(string text, string language, string? voiceName = null)
        {
            await SpeakAsync(text, language, voiceName, null);
        }

        public async Task SpeakAsync(string text, string language, string? voiceName, string? outputDeviceName)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await _speakGate.WaitAsync();
            try
            {
                lock (_lock)
                {
                    if (IsSpeaking)
                    {
                        Stop();
                    }
                }

                _logger.Debug(
                    "Server TTS request - textLength: {Length}, language: {Language}, outputDevice: {OutputDevice}",
                    text.Length,
                    language,
                    ResolveOutputDeviceName(outputDeviceName));

                var provider = AppConfig.NormalizeTtsProvider(_config.TtsProvider);

                // Select voice: explicit argument > provider-specific configured voice > language default.
                var configuredVoice = ResolveConfiguredVoiceName(provider);
                var voice = !string.IsNullOrWhiteSpace(voiceName)
                    ? voiceName.Trim()
                    : !string.IsNullOrWhiteSpace(configuredVoice)
                        ? configuredVoice
                        : GetVoiceForLanguage(language, provider);
                var speed = Math.Clamp((double)_config.TtsSpeed, 0.5d, 2.0d);
                var volume = ResolveTtsVolume();
                var engineCatalog = await GetTtsEngineCatalogAsync().ConfigureAwait(false);
                var responseFormat = ResolveServerTtsResponseFormat(provider, engineCatalog);

                var useQwenVoiceClone = ShouldUseQwenVoiceClone(provider);
                var useMossVoiceClone = ShouldUseMossVoiceClone(provider);
                var useOmniVoiceVoiceClone = ShouldUseOmniVoiceVoiceClone(provider);
                var activeVoiceCloneProfile = ResolveVoiceCloneProfile(provider, language);
                if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                    && !useQwenVoiceClone)
                {
                    var qwenVoice = voice?.Trim() ?? string.Empty;
                    voice = string.IsNullOrWhiteSpace(qwenVoice)
                        ? QwenTtsSpeakerCatalog.DefaultSpeaker
                        : QwenTtsSpeakerCatalog.TryNormalizeSpeaker(qwenVoice, out var normalizedQwenVoice)
                            ? normalizedQwenVoice
                            : qwenVoice;
                }

                var cloneValidationError = useQwenVoiceClone
                    ? ValidateQwenVoiceCloneSettings(activeVoiceCloneProfile)
                    : useMossVoiceClone
                        ? ValidateMossVoiceCloneSettings(activeVoiceCloneProfile)
                        : useOmniVoiceVoiceClone
                            ? ValidateOmniVoiceVoiceCloneSettings(activeVoiceCloneProfile)
                            : null;
                if (!string.IsNullOrWhiteSpace(cloneValidationError))
                {
                    ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                    {
                        Code = ServiceErrorCode.TtsFailed,
                        Context = new Dictionary<string, string>
                        {
                            ["Source"] = "语音合成",
                            ["Stage"] = "tts_clone",
                            ["Reason"] = NormalizeErrorReason(cloneValidationError)
                        }
                    });
                    _logger.Warning("TTS voice clone settings are invalid: {Reason}", cloneValidationError);
                    SpeakError?.Invoke(this, cloneValidationError);
                    return;
                }

                var requestAborted = false;

                async Task<HttpResponseMessage?> SendCurrentTtsRequestAsync(string requestResponseFormat)
                {
                    if (useQwenVoiceClone)
                    {
                        if (!EnsureTtsEngineAvailable(engineCatalog, provider, clone: true))
                        {
                            requestAborted = true;
                            return null;
                        }

                        var cloneUrl = BuildQwenCloneUrl(engineCatalog);
                        _logger.Debug(
                            "Server Qwen TTS clone request - URL: {Url}, textLength: {Length}, language: {Language}, refAudio: {RefAudio}, profile: {Profile}, responseFormat: {ResponseFormat}",
                            cloneUrl,
                            text.Length,
                            ResolveQwenCloneLanguage(activeVoiceCloneProfile),
                            ResolveQwenCloneReferenceAudioPath(activeVoiceCloneProfile),
                            ResolveVoiceCloneProfileName(activeVoiceCloneProfile),
                            requestResponseFormat);
                        return await SendTtsRequestWithAuthRetryAsync(
                            "Server Qwen TTS voice clone",
                            () => CreateQwenCloneRequest(cloneUrl, text, activeVoiceCloneProfile, requestResponseFormat)).ConfigureAwait(false);
                    }

                    if (useMossVoiceClone)
                    {
                        if (!EnsureTtsEngineAvailable(engineCatalog, provider, clone: true))
                        {
                            requestAborted = true;
                            return null;
                        }

                        var cloneUrl = BuildMossCloneUrl(engineCatalog);
                        _logger.Debug(
                            "Server MOSS TTS clone request - URL: {Url}, textLength: {Length}, language: {Language}, promptAudio: {PromptAudio}, profile: {Profile}, responseFormat: {ResponseFormat}",
                            cloneUrl,
                            text.Length,
                            ResolveMossCloneLanguage(language, activeVoiceCloneProfile),
                            ResolveMossCloneReferenceAudioPath(activeVoiceCloneProfile),
                            ResolveVoiceCloneProfileName(activeVoiceCloneProfile),
                            requestResponseFormat);
                        return await SendTtsRequestWithAuthRetryAsync(
                            "Server MOSS TTS voice clone",
                            () => CreateMossCloneRequest(cloneUrl, text, language, activeVoiceCloneProfile, requestResponseFormat)).ConfigureAwait(false);
                    }

                    if (useOmniVoiceVoiceClone)
                    {
                        if (!EnsureTtsEngineAvailable(engineCatalog, provider, clone: true))
                        {
                            requestAborted = true;
                            return null;
                        }

                        var cloneUrl = BuildOmniVoiceCloneUrl(engineCatalog);
                        _logger.Debug(
                            "Server OmniVoice TTS clone request - URL: {Url}, textLength: {Length}, language: {Language}, refAudio: {RefAudio}, profile: {Profile}, responseFormat: {ResponseFormat}",
                            cloneUrl,
                            text.Length,
                            ResolveOmniVoiceCloneLanguage(language, activeVoiceCloneProfile),
                            ResolveOmniVoiceCloneReferenceAudioPath(activeVoiceCloneProfile),
                            ResolveVoiceCloneProfileName(activeVoiceCloneProfile),
                            requestResponseFormat);
                        return await SendTtsRequestWithAuthRetryAsync(
                            "Server OmniVoice TTS voice clone",
                            () => CreateOmniVoiceCloneRequest(cloneUrl, text, language, speed, activeVoiceCloneProfile, requestResponseFormat)).ConfigureAwait(false);
                    }

                    if (!EnsureTtsEngineAvailable(engineCatalog, provider, clone: false))
                    {
                        requestAborted = true;
                        return null;
                    }

                    var request = CreateTtsPayload(text, voice, speed, volume, provider, language, requestResponseFormat);
                    var json = JsonSerializer.Serialize(request);

                    // Build TTS API URL via BackendUrlHelper for consistency.
                    var ttsUrl = BuildTtsUrl(provider, engineCatalog);
                    _logger.Debug("Server TTS request - URL: {Url}, provider: {Provider}, body: {Body}, language: {Language}, voice: {Voice}, responseFormat: {ResponseFormat}",
                        ttsUrl, provider, json, language, voice, requestResponseFormat);

                    return await SendTtsRequestWithAuthRetryAsync(ttsUrl, json).ConfigureAwait(false);
                }

                var response = await SendCurrentTtsRequestAsync(responseFormat).ConfigureAwait(false);
                if (requestAborted)
                {
                    return;
                }

                using (response)
                {
                    if (response == null)
                    {
                        _logger.Warning("Server TTS aborted because token is unavailable.");
                        ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                        {
                            Code = ServiceErrorCode.TtsAuthRequired,
                            Context = new Dictionary<string, string>
                            {
                                ["Source"] = "语音合成",
                                ["Stage"] = "tts",
                                ["Reason"] = "未获取到后端授权"
                            }
                        });
                        SpeakError?.Invoke(this, "使用云端语音合成前请先登录账户。");
                        return;
                    }

                    if ((int)response.StatusCode == 430)
                    {
                        var tooManyContent = await response.Content.ReadAsStringAsync();
                        _authService.NotifyTooManyRequests(source: "语音合成", backendDetail: tooManyContent, stage: "tts");
                        ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                        {
                            Code = ServiceErrorCode.TtsRateLimit,
                            Context = new Dictionary<string, string>
                            {
                                ["Source"] = "语音合成",
                                ["Stage"] = "tts",
                                ["Reason"] = "请求过于频繁"
                            }
                        });
                        _logger.Warning("TTS request is too frequent. response: {Response}", tooManyContent);
                        SpeakError?.Invoke(this, "语音合成请求过于频繁，请稍后再试。");
                        return;
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                        {
                            Code = ServiceErrorCode.TtsAuthExpired,
                            Context = new Dictionary<string, string>
                            {
                                ["Source"] = "语音合成",
                                ["Stage"] = "tts",
                                ["Reason"] = "登录状态已过期"
                            }
                        });
                        _logger.Warning("Server TTS still returned 401 after centralized auth retry.");
                        SpeakError?.Invoke(this, "登录状态已过期，请重新登录后再试。");
                        return;
                    }

                    if (IsClientUpgradeResponse(response))
                    {
                        var upgradeContent = await response.Content.ReadAsStringAsync();
                        var upgradeMessage = ExtractClientUpgradeMessage(upgradeContent);
                        ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                        {
                            Code = ServiceErrorCode.TtsClientUpgradeRequired,
                            Context = new Dictionary<string, string>
                            {
                                ["Source"] = "语音合成",
                                ["Stage"] = "tts",
                                ["StatusCode"] = ((int)response.StatusCode).ToString(),
                                ["Reason"] = upgradeMessage,
                                ["DisplayHint"] = GetFirstHeaderValue(response, "X-Client-Upgrade-Display") ?? "main_top_banner",
                                ["UpgradeCode"] = GetFirstHeaderValue(response, "X-Client-Upgrade-Code") ?? "tts_client_upgrade_required"
                            }
                        });
                        _logger.Warning("TTS client upgrade is required. response: {Response}", upgradeContent);
                        SpeakError?.Invoke(this, upgradeMessage);
                        return;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        var briefReason = ExtractBriefHttpError(response.StatusCode, errorContent);
                        ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                        {
                            Code = ServiceErrorCode.TtsHttpError,
                            Context = new Dictionary<string, string>
                            {
                                ["Source"] = "语音合成",
                                ["Stage"] = "tts",
                                ["StatusCode"] = ((int)response.StatusCode).ToString(),
                                ["Reason"] = briefReason
                            }
                        });
                        _logger.Error("TTS request failed - statusCode: {StatusCode}, response: {Response}",
                            (int)response.StatusCode, errorContent);
                        SpeakError?.Invoke(this, $"语音合成失败（HTTP {(int)response.StatusCode}）：{briefReason}");
                        return;
                    }

                    if (IsPackagedOpusResponse(response))
                    {
                        await PlayPackagedOpusStreamingAudioAsync(response, $"{GetTtsProviderDisplayName(provider)} Opus", outputDeviceName);
                    }
                    else if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                        && !BackendUrlHelper.UsesLegacyBackendApi(_config))
                    {
                        await PlayQwenStreamingAudioAsync(response, outputDeviceName);
                    }
                    else if (string.Equals(provider, AppConfig.TtsProviderMoss, StringComparison.OrdinalIgnoreCase)
                        && !BackendUrlHelper.UsesLegacyBackendApi(_config))
                    {
                        await PlayMossStreamingAudioAsync(response, outputDeviceName);
                    }
                    else if (string.Equals(provider, AppConfig.TtsProviderOmniVoice, StringComparison.OrdinalIgnoreCase)
                        && !BackendUrlHelper.UsesLegacyBackendApi(_config))
                    {
                        await PlayOmniVoiceStreamingAudioAsync(response, outputDeviceName);
                    }
                    else
                    {
                        await PlayAudioStreamAsync(response, outputDeviceName);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                _logger.Debug("TTS playback canceled.");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                {
                    Code = ServiceErrorCode.TtsFailed,
                    Context = new Dictionary<string, string>
                    {
                        ["Source"] = "语音合成",
                        ["Stage"] = "tts",
                        ["Reason"] = NormalizeErrorReason(ex.Message)
                    },
                    Exception = ex
                });
                _logger.Error(ex, "Server TTS failed.");
                SpeakError?.Invoke(this, $"语音合成失败：{ex.Message}");
            }
            finally
            {
                IsSpeaking = false;
                SpeakCompleted?.Invoke(this, EventArgs.Empty);
                _speakGate.Release();
            }
        }

        private async Task<HttpResponseMessage?> SendTtsRequestWithAuthRetryAsync(string ttsUrl, string json)
        {
            return await SendTtsRequestWithAuthRetryAsync(
                "Server TTS",
                () => CreateTtsRequest(ttsUrl, json)).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage?> SendTtsRequestWithAuthRetryAsync(
            string operation,
            Func<HttpRequestMessage> createRequest)
        {
            return await _authService.SendAuthorizedAsync(
                operation,
                createRequest,
                (request, cancellationToken) => _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)).ConfigureAwait(false);
        }

        private async Task<TtsEngineCatalog?> GetTtsEngineCatalogAsync()
        {
            if (BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (_engineCatalogCache != null && now < _engineCatalogCacheExpiresUtc)
            {
                return _engineCatalogCache;
            }

            await _engineCatalogGate.WaitAsync().ConfigureAwait(false);
            try
            {
                now = DateTimeOffset.UtcNow;
                if (_engineCatalogCache != null && now < _engineCatalogCacheExpiresUtc)
                {
                    return _engineCatalogCache;
                }

                var catalog = await TtsEngineCatalogClient.GetEnginesAsync(
                    _config,
                    _authService,
                    _httpClient).ConfigureAwait(false);
                if (catalog?.Engines.Count > 0)
                {
                    _engineCatalogCache = catalog;
                    _engineCatalogCacheExpiresUtc = now.Add(EngineCatalogCacheDuration);
                }

                return catalog;
            }
            finally
            {
                _engineCatalogGate.Release();
            }
        }

        private bool EnsureTtsEngineAvailable(TtsEngineCatalog? engineCatalog, string provider, bool clone)
        {
            if (BackendUrlHelper.UsesLegacyBackendApi(_config) || engineCatalog == null)
            {
                return true;
            }

            var engine = engineCatalog.GetEngine(provider);
            var available = clone
                ? engine?.IsCloneEnabled == true
                : engine?.IsSpeechEnabled == true;
            if (available)
            {
                return true;
            }

            var stage = clone ? "tts_clone" : "tts";
            var message = $"服务端未启用 {GetTtsProviderDisplayName(provider)}{(clone ? " 声音克隆" : " 语音合成")}。";
            ErrorOccurred?.Invoke(this, new ServiceErrorEvent
            {
                Code = ServiceErrorCode.TtsFailed,
                Context = new Dictionary<string, string>
                {
                    ["Source"] = "语音合成",
                    ["Stage"] = stage,
                    ["Provider"] = provider,
                    ["Reason"] = message
                }
            });
            _logger.Warning("TTS engine is unavailable. provider={Provider}, clone={Clone}", provider, clone);
            SpeakError?.Invoke(this, message);
            return false;
        }

        private string ResolveServerTtsResponseFormat(string provider, TtsEngineCatalog? engineCatalog)
        {
            if (ShouldRequestOpus(provider, engineCatalog))
            {
                return TtsOpusResponseFormat;
            }

            return TtsPcmResponseFormat;
        }

        private bool ShouldRequestOpus(string provider, TtsEngineCatalog? engineCatalog)
        {
            return !BackendUrlHelper.UsesLegacyBackendApi(_config);
        }

        private static string GetTtsProviderDisplayName(string provider)
        {
            return AppConfig.NormalizeTtsProvider(provider) switch
            {
                AppConfig.TtsProviderQwen => "Qwen-TTS",
                AppConfig.TtsProviderMoss => "MOSS-TTS",
                AppConfig.TtsProviderOmniVoice => "OmniVoice-TTS",
                AppConfig.TtsProviderEdge => "Edge-TTS",
                _ => "TTS"
            };
        }

        private static bool IsClientUpgradeResponse(HttpResponseMessage response)
        {
            return response.StatusCode == HttpStatusCode.UpgradeRequired
                || string.Equals(GetFirstHeaderValue(response, "X-Client-Upgrade-Required"), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractClientUpgradeMessage(string? content)
        {
            return TryExtractJsonErrorReason(content)
                ?? "请升级新版客户端以支持 Opus 流式语音合成。";
        }

        private static string? GetFirstHeaderValue(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }

            if (response.Content.Headers.TryGetValues(name, out values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }

            return null;
        }

        private static string ExtractBriefHttpError(HttpStatusCode statusCode, string? content)
        {
            var reason = TryExtractJsonErrorReason(content)
                ?? NormalizeErrorReason(content);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return reason;
            }

            return statusCode switch
            {
                HttpStatusCode.BadRequest => "请求参数不正确",
                HttpStatusCode.Forbidden => "账号没有权限使用当前语音合成服务",
                HttpStatusCode.NotFound => "后端没有找到语音合成接口",
                HttpStatusCode.RequestTimeout => "请求超时",
                HttpStatusCode.RequestEntityTooLarge => "请求内容过大",
                HttpStatusCode.TooManyRequests => "请求过于频繁",
                HttpStatusCode.InternalServerError => "后端语音合成服务内部错误",
                HttpStatusCode.BadGateway => "后端无法连接语音合成引擎",
                HttpStatusCode.ServiceUnavailable => "后端语音合成服务不可用",
                HttpStatusCode.GatewayTimeout => "后端语音合成服务超时",
                _ => statusCode.ToString()
            };
        }

        private static string? TryExtractJsonErrorReason(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                return TryExtractJsonErrorReason(document.RootElement, out var reason)
                    ? NormalizeErrorReason(reason)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool TryExtractJsonErrorReason(JsonElement element, out string reason)
        {
            reason = string.Empty;
            if (element.ValueKind == JsonValueKind.String)
            {
                reason = element.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(reason);
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryExtractJsonErrorReason(item, out reason))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var propertyName in new[] { "detail", "message", "error", "reason", "msg" })
            {
                if (element.TryGetProperty(propertyName, out var property)
                    && TryExtractJsonErrorReason(property, out reason))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeErrorReason(string? reason)
        {
            var normalized = string.Join(
                " ",
                (reason ?? string.Empty)
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Replace("\t", " ", StringComparison.Ordinal)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length > 180)
            {
                normalized = normalized[..180].TrimEnd() + "...";
            }

            return normalized;
        }

        private double ResolveTtsVolume()
        {
            return Math.Clamp((double)_config.TtsVolume, 0d, 4d);
        }

        private HttpRequestMessage CreateTtsRequest(string ttsUrl, string json)
        {
            return new HttpRequestMessage(HttpMethod.Post, ttsUrl)
            {
                Content = new StringContent(json)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            };
        }

        private HttpRequestMessage CreateQwenCloneRequest(
            string cloneUrl,
            string text,
            TtsVoiceCloneProfileConfig? profile,
            string responseFormat)
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(text), "input");
            content.Add(new StringContent(ResolveQwenCloneReferenceText(profile)), "ref_text");
            content.Add(new StringContent(ResolveQwenCloneLanguage(profile)), "language");
            content.Add(new StringContent(responseFormat), "response_format");
            content.Add(new StringContent(QwenStableChunkSize.ToString(CultureInfo.InvariantCulture)), "chunk_size");
            content.Add(new StringContent("false"), "append_silence");
            content.Add(new StringContent(ResolveTtsVolume().ToString(CultureInfo.InvariantCulture)), "volume");

            var instruct = ResolveQwenTtsInstruct();
            if (!string.IsNullOrWhiteSpace(instruct))
            {
                content.Add(new StringContent(instruct), "instruct");
            }

            var refAudioPath = ResolveQwenCloneReferenceAudioPath(profile);
            var opusStream = CreateQwenCloneReferenceOpusStream(refAudioPath, out var refDuration, out var originalBytes);
            var fileContent = new StreamContent(opusStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/opus");
            content.Add(fileContent, "ref_audio", $"{Path.GetFileNameWithoutExtension(refAudioPath)}.opus");
            content.Add(new StringContent("length_prefixed_opus"), "ref_audio_encoding");
            content.Add(
                new StringContent(QwenCloneReferenceOpusSampleRate.ToString(CultureInfo.InvariantCulture)),
                "ref_audio_sample_rate");
            content.Add(
                new StringContent(QwenCloneReferenceOpusFrameMilliseconds.ToString(CultureInfo.InvariantCulture)),
                "ref_audio_frame_ms");
            _logger.Information(
                "Qwen TTS clone reference audio compressed - duration: {DurationSeconds:F2}s, originalBytes: {OriginalBytes}, opusBytes: {OpusBytes}, sampleRate: {SampleRate}, channels: {Channels}",
                refDuration.TotalSeconds,
                originalBytes,
                opusStream.Length,
                QwenCloneReferenceOpusSampleRate,
                QwenCloneReferenceOpusChannels);

            return new HttpRequestMessage(HttpMethod.Post, cloneUrl)
            {
                Content = content
            };
        }

        private HttpRequestMessage CreateMossCloneRequest(
            string cloneUrl,
            string text,
            string language,
            TtsVoiceCloneProfileConfig? profile,
            string responseFormat)
        {
            var refAudioPath = ResolveMossCloneReferenceAudioPath(profile);
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(text), "input");
            content.Add(new StringContent(ResolveMossCloneLanguage(language, profile)), "language");
            content.Add(new StringContent(responseFormat), "response_format");
            content.Add(new StringContent("true"), "stream");
            content.Add(new StringContent(MossStableSampleMode), "sample_mode");
            content.Add(new StringContent(MossStableDoSample ? "true" : "false"), "do_sample");
            content.Add(new StringContent(MossStableSeed.ToString(CultureInfo.InvariantCulture)), "seed");
            content.Add(
                new StringContent(MossVoiceCloneMaxTextTokens.ToString(CultureInfo.InvariantCulture)),
                "voice_clone_max_text_tokens");
            content.Add(
                new StringContent(MossInterChunkPauseSeconds.ToString(CultureInfo.InvariantCulture)),
                "voice_clone_inter_chunk_pause_seconds");
            content.Add(new StringContent("true"), "trim_long_silence");
            content.Add(
                new StringContent(MossMaxSilenceSeconds.ToString(CultureInfo.InvariantCulture)),
                "max_silence_seconds");
            content.Add(
                new StringContent(MossSilenceThreshold.ToString(CultureInfo.InvariantCulture)),
                "silence_threshold");
            content.Add(new StringContent(ResolveTtsVolume().ToString(CultureInfo.InvariantCulture)), "volume");

            var fileStream = File.OpenRead(refAudioPath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveAudioContentType(refAudioPath));
            content.Add(fileContent, "prompt_audio", Path.GetFileName(refAudioPath));

            return new HttpRequestMessage(HttpMethod.Post, cloneUrl)
            {
                Content = content
            };
        }

        private HttpRequestMessage CreateOmniVoiceCloneRequest(
            string cloneUrl,
            string text,
            string language,
            double speed,
            TtsVoiceCloneProfileConfig? profile,
            string responseFormat)
        {
            var refAudioPath = ResolveOmniVoiceCloneReferenceAudioPath(profile);
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(text), "input");
            content.Add(new StringContent(ResolveOmniVoiceCloneLanguage(language, profile)), "language");
            content.Add(new StringContent(responseFormat), "response_format");
            content.Add(new StringContent("true"), "stream");
            content.Add(new StringContent(speed.ToString(CultureInfo.InvariantCulture)), "speed");
            content.Add(new StringContent(OmniVoiceNumStep.ToString(CultureInfo.InvariantCulture)), "num_step");
            content.Add(new StringContent(OmniVoiceGuidanceScale.ToString(CultureInfo.InvariantCulture)), "guidance_scale");
            content.Add(new StringContent(OmniVoiceTShift.ToString(CultureInfo.InvariantCulture)), "t_shift");
            content.Add(new StringContent("true"), "denoise");
            content.Add(new StringContent("true"), "preprocess_prompt");
            content.Add(new StringContent("true"), "postprocess_output");
            content.Add(new StringContent(OmniVoiceLayerPenaltyFactor.ToString(CultureInfo.InvariantCulture)), "layer_penalty_factor");
            content.Add(new StringContent(OmniVoicePositionTemperature.ToString(CultureInfo.InvariantCulture)), "position_temperature");
            content.Add(new StringContent(OmniVoiceClassTemperature.ToString(CultureInfo.InvariantCulture)), "class_temperature");
            content.Add(new StringContent(ResolveTtsVolume().ToString(CultureInfo.InvariantCulture)), "volume");

            var refText = ResolveOmniVoiceCloneReferenceText(profile);
            if (!string.IsNullOrWhiteSpace(refText))
            {
                content.Add(new StringContent(refText), "ref_text");
            }

            var fileStream = File.OpenRead(refAudioPath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveAudioContentType(refAudioPath));
            content.Add(fileContent, "ref_audio", Path.GetFileName(refAudioPath));

            return new HttpRequestMessage(HttpMethod.Post, cloneUrl)
            {
                Content = content
            };
        }

        private string BuildTtsUrl(string provider, TtsEngineCatalog? engineCatalog)
        {
            if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return BuildEnginePathUrl(engineCatalog, provider, engine => engine.SpeechPath, "tts/qwen");
            }

            if (string.Equals(provider, AppConfig.TtsProviderMoss, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return BuildEnginePathUrl(engineCatalog, provider, engine => engine.SpeechPath, "tts/moss");
            }

            if (string.Equals(provider, AppConfig.TtsProviderOmniVoice, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return BuildEnginePathUrl(engineCatalog, provider, engine => engine.SpeechPath, "tts/omnivoice");
            }

            if (string.Equals(provider, AppConfig.TtsProviderEdge, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return BuildEnginePathUrl(engineCatalog, provider, engine => engine.SpeechPath, "tts/edge");
            }

            return $"{BackendUrlHelper.BuildApiBaseUrl(_config)}func/tts";
        }

        private string BuildQwenCloneUrl(TtsEngineCatalog? engineCatalog)
        {
            return BuildEnginePathUrl(engineCatalog, AppConfig.TtsProviderQwen, engine => engine.ClonePath, "clone/qwen");
        }

        private string BuildMossCloneUrl(TtsEngineCatalog? engineCatalog)
        {
            return BuildEnginePathUrl(engineCatalog, AppConfig.TtsProviderMoss, engine => engine.ClonePath, "clone/moss");
        }

        private string BuildOmniVoiceCloneUrl(TtsEngineCatalog? engineCatalog)
        {
            return BuildEnginePathUrl(engineCatalog, AppConfig.TtsProviderOmniVoice, engine => engine.ClonePath, "clone/omnivoice");
        }

        private string BuildEnginePathUrl(
            TtsEngineCatalog? engineCatalog,
            string provider,
            Func<TtsEngineInfo, string?> pathSelector,
            string fallbackApiRelativePath)
        {
            if (TtsEngineCatalogClient.TryBuildEnginePathUrl(_config, engineCatalog, provider, pathSelector, out var url))
            {
                return url;
            }

            return TtsEngineCatalogClient.BuildBackendPathUrl(_config, null, fallbackApiRelativePath);
        }

        private TtsRequest CreateTtsPayload(
            string text,
            string voice,
            double speed,
            double volume,
            string provider,
            string language,
            string responseFormat)
        {
            var request = new TtsRequest
            {
                Input = text,
                Voice = voice,
                Speed = speed,
                Volume = volume
            };

            if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase))
            {
                request.ResponseFormat = responseFormat;
                request.ChunkSize = QwenStableChunkSize;
                var instruct = ResolveQwenTtsInstruct();
                if (!string.IsNullOrWhiteSpace(instruct))
                {
                    request.Instruct = instruct;
                    request.Temperature = QwenStableTemperature;
                }
            }
            else if (string.Equals(provider, AppConfig.TtsProviderMoss, StringComparison.OrdinalIgnoreCase))
            {
                request.Language = GetMossLanguageForLanguage(language);
                request.ResponseFormat = responseFormat;
                request.Stream = true;
                request.SampleMode = MossStableSampleMode;
                request.DoSample = MossStableDoSample;
                request.Seed = MossStableSeed;
                request.VoiceCloneMaxTextTokens = MossVoiceCloneMaxTextTokens;
                request.VoiceCloneInterChunkPauseSeconds = MossInterChunkPauseSeconds;
                request.TrimLongSilence = true;
                request.MaxSilenceSeconds = MossMaxSilenceSeconds;
                request.SilenceThreshold = MossSilenceThreshold;
            }
            else if (string.Equals(provider, AppConfig.TtsProviderOmniVoice, StringComparison.OrdinalIgnoreCase))
            {
                request.Language = GetOmniVoiceLanguageForLanguage(language);
                request.ResponseFormat = responseFormat;
                request.Stream = true;
                request.NumStep = OmniVoiceNumStep;
                request.GuidanceScale = OmniVoiceGuidanceScale;
                request.TShift = OmniVoiceTShift;
                request.Denoise = true;
                request.PreprocessPrompt = true;
                request.PostprocessOutput = true;
                request.LayerPenaltyFactor = OmniVoiceLayerPenaltyFactor;
                request.PositionTemperature = OmniVoicePositionTemperature;
                request.ClassTemperature = OmniVoiceClassTemperature;
            }
            else if (string.Equals(responseFormat, TtsOpusResponseFormat, StringComparison.OrdinalIgnoreCase))
            {
                request.ResponseFormat = responseFormat;
            }

            return request;
        }

        private bool ShouldUseQwenVoiceClone(string provider)
        {
            return string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config)
                && _config.QwenTtsVoiceModeValue == QwenTtsVoiceMode.Clone;
        }

        private bool ShouldUseMossVoiceClone(string provider)
        {
            return string.Equals(provider, AppConfig.TtsProviderMoss, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config)
                && _config.MossTtsVoiceModeValue == MossTtsVoiceMode.Clone;
        }

        private bool ShouldUseOmniVoiceVoiceClone(string provider)
        {
            return string.Equals(provider, AppConfig.TtsProviderOmniVoice, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config)
                && _config.OmniVoiceTtsVoiceModeValue == OmniVoiceTtsVoiceMode.Clone;
        }

        private TtsVoiceCloneProfileConfig? ResolveVoiceCloneProfile(string provider, string language)
        {
            var profiles = ResolveActiveVoiceCloneProfiles();
            if (profiles == null || profiles.Count == 0)
            {
                return null;
            }

            TtsVoiceCloneProfileConfig? bestProfile = null;
            var bestScore = -1;

            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (profile == null || !profile.Enabled)
                {
                    continue;
                }

                var providerScore = GetVoiceCloneProfileProviderMatchScore(profile.Provider, provider);
                if (providerScore < 0)
                {
                    continue;
                }

                var languageScore = GetVoiceCloneProfileLanguageMatchScore(profile, language);
                if (languageScore < 0)
                {
                    continue;
                }

                var score = languageScore * 10 + providerScore;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestProfile = profile;
                }
            }

            return bestProfile;
        }

        private IList<TtsVoiceCloneProfileConfig>? ResolveActiveVoiceCloneProfiles()
        {
            var presetId = _config.TtsVoiceClonePresetId?.Trim();
            if (!string.IsNullOrWhiteSpace(presetId)
                && _config.TtsVoiceClonePresets != null
                && _config.TtsVoiceClonePresets.Count > 0)
            {
                for (var i = 0; i < _config.TtsVoiceClonePresets.Count; i++)
                {
                    var preset = _config.TtsVoiceClonePresets[i];
                    if (preset == null
                        || !preset.Enabled
                        || !string.Equals(preset.Id?.Trim(), presetId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return preset.Profiles;
                }
            }

            return null;
        }

        private static int GetVoiceCloneProfileProviderMatchScore(string? configuredProvider, string activeProvider)
        {
            var configured = (configuredProvider ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(configured)
                || string.Equals(configured, "*", StringComparison.Ordinal)
                || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured, "default", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var normalized = configured.ToLowerInvariant() switch
            {
                AppConfig.TtsProviderQwen => AppConfig.TtsProviderQwen,
                AppConfig.TtsProviderMoss or "moss-tts" or "smoss" or "smoss-tts" => AppConfig.TtsProviderMoss,
                AppConfig.TtsProviderOmniVoice or "omni" or "omni-voice" or "omni_voice" => AppConfig.TtsProviderOmniVoice,
                _ => string.Empty
            };

            return string.Equals(normalized, activeProvider, StringComparison.OrdinalIgnoreCase)
                ? 100
                : -1;
        }

        private static int GetVoiceCloneProfileLanguageMatchScore(TtsVoiceCloneProfileConfig profile, string language)
        {
            var targetLanguages = profile.TargetLanguages;
            if (targetLanguages == null || targetLanguages.Count == 0)
            {
                return 0;
            }

            var normalizedLanguage = NormalizeVoiceCloneLanguageKey(language);
            var bestScore = -1;
            for (var i = 0; i < targetLanguages.Count; i++)
            {
                var configured = (targetLanguages[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(configured))
                {
                    continue;
                }

                if (string.Equals(configured, "*", StringComparison.Ordinal)
                    || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(configured, "all", StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 0);
                    continue;
                }

                var normalizedConfigured = NormalizeVoiceCloneLanguageKey(configured);
                if (string.Equals(normalizedLanguage, normalizedConfigured, StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 1000 + normalizedConfigured.Length);
                }
                else if (normalizedLanguage.StartsWith(normalizedConfigured + "-", StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 800 + normalizedConfigured.Length);
                }
                else if (normalizedConfigured.StartsWith(normalizedLanguage + "-", StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 700 + normalizedConfigured.Length);
                }
            }

            return bestScore;
        }

        private static string NormalizeVoiceCloneLanguageKey(string? language)
        {
            var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
            return normalized switch
            {
                "jp" => "ja",
                "kr" => "ko",
                "cn" => "zh",
                _ => normalized
            };
        }

        private static string ResolveVoiceCloneProfileName(TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null)
            {
                return "default";
            }

            if (!string.IsNullOrWhiteSpace(profile.Name))
            {
                return profile.Name.Trim();
            }

            var languages = profile.TargetLanguages == null || profile.TargetLanguages.Count == 0
                ? "no-language"
                : string.Join(",", profile.TargetLanguages);
            return $"profile[{languages}]";
        }

        private string ResolveQwenCloneReferenceAudioPath(TtsVoiceCloneProfileConfig? profile)
        {
            return profile == null
                ? ResolveReferenceAudioPath(_config.QwenTtsCloneReferenceAudioPath)
                : ResolveReferenceAudioPath(profile.ReferenceAudioPath);
        }

        private string ResolveQwenCloneReferenceText(TtsVoiceCloneProfileConfig? profile)
        {
            return profile == null
                ? _config.QwenTtsCloneReferenceText?.Trim() ?? string.Empty
                : profile.ReferenceText?.Trim() ?? string.Empty;
        }

        private string ResolveMossCloneReferenceAudioPath(TtsVoiceCloneProfileConfig? profile)
        {
            return profile == null
                ? ResolveReferenceAudioPath(_config.MossTtsCloneReferenceAudioPath)
                : ResolveReferenceAudioPath(profile.ReferenceAudioPath);
        }

        private string ResolveOmniVoiceCloneReferenceAudioPath(TtsVoiceCloneProfileConfig? profile)
        {
            return profile == null
                ? ResolveReferenceAudioPath(_config.OmniVoiceTtsCloneReferenceAudioPath)
                : ResolveReferenceAudioPath(profile.ReferenceAudioPath);
        }

        private string ResolveOmniVoiceCloneReferenceText(TtsVoiceCloneProfileConfig? profile)
        {
            return profile == null
                ? _config.OmniVoiceTtsCloneReferenceText?.Trim() ?? string.Empty
                : profile.ReferenceText?.Trim() ?? string.Empty;
        }

        private static string ResolveReferenceAudioPath(string? path)
        {
            var trimmed = path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed) || Path.IsPathRooted(trimmed))
            {
                return trimmed;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
        }

        private string? ValidateQwenVoiceCloneSettings(TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null)
            {
                return MissingVoicePresetMessage;
            }

            var profileName = ResolveVoiceCloneProfileName(profile);
            var refAudioPath = ResolveQwenCloneReferenceAudioPath(profile);
            if (string.IsNullOrWhiteSpace(refAudioPath))
            {
                return $"Qwen-TTS voice clone profile '{profileName}' requires referenceAudioPath.";
            }

            if (!File.Exists(refAudioPath))
            {
                return $"Qwen-TTS voice clone profile '{profileName}' reference audio does not exist: {refAudioPath}";
            }

            if (string.IsNullOrWhiteSpace(ResolveQwenCloneReferenceText(profile)))
            {
                return $"Qwen-TTS voice clone profile '{profileName}' requires referenceText.";
            }

            TimeSpan duration;
            try
            {
                duration = GetAudioDuration(refAudioPath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read Qwen TTS clone profile reference audio duration: {Path}", refAudioPath);
                return $"Failed to read Qwen-TTS voice clone profile '{profileName}' reference audio.";
            }

            if (duration < QwenCloneReferenceMinDuration)
            {
                return $"Qwen-TTS voice clone profile '{profileName}' reference audio is too short: {duration.TotalSeconds:F1}s. Minimum is 3s, recommended 10-20s.";
            }

            if (duration > QwenCloneReferenceMaxDuration)
            {
                return $"Qwen-TTS voice clone profile '{profileName}' reference audio is too long: {duration.TotalSeconds:F1}s. Maximum is 60s, recommended 10-20s.";
            }

            return null;
        }

        private string? ValidateMossVoiceCloneSettings(TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null)
            {
                return MissingVoicePresetMessage;
            }

            return ValidateFileBackedVoiceCloneProfile(profile, "MOSS-TTS");
        }

        private string? ValidateOmniVoiceVoiceCloneSettings(TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null)
            {
                return MissingVoicePresetMessage;
            }

            return ValidateFileBackedVoiceCloneProfile(profile, "OmniVoice-TTS");
        }

        private string? ValidateFileBackedVoiceCloneProfile(TtsVoiceCloneProfileConfig profile, string providerLabel)
        {
            var profileName = ResolveVoiceCloneProfileName(profile);
            var refAudioPath = ResolveReferenceAudioPath(profile.ReferenceAudioPath);
            if (string.IsNullOrWhiteSpace(refAudioPath))
            {
                return $"{providerLabel} voice clone profile '{profileName}' requires referenceAudioPath.";
            }

            if (!File.Exists(refAudioPath))
            {
                return $"{providerLabel} voice clone profile '{profileName}' reference audio does not exist: {refAudioPath}";
            }

            try
            {
                var duration = GetAudioDuration(refAudioPath);
                if (duration <= TimeSpan.Zero)
                {
                    return $"{providerLabel} voice clone profile '{profileName}' reference audio is empty.";
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read {Provider} clone profile reference audio duration: {Path}", providerLabel, refAudioPath);
                return $"Failed to read {providerLabel} voice clone profile '{profileName}' reference audio.";
            }

            return null;
        }

        private string? ValidateQwenVoiceCloneSettings()
        {
            var refAudioPath = _config.QwenTtsCloneReferenceAudioPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(refAudioPath))
            {
                return "Qwen-TTS 声音克隆已启用，请先选择参考音频文件。";
            }

            if (!File.Exists(refAudioPath))
            {
                return $"Qwen-TTS 声音克隆参考音频不存在：{refAudioPath}";
            }

            if (string.IsNullOrWhiteSpace(_config.QwenTtsCloneReferenceText))
            {
                return "Qwen-TTS 声音克隆已启用，请填写参考音频对应的文本。";
            }

            TimeSpan duration;
            try
            {
                duration = GetAudioDuration(refAudioPath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read Qwen TTS clone reference audio duration: {Path}", refAudioPath);
                return "无法读取 Qwen-TTS 声音克隆参考音频，请确认文件格式可被系统解码。";
            }

            if (duration < QwenCloneReferenceMinDuration)
            {
                return $"Qwen-TTS 声音克隆参考音频过短：{duration.TotalSeconds:F1} 秒。请提供至少 3 秒连续清晰语音，推荐 10-20 秒。";
            }

            if (duration > QwenCloneReferenceMaxDuration)
            {
                return $"Qwen-TTS 声音克隆参考音频过长：{duration.TotalSeconds:F1} 秒。请控制在 60 秒以内，推荐 10-20 秒。";
            }

            if (duration < QwenCloneReferenceRecommendedMinDuration || duration > QwenCloneReferenceRecommendedMaxDuration)
            {
                _logger.Information(
                    "Qwen TTS clone reference audio duration is outside recommended range - duration: {DurationSeconds:F2}s, recommended: {RecommendedMin}-{RecommendedMax}s",
                    duration.TotalSeconds,
                    QwenCloneReferenceRecommendedMinDuration.TotalSeconds,
                    QwenCloneReferenceRecommendedMaxDuration.TotalSeconds);
            }

            return null;
        }

        private string? ValidateMossVoiceCloneSettings()
        {
            var refAudioPath = _config.MossTtsCloneReferenceAudioPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(refAudioPath))
            {
                return "MOSS-TTS 声音克隆已启用，请先选择参考音频文件。";
            }

            if (!File.Exists(refAudioPath))
            {
                return $"MOSS-TTS 声音克隆参考音频不存在：{refAudioPath}";
            }

            try
            {
                var duration = GetAudioDuration(refAudioPath);
                if (duration <= TimeSpan.Zero)
                {
                    return "MOSS-TTS 声音克隆参考音频为空，请选择有效音频文件。";
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read MOSS TTS clone reference audio duration: {Path}", refAudioPath);
                return "无法读取 MOSS-TTS 声音克隆参考音频，请确认文件格式可被系统解码。";
            }

            return null;
        }

        private string? ValidateOmniVoiceVoiceCloneSettings()
        {
            var refAudioPath = _config.OmniVoiceTtsCloneReferenceAudioPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(refAudioPath))
            {
                return "OmniVoice-TTS 声音克隆已启用，请先选择参考音频文件。";
            }

            if (!File.Exists(refAudioPath))
            {
                return $"OmniVoice-TTS 参考音频不存在：{refAudioPath}";
            }

            try
            {
                var duration = GetAudioDuration(refAudioPath);
                if (duration <= TimeSpan.Zero)
                {
                    return "OmniVoice-TTS 参考音频为空，请选择有效音频文件。";
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read OmniVoice TTS clone reference audio duration: {Path}", refAudioPath);
                return "无法读取 OmniVoice-TTS 参考音频，请确认文件格式可被系统解码。";
            }

            return null;
        }

        private string ResolveQwenCloneLanguage(TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.CloneLanguage))
            {
                return ResolveQwenCloneLanguage();
            }

            var configured = profile.CloneLanguage.Trim();
            return string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
                ? AppConfig.DefaultQwenTtsCloneLanguage
                : configured;
        }

        private string ResolveQwenCloneLanguage()
        {
            return string.IsNullOrWhiteSpace(_config.QwenTtsCloneLanguage)
                ? AppConfig.DefaultQwenTtsCloneLanguage
                : _config.QwenTtsCloneLanguage.Trim();
        }

        private string ResolveMossCloneLanguage(string fallbackLanguage, TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.CloneLanguage))
            {
                return ResolveMossCloneLanguage(fallbackLanguage);
            }

            var configured = profile.CloneLanguage.Trim();
            return string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
                ? GetMossLanguageForLanguage(fallbackLanguage)
                : GetMossLanguageForLanguage(configured);
        }

        private string ResolveMossCloneLanguage(string fallbackLanguage)
        {
            var configured = string.IsNullOrWhiteSpace(_config.MossTtsCloneLanguage)
                ? AppConfig.DefaultMossTtsCloneLanguage
                : _config.MossTtsCloneLanguage.Trim();

            if (string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return GetMossLanguageForLanguage(fallbackLanguage);
            }

            return GetMossLanguageForLanguage(configured);
        }

        private string ResolveOmniVoiceCloneLanguage(string fallbackLanguage, TtsVoiceCloneProfileConfig? profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.CloneLanguage))
            {
                return ResolveOmniVoiceCloneLanguage(fallbackLanguage);
            }

            var configured = profile.CloneLanguage.Trim();
            return string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
                ? GetOmniVoiceLanguageForLanguage(fallbackLanguage)
                : GetOmniVoiceLanguageForLanguage(configured);
        }

        private string ResolveOmniVoiceCloneLanguage(string fallbackLanguage)
        {
            var configured = string.IsNullOrWhiteSpace(_config.OmniVoiceTtsCloneLanguage)
                ? AppConfig.DefaultOmniVoiceTtsCloneLanguage
                : _config.OmniVoiceTtsCloneLanguage.Trim();

            if (string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return GetOmniVoiceLanguageForLanguage(fallbackLanguage);
            }

            return GetOmniVoiceLanguageForLanguage(configured);
        }

        private string? ResolveQwenTtsInstruct()
        {
            if (_config.QwenTtsToneModeValue != QwenTtsToneMode.Custom)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(_config.QwenTtsInstruct)
                ? AppConfig.DefaultQwenTtsInstruct
                : _config.QwenTtsInstruct.Trim();
        }

        private static TimeSpan GetAudioDuration(string path)
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime;
        }

        private static MemoryStream CreateQwenCloneReferenceOpusStream(
            string path,
            out TimeSpan duration,
            out long originalBytes)
        {
            originalBytes = new FileInfo(path).Length;
            using var reader = new AudioFileReader(path);
            duration = reader.TotalTime;
            ValidateQwenCloneReferenceDuration(duration);

            var sampleProvider = CreateQwenCloneReferenceSampleProvider(reader);
            var frameSampleCount = QwenCloneReferenceOpusSampleRate * QwenCloneReferenceOpusFrameMilliseconds / 1000;
            var frameBuffer = new float[frameSampleCount];
            var opusPacket = new byte[NativeOpusConstants.MaxPacketBytes];
            Span<byte> lengthPrefix = stackalloc byte[4];
            var output = new MemoryStream();
            using var encoder = new NativeOpusEncoder(
                QwenCloneReferenceOpusSampleRate,
                QwenCloneReferenceOpusChannels,
                NativeOpusApplication.Audio,
                bitrate: QwenCloneReferenceOpusBitrate,
                useVbr: true,
                complexity: 10);

            while (true)
            {
                var samplesRead = ReadFullFrame(sampleProvider, frameBuffer, frameSampleCount);
                if (samplesRead == 0)
                {
                    break;
                }

                for (var i = 0; i < samplesRead; i++)
                {
                    frameBuffer[i] = Math.Clamp(frameBuffer[i], -1.0f, 1.0f);
                }

                if (samplesRead < frameSampleCount)
                {
                    Array.Clear(frameBuffer, samplesRead, frameSampleCount - samplesRead);
                }

                var encodedLength = encoder.Encode(frameBuffer, frameSampleCount, opusPacket);
                if (encodedLength <= 0)
                {
                    throw new InvalidDataException("Failed to encode Qwen TTS clone reference audio to Opus.");
                }

                BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, encodedLength);
                output.Write(lengthPrefix);
                output.Write(opusPacket, 0, encodedLength);

                if (samplesRead < frameSampleCount)
                {
                    break;
                }
            }

            if (output.Length == 0)
            {
                throw new InvalidDataException("Qwen TTS clone reference audio produced an empty Opus stream.");
            }

            output.Position = 0;
            return output;
        }

        private static ISampleProvider CreateQwenCloneReferenceSampleProvider(ISampleProvider source)
        {
            var provider = source;
            var channels = provider.WaveFormat.Channels;
            if (channels == 2)
            {
                provider = new StereoToMonoSampleProvider(provider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }
            else if (channels != 1)
            {
                provider = new MultiChannelToMonoSampleProvider(provider);
            }

            if (provider.WaveFormat.SampleRate != QwenCloneReferenceOpusSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, QwenCloneReferenceOpusSampleRate);
            }

            return provider;
        }

        private static void ValidateQwenCloneReferenceDuration(TimeSpan duration)
        {
            if (duration < QwenCloneReferenceMinDuration)
            {
                throw new InvalidDataException(
                    $"Qwen TTS clone reference audio is too short: {duration.TotalSeconds:F1}s. Minimum is 3s.");
            }

            if (duration > QwenCloneReferenceMaxDuration)
            {
                throw new InvalidDataException(
                    $"Qwen TTS clone reference audio is too long: {duration.TotalSeconds:F1}s. Maximum is 60s.");
            }
        }

        private static int ReadFullFrame(ISampleProvider sampleProvider, float[] buffer, int frameSampleCount)
        {
            var totalRead = 0;
            while (totalRead < frameSampleCount)
            {
                var read = sampleProvider.Read(buffer, totalRead, frameSampleCount - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static string ResolveAudioContentType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".flac" => "audio/flac",
                ".ogg" => "audio/ogg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                _ => "application/octet-stream"
            };
        }

        private async Task PlayAudioStreamAsync(HttpResponseMessage response, string? outputDeviceName)
        {
            var stream = await response.Content.ReadAsStreamAsync();

            // NAudio readers need a seekable stream for headers/tags. HttpContent streams are often non-seekable.
            Stream inputStream;
            if (stream.CanSeek)
            {
                stream.Position = 0;
                inputStream = stream;
            }
            else
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                inputStream = ms;
                try { stream.Dispose(); } catch { }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token;
            long version;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            lock (_lock)
            {
                CleanupPlaybackResourcesUnsafe();
                _playbackCts?.Cancel();
                _playbackCts?.Dispose();
                _playbackCts = new CancellationTokenSource();
                token = _playbackCts.Token;
                version = ++_playbackVersion;

                _audioStream = inputStream;
                _audioReader = CreateAudioReader(inputStream, contentType);

                var deviceIndex = GetOutputDeviceIndex(outputDeviceName);
                var waveOut = new WaveOutEvent();
                if (deviceIndex >= 0)
                {
                    waveOut.DeviceNumber = deviceIndex;
                }

                waveOut.PlaybackStopped += (_, __) => tcs.TrySetResult(true);
                waveOut.Init(_audioReader);
                _waveOut = waveOut;

                IsSpeaking = true;
                _waveOut.Play();
            }

            _logger.Debug("TTS audio playback started");

            try
            {
                CancellationTokenRegistration registration;
                try
                {
                    registration = token.Register(() => tcs.TrySetCanceled(token));
                }
                catch (ObjectDisposedException)
                {
                    throw new TaskCanceledException();
                }

                using (registration)
                {
                    await tcs.Task;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TaskCanceledException();
            }
            finally
            {
                lock (_lock)
                {
                    if (version == _playbackVersion)
                    {
                        CleanupPlaybackResourcesUnsafe();
                    }
                }
            }
        }

        private static bool IsPackagedOpusResponse(HttpResponseMessage response)
        {
            var encoding = GetFirstHeaderValue(response, "X-Audio-Encoding");
            if (string.Equals(encoding, LengthPrefixedOpusEncoding, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return contentType.Contains("opus", StringComparison.OrdinalIgnoreCase);
        }

        private async Task PlayPackagedOpusStreamingAudioAsync(HttpResponseMessage response, string source, string? outputDeviceName)
        {
            Stream? stream = null;
            IWavePlayer? waveOut = null;
            CancellationToken token;
            long version;

            lock (_lock)
            {
                CleanupPlaybackResourcesUnsafe();
                _playbackCts?.Cancel();
                _playbackCts?.Dispose();
                _playbackCts = new CancellationTokenSource();
                token = _playbackCts.Token;
                version = ++_playbackVersion;
            }

            try
            {
                stream = await response.Content.ReadAsStreamAsync(token);

                lock (_lock)
                {
                    if (version == _playbackVersion)
                    {
                        _audioStream = stream;
                    }
                }

                var waveFormat = ResolveOpusPcmWaveFormat(response);
                var frameMilliseconds = TryGetAudioHeaderInt(response, "X-Audio-Frame-Ms") ?? DefaultOpusFrameMilliseconds;
                var frameSize = Math.Max(1, waveFormat.SampleRate * frameMilliseconds / 1000);
                var bufferedProvider = new BlockingStreamingWaveProvider(waveFormat, StreamingBufferDuration);

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waveOut = new WaveOutEvent
                {
                    DesiredLatency = StreamingDesiredLatencyMilliseconds,
                    NumberOfBuffers = StreamingNumberOfBuffers
                };

                var deviceIndex = GetOutputDeviceIndex(outputDeviceName);
                if (deviceIndex >= 0 && waveOut is WaveOutEvent waveOutEvent)
                {
                    waveOutEvent.DeviceNumber = deviceIndex;
                }

                waveOut.PlaybackStopped += (_, __) => tcs.TrySetResult(true);
                waveOut.Init(bufferedProvider);

                lock (_lock)
                {
                    if (version != _playbackVersion || token.IsCancellationRequested)
                    {
                        throw new TaskCanceledException();
                    }

                    _streamingBuffer = bufferedProvider;
                    _waveOut = waveOut;
                    IsSpeaking = true;
                }

                _logger.Debug(
                    "{Source} streaming playback prepared - sampleRate: {SampleRate}, channels: {Channels}, frameMs: {FrameMilliseconds}",
                    source,
                    waveFormat.SampleRate,
                    waveFormat.Channels,
                    frameMilliseconds);

                var started = await PumpPackagedOpusStreamingAudioAsync(
                    stream,
                    bufferedProvider,
                    waveOut,
                    tcs.Task,
                    token,
                    source,
                    frameSize,
                    StreamingStartBufferDuration);
                bufferedProvider.MarkComplete();

                if (started && !tcs.Task.IsCompleted)
                {
                    using var registration = token.Register(() => tcs.TrySetCanceled(token));
                    await tcs.Task;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TaskCanceledException();
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                throw new TaskCanceledException();
            }
            finally
            {
                lock (_lock)
                {
                    if (version == _playbackVersion)
                    {
                        CleanupPlaybackResourcesUnsafe();
                    }
                }
            }
        }

        private static WaveFormat ResolveOpusPcmWaveFormat(HttpResponseMessage response)
        {
            var sampleRate = TryGetAudioHeaderInt(response, "X-Audio-Sample-Rate")
                ?? TryGetContentTypeParameterInt(response, "rate")
                ?? DefaultOpusSampleRate;
            var channels = TryGetAudioHeaderInt(response, "X-Audio-Channels")
                ?? TryGetContentTypeParameterInt(response, "channels")
                ?? DefaultOpusChannels;
            var bitsPerSample = TryGetContentTypeParameterInt(response, "bits")
                ?? DefaultOpusBitsPerSample;

            return new WaveFormat(sampleRate, bitsPerSample, channels);
        }

        private static async Task<bool> PumpPackagedOpusStreamingAudioAsync(
            Stream stream,
            BlockingStreamingWaveProvider bufferedProvider,
            IWavePlayer waveOut,
            Task playbackStoppedTask,
            CancellationToken token,
            string source,
            int frameSize,
            TimeSpan startBufferDuration)
        {
            using var decoder = new NativeOpusDecoder(bufferedProvider.WaveFormat.SampleRate, bufferedProvider.WaveFormat.Channels);
            var lengthBuffer = new byte[4];
            var packetBuffer = new byte[MaxOpusPacketBytes];
            var pcmBuffer = new short[Math.Max(frameSize * bufferedProvider.WaveFormat.Channels, 1)];
            var pcmBytes = new byte[pcmBuffer.Length * sizeof(short)];
            var started = false;
            var loggedFirstPacket = false;
            long totalPackets = 0;
            long totalOpusBytes = 0;
            long totalPcmBytes = 0;

            while (!token.IsCancellationRequested)
            {
                if (playbackStoppedTask.IsCompleted)
                {
                    _logger.Debug(
                        "{Source} Opus playback stopped while reading - packets: {Packets}, opusBytes: {OpusBytes}, pcmBytes: {PcmBytes}, started: {Started}, bufferedBytes: {BufferedBytes}",
                        source,
                        totalPackets,
                        totalOpusBytes,
                        totalPcmBytes,
                        started,
                        bufferedProvider.BufferedBytes);
                    return started;
                }

                if (!await TryReadExactOrEndAsync(stream, lengthBuffer, token))
                {
                    if (!started && bufferedProvider.BufferedBytes > 0 && !playbackStoppedTask.IsCompleted)
                    {
                        waveOut.Play();
                        started = true;
                        _logger.Debug(
                            "{Source} Opus playback started at stream end - bufferedBytes: {BufferedBytes}, bufferedMs: {BufferedMilliseconds:F0}, packets: {Packets}",
                            source,
                            bufferedProvider.BufferedBytes,
                            bufferedProvider.BufferedDuration.TotalMilliseconds,
                            totalPackets);
                    }

                    _logger.Debug(
                        "{Source} Opus response ended - packets: {Packets}, opusBytes: {OpusBytes}, pcmBytes: {PcmBytes}, started: {Started}, bufferedBytes: {BufferedBytes}",
                        source,
                        totalPackets,
                        totalOpusBytes,
                        totalPcmBytes,
                        started,
                        bufferedProvider.BufferedBytes);
                    return started;
                }

                var packetLength = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
                if (packetLength <= 0 || packetLength > MaxOpusPacketBytes)
                {
                    throw new InvalidDataException($"Invalid Opus packet length: {packetLength}.");
                }

                if (packetLength > packetBuffer.Length)
                {
                    packetBuffer = new byte[packetLength];
                }

                await ReadExactAsync(stream, packetBuffer, packetLength, token);
                var decodedSamplesPerChannel = decoder.Decode(
                    packetBuffer,
                    packetLength,
                    pcmBuffer,
                    frameSize,
                    false);
                if (decodedSamplesPerChannel <= 0)
                {
                    continue;
                }

                var decodedBytes = decodedSamplesPerChannel * bufferedProvider.WaveFormat.Channels * sizeof(short);
                Buffer.BlockCopy(pcmBuffer, 0, pcmBytes, 0, decodedBytes);

                totalPackets++;
                totalOpusBytes += packetLength + sizeof(uint);
                totalPcmBytes += decodedBytes;
                if (!loggedFirstPacket)
                {
                    loggedFirstPacket = true;
                    _logger.Debug(
                        "{Source} first Opus packet decoded - packetBytes: {PacketBytes}, pcmBytes: {PcmBytes}",
                        source,
                        packetLength,
                        decodedBytes);
                }

                await AddSamplesWithBackpressureAsync(bufferedProvider, pcmBytes, decodedBytes, playbackStoppedTask, token);

                if (!started && bufferedProvider.BufferedDuration >= startBufferDuration)
                {
                    waveOut.Play();
                    started = true;
                    _logger.Debug(
                        "{Source} Opus playback started - bufferedBytes: {BufferedBytes}, bufferedMs: {BufferedMilliseconds:F0}, packets: {Packets}",
                        source,
                        bufferedProvider.BufferedBytes,
                        bufferedProvider.BufferedDuration.TotalMilliseconds,
                        totalPackets);
                }
            }

            return started;
        }

        private static async Task<bool> TryReadExactOrEndAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
                if (read <= 0)
                {
                    if (offset == 0)
                    {
                        return false;
                    }

                    throw new EndOfStreamException("Unexpected end of TTS Opus stream.");
                }

                offset += read;
            }

            return true;
        }

        private async Task PlayQwenStreamingAudioAsync(HttpResponseMessage response, string? outputDeviceName)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (IsWaveContent(contentType))
            {
                // Compatibility path for older Qwen proxy deployments that still stream WAV.
                await PlayStreamingAudioAsync(
                    response,
                    ReadStreamingWaveFormatAsync,
                    "Qwen TTS WAV",
                    outputDeviceName,
                    QwenStreamingStartBufferDuration);
                return;
            }

            var pcmFormat = ResolveQwenPcmWaveFormat(response);
            await PlayStreamingAudioAsync(
                response,
                (_, _) => Task.FromResult(pcmFormat),
                "Qwen TTS PCM",
                outputDeviceName,
                QwenStreamingStartBufferDuration);
        }

        private async Task PlayMossStreamingAudioAsync(HttpResponseMessage response, string? outputDeviceName)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (IsWaveContent(contentType))
            {
                await PlayStreamingAudioAsync(response, ReadStreamingWaveFormatAsync, "MOSS TTS WAV", outputDeviceName);
                return;
            }

            var pcmFormat = ResolveMossPcmWaveFormat(response);
            await PlayStreamingAudioAsync(
                response,
                (_, _) => Task.FromResult(pcmFormat),
                "MOSS TTS PCM",
                outputDeviceName);
        }

        private async Task PlayOmniVoiceStreamingAudioAsync(HttpResponseMessage response, string? outputDeviceName)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (IsWaveContent(contentType))
            {
                await PlayStreamingAudioAsync(response, ReadStreamingWaveFormatAsync, "OmniVoice TTS WAV", outputDeviceName);
                return;
            }

            var pcmFormat = ResolveOmniVoicePcmWaveFormat(response);
            await PlayStreamingAudioAsync(
                response,
                (_, _) => Task.FromResult(pcmFormat),
                "OmniVoice TTS PCM",
                outputDeviceName);
        }

        private async Task PlayStreamingAudioAsync(
            HttpResponseMessage response,
            Func<Stream, CancellationToken, Task<WaveFormat>> readWaveFormatAsync,
            string source,
            string? outputDeviceName,
            TimeSpan? startBufferDuration = null)
        {
            Stream? stream = null;
            IWavePlayer? waveOut = null;
            CancellationToken token;
            long version;

            lock (_lock)
            {
                CleanupPlaybackResourcesUnsafe();
                _playbackCts?.Cancel();
                _playbackCts?.Dispose();
                _playbackCts = new CancellationTokenSource();
                token = _playbackCts.Token;
                version = ++_playbackVersion;
            }

            try
            {
                stream = await response.Content.ReadAsStreamAsync(token);

                lock (_lock)
                {
                    if (version == _playbackVersion)
                    {
                        _audioStream = stream;
                    }
                }

                var waveFormat = await readWaveFormatAsync(stream, token);
                var bufferedProvider = new BlockingStreamingWaveProvider(waveFormat, StreamingBufferDuration);

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waveOut = new WaveOutEvent
                {
                    DesiredLatency = StreamingDesiredLatencyMilliseconds,
                    NumberOfBuffers = StreamingNumberOfBuffers
                };

                var deviceIndex = GetOutputDeviceIndex(outputDeviceName);
                if (deviceIndex >= 0 && waveOut is WaveOutEvent waveOutEvent)
                {
                    waveOutEvent.DeviceNumber = deviceIndex;
                }

                waveOut.PlaybackStopped += (_, __) => tcs.TrySetResult(true);
                waveOut.Init(bufferedProvider);

                lock (_lock)
                {
                    if (version != _playbackVersion || token.IsCancellationRequested)
                    {
                        throw new TaskCanceledException();
                    }

                    _streamingBuffer = bufferedProvider;
                    _waveOut = waveOut;
                    IsSpeaking = true;
                }

                _logger.Debug("{Source} streaming playback prepared - sampleRate: {SampleRate}, channels: {Channels}, bits: {Bits}",
                    source, waveFormat.SampleRate, waveFormat.Channels, waveFormat.BitsPerSample);

                var started = await PumpStreamingAudioAsync(
                    stream,
                    bufferedProvider,
                    waveOut,
                    tcs.Task,
                    token,
                    source,
                    startBufferDuration ?? StreamingStartBufferDuration);
                bufferedProvider.MarkComplete();

                if (started && !tcs.Task.IsCompleted)
                {
                    using var registration = token.Register(() => tcs.TrySetCanceled(token));
                    await tcs.Task;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TaskCanceledException();
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                throw new TaskCanceledException();
            }
            finally
            {
                lock (_lock)
                {
                    if (version == _playbackVersion)
                    {
                        CleanupPlaybackResourcesUnsafe();
                    }
                }
            }
        }

        private static async Task<bool> PumpStreamingAudioAsync(
            Stream stream,
            BlockingStreamingWaveProvider bufferedProvider,
            IWavePlayer waveOut,
            Task playbackStoppedTask,
            CancellationToken token,
            string source,
            TimeSpan startBufferDuration)
        {
            var buffer = new byte[8192];
            var started = false;
            var loggedFirstChunk = false;
            long totalBytes = 0;

            while (!token.IsCancellationRequested)
            {
                if (playbackStoppedTask.IsCompleted)
                {
                    _logger.Debug(
                        "{Source} streaming playback stopped while reading - totalBytes: {TotalBytes}, started: {Started}, bufferedBytes: {BufferedBytes}",
                        source,
                        totalBytes,
                        started,
                        bufferedProvider.BufferedBytes);
                    return started;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                {
                    if (!started && bufferedProvider.BufferedBytes > 0 && !playbackStoppedTask.IsCompleted)
                    {
                        waveOut.Play();
                        started = true;
                        _logger.Debug(
                            "{Source} streaming playback started at stream end - bufferedBytes: {BufferedBytes}, bufferedMs: {BufferedMilliseconds:F0}, totalBytes: {TotalBytes}",
                            source,
                            bufferedProvider.BufferedBytes,
                            bufferedProvider.BufferedDuration.TotalMilliseconds,
                            totalBytes);
                    }

                    _logger.Debug(
                        "{Source} streaming response ended - totalBytes: {TotalBytes}, started: {Started}, bufferedBytes: {BufferedBytes}",
                        source,
                        totalBytes,
                        started,
                        bufferedProvider.BufferedBytes);
                    return started;
                }

                totalBytes += read;
                if (!loggedFirstChunk)
                {
                    loggedFirstChunk = true;
                    _logger.Debug("{Source} streaming first audio chunk received - bytes: {Bytes}", source, read);
                }

                await AddSamplesWithBackpressureAsync(bufferedProvider, buffer, read, playbackStoppedTask, token);

                if (!started && bufferedProvider.BufferedDuration >= startBufferDuration)
                {
                    waveOut.Play();
                    started = true;
                    _logger.Debug(
                        "{Source} streaming playback started - bufferedBytes: {BufferedBytes}, bufferedMs: {BufferedMilliseconds:F0}, totalBytes: {TotalBytes}",
                        source,
                        bufferedProvider.BufferedBytes,
                        bufferedProvider.BufferedDuration.TotalMilliseconds,
                        totalBytes);
                }
            }

            return started;
        }

        private static async Task AddSamplesWithBackpressureAsync(
            BlockingStreamingWaveProvider bufferedProvider,
            byte[] buffer,
            int count,
            Task playbackStoppedTask,
            CancellationToken token)
        {
            var offset = 0;
            while (offset < count)
            {
                token.ThrowIfCancellationRequested();
                if (playbackStoppedTask.IsCompleted)
                {
                    return;
                }

                var writable = bufferedProvider.BufferLength - bufferedProvider.BufferedBytes;
                if (writable <= 0)
                {
                    await Task.Delay(10, token);
                    continue;
                }

                var write = Math.Min(writable, count - offset);
                bufferedProvider.AddSamples(buffer, offset, write);
                offset += write;
            }
        }

        private sealed class BlockingStreamingWaveProvider : IWaveProvider, IDisposable
        {
            private readonly object _sync = new();
            private readonly byte[] _buffer;
            private int _readOffset;
            private int _writeOffset;
            private int _bufferedBytes;
            private bool _isComplete;
            private bool _isDisposed;

            public BlockingStreamingWaveProvider(WaveFormat waveFormat, TimeSpan bufferDuration)
            {
                WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
                var bufferBytes = Math.Max(
                    WaveFormat.AverageBytesPerSecond,
                    (int)Math.Ceiling(WaveFormat.AverageBytesPerSecond * bufferDuration.TotalSeconds));
                bufferBytes -= bufferBytes % Math.Max(1, WaveFormat.BlockAlign);
                _buffer = new byte[Math.Max(WaveFormat.BlockAlign, bufferBytes)];
            }

            public WaveFormat WaveFormat { get; }

            public int BufferLength => _buffer.Length;

            public int BufferedBytes
            {
                get
                {
                    lock (_sync)
                    {
                        return _bufferedBytes;
                    }
                }
            }

            public TimeSpan BufferedDuration
            {
                get
                {
                    var bytesPerSecond = Math.Max(1, WaveFormat.AverageBytesPerSecond);
                    return TimeSpan.FromSeconds(BufferedBytes / (double)bytesPerSecond);
                }
            }

            public void AddSamples(byte[] source, int offset, int count)
            {
                if (count <= 0)
                {
                    return;
                }

                lock (_sync)
                {
                    ThrowIfDisposed();

                    var writable = _buffer.Length - _bufferedBytes;
                    if (count > writable)
                    {
                        throw new InvalidOperationException("Streaming audio buffer overflow.");
                    }

                    var firstCopy = Math.Min(count, _buffer.Length - _writeOffset);
                    Buffer.BlockCopy(source, offset, _buffer, _writeOffset, firstCopy);
                    var remaining = count - firstCopy;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(source, offset + firstCopy, _buffer, 0, remaining);
                    }

                    _writeOffset = (_writeOffset + count) % _buffer.Length;
                    _bufferedBytes += count;
                    Monitor.PulseAll(_sync);
                }
            }

            public int Read(byte[] destination, int offset, int count)
            {
                if (count <= 0)
                {
                    return 0;
                }

                lock (_sync)
                {
                    while (_bufferedBytes == 0 && !_isComplete && !_isDisposed)
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_isDisposed || (_isComplete && _bufferedBytes == 0))
                    {
                        return 0;
                    }

                    var blockAlign = Math.Max(1, WaveFormat.BlockAlign);
                    while (_bufferedBytes > 0 && _bufferedBytes < blockAlign && !_isComplete && !_isDisposed)
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_isDisposed || (_isComplete && _bufferedBytes == 0))
                    {
                        return 0;
                    }

                    var bytesToRead = Math.Min(count, _bufferedBytes);
                    bytesToRead -= bytesToRead % blockAlign;
                    if (bytesToRead <= 0)
                    {
                        _bufferedBytes = 0;
                        Monitor.PulseAll(_sync);
                        return 0;
                    }

                    var firstCopy = Math.Min(bytesToRead, _buffer.Length - _readOffset);
                    Buffer.BlockCopy(_buffer, _readOffset, destination, offset, firstCopy);
                    var remaining = bytesToRead - firstCopy;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(_buffer, 0, destination, offset + firstCopy, remaining);
                    }

                    _readOffset = (_readOffset + bytesToRead) % _buffer.Length;
                    _bufferedBytes -= bytesToRead;
                    Monitor.PulseAll(_sync);
                    return bytesToRead;
                }
            }

            public void MarkComplete()
            {
                lock (_sync)
                {
                    _isComplete = true;
                    Monitor.PulseAll(_sync);
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    _isDisposed = true;
                    _isComplete = true;
                    _bufferedBytes = 0;
                    Monitor.PulseAll(_sync);
                }
            }

            private void ThrowIfDisposed()
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(BlockingStreamingWaveProvider));
                }
            }
        }

        private static async Task<WaveFormat> ReadStreamingWaveFormatAsync(Stream stream, CancellationToken token)
        {
            var riff = new byte[12];
            await ReadExactAsync(stream, riff, token);
            if (!MatchesAscii(riff.AsSpan(0, 4), "RIFF") || !MatchesAscii(riff.AsSpan(8, 4), "WAVE"))
            {
                throw new InvalidDataException("Qwen TTS stream is not a WAV stream.");
            }

            WaveFormat? format = null;
            var chunkHeader = new byte[8];
            while (true)
            {
                await ReadExactAsync(stream, chunkHeader, token);
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));

                if (MatchesAscii(chunkHeader.AsSpan(0, 4), "fmt "))
                {
                    if (chunkSize < 16 || chunkSize > 4096)
                    {
                        throw new InvalidDataException($"Unsupported WAV fmt chunk size: {chunkSize}.");
                    }

                    var fmt = new byte[(int)chunkSize];
                    await ReadExactAsync(stream, fmt, token);
                    format = ParseWaveFormat(fmt);

                    if ((chunkSize & 1) != 0)
                    {
                        await SkipExactAsync(stream, 1, token);
                    }

                    continue;
                }

                if (MatchesAscii(chunkHeader.AsSpan(0, 4), "data"))
                {
                    return format ?? throw new InvalidDataException("WAV data chunk appears before fmt chunk.");
                }

                await SkipExactAsync(stream, chunkSize + (chunkSize & 1), token);
            }
        }

        private static WaveFormat ParseWaveFormat(byte[] fmt)
        {
            var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
            var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4));
            var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));

            if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
            {
                throw new InvalidDataException("Invalid WAV format in Qwen TTS stream.");
            }

            return audioFormat switch
            {
                1 => new WaveFormat((int)sampleRate, bitsPerSample, channels),
                3 when bitsPerSample == 32 => WaveFormat.CreateIeeeFloatWaveFormat((int)sampleRate, channels),
                _ => throw new InvalidDataException($"Unsupported WAV encoding format: {audioFormat}.")
            };
        }

        private static WaveFormat ResolveQwenPcmWaveFormat(HttpResponseMessage response)
        {
            return ResolvePcmWaveFormat(response, QwenPcmSampleRate, QwenPcmChannels, QwenPcmBitsPerSample);
        }

        private static WaveFormat ResolveMossPcmWaveFormat(HttpResponseMessage response)
        {
            return ResolvePcmWaveFormat(response, MossPcmSampleRate, MossPcmChannels, MossPcmBitsPerSample);
        }

        private static WaveFormat ResolveOmniVoicePcmWaveFormat(HttpResponseMessage response)
        {
            return ResolvePcmWaveFormat(response, OmniVoicePcmSampleRate, OmniVoicePcmChannels, OmniVoicePcmBitsPerSample);
        }

        private static WaveFormat ResolvePcmWaveFormat(
            HttpResponseMessage response,
            int defaultSampleRate,
            int defaultChannels,
            int defaultBitsPerSample)
        {
            var sampleRate = TryGetAudioHeaderInt(response, "X-Audio-Sample-Rate")
                ?? TryGetContentTypeParameterInt(response, "rate")
                ?? defaultSampleRate;
            var channels = TryGetAudioHeaderInt(response, "X-Audio-Channels")
                ?? TryGetContentTypeParameterInt(response, "channels")
                ?? defaultChannels;
            var bitsPerSample = TryGetContentTypeParameterInt(response, "bits")
                ?? defaultBitsPerSample;

            return new WaveFormat(sampleRate, bitsPerSample, channels);
        }

        private static int? TryGetAudioHeaderInt(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                foreach (var value in values)
                {
                    if (int.TryParse(value, out var parsed) && parsed > 0)
                    {
                        return parsed;
                    }
                }
            }

            return null;
        }

        private static int? TryGetContentTypeParameterInt(HttpResponseMessage response, string name)
        {
            var parameters = response.Content.Headers.ContentType?.Parameters;
            if (parameters == null)
            {
                return null;
            }

            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parameter.Value?.Trim('"'), out var parsed)
                    && parsed > 0)
                {
                    return parsed;
                }
            }

            return null;
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            await ReadExactAsync(stream, buffer, buffer.Length, token);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of TTS WAV stream.");
                }

                offset += read;
            }
        }

        private static async Task SkipExactAsync(Stream stream, uint count, CancellationToken token)
        {
            var buffer = new byte[Math.Min(8192, Math.Max(1, (int)Math.Min(count, int.MaxValue)))];
            var remaining = count;
            while (remaining > 0)
            {
                var readSize = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, readSize), token);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of TTS WAV stream while skipping chunk.");
                }

                remaining -= (uint)read;
            }
        }

        private static bool MatchesAscii(ReadOnlySpan<byte> value, string ascii)
        {
            if (value.Length != ascii.Length)
            {
                return false;
            }

            for (var i = 0; i < ascii.Length; i++)
            {
                if (value[i] != (byte)ascii[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static WaveStream CreateAudioReader(Stream stream, string? contentType)
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            if (IsWaveContent(contentType) || StreamStartsWith(stream, new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }))
            {
                return new WaveFileReader(stream);
            }

            return new Mp3FileReader(stream);
        }

        private static bool IsWaveContent(string? contentType)
        {
            return !string.IsNullOrWhiteSpace(contentType)
                && (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase));
        }

        private static bool StreamStartsWith(Stream stream, byte[] prefix)
        {
            if (!stream.CanSeek || prefix.Length == 0)
            {
                return false;
            }

            var originalPosition = stream.Position;
            try
            {
                var buffer = new byte[prefix.Length];
                var read = stream.Read(buffer, 0, buffer.Length);
                return read == prefix.Length && buffer.AsSpan().SequenceEqual(prefix);
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }

        private string GetVoiceForLanguage(string language, string provider)
        {
            if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase))
            {
                return GetQwenVoiceForLanguage(language);
            }

            if (string.Equals(provider, AppConfig.TtsProviderMoss, StringComparison.OrdinalIgnoreCase))
            {
                return GetMossVoiceForLanguage(language);
            }

            if (string.Equals(provider, AppConfig.TtsProviderOmniVoice, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return GetEdgeVoiceForLanguage(language);
        }

        private static string GetEdgeVoiceForLanguage(string language)
        {
            return language?.ToLowerInvariant() switch
            {
                "zh" or "zh-cn" or "zh-tw" => "zh-CN-XiaoxiaoNeural",
                "en" or "en-us" => "en-US-JennyNeural",
                "ja" or "jp" => "ja-JP-NanamiNeural",
                "ko" or "kr" => "ko-KR-SunHiNeural",
                "fr" => "fr-FR-DeniseNeural",
                "de" => "de-DE-KatjaNeural",
                "es" => "es-ES-ElviraNeural",
                "ru" => "ru-RU-SvetlanaNeural",
                _ => "zh-CN-XiaoxiaoNeural"
            };
        }

        private static string GetQwenVoiceForLanguage(string language)
        {
            return language?.ToLowerInvariant() switch
            {
                "en" or "en-us" or "en-gb" => "aiden",
                "ja" or "jp" or "ja-jp" => "ono_anna",
                "ko" or "kr" or "ko-kr" => "sohee",
                _ => QwenTtsSpeakerCatalog.DefaultSpeaker
            };
        }

        private static string GetMossVoiceForLanguage(string language)
        {
            return language?.ToLowerInvariant() switch
            {
                "en" or "en-us" or "en-gb" => "Ava",
                "ja" or "jp" or "ja-jp" => "Soyo",
                _ => "Junhao"
            };
        }

        private static string GetMossLanguageForLanguage(string language)
        {
            var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "zh";
            }

            return normalized switch
            {
                "zh" or "zh-cn" or "zh-hans" or "zh-tw" or "zh-hant" or "cn" => "zh",
                "en" or "en-us" or "en-gb" => "en",
                "ja" or "jp" or "ja-jp" => "ja",
                "ko" or "kr" or "ko-kr" => "ko",
                "pt" or "pt-br" or "pt-pt" => "pt",
                _ => normalized.Split('-', 2)[0]
            };
        }

        private static string GetOmniVoiceLanguageForLanguage(string language)
        {
            var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "zh";
            }

            return normalized switch
            {
                "zh" or "zh-cn" or "zh-hans" or "zh-tw" or "zh-hant" or "cn" => "zh",
                "en" or "en-us" or "en-gb" => "en",
                "ja" or "jp" or "ja-jp" => "ja",
                "ko" or "kr" or "ko-kr" => "ko",
                "pt" or "pt-br" or "pt-pt" => "pt",
                _ => normalized.Split('-', 2)[0]
            };
        }

        private string ResolveConfiguredVoiceName(string provider)
        {
            var value = string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                ? _config.QwenTtsVoiceName
                : string.Equals(provider, AppConfig.TtsProviderMoss, StringComparison.OrdinalIgnoreCase)
                    ? _config.MossTtsVoiceName
                    : string.Equals(provider, AppConfig.TtsProviderOmniVoice, StringComparison.OrdinalIgnoreCase)
                        ? _config.OmniVoiceTtsVoiceName
                        : _config.TtsVoiceName;

            if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase))
            {
                var qwenVoice = value?.Trim() ?? string.Empty;
                return QwenTtsSpeakerCatalog.TryNormalizeSpeaker(qwenVoice, out var normalizedQwenVoice)
                    ? normalizedQwenVoice
                    : qwenVoice;
            }

            return value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// 获取输出设备索引
        /// </summary>
        private int GetOutputDeviceIndex(string? outputDeviceName)
        {
            var deviceName = ResolveOutputDeviceName(outputDeviceName);
            if (string.IsNullOrEmpty(deviceName) || deviceName == "default")
                return -1;

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                var productName = caps.ProductName ?? string.Empty;
                if (productName.Contains(deviceName, StringComparison.OrdinalIgnoreCase)
                    || deviceName.Contains(productName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private string ResolveOutputDeviceName(string? outputDeviceName)
        {
            return string.IsNullOrWhiteSpace(outputDeviceName)
                ? (_config.TtsOutputName ?? string.Empty).Trim()
                : outputDeviceName.Trim();
        }

        /// <summary>
        /// 清理播放资源
        /// </summary>
        // Caller must hold _lock.
        private void CleanupPlaybackResourcesUnsafe()
        {
            try
            {
                _streamingBuffer?.Dispose();
            }
            catch { }
            finally
            {
                _streamingBuffer = null;
            }

            try
            {
                _waveOut?.Stop();
            }
            catch { }

            try
            {
                _waveOut?.Dispose();
            }
            catch { }
            finally
            {
                _waveOut = null;
            }

            try
            {
                _audioReader?.Dispose();
            }
            catch { }
            finally
            {
                _audioReader = null;
            }

            try
            {
                _audioStream?.Dispose();
            }
            catch { }
            finally
            {
                _audioStream = null;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    _playbackVersion++;
                    _playbackCts?.Cancel();
                    _playbackCts?.Dispose();
                    _playbackCts = null;

                    CleanupPlaybackResourcesUnsafe();
                    IsSpeaking = false;

                    _logger.Debug("TTS playback stopped");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to stop TTS");
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _httpClient?.Dispose();
            _playbackCts?.Dispose();
            _speakGate.Dispose();
            _engineCatalogGate.Dispose();
        }

        /// <summary>
        /// Downmixes multi-channel reference audio to mono before resampling.
        /// </summary>
        private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;

            public MultiChannelToMonoSampleProvider(ISampleProvider source)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                _channels = Math.Max(1, source.WaveFormat.Channels);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                if (_channels == 1)
                {
                    return _source.Read(buffer, offset, count);
                }

                var sourceBuffer = new float[count * _channels];
                var sourceRead = _source.Read(sourceBuffer, 0, sourceBuffer.Length);
                var framesRead = sourceRead / _channels;

                for (var frame = 0; frame < framesRead; frame++)
                {
                    var sum = 0f;
                    var sourceOffset = frame * _channels;
                    for (var channel = 0; channel < _channels; channel++)
                    {
                        sum += sourceBuffer[sourceOffset + channel];
                    }

                    buffer[offset + frame] = sum / _channels;
                }

                return framesRead;
            }
        }

        /// <summary>
        /// TTS 请求模型
        /// </summary>
        private class TtsRequest
        {
            [JsonPropertyName("input")]
            public string Input { get; set; } = string.Empty;

            [JsonPropertyName("voice")]
            public string Voice { get; set; } = string.Empty;

            [JsonPropertyName("language")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Language { get; set; }

            [JsonPropertyName("speed")]
            public double Speed { get; set; } = 1.2;

            [JsonPropertyName("volume")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? Volume { get; set; }

            [JsonPropertyName("response_format")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? ResponseFormat { get; set; }

            [JsonPropertyName("stream")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? Stream { get; set; }

            [JsonPropertyName("sample_mode")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? SampleMode { get; set; }

            [JsonPropertyName("do_sample")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? DoSample { get; set; }

            [JsonPropertyName("seed")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? Seed { get; set; }

            [JsonPropertyName("voice_clone_max_text_tokens")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? VoiceCloneMaxTextTokens { get; set; }

            [JsonPropertyName("voice_clone_inter_chunk_pause_seconds")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? VoiceCloneInterChunkPauseSeconds { get; set; }

            [JsonPropertyName("trim_long_silence")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? TrimLongSilence { get; set; }

            [JsonPropertyName("max_silence_seconds")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? MaxSilenceSeconds { get; set; }

            [JsonPropertyName("silence_threshold")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? SilenceThreshold { get; set; }

            [JsonPropertyName("chunk_size")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? ChunkSize { get; set; }

            [JsonPropertyName("instruct")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Instruct { get; set; }

            [JsonPropertyName("temperature")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? Temperature { get; set; }

            [JsonPropertyName("num_step")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? NumStep { get; set; }

            [JsonPropertyName("guidance_scale")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? GuidanceScale { get; set; }

            [JsonPropertyName("t_shift")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? TShift { get; set; }

            [JsonPropertyName("denoise")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? Denoise { get; set; }

            [JsonPropertyName("preprocess_prompt")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? PreprocessPrompt { get; set; }

            [JsonPropertyName("postprocess_output")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public bool? PostprocessOutput { get; set; }

            [JsonPropertyName("layer_penalty_factor")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? LayerPenaltyFactor { get; set; }

            [JsonPropertyName("position_temperature")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? PositionTemperature { get; set; }

            [JsonPropertyName("class_temperature")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? ClassTemperature { get; set; }
        }
    }
}
