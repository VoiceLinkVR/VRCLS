using System;

namespace VRCLS.Core.Models
{
    public sealed class AudioDataEventArgs : EventArgs
    {
        public AudioDataEventArgs(float[] audioData)
        {
            AudioData = audioData ?? Array.Empty<float>();
        }

        public float[] AudioData { get; }
    }

    public sealed class VolumeLevelChangedEventArgs : EventArgs
    {
        public VolumeLevelChangedEventArgs(float level)
        {
            Level = level;
        }

        public float Level { get; }
    }

    public sealed class SpeechStateChangedEventArgs : EventArgs
    {
        public SpeechStateChangedEventArgs(bool isSpeaking)
        {
            IsSpeaking = isSpeaking;
        }

        public bool IsSpeaking { get; }
    }

    public sealed class OscMessageReceivedEventArgs : EventArgs
    {
        public OscMessageReceivedEventArgs(string message)
        {
            Message = message ?? string.Empty;
        }

        public string Message { get; }
    }

    public enum HealthStatus
    {
        Healthy = 0,
        Degraded = 1,
        Unhealthy = 2
    }

    public sealed class HealthReport
    {
        public HealthStatus Status { get; init; } = HealthStatus.Healthy;
        public string ServiceName { get; init; } = string.Empty;
        public string? Message { get; init; }
    }
}
