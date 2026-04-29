using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.Tts
{
    /// <summary>
    /// TTS via late-bound System.Speech.Synthesis to avoid hard compile dependency.
    /// If System.Speech is unavailable, this becomes a no-op.
    /// </summary>
    public class SystemSpeechTtsService : ITtsService
    {
        private static readonly ILogger _logger = Log.ForContext<SystemSpeechTtsService>();
        private readonly AppConfig _config;
        private readonly object? _synth;
        private readonly MethodInfo? _speak;
        private readonly MethodInfo? _speakAsync;
        private readonly MethodInfo? _cancelAll;
        private readonly MethodInfo? _selectVoice;
        private readonly MethodInfo? _setRate;
        private readonly MethodInfo? _setOutputDefault;
        private readonly object _lock = new();
        private readonly bool _available;
        private bool _isSpeaking;

        public bool IsSpeaking => _isSpeaking;

        public event EventHandler? SpeakCompleted;
        public event EventHandler<string>? SpeakError;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        public SystemSpeechTtsService(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger.Debug("鍒濆鍖?SystemSpeechTtsService");
            try
            {
                Assembly asm;
                try
                {
                    asm = Assembly.Load(new AssemblyName("System.Speech"));
                }
                catch
                {
                    // Fallback: load from app base directory if present (helps when probing differs).
                    var path = Path.Combine(AppContext.BaseDirectory, "System.Speech.dll");
                    if (!File.Exists(path))
                    {
                        throw;
                    }

                    asm = Assembly.LoadFrom(path);
                }

                var type = asm.GetType("System.Speech.Synthesis.SpeechSynthesizer");
                if (type != null)
                {
                    _synth = Activator.CreateInstance(type);
                    _speak = type.GetMethod("Speak", new[] { typeof(string) });
                    _speakAsync = type.GetMethod("SpeakAsync", new[] { typeof(string) });
                    _cancelAll = type.GetMethod("SpeakAsyncCancelAll", Type.EmptyTypes);
                    _selectVoice = type.GetMethod("SelectVoice", new[] { typeof(string) });
                    _setRate = type.GetProperty("Rate")?.SetMethod;
                    _setOutputDefault = type.GetMethod("SetOutputToDefaultAudioDevice", Type.EmptyTypes);
                    _available = _synth != null && (_speak != null || _speakAsync != null);
                    if (_available)
                    {
                        _logger.Information("System.Speech.TTS 初始化成功");
                    }
                    else
                    {
                        _logger.Warning("System.Speech.TTS 初始化失败 - 未找到所需方法");
                    }
                }
                else
                {
                    _logger.Warning("System.Speech.TTS 初始化失败 - 未找到类型");
                    _available = false;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "System.Speech not available");
                _available = false;
            }
        }

        public Task SpeakAsync(string text, string? voiceName = null)
        {
            // 榛樿浣跨敤涓枃
            return SpeakAsync(text, "zh", voiceName);
        }

        public Task SpeakAsync(string text, string language, string? voiceName = null)
        {
            if (!_available || string.IsNullOrWhiteSpace(text) || _synth == null || (_speak == null && _speakAsync == null))
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logger.Warning("TTS涓嶅彲鐢ㄦ椂灏濊瘯鏈楄 - 鏂囨湰闀垮害: {Length}", text.Length);
                }
                return Task.CompletedTask;
            }

            _logger.Debug("TTS鏈楄璇锋眰 - 鏂囨湰闀垮害: {Length}, 璇█: {Language}, 璇煶: {Voice}",
                text.Length, language, voiceName ?? "default");

            return Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        _isSpeaking = true;
                        _cancelAll?.Invoke(_synth, null);
                        _setOutputDefault?.Invoke(_synth, null);

                        if (!string.IsNullOrWhiteSpace(voiceName))
                        {
                            try { _selectVoice?.Invoke(_synth, new object?[] { voiceName }); } catch { }
                        }

                        try
                        {
                            var clamped = Math.Clamp(_config.TtsSpeed, 0.5f, 2.0f);
                            var rate = (int)Math.Round((clamped - 1.0f) * 10.0f);
                            _setRate?.Invoke(_synth, new object?[] { rate });
                        }
                        catch { }

                        // Prefer blocking Speak() so callers can await sequentially (used by multi-select TTS).
                        if (_speak != null)
                        {
                            _speak.Invoke(_synth, new object?[] { text });
                            _isSpeaking = false;
                            SpeakCompleted?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            _speakAsync!.Invoke(_synth, new object?[] { text });
                            _isSpeaking = false;
                            SpeakCompleted?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    catch (Exception ex)
                    {
                        _isSpeaking = false;
                        _logger.Error(ex, "TTS鏈楄澶辫触");
                        ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                        {
                            Code = ServiceErrorCode.TtsFailed,
                            Context = new Dictionary<string, string>
                            {
                                ["Source"] = "本地语音合成",
                                ["Stage"] = "tts"
                            },
                            Exception = ex
                        });
                        SpeakError?.Invoke(this, ex.Message);
                    }
                }
            });
        }

        public void Stop()
        {
            if (!_available || _synth == null || _cancelAll == null) return;
            lock (_lock)
            {
                try
                {
                    _cancelAll.Invoke(_synth, null);
                    _isSpeaking = false;
                }
                catch { }
            }
        }
    }
}

