using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    public sealed class LanguageCatalogItem
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class TranslationRuntimeCapabilities
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("supportedRecognitionLanguages")]
        public List<LanguageCatalogItem> SupportedRecognitionLanguages { get; set; } = new();

        [JsonPropertyName("supportedTranslationLanguages")]
        public List<LanguageCatalogItem> SupportedTranslationLanguages { get; set; } = new();

        [JsonPropertyName("supportedTranslationEngines")]
        public List<string> SupportedTranslationEngines { get; set; } = new();
    }
}
