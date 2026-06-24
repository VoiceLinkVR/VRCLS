using System;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IVadService
    {
        event EventHandler<VadSpeechEventArgs> SpeechDetected;
        event EventHandler<VadSpeechEventArgs> SpeechEnded;
        event EventHandler<VadConfidenceMeasuredEventArgs> ConfidenceMeasured;
        event EventHandler<ServiceErrorEvent>? ErrorOccurred;

        void ProcessAudio(float[] audioData);
        void Reset();
    }

    public class VadSpeechEventArgs : EventArgs
    {
        public float[] AudioData { get; set; } = Array.Empty<float>();
        public bool IsSpeech { get; set; }
    }

    public class VadConfidenceMeasuredEventArgs : EventArgs
    {
        public float CurrentConfidence { get; set; }

        public float PeakConfidence { get; set; }

        public float Threshold { get; set; }

        public bool IsSpeechDetected { get; set; }

        public int WindowCount { get; set; }

        public float[] WindowConfidences { get; set; } = Array.Empty<float>();
    }

}
