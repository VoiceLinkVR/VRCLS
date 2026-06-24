using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    public sealed class TtsVoiceClonePresetConfig
    {
        private List<TtsVoiceCloneProfileConfig> _profiles = new();

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("profiles")]
        public List<TtsVoiceCloneProfileConfig> Profiles
        {
            get => _profiles;
            set => _profiles = value ?? new List<TtsVoiceCloneProfileConfig>();
        }
    }
}
