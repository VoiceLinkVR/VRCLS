using System;
using System.IO;
using Concentus.Enums;
using Concentus.Structs;
using Serilog;

namespace VRCLS.Services.Audio
{
    public class OpusCodecService
    {
        private static readonly ILogger _logger = Log.ForContext<OpusCodecService>();
        private readonly OpusEncoder _encoder;
        private readonly int _frameSize;
        private readonly int _sampleRate = 16000;

        public OpusCodecService(int bitrate = 32000)
        {
            _logger.Information(
                "Initializing OpusCodecService - sampleRate: {SampleRate}Hz, bitrate: {Bitrate}, channels: 1",
                _sampleRate,
                bitrate);

            try
            {
                _encoder = OpusEncoder.Create(_sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                _encoder.Bitrate = bitrate;

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

            var shorts = new short[audioData.Length];
            for (int i = 0; i < audioData.Length; i++)
            {
                var sample = Math.Clamp(audioData[i], -1.0f, 1.0f);
                shorts[i] = (short)(sample * 32767);
            }

            var buffer = new byte[1024];
            var paddedFrame = new short[_frameSize];
            var ptr = 0;
            var frameCount = 0;

            while (ptr < shorts.Length)
            {
                frameCount++;

                var remainingSamples = shorts.Length - ptr;
                var samplesThisFrame = Math.Min(_frameSize, remainingSamples);
                short[] framePcm;
                var frameOffset = ptr;

                if (samplesThisFrame == _frameSize)
                {
                    framePcm = shorts;
                }
                else
                {
                    Array.Clear(paddedFrame, 0, paddedFrame.Length);
                    Array.Copy(shorts, ptr, paddedFrame, 0, samplesThisFrame);
                    framePcm = paddedFrame;
                    frameOffset = 0;
                }

                var len = _encoder.Encode(framePcm, frameOffset, _frameSize, buffer, 0, buffer.Length);
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
    }
}
