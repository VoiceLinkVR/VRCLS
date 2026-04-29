using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VRCLS.UI.Services
{
    public static class EdgeTtsVoiceCatalogService
    {
        private static readonly string[] _fallbackVoices =
        {
            "zh-CN-XiaoxiaoNeural",
            "en-US-JennyNeural",
            "ja-JP-NanamiNeural",
            "ko-KR-SunHiNeural",
            "fr-FR-DeniseNeural",
            "de-DE-KatjaNeural",
            "es-ES-ElviraNeural",
            "ru-RU-SvetlanaNeural"
        };

        public static async Task<IReadOnlyList<string>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            var loaded = await TryLoadVoicesFromJsonAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Count > 0)
            {
                return loaded;
            }

            return _fallbackVoices;
        }

        private static async Task<IReadOnlyList<string>> TryLoadVoicesFromJsonAsync(CancellationToken cancellationToken)
        {
            foreach (var path in GetCandidateJsonPaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    var parsed = ParseVoiceJson(json);
                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // ignore and continue to the next candidate
                }
            }

            return Array.Empty<string>();
        }

        private static IReadOnlyList<string> ParseVoiceJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<string>();
            }

            using var doc = JsonDocument.Parse(json);
            JsonElement voiceArray;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                voiceArray = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("voices", out var voicesProperty)
                && voicesProperty.ValueKind == JsonValueKind.Array)
            {
                voiceArray = voicesProperty;
            }
            else
            {
                return Array.Empty<string>();
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in voiceArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var voice = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(voice))
                {
                    set.Add(voice);
                }
            }

            return set.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IEnumerable<string> GetCandidateJsonPaths()
        {
            // Preferred packaged path (copied by csproj).
            yield return Path.Combine(AppContext.BaseDirectory, "data", "edge_tts_voices.json");
            // Compatibility path in case packaging path changes.
            yield return Path.Combine(AppContext.BaseDirectory, "edge_tts_voices.json");
        }
    }
}
