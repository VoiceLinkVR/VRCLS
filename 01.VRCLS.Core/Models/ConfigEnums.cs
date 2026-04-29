namespace VRCLS.Core.Models
{
    // Keep numeric values aligned with persisted config for backward compatibility.
    public enum VoiceTriggerMode
    {
        Continuous = 0,
        HotkeyToggle = 1,
        PressToTalk = 2
    }

    // Keep numeric values aligned with persisted config for backward compatibility.
    public enum TtsEngineMode
    {
        Server = 0,
        System = 1
    }

    public enum TtsInterruptPolicy
    {
        InterruptCurrent = 0,
        Queue = 1
    }

    public enum QwenTtsToneMode
    {
        Auto = 0,
        Custom = 1
    }

    // Keep numeric values aligned with persisted config for backward compatibility.
    public enum LocalRecognitionModel
    {
        Sherpa = 0,
        SenseVoice = 1
    }

    public enum CaptionOutputStyle
    {
        Mixed = 0,
        TranslationOnly = 1,
        OriginalOnly = 2
    }

    public enum ControlMicSourceType
    {
        FollowMainMic = 0,
        IndependentMic = 1
    }

    public enum RecognitionBackendMode
    {
        Remote = 0,
        Local = 1
    }

    public enum DesktopAudioSourceMode
    {
        System = 0,
        Application = 1
    }
}
