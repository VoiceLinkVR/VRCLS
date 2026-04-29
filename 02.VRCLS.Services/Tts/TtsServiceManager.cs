using System;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.Tts
{
    /// <summary>
    /// TTS 服务管理器
    /// 根据配置决定使用本地 TTS 还是服务器 TTS
    /// </summary>
    public class TtsServiceManager : ITtsService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<TtsServiceManager>();
        private readonly AppConfig _config;
        private readonly ITtsService _localTts;
        private readonly ITtsService _serverTts;
        private readonly SemaphoreSlim _queueGate = new(1, 1);
        private bool _disposed;

        public bool IsSpeaking => CurrentService?.IsSpeaking ?? false;

        public event EventHandler? SpeakCompleted;
        public event EventHandler<string>? SpeakError;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        private void OnLocalSpeakCompleted(object? sender, EventArgs e) => SpeakCompleted?.Invoke(this, e);
        private void OnLocalSpeakError(object? sender, string error) => SpeakError?.Invoke(this, error);
        private void OnLocalErrorOccurred(object? sender, ServiceErrorEvent error) => ErrorOccurred?.Invoke(this, error);
        private void OnServerSpeakCompleted(object? sender, EventArgs e) => SpeakCompleted?.Invoke(this, e);
        private void OnServerSpeakError(object? sender, string error) => SpeakError?.Invoke(this, error);
        private void OnServerErrorOccurred(object? sender, ServiceErrorEvent error) => ErrorOccurred?.Invoke(this, error);

        /// <summary>
        /// 当前使用的 TTS 服务
        /// </summary>
        private ITtsService? CurrentService => SelectTtsService();

        public TtsServiceManager(AppConfig config, ITtsService localTts, ITtsService serverTts)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _localTts = localTts ?? throw new ArgumentNullException(nameof(localTts));
            _serverTts = serverTts ?? throw new ArgumentNullException(nameof(serverTts));

            // 转发事件
            _localTts.SpeakCompleted += OnLocalSpeakCompleted;
            _localTts.SpeakError += OnLocalSpeakError;
            _localTts.ErrorOccurred += OnLocalErrorOccurred;
            _serverTts.SpeakCompleted += OnServerSpeakCompleted;
            _serverTts.SpeakError += OnServerSpeakError;
            _serverTts.ErrorOccurred += OnServerErrorOccurred;

            _logger.Debug("TTS 服务管理器已初始化");
        }

        /// <summary>
        /// 选择 TTS 服务
        /// 优先使用服务器 TTS，如果不可用则回退到本地 TTS
        /// </summary>
        private ITtsService SelectTtsService()
        {
            return _config.TtsEngineModeValue == TtsEngineMode.System ? _localTts : _serverTts;
        }

        public Task SpeakAsync(string text, string? voiceName = null)
        {
            if (_disposed) return Task.CompletedTask;

            // NOTE: C# overload resolution will pick this overload for calls like SpeakAsync(text, "zh"),
            // which is easily mistaken as specifying the language. Add a small heuristic to treat short
            // language-like values ("zh", "en", "zh-CN", ...) as the language instead of the voice name.
            if (!string.IsNullOrWhiteSpace(voiceName) && LooksLikeLanguageCode(voiceName))
            {
                return SpeakAsync(text, voiceName, voiceName: null);
            }

            return SpeakAsync(text, "zh", voiceName);
        }

        public async Task SpeakAsync(string text, string language, string? voiceName = null)
        {
            if (_disposed) return;

            var service = CurrentService;
            if (service == null)
            {
                _logger.Warning("没有可用的 TTS 服务");
                return;
            }

            if (_config.TtsInterruptPolicyValue == TtsInterruptPolicy.InterruptCurrent)
            {
                // Interrupt mode: new speech preempts current playback.
                service.Stop();
                await service.SpeakAsync(text, language, voiceName);
                return;
            }

            // Queue mode: serialize requests in arrival order.
            await _queueGate.WaitAsync();
            try
            {
                await service.SpeakAsync(text, language, voiceName);
            }
            finally
            {
                _queueGate.Release();
            }
        }

        public void Stop()
        {
            if (_disposed) return;

            _localTts.Stop();
            _serverTts.Stop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _localTts.SpeakCompleted -= OnLocalSpeakCompleted;
            _localTts.SpeakError -= OnLocalSpeakError;
            _localTts.ErrorOccurred -= OnLocalErrorOccurred;
            _serverTts.SpeakCompleted -= OnServerSpeakCompleted;
            _serverTts.SpeakError -= OnServerSpeakError;
            _serverTts.ErrorOccurred -= OnServerErrorOccurred;
            _queueGate.Dispose();
        }

        private static bool LooksLikeLanguageCode(string value)
        {
            // Accept: "zh", "en", "ja", "zh-CN", "en-US" (case-insensitive).
            var s = value.Trim();
            if (s.Length == 2)
            {
                return char.IsLetter(s[0]) && char.IsLetter(s[1]);
            }

            if (s.Length == 5 && s[2] == '-')
            {
                return char.IsLetter(s[0]) && char.IsLetter(s[1]) &&
                       char.IsLetter(s[3]) && char.IsLetter(s[4]);
            }

            return false;
        }
    }

    /// <summary>
    /// TTS 服务类型
    /// </summary>
    public enum TtsServiceType
    {
        /// <summary>
        /// 本地 System.Speech TTS
        /// </summary>
        Local,

        /// <summary>
        /// 服务器 TTS (Azure 等)
        /// </summary>
        Server
    }
}
