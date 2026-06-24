using System;
using System.Collections.Generic;
using SherpaOnnx;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Audio
{
    public class VadService : IVadService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<VadService>();
        private const float MinSpeechDurationSeconds = 0.15f;
        private const int MaxConsecutiveProcessingFailures = 3;

        private readonly VoiceActivityDetector _vad;
        private SileroVadConfidenceMeter? _confidenceMeter;
        private readonly object _vadLock = new();
        private readonly int _maxSegmentSamples;
        private readonly string _modelPath;
        private readonly float _threshold;

        private bool _isDetectingSpeech;
        private bool _confidenceMeasurementUnavailable;
        private bool _disposed;
        private int _consecutiveProcessingFailures;

        public event EventHandler<VadSpeechEventArgs>? SpeechDetected;
        public event EventHandler<VadSpeechEventArgs>? SpeechEnded;
        public event EventHandler<VadConfidenceMeasuredEventArgs>? ConfidenceMeasured;
        public event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        public VadService(
            string modelPath,
            float minSilenceDurationSeconds = 0.5f,
            float maxSegmentSeconds = 0f,
            float threshold = 0.5f)
        {
            minSilenceDurationSeconds = Math.Clamp(minSilenceDurationSeconds, 0.05f, 5f);
            maxSegmentSeconds = Math.Max(0f, maxSegmentSeconds);
            threshold = Math.Clamp(threshold, 0.01f, 0.99f);
            _modelPath = modelPath;
            _maxSegmentSamples = maxSegmentSeconds <= 0 ? 0 : (int)(maxSegmentSeconds * 16000);
            _threshold = threshold;

            var config = new VadModelConfig
            {
                SileroVad = new SileroVadModelConfig
                {
                    Model = modelPath,
                    Threshold = threshold,
                    MinSilenceDuration = minSilenceDurationSeconds,
                    MinSpeechDuration = MinSpeechDurationSeconds,
                    WindowSize = 512
                },
                SampleRate = 16000,
                Debug = 0,
                NumThreads = 1
            };

            _vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 60);

            _logger.Information(
                "VAD initialized. model={ModelPath}, minSilence={MinSilence:F2}s, maxSegment={MaxSegment}",
                modelPath,
                minSilenceDurationSeconds,
                maxSegmentSeconds <= 0 ? "unlimited" : $"{maxSegmentSeconds:F0}s");
        }

        public void ProcessAudio(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                return;
            }

            List<float[]> segmentsToDispatch = new();
            var speechDetected = false;
            var segmentCount = 0;
            VadConfidenceSnapshot? confidenceSnapshot = null;

            try
            {
                lock (_vadLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _vad.AcceptWaveform(audioData);

                    if (ConfidenceMeasured != null)
                    {
                        try
                        {
                            if (!_confidenceMeasurementUnavailable)
                            {
                                _confidenceMeter ??= new SileroVadConfidenceMeter(_modelPath);
                                confidenceSnapshot = _confidenceMeter.Measure(audioData, _threshold, _vad.IsSpeechDetected());
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "VAD confidence measurement unavailable.");
                            _confidenceMeter?.Dispose();
                            _confidenceMeter = null;
                            _confidenceMeasurementUnavailable = true;
                        }
                    }

                    while (!_vad.IsEmpty())
                    {
                        var segment = _vad.Front();
                        _vad.Pop();

                        if (segment.Samples.Length <= 0)
                        {
                            continue;
                        }

                        segmentCount++;
                        segmentsToDispatch.AddRange(BuildSegmentsToDispatch(segment.Samples));

                        _isDetectingSpeech = false;
                    }

                    if (_vad.IsSpeechDetected() && !_isDetectingSpeech)
                    {
                        _isDetectingSpeech = true;
                        speechDetected = true;
                    }
                }

                foreach (var segment in segmentsToDispatch)
                {
                    SpeechEnded?.Invoke(this, new VadSpeechEventArgs
                    {
                        AudioData = segment,
                        IsSpeech = true
                    });
                }

                if (speechDetected)
                {
                    SpeechDetected?.Invoke(this, new VadSpeechEventArgs { IsSpeech = true });
                }

                if (confidenceSnapshot != null)
                {
                    ConfidenceMeasured?.Invoke(this, new VadConfidenceMeasuredEventArgs
                    {
                        CurrentConfidence = confidenceSnapshot.CurrentConfidence,
                        PeakConfidence = confidenceSnapshot.PeakConfidence,
                        Threshold = confidenceSnapshot.Threshold,
                        IsSpeechDetected = confidenceSnapshot.IsSpeechDetected,
                        WindowCount = confidenceSnapshot.WindowCount,
                        WindowConfidences = confidenceSnapshot.WindowConfidences
                    });
                }

                if (segmentCount > 0)
                {
                    _logger.Debug("VAD emitted {SegmentCount} segment(s).", segmentCount);
                    _confidenceMeter?.ResetPeak();
                }

                _consecutiveProcessingFailures = 0;
            }
            catch (Exception ex)
            {
                var failures = ++_consecutiveProcessingFailures;
                _logger.Error(ex, "VAD processing failed.");
                if (failures == MaxConsecutiveProcessingFailures)
                {
                    ErrorOccurred?.Invoke(this, new ServiceErrorEvent
                    {
                        Code = ServiceErrorCode.AudioProcessingRepeatedFailures,
                        Context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Stage"] = "vad_processing",
                            ["Reason"] = $"VAD 连续处理失败 {failures} 次。",
                            ["ConsecutiveFailures"] = failures.ToString(),
                            ["Threshold"] = MaxConsecutiveProcessingFailures.ToString()
                        },
                        Exception = ex
                    });
                }
            }
        }

        public void Reset()
        {
            lock (_vadLock)
            {
                if (_disposed)
                {
                    return;
                }

                _vad.Reset();
                _confidenceMeter?.Reset();
                _isDetectingSpeech = false;
            }
        }

        private List<float[]> BuildSegmentsToDispatch(float[] segmentSamples)
        {
            var segments = new List<float[]>();
            if (segmentSamples == null || segmentSamples.Length == 0)
            {
                return segments;
            }

            if (_maxSegmentSamples <= 0 || segmentSamples.Length <= _maxSegmentSamples)
            {
                segments.Add(ComposeDispatchSegment(segmentSamples, 0, segmentSamples.Length));
                return segments;
            }

            var offset = 0;
            while (offset < segmentSamples.Length)
            {
                var len = Math.Min(_maxSegmentSamples, segmentSamples.Length - offset);
                segments.Add(ComposeDispatchSegment(
                    segmentSamples,
                    offset,
                    len));

                if (offset + len >= segmentSamples.Length)
                {
                    break;
                }

                offset += len;
            }

            return segments;
        }

        private static float[] ComposeDispatchSegment(float[] segmentSamples, int offset, int length)
        {
            var output = new float[length];
            Array.Copy(segmentSamples, offset, output, 0, length);
            return output;
        }

        public void Dispose()
        {
            lock (_vadLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _confidenceMeter?.Dispose();
                _vad.Dispose();
            }
        }
    }
}
