using System;
using System.IO;
using System.Linq;
using System.Threading;
using SherpaOnnx;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Recognition
{
    /// <summary>
    /// Sherpa ASR service.
    /// - When useVad is true, each ProcessAudio call is treated as a complete VAD segment.
    /// - When useVad is false, audio is streamed continuously and partial results are emitted.
    /// </summary>
    public class SherpaAsrService : IRecognitionService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<SherpaAsrService>();
        private readonly OnlineRecognizer _recognizer;
        private OnlineStream? _stream;
        private volatile bool _isRunning;
        private readonly string _source;
        private readonly string _modelPath;
        private int _segmentIndex;
        private readonly bool _useVad;
        private readonly object _streamLock = new();
        private bool _disposed;

        // Padding durations in seconds (from official demo)
        private const float LeftPaddingSeconds = 0.3f;
        private const float TailPaddingSeconds = 0.6f;
        private const int SampleRate = 16000;
        private const int MaxCpuThreads = 4;

        private static readonly float[] LeftPadding = new float[(int)(SampleRate * LeftPaddingSeconds)];
        private static readonly float[] TailPadding = new float[(int)(SampleRate * TailPaddingSeconds)];

        // Streaming heuristics (used only when VAD is disabled)
        private const float SilenceThreshold = 0.005f;
        private static readonly TimeSpan PartialEmitMinInterval = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan StreamingSegmentStartFinalizeGuard = TimeSpan.FromMilliseconds(280);

        private readonly float _endpointSilenceSeconds;
        private volatile float _silenceSeconds;
        private string _lastPartialText = string.Empty;
        private string _lastFinalText = string.Empty;
        private DateTime _lastPartialEmitUtc = DateTime.MinValue;
        private long _streamStartedUtcTicks = DateTime.MinValue.Ticks;
        private RecognitionSegmentInfo _currentSegmentInfo = RecognitionSegmentInfo.Empty;

        public event EventHandler<TranscriptionResult>? TextRecognized;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public SherpaAsrService(
            string modelPath = "models/sherpa-onnx-streaming-zipformer-zh-CN-2023-12-12",
            string source = "Mic",
            bool useVad = true,
            float endpointSilenceSeconds = 1.2f)
        {
            _modelPath = modelPath;
            _source = source;
            _useVad = useVad;
            _endpointSilenceSeconds = Math.Clamp(endpointSilenceSeconds, 0.8f, 3.0f);

            _logger.Information(
                "Initializing SherpaAsrService. ModelPath: {ModelPath}, Source: {Source}, UseVad: {UseVad}, EndpointSilenceSeconds: {EndpointSilenceSeconds:F2}",
                modelPath, source, useVad, _endpointSilenceSeconds);

            if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
            {
                _logger.Error("SherpaOnnx model path not found: {ModelPath}", modelPath);
                throw new DirectoryNotFoundException($"SherpaOnnx model path not found: {modelPath}");
            }

            var config = new OnlineRecognizerConfig
            {
                FeatConfig = new FeatureConfig
                {
                    SampleRate = SampleRate,
                    FeatureDim = 80
                },
                ModelConfig = new OnlineModelConfig
                {
                    Transducer = new OnlineTransducerModelConfig
                    {
                        Encoder = Path.Combine(modelPath, "encoder.onnx"),
                        Decoder = Path.Combine(modelPath, "decoder.onnx"),
                        Joiner = Path.Combine(modelPath, "joiner.onnx")
                    },
                    Tokens = Path.Combine(modelPath, "tokens.txt"),
                    Provider = "cpu",
                    NumThreads = GetRecommendedThreadCount(),
                    Debug = 0
                },
                CtcFstDecoderConfig = new OnlineCtcFstDecoderConfig(),
                DecodingMethod = "greedy_search",
                MaxActivePaths = 4,
                // When VAD is disabled we use streaming endpoint detection.
                EnableEndpoint = useVad ? 0 : 1,
                Rule1MinTrailingSilence = 2.4f,
                Rule2MinTrailingSilence = _endpointSilenceSeconds,
                Rule3MinUtteranceLength = 20.0f,
                HotwordsFile = "",
                HotwordsScore = 1.5f
            };

            try
            {
                _recognizer = new OnlineRecognizer(config);
                _isRunning = false;
                _logger.Information("SherpaAsrService initialized.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SherpaOnnx initialization failed.");
                throw;
            }
        }

        public void Start()
        {
            lock (_streamLock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SherpaAsrService));
                }

                if (_isRunning)
                {
                    _logger.Warning("SherpaOnnx recognition service already running.");
                    return;
                }

                _isRunning = true;
                _segmentIndex = 0;
                ResetStreamingState();
                _currentSegmentInfo = RecognitionSegmentInfo.Empty;
                _logger.Information("SherpaOnnx recognition service started.");
            }
        }

        public void Stop()
        {
            lock (_streamLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_isRunning)
                {
                    _logger.Warning("SherpaOnnx recognition service not running.");
                    return;
                }

                _isRunning = false;
                _stream = null;
                _currentSegmentInfo = RecognitionSegmentInfo.Empty;
                ResetStreamingState();
                _logger.Information("SherpaOnnx recognition service stopped.");
            }
        }

        public void ProcessAudio(float[] audioData, RecognitionSegmentInfo? segmentInfo = null)
        {
            if (_disposed || !_isRunning)
            {
                return;
            }

            if (audioData == null || audioData.Length == 0)
            {
                _logger.Warning("Received empty audio data.");
                return;
            }

            if (segmentInfo != null)
            {
                _currentSegmentInfo = segmentInfo;
            }

            if (_useVad)
            {
                ProcessVadSegment(audioData, NormalizeSegmentInfo(_currentSegmentInfo));
                return;
            }

            ProcessStreamingAudio(audioData);
        }

        private void ProcessVadSegment(float[] audioData, RecognitionSegmentInfo segmentInfo)
        {
            lock (_streamLock)
            {
                if (_disposed || !_isRunning)
                {
                    return;
                }

                try
                {
                    var segmentIndex = Volatile.Read(ref _segmentIndex);
                    _logger.Debug(
                        "Processing VAD segment #{Segment} - samples: {SampleCount}, duration: {Duration:F2}s",
                        segmentIndex, audioData.Length, audioData.Length / (float)SampleRate);

                    using var stream = _recognizer.CreateStream();

                    stream.AcceptWaveform(SampleRate, LeftPadding);
                    stream.AcceptWaveform(SampleRate, audioData);
                    stream.AcceptWaveform(SampleRate, TailPadding);
                    stream.InputFinished();

                    while (_recognizer.IsReady(stream))
                    {
                        _recognizer.Decode(stream);
                    }

                    var result = _recognizer.GetResult(stream);
                    var text = result.Text?.Trim() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _logger.Information("Final result #{Segment}: '{Text}'", segmentIndex, text);
                        OnTextRecognized(new TranscriptionResult
                        {
                            Text = text,
                            Original = text,
                            IsFinal = true,
                            Confidence = 1.0f,
                            Source = _source,
                            SegmentId = segmentInfo.SegmentId,
                            CapturedAtUtc = segmentInfo.CapturedAtUtc
                        });
                    }
                    else
                    {
                        _logger.Debug("VAD segment #{Segment} produced no text.");
                    }

                    Interlocked.Increment(ref _segmentIndex);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "SherpaOnnx VAD segment processing failed.");
                }
            }
        }

        private void ProcessStreamingAudio(float[] audioData)
        {
            try
            {
                lock (_streamLock)
                {
                    if (_disposed || !_isRunning)
                    {
                        return;
                    }

                    EnsureStream();

                    _stream!.AcceptWaveform(SampleRate, audioData);

                    while (_recognizer.IsReady(_stream))
                    {
                        _recognizer.Decode(_stream);
                    }

                    var result = _recognizer.GetResult(_stream);
                    var text = result.Text?.Trim() ?? string.Empty;

                    var segmentInfo = NormalizeSegmentInfo(_currentSegmentInfo);
                    EmitPartialIfChanged(text, segmentInfo);
                    UpdateSilence(audioData);

                    if (ShouldFinalize(text))
                    {
                        EmitFinal(text, segmentInfo);
                        ResetStream();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SherpaOnnx streaming audio processing failed.");
            }
        }

        public void FinishSegment()
        {
            if (_useVad)
            {
                return;
            }

            lock (_streamLock)
            {
                if (_disposed || !_isRunning || _stream == null)
                {
                    return;
                }

                var result = _recognizer.GetResult(_stream);
                var text = result.Text?.Trim() ?? string.Empty;
                EmitFinal(text, NormalizeSegmentInfo(_currentSegmentInfo));
                ResetStream();
            }
        }

        private void EmitPartialIfChanged(string text, RecognitionSegmentInfo segmentInfo)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (string.Equals(text, _lastPartialText, StringComparison.Ordinal))
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastPartialEmitUtc < PartialEmitMinInterval &&
                text.StartsWith(_lastPartialText, StringComparison.Ordinal))
            {
                return;
            }

            _lastPartialText = text;
            _lastPartialEmitUtc = now;

            OnTextRecognized(new TranscriptionResult
            {
                Text = text,
                Original = text,
                IsFinal = false,
                Confidence = 1.0f,
                Source = _source,
                SegmentId = segmentInfo.SegmentId,
                CapturedAtUtc = segmentInfo.CapturedAtUtc
            });
        }

        private void EmitFinal(string text, RecognitionSegmentInfo segmentInfo)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (string.Equals(text, _lastFinalText, StringComparison.Ordinal))
            {
                return;
            }

            _lastFinalText = text;
            _logger.Information("Final result: '{Text}'", text);

            OnTextRecognized(new TranscriptionResult
            {
                Text = text,
                Original = text,
                IsFinal = true,
                Confidence = 1.0f,
                Source = _source,
                SegmentId = segmentInfo.SegmentId,
                CapturedAtUtc = segmentInfo.CapturedAtUtc
            });
        }

        private void UpdateSilence(float[] audioData)
        {
            var avgAbs = ComputeAverageAbs(audioData);
            var chunkSeconds = audioData.Length / (float)SampleRate;

            if (avgAbs < SilenceThreshold)
            {
                _silenceSeconds += chunkSeconds;
            }
            else
            {
                _silenceSeconds = 0f;
            }
        }

        private bool ShouldFinalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (IsWithinStreamingStartFinalizeGuard())
            {
                return false;
            }

            if (TryIsEndpoint(out var isEndpoint) && isEndpoint)
            {
                return true;
            }

            return _silenceSeconds >= _endpointSilenceSeconds;
        }

        private void EnsureStream()
        {
            if (_stream != null)
            {
                return;
            }

            _stream = _recognizer.CreateStream();
            ResetStreamingState();
            InitializeStreamingSegment();
        }

        private void ResetStream()
        {
            if (_stream == null)
            {
                ResetStreamingState();
                return;
            }

            if (!TryResetStream(_stream))
            {
                _stream = _recognizer.CreateStream();
            }

            ResetStreamingState();
            InitializeStreamingSegment();
        }

        private bool TryResetStream(OnlineStream stream)
        {
            var method = _recognizer.GetType().GetMethod("Reset");
            if (method == null)
            {
                return false;
            }

            try
            {
                method.Invoke(_recognizer, new object[] { stream });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void InitializeStreamingSegment()
        {
            if (_stream == null)
            {
                return;
            }

            _stream.AcceptWaveform(SampleRate, LeftPadding);
            Interlocked.Exchange(ref _streamStartedUtcTicks, DateTime.UtcNow.Ticks);
        }

        private bool IsWithinStreamingStartFinalizeGuard()
        {
            var startedTicks = Interlocked.Read(ref _streamStartedUtcTicks);
            if (startedTicks <= DateTime.MinValue.Ticks)
            {
                return false;
            }

            var startedAtUtc = new DateTime(startedTicks, DateTimeKind.Utc);
            return DateTime.UtcNow - startedAtUtc < StreamingSegmentStartFinalizeGuard;
        }

        private bool TryIsEndpoint(out bool isEndpoint)
        {
            isEndpoint = false;
            if (_stream == null)
            {
                return false;
            }

            var method = _recognizer.GetType().GetMethod("IsEndpoint");
            if (method == null)
            {
                return false;
            }

            try
            {
                isEndpoint = (bool)method.Invoke(_recognizer, new object[] { _stream })!;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ResetStreamingState()
        {
            _silenceSeconds = 0f;
            _lastPartialText = string.Empty;
            _lastFinalText = string.Empty;
            _lastPartialEmitUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _streamStartedUtcTicks, DateTime.MinValue.Ticks);
            _currentSegmentInfo = RecognitionSegmentInfo.Empty;
        }

        private static RecognitionSegmentInfo NormalizeSegmentInfo(RecognitionSegmentInfo segmentInfo)
        {
            if (segmentInfo.SegmentId > 0 && segmentInfo.CapturedAtUtc != DateTime.MinValue)
            {
                return segmentInfo;
            }

            return new RecognitionSegmentInfo
            {
                SegmentId = segmentInfo.SegmentId > 0 ? segmentInfo.SegmentId : 0,
                CapturedAtUtc = segmentInfo.CapturedAtUtc == DateTime.MinValue ? DateTime.UtcNow : segmentInfo.CapturedAtUtc
            };
        }

        private void OnTextRecognized(TranscriptionResult result)
        {
            TextRecognized?.Invoke(this, result);
        }

        private static float ComputeAverageAbs(float[] data)
        {
            if (data.Length == 0)
            {
                return 0f;
            }

            float sum = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                sum += Math.Abs(data[i]);
            }

            return sum / data.Length;
        }

        private static int GetRecommendedThreadCount()
        {
            var cpu = Environment.ProcessorCount;
            var recommended = Math.Max(1, cpu / 2);
            return Math.Min(MaxCpuThreads, recommended);
        }

        public bool IsRunning => _isRunning;

        public void Dispose()
        {
            _logger.Debug("Disposing SherpaAsrService.");
            try
            {
                lock (_streamLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _isRunning = false;
                    _stream = null;
                    ResetStreamingState();
                }

                _recognizer?.Dispose();
                _logger.Information("SherpaAsrService disposed.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose SherpaAsrService.");
            }
        }
    }
}
