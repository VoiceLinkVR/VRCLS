using System;
using System.Collections.Generic;
using System.Linq;

namespace VRCLS.UI.Services
{
    internal enum VadCalibrationSamplePhase
    {
        Silence = 0,
        Speech = 1,
        Pause = 2
    }

    internal readonly record struct VadCalibrationSample(float Confidence, VadCalibrationSamplePhase Phase);

    internal sealed record VadThresholdSearchResult(
        bool UsedSearch,
        double Threshold,
        double BalancedAccuracy,
        double F1Score,
        int SilenceSampleCount,
        int SpeechSampleCount,
        double? SilenceP95,
        double? SpeechP20);

    internal sealed record VadMinSilenceSearchResult(
        bool UsedSearch,
        double MinSilenceDuration,
        int SegmentCount,
        int PauseWindowCount,
        int InterSpeechPauseCount,
        double? PauseP25);

    internal static class VadCalibrationSearch
    {
        private const double MinThreshold = 0.05;
        private const double MaxThreshold = 0.95;
        private const double ThresholdStep = 0.01;
        private const double MinSilenceLowerBound = 0.10;
        private const double MinSilenceUpperBound = 1.50;
        private const double MinSilenceStep = 0.05;
        private const double WindowDurationSeconds = 512d / 16000d;
        private const double MinSpeechDurationSeconds = 0.15;
        private const int MinimumSearchSamplesPerClass = 8;
        private const int TargetMinSegments = 2;
        private const int TargetMaxSegments = 3;
        private const double ScoreEqualityTolerance = 1e-9;

        public static VadThresholdSearchResult SearchThreshold(IReadOnlyList<VadCalibrationSample> samples, double fallbackThreshold)
        {
            var silenceSamples = samples
                .Where(sample => sample.Phase == VadCalibrationSamplePhase.Silence)
                .Select(sample => (double)sample.Confidence)
                .ToArray();
            var speechSamples = samples
                .Where(sample => sample.Phase == VadCalibrationSamplePhase.Speech)
                .Select(sample => (double)sample.Confidence)
                .ToArray();

            var silenceP95 = Percentile(silenceSamples, 0.95);
            var speechP20 = Percentile(speechSamples, 0.20);
            var midpoint = silenceP95.HasValue && speechP20.HasValue
                ? (silenceP95.Value + speechP20.Value) / 2d
                : (double?)null;

            if (silenceSamples.Length < MinimumSearchSamplesPerClass || speechSamples.Length < MinimumSearchSamplesPerClass)
            {
                return new VadThresholdSearchResult(
                    UsedSearch: false,
                    Threshold: Clamp(fallbackThreshold, MinThreshold, MaxThreshold),
                    BalancedAccuracy: 0d,
                    F1Score: 0d,
                    SilenceSampleCount: silenceSamples.Length,
                    SpeechSampleCount: speechSamples.Length,
                    SilenceP95: silenceP95,
                    SpeechP20: speechP20);
            }

            VadThresholdSearchResult? best = null;
            for (var threshold = MinThreshold; threshold <= MaxThreshold + ScoreEqualityTolerance; threshold += ThresholdStep)
            {
                var roundedThreshold = Math.Round(threshold, 2, MidpointRounding.AwayFromZero);
                var tp = speechSamples.Count(value => value >= roundedThreshold);
                var fn = speechSamples.Length - tp;
                var tn = silenceSamples.Count(value => value < roundedThreshold);
                var fp = silenceSamples.Length - tn;

                var recall = tp / (double)speechSamples.Length;
                var specificity = tn / (double)silenceSamples.Length;
                var balancedAccuracy = (recall + specificity) / 2d;
                var precision = tp + fp == 0 ? 0d : tp / (double)(tp + fp);
                var f1 = precision + recall == 0d ? 0d : (2d * precision * recall) / (precision + recall);

                var candidate = new VadThresholdSearchResult(
                    UsedSearch: true,
                    Threshold: roundedThreshold,
                    BalancedAccuracy: balancedAccuracy,
                    F1Score: f1,
                    SilenceSampleCount: silenceSamples.Length,
                    SpeechSampleCount: speechSamples.Length,
                    SilenceP95: silenceP95,
                    SpeechP20: speechP20);

                if (best == null || IsBetterThresholdCandidate(candidate, best, midpoint))
                {
                    best = candidate;
                }
            }

            return best ?? new VadThresholdSearchResult(
                UsedSearch: false,
                Threshold: Clamp(fallbackThreshold, MinThreshold, MaxThreshold),
                BalancedAccuracy: 0d,
                F1Score: 0d,
                SilenceSampleCount: silenceSamples.Length,
                SpeechSampleCount: speechSamples.Length,
                SilenceP95: silenceP95,
                SpeechP20: speechP20);
        }

        public static VadMinSilenceSearchResult SearchMinSilence(
            IReadOnlyList<VadCalibrationSample> samples,
            double threshold,
            double fallbackMinSilence)
        {
            var pauseConfidences = samples
                .Where(sample => sample.Phase == VadCalibrationSamplePhase.Pause)
                .Select(sample => (double)sample.Confidence)
                .ToArray();

            if (pauseConfidences.Length == 0)
            {
                return new VadMinSilenceSearchResult(
                    UsedSearch: false,
                    MinSilenceDuration: Clamp(fallbackMinSilence, MinSilenceLowerBound, MinSilenceUpperBound),
                    SegmentCount: 0,
                    PauseWindowCount: 0,
                    InterSpeechPauseCount: 0,
                    PauseP25: null);
            }

            var speechFlags = pauseConfidences
                .Select(confidence => confidence >= threshold)
                .ToArray();
            var normalizedRuns = BuildNormalizedRuns(speechFlags);
            var interSpeechPauseDurations = GetInterSpeechPauseDurations(normalizedRuns);
            var pauseP25 = Percentile(interSpeechPauseDurations, 0.25);

            VadMinSilenceSearchResult? best = null;
            for (var minSilence = MinSilenceLowerBound; minSilence <= MinSilenceUpperBound + ScoreEqualityTolerance; minSilence += MinSilenceStep)
            {
                var roundedMinSilence = Math.Round(minSilence, 2, MidpointRounding.AwayFromZero);
                var segmentCount = CountSegments(normalizedRuns, roundedMinSilence);
                var candidate = new VadMinSilenceSearchResult(
                    UsedSearch: segmentCount >= TargetMinSegments && segmentCount <= TargetMaxSegments,
                    MinSilenceDuration: roundedMinSilence,
                    SegmentCount: segmentCount,
                    PauseWindowCount: pauseConfidences.Length,
                    InterSpeechPauseCount: interSpeechPauseDurations.Length,
                    PauseP25: pauseP25);

                if (!candidate.UsedSearch)
                {
                    continue;
                }

                if (best == null || IsBetterMinSilenceCandidate(candidate, best))
                {
                    best = candidate;
                }
            }

            if (best != null)
            {
                return best;
            }

            var fallback = pauseP25 ?? fallbackMinSilence;
            return new VadMinSilenceSearchResult(
                UsedSearch: false,
                MinSilenceDuration: Clamp(fallback, MinSilenceLowerBound, MinSilenceUpperBound),
                SegmentCount: CountSegments(normalizedRuns, Clamp(fallback, MinSilenceLowerBound, MinSilenceUpperBound)),
                PauseWindowCount: pauseConfidences.Length,
                InterSpeechPauseCount: interSpeechPauseDurations.Length,
                PauseP25: pauseP25);
        }

        private static bool IsBetterThresholdCandidate(
            VadThresholdSearchResult candidate,
            VadThresholdSearchResult currentBest,
            double? midpoint)
        {
            if (candidate.BalancedAccuracy > currentBest.BalancedAccuracy + ScoreEqualityTolerance)
            {
                return true;
            }

            if (candidate.BalancedAccuracy < currentBest.BalancedAccuracy - ScoreEqualityTolerance)
            {
                return false;
            }

            if (candidate.F1Score > currentBest.F1Score + ScoreEqualityTolerance)
            {
                return true;
            }

            if (candidate.F1Score < currentBest.F1Score - ScoreEqualityTolerance)
            {
                return false;
            }

            if (midpoint.HasValue)
            {
                var candidateDistance = Math.Abs(candidate.Threshold - midpoint.Value);
                var bestDistance = Math.Abs(currentBest.Threshold - midpoint.Value);
                if (candidateDistance + ScoreEqualityTolerance < bestDistance)
                {
                    return true;
                }

                if (candidateDistance > bestDistance + ScoreEqualityTolerance)
                {
                    return false;
                }
            }

            return candidate.Threshold > currentBest.Threshold;
        }

        private static bool IsBetterMinSilenceCandidate(VadMinSilenceSearchResult candidate, VadMinSilenceSearchResult currentBest)
        {
            if (candidate.MinSilenceDuration > currentBest.MinSilenceDuration + ScoreEqualityTolerance)
            {
                return true;
            }

            if (candidate.MinSilenceDuration < currentBest.MinSilenceDuration - ScoreEqualityTolerance)
            {
                return false;
            }

            return candidate.SegmentCount < currentBest.SegmentCount;
        }

        private static Run[] BuildNormalizedRuns(IReadOnlyList<bool> speechFlags)
        {
            if (speechFlags.Count == 0)
            {
                return Array.Empty<Run>();
            }

            var runs = BuildRuns(speechFlags);
            var minSpeechWindows = Math.Max(1, (int)Math.Ceiling(MinSpeechDurationSeconds / WindowDurationSeconds));
            var normalizedFlags = new List<bool>(speechFlags.Count);

            foreach (var run in runs)
            {
                var isSpeech = run.IsSpeech && run.WindowCount >= minSpeechWindows;
                for (var index = 0; index < run.WindowCount; index++)
                {
                    normalizedFlags.Add(isSpeech);
                }
            }

            return BuildRuns(normalizedFlags);
        }

        private static Run[] BuildRuns(IReadOnlyList<bool> speechFlags)
        {
            if (speechFlags.Count == 0)
            {
                return Array.Empty<Run>();
            }

            var runs = new List<Run>();
            var currentState = speechFlags[0];
            var currentCount = 1;

            for (var index = 1; index < speechFlags.Count; index++)
            {
                if (speechFlags[index] == currentState)
                {
                    currentCount++;
                    continue;
                }

                runs.Add(new Run(currentState, currentCount));
                currentState = speechFlags[index];
                currentCount = 1;
            }

            runs.Add(new Run(currentState, currentCount));
            return runs.ToArray();
        }

        private static double[] GetInterSpeechPauseDurations(IReadOnlyList<Run> runs)
        {
            if (runs.Count < 3)
            {
                return Array.Empty<double>();
            }

            var durations = new List<double>();
            for (var index = 1; index < runs.Count - 1; index++)
            {
                if (!runs[index].IsSpeech && runs[index - 1].IsSpeech && runs[index + 1].IsSpeech)
                {
                    durations.Add(runs[index].WindowCount * WindowDurationSeconds);
                }
            }

            return durations.ToArray();
        }

        private static int CountSegments(IReadOnlyList<Run> runs, double minSilenceDuration)
        {
            if (runs.Count == 0)
            {
                return 0;
            }

            var segments = 0;
            var inSegment = false;

            foreach (var run in runs)
            {
                if (run.IsSpeech)
                {
                    if (!inSegment)
                    {
                        segments++;
                        inSegment = true;
                    }

                    continue;
                }

                if (run.WindowCount * WindowDurationSeconds >= minSilenceDuration)
                {
                    inSegment = false;
                }
            }

            return segments;
        }

        private static double Clamp(double value, double minValue, double maxValue)
            => Math.Clamp(value, minValue, maxValue);

        private static double? Percentile(IReadOnlyList<double> values, double percentile)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            var ordered = values.OrderBy(x => x).ToArray();
            var index = (ordered.Length - 1) * percentile;
            var lower = (int)Math.Floor(index);
            var upper = (int)Math.Ceiling(index);
            if (lower == upper)
            {
                return ordered[lower];
            }

            var fraction = index - lower;
            return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
        }

        private readonly record struct Run(bool IsSpeech, int WindowCount);
    }
}
