using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    public sealed class TtsVoiceCloneProfileConfig
    {
        private List<string> _targetLanguages = new();

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("targetLanguages")]
        public List<string> TargetLanguages
        {
            get => _targetLanguages;
            set => _targetLanguages = value ?? new List<string>();
        }

        [JsonPropertyName("languages")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? LanguagesAlias
        {
            get => null;
            set
            {
                if (value != null && _targetLanguages.Count == 0)
                {
                    _targetLanguages = value;
                }
            }
        }

        [JsonPropertyName("referenceAudioPath")]
        public string ReferenceAudioPath { get; set; } = string.Empty;

        [JsonPropertyName("referenceText")]
        public string ReferenceText { get; set; } = string.Empty;

        [JsonPropertyName("cloneLanguage")]
        public string CloneLanguage { get; set; } = string.Empty;
    }
}
