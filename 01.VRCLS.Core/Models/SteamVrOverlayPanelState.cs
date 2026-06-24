using System;
using System.Collections.Generic;

namespace VRCLS.Core.Models
{
    [Flags]
    public enum TranscriptSourceMask
    {
        None = 0,
        Mic = 1,
        Desktop = 2,
        Photo = 4,
        All = Mic | Desktop | Photo
    }

    [Flags]
    public enum TranscriptLanguageMask
    {
        None = 0,
        Original = 1,
        Translation1 = 2,
        Translation2 = 4,
        Translation3 = 8,
        All = Original | Translation1 | Translation2 | Translation3
    }

    public enum SteamVrOverlayAnchorMode
    {
        Palm = 0,
        BackOfHand = 1,
        Head = 2
    }

    public enum SteamVrTrackingDeviceMode
    {
        Auto = 0,
        Controller = 1,
        HandTracking = 2
    }

    public enum SteamVrOverlayActionType
    {
        ToggleMic,
        ToggleDesktop,
        TogglePanelSourceMic,
        TogglePanelSourceDesktop,
        TogglePanelLangOriginal,
        TogglePanelLangTranslation1,
        TogglePanelLangTranslation2,
        TogglePanelLangTranslation3,
        CopyTranscript,
        SetAnchorPalm,
        SetAnchorBackOfHand,
        SetAnchorHead,
        SetHandRight,
        SetHandLeft,
        SetTrackingDeviceAuto,
        SetTrackingDeviceController,
        SetTrackingDeviceHandTracking,
        IncreaseOverlaySize,
        DecreaseOverlaySize,
        IncreasePanelFontSize,
        DecreasePanelFontSize,
        ResetOverlayOffset,
        ToggleSteamVrAutoLaunch,
        ToggleTts,
        ToggleTtsMicPassthrough,
        DecreaseTtsMicPassthroughVolume,
        IncreaseTtsMicPassthroughVolume,
        ToggleMicTtsOriginal,
        ToggleMicTtsTranslation1,
        ToggleMicTtsTranslation2,
        ToggleMicTtsTranslation3,
        ToggleDesktopTtsOriginal,
        ToggleDesktopTtsTranslation1,
        ToggleDesktopTtsTranslation2,
        ToggleDesktopTtsTranslation3,
        SelectMicSourceLanguage,
        SelectMicTargetLanguage,
        SelectMicTargetLanguage2,
        SelectMicTargetLanguage3,
        SelectDesktopSourceLanguage,
        SelectDesktopTargetLanguage,
        SelectDesktopTargetLanguage2,
        SelectDesktopTargetLanguage3,
        RequestPhotoTranslation,
        CancelPhotoTranslation,
        ClosePhotoTranslation
    }

    public sealed class SteamVrOverlayAction : EventArgs
    {
        public SteamVrOverlayActionType Type { get; init; }
        public SteamVrOverlayTranscript? Transcript { get; init; }
        public string LanguageCode { get; init; } = string.Empty;
    }

    public sealed class SteamVrOverlayTranscript
    {
        public string Source { get; init; } = string.Empty;   // "Mic" / "Desktop" / etc
        public string Original { get; init; } = string.Empty;
        public string Translation { get; init; } = string.Empty;
        public string Translation2 { get; init; } = string.Empty;
        public string Translation3 { get; init; } = string.Empty;
        public string PhotoTranslationMode { get; init; } = string.Empty;
        public string PhotoImagePath { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.MinValue;
    }

    public sealed class SteamVrOverlayPanelState
    {
        public bool IsMicListening { get; init; }
        public bool IsDesktopListening { get; init; }
        public bool IsPhotoTranslationArmed { get; init; }
        public bool IsPhotoTranslationProcessing { get; init; }
        public float MicVolume { get; init; }       // 0..1 (best-effort)
        public float DesktopVolume { get; init; }   // 0..1 (best-effort)
        public bool IsTtsEnabled { get; init; }
        public bool IsTtsMicPassthroughEnabled { get; init; }
        public float TtsMicPassthroughVolume { get; init; } = 1f;
        public int MicTtsSpeakMask { get; init; } = 2;
        public int DesktopTtsSpeakMask { get; init; } = 2;
        public float NonTextOpacity { get; init; } = 1f;
        public float FontScale { get; init; } = 1f;
        public string MicTextColor { get; init; } = AppConfig.DefaultSteamVrMicTextColor;
        public string DesktopTextColor { get; init; } = AppConfig.DefaultSteamVrDesktopTextColor;
        public TranscriptSourceMask SourceMask { get; init; } = TranscriptSourceMask.All;
        public TranscriptLanguageMask LanguageMask { get; init; } = TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1;
        public string UiLanguage { get; init; } = "zh-CN";
        public string MicSourceLanguage { get; init; } = string.Empty;
        public string MicTargetLanguage { get; init; } = string.Empty;
        public string MicTargetLanguage2 { get; init; } = string.Empty;
        public string MicTargetLanguage3 { get; init; } = string.Empty;
        public string DesktopSourceLanguage { get; init; } = string.Empty;
        public string DesktopTargetLanguage { get; init; } = string.Empty;
        public string DesktopTargetLanguage2 { get; init; } = string.Empty;
        public string DesktopTargetLanguage3 { get; init; } = string.Empty;
        public int PhotoPanelIndex { get; init; }
        public int PhotoPanelTextScrollOffset { get; init; }

        public IReadOnlyList<SteamVrOverlayTranscript> Transcripts { get; init; } =
            Array.Empty<SteamVrOverlayTranscript>();
    }
}

