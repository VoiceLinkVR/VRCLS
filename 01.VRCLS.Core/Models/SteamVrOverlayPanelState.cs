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
        All = Mic | Desktop
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
        IncreaseOverlaySize,
        DecreaseOverlaySize,
        ResetOverlayOffset,
        ToggleSteamVrAutoLaunch
    }

    public sealed class SteamVrOverlayAction : EventArgs
    {
        public SteamVrOverlayActionType Type { get; init; }
        public SteamVrOverlayTranscript? Transcript { get; init; }
    }

    public sealed class SteamVrOverlayTranscript
    {
        public string Source { get; init; } = string.Empty;   // "Mic" / "Desktop" / etc
        public string Original { get; init; } = string.Empty;
        public string Translation { get; init; } = string.Empty;
        public string Translation2 { get; init; } = string.Empty;
        public string Translation3 { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.MinValue;
    }

    public sealed class SteamVrOverlayPanelState
    {
        public bool IsMicListening { get; init; }
        public bool IsDesktopListening { get; init; }
        public float MicVolume { get; init; }       // 0..1 (best-effort)
        public float DesktopVolume { get; init; }   // 0..1 (best-effort)
        public float NonTextOpacity { get; init; } = 1f;
        public TranscriptSourceMask SourceMask { get; init; } = TranscriptSourceMask.All;
        public TranscriptLanguageMask LanguageMask { get; init; } = TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1;

        public IReadOnlyList<SteamVrOverlayTranscript> Transcripts { get; init; } =
            Array.Empty<SteamVrOverlayTranscript>();
    }
}

