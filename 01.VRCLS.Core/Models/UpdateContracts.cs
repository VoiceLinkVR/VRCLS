using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    public enum AppUpdateState
    {
        Idle,
        Checking,
        NoUpdate,
        UpdateAvailable,
        Downloading,
        ReadyToRestart,
        Applying,
        Failed
    }

    public sealed class AppUpdateInfo
    {
        [JsonPropertyName("targetVersion")]
        public string TargetVersion { get; set; } = string.Empty;

        [JsonPropertyName("displayVersion")]
        public string DisplayVersion { get; set; } = string.Empty;

        [JsonPropertyName("releaseNotes")]
        public string ReleaseNotes { get; set; } = string.Empty;

        [JsonPropertyName("downloadSizeBytes")]
        public long DownloadSizeBytes { get; set; }

        [JsonIgnore]
        public string ResolvedDisplayVersion => string.IsNullOrWhiteSpace(DisplayVersion) ? TargetVersion : DisplayVersion;
    }

    public sealed class StaticManifestIndex
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; } = 1;

        [JsonPropertyName("defaultChannel")]
        public string DefaultChannel { get; set; } = "stable";

        [JsonPropertyName("manifests")]
        public Dictionary<string, Dictionary<string, string>> Manifests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class StaticChannelManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("displayVersion")]
        public string DisplayVersion { get; set; } = string.Empty;

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;
    }

    public sealed class ModelCatalogResponse
    {
        [JsonPropertyName("models")]
        public List<ModelPackageInfo> Models { get; set; } = new();
    }

    public sealed class ModelPackageInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;

        [JsonPropertyName("installPath")]
        public string InstallPath { get; set; } = string.Empty;

        [JsonPropertyName("archiveRoot")]
        public string ArchiveRoot { get; set; } = string.Empty;

        [JsonPropertyName("required")]
        public bool Required { get; set; }
    }

    public sealed class TranslationProfileUpdateInfo
    {
        [JsonPropertyName("hasUpdate")]
        public bool HasUpdate { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public TranslationProfilePayload? Profile { get; set; }

        [JsonPropertyName("translators")]
        public TranslatorRuntimeUpdateInfo? Translators { get; set; }
    }

    public sealed class TranslationProfilePayload
    {
        [JsonPropertyName("translationServer")]
        public string TranslationServer { get; set; } = string.Empty;

        [JsonPropertyName("translateService")]
        public string TranslateService { get; set; } = string.Empty;

        [JsonPropertyName("translateServicecap")]
        public string TranslateServiceCap { get; set; } = string.Empty;

        [JsonPropertyName("sourceLanguage")]
        public string SourceLanguage { get; set; } = string.Empty;

        [JsonPropertyName("targetTranslationLanguage")]
        public string TargetTranslationLanguage { get; set; } = string.Empty;

        [JsonPropertyName("targetTranslationLanguage2")]
        public string TargetTranslationLanguage2 { get; set; } = string.Empty;

        [JsonPropertyName("targetTranslationLanguage3")]
        public string TargetTranslationLanguage3 { get; set; } = string.Empty;

        [JsonPropertyName("openai_config")]
        public OpenAiConfig? OpenAiConfig { get; set; }

        [JsonPropertyName("supportedTranslationEngines")]
        public List<string> SupportedTranslationEngines { get; set; } = new();

        [JsonPropertyName("openaiPresetTemplates")]
        public List<OpenAiPresetTemplate> OpenAiPresetTemplates { get; set; } = new();
    }

    public sealed class TranslatorRuntimeUpdateInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;
    }

    public sealed class ModelHotReloadEventArgs : EventArgs
    {
        public ModelHotReloadEventArgs(string reason, IReadOnlyList<string>? changedFiles = null)
        {
            Reason = reason ?? string.Empty;
            ChangedFiles = changedFiles ?? Array.Empty<string>();
        }

        public string Reason { get; }
        public IReadOnlyList<string> ChangedFiles { get; }
    }

    public sealed class TranslationProfileUpdatedEventArgs : EventArgs
    {
        public TranslationProfileUpdatedEventArgs(string version)
        {
            Version = version ?? string.Empty;
        }

        public string Version { get; }
    }
}
