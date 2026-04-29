using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
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
        private long _playbackVersion;
        private const int QwenPcmSampleRate = 24000;
        private const int QwenPcmBitsPerSample = 16;
        private const int QwenPcmChannels = 1;
        private const int StreamingDesiredLatencyMilliseconds = 220;
        private const int StreamingNumberOfBuffers = 4;
        private const double QwenStableTemperature = 0.65d;
        private static readonly TimeSpan StreamingBufferDuration = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan StreamingStartBufferDuration = TimeSpan.FromMilliseconds(900);

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
            return SpeakAsync(text, "zh", voiceName);
        }

        public async Task SpeakAsync(string text, string language, string? voiceName = null)
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

                _logger.Debug("Server TTS request - textLength: {Length}, language: {Language}",
                    text.Length, language);

                var provider = AppConfig.NormalizeTtsProvider(_config.TtsProvider);

                // Select voice: explicit argument > configured voice > language default.
                var configuredVoice = _config.TtsVoiceName?.Trim();
                var voice = !string.IsNullOrWhiteSpace(voiceName)
                    ? voiceName
                    : !string.IsNullOrWhiteSpace(configuredVoice)
                        ? configuredVoice
                        : GetVoiceForLanguage(language, provider);
                var speed = Math.Clamp((double)_config.TtsSpeed, 0.5d, 2.0d);

                var request = CreateTtsPayload(text, voice, speed, provider);
                var json = JsonSerializer.Serialize(request);

                // Build TTS API URL via BackendUrlHelper for consistency.
                var ttsUrl = BuildTtsUrl(provider);
                _logger.Debug("Server TTS request - URL: {Url}, provider: {Provider}, body: {Body}, language: {Language}, voice: {Voice}",
                    ttsUrl, provider, json, language, voice);

                using var response = await SendTtsRequestWithAuthRetryAsync(ttsUrl, json);
                if (response == null)
                {
                    _logger.Warning("Server TTS aborted because token is unavailable.");
                    ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                    {
                        Code = ServiceErrorCode.TtsAuthRequired,
                        Context = new Dictionary<string, string>
                        {
                            ["Source"] = "语音合成",
                            ["Stage"] = "tts"
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
                            ["Stage"] = "tts"
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
                            ["Stage"] = "tts"
                        }
                    });
                    _logger.Warning("Server TTS still returned 401 after centralized auth retry.");
                    SpeakError?.Invoke(this, "登录状态已过期，请重新登录后再试。");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                    {
                        Code = ServiceErrorCode.TtsHttpError,
                        Context = new Dictionary<string, string>
                        {
                            ["Source"] = "语音合成",
                            ["Stage"] = "tts",
                            ["StatusCode"] = ((int)response.StatusCode).ToString()
                        }
                    });
                    _logger.Error("TTS request failed - statusCode: {StatusCode}, response: {Response}",
                        (int)response.StatusCode, errorContent);
                    SpeakError?.Invoke(this, $"语音合成失败（HTTP {(int)response.StatusCode}）。");
                    return;
                }

                if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                    && !BackendUrlHelper.UsesLegacyBackendApi(_config))
                {
                    await PlayQwenStreamingAudioAsync(response);
                }
                else
                {
                    await PlayAudioStreamAsync(response);
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
                        ["Stage"] = "tts"
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
            return await _authService.SendAuthorizedAsync(
                "Server TTS",
                () => CreateTtsRequest(ttsUrl, json),
                (request, cancellationToken) => _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)).ConfigureAwait(false);
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

        private string BuildTtsUrl(string provider)
        {
            if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase)
                && !BackendUrlHelper.UsesLegacyBackendApi(_config))
            {
                return $"{BackendUrlHelper.BuildApiBaseUrl(_config)}tts";
            }

            return $"{BackendUrlHelper.BuildApiBaseUrl(_config)}func/tts";
        }

        private TtsRequest CreateTtsPayload(string text, string voice, double speed, string provider)
        {
            var request = new TtsRequest
            {
                Input = text,
                Voice = voice,
                Speed = speed
            };

            if (string.Equals(provider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase))
            {
                request.ResponseFormat = "pcm";
                request.ChunkSize = 12;
                if (_config.QwenTtsToneModeValue == QwenTtsToneMode.Custom)
                {
                    request.Instruct = string.IsNullOrWhiteSpace(_config.QwenTtsInstruct)
                        ? AppConfig.DefaultQwenTtsInstruct
                        : _config.QwenTtsInstruct.Trim();
                    request.Temperature = QwenStableTemperature;
                }
            }

            return request;
        }

        private async Task PlayAudioStreamAsync(HttpResponseMessage response)
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

                var deviceIndex = GetOutputDeviceIndex();
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

        private async Task PlayQwenStreamingAudioAsync(HttpResponseMessage response)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (IsWaveContent(contentType))
            {
                // Compatibility path for older Qwen proxy deployments that still stream WAV.
                await PlayStreamingAudioAsync(response, ReadStreamingWaveFormatAsync, "Qwen TTS WAV");
                return;
            }

            var pcmFormat = ResolveQwenPcmWaveFormat(response);
            await PlayStreamingAudioAsync(
                response,
                (_, _) => Task.FromResult(pcmFormat),
                "Qwen TTS PCM");
        }

        private async Task PlayStreamingAudioAsync(
            HttpResponseMessage response,
            Func<Stream, CancellationToken, Task<WaveFormat>> readWaveFormatAsync,
            string source)
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

                var deviceIndex = GetOutputDeviceIndex();
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

                var started = await PumpStreamingAudioAsync(stream, bufferedProvider, waveOut, tcs.Task, token);
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
            CancellationToken token)
        {
            var buffer = new byte[8192];
            var started = false;

            while (!token.IsCancellationRequested)
            {
                if (playbackStoppedTask.IsCompleted)
                {
                    return started;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                {
                    if (!started && bufferedProvider.BufferedBytes > 0 && !playbackStoppedTask.IsCompleted)
                    {
                        waveOut.Play();
                        started = true;
                    }

                    return started;
                }

                await AddSamplesWithBackpressureAsync(bufferedProvider, buffer, read, playbackStoppedTask, token);

                if (!started && bufferedProvider.BufferedDuration >= StreamingStartBufferDuration)
                {
                    waveOut.Play();
                    started = true;
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

                    while (_bufferedBytes < count && !_isComplete && !_isDisposed)
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_isDisposed || (_isComplete && _bufferedBytes == 0))
                    {
                        return 0;
                    }

                    var bytesToRead = Math.Min(count, _bufferedBytes);
                    var blockAlign = Math.Max(1, WaveFormat.BlockAlign);
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
            var sampleRate = TryGetAudioHeaderInt(response, "X-Audio-Sample-Rate")
                ?? TryGetContentTypeParameterInt(response, "rate")
                ?? QwenPcmSampleRate;
            var channels = TryGetAudioHeaderInt(response, "X-Audio-Channels")
                ?? TryGetContentTypeParameterInt(response, "channels")
                ?? QwenPcmChannels;

            return new WaveFormat(sampleRate, QwenPcmBitsPerSample, channels);
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
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
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
                "en" or "en-us" or "en-gb" => "echo",
                "ja" or "jp" or "ja-jp" => "shimmer",
                "ko" or "kr" or "ko-kr" => "nova",
                _ => "alloy"
            };
        }

        /// <summary>
        /// 获取输出设备索引
        /// </summary>
        private int GetOutputDeviceIndex()
        {
            if (string.IsNullOrEmpty(_config.TtsOutputName) || _config.TtsOutputName == "default")
                return -1;

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                var productName = caps.ProductName ?? string.Empty;
                if (productName.Contains(_config.TtsOutputName, StringComparison.OrdinalIgnoreCase)
                    || _config.TtsOutputName.Contains(productName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
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

            [JsonPropertyName("speed")]
            public double Speed { get; set; } = 1.2;

            [JsonPropertyName("response_format")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? ResponseFormat { get; set; }

            [JsonPropertyName("chunk_size")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? ChunkSize { get; set; }

            [JsonPropertyName("instruct")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Instruct { get; set; }

            [JsonPropertyName("temperature")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? Temperature { get; set; }
        }
    }
}
