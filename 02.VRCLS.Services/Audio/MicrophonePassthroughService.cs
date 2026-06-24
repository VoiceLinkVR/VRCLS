using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace VRCLS.Services.Audio
{
    /// <summary>
    /// Low-latency microphone monitor that forwards the selected mic to the same render
    /// endpoint used by TTS. Intended for virtual audio cable workflows.
    /// </summary>
    public sealed class MicrophonePassthroughService : IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<MicrophonePassthroughService>();
        private const int CaptureBufferMilliseconds = 30;
        private const int PlaybackDesiredLatencyMilliseconds = 90;
        private const int PlaybackBufferMilliseconds = 500;
        private static readonly TimeSpan PlaybackStartBufferDuration = TimeSpan.FromMilliseconds(90);
        private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

        private readonly object _lock = new();
        private WasapiCapture? _capture;
        private IWavePlayer? _waveOut;
        private MMDevice? _renderDevice;
        private BufferedWaveProvider? _bufferedProvider;
        private WaveFormat? _captureWaveFormat;
        private WaveFormat? _playbackWaveFormat;
        private string _micDeviceName = "default";
        private string _outputDeviceName = "default";
        private int _volumePermille = 1000;
        private int _playbackStarted;
        private bool _stopping;
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public string MicDeviceName => _micDeviceName;
        public string OutputDeviceName => _outputDeviceName;

        public void SetVolume(float volume)
        {
            var clamped = Math.Clamp(volume, 0f, 4f);
            Volatile.Write(ref _volumePermille, (int)Math.Round(clamped * 1000f));
        }

        public void Start(string micDeviceName, string outputDeviceName)
        {
            var normalizedMic = NormalizeDeviceName(micDeviceName);
            var normalizedOutput = NormalizeDeviceName(outputDeviceName);

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (IsRunning
                    && string.Equals(_micDeviceName, normalizedMic, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_outputDeviceName, normalizedOutput, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                StopUnsafe();

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
                    var selectedMic = ResolveCaptureDevice(captureDevices, normalizedMic);
                    var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
                    _renderDevice = ResolveRenderDevice(enumerator, renderDevices, normalizedOutput);

                    _capture = selectedMic != null
                        ? new WasapiCapture(selectedMic, true, CaptureBufferMilliseconds)
                        : new WasapiCapture();
                    _capture.ShareMode = AudioClientShareMode.Shared;

                    _captureWaveFormat = _capture.WaveFormat;
                    var playbackFormat = _renderDevice.AudioClient.MixFormat;
                    _playbackWaveFormat = playbackFormat;
                    _bufferedProvider = new BufferedWaveProvider(playbackFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(PlaybackBufferMilliseconds),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };

                    _waveOut = new WasapiOut(
                        _renderDevice,
                        AudioClientShareMode.Shared,
                        true,
                        PlaybackDesiredLatencyMilliseconds);
                    _waveOut.Init(_bufferedProvider);

                    _capture.DataAvailable += OnCaptureDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    _capture.StartRecording();

                    _micDeviceName = normalizedMic;
                    _outputDeviceName = normalizedOutput;
                    IsRunning = true;

                    _logger.Information(
                        "麦克风透传已启动 - 输入: {Input}, 输出: {Output}, 输入格式: {InputFormat}, 输出格式: {OutputFormat}",
                        selectedMic?.FriendlyName ?? "default",
                        _renderDevice.FriendlyName,
                        _capture.WaveFormat,
                        playbackFormat);
                }
                catch
                {
                    StopUnsafe();
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                StopUnsafe();
            }
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
                StopUnsafe();
            }
        }

        private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            try
            {
                var captureFormat = _captureWaveFormat;
                var playbackFormat = _playbackWaveFormat;
                var bufferedProvider = _bufferedProvider;
                if (captureFormat == null || playbackFormat == null || bufferedProvider == null)
                {
                    return;
                }

                var volume = Volatile.Read(ref _volumePermille) / 1000f;
                var playbackBuffer = ConvertCaptureBufferToPlaybackFormat(captureFormat, playbackFormat, e.Buffer, e.BytesRecorded, volume, out var playbackBytes);
                try
                {
                    if (playbackBytes > 0)
                    {
                        bufferedProvider.AddSamples(playbackBuffer, 0, playbackBytes);
                        TryStartPlayback(bufferedProvider);
                    }
                }
                finally
                {
                    if (playbackBuffer.Length > 0)
                    {
                        ArrayPool<byte>.Shared.Return(playbackBuffer);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "麦克风透传音频格式转换或写入播放缓冲失败");
            }
        }

        private void TryStartPlayback(BufferedWaveProvider bufferedProvider)
        {
            if (Volatile.Read(ref _playbackStarted) != 0
                || bufferedProvider.BufferedDuration < PlaybackStartBufferDuration)
            {
                return;
            }

            if (Interlocked.Exchange(ref _playbackStarted, 1) != 0)
            {
                return;
            }

            try
            {
                _waveOut?.Play();
            }
            catch
            {
                Interlocked.Exchange(ref _playbackStarted, 0);
                throw;
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_stopping)
            {
                return;
            }

            if (e.Exception != null)
            {
                _logger.Warning(e.Exception, "麦克风透传捕获意外停止");
            }
            else
            {
                _logger.Information("麦克风透传捕获已停止");
            }
        }

        private void StopUnsafe()
        {
            _stopping = true;
            try
            {
                if (_capture != null)
                {
                    try { _capture.StopRecording(); } catch { }
                    _capture.DataAvailable -= OnCaptureDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.Dispose();
                    _capture = null;
                }

                if (_waveOut != null)
                {
                    try { _waveOut.Stop(); } catch { }
                    _waveOut.Dispose();
                    _waveOut = null;
                }

                _renderDevice?.Dispose();
                _renderDevice = null;

                _bufferedProvider = null;
                _captureWaveFormat = null;
                _playbackWaveFormat = null;
                Interlocked.Exchange(ref _playbackStarted, 0);
                IsRunning = false;
            }
            finally
            {
                _stopping = false;
            }
        }

        private static string NormalizeDeviceName(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
        }

        private static byte[] ConvertCaptureBufferToPlaybackFormat(
            WaveFormat sourceFormat,
            WaveFormat targetFormat,
            byte[] bytes,
            int bytesRecorded,
            float volume,
            out int outputByteCount)
        {
            if (bytesRecorded <= 0)
            {
                outputByteCount = 0;
                return Array.Empty<byte>();
            }

            var sourceSampleRate = Math.Max(1, sourceFormat.SampleRate);
            var sourceChannels = Math.Max(1, sourceFormat.Channels);
            var targetSampleRate = Math.Max(1, targetFormat.SampleRate);
            var targetChannels = Math.Max(1, targetFormat.Channels);
            var bytesPerSample = sourceFormat.BitsPerSample switch
            {
                16 => 2,
                24 => 3,
                32 => 4,
                _ => throw new NotSupportedException(
                    $"Unsupported microphone passthrough format: {sourceFormat.Encoding}, {sourceFormat.BitsPerSample} bits.")
            };

            var sourceSampleCount = bytesRecorded / bytesPerSample;
            var sourceFrameCount = sourceSampleCount / sourceChannels;
            if (sourceFrameCount <= 0)
            {
                outputByteCount = 0;
                return Array.Empty<byte>();
            }

            var outputFrameCount = sourceSampleRate == targetSampleRate
                ? sourceFrameCount
                : Math.Max(1, (int)Math.Ceiling(sourceFrameCount * (double)targetSampleRate / sourceSampleRate));
            outputByteCount = outputFrameCount * targetFormat.BlockAlign;
            var output = ArrayPool<byte>.Shared.Rent(outputByteCount);
            var isFloat = IsFloatFormat(sourceFormat);

            for (var outputFrame = 0; outputFrame < outputFrameCount; outputFrame++)
            {
                var sourcePosition = sourceSampleRate == targetSampleRate
                    ? outputFrame
                    : outputFrame * (double)sourceSampleRate / targetSampleRate;
                var sourceFrame0 = Math.Min(sourceFrameCount - 1, (int)sourcePosition);
                var sourceFrame1 = Math.Min(sourceFrameCount - 1, sourceFrame0 + 1);
                var fraction = sourcePosition - sourceFrame0;

                GetStereoFrame(bytes, bytesRecorded, bytesPerSample, sourceChannels, sourceFrame0, isFloat, sourceFormat, out var left0, out var right0);
                GetStereoFrame(bytes, bytesRecorded, bytesPerSample, sourceChannels, sourceFrame1, isFloat, sourceFormat, out var left1, out var right1);

                var left = (left0 + (left1 - left0) * (float)fraction) * volume;
                var right = (right0 + (right1 - right0) * (float)fraction) * volume;
                WriteOutputFrame(output, outputFrame * targetFormat.BlockAlign, targetFormat, targetChannels, left, right);
            }

            return output;
        }

        private static void GetStereoFrame(
            byte[] bytes,
            int bytesRecorded,
            int bytesPerSample,
            int sourceChannels,
            int frameIndex,
            bool isFloat,
            WaveFormat sourceFormat,
            out float left,
            out float right)
        {
            if (sourceChannels == 1)
            {
                left = right = ReadFloatSample(bytes, bytesRecorded, bytesPerSample, frameIndex, isFloat, sourceFormat);
                return;
            }

            if (sourceChannels == 2)
            {
                var sampleIndex = frameIndex * sourceChannels;
                left = ReadFloatSample(bytes, bytesRecorded, bytesPerSample, sampleIndex, isFloat, sourceFormat);
                right = ReadFloatSample(bytes, bytesRecorded, bytesPerSample, sampleIndex + 1, isFloat, sourceFormat);
                return;
            }

            var sum = 0f;
            var baseSampleIndex = frameIndex * sourceChannels;
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                sum += ReadFloatSample(bytes, bytesRecorded, bytesPerSample, baseSampleIndex + channel, isFloat, sourceFormat);
            }

            left = right = sum / sourceChannels;
        }

        private static float ReadFloatSample(
            byte[] bytes,
            int bytesRecorded,
            int bytesPerSample,
            int sampleIndex,
            bool isFloat,
            WaveFormat sourceFormat)
        {
            var byteOffset = sampleIndex * bytesPerSample;
            if (byteOffset < 0 || byteOffset + bytesPerSample > bytesRecorded)
            {
                return 0f;
            }

            if (isFloat)
            {
                return BitConverter.ToSingle(bytes, byteOffset);
            }

            return sourceFormat.BitsPerSample switch
            {
                16 => BitConverter.ToInt16(bytes, byteOffset) / 32768f,
                24 => ReadInt24(bytes, byteOffset) / 8388608f,
                32 => BitConverter.ToInt32(bytes, byteOffset) / 2147483648f,
                _ => 0f
            };
        }

        private static int ReadInt24(byte[] bytes, int offset)
        {
            var sample = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
            if ((sample & 0x800000) != 0)
            {
                sample |= unchecked((int)0xFF000000);
            }

            return sample;
        }

        private static void WriteOutputFrame(byte[] bytes, int frameOffset, WaveFormat targetFormat, int targetChannels, float left, float right)
        {
            var bytesPerSample = Math.Max(1, targetFormat.BitsPerSample / 8);
            for (var channel = 0; channel < targetChannels; channel++)
            {
                var value = targetChannels == 1
                    ? (left + right) * 0.5f
                    : channel == 0
                        ? left
                        : channel == 1
                            ? right
                            : 0f;

                WriteSample(bytes, frameOffset + channel * bytesPerSample, value, targetFormat);
            }
        }

        private static void WriteSample(byte[] bytes, int offset, float value, WaveFormat targetFormat)
        {
            var clamped = Math.Clamp(value, -1f, 1f);
            if (IsFloatFormat(targetFormat))
            {
                var floatBytes = BitConverter.GetBytes(clamped);
                bytes[offset] = floatBytes[0];
                bytes[offset + 1] = floatBytes[1];
                bytes[offset + 2] = floatBytes[2];
                bytes[offset + 3] = floatBytes[3];
                return;
            }

            switch (targetFormat.BitsPerSample)
            {
                case 16:
                {
                    var sample = (short)Math.Round(clamped * short.MaxValue);
                    bytes[offset] = (byte)(sample & 0xFF);
                    bytes[offset + 1] = (byte)((sample >> 8) & 0xFF);
                    break;
                }
                case 24:
                {
                    var sample = (int)Math.Round(clamped * 8388607f);
                    bytes[offset] = (byte)(sample & 0xFF);
                    bytes[offset + 1] = (byte)((sample >> 8) & 0xFF);
                    bytes[offset + 2] = (byte)((sample >> 16) & 0xFF);
                    break;
                }
                case 32:
                {
                    var sample = (int)Math.Round(clamped * int.MaxValue);
                    bytes[offset] = (byte)(sample & 0xFF);
                    bytes[offset + 1] = (byte)((sample >> 8) & 0xFF);
                    bytes[offset + 2] = (byte)((sample >> 16) & 0xFF);
                    bytes[offset + 3] = (byte)((sample >> 24) & 0xFF);
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"Unsupported microphone passthrough output format: {targetFormat.Encoding}, {targetFormat.BitsPerSample} bits.");
            }
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

        private static MMDevice? ResolveCaptureDevice(MMDevice[] devices, string deviceName)
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
                _logger.Warning("麦克风透传输入设备名称未精确匹配，使用近似匹配: {Requested} -> {Resolved}", deviceName, partial.FriendlyName);
                return partial;
            }

            _logger.Warning("麦克风透传未找到输入设备: {DeviceName}，回退默认输入设备", deviceName);
            return null;
        }

        private static MMDevice ResolveRenderDevice(MMDeviceEnumerator enumerator, MMDevice[] devices, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.Equals(deviceName, "default", StringComparison.OrdinalIgnoreCase))
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
                _logger.Warning("麦克风透传输出设备名称未精确匹配，使用近似匹配: {Requested} -> {Resolved}", deviceName, partial.FriendlyName);
                return partial;
            }

            _logger.Warning("麦克风透传未找到输出设备: {DeviceName}，回退默认输出设备", deviceName);
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
    }
}
