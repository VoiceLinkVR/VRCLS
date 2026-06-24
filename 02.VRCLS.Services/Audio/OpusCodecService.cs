using System;
using System.IO;
using Serilog;

namespace VRCLS.Services.Audio
{
    public class OpusCodecService : IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<OpusCodecService>();
        private readonly NativeOpusEncoder _encoder;
        private readonly int _frameSize;
        private readonly int _sampleRate = 16000;
        private bool _disposed;

        public OpusCodecService(int bitrate = 32000)
        {
            _logger.Information(
                "Initializing OpusCodecService - sampleRate: {SampleRate}Hz, bitrate: {Bitrate}, channels: 1",
                _sampleRate,
                bitrate);

            try
            {
                _encoder = new NativeOpusEncoder(
                    _sampleRate,
                    1,
                    NativeOpusApplication.Voip,
                    bitrate: bitrate);

                // The server expects 20 ms Opus packets at 16 kHz.
                _frameSize = 320;

                _logger.Debug("Opus encoder created - frameSize: {FrameSize} samples", _frameSize);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Opus encoder initialization failed");
                throw;
            }
        }

        public byte[] EncodeAndPackage(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                _logger.Warning("Attempted to encode empty audio data");
                return Array.Empty<byte>();
            }

            _logger.Verbose(
                "Start encoding audio - samples: {SampleCount}, duration: {Duration:F2}s",
                audioData.Length,
                audioData.Length / (float)_sampleRate);

            using var outputStream = new MemoryStream();
            using var writer = new BinaryWriter(outputStream);

            var buffer = new byte[NativeOpusConstants.MaxPacketBytes];
            var frameBuffer = new float[_frameSize];
            var ptr = 0;
            var frameCount = 0;

            while (ptr < audioData.Length)
            {
                frameCount++;

                var remainingSamples = audioData.Length - ptr;
                var samplesThisFrame = Math.Min(_frameSize, remainingSamples);
                NativeOpusPcm.CopyClampedFrame(audioData, ptr, samplesThisFrame, frameBuffer);
                var len = _encoder.Encode(frameBuffer, _frameSize, buffer);
                ptr += samplesThisFrame;

                if (len <= 0)
                {
                    _logger.Warning("Opus frame encoding failed - frame: {FrameNumber}", frameCount);
                    continue;
                }

                var lenBytes = BitConverter.GetBytes((uint)len);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(lenBytes);
                }

                writer.Write(lenBytes);
                writer.Write(buffer, 0, len);
            }

            var result = outputStream.ToArray();
            _logger.Information(
                "Opus encoding completed - original: {OriginalSize}, encoded: {EncodedSize}, frames: {FrameCount}, ratio: {Ratio:F1}%",
                audioData.Length * 4,
                result.Length,
                frameCount,
                (1 - (double)result.Length / (audioData.Length * 4)) * 100);

            return result;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _encoder.Dispose();
            _disposed = true;
        }
    }
}
