using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Audio;
using VRCLS.Services.Auth;
using Serilog;

namespace VRCLS.Services.Recognition
{
    public class FastApiRecognitionService : IRecognitionService, IDisposable
    {
        public const string HttpClientName = "VRCLS.RecognitionApi";
        private static readonly ILogger _logger = Log.ForContext<FastApiRecognitionService>();
        private readonly HttpClient _httpClient;
        private readonly OpusCodecService _opusCodec;
        private readonly AppConfig _config;
        private readonly IBackendAuthService _authService;
        private readonly IDisposable? _ownedAuthDisposable;
        private readonly string _pipelineName;
        private readonly object _requestLock = new();
        private CancellationTokenSource _activeRequestCts = new();
        private static readonly TimeSpan ErrorNotificationInterval = TimeSpan.FromSeconds(30);
        private DateTime _lastAuthRequiredErrorUtc = DateTime.MinValue;
        private DateTime _lastAuthExpiredErrorUtc = DateTime.MinValue;
        private bool _disposed;

        public event EventHandler<TranscriptionResult>? TextRecognized;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        public FastApiRecognitionService(
            AppConfig config,
            string pipelineName,
            IBackendAuthService? authService = null,
            IHttpClientFactory? httpClientFactory = null)
        {
            _logger.Information("初始化 FastApiRecognitionService - 管道: {PipelineName}", pipelineName);

            _config = config;
            _pipelineName = pipelineName;
            _opusCodec = new OpusCodecService();

            // Keep URL handling consistent with the rest of the app: normalize to .../api/
            var baseAddress = BackendUrlHelper.BuildApiBaseAddress(config);
            _logger.Debug("配置HTTP客户端 - API地址: {BaseAddress}, 超时: infinite", baseAddress);

            _httpClient = httpClientFactory?.CreateClient(HttpClientName) ?? CreateFallbackHttpClient();

            if (authService != null)
            {
                _authService = authService;
            }
            else
            {
                // Backward compatible: tests/console code may new() this service directly.
                var owned = new BackendAuthService(config);
                _authService = owned;
                _ownedAuthDisposable = owned;
            }

            _logger.Debug("FastApiRecognitionService 初始化完成");
            _authService.StateChanged += OnAuthStateChanged;
        }

        private static HttpClient CreateFallbackHttpClient()
        {
            return new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public async Task LoginAsync()
        {
            // Delegate to the shared auth service so UI/token state stays consistent.
            _logger.Debug("尝试登录(共享鉴权) - 用户名: {Username}", _config.UserInfo.Username);
            await _authService.LoginAsync();
        }

        public void Start()
        {
            _logger.Debug("FastApiRecognitionService 启动");
            _ = WarmUpAccessTokenAsync();
        }

        public void Stop() { }

        public void ProcessAudio(float[] audioData, RecognitionSegmentInfo? segmentInfo = null)
        {
            if (audioData == null || audioData.Length == 0)
            {
                _logger.Warning("接收到的音频数据为空");
                return;
            }

            _logger.Verbose("处理音频数据 - 样本数: {SampleCount}, 时长: {Duration:F2}s",
                audioData.Length, audioData.Length / 16000.0);

            if (!TryGetActiveRequestCancellationToken(out var requestToken))
            {
                return;
            }

            _ = Task.Run(() => SendAudioAsync(audioData, segmentInfo, requestToken));
        }

        private (string sourceLanguage, string[] targetLanguages) GetEffectiveLanguagesForRequest()
        {
            var source = NormalizeRecognitionSourceLanguage(_config.ResolveSourceLanguage(_pipelineName));
            var targets = _config.ResolveEnabledTargetLanguages(_pipelineName).ToArray();

            return (source, targets);
        }

        private static string NormalizeRecognitionSourceLanguage(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) || normalized == "auto"
                ? "zh"
                : normalized;
        }

        private async Task SendAudioAsync(float[] audioData, RecognitionSegmentInfo? segmentInfo, CancellationToken cancellationToken)
        {
            try
            {
                _logger.Debug("Encoding and packaging audio payload");
                byte[] packagedData = _opusCodec.EncodeAndPackage(audioData);

                if (packagedData.Length == 0)
                {
                    _logger.Warning("Encoded audio payload is empty");
                    return;
                }

                _logger.Verbose("Audio payload encoded - original: {OriginalSize}, encoded: {EncodedSize}",
                    audioData.Length * 4, packagedData.Length);

                var (effectiveSourceLanguage, effectiveTargetLanguages) = GetEffectiveLanguagesForRequest();
                var effectiveAsrEngine = AppConfig.IsChineseRecognitionLanguage(effectiveSourceLanguage)
                    ? _config.ResolveChineseAsrEngine(_pipelineName)
                    : string.Empty;
                var recognitionOnly = effectiveTargetLanguages.Length == 0;
                if (effectiveTargetLanguages.Length == 0)
                {
                    _logger.Debug(
                        "[{PipelineName}] No enabled translation targets; using recognition-only endpoint.",
                        _pipelineName);
                }

                HttpRequestMessage CreateRecognitionRequest()
                {
                    var content = new MultipartFormDataContent();

                    var fileContent = new ByteArrayContent(packagedData);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/opus");
                    content.Add(fileContent, "file", "audio.opus");

                    content.Add(new StringContent(effectiveSourceLanguage), "sourceLanguage");
                    if (!string.IsNullOrWhiteSpace(effectiveAsrEngine))
                    {
                        content.Add(new StringContent(effectiveAsrEngine), "engine");
                        _logger.Debug(
                            "[{PipelineName}] Attach Chinese ASR engine: {AsrEngine}",
                            _pipelineName,
                            effectiveAsrEngine);
                    }

                    if (!recognitionOnly)
                    {
                        content.Add(new StringContent(effectiveTargetLanguages[0]), "targetLanguage");

                        var targetLanguage2 = effectiveTargetLanguages.ElementAtOrDefault(1);
                        if (!string.IsNullOrWhiteSpace(targetLanguage2))
                        {
                            content.Add(new StringContent(targetLanguage2), "targetLanguage2");
                            _logger.Debug("Attach secondary target language: {Lang2}", targetLanguage2);
                        }

                        var targetLanguage3 = effectiveTargetLanguages.ElementAtOrDefault(2);
                        if (!string.IsNullOrWhiteSpace(targetLanguage3))
                        {
                            content.Add(new StringContent(targetLanguage3), "targetLanguage3");
                            _logger.Debug("Attach tertiary target language: {Lang3}", targetLanguage3);
                        }
                    }

                    var emojiOutput = string.Equals(_config.FilterEmoji, "true", StringComparison.OrdinalIgnoreCase)
                        ? "true"
                        : "false";
                    content.Add(new StringContent(emojiOutput), "emojiOutput");

                    var relativePath = recognitionOnly
                        ? "whisper/multitranscription"
                        : "func/multitranslateToOtherLanguage";
                    var requestUrl = new Uri($"{BackendUrlHelper.BuildApiBaseUrl(_config)}{relativePath}", UriKind.Absolute);
                    return new HttpRequestMessage(HttpMethod.Post, requestUrl)
                    {
                        Content = content
                    };
                }

                using var response = await _authService.SendAuthorizedAsync(
                    $"Recognition API ({_pipelineName})",
                    CreateRecognitionRequest,
                    (request, ct) =>
                    {
                        _logger.Debug("Send POST request to recognition API");
                        return _httpClient.SendAsync(request, ct);
                    },
                    cancellationToken);
                if (response == null)
                {
                    _logger.Warning("[{PipelineName}] Missing token, skip recognition request.", _pipelineName);
                    if (ShouldNotifyError(ref _lastAuthRequiredErrorUtc))
                    {
                        RaiseRecognitionError(
                            ServiceErrorCode.RecognitionAuthRequired,
                            "recognition",
                            "识别服务需要登录账户。",
                            segmentInfo);
                    }
                    return;
                }

                _logger.Debug("Recognition API response status: {StatusCode}", response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    _logger.Debug("Parse recognition API response");
                    var result = await response.Content.ReadFromJsonAsync<TranslationApiResponse>(cancellationToken: cancellationToken);

                    if (result != null)
                    {
                        _logger.Information("[{PipelineName}] Recognition API request succeeded", _pipelineName);
                        OnTextRecognized(result, effectiveSourceLanguage, effectiveTargetLanguages, segmentInfo);
                    }
                    else
                    {
                        _logger.Warning("[{PipelineName}] Recognition API returned empty result", _pipelineName);
                    }
                    return;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var unauthorizedContent = await response.Content.ReadAsStringAsync();
                    _logger.Warning("[{PipelineName}] Recognition API returned 401 after centralized auth retry. content: {Content}", _pipelineName, unauthorizedContent);
                    if (ShouldNotifyError(ref _lastAuthExpiredErrorUtc))
                    {
                        RaiseRecognitionError(
                            ServiceErrorCode.RecognitionAuthExpired,
                            "recognition",
                            "识别服务登录状态已过期，请重新登录后再试。",
                            segmentInfo);
                    }
                    return;
                }

                if ((int)response.StatusCode == 430)
                {
                    var tooManyContent = await response.Content.ReadAsStringAsync();
                    _authService.NotifyTooManyRequests(source: "识别服务", backendDetail: tooManyContent, channel: _pipelineName, stage: "recognition", segmentId: segmentInfo?.SegmentId);
                    _logger.Warning("[{PipelineName}] Recognition API returned 430 - content: {Content}", _pipelineName, tooManyContent);
                    return;
                }

                var err = await response.Content.ReadAsStringAsync();
                _logger.Error("[{PipelineName}] Recognition API failed - status: {StatusCode}, content: {Content}",
                    _pipelineName, response.StatusCode, err);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("[{PipelineName}] Recognition request canceled because auth state changed or the service is disposing.", _pipelineName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[{PipelineName}] Recognition request exception", _pipelineName);
            }
        }

        private async Task WarmUpAccessTokenAsync()
        {
            try
            {
                var accessToken = await _authService.GetAccessTokenAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    _logger.Debug("[{PipelineName}] Recognition auth warm-up completed without an access token.", _pipelineName);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[{PipelineName}] Recognition auth warm-up failed.", _pipelineName);
            }
        }

        private bool ShouldNotifyError(ref DateTime lastNotificationUtc)
        {
            var now = DateTime.UtcNow;
            if (now - lastNotificationUtc < ErrorNotificationInterval)
            {
                return false;
            }

            lastNotificationUtc = now;
            return true;
        }

        private void RaiseRecognitionError(
            ServiceErrorCode code,
            string stage,
            string reason,
            RecognitionSegmentInfo? segmentInfo,
            Exception? exception = null)
        {
            var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Source"] = "识别服务",
                ["Pipeline"] = _pipelineName,
                ["Stage"] = stage,
                ["Reason"] = reason
            };

            if (segmentInfo?.SegmentId > 0)
            {
                context["SegmentId"] = segmentInfo.SegmentId.ToString();
            }

            ErrorOccurred?.Invoke(this, new ServiceErrorEvent
            {
                Code = code,
                Context = context,
                Exception = exception
            });
        }

        private void OnAuthStateChanged(object? sender, BackendAuthState state)
        {
            if (!state.HasToken && state.Status != BackendAuthStatus.LoggingIn)
            {
                CancelActiveRequest("auth token unavailable");
            }
        }

        private bool TryGetActiveRequestCancellationToken(out CancellationToken token)
        {
            lock (_requestLock)
            {
                if (_disposed)
                {
                    token = CancellationToken.None;
                    return false;
                }

                token = _activeRequestCts.Token;
                return true;
            }
        }

        private void CancelActiveRequest(string reason)
        {
            lock (_requestLock)
            {
                if (_disposed)
                {
                    return;
                }

                _logger.Debug("[{PipelineName}] Cancel in-flight recognition request: {Reason}", _pipelineName, reason);
                _activeRequestCts.Cancel();
            }
        }

        private void OnTextRecognized(TranslationApiResponse apiResponse, string sourceLanguage, string[] targetLanguages, RecognitionSegmentInfo? segmentInfo)
        {
            var original = apiResponse.Text ?? string.Empty;
            var t1 = apiResponse.TranslatedText ?? string.Empty;
            var t2 = apiResponse.TranslatedText2 ?? string.Empty;
            var t3 = apiResponse.TranslatedText3 ?? string.Empty;

            if (string.IsNullOrWhiteSpace(original)
                && string.IsNullOrWhiteSpace(t1)
                && string.IsNullOrWhiteSpace(t2)
                && string.IsNullOrWhiteSpace(t3))
            {
                _logger.Debug(
                    "[{PipelineName}] Recognition API returned empty text, skipping (segmentId={SegmentId})",
                    _pipelineName,
                    segmentInfo?.SegmentId ?? 0);
                return;
            }

            _logger.Debug("处理API响应数据");

            var combinedList = new System.Collections.Generic.List<string> { original };
            if (!string.IsNullOrEmpty(t1))
            {
                combinedList.Add(t1);
                _logger.Debug("添加翻译1: {Trans1}", t1);
            }
            if (!string.IsNullOrEmpty(t2))
            {
                combinedList.Add(t2);
                _logger.Debug("添加翻译2: {Trans2}", t2);
            }
            if (!string.IsNullOrEmpty(t3))
            {
                combinedList.Add(t3);
                _logger.Debug("添加翻译3: {Trans3}", t3);
            }

            string combinedText = string.Join("||", combinedList);
            _logger.Debug("组合文本 - 组合数: {Count}, 总长度: {Length}", combinedList.Count, combinedText.Length);

            var transcriptionResult = new TranscriptionResult
            {
                Source = _pipelineName,
                Text = combinedText,
                Original = original,
                Translation = t1,
                Translation2 = t2,
                Translation3 = t3,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguages.ElementAtOrDefault(0) ?? string.Empty,
                TargetLanguage2 = targetLanguages.ElementAtOrDefault(1) ?? string.Empty,
                TargetLanguage3 = targetLanguages.ElementAtOrDefault(2) ?? string.Empty,
                IsFinal = true,
                Confidence = 1.0f,
                SegmentId = segmentInfo?.SegmentId ?? 0,
                CapturedAtUtc = segmentInfo?.CapturedAtUtc ?? DateTime.UtcNow
            };

            TextRecognized?.Invoke(this, transcriptionResult);
            _logger.Information("[{PipelineName}] 识别结果已触发 - 原文: '{Original}'", _pipelineName, original);
        }

        /// <summary>
        /// Called when VAD detects end of speech segment.
        /// For remote API, this is a no-op since each request is independent.
        /// </summary>
        public void FinishSegment()
        {
            // No-op for remote API - each ProcessAudio call is independent
        }

        public bool IsRunning => _authService.State.Status == BackendAuthStatus.LoggedIn && _authService.State.HasToken;

        public void Dispose()
        {
            _logger.Debug("释放 FastApiRecognitionService 资源");

            try
            {
                _authService.StateChanged -= OnAuthStateChanged;
                CancellationTokenSource requestCts;
                lock (_requestLock)
                {
                    _disposed = true;
                    requestCts = _activeRequestCts;
                    requestCts.Cancel();
                }

                requestCts.Dispose();
                _opusCodec.Dispose();
                _httpClient.Dispose();
                _ownedAuthDisposable?.Dispose();
                _logger.Information("FastApiRecognitionService 资源已释放");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "释放 FastApiRecognitionService 资源失败");
            }
        }

        private class TranslationApiResponse
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("translatedText")]
            public string TranslatedText { get; set; } = string.Empty;

            [JsonPropertyName("translatedText2")]
            public string TranslatedText2 { get; set; } = string.Empty;

            [JsonPropertyName("translatedText3")]
            public string TranslatedText3 { get; set; } = string.Empty;
        }
    }
}

