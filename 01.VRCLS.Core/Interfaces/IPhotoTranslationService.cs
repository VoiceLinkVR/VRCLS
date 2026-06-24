using System;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IPhotoTranslationService : IDisposable
    {
        event EventHandler<TranscriptionResult>? TextRecognized;
        event EventHandler<bool>? NextPhotoTranslationArmedChanged;
        event EventHandler<bool>? PhotoTranslationProcessingChanged;

        bool IsRunning { get; }
        bool IsNextPhotoTranslationArmed { get; }
        bool IsPhotoTranslationProcessing { get; }

        void Start();

        void Stop();

        bool RequestNextPhotoTranslation();

        void CancelNextPhotoTranslation();

        Task<TranscriptionResult?> TranslateImageAsync(string imagePath);
    }
}
