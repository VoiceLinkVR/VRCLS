using System;

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
            set => _root.SourceLanguage = value ?? "zh";
        }

        public string TargetLanguage
        {
            get => _root.TargetTranslationLanguage;
            set => _root.TargetTranslationLanguage = value ?? "en";
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

        public string Provider
        {
            get => AppConfig.NormalizeTtsProvider(_root.TtsProvider);
            set => _root.TtsProvider = AppConfig.NormalizeTtsProvider(value);
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

        public string SherpaModelDir
        {
            get => _root.SherpaModelDir;
            set => _root.SherpaModelDir = value ?? string.Empty;
        }
    }
}
