using System;
using System.Collections.Generic;

namespace VRCLS.UI.Services
{
    public sealed class VadMetricsService
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, VadMetricSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mic"] = VadMetricSnapshot.CreateDefault("Mic"),
            ["Desktop"] = VadMetricSnapshot.CreateDefault("Desktop")
        };

        public event EventHandler<VadMetricSnapshot>? SnapshotUpdated;

        public VadMetricSnapshot GetSnapshot(string source)
        {
            lock (_lock)
            {
                return _snapshots.TryGetValue(source, out var snapshot)
                    ? snapshot
                    : VadMetricSnapshot.CreateDefault(source);
            }
        }

        public void SetAvailability(string source, bool enabled, float threshold)
        {
            Update(new VadMetricSnapshot(
                source,
                enabled,
                CurrentConfidence: 0f,
                PeakConfidence: 0f,
                Threshold: threshold,
                IsSpeechDetected: false,
                UpdatedAtUtc: DateTime.UtcNow,
                HasMeasurement: false));
        }

        public void Update(VadMetricSnapshot snapshot)
        {
            lock (_lock)
            {
                _snapshots[snapshot.Source] = snapshot;
            }

            SnapshotUpdated?.Invoke(this, snapshot);
        }
    }

    public sealed record VadMetricSnapshot(
        string Source,
        bool Enabled,
        float CurrentConfidence,
        float PeakConfidence,
        float Threshold,
        bool IsSpeechDetected,
        DateTime UpdatedAtUtc,
        bool HasMeasurement)
    {
        public static VadMetricSnapshot CreateDefault(string source) => new(
            source,
            Enabled: false,
            CurrentConfidence: 0f,
            PeakConfidence: 0f,
            Threshold: 0.5f,
            IsSpeechDetected: false,
            UpdatedAtUtc: DateTime.MinValue,
            HasMeasurement: false);
    }
}
