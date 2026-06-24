using System;
using System.Runtime.InteropServices;

namespace VRCLS.Services.Audio
{
    internal enum NativeOpusApplication
    {
        Voip = 2048,
        Audio = 2049,
        RestrictedLowDelay = 2051
    }

    internal sealed class NativeOpusEncoder : IDisposable
    {
        private const int OpusOk = 0;
        private const int SetBitrateRequest = 4002;
        private const int SetVbrRequest = 4006;
        private const int SetComplexityRequest = 4010;
        private IntPtr _handle;

        public NativeOpusEncoder(
            int sampleRate,
            int channels,
            NativeOpusApplication application,
            int? bitrate = null,
            bool? useVbr = null,
            int? complexity = null)
        {
            _handle = NativeMethods.opus_encoder_create(sampleRate, channels, (int)application, out var error);
            ThrowIfError(error, "create Opus encoder");

            if (bitrate.HasValue)
            {
                SetControl(SetBitrateRequest, bitrate.Value);
            }

            if (useVbr.HasValue)
            {
                SetControl(SetVbrRequest, useVbr.Value ? 1 : 0);
            }

            if (complexity.HasValue)
            {
                SetControl(SetComplexityRequest, Math.Clamp(complexity.Value, 0, 10));
            }
        }

        public int Encode(float[] pcm, int frameSize, byte[] output)
        {
            ThrowIfDisposed();
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var encoded = NativeMethods.opus_encode_float(_handle, pcm, frameSize, output, output.Length);
            ThrowIfError(encoded, "encode Opus frame");
            return encoded;
        }

        private void SetControl(int request, int value)
        {
            ThrowIfDisposed();
            var error = NativeMethods.opus_encoder_ctl(_handle, request, value);
            ThrowIfError(error, $"set Opus encoder control {request}");
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.opus_encoder_destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(NativeOpusEncoder));
            }
        }

        private static void ThrowIfError(int code, string operation)
        {
            if (code >= OpusOk)
            {
                return;
            }

            throw new InvalidOperationException($"Failed to {operation}: {NativeMethods.GetErrorMessage(code)} ({code}).");
        }
    }

    internal sealed class NativeOpusDecoder : IDisposable
    {
        private const int OpusOk = 0;
        private IntPtr _handle;

        public NativeOpusDecoder(int sampleRate, int channels)
        {
            _handle = NativeMethods.opus_decoder_create(sampleRate, channels, out var error);
            ThrowIfError(error, "create Opus decoder");
        }

        public int Decode(byte[] packet, int packetLength, short[] pcm, int frameSize, bool decodeFec = false)
        {
            ThrowIfDisposed();
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));

            var decodedSamplesPerChannel = NativeMethods.opus_decode(
                _handle,
                packet,
                packetLength,
                pcm,
                frameSize,
                decodeFec ? 1 : 0);
            ThrowIfError(decodedSamplesPerChannel, "decode Opus packet");
            return decodedSamplesPerChannel;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.opus_decoder_destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(NativeOpusDecoder));
            }
        }

        private static void ThrowIfError(int code, string operation)
        {
            if (code >= OpusOk)
            {
                return;
            }

            throw new InvalidOperationException($"Failed to {operation}: {NativeMethods.GetErrorMessage(code)} ({code}).");
        }
    }

    internal static class NativeOpusConstants
    {
        public const int MaxPacketBytes = 1275;
    }

    internal static class NativeOpusPcm
    {
        public static void CopyClampedFrame(float[] source, int sourceOffset, int samplesToCopy, float[] destination)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            Array.Clear(destination, 0, destination.Length);
            for (var i = 0; i < samplesToCopy; i++)
            {
                destination[i] = Math.Clamp(source[sourceOffset + i], -1.0f, 1.0f);
            }
        }
    }

    internal static class NativeMethods
    {
        private const string OpusLibrary = "opus";

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr opus_encoder_create(int fs, int channels, int application, out int error);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_encoder_destroy(IntPtr encoder);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encode_float(
            IntPtr encoder,
            [In] float[] pcm,
            int frameSize,
            [Out] byte[] data,
            int maxDataBytes);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "opus_encoder_ctl")]
        public static extern int opus_encoder_ctl(IntPtr encoder, int request, int value);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr opus_decoder_create(int fs, int channels, out int error);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_decoder_destroy(IntPtr decoder);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decode(
            IntPtr decoder,
            [In] byte[] data,
            int len,
            [Out] short[] pcm,
            int frameSize,
            int decodeFec);

        [DllImport(OpusLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr opus_strerror(int error);

        public static string GetErrorMessage(int error)
        {
            var pointer = opus_strerror(error);
            return Marshal.PtrToStringAnsi(pointer) ?? "unknown Opus error";
        }
    }
}
