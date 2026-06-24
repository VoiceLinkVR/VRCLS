using System;
using System.Collections.Generic;

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
            Address = string.Empty;
            Values = Array.Empty<object?>();
            Text = string.Empty;
        }

        public OscMessageReceivedEventArgs(string address, IReadOnlyList<object?> values)
        {
            Address = address ?? string.Empty;
            Values = values ?? Array.Empty<object?>();
            Text = ResolveTextValue(Values);
            Message = BuildMessage(Address, Values);
        }

        public string Message { get; }
        public string Address { get; }
        public IReadOnlyList<object?> Values { get; }
        public string Text { get; }

        private static string ResolveTextValue(IReadOnlyList<object?> values)
        {
            foreach (var value in values)
            {
                if (value is string text)
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private static string BuildMessage(string address, IReadOnlyList<object?> values)
        {
            if (values.Count == 0)
            {
                return address;
            }

            var parts = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                parts[i] = values[i] switch
                {
                    null => "null",
                    byte[] bytes => $"blob[{bytes.Length}]",
                    string text => text,
                    _ => values[i]?.ToString() ?? string.Empty
                };
            }

            return $"{address} {string.Join(", ", parts)}";
        }
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
