using System;
using System.Threading.Tasks;
using VRCLS.Core.Models;

namespace VRCLS.Core.Interfaces
{
    public interface IAudioService
    {
        event EventHandler<AudioDataEventArgs> AudioDataAvailable;
        event EventHandler<VolumeLevelChangedEventArgs> VolumeLevelChanged;

        void StartCapture(AudioCaptureRequest request);
        void StopCapture();
        bool IsCapturing { get; }
    }
}
