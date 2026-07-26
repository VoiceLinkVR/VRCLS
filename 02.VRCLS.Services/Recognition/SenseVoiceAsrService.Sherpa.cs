namespace VRCLS.Services.Recognition
{
    public sealed partial class SenseVoiceAsrService
    {
        public SenseVoiceAsrService(string modelPath, string source = "Mic")
            : this(new SherpaOfflineSenseVoiceRecognizer(modelPath), source)
        {
        }
    }
}
