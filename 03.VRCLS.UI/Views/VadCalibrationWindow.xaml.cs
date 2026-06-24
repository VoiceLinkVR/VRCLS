using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using VRCLS.UI.Services;

namespace VRCLS.UI.Views
{
    public partial class VadCalibrationWindow : Window, INotifyPropertyChanged
    {
        private static readonly TimeSpan SilentPhaseDuration = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan SpeechPhaseDuration = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PausePhaseDuration = TimeSpan.FromSeconds(8);

        private readonly string _source;
        private readonly VadCalibrationService _calibrationService;
        private readonly IDisposable _session;
        private readonly DispatcherTimer _guideTimer;
        private readonly double _initialMinSilenceDuration;
        private readonly double _initialThreshold;
        private readonly List<VadCalibrationSample> _calibrationSamples = new();

        private DateTime _phaseEndsAtUtc;
        private GuidePhase _guidePhase = GuidePhase.Ready;
        private double _minSilenceDuration;
        private double _threshold;
        private string _measurementText = "Waiting for measurements.";
        private string _guideCountdownText = string.Empty;
        private string _suggestedSummary = "No recommendation yet. Start the guide to collect silence, speech, and pause samples.";
        private double? _suggestedMinSilenceDuration;
        private double? _suggestedThreshold;

        public VadCalibrationWindow(
            string routeDisplayName,
            string source,
            double minSilenceDuration,
            double threshold,
            VadCalibrationService calibrationService)
        {
            InitializeComponent();

            RouteDisplayName = string.IsNullOrWhiteSpace(routeDisplayName) ? "VAD" : routeDisplayName.Trim();
            _source = string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase) ? "Desktop" : "Mic";
            _calibrationService = calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));
            _initialMinSilenceDuration = Math.Clamp(minSilenceDuration, 0.05, 5.0);
            _initialThreshold = Math.Clamp(threshold, 0.01, 0.99);
            _minSilenceDuration = _initialMinSilenceDuration;
            _threshold = _initialThreshold;
            _session = _calibrationService.BeginSession(_source);

            _guideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _guideTimer.Tick += OnGuideTimerTick;

            DataContext = this;
            RefreshMeasurementText(_calibrationService.GetSnapshot(_source));

            _calibrationService.SnapshotUpdated += OnSnapshotUpdated;
            Closed += OnClosed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string RouteDisplayName { get; }

        public string TitleText => $"{RouteDisplayName} VAD Calibration";

        public double MinSilenceDuration
        {
            get => _minSilenceDuration;
            set
            {
                var clamped = Math.Clamp(value, 0.05, 5.0);
                if (SetField(ref _minSilenceDuration, clamped))
                {
                    OnPropertyChanged(nameof(PendingSummary));
                }
            }
        }

        public double Threshold
        {
            get => _threshold;
            set
            {
                var clamped = Math.Clamp(value, 0.01, 0.99);
                if (SetField(ref _threshold, clamped))
                {
                    OnPropertyChanged(nameof(PendingSummary));
                }
            }
        }

        public string MeasurementText
        {
            get => _measurementText;
            private set => SetField(ref _measurementText, value);
        }

        public string PendingSummary => $"Pending apply: silence {MinSilenceDuration:F2}s, threshold {Threshold:F2}";

        public double SelectedMinSilenceDuration => MinSilenceDuration;

        public double SelectedThreshold => Threshold;

        public string GuideTitle => _guidePhase switch
        {
            GuidePhase.Ready => "Prepare Calibration",
            GuidePhase.Silent => "Step 1: Stay Silent",
            GuidePhase.Speech => "Step 2: Speak Continuously",
            GuidePhase.PauseSpeech => "Step 3: Speak with Natural Pauses",
            GuidePhase.Completed => "Calibration Complete",
            _ => "Calibration"
        };

        public string GuideInstruction => _guidePhase switch
        {
            GuidePhase.Ready => "Start the guide, record room silence first, then continuous speech, and finally 2-3 short sentences with natural pauses.",
            GuidePhase.Silent => "Do not speak. Let the app observe background noise and silence windows.",
            GuidePhase.Speech => "Speak at a normal volume without long pauses so the app can collect labeled speech windows.",
            GuidePhase.PauseSpeech => "Say 2-3 short sentences with natural pauses so the app can search for a stable min-silence setting.",
            GuidePhase.Completed => "You can apply the suggested values directly or continue with manual fine-tuning below.",
            _ => string.Empty
        };

        public string GuideProgressText => _guidePhase switch
        {
            GuidePhase.Ready => "Collect silence, speech, and pause samples in order.",
            GuidePhase.Silent => "Recording labeled silence windows...",
            GuidePhase.Speech => "Recording labeled speech windows...",
            GuidePhase.PauseSpeech => "Recording pause-search windows...",
            GuidePhase.Completed => $"Collected {CountSamplesByPhase(VadCalibrationSamplePhase.Silence)} silence windows, {CountSamplesByPhase(VadCalibrationSamplePhase.Speech)} speech windows, {CountSamplesByPhase(VadCalibrationSamplePhase.Pause)} pause windows.",
            _ => string.Empty
        };

        public string GuideCountdownText
        {
            get => _guideCountdownText;
            private set => SetField(ref _guideCountdownText, value);
        }

        public string SuggestedSummary
        {
            get => _suggestedSummary;
            private set => SetField(ref _suggestedSummary, value);
        }

        public bool HasSuggestedValues => _suggestedMinSilenceDuration.HasValue && _suggestedThreshold.HasValue;

        public bool CanRestartGuide => _guidePhase != GuidePhase.Ready;

        private void OnSnapshotUpdated(object? sender, VadCalibrationSnapshot snapshot)
        {
            if (!string.Equals(snapshot.Source, _source, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshMeasurementText(snapshot);
                CaptureGuidedSample(snapshot);
            }));
        }

        private void RefreshMeasurementText(VadCalibrationSnapshot snapshot)
        {
            if (!snapshot.Enabled)
            {
                MeasurementText = "VAD monitoring is unavailable for this route. Check that the route is running and using VAD.";
                return;
            }

            if (!snapshot.HasMeasurement)
            {
                MeasurementText = "Waiting for VAD windows. Start the route and make sure audio is reaching the pipeline.";
                return;
            }

            var stateText = snapshot.IsSpeechDetected ? "speech detected" : "idle";
            MeasurementText = $"Current {snapshot.CurrentConfidence:F2} | Peak {snapshot.PeakConfidence:F2} | Threshold {snapshot.Threshold:F2} | {stateText}";
        }

        private void CaptureGuidedSample(VadCalibrationSnapshot snapshot)
        {
            if (!snapshot.Enabled || !snapshot.HasMeasurement || snapshot.WindowConfidences == null || snapshot.WindowConfidences.Length == 0)
            {
                return;
            }

            var phase = _guidePhase switch
            {
                GuidePhase.Silent => VadCalibrationSamplePhase.Silence,
                GuidePhase.Speech => VadCalibrationSamplePhase.Speech,
                GuidePhase.PauseSpeech => VadCalibrationSamplePhase.Pause,
                _ => (VadCalibrationSamplePhase?)null
            };

            if (!phase.HasValue)
            {
                return;
            }

            foreach (var confidence in snapshot.WindowConfidences)
            {
                _calibrationSamples.Add(new VadCalibrationSample(confidence, phase.Value));
            }

            OnPropertyChanged(nameof(GuideProgressText));
        }

        private void OnStartGuideClicked(object sender, RoutedEventArgs e)
        {
            StartGuide();
        }

        private void OnRestartGuideClicked(object sender, RoutedEventArgs e)
        {
            StartGuide();
        }

        private void StartGuide()
        {
            _calibrationSamples.Clear();
            _suggestedMinSilenceDuration = null;
            _suggestedThreshold = null;
            SuggestedSummary = "Guide is running. Follow the prompts to collect labeled windows for threshold search and pause search.";
            OnPropertyChanged(nameof(HasSuggestedValues));
            OnPropertyChanged(nameof(GuideProgressText));

            SwitchPhase(GuidePhase.Silent, SilentPhaseDuration);
        }

        private void SwitchPhase(GuidePhase nextPhase, TimeSpan duration)
        {
            _guidePhase = nextPhase;
            _phaseEndsAtUtc = DateTime.UtcNow + duration;
            UpdateGuideCountdown();
            OnPropertyChanged(nameof(GuideTitle));
            OnPropertyChanged(nameof(GuideInstruction));
            OnPropertyChanged(nameof(GuideProgressText));
            OnPropertyChanged(nameof(CanRestartGuide));

            if (!_guideTimer.IsEnabled)
            {
                _guideTimer.Start();
            }
        }

        private void CompleteGuide()
        {
            _guideTimer.Stop();
            _guidePhase = GuidePhase.Completed;
            GuideCountdownText = string.Empty;
            ComputeSuggestedValues();
            OnPropertyChanged(nameof(GuideTitle));
            OnPropertyChanged(nameof(GuideInstruction));
            OnPropertyChanged(nameof(GuideProgressText));
            OnPropertyChanged(nameof(CanRestartGuide));
        }

        private void OnGuideTimerTick(object? sender, EventArgs e)
        {
            if (_guidePhase is GuidePhase.Ready or GuidePhase.Completed)
            {
                _guideTimer.Stop();
                return;
            }

            if (DateTime.UtcNow >= _phaseEndsAtUtc)
            {
                AdvanceGuidePhase();
                return;
            }

            UpdateGuideCountdown();
        }

        private void AdvanceGuidePhase()
        {
            switch (_guidePhase)
            {
                case GuidePhase.Silent:
                    SwitchPhase(GuidePhase.Speech, SpeechPhaseDuration);
                    return;
                case GuidePhase.Speech:
                    SwitchPhase(GuidePhase.PauseSpeech, PausePhaseDuration);
                    return;
                case GuidePhase.PauseSpeech:
                    CompleteGuide();
                    return;
                default:
                    _guideTimer.Stop();
                    return;
            }
        }

        private void UpdateGuideCountdown()
        {
            var remaining = _phaseEndsAtUtc - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            GuideCountdownText = remaining == TimeSpan.Zero ? string.Empty : $"{Math.Ceiling(remaining.TotalSeconds):F0}s remaining";
        }

        private void ComputeSuggestedValues()
        {
            var thresholdSearch = VadCalibrationSearch.SearchThreshold(_calibrationSamples, _initialThreshold);
            var minSilenceSearch = VadCalibrationSearch.SearchMinSilence(_calibrationSamples, thresholdSearch.Threshold, _initialMinSilenceDuration);

            _suggestedThreshold = thresholdSearch.Threshold;
            _suggestedMinSilenceDuration = minSilenceSearch.MinSilenceDuration;
            OnPropertyChanged(nameof(HasSuggestedValues));

            SuggestedSummary = $"Suggested threshold {thresholdSearch.Threshold:F2}, suggested min silence {minSilenceSearch.MinSilenceDuration:F2}s."
                + BuildSuggestionReason(thresholdSearch, minSilenceSearch);
        }

        private static string BuildSuggestionReason(
            VadThresholdSearchResult thresholdSearch,
            VadMinSilenceSearchResult minSilenceSearch)
        {
            var reasons = new List<string>();

            if (thresholdSearch.UsedSearch)
            {
                reasons.Add($"Threshold search used {thresholdSearch.SilenceSampleCount} silence windows and {thresholdSearch.SpeechSampleCount} speech windows with balanced accuracy {thresholdSearch.BalancedAccuracy:P1}.");
            }
            else
            {
                reasons.Add("Threshold search fell back to the current setting because labeled silence or speech windows were insufficient.");
            }

            if (minSilenceSearch.UsedSearch)
            {
                reasons.Add($"Min-silence search chose the largest value that still preserved {minSilenceSearch.SegmentCount} segments in the pause sample.");
            }
            else if (minSilenceSearch.PauseP25.HasValue)
            {
                reasons.Add("Min-silence search fell back to the observed pause distribution because no candidate preserved 2-3 segments.");
            }
            else
            {
                reasons.Add("Min-silence search fell back to the current setting because no usable pause boundary was found.");
            }

            return " " + string.Join(" ", reasons);
        }

        private void OnApplySuggestedClicked(object sender, RoutedEventArgs e)
        {
            if (!_suggestedMinSilenceDuration.HasValue || !_suggestedThreshold.HasValue)
            {
                return;
            }

            MinSilenceDuration = _suggestedMinSilenceDuration.Value;
            Threshold = _suggestedThreshold.Value;
        }

        private void OnApplyClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void OnResetClicked(object sender, RoutedEventArgs e)
        {
            MinSilenceDuration = _initialMinSilenceDuration;
            Threshold = _initialThreshold;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _guideTimer.Stop();
            _calibrationService.SnapshotUpdated -= OnSnapshotUpdated;
            _session.Dispose();
        }

        private int CountSamplesByPhase(VadCalibrationSamplePhase phase)
            => _calibrationSamples.Count(sample => sample.Phase == phase);

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private enum GuidePhase
        {
            Ready = 0,
            Silent = 1,
            Speech = 2,
            PauseSpeech = 3,
            Completed = 4
        }
    }
}
