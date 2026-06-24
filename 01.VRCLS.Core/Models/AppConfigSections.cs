using System;
using System.Collections.Generic;

namespace VRCLS.Core.Models
{
    // Thin grouped views over legacy flat config fields.
    // They keep JSON compatibility while reducing "god object" usage in services.
    public sealed class AudioConfigSection
    {
        private readonly AppConfig _root;

        internal AudioConfigSection(AppConfig root) => _root = root;

        public string MicName
        {
            get => _root.MicName;
            set => _root.MicName = value ?? "default";
        }

        public VoiceTriggerMode VoiceMode
        {
            get => _root.VoiceTriggerModeValue;
            set => _root.VoiceTriggerModeValue = value;
        }

        public VoiceTriggerMode DesktopVoiceMode
        {
            get => _root.GameVoiceTriggerModeValue;
            set => _root.GameVoiceTriggerModeValue = value;
        }

        public bool DynamicThreshold
        {
            get => _root.DynamicThreshold;
            set => _root.DynamicThreshold = value;
        }

        public float CustomThreshold
        {
            get => _root.CustomThreshold;
            set => _root.CustomThreshold = value;
        }

        public float VadThreshold
        {
            get => _root.VadThreshold;
            set => _root.VadThreshold = value;
        }

        public float VadMinSilenceDuration
        {
            get => _root.VadMinSilenceDuration;
            set => _root.VadMinSilenceDuration = value;
        }
    }

    public sealed class TranslationConfigSection
    {
        private readonly AppConfig _root;

        internal TranslationConfigSection(AppConfig root) => _root = root;

        public string SourceLanguage
        {
            get => _root.SourceLanguage;
            set => _root.SourceLanguage = value ?? AppConfig.DefaultMicSourceLanguage;
        }

        public string TargetLanguage
        {
            get => _root.TargetTranslationLanguage;
            set => _root.TargetTranslationLanguage = value ?? AppConfig.DefaultMicTargetTranslationLanguage;
        }

        public string Engine
        {
            get => _root.TranslationServer;
            set => _root.TranslationServer = value ?? "developer";
        }
    }

    public sealed class TtsConfigSection
    {
        private readonly AppConfig _root;

        internal TtsConfigSection(AppConfig root) => _root = root;

        public TtsEngineMode Engine
        {
            get => _root.TtsEngineModeValue;
            set => _root.TtsEngineModeValue = value;
        }

        public TtsInterruptPolicy InterruptPolicy
        {
            get => _root.TtsInterruptPolicyValue;
            set => _root.TtsInterruptPolicyValue = value;
        }

        public float Speed
        {
            get => _root.TtsSpeed;
            set => _root.TtsSpeed = value;
        }

        public float Volume
        {
            get => _root.TtsVolume;
            set => _root.TtsVolume = value;
        }

        public string Provider
        {
            get => AppConfig.NormalizeTtsProvider(_root.TtsProvider);
            set => _root.TtsProvider = AppConfig.NormalizeTtsProvider(value);
        }

        public string EdgeVoiceName
        {
            get => _root.TtsVoiceName;
            set => _root.TtsVoiceName = value ?? string.Empty;
        }

        public string QwenVoiceName
        {
            get => _root.QwenTtsVoiceName;
            set => _root.QwenTtsVoiceName = value ?? string.Empty;
        }

        public string MossVoiceName
        {
            get => _root.MossTtsVoiceName;
            set => _root.MossTtsVoiceName = value ?? string.Empty;
        }

        public string OmniVoiceVoiceName
        {
            get => _root.OmniVoiceTtsVoiceName;
            set => _root.OmniVoiceTtsVoiceName = value ?? string.Empty;
        }

        public QwenTtsToneMode QwenToneMode
        {
            get => _root.QwenTtsToneModeValue;
            set => _root.QwenTtsToneModeValue = value;
        }

        public string QwenInstruct
        {
            get => _root.QwenTtsInstruct;
            set => _root.QwenTtsInstruct = value ?? string.Empty;
        }

        public QwenTtsVoiceMode QwenVoiceMode
        {
            get => _root.QwenTtsVoiceModeValue;
            set => _root.QwenTtsVoiceModeValue = value;
        }

        public string QwenCloneReferenceAudioPath
        {
            get => _root.QwenTtsCloneReferenceAudioPath;
            set => _root.QwenTtsCloneReferenceAudioPath = value ?? string.Empty;
        }

        public string QwenCloneReferenceText
        {
            get => _root.QwenTtsCloneReferenceText;
            set => _root.QwenTtsCloneReferenceText = value ?? string.Empty;
        }

        public string QwenCloneLanguage
        {
            get => string.IsNullOrWhiteSpace(_root.QwenTtsCloneLanguage)
                ? AppConfig.DefaultQwenTtsCloneLanguage
                : _root.QwenTtsCloneLanguage;
            set => _root.QwenTtsCloneLanguage = string.IsNullOrWhiteSpace(value)
                ? AppConfig.DefaultQwenTtsCloneLanguage
                : value.Trim();
        }

        public MossTtsVoiceMode MossVoiceMode
        {
            get => _root.MossTtsVoiceModeValue;
            set => _root.MossTtsVoiceModeValue = value;
        }

        public string MossCloneReferenceAudioPath
        {
            get => _root.MossTtsCloneReferenceAudioPath;
            set => _root.MossTtsCloneReferenceAudioPath = value ?? string.Empty;
        }

        public string MossCloneLanguage
        {
            get => string.IsNullOrWhiteSpace(_root.MossTtsCloneLanguage)
                ? AppConfig.DefaultMossTtsCloneLanguage
                : _root.MossTtsCloneLanguage;
            set => _root.MossTtsCloneLanguage = string.IsNullOrWhiteSpace(value)
                ? AppConfig.DefaultMossTtsCloneLanguage
                : value.Trim();
        }

        public OmniVoiceTtsVoiceMode OmniVoiceVoiceMode
        {
            get => _root.OmniVoiceTtsVoiceModeValue;
            set => _root.OmniVoiceTtsVoiceModeValue = value;
        }

        public string OmniVoiceCloneReferenceAudioPath
        {
            get => _root.OmniVoiceTtsCloneReferenceAudioPath;
            set => _root.OmniVoiceTtsCloneReferenceAudioPath = value ?? string.Empty;
        }

        public string OmniVoiceCloneReferenceText
        {
            get => _root.OmniVoiceTtsCloneReferenceText;
            set => _root.OmniVoiceTtsCloneReferenceText = value ?? string.Empty;
        }

        public string OmniVoiceCloneLanguage
        {
            get => string.IsNullOrWhiteSpace(_root.OmniVoiceTtsCloneLanguage)
                ? AppConfig.DefaultOmniVoiceTtsCloneLanguage
                : _root.OmniVoiceTtsCloneLanguage;
            set => _root.OmniVoiceTtsCloneLanguage = string.IsNullOrWhiteSpace(value)
                ? AppConfig.DefaultOmniVoiceTtsCloneLanguage
                : value.Trim();
        }

        public IList<TtsVoiceCloneProfileConfig> VoiceCloneProfiles => _root.TtsVoiceCloneProfiles;

        public IList<TtsVoiceClonePresetConfig> VoiceClonePresets => _root.TtsVoiceClonePresets;

        public string VoiceClonePresetId
        {
            get => _root.TtsVoiceClonePresetId;
            set => _root.TtsVoiceClonePresetId = value?.Trim() ?? string.Empty;
        }
    }

    public sealed class RecognitionConfigSection
    {
        private readonly AppConfig _root;

        internal RecognitionConfigSection(AppConfig root) => _root = root;

        public RecognitionBackendMode Backend
        {
            get => _root.RecognitionBackendModeValue;
            set => _root.RecognitionBackendModeValue = value;
        }

        public LocalRecognitionModel LocalModel
        {
            get => _root.LocalRecognitionModelValue;
            set => _root.LocalRecognitionModelValue = value;
        }

        public string MicChineseAsrEngine
        {
            get => _root.ResolveChineseAsrEngine("Mic");
            set => _root.MicChineseAsrEngine = AppConfig.NormalizeChineseAsrEngine(value);
        }

        public string DesktopChineseAsrEngine
        {
            get => _root.ResolveChineseAsrEngine("Desktop");
            set => _root.DesktopChineseAsrEngine = AppConfig.NormalizeChineseAsrEngine(value);
        }

        public string SherpaModelDir
        {
            get => _root.SherpaModelDir;
            set => _root.SherpaModelDir = value ?? string.Empty;
        }
    }
}
