using System;
using System.Collections.Generic;
using System.Linq;
using VRCLS.Core.Interfaces;

namespace VRCLS.UI.Services
{
    public sealed class VadCalibrationService
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _sessionCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, VadCalibrationSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mic"] = VadCalibrationSnapshot.CreateDefault("Mic"),
            ["Desktop"] = VadCalibrationSnapshot.CreateDefault("Desktop")
        };

        public event EventHandler<VadCalibrationMonitoringChangedEventArgs>? MonitoringChanged;
        public event EventHandler<VadCalibrationSnapshot>? SnapshotUpdated;

        public IDisposable BeginSession(string source)
        {
            var normalized = NormalizeSource(source);
            var notify = false;

            lock (_lock)
            {
                _sessionCounts.TryGetValue(normalized, out var count);
                count++;
                _sessionCounts[normalized] = count;
                notify = count == 1;
                if (!_snapshots.ContainsKey(normalized))
                {
                    _snapshots[normalized] = VadCalibrationSnapshot.CreateDefault(normalized);
                }
            }

            if (notify)
            {
                MonitoringChanged?.Invoke(this, new VadCalibrationMonitoringChangedEventArgs(normalized, true));
            }

            return new Session(this, normalized);
        }

        public bool IsMonitoring(string source)
        {
            var normalized = NormalizeSource(source);
            lock (_lock)
            {
                return _sessionCounts.TryGetValue(normalized, out var count) && count > 0;
            }
        }

        public VadCalibrationSnapshot GetSnapshot(string source)
        {
            var normalized = NormalizeSource(source);
            lock (_lock)
            {
                return _snapshots.TryGetValue(normalized, out var snapshot)
                    ? snapshot
                    : VadCalibrationSnapshot.CreateDefault(normalized);
            }
        }

        public void PublishAvailability(string source, bool enabled)
        {
            var normalized = NormalizeSource(source);
            VadCalibrationSnapshot snapshot;

            lock (_lock)
            {
                var current = _snapshots.TryGetValue(normalized, out var existing)
                    ? existing
                    : VadCalibrationSnapshot.CreateDefault(normalized);
                snapshot = current with
                {
                    Enabled = enabled,
                    UpdatedAtUtc = DateTime.UtcNow,
                    HasMeasurement = enabled && current.HasMeasurement
                };
                _snapshots[normalized] = snapshot;
            }

            SnapshotUpdated?.Invoke(this, snapshot);
        }

        public void PublishMeasurement(string source, VadConfidenceMeasuredEventArgs e)
        {
            var normalized = NormalizeSource(source);
            VadCalibrationSnapshot snapshot;

            lock (_lock)
            {
                snapshot = new VadCalibrationSnapshot(
                    normalized,
                    Enabled: true,
                    CurrentConfidence: e.CurrentConfidence,
                    PeakConfidence: e.PeakConfidence,
                    Threshold: e.Threshold,
                    IsSpeechDetected: e.IsSpeechDetected,
                    UpdatedAtUtc: DateTime.UtcNow,
                    HasMeasurement: true,
                    WindowCount: e.WindowCount,
                    WindowConfidences: e.WindowConfidences?.ToArray() ?? Array.Empty<float>());

                _snapshots[normalized] = snapshot;
            }

            SnapshotUpdated?.Invoke(this, snapshot);
        }

        private void EndSession(string source)
        {
            var normalized = NormalizeSource(source);
            var notify = false;

            lock (_lock)
            {
                if (!_sessionCounts.TryGetValue(normalized, out var count) || count <= 0)
                {
                    return;
                }

                count--;
                if (count == 0)
                {
                    _sessionCounts.Remove(normalized);
                    _snapshots[normalized] = VadCalibrationSnapshot.CreateDefault(normalized);
                    notify = true;
                }
                else
                {
                    _sessionCounts[normalized] = count;
                }
            }

            if (notify)
            {
                MonitoringChanged?.Invoke(this, new VadCalibrationMonitoringChangedEventArgs(normalized, false));
            }
        }

        private static string NormalizeSource(string source)
            => string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase) ? "Desktop" : "Mic";

        private sealed class Session : IDisposable
        {
            private readonly VadCalibrationService _owner;
            private readonly string _source;
            private bool _disposed;

            public Session(VadCalibrationService owner, string source)
            {
                _owner = owner;
                _source = source;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner.EndSession(_source);
            }
        }
    }

    public sealed class VadCalibrationMonitoringChangedEventArgs : EventArgs
    {
        public VadCalibrationMonitoringChangedEventArgs(string source, bool isActive)
        {
            Source = source;
            IsActive = isActive;
        }

        public string Source { get; }

        public bool IsActive { get; }
    }

    public sealed record VadCalibrationSnapshot(
        string Source,
        bool Enabled,
        float CurrentConfidence,
        float PeakConfidence,
        float Threshold,
        bool IsSpeechDetected,
        DateTime UpdatedAtUtc,
        bool HasMeasurement,
        int WindowCount,
        float[] WindowConfidences)
    {
        public static VadCalibrationSnapshot CreateDefault(string source) => new(
            source,
            Enabled: false,
            CurrentConfidence: 0f,
            PeakConfidence: 0f,
            Threshold: 0.5f,
            IsSpeechDetected: false,
            UpdatedAtUtc: DateTime.MinValue,
            HasMeasurement: false,
            WindowCount: 0,
            WindowConfidences: Array.Empty<float>());
    }
}
