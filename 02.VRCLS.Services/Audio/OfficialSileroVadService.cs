using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using VRCLS.Core.Interfaces;

namespace VRCLS.Services.Audio
{
    /// <summary>
    /// VAD implementation backed by the official Silero ONNX model.
    /// It keeps the existing IVadService contract so the rest of the pipeline
    /// can switch away from the Sherpa wrapper without changing UI/event flow.
    /// </summary>
    public sealed class OfficialSileroVadService : IVadService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<OfficialSileroVadService>();

        private const int SampleRate = 16000;
        private const float ThresholdGap = 0.15f;
        private const float MinSpeechDurationSeconds = 0.15f;
        private const int SpeechPadMilliseconds = 200;

        private readonly object _lock = new();
        private readonly OfficialSileroOnnxModel _model;
        private readonly List<float> _pendingSamples = new();
        private readonly Queue<float[]> _preSpeechFrames = new();
        private readonly List<float> _currentSegmentSamples = new();
        private readonly int _minSpeechSamples;
        private readonly int _minSilenceSamples;
        private readonly int _speechPadSamples;
        private readonly int _maxSegmentSamples;
        private readonly float _threshold;
        private readonly float _negativeThreshold;

        private bool _disposed;
        private bool _isSpeechDetected;
        private int _trailingSilenceSamples;
        private float _peakConfidence;

        public event EventHandler<VadSpeechEventArgs>? SpeechDetected;
        public event EventHandler<VadSpeechEventArgs>? SpeechEnded;
        public event EventHandler<VadConfidenceMeasuredEventArgs>? ConfidenceMeasured;
        public event EventHandler<VRCLS.Core.Models.ServiceErrorEvent>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public OfficialSileroVadService(
            string modelPath,
            float minSilenceDurationSeconds = 0.5f,
            float maxSegmentSeconds = 0f,
            float threshold = 0.5f)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("Model path is required.", nameof(modelPath));
            }

            minSilenceDurationSeconds = Math.Clamp(minSilenceDurationSeconds, 0.05f, 5f);
            maxSegmentSeconds = Math.Max(0f, maxSegmentSeconds);
            threshold = Math.Clamp(threshold, 0.01f, 0.99f);

            _threshold = threshold;
            _negativeThreshold = Math.Max(0.01f, threshold - ThresholdGap);
            _minSpeechSamples = (int)(SampleRate * MinSpeechDurationSeconds);
            _minSilenceSamples = (int)(SampleRate * minSilenceDurationSeconds);
            _speechPadSamples = (int)(SampleRate * (SpeechPadMilliseconds / 1000f));
            _maxSegmentSamples = maxSegmentSeconds <= 0 ? 0 : (int)(SampleRate * maxSegmentSeconds);
            _model = new OfficialSileroOnnxModel(modelPath);

            _logger.Information(
                "Official Silero VAD initialized. model={ModelPath}, minSilence={MinSilence:F2}s, maxSegment={MaxSegment}, threshold={Threshold:F2}",
                modelPath,
                minSilenceDurationSeconds,
                maxSegmentSeconds <= 0 ? "unlimited" : $"{maxSegmentSeconds:F0}s",
                threshold);
        }

        public void ProcessAudio(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                return;
            }

            List<float[]> segmentsToDispatch = new();
            bool speechStarted = false;
            VadConfidenceSnapshot? confidenceSnapshot = null;

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingSamples.AddRange(audioData);

                var windowCount = 0;
                var currentConfidence = 0f;
                var windowConfidences = new List<float>();

                while (_pendingSamples.Count >= _model.WindowShift)
                {
                    var shiftedWindow = _pendingSamples.GetRange(0, _model.WindowShift).ToArray();
                    _pendingSamples.RemoveRange(0, _model.WindowShift);

                    currentConfidence = _model.Run(shiftedWindow);
                    windowConfidences.Add(currentConfidence);
                    _peakConfidence = Math.Max(_peakConfidence, currentConfidence);
                    windowCount++;

                    if (!_isSpeechDetected)
                    {
                        AppendToPreSpeech(shiftedWindow);

                        if (currentConfidence >= _threshold)
                        {
                            _isSpeechDetected = true;
                            speechStarted = true;
                            _trailingSilenceSamples = 0;

                            while (_preSpeechFrames.Count > 0)
                            {
                                _currentSegmentSamples.AddRange(_preSpeechFrames.Dequeue());
                            }
                        }

                        continue;
                    }

                    _currentSegmentSamples.AddRange(shiftedWindow);

                    if (currentConfidence < _negativeThreshold)
                    {
                        _trailingSilenceSamples += shiftedWindow.Length;
                    }
                    else
                    {
                        _trailingSilenceSamples = 0;
                    }

                    if (_trailingSilenceSamples >= _minSilenceSamples)
                    {
                        FinalizeCurrentSegment(segmentsToDispatch);
                        continue;
                    }

                    if (_maxSegmentSamples > 0 && _currentSegmentSamples.Count >= _maxSegmentSamples + _speechPadSamples)
                    {
                        SplitLongSegment(segmentsToDispatch);
                    }
                }

                if (windowCount > 0)
                {
                    confidenceSnapshot = new VadConfidenceSnapshot(
                        currentConfidence,
                        _peakConfidence,
                        _threshold,
                        _isSpeechDetected,
                        windowCount,
                        windowConfidences.ToArray());
                }
            }

            if (speechStarted)
            {
                SpeechDetected?.Invoke(this, new VadSpeechEventArgs { IsSpeech = true });
            }

            foreach (var segment in segmentsToDispatch)
            {
                SpeechEnded?.Invoke(this, new VadSpeechEventArgs
                {
                    AudioData = segment,
                    IsSpeech = true
                });
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

            if (segmentsToDispatch.Count > 0)
            {
                _peakConfidence = 0f;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _model.ResetStates();
                _pendingSamples.Clear();
                _preSpeechFrames.Clear();
                _currentSegmentSamples.Clear();
                _isSpeechDetected = false;
                _trailingSilenceSamples = 0;
                _peakConfidence = 0f;
            }
        }

        private void AppendToPreSpeech(float[] window)
        {
            _preSpeechFrames.Enqueue(window);

            var totalSamples = 0;
            foreach (var frame in _preSpeechFrames)
            {
                totalSamples += frame.Length;
            }

            while (totalSamples > _speechPadSamples && _preSpeechFrames.Count > 0)
            {
                totalSamples -= _preSpeechFrames.Dequeue().Length;
            }
        }

        private void FinalizeCurrentSegment(List<float[]> dispatch)
        {
            var effectiveLength = _currentSegmentSamples.Count - Math.Max(0, _trailingSilenceSamples - _speechPadSamples);
            effectiveLength = Math.Max(0, effectiveLength);

            if (effectiveLength >= _minSpeechSamples)
            {
                dispatch.AddRange(BuildSegmentsToDispatch(_currentSegmentSamples, effectiveLength));
            }

            _currentSegmentSamples.Clear();
            _preSpeechFrames.Clear();
            _isSpeechDetected = false;
            _trailingSilenceSamples = 0;
        }

        private void SplitLongSegment(List<float[]> dispatch)
        {
            var keepTailSamples = Math.Min(_speechPadSamples, _currentSegmentSamples.Count);
            var emitLength = _currentSegmentSamples.Count - keepTailSamples;

            if (emitLength < _minSpeechSamples)
            {
                return;
            }

            dispatch.AddRange(BuildSegmentsToDispatch(_currentSegmentSamples, emitLength));

            var tail = _currentSegmentSamples.Skip(emitLength).ToArray();
            _currentSegmentSamples.Clear();
            _currentSegmentSamples.AddRange(tail);
            _trailingSilenceSamples = 0;
        }

        private List<float[]> BuildSegmentsToDispatch(List<float> source, int effectiveLength)
        {
            var segments = new List<float[]>();
            if (effectiveLength <= 0)
            {
                return segments;
            }

            if (_maxSegmentSamples <= 0 || effectiveLength <= _maxSegmentSamples)
            {
                segments.Add(source.Take(effectiveLength).ToArray());
                return segments;
            }

            var offset = 0;
            while (offset < effectiveLength)
            {
                var len = Math.Min(_maxSegmentSamples, effectiveLength - offset);
                segments.Add(source.Skip(offset).Take(len).ToArray());
                offset += len;
            }

            return segments;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _model.Dispose();
                _pendingSamples.Clear();
                _preSpeechFrames.Clear();
                _currentSegmentSamples.Clear();
            }
        }

        private sealed class OfficialSileroOnnxModel : IDisposable
        {
            private readonly InferenceSession _session;
            private readonly string _audioInputName;
            private readonly string? _sampleRateInputName;
            private readonly string? _stateInputName;
            private readonly string? _hiddenInputName;
            private readonly string? _cellInputName;
            private readonly string _probOutputName;
            private readonly string? _stateOutputName;
            private readonly string? _hiddenOutputName;
            private readonly string? _cellOutputName;
            private readonly float[]? _state;
            private readonly float[]? _hidden;
            private readonly float[]? _cell;
            private readonly float[] _overlapSamples;
            private bool _disposed;

            public int WindowShift { get; }

            public OfficialSileroOnnxModel(string modelPath)
            {
                var options = new SessionOptions
                {
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                    EnableCpuMemArena = true
                };

                _session = new InferenceSession(modelPath, options);

                var inputNames = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
                var outputNames = _session.OutputMetadata.Keys.ToHashSet(StringComparer.Ordinal);

                if (inputNames.Contains("input") && inputNames.Contains("state") && inputNames.Contains("sr")
                    && outputNames.Contains("output") && outputNames.Contains("stateN"))
                {
                    _audioInputName = "input";
                    _sampleRateInputName = "sr";
                    _stateInputName = "state";
                    _probOutputName = "output";
                    _stateOutputName = "stateN";
                    WindowShift = 512;
                    _overlapSamples = new float[64];
                    _state = new float[2 * 1 * 128];
                }
                else if (inputNames.Contains("input") && inputNames.Contains("sr") && inputNames.Contains("h") && inputNames.Contains("c")
                    && outputNames.Contains("output") && outputNames.Contains("hn") && outputNames.Contains("cn"))
                {
                    _audioInputName = "input";
                    _sampleRateInputName = "sr";
                    _hiddenInputName = "h";
                    _cellInputName = "c";
                    _probOutputName = "output";
                    _hiddenOutputName = "hn";
                    _cellOutputName = "cn";
                    WindowShift = 512;
                    _overlapSamples = Array.Empty<float>();
                    _hidden = new float[2 * 1 * 64];
                    _cell = new float[2 * 1 * 64];
                }
                else if (inputNames.Contains("x") && inputNames.Contains("h") && inputNames.Contains("c")
                    && outputNames.Contains("prob") && outputNames.Contains("new_h") && outputNames.Contains("new_c"))
                {
                    _audioInputName = "x";
                    _hiddenInputName = "h";
                    _cellInputName = "c";
                    _probOutputName = "prob";
                    _hiddenOutputName = "new_h";
                    _cellOutputName = "new_c";
                    WindowShift = 512;
                    _overlapSamples = Array.Empty<float>();
                    _hidden = new float[2 * 1 * 64];
                    _cell = new float[2 * 1 * 64];
                }
                else
                {
                    throw new NotSupportedException(
                        $"Unsupported Silero VAD ONNX signature. Inputs=[{string.Join(", ", inputNames)}], Outputs=[{string.Join(", ", outputNames)}]");
                }
            }

            public void ResetStates()
            {
                Array.Clear(_overlapSamples, 0, _overlapSamples.Length);

                if (_state != null)
                {
                    Array.Clear(_state, 0, _state.Length);
                }

                if (_hidden != null)
                {
                    Array.Clear(_hidden, 0, _hidden.Length);
                }

                if (_cell != null)
                {
                    Array.Clear(_cell, 0, _cell.Length);
                }
            }

            public float Run(float[] shiftedWindow)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(OfficialSileroOnnxModel));
                }

                if (shiftedWindow.Length != WindowShift)
                {
                    throw new ArgumentException($"Silero VAD expects {WindowShift} samples at {SampleRate} Hz.", nameof(shiftedWindow));
                }

                var input = new float[_overlapSamples.Length + shiftedWindow.Length];
                if (_overlapSamples.Length > 0)
                {
                    Array.Copy(_overlapSamples, 0, input, 0, _overlapSamples.Length);
                }

                Array.Copy(shiftedWindow, 0, input, _overlapSamples.Length, shiftedWindow.Length);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_audioInputName, new DenseTensor<float>(input, new[] { 1, input.Length }))
                };

                if (_sampleRateInputName != null)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(_sampleRateInputName, new DenseTensor<long>(new[] { (long)SampleRate }, new[] { 1 })));
                }

                if (_stateInputName != null && _state != null)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(_stateInputName, new DenseTensor<float>(_state, new[] { 2, 1, 128 })));
                }

                if (_hiddenInputName != null && _hidden != null)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(_hiddenInputName, new DenseTensor<float>(_hidden, new[] { 2, 1, 64 })));
                }

                if (_cellInputName != null && _cell != null)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(_cellInputName, new DenseTensor<float>(_cell, new[] { 2, 1, 64 })));
                }

                using var outputs = _session.Run(inputs);

                var probability = outputs.First(x => string.Equals(x.Name, _probOutputName, StringComparison.Ordinal))
                    .AsTensor<float>()
                    .ToArray()[0];

                if (_stateOutputName != null && _state != null)
                {
                    var newState = outputs.First(x => string.Equals(x.Name, _stateOutputName, StringComparison.Ordinal))
                        .AsTensor<float>()
                        .ToArray();
                    Array.Copy(newState, _state, Math.Min(newState.Length, _state.Length));
                }

                if (_hiddenOutputName != null && _hidden != null)
                {
                    var hidden = outputs.First(x => string.Equals(x.Name, _hiddenOutputName, StringComparison.Ordinal))
                        .AsTensor<float>()
                        .ToArray();
                    Array.Copy(hidden, _hidden, Math.Min(hidden.Length, _hidden.Length));
                }

                if (_cellOutputName != null && _cell != null)
                {
                    var cell = outputs.First(x => string.Equals(x.Name, _cellOutputName, StringComparison.Ordinal))
                        .AsTensor<float>()
                        .ToArray();
                    Array.Copy(cell, _cell, Math.Min(cell.Length, _cell.Length));
                }

                if (_overlapSamples.Length > 0)
                {
                    Array.Copy(
                        shiftedWindow,
                        shiftedWindow.Length - _overlapSamples.Length,
                        _overlapSamples,
                        0,
                        _overlapSamples.Length);
                }

                return probability;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _session.Dispose();
            }
        }

        private sealed record VadConfidenceSnapshot(
            float CurrentConfidence,
            float PeakConfidence,
            float Threshold,
            bool IsSpeechDetected,
            int WindowCount,
            float[] WindowConfidences);
    }
}
