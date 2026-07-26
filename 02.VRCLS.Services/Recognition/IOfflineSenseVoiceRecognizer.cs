using System;

namespace VRCLS.Services.Recognition
{
    internal interface IOfflineSenseVoiceRecognizer : IDisposable
    {
        string Decode(float[] audioData);
    }
}
