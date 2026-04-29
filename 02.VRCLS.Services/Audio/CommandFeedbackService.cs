using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using NAudio.Wave;
using Serilog;

namespace VRCLS.Services.Audio
{
    /// <summary>
    /// Service for playing command feedback sounds.
    /// Uses embedded WAV audio for command acknowledgment.
    /// </summary>
    public class CommandFeedbackService : IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<CommandFeedbackService>();
        private readonly object _lock = new();
        private bool _disposed;
        private bool _isPlaying;

        // Simple beep sound parameters
        private const int SampleRate = 16000;
        private const int DurationMs = 80;
        private const int Frequency = 1200; // Higher pitch for quick feedback

        private byte[]? _feedbackSound;

        public CommandFeedbackService()
        {
            _logger.Debug("CommandFeedbackService initialized");
            GenerateFeedbackSound();
        }

        /// <summary>
        /// Generate a simple sine wave beep sound.
        /// </summary>
        private void GenerateFeedbackSound()
        {
            try
            {
                var samples = SampleRate * DurationMs / 1000;
                var buffer = new short[samples];

                for (int i = 0; i < samples; i++)
                {
                    // Generate sine wave with fade in/out
                    double t = (double)i / SampleRate;
                    double envelope = 1.0;

                    // Fade in (first 5ms)
                    if (i < SampleRate * 0.005)
                        envelope = i / (SampleRate * 0.005);
                    // Fade out (last 15ms)
                    else if (i > samples - SampleRate * 0.015)
                        envelope = (samples - i) / (SampleRate * 0.015);

                    double sample = Math.Sin(2 * Math.PI * Frequency * t) * envelope;
                    buffer[i] = (short)(sample * 12000); // ~40% volume
                }

                // Convert to WAV format
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                // WAV header
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + buffer.Length * 2); // File size - 8
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16); // Subchunk1Size
                writer.Write((short)1); // AudioFormat (PCM)
                writer.Write((short)1); // NumChannels
                writer.Write(SampleRate); // SampleRate
                writer.Write(SampleRate * 2); // ByteRate
                writer.Write((short)2); // BlockAlign
                writer.Write((short)16); // BitsPerSample
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(buffer.Length * 2); // Subchunk2Size

                // Audio data
                foreach (var sample in buffer)
                {
                    writer.Write(sample);
                }

                _feedbackSound = ms.ToArray();
                _logger.Debug("Feedback sound generated: {Size} bytes", _feedbackSound.Length);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to generate feedback sound");
            }
        }

        /// <summary>
        /// Play the command feedback sound (fire and forget, non-blocking).
        /// Uses SoundPlayer which doesn't conflict with WaveOutEvent.
        /// </summary>
        public void PlayFeedback()
        {
            if (_disposed || _feedbackSound == null)
                return;

            lock (_lock)
            {
                if (_isPlaying)
                    return;
                _isPlaying = true;
            }

            Task.Run(() =>
            {
                try
                {
                    using var ms = new MemoryStream(_feedbackSound);
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                    _logger.Verbose("Feedback sound played");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to play feedback sound");
                }
                finally
                {
                    lock (_lock)
                    {
                        _isPlaying = false;
                    }
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _feedbackSound = null;
            _logger.Debug("CommandFeedbackService disposed");
        }
    }
}
