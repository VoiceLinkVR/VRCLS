using System;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IRecognitionService
    {
        event EventHandler<TranscriptionResult> TextRecognized;

        void Start();
        void Stop();
        void ProcessAudio(float[] audioData, RecognitionSegmentInfo? segmentInfo = null);

        /// <summary>
        /// Signal that the current speech segment has ended (e.g., VAD detected silence).
        /// This should reset the recognition state for the next segment.
        /// </summary>
        void FinishSegment();

        bool IsRunning { get; }
    }
}
