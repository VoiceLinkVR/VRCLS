using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    public class AppConfig
    {
        public const string DefaultServiceBaseUrl = "https://www.voicelinkvr.top";
        public const string LegacyDefaultServiceBaseUrl = "https://whisper.boyqiu001.cn:7070";
        public const string DefaultApplicationAudioTargetProcessName = "VRChat";
        public const string TtsProviderEdge = "edge";
        public const string TtsProviderQwen = "qwen";
        public const string DefaultTtsProvider = TtsProviderQwen;
        public const string DefaultQwenTtsInstruct = "请用稳定、自然、克制的语气朗读，语速均匀，情绪变化小，不要夸张，不要突然升降调。";
        public const float DefaultVadMaxSegmentSecondsValue = 10f;
        public const string DefaultUpdateServerUrl = "https://cloudflarestorage.boyqiu001.top/";
        public const string DefaultAppUpdateFeedUrl = DefaultUpdateServerUrl + "app-releases/appcast.xml";

        [JsonPropertyName("userInfo")]
        public UserInfo UserInfo { get; set; } = new();

        [JsonPropertyName("baseurl")]
        public string BaseUrl { get; set; } = DefaultServiceBaseUrl;

        [JsonPropertyName("useLegacyBackendApi")]
        public bool UseLegacyBackendApi { get; set; }

        [JsonPropertyName("api-ip")]
        public string ApiIp { get; set; } = "127.0.0.1";

        [JsonPropertyName("api-port")]
        public int ApiPort { get; set; } = 8980;

        [JsonPropertyName("osc-port")]
        public int OscPort { get; set; } = 9000;

        [JsonPropertyName("osc-ip")]
        public string OscIp { get; set; } = "127.0.0.1";

        [JsonPropertyName("defaultMode")]
        public string DefaultMode { get; set; } = "translation";

        [JsonPropertyName("activateText")]
        public string ActivateText { get; set; } = string.Empty;

        [JsonPropertyName("stopText")]
        public string StopText { get; set; } = string.Empty;

        [JsonPropertyName("micName")]
        public string MicName { get; set; } = "default";

        [JsonPropertyName("voiceMode")]
        public int VoiceMode { get; set; } = 0;

        [JsonIgnore]
        public VoiceTriggerMode VoiceTriggerModeValue
        {
            get => Enum.IsDefined(typeof(VoiceTriggerMode), VoiceMode)
                ? (VoiceTriggerMode)VoiceMode
                : VoiceTriggerMode.Continuous;
            set => VoiceMode = (int)value;
        }

        [JsonPropertyName("autoStartMic")]
        public bool AutoStartMic { get; set; } = true;

        [JsonPropertyName("webBrowserPath")]
        public string WebBrowserPath { get; set; } = string.Empty;

        [JsonPropertyName("dynamicThreshold")]
        public bool DynamicThreshold { get; set; } = false;

        [JsonPropertyName("customThreshold")]
        public float CustomThreshold { get; set; } = 0.01f;

        [JsonPropertyName("voiceHotKey_new")]
        public string VoiceHotKey { get; set; } = "<alt>+1";

        [JsonPropertyName("sourceLanguage")]
        public string SourceLanguage { get; set; } = "zh";

        [JsonPropertyName("micSourceLanguage")]
        public string MicSourceLanguage { get; set; } = string.Empty;

        [JsonPropertyName("desktopSourceLanguage")]
        public string DesktopSourceLanguage { get; set; } = string.Empty;

        [JsonPropertyName("targetTranslationLanguage")]
        public string TargetTranslationLanguage { get; set; } = "en";

        [JsonPropertyName("targetTranslationLanguage2")]
        public string TargetTranslationLanguage2 { get; set; } = "none";

        [JsonPropertyName("targetTranslationLanguage3")]
        public string TargetTranslationLanguage3 { get; set; } = "none";

        [JsonPropertyName("micTargetTranslationLanguage")]
        public string MicTargetTranslationLanguage { get; set; } = string.Empty;

        [JsonPropertyName("micTargetTranslationLanguage2")]
        public string MicTargetTranslationLanguage2 { get; set; } = string.Empty;

        [JsonPropertyName("micTargetTranslationLanguage3")]
        public string MicTargetTranslationLanguage3 { get; set; } = string.Empty;

        [JsonPropertyName("desktopTargetTranslationLanguage")]
        public string DesktopTargetTranslationLanguage { get; set; } = string.Empty;

        [JsonPropertyName("desktopTargetTranslationLanguage2")]
        public string DesktopTargetTranslationLanguage2 { get; set; } = string.Empty;

        [JsonPropertyName("desktopTargetTranslationLanguage3")]
        public string DesktopTargetTranslationLanguage3 { get; set; } = string.Empty;

        [JsonPropertyName("textSendTargetTranslationLanguage")]
        public string TextSendTargetTranslationLanguage { get; set; } = string.Empty;

        [JsonPropertyName("textSendTargetTranslationLanguage2")]
        public string TextSendTargetTranslationLanguage2 { get; set; } = string.Empty;

        [JsonPropertyName("textSendTargetTranslationLanguage3")]
        public string TextSendTargetTranslationLanguage3 { get; set; } = string.Empty;

        [JsonPropertyName("translationServer")]
        public string TranslationServer { get; set; } = "developer";

        [JsonPropertyName("translatorsDefaultRegion")]
        public string TranslatorsDefaultRegion { get; set; } = string.Empty;

        [JsonPropertyName("supportedTranslationEngines")]
        public List<string> SupportedTranslationEngines { get; set; } = new()
        {
            "google", "bing", "baidu", "yandex", "deepl"
        };

        [JsonPropertyName("supportedRecognitionLanguages")]
        public List<LanguageCatalogItem> SupportedRecognitionLanguages { get; set; } = new();

        [JsonPropertyName("supportedTranslationLanguages")]
        public List<LanguageCatalogItem> SupportedTranslationLanguages { get; set; } = new();

        [JsonPropertyName("translatorsRuntimeVersion")]
        public string TranslatorsRuntimeVersion { get; set; } = string.Empty;

        [JsonPropertyName("translatorsRuntimeDir")]
        public string TranslatorsRuntimeDir { get; set; } = string.Empty;

        [JsonPropertyName("appUpdateFeedUrl")]
        public string AppUpdateFeedUrl { get; set; } = DefaultAppUpdateFeedUrl;

        // Static content base URL for model catalog, model packages,
        // translation profiles, and translator runtime packages.
        [JsonPropertyName("updateServerUrl")]
        public string UpdateServerUrl { get; set; } = DefaultUpdateServerUrl;

        [JsonPropertyName("updateChannel")]
        public string UpdateChannel { get; set; } = "stable";

        [JsonPropertyName("updateClientId")]
        public string UpdateClientId { get; set; } = string.Empty;

        [JsonPropertyName("autoCheckUpdate")]
        public bool AutoCheckUpdate { get; set; } = true;

        [JsonPropertyName("autoDownloadUpdate")]
        public bool AutoDownloadUpdate { get; set; } = false;

        [JsonPropertyName("updateCheckIntervalHours")]
        public int UpdateCheckIntervalHours { get; set; } = 24;

        [JsonPropertyName("lastUpdateCheckUtc")]
        public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;

        [JsonPropertyName("autoDownloadModels")]
        public bool AutoDownloadModels { get; set; } = true;

        [JsonPropertyName("translationProfileVersion")]
        public string TranslationProfileVersion { get; set; } = string.Empty;

        [JsonPropertyName("autoSyncTranslationProfile")]
        public bool AutoSyncTranslationProfile { get; set; } = true;

        [JsonPropertyName("VRCBitmapLed_row")]
        public int VrcBitmapLedRow { get; set; } = 8;

        [JsonPropertyName("VRCBitmapLed_col")]
        public int VrcBitmapLedCol { get; set; } = 16;

        [JsonPropertyName("VRCBitmapLed_COLOR")]
        public bool VrcBitmapLedColor { get; set; } = true;

        [JsonPropertyName("Separate_Self_Game_Mic")]
        public int SeparateSelfGameMic { get; set; } = 0;

        [JsonPropertyName("gameMicName")]
        public string GameMicName { get; set; } = "default";

        [JsonPropertyName("gameVoiceMode")]
        public int GameVoiceMode { get; set; } = 0;

        [JsonIgnore]
        public VoiceTriggerMode GameVoiceTriggerModeValue
        {
            get => Enum.IsDefined(typeof(VoiceTriggerMode), GameVoiceMode)
                ? (VoiceTriggerMode)GameVoiceMode
                : VoiceTriggerMode.Continuous;
            set => GameVoiceMode = (int)value;
        }

        [JsonPropertyName("autoStartDesktop")]
        public bool AutoStartDesktop { get; set; } = false;

        [JsonPropertyName("desktopAudioSourceMode")]
        public int DesktopAudioSourceMode { get; set; } = (int)VRCLS.Core.Models.DesktopAudioSourceMode.Application;

        [JsonIgnore]
        public DesktopAudioSourceMode DesktopAudioSourceModeValue
        {
            get => Enum.IsDefined(typeof(VRCLS.Core.Models.DesktopAudioSourceMode), DesktopAudioSourceMode)
                ? (VRCLS.Core.Models.DesktopAudioSourceMode)DesktopAudioSourceMode
                : VRCLS.Core.Models.DesktopAudioSourceMode.System;
            set => DesktopAudioSourceMode = (int)value;
        }

        [JsonPropertyName("appAudioTargetProcessId")]
        public int AppAudioTargetProcessId { get; set; } = 0;

        [JsonPropertyName("appAudioTargetProcessName")]
        public string AppAudioTargetProcessName { get; set; } = DefaultApplicationAudioTargetProcessName;

        [JsonPropertyName("gameCustomThreshold")]
        public float GameCustomThreshold { get; set; } = 0.01f;

        [JsonPropertyName("gameVoiceHotKey_new")]
        public string GameVoiceHotKey { get; set; } = "<alt>+2";

        [JsonPropertyName("textInSteamVR")]
        public bool TextInSteamVr { get; set; } = false;

        [JsonPropertyName("SteamVRHad")]
        public int SteamVrHand { get; set; } = 0;

        [JsonPropertyName("SteamVRSize")]
        public float SteamVrSize { get; set; } = 0.15f;

        [JsonPropertyName("steamVrAnchorMode")]
        public int SteamVrAnchorMode { get; set; } = (int)VRCLS.Core.Models.SteamVrOverlayAnchorMode.Palm;

        [JsonIgnore]
        public VRCLS.Core.Models.SteamVrOverlayAnchorMode SteamVrAnchorModeValue
        {
            get => Enum.IsDefined(typeof(VRCLS.Core.Models.SteamVrOverlayAnchorMode), SteamVrAnchorMode)
                ? (VRCLS.Core.Models.SteamVrOverlayAnchorMode)SteamVrAnchorMode
                : VRCLS.Core.Models.SteamVrOverlayAnchorMode.Palm;
            set => SteamVrAnchorMode = (int)value;
        }

        [JsonPropertyName("steamVrOffsetX")]
        public float SteamVrOffsetX { get; set; } = 0f;

        [JsonPropertyName("steamVrOffsetY")]
        public float SteamVrOffsetY { get; set; } = 0f;

        [JsonPropertyName("steamVrOffsetZ")]
        public float SteamVrOffsetZ { get; set; } = 0f;

        [JsonPropertyName("steamVrBackOfHandOffsetX")]
        public float SteamVrBackOfHandOffsetX { get; set; } = 0f;

        [JsonPropertyName("steamVrBackOfHandOffsetY")]
        public float SteamVrBackOfHandOffsetY { get; set; } = 0f;

        [JsonPropertyName("steamVrBackOfHandOffsetZ")]
        public float SteamVrBackOfHandOffsetZ { get; set; } = 0f;

        [JsonPropertyName("steamVrHeadOffsetX")]
        public float SteamVrHeadOffsetX { get; set; } = 0f;

        [JsonPropertyName("steamVrHeadOffsetY")]
        public float SteamVrHeadOffsetY { get; set; } = 0f;

        [JsonPropertyName("steamVrHeadOffsetZ")]
        public float SteamVrHeadOffsetZ { get; set; } = 0f;

        [JsonPropertyName("steamVrRotationX")]
        public float SteamVrRotationX { get; set; } = 0f;

        [JsonPropertyName("steamVrRotationY")]
        public float SteamVrRotationY { get; set; } = 0f;

        [JsonPropertyName("steamVrRotationZ")]
        public float SteamVrRotationZ { get; set; } = 0f;

        [JsonPropertyName("steamVrBackOfHandRotationX")]
        public float SteamVrBackOfHandRotationX { get; set; } = 0f;

        [JsonPropertyName("steamVrBackOfHandRotationY")]
        public float SteamVrBackOfHandRotationY { get; set; } = 0f;

        [JsonPropertyName("steamVrBackOfHandRotationZ")]
        public float SteamVrBackOfHandRotationZ { get; set; } = 0f;

        [JsonPropertyName("steamVrHeadRotationX")]
        public float SteamVrHeadRotationX { get; set; } = 0f;

        [JsonPropertyName("steamVrHeadRotationY")]
        public float SteamVrHeadRotationY { get; set; } = 0f;

        [JsonPropertyName("steamVrHeadRotationZ")]
        public float SteamVrHeadRotationZ { get; set; } = 0f;

        [JsonPropertyName("steamVrAutoLaunch")]
        public bool SteamVrAutoLaunch { get; set; } = false;

        [JsonPropertyName("steamVrNonTextOpacity")]
        public float SteamVrNonTextOpacity { get; set; } = 1f;

        public (float X, float Y, float Z) GetSteamVrAnchorOffset(VRCLS.Core.Models.SteamVrOverlayAnchorMode anchorMode)
        {
            return anchorMode switch
            {
                VRCLS.Core.Models.SteamVrOverlayAnchorMode.BackOfHand => (SteamVrBackOfHandOffsetX, SteamVrBackOfHandOffsetY, SteamVrBackOfHandOffsetZ),
                VRCLS.Core.Models.SteamVrOverlayAnchorMode.Head => (SteamVrHeadOffsetX, SteamVrHeadOffsetY, SteamVrHeadOffsetZ),
                _ => (SteamVrOffsetX, SteamVrOffsetY, SteamVrOffsetZ)
            };
        }

        public void SetSteamVrAnchorOffset(VRCLS.Core.Models.SteamVrOverlayAnchorMode anchorMode, float x, float y, float z)
        {
            switch (anchorMode)
            {
                case VRCLS.Core.Models.SteamVrOverlayAnchorMode.BackOfHand:
                    SteamVrBackOfHandOffsetX = x;
                    SteamVrBackOfHandOffsetY = y;
                    SteamVrBackOfHandOffsetZ = z;
                    break;
                case VRCLS.Core.Models.SteamVrOverlayAnchorMode.Head:
                    SteamVrHeadOffsetX = x;
                    SteamVrHeadOffsetY = y;
                    SteamVrHeadOffsetZ = z;
                    break;
                default:
                    SteamVrOffsetX = x;
                    SteamVrOffsetY = y;
                    SteamVrOffsetZ = z;
                    break;
            }
        }

        public (float X, float Y, float Z) GetSteamVrAnchorRotation(VRCLS.Core.Models.SteamVrOverlayAnchorMode anchorMode)
        {
            return anchorMode switch
            {
                VRCLS.Core.Models.SteamVrOverlayAnchorMode.BackOfHand => (SteamVrBackOfHandRotationX, SteamVrBackOfHandRotationY, SteamVrBackOfHandRotationZ),
                VRCLS.Core.Models.SteamVrOverlayAnchorMode.Head => (SteamVrHeadRotationX, SteamVrHeadRotationY, SteamVrHeadRotationZ),
                _ => (SteamVrRotationX, SteamVrRotationY, SteamVrRotationZ)
            };
        }

        public void SetSteamVrAnchorRotation(VRCLS.Core.Models.SteamVrOverlayAnchorMode anchorMode, float x, float y, float z)
        {
            switch (anchorMode)
            {
                case VRCLS.Core.Models.SteamVrOverlayAnchorMode.BackOfHand:
                    SteamVrBackOfHandRotationX = x;
                    SteamVrBackOfHandRotationY = y;
                    SteamVrBackOfHandRotationZ = z;
                    break;
                case VRCLS.Core.Models.SteamVrOverlayAnchorMode.Head:
                    SteamVrHeadRotationX = x;
                    SteamVrHeadRotationY = y;
                    SteamVrHeadRotationZ = z;
                    break;
                default:
                    SteamVrRotationX = x;
                    SteamVrRotationY = y;
                    SteamVrRotationZ = z;
                    break;
            }
        }

        [JsonPropertyName("steamVrPanelSourceMask")]
        public TranscriptSourceMask SteamVrPanelSourceMask { get; set; } = TranscriptSourceMask.All;

        [JsonPropertyName("steamVrPanelLanguageMask")]
        public TranscriptLanguageMask SteamVrPanelLanguageMask { get; set; } = TranscriptLanguageMask.All;

        // SteamVR overlay cursor UV flips (helps calibrate palm overlay interaction across different controller poses/devices).
        [JsonPropertyName("SteamVrCursorFlipU")]
        public bool SteamVrCursorFlipU { get; set; } = false;

        [JsonPropertyName("SteamVrCursorFlipV")]
        public bool SteamVrCursorFlipV { get; set; } = false;

        [JsonPropertyName("localizedSpeech")]
        public bool LocalizedSpeech { get; set; } = false;

        [JsonPropertyName("localizedCapture")]
        public bool LocalizedCapture { get; set; } = false;

        [JsonPropertyName("enablePhotoTranslation")]
        public bool EnablePhotoTranslation { get; set; } = true;

        [JsonPropertyName("photoRootPath")]
        public string PhotoRootPath { get; set; } = string.Empty;

        [JsonPropertyName("CopyBox")]
        public bool CopyBox { get; set; } = false;

        [JsonPropertyName("copyLanguageMask")]
        public TranscriptLanguageMask CopyLanguageMask { get; set; } = TranscriptLanguageMask.Translation1;

        [JsonPropertyName("TTSToggle")]
        public int TtsToggle { get; set; } = 0;

        // 0 = server (remote); 1 = system (local System.Speech)
        // Keep default as server for backwards compatibility with existing configs.
        [JsonPropertyName("TTSEngine")]
        public int TtsEngine { get; set; } = 0;

        [JsonIgnore]
        public TtsEngineMode TtsEngineModeValue
        {
            get => Enum.IsDefined(typeof(TtsEngineMode), TtsEngine)
                ? (TtsEngineMode)TtsEngine
                : TtsEngineMode.Server;
            set => TtsEngine = (int)value;
        }

        [JsonPropertyName("TTSOutputName")]
        public string TtsOutputName { get; set; } = "default";

        [JsonPropertyName("TTSVoiceName")]
        public string TtsVoiceName { get; set; } = string.Empty;

        [JsonPropertyName("ttsProvider")]
        public string TtsProvider { get; set; } = DefaultTtsProvider;

        [JsonPropertyName("ttsSpeed")]
        public float TtsSpeed { get; set; } = 1.0f;

        [JsonPropertyName("ttsPitch")]
        public float TtsPitch { get; set; } = 1.0f;

        // 0 = auto model tone, 1 = use custom Qwen TTS instruction.
        [JsonPropertyName("qwenTtsToneMode")]
        public int QwenTtsToneMode { get; set; } = (int)VRCLS.Core.Models.QwenTtsToneMode.Custom;

        [JsonIgnore]
        public VRCLS.Core.Models.QwenTtsToneMode QwenTtsToneModeValue
        {
            get => Enum.IsDefined(typeof(VRCLS.Core.Models.QwenTtsToneMode), QwenTtsToneMode)
                ? (VRCLS.Core.Models.QwenTtsToneMode)QwenTtsToneMode
                : VRCLS.Core.Models.QwenTtsToneMode.Custom;
            set => QwenTtsToneMode = (int)value;
        }

        [JsonPropertyName("qwenTtsInstruct")]
        public string QwenTtsInstruct { get; set; } = DefaultQwenTtsInstruct;

        // 0 = interrupt current playback, 1 = queue wait.
        [JsonPropertyName("ttsInterruptMode")]
        public int TtsInterruptMode { get; set; } = 1;

        [JsonIgnore]
        public TtsInterruptPolicy TtsInterruptPolicyValue
        {
            get => Enum.IsDefined(typeof(TtsInterruptPolicy), TtsInterruptMode)
                ? (TtsInterruptPolicy)TtsInterruptMode
                : TtsInterruptPolicy.Queue;
            set => TtsInterruptMode = (int)value;
        }

        // TTS content selection (bitmask).
        // 1 = original, 2 = translation1, 4 = translation2, 8 = translation3
        [JsonPropertyName("MicTtsSpeakMask")]
        public int MicTtsSpeakMask { get; set; } = 2;

        [JsonPropertyName("DesktopTtsSpeakMask")]
        public int DesktopTtsSpeakMask { get; set; } = 2;

        [JsonPropertyName("translateService")]
        public string TranslateService { get; set; } = "developer";

        [JsonPropertyName("pythonPath")]
        public string PythonPath { get; set; } = "python";

        [JsonPropertyName("translateRegion")]
        public string TranslateRegion { get; set; } = "CN";

        [JsonPropertyName("oscShutdown")]
        public bool OscShutdown { get; set; } = false;

        [JsonPropertyName("VRCChatboxformat_new")]
        public string VrcChatboxFormatNew { get; set; } = "{translatedText}\n{translatedText2}\n{translatedText3}\n{text}";

        [JsonPropertyName("VRCChatboxformat_text")]
        public string VrcChatboxFormatText { get; set; } = "{text}";

        [JsonPropertyName("oscChatboxNotificationSfx")]
        public bool OscChatboxNotificationSfx { get; set; } = true;

        [JsonPropertyName("realtimeOutputDelay")]
        [Obsolete("Deprecated: no longer used by UI")]
        public float RealtimeOutputDelay { get; set; } = -1.0f;

        [JsonPropertyName("micPressingKey")]
        public string MicPressingKey { get; set; } = "v";

        [JsonPropertyName("capPressingKey")]
        public string CapPressingKey { get; set; } = "b";

        [JsonPropertyName("enableOscServer")]
        public bool EnableOscServer { get; set; } = false;

        [JsonPropertyName("oscServerIp")]
        public string OscServerIp { get; set; } = "127.0.0.1";

        [JsonPropertyName("oscServerPort")]
        public int OscServerPort { get; set; } = 9003;

        [JsonPropertyName("chatboxOscMixTemplate")]
        public string ChatboxOscMixTemplate { get; set; } = "{clientdata}{serverdata}";

        [JsonPropertyName("darkmode")]
        public bool DarkMode { get; set; } = false;

        [JsonPropertyName("transparentWindow")]
        public bool TransparentWindow { get; set; } = false;

        [JsonPropertyName("transparentWindowSize")]
        public string TransparentWindowSize { get; set; } = "large";

        [JsonPropertyName("overlayWidth")]
        public int OverlayWidth { get; set; } = 800;

        [JsonPropertyName("overlayHeight")]
        public int OverlayHeight { get; set; } = 500;

        [JsonPropertyName("mainWindowPinned")]
        public bool MainWindowPinned { get; set; } = true;

        [JsonPropertyName("mainWindowLeft")]
        public double? MainWindowLeft { get; set; }

        [JsonPropertyName("mainWindowTop")]
        public double? MainWindowTop { get; set; }

        [JsonPropertyName("uiLanguage")]
        public string UiLanguage { get; set; } = "zh-CN";

        [JsonPropertyName("hasSeenQuickStartGuide")]
        public bool HasSeenQuickStartGuide { get; set; }

        [JsonPropertyName("mainWindowLanguageMask")]
        public TranscriptLanguageMask MainWindowLanguageMask { get; set; } = TranscriptLanguageMask.All;

        [JsonPropertyName("filteremoji")]
        public string FilterEmoji { get; set; } = "false";

        [JsonPropertyName("translateServicecap")]
        public string TranslateServiceCap { get; set; } = "developer";

        [JsonPropertyName("capOutputStyle")]
        public int CapOutputStyle { get; set; } = 0;

        [JsonIgnore]
        public CaptionOutputStyle CapOutputStyleValue
        {
            get => Enum.IsDefined(typeof(CaptionOutputStyle), CapOutputStyle)
                ? (CaptionOutputStyle)CapOutputStyle
                : CaptionOutputStyle.Mixed;
            set => CapOutputStyle = (int)value;
        }

        [JsonPropertyName("openai_config")]
        public OpenAiConfig OpenAiConfig { get; set; } = new();

        [JsonPropertyName("openaiPresetTemplates")]
        public List<OpenAiPresetTemplate> OpenAiPresetTemplates { get; set; } = new();

        [JsonPropertyName("selectedOpenAiPreset")]
        public string SelectedOpenAiPreset { get; set; } = string.Empty;

        [JsonPropertyName("defaultScripts")]
        public List<VoiceScript> DefaultScripts { get; set; } = new();

        [JsonPropertyName("scripts")]
        public List<VoiceScript> Scripts { get; set; } = new();

        [JsonPropertyName("recognitionMode")]
        public string RecognitionMode { get; set; } = "remote"; // remote | local

        [JsonIgnore]
        public RecognitionBackendMode RecognitionBackendModeValue
        {
            get => string.Equals(RecognitionMode, "local", StringComparison.OrdinalIgnoreCase)
                ? RecognitionBackendMode.Local
                : RecognitionBackendMode.Remote;
            set => RecognitionMode = value == RecognitionBackendMode.Local ? "local" : "remote";
        }

        // Local recognition model type: 0 = Sherpa bilingual, 1 = SenseVoice
        [JsonPropertyName("localRecognitionModelType")]
        public int LocalRecognitionModelType { get; set; } = 0;

        [JsonIgnore]
        public LocalRecognitionModel LocalRecognitionModelValue
        {
            get => Enum.IsDefined(typeof(LocalRecognitionModel), LocalRecognitionModelType)
                ? (LocalRecognitionModel)LocalRecognitionModelType
                : LocalRecognitionModel.Sherpa;
            set => LocalRecognitionModelType = (int)value;
        }

        [JsonPropertyName("vadModelPath")]
        public string VadModelPath { get; set; } = string.Empty;

        // Root folder for bundled/preinstalled models (default: <exe>/pre-models).
        // It should contain subfolders for each model family.
        [JsonPropertyName("preModelsDir")]
        public string PreModelsDir { get; set; } = string.Empty;

        // VAD segmentation tuning (seconds).
        [JsonPropertyName("vadMinSilenceDuration")]
        public float VadMinSilenceDuration { get; set; } = 0.8f;

        // Optional per-route override for microphone VAD segmentation (seconds).
        [JsonPropertyName("micVadMinSilenceDuration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? MicVadMinSilenceDuration { get; set; }

        // Optional per-route override for desktop VAD segmentation (seconds).
        [JsonPropertyName("desktopVadMinSilenceDuration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? DesktopVadMinSilenceDuration { get; set; }

        // VAD speech confidence threshold (0-1).
        [JsonPropertyName("vadThreshold")]
        public float VadThreshold { get; set; } = 0.5f;

        // Optional per-route override for microphone VAD threshold (0-1).
        [JsonPropertyName("micVadThreshold")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? MicVadThreshold { get; set; }

        // Optional per-route override for desktop VAD threshold (0-1).
        [JsonPropertyName("desktopVadThreshold")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? DesktopVadThreshold { get; set; }

        // Maximum VAD segment length in seconds. Long speech will be split into chunks in the 1-60s range.
        [JsonPropertyName("vadMaxSegmentSeconds")]
        public float VadMaxSegmentSeconds { get; set; } = DefaultVadMaxSegmentSecondsValue;

        // Optional per-route override for microphone max VAD segment length (seconds).
        [JsonPropertyName("micVadMaxSegmentSeconds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? MicVadMaxSegmentSeconds { get; set; }

        // Optional per-route override for desktop max VAD segment length (seconds).
        [JsonPropertyName("desktopVadMaxSegmentSeconds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? DesktopVadMaxSegmentSeconds { get; set; }

        // Local streaming (mic) endpoint silence seconds when using Sherpa streaming without VAD.
        [JsonPropertyName("localStreamingEndpointSilenceSeconds")]
        public float LocalStreamingEndpointSilenceSeconds { get; set; } = 1.2f;

        [JsonPropertyName("sherpaModelDir")]
        public string SherpaModelDir { get; set; } = string.Empty;

        [JsonPropertyName("senseVoiceModelDir")]
        public string SenseVoiceModelDir { get; set; } = string.Empty;

        // Control recognition (local, resident model) + wake/command gating.
        [JsonPropertyName("controlEnabled")]
        public bool ControlEnabled { get; set; } = false;

        // Whether to play a feedback sound when a voice command is recognized.
        [JsonPropertyName("commandFeedbackEnabled")]
        public bool CommandFeedbackEnabled { get; set; } = true;

        // Whether to auto-start control recognition when the application starts.
        [JsonPropertyName("controlAutoStart")]
        public bool ControlAutoStart { get; set; } = true;

        // Control mic source mode: 0 = same as main mic, 1 = independent device.
        [JsonPropertyName("controlMicSourceMode")]
        public int ControlMicSourceMode { get; set; } = 0;

        [JsonIgnore]
        public ControlMicSourceType ControlMicSourceModeValue
        {
            get => Enum.IsDefined(typeof(ControlMicSourceType), ControlMicSourceMode)
                ? (ControlMicSourceType)ControlMicSourceMode
                : ControlMicSourceType.FollowMainMic;
            set => ControlMicSourceMode = (int)value;
        }

        // Independent mic device name for control recognition (used when ControlMicSourceMode = 1).
        [JsonPropertyName("controlMicName")]
        public string ControlMicName { get; set; } = "default";

        // Optional: dedicated model dir for control recognition. If empty, falls back to sherpaModelDir resolution.
        [JsonPropertyName("controlSherpaModelDir")]
        public string ControlSherpaModelDir { get; set; } = string.Empty;

        // If true, commands (other than wake word) require a recent wake event.
        [JsonPropertyName("controlRequireWakeForCommands")]
        public bool ControlRequireWakeForCommands { get; set; } = true;

        // Wake window length after hearing a wake word.
        [JsonPropertyName("controlWakeWindowSeconds")]
        public float ControlWakeWindowSeconds { get; set; } = 8.0f;

        /// <summary>
        /// Optional: configurable trigger keywords for built-in (default) voice commands.
        /// Changes require user confirmation (approval hash) before taking effect.
        /// </summary>
        [JsonPropertyName("voiceCommandKeywords")]
        public VoiceCommandKeywordConfig VoiceCommandKeywords { get; set; } = new();

        [JsonIgnore]
        public AudioConfigSection Audio => new(this);

        [JsonIgnore]
        public TranslationConfigSection Translation => new(this);

        [JsonIgnore]
        public TtsConfigSection Tts => new(this);

        [JsonIgnore]
        public RecognitionConfigSection Recognition => new(this);

        public bool NormalizeLegacyVadDefaults()
        {
            var changed = false;

            if (VadMaxSegmentSeconds <= 0f)
            {
                VadMaxSegmentSeconds = DefaultVadMaxSegmentSecondsValue;
                changed = true;
            }

            if (MicVadMaxSegmentSeconds.HasValue && MicVadMaxSegmentSeconds.Value <= 0f)
            {
                MicVadMaxSegmentSeconds = DefaultVadMaxSegmentSecondsValue;
                changed = true;
            }

            if (DesktopVadMaxSegmentSeconds.HasValue && DesktopVadMaxSegmentSeconds.Value <= 0f)
            {
                DesktopVadMaxSegmentSeconds = DefaultVadMaxSegmentSecondsValue;
                changed = true;
            }

            return changed;
        }

        public ConfigValidationResult Validate()
        {
            var errors = new List<string>();

            if (OscPort < 1 || OscPort > 65535)
                errors.Add($"OscPort must be 1-65535, current={OscPort}.");

            if (OscServerPort < 1 || OscServerPort > 65535)
                errors.Add($"OscServerPort must be 1-65535, current={OscServerPort}.");

            if (CustomThreshold < 0.001f || CustomThreshold > 0.99f)
                errors.Add($"CustomThreshold must be 0.001-0.99, current={CustomThreshold:F3}.");

            if (VadThreshold < 0.01f || VadThreshold > 0.99f)
                errors.Add($"VadThreshold must be 0.01-0.99, current={VadThreshold:F3}.");

            ValidateOptionalVadThreshold(MicVadThreshold, nameof(MicVadThreshold), errors);
            ValidateOptionalVadThreshold(DesktopVadThreshold, nameof(DesktopVadThreshold), errors);

            ValidateOptionalVadMinSilence(MicVadMinSilenceDuration, nameof(MicVadMinSilenceDuration), errors);
            ValidateOptionalVadMinSilence(DesktopVadMinSilenceDuration, nameof(DesktopVadMinSilenceDuration), errors);

            if (VadMaxSegmentSeconds < 1f || VadMaxSegmentSeconds > 60f)
                errors.Add($"VadMaxSegmentSeconds must be 1-60, current={VadMaxSegmentSeconds:F3}.");

            ValidateOptionalVadMaxSegment(MicVadMaxSegmentSeconds, nameof(MicVadMaxSegmentSeconds), errors);
            ValidateOptionalVadMaxSegment(DesktopVadMaxSegmentSeconds, nameof(DesktopVadMaxSegmentSeconds), errors);

            if (LocalStreamingEndpointSilenceSeconds < 0.8f || LocalStreamingEndpointSilenceSeconds > 3.0f)
                errors.Add($"LocalStreamingEndpointSilenceSeconds must be 0.8-3.0, current={LocalStreamingEndpointSilenceSeconds:F2}.");

            if (TtsSpeed < 0.5f || TtsSpeed > 2.0f)
                errors.Add($"TtsSpeed must be 0.5-2.0, current={TtsSpeed:F2}.");

            if (!Enum.IsDefined(typeof(VoiceTriggerMode), VoiceMode))
                errors.Add($"VoiceMode is invalid, current={VoiceMode}.");

            if (!Enum.IsDefined(typeof(VoiceTriggerMode), GameVoiceMode))
                errors.Add($"GameVoiceMode is invalid, current={GameVoiceMode}.");

            if (!Enum.IsDefined(typeof(TtsEngineMode), TtsEngine))
                errors.Add($"TtsEngine is invalid, current={TtsEngine}.");

            if (!Enum.IsDefined(typeof(TtsInterruptPolicy), TtsInterruptMode))
                errors.Add($"TtsInterruptMode is invalid, current={TtsInterruptMode}.");

            if (!Enum.IsDefined(typeof(VRCLS.Core.Models.QwenTtsToneMode), QwenTtsToneMode))
                errors.Add($"QwenTtsToneMode is invalid, current={QwenTtsToneMode}.");

            if (!Enum.IsDefined(typeof(LocalRecognitionModel), LocalRecognitionModelType))
                errors.Add($"LocalRecognitionModelType is invalid, current={LocalRecognitionModelType}.");

            if (!Enum.IsDefined(typeof(ControlMicSourceType), ControlMicSourceMode))
                errors.Add($"ControlMicSourceMode is invalid, current={ControlMicSourceMode}.");

            if (!Enum.IsDefined(typeof(CaptionOutputStyle), CapOutputStyle))
                errors.Add($"CapOutputStyle is invalid, current={CapOutputStyle}.");

            if (!Enum.IsDefined(typeof(VRCLS.Core.Models.SteamVrOverlayAnchorMode), SteamVrAnchorMode))
                errors.Add($"SteamVrAnchorMode is invalid, current={SteamVrAnchorMode}.");

            if (SteamVrSize < 0.05f || SteamVrSize > 0.5f)
                errors.Add($"SteamVrSize must be 0.05-0.5, current={SteamVrSize:F2}.");

            if (SteamVrNonTextOpacity < 0f || SteamVrNonTextOpacity > 1f)
                errors.Add($"SteamVrNonTextOpacity must be 0-1, current={SteamVrNonTextOpacity:F2}.");

            if (string.IsNullOrWhiteSpace(AppUpdateFeedUrl))
                errors.Add("AppUpdateFeedUrl is required.");
            else if (!Uri.TryCreate(AppUpdateFeedUrl, UriKind.Absolute, out _))
                errors.Add($"AppUpdateFeedUrl must be an absolute url, current='{AppUpdateFeedUrl}'.");

            if (string.IsNullOrWhiteSpace(UpdateServerUrl))
                errors.Add("UpdateServerUrl is required.");
            else if (!Uri.TryCreate(UpdateServerUrl, UriKind.Absolute, out _))
                errors.Add($"UpdateServerUrl must be an absolute url, current='{UpdateServerUrl}'.");

            if (!string.Equals(RecognitionMode, "remote", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(RecognitionMode, "local", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"RecognitionMode must be 'remote' or 'local', current='{RecognitionMode}'.");
            }

            if (!string.IsNullOrWhiteSpace(VadModelPath) && !File.Exists(VadModelPath))
                errors.Add($"VadModelPath does not exist: {VadModelPath}");

            if (!string.IsNullOrWhiteSpace(SherpaModelDir) && !Directory.Exists(SherpaModelDir))
                errors.Add($"SherpaModelDir does not exist: {SherpaModelDir}");

            return new ConfigValidationResult(errors);
        }

        public VadRouteConfig ResolveVadRoute(string? source)
        {
            var isDesktop = string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase);
            var routeSource = isDesktop ? "Desktop" : "Mic";

            var minSilence = ClampVadMinSilence(isDesktop ? DesktopVadMinSilenceDuration ?? VadMinSilenceDuration : MicVadMinSilenceDuration ?? VadMinSilenceDuration);
            var threshold = ClampVadThreshold(isDesktop ? DesktopVadThreshold ?? VadThreshold : MicVadThreshold ?? VadThreshold);
            var maxSegment = ClampVadMaxSegment(isDesktop ? DesktopVadMaxSegmentSeconds ?? VadMaxSegmentSeconds : MicVadMaxSegmentSeconds ?? VadMaxSegmentSeconds);

            return new VadRouteConfig
            {
                Source = routeSource,
                Enabled = true,
                MinSilenceDuration = minSilence,
                Threshold = threshold,
                MaxSegmentSeconds = maxSegment
            };
        }

        private static float ClampVadThreshold(float value) => Math.Clamp(value, 0.01f, 0.99f);

        private static float ClampVadMinSilence(float value) => Math.Clamp(value, 0.05f, 5f);

        private static float ClampVadMaxSegment(float value) => Math.Clamp(value, 1f, 60f);

        private static void ValidateOptionalVadThreshold(float? value, string propertyName, List<string> errors)
        {
            if (value.HasValue && (value.Value < 0.01f || value.Value > 0.99f))
            {
                errors.Add($"{propertyName} must be 0.01-0.99, current={value.Value:F3}.");
            }
        }

        private static void ValidateOptionalVadMinSilence(float? value, string propertyName, List<string> errors)
        {
            if (value.HasValue && (value.Value < 0.05f || value.Value > 5f))
            {
                errors.Add($"{propertyName} must be 0.05-5.00, current={value.Value:F3}.");
            }
        }

        private static void ValidateOptionalVadMaxSegment(float? value, string propertyName, List<string> errors)
        {
            if (value.HasValue && (value.Value < 1f || value.Value > 60f))
            {
                errors.Add($"{propertyName} must be 1-60, current={value.Value:F3}.");
            }
        }

        public string ResolveTranslationEngine(string? routeHint = null)
        {
            var isDesktopRoute = IsDesktopTranslationRoute(routeHint);
            var preferred = NormalizeTranslationEngine(isDesktopRoute ? TranslateServiceCap : TranslateService);
            var shared = NormalizeTranslationEngine(TranslationServer);
            var alternate = NormalizeTranslationEngine(isDesktopRoute ? TranslateService : TranslateServiceCap);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            if (!string.IsNullOrWhiteSpace(shared) && shared != "developer")
            {
                return shared;
            }

            if (!string.IsNullOrWhiteSpace(alternate) && alternate != "developer")
            {
                return alternate;
            }

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            if (!string.IsNullOrWhiteSpace(shared))
            {
                return shared;
            }

            if (!string.IsNullOrWhiteSpace(alternate))
            {
                return alternate;
            }

            return "developer";
        }

        public static string NormalizeTtsProvider(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                TtsProviderQwen => TtsProviderQwen,
                TtsProviderEdge => TtsProviderEdge,
                _ => DefaultTtsProvider
            };
        }

        public string ResolvePrimaryTargetLanguage(string? routeHint = null)
        {
            if (IsTextSendRoute(routeHint))
            {
                var textSendPreferred = NormalizeTranslationLanguage(TextSendTargetTranslationLanguage);
                if (!string.IsNullOrWhiteSpace(textSendPreferred))
                {
                    return textSendPreferred;
                }

                return NormalizeTranslationLanguage(TargetTranslationLanguage);
            }

            var isDesktopRoute = IsDesktopTranslationRoute(routeHint);
            var preferred = NormalizeTranslationLanguage(isDesktopRoute ? DesktopTargetTranslationLanguage : MicTargetTranslationLanguage);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return NormalizeTranslationLanguage(TargetTranslationLanguage);
        }

        public string ResolveSourceLanguage(string? routeHint = null)
        {
            if (IsTextSendRoute(routeHint))
            {
                return "auto";
            }

            var isDesktopRoute = IsDesktopTranslationRoute(routeHint);
            var preferred = NormalizeRecognitionLanguage(isDesktopRoute ? DesktopSourceLanguage : MicSourceLanguage);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return NormalizeRecognitionLanguage(SourceLanguage);
        }

        public string ResolveSecondaryTargetLanguage(string? routeHint = null)
        {
            if (IsTextSendRoute(routeHint))
            {
                return NormalizeTranslationLanguage(TextSendTargetTranslationLanguage2);
            }

            var isDesktopRoute = IsDesktopTranslationRoute(routeHint);
            var preferred = NormalizeTranslationLanguage(isDesktopRoute ? DesktopTargetTranslationLanguage2 : MicTargetTranslationLanguage2);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return NormalizeTranslationLanguage(TargetTranslationLanguage2);
        }

        public string ResolveTertiaryTargetLanguage(string? routeHint = null)
        {
            if (IsTextSendRoute(routeHint))
            {
                return NormalizeTranslationLanguage(TextSendTargetTranslationLanguage3);
            }

            var isDesktopRoute = IsDesktopTranslationRoute(routeHint);
            var preferred = NormalizeTranslationLanguage(isDesktopRoute ? DesktopTargetTranslationLanguage3 : MicTargetTranslationLanguage3);
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            return NormalizeTranslationLanguage(TargetTranslationLanguage3);
        }

        public IReadOnlyList<string> ResolveEnabledTargetLanguages(string? routeHint = null)
        {
            var result = new List<string>(3);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfEnabled(string? language)
            {
                var normalized = NormalizeTranslationLanguage(language);
                if (string.IsNullOrWhiteSpace(normalized)
                    || string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(normalized))
                {
                    return;
                }

                result.Add(normalized);
            }

            AddIfEnabled(ResolvePrimaryTargetLanguage(routeHint));
            AddIfEnabled(ResolveSecondaryTargetLanguage(routeHint));
            AddIfEnabled(ResolveTertiaryTargetLanguage(routeHint));

            return result;
        }

        public bool HasEnabledTranslationTarget(string? routeHint = null)
        {
            return ResolveEnabledTargetLanguages(routeHint).Count > 0;
        }

        public bool UsesEmbeddedTranslation(string? routeHint = null)
        {
            return IsEmbeddedTranslatorEngine(ResolveTranslationEngine(routeHint));
        }

        public bool UsesOpenAiTranslation(string? routeHint = null)
        {
            return string.Equals(ResolveTranslationEngine(routeHint), "openai", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsEmbeddedTranslatorEngine(string? engine)
        {
            var normalized = NormalizeTranslationEngine(engine);
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "developer" || normalized == "openai")
            {
                return false;
            }

            if (SupportedTranslationEngines == null || SupportedTranslationEngines.Count == 0)
            {
                return normalized is "google" or "bing" or "baidu" or "yandex" or "deepl";
            }

            foreach (var item in SupportedTranslationEngines)
            {
                if (string.Equals(item?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeTranslationEngine(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizeTranslationLanguage(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
        }

        private static string NormalizeRecognitionLanguage(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "auto")
            {
                return "zh";
            }

            return normalized;
        }

        private static bool IsDesktopTranslationRoute(string? routeHint)
        {
            if (string.IsNullOrWhiteSpace(routeHint))
            {
                return false;
            }

            var normalized = routeHint.Trim().ToLowerInvariant();
            return normalized.Contains("desktop")
                || normalized.Contains("cap")
                || normalized.Contains("capture");
        }

        private static bool IsTextSendRoute(string? routeHint)
        {
            if (string.IsNullOrWhiteSpace(routeHint))
            {
                return false;
            }

            return string.Equals(routeHint.Trim(), "TextSend", StringComparison.OrdinalIgnoreCase);
        }

        public sealed class ConfigValidationResult
        {
            public ConfigValidationResult(IReadOnlyList<string> errors)
            {
                Errors = errors;
            }

            public IReadOnlyList<string> Errors { get; }
            public bool IsValid => Errors.Count == 0;
        }
    }

    public class UserInfo
    {
        public const string AuthModeAnonymousTest = "anonymous_test";
        public const string AuthModeWebsiteAuthorized = "website_authorized";
        public const string AuthModeLegacyCredentials = "legacy_credentials";

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("auth_mode")]
        public string AuthMode { get; set; } = AuthModeAnonymousTest;

        [JsonPropertyName("user_uuid")]
        public string UserUuid { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("email_verified")]
        public bool EmailVerified { get; set; }

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonIgnore]
        public bool IsWebsiteAuthorized =>
            string.Equals(AuthMode, AuthModeWebsiteAuthorized, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsAnonymousTest =>
            string.Equals(AuthMode, AuthModeAnonymousTest, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsLegacyCredentials =>
            string.Equals(AuthMode, AuthModeLegacyCredentials, StringComparison.OrdinalIgnoreCase);

        public override string ToString()
        {
            return $"AuthMode: {AuthMode}, UserUuid: {(string.IsNullOrEmpty(UserUuid) ? "(empty)" : UserUuid)}, Username: {Username}, Token: {(string.IsNullOrEmpty(AccessToken) ? "(empty)" : "****")}";
        }
    }

    public class OpenAiConfig
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4/";

        [JsonPropertyName("model")]
        public string Model { get; set; } = "glm-4-flash";

        // OpenAI-compatible settings (optional).
        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.3f;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1000;

        // Optional JSON object merged into the OpenAI-compatible request body.
        [JsonPropertyName("extra_body")]
        public string ExtraBody { get; set; } = string.Empty;

        // If empty, a default translation prompt is used.
        // You may use placeholders: {sourceLang}, {targetLang}
        [JsonPropertyName("system_prompt")]
        public string SystemPrompt { get; set; } = string.Empty;
    }

    public class OpenAiPresetTemplate
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.3f;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1000;

        [JsonPropertyName("extra_body")]
        public string ExtraBody { get; set; } = string.Empty;

        [JsonPropertyName("system_prompt")]
        public string SystemPrompt { get; set; } = string.Empty;
    }

    public class VoiceScript
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public List<string> Text { get; set; } = new();

        [JsonPropertyName("vrcActions")]
        public List<VrcAction> VrcActions { get; set; } = new();
    }

    public class VrcAction
    {
        [JsonPropertyName("vrcPath")]
        public string VrcPath { get; set; } = string.Empty;

        [JsonPropertyName("vrcValue")]
        public object VrcValue { get; set; } = 0;

        [JsonPropertyName("vrcValueType")]
        public string VrcValueType { get; set; } = "float";

        [JsonPropertyName("sleeptime")]
        public float SleepTime { get; set; } = 0;
    }
}
