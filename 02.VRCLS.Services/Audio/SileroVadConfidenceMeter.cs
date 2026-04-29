using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VRCLS.Services.Audio
{
    internal sealed class SileroVadConfidenceMeter : IDisposable
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
        private readonly int _windowShift;
        private readonly int _windowOverlap;
        private readonly int _windowSize;
        private readonly List<float> _pendingSamples = new();

        private readonly float[]? _state;
        private readonly float[]? _hidden;
        private readonly float[]? _cell;
        private readonly float[] _overlapSamples;

        private bool _disposed;
        private float _peakConfidence;

        public SileroVadConfidenceMeter(string modelPath)
        {
            _session = new InferenceSession(modelPath);

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
                _windowShift = 512;
                _windowOverlap = 64;
                _windowSize = _windowShift + _windowOverlap;
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
                _windowShift = 512;
                _windowOverlap = 0;
                _windowSize = _windowShift;
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
                _windowShift = 512;
                _windowOverlap = 0;
                _windowSize = _windowShift;
                _hidden = new float[2 * 1 * 64];
                _cell = new float[2 * 1 * 64];
            }
            else
            {
                throw new NotSupportedException("Unsupported Silero VAD ONNX signature for confidence measurement.");
            }

            _overlapSamples = new float[_windowOverlap];
        }

        public VadConfidenceSnapshot? Measure(float[] audioData, float threshold, bool isSpeechDetected)
        {
            if (_disposed || audioData == null || audioData.Length == 0)
            {
                return null;
            }

            _pendingSamples.AddRange(audioData);

            var processedWindows = 0;
            var currentConfidence = 0f;
            var windowConfidences = new List<float>();

            while (_pendingSamples.Count >= _windowShift)
            {
                var shifted = _pendingSamples.GetRange(0, _windowShift).ToArray();
                _pendingSamples.RemoveRange(0, _windowShift);

                var window = new float[_windowSize];
                if (_windowOverlap > 0)
                {
                    Array.Copy(_overlapSamples, 0, window, 0, _windowOverlap);
                }

                Array.Copy(shifted, 0, window, _windowOverlap, _windowShift);
                currentConfidence = Run(window);
                windowConfidences.Add(currentConfidence);
                _peakConfidence = Math.Max(_peakConfidence, currentConfidence);
                processedWindows++;

                if (_windowOverlap > 0)
                {
                    Array.Copy(shifted, _windowShift - _windowOverlap, _overlapSamples, 0, _windowOverlap);
                }
            }

            if (processedWindows == 0)
            {
                return null;
            }

            return new VadConfidenceSnapshot(
                currentConfidence,
                _peakConfidence,
                threshold,
                isSpeechDetected,
                processedWindows,
                windowConfidences.ToArray());
        }

        public void ResetPeak() => _peakConfidence = 0f;

        public void Reset()
        {
            _pendingSamples.Clear();
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

            ResetPeak();
        }

        private float Run(float[] window)
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_audioInputName, new DenseTensor<float>(window, new[] { 1, window.Length }))
            };

            if (_sampleRateInputName != null)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(_sampleRateInputName, new DenseTensor<long>(new[] { 16000L }, new[] { 1 })));
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
            var probability = outputs.First(x => string.Equals(x.Name, _probOutputName, StringComparison.Ordinal)).AsTensor<float>().ToArray()[0];

            if (_stateOutputName != null && _state != null)
            {
                Array.Copy(outputs.First(x => string.Equals(x.Name, _stateOutputName, StringComparison.Ordinal)).AsTensor<float>().ToArray(), _state, _state.Length);
            }

            if (_hiddenOutputName != null && _hidden != null)
            {
                Array.Copy(outputs.First(x => string.Equals(x.Name, _hiddenOutputName, StringComparison.Ordinal)).AsTensor<float>().ToArray(), _hidden, _hidden.Length);
            }

            if (_cellOutputName != null && _cell != null)
            {
                Array.Copy(outputs.First(x => string.Equals(x.Name, _cellOutputName, StringComparison.Ordinal)).AsTensor<float>().ToArray(), _cell, _cell.Length);
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

    internal sealed record VadConfidenceSnapshot(
        float CurrentConfidence,
        float PeakConfidence,
        float Threshold,
        bool IsSpeechDetected,
        int WindowCount,
        float[] WindowConfidences);
}
