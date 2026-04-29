using System;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IPhotoTranslationService : IDisposable
    {
        event EventHandler<TranscriptionResult>? TextRecognized;

        bool IsRunning { get; }

        void Start();

        void Stop();
    }
}
