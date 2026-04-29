using System;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Dsp;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.Audio
{
    public class AudioCaptureService : IAudioService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<AudioCaptureService>();
        private WasapiCapture? _microphoneCapture;
        private WasapiLoopbackCapture? _loopbackCapture;
        private ProcessLoopbackCapture? _processLoopbackCapture;
        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;
        private const int BufferMilliseconds = 50;
        private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
        // Keep microphone samples close to the old pipeline behavior by default.
        // The previous auto-gain stage could distort consonants before Opus encoding.
        private static readonly bool EnableMicrophoneNormalization = false;
        private AudioCaptureRequest _currentRequest = new();
        private readonly object _lockObj = new();

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
        public event EventHandler<VolumeLevelChangedEventArgs>? VolumeLevelChanged;

        public bool IsCapturing { get; private set; } = false;

        public void StartCapture(AudioCaptureRequest request)
        {
            request ??= new AudioCaptureRequest();
            _logger.Debug(
                "启动音频捕获 - 设备: {DeviceId}, 环路: {IsLoopback}, 桌面来源: {DesktopSourceMode}, 目标进程: {TargetProcessId}/{TargetProcessName}",
                request.DeviceId,
                request.IsLoopback,
                request.DesktopSourceMode,
                request.TargetProcessId,
                request.TargetProcessName);

            lock (_lockObj)
            {
                if (IsCapturing)
                {
                    _logger.Warning("音频捕获已在运行中，先停止再重启");
                    StopCapture();
                }

                _currentRequest = request;

                try
                {
                    if (request.IsLoopback)
                    {
                        if (request.IsApplicationLoopback)
                        {
                            _logger.Debug("启动应用监听音频捕获");
                            StartProcessLoopbackCapture(request);
                        }
                        else
                        {
                            _logger.Debug("启动系统监听音频捕获");
                            StartLoopbackCapture();
                        }
                    }
                    else
                    {
                        _logger.Debug("启动麦克风音频捕获");
                        StartMicrophoneCapture(request.DeviceId);
                    }

                    IsCapturing = true;
                    _logger.Information("音频捕获已启动 - 设备: {DeviceId}, 环路: {IsLoopback}", request.DeviceId, request.IsLoopback);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "启动音频捕获失败");
                    throw;
                }
            }
        }

        private void StartLoopbackCapture()
        {
            _logger.Debug("初始化 WasapiLoopbackCapture");

            try
            {
                if (!string.IsNullOrWhiteSpace(_currentRequest.DeviceId) && _currentRequest.DeviceId != "default")
                {
                    var enumerator = new MMDeviceEnumerator();
                    var selected = enumerator
                        .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .FirstOrDefault(d => string.Equals(d.FriendlyName, _currentRequest.DeviceId, StringComparison.OrdinalIgnoreCase));

                    if (selected != null)
                    {
                        _loopbackCapture = new WasapiLoopbackCapture(selected);
                        _logger.Information("使用指定 Loopback 设备: {Device}", selected.FriendlyName);
                    }
                    else
                    {
                        _logger.Warning("未找到指定监听设备: {Device}，回退默认设备", _currentRequest.DeviceId);
                        _loopbackCapture = new WasapiLoopbackCapture();
                    }
                }
                else
                {
                    _loopbackCapture = new WasapiLoopbackCapture();
                }

                _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
                _loopbackCapture.RecordingStopped += OnRecordingStopped;
                _loopbackCapture.StartRecording();

                _logger.Information("WasapiLoopbackCapture 启动成功 - 格式: {Format}",
                    _loopbackCapture.WaveFormat);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动 WasapiLoopbackCapture 失败");
                throw;
            }
        }

        private void StartProcessLoopbackCapture(AudioCaptureRequest request)
        {
            if (request.TargetProcessId == 0)
            {
                throw new InvalidOperationException("应用监听音频模式缺少目标进程。");
            }

            _processLoopbackCapture = new ProcessLoopbackCapture(request.TargetProcessId, request.TargetProcessName);
            _processLoopbackCapture.DataAvailable += OnProcessLoopbackDataAvailable;
            _processLoopbackCapture.RecordingStopped += OnRecordingStopped;
            _processLoopbackCapture.StartRecording();

            _logger.Information(
                "应用监听音频捕获启动成功 - 目标进程: {ProcessId}/{ProcessName}, 格式: {Format}",
                request.TargetProcessId,
                string.IsNullOrWhiteSpace(request.TargetProcessName) ? "(unknown)" : request.TargetProcessName,
                _processLoopbackCapture.WaveFormat);
        }

        private void StartMicrophoneCapture(string deviceId)
        {
            _logger.Debug("初始化 WasapiCapture 麦克风捕获");

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
                var selectedDevice = ResolveMicrophoneDevice(devices, deviceId);
                _microphoneCapture = selectedDevice != null
                    ? new WasapiCapture(selectedDevice)
                    : new WasapiCapture();

                _microphoneCapture.ShareMode = AudioClientShareMode.Shared;
                _microphoneCapture.DataAvailable += OnMicrophoneDataAvailable;
                _microphoneCapture.RecordingStopped += OnRecordingStopped;
                _microphoneCapture.StartRecording();

                _logger.Information(
                    "WasapiCapture 麦克风捕获启动成功 - 设备: {Device}, 格式: {Format}",
                    selectedDevice?.FriendlyName ?? "default",
                    _microphoneCapture.WaveFormat);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动 WasapiCapture 麦克风捕获失败");
                throw;
            }
        }

        private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0)
            {
                return;
            }

            if (_loopbackCapture?.WaveFormat == null)
            {
                return;
            }

            ProcessLoopbackData(_loopbackCapture.WaveFormat, e.Buffer, e.BytesRecorded);
        }

        private void OnProcessLoopbackDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0)
            {
                return;
            }

            if (_processLoopbackCapture?.WaveFormat == null)
            {
                return;
            }

            ProcessLoopbackData(_processLoopbackCapture.WaveFormat, e.Buffer, e.BytesRecorded);
        }

        private void ProcessLoopbackData(WaveFormat waveFormat, byte[] buffer, int bytesRecorded)
        {
            if (bytesRecorded == 0)
            {
                return;
            }

            ProcessCapturedData(waveFormat, buffer, bytesRecorded, enableNormalization: false, sourceName: "环路");
        }

        private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0)
            {
                return;
            }

            if (_microphoneCapture?.WaveFormat == null)
            {
                return;
            }

            ProcessCapturedData(_microphoneCapture.WaveFormat, e.Buffer, e.BytesRecorded, EnableMicrophoneNormalization, "麦克风");
        }

        private void ProcessCapturedData(WaveFormat waveFormat, byte[] buffer, int bytesRecorded, bool enableNormalization, string sourceName)
        {
            if (bytesRecorded == 0)
            {
                return;
            }

            float[]? converted = null;
            float[]? mono = null;
            float[]? resampled = null;

            try
            {
                converted = ConvertBytesToFloat(waveFormat, buffer, bytesRecorded, out var convertedLength);
                mono = ConvertToMono(converted, convertedLength, waveFormat.Channels, out var monoLength);
                resampled = ResampleAudio(
                    mono,
                    monoLength,
                    waveFormat.SampleRate,
                    SampleRate,
                    out var resampledLength);

                if (enableNormalization)
                {
                    ApplyMicrophoneNormalizationInPlace(resampled, resampledLength);
                }

                var volume = CalculateVolume(resampled, resampledLength);
                VolumeLevelChanged?.Invoke(this, new VolumeLevelChangedEventArgs(volume));

                var output = new float[resampledLength];
                Array.Copy(resampled, 0, output, 0, resampledLength);
                AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(output));

                _logger.Verbose(
                    "{Source}音频数据处理完成 - 输入字节: {BytesRecorded}, 输出样本: {Samples}, 音量: {Volume:F3}",
                    sourceName,
                    bytesRecorded,
                    resampledLength,
                    volume);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{Source}音频数据处理错误", sourceName);
            }
            finally
            {
                ReturnRentedBuffer(ref resampled, mono, converted);
                ReturnRentedBuffer(ref mono, converted);
                ReturnRentedBuffer(ref converted);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _logger.Debug("录音停止事件触发");
            IsCapturing = false;

            if (e.Exception != null)
            {
                _logger.Error(e.Exception, "录音停止，发生错误");
            }
            else
            {
                _logger.Information("录音正常停止");
            }
        }

        public void StopCapture()
        {
            var stopStopwatch = Stopwatch.StartNew();
            _logger.Debug("停止音频捕获");

            lock (_lockObj)
            {
                try
                {
                    if (_loopbackCapture != null)
                    {
                        _logger.Debug("停止并释放 WasapiLoopbackCapture");
                        _loopbackCapture.StopRecording();
                        _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
                        _loopbackCapture.RecordingStopped -= OnRecordingStopped;
                        _loopbackCapture.Dispose();
                        _loopbackCapture = null;
                    }

                    if (_processLoopbackCapture != null)
                    {
                        _logger.Debug("停止并释放 ProcessLoopbackCapture。ElapsedMilliseconds={ElapsedMilliseconds}", stopStopwatch.ElapsedMilliseconds);
                        _processLoopbackCapture.StopRecording();
                        _processLoopbackCapture.DataAvailable -= OnProcessLoopbackDataAvailable;
                        _processLoopbackCapture.RecordingStopped -= OnRecordingStopped;
                        _processLoopbackCapture.Dispose();
                        _processLoopbackCapture = null;
                        _logger.Debug("ProcessLoopbackCapture 已释放。ElapsedMilliseconds={ElapsedMilliseconds}", stopStopwatch.ElapsedMilliseconds);
                    }

                    if (_microphoneCapture != null)
                    {
                        _logger.Debug("停止并释放 WasapiCapture");
                        _microphoneCapture.StopRecording();
                        _microphoneCapture.DataAvailable -= OnMicrophoneDataAvailable;
                        _microphoneCapture.RecordingStopped -= OnRecordingStopped;
                        _microphoneCapture.Dispose();
                        _microphoneCapture = null;
                    }

                    IsCapturing = false;
                    _logger.Information("音频捕获已停止。ElapsedMilliseconds={ElapsedMilliseconds}", stopStopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "停止音频捕获时发生错误");
                    throw;
                }
            }
        }

        private float[] ConvertBytesToFloat(WaveFormat waveFormat, byte[] bytes, int bytesRecorded, out int outputLength)
        {
            if (IsFloatFormat(waveFormat))
            {
                outputLength = bytesRecorded / 4;
                var floatData = ArrayPool<float>.Shared.Rent(outputLength);
                Buffer.BlockCopy(bytes, 0, floatData, 0, bytesRecorded);
                return floatData;
            }

            outputLength = bytesRecorded / Math.Max(1, waveFormat.BlockAlign / Math.Max(1, waveFormat.Channels));
            var pcmData = ArrayPool<float>.Shared.Rent(outputLength);

            switch (waveFormat.BitsPerSample)
            {
                case 16:
                    for (int i = 0; i < outputLength; i++)
                    {
                        short sample = BitConverter.ToInt16(bytes, i * 2);
                        pcmData[i] = sample / 32768f;
                    }
                    break;
                case 24:
                    for (int i = 0; i < outputLength; i++)
                    {
                        int index = i * 3;
                        int sample = bytes[index] | (bytes[index + 1] << 8) | (bytes[index + 2] << 16);
                        if ((sample & 0x800000) != 0)
                        {
                            sample |= unchecked((int)0xFF000000);
                        }

                        pcmData[i] = sample / 8388608f;
                    }
                    break;
                case 32:
                    for (int i = 0; i < outputLength; i++)
                    {
                        int sample = BitConverter.ToInt32(bytes, i * 4);
                        pcmData[i] = sample / 2147483648f;
                    }
                    break;
                default:
                    ReturnRentedBuffer(ref pcmData);
                    throw new NotSupportedException($"Unsupported microphone bit depth: {waveFormat.BitsPerSample}, encoding: {waveFormat.Encoding}.");
            }

            return pcmData;
        }

        private float[] ResampleAudio(float[] data, int dataLength, int sourceRate, int targetRate, out int outputLength)
        {
            if (sourceRate == targetRate)
            {
                outputLength = dataLength;
                return data;
            }

            float ratio = (float)targetRate / sourceRate;
            outputLength = (int)(dataLength * ratio);
            var output = ArrayPool<float>.Shared.Rent(outputLength);

            for (int i = 0; i < outputLength; i++)
            {
                float sourceIndex = i / ratio;
                int index0 = (int)sourceIndex;
                int index1 = Math.Min(index0 + 1, dataLength - 1);
                float fraction = sourceIndex - index0;

                output[i] = data[index0] * (1 - fraction) + data[index1] * fraction;
            }

            return output;
        }

        private float[] ConvertToMono(float[] data, int dataLength, int channels, out int outputLength)
        {
            if (channels == 1)
            {
                outputLength = dataLength;
                return data;
            }

            outputLength = dataLength / channels;
            var mono = ArrayPool<float>.Shared.Rent(outputLength);

            for (int i = 0; i < outputLength; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += data[i * channels + ch];
                }
                mono[i] = sum / channels;
            }

            return mono;
        }

        private float CalculateVolume(float[] data, int dataLength)
        {
            if (dataLength == 0) return 0;

            float sum = 0;
            for (int i = 0; i < dataLength; i++)
            {
                sum += Math.Abs(data[i]);
            }

            return sum / dataLength;
        }

        private void ApplyMicrophoneNormalizationInPlace(float[] data, int dataLength)
        {
            if (dataLength <= 0)
            {
                return;
            }

            const float micNormalizationActivationPeak = 0.015f;
            const float micNormalizationActivationAverage = 0.003f;
            const float micNormalizationTargetPeak = 0.32f;
            const float micNormalizationMaxGain = 8.0f;
            const float micNormalizationClipLimit = 0.98f;

            float peak = 0f;
            float sum = 0f;
            for (int i = 0; i < dataLength; i++)
            {
                var sample = Math.Abs(data[i]);
                if (sample > peak)
                {
                    peak = sample;
                }

                sum += sample;
            }

            var average = sum / dataLength;
            if (peak < micNormalizationActivationPeak && average < micNormalizationActivationAverage)
            {
                return;
            }

            if (peak <= 0f)
            {
                return;
            }

            var gain = Math.Clamp(micNormalizationTargetPeak / peak, 1f, micNormalizationMaxGain);
            if (gain <= 1.01f)
            {
                return;
            }

            for (int i = 0; i < dataLength; i++)
            {
                var boosted = data[i] * gain;
                data[i] = Math.Clamp(boosted, -micNormalizationClipLimit, micNormalizationClipLimit);
            }
        }

        private static void ReturnRentedBuffer(ref float[]? buffer, params float[]?[] skipBuffers)
        {
            if (buffer == null)
            {
                return;
            }

            foreach (var skip in skipBuffers)
            {
                if (ReferenceEquals(buffer, skip))
                {
                    buffer = null;
                    return;
                }
            }

            ArrayPool<float>.Shared.Return(buffer);
            buffer = null;
        }

        private static bool IsFloatFormat(WaveFormat waveFormat)
        {
            if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return true;
            }

            if (waveFormat is WaveFormatExtensible extensible)
            {
                return extensible.SubFormat == IeeeFloatSubFormat;
            }

            return false;
        }

        private MMDevice? ResolveMicrophoneDevice(MMDevice[] devices, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.Equals(deviceName, "default", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var exact = devices.FirstOrDefault(device =>
                string.Equals(device.FriendlyName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            var partial = devices.FirstOrDefault(device =>
                device.FriendlyName.Contains(deviceName, StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains(device.FriendlyName, StringComparison.OrdinalIgnoreCase));
            if (partial != null)
            {
                _logger.Warning("麦克风名称未精确匹配，使用近似匹配设备: {Requested} -> {Resolved}", deviceName, partial.FriendlyName);
                return partial;
            }

            _logger.Warning("未找到指定麦克风设备: {DeviceName}，回退默认输入设备", deviceName);
            return null;
        }

        public void Dispose()
        {
            _logger.Debug("释放 AudioCaptureService 资源");
            StopCapture();
            _logger.Information("AudioCaptureService 资源已释放");
        }
    }
}

