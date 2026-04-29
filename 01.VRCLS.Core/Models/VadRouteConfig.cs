using System;

namespace VRCLS.Core.Models
{
    public sealed class VadRouteConfig
    {
        public string Source { get; init; } = string.Empty;

        public float MinSilenceDuration { get; init; }

        public float Threshold { get; init; }

        public float MaxSegmentSeconds { get; init; }

        public bool Enabled { get; init; } = true;

        public static VadRouteConfig Disabled(string source) => new()
        {
            Source = source ?? string.Empty,
            Enabled = false,
            MinSilenceDuration = 0.5f,
            Threshold = 0.5f,
            MaxSegmentSeconds = 0f
        };
    }
}
