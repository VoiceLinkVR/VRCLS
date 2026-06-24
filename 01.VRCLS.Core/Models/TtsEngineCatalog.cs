using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    public sealed class TtsEngineCatalog
    {
        [JsonPropertyName("audio_transport")]
        public TtsAudioTransportInfo? AudioTransport { get; set; }

        [JsonPropertyName("engines")]
        public Dictionary<string, TtsEngineInfo> Engines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public TtsEngineInfo? GetEngine(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider) || Engines.Count == 0)
            {
                return null;
            }

            if (Engines.TryGetValue(provider.Trim(), out var exact))
            {
                return exact;
            }

            foreach (var pair in Engines)
            {
                if (string.Equals(pair.Key, provider.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }
    }

    public sealed class TtsEngineInfo
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("service_enabled")]
        public bool? ServiceEnabled { get; set; }

        [JsonPropertyName("speech_enabled")]
        public bool? SpeechEnabled { get; set; }

        [JsonPropertyName("clone_enabled")]
        public bool? CloneEnabled { get; set; }

        [JsonPropertyName("builtin_voices_enabled")]
        public bool? BuiltinVoicesEnabled { get; set; }

        [JsonPropertyName("speech_path")]
        public string? SpeechPath { get; set; }

        [JsonPropertyName("v1_speech_path")]
        public string? LegacySpeechPath { get; set; }

        [JsonPropertyName("clone_path")]
        public string? ClonePath { get; set; }

        [JsonPropertyName("voices_path")]
        public string? VoicesPath { get; set; }

        [JsonPropertyName("languages_path")]
        public string? LanguagesPath { get; set; }

        [JsonPropertyName("supports_clone")]
        public bool? SupportsClone { get; set; }

        [JsonPropertyName("supports_opus")]
        public bool? SupportsOpus { get; set; }

        [JsonIgnore]
        public bool IsSpeechEnabled => SpeechEnabled ?? Enabled ?? ServiceEnabled ?? true;

        [JsonIgnore]
        public bool IsCloneEnabled => CloneEnabled ?? (SupportsClone == false ? false : (Enabled ?? ServiceEnabled ?? true));
    }

    public sealed class TtsAudioTransportInfo
    {
        [JsonPropertyName("legacy_compat")]
        public bool? LegacyCompat { get; set; }

        [JsonPropertyName("opus_streaming_supported")]
        public bool? OpusStreamingSupported { get; set; }

        [JsonPropertyName("opus_encoding")]
        public string? OpusEncoding { get; set; }

        [JsonPropertyName("upgrade_required_when_legacy_compat_false")]
        public bool? UpgradeRequiredWhenLegacyCompatFalse { get; set; }

        [JsonIgnore]
        public bool SupportsLengthPrefixedOpus =>
            OpusStreamingSupported == true
            && string.Equals(OpusEncoding, "length_prefixed_opus", StringComparison.OrdinalIgnoreCase);
    }
}
