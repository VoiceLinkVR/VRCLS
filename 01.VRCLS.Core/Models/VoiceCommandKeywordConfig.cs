using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCLS.Core.Models
{
    /// <summary>
    /// Keyword overrides for built-in (default) voice commands.
    ///
    /// IMPORTANT: These settings are "two-phase":
    /// - Users may edit the keywords (e.g. by editing config.json or via UI).
    /// - The change only becomes effective after user confirmation, recorded by ApprovedHash.
    /// - If a hash is explicitly rejected, it is stored in RejectedHash to avoid re-prompting.
    /// </summary>
    public class VoiceCommandKeywordConfig
    {
        [JsonPropertyName("requireSwitchWord")]
        public bool RequireSwitchWord { get; set; } = true;

        // "切换" (or user override) to reduce false positives.
        [JsonPropertyName("switchWord")]
        public string SwitchWord { get; set; } = "切换";

        // Mode key -> keywords list. Valid built-in keys: text / translation / control / led
        [JsonPropertyName("modeSwitch")]
        public Dictionary<string, List<string>> ModeSwitch { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Language key -> keywords list. Valid built-in keys: en / ja / ru / ko
        [JsonPropertyName("languageSwitch")]
        public Dictionary<string, List<string>> LanguageSwitch { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Wake words for control recognition ("hotword wake-up").
        [JsonPropertyName("wakeWords")]
        public List<string> WakeWords { get; set; } = new();

        // Device control keywords (target -> keywords list). Built-in keys: mic / desktop
        [JsonPropertyName("deviceControl")]
        public Dictionary<string, List<string>> DeviceControl { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // When equals the current computed content hash, custom keywords are active.
        [JsonPropertyName("approvedHash")]
        public string ApprovedHash { get; set; } = string.Empty;

        // When equals the current computed content hash, the user already rejected it (do not prompt again).
        [JsonPropertyName("rejectedHash")]
        public string RejectedHash { get; set; } = string.Empty;

        public static VoiceCommandKeywordConfig CreateDefault()
        {
            return new VoiceCommandKeywordConfig
            {
                RequireSwitchWord = true,
                SwitchWord = "切换",
                ModeSwitch = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["text"] = new List<string> { "text", "文字模式", "文字发送" },
                    ["translation"] = new List<string> { "translation", "翻译模式", "翻译" },
                    ["control"] = new List<string> { "control", "控制模式", "控制" },
                    ["led"] = new List<string> { "led", "点阵屏模式", "点阵屏", "LED模式" },
                },
                LanguageSwitch = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = new List<string> { "英语", "英文" },
                    ["ja"] = new List<string> { "日语", "日文" },
                    ["ru"] = new List<string> { "俄语", "俄文" },
                    ["ko"] = new List<string> { "韩语", "韩文" },
                },
                WakeWords = new List<string> { "语音助手", "vrcls" },
                DeviceControl = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mic"] = new List<string> { "麦克风", "mic" },
                    ["desktop"] = new List<string> { "桌面", "系统音频", "desktop" }
                },
            };
        }

        /// <summary>
        /// Merge partial overrides into defaults (missing keys fall back to default keywords).
        /// This keeps the config backwards compatible and supports overriding only a subset.
        /// </summary>
        public static VoiceCommandKeywordConfig MergeWithDefaults(VoiceCommandKeywordConfig? overrides)
        {
            var d = CreateDefault();
            if (overrides == null) return d;

            d.RequireSwitchWord = overrides.RequireSwitchWord;
            if (!string.IsNullOrWhiteSpace(overrides.SwitchWord))
                d.SwitchWord = overrides.SwitchWord.Trim();

            if (overrides.ModeSwitch != null)
            {
                foreach (var kv in overrides.ModeSwitch)
                {
                    var key = (kv.Key ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    var list = NormalizeKeywords(kv.Value);
                    if (list.Count > 0) d.ModeSwitch[key] = list;
                }
            }

            if (overrides.LanguageSwitch != null)
            {
                foreach (var kv in overrides.LanguageSwitch)
                {
                    var key = (kv.Key ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    var list = NormalizeKeywords(kv.Value);
                    if (list.Count > 0) d.LanguageSwitch[key] = list;
                }
            }

            if (overrides.WakeWords != null)
            {
                var list = NormalizeKeywords(overrides.WakeWords);
                if (list.Count > 0) d.WakeWords = list;
            }

            if (overrides.DeviceControl != null)
            {
                foreach (var kv in overrides.DeviceControl)
                {
                    var key = (kv.Key ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    var list = NormalizeKeywords(kv.Value);
                    if (list.Count > 0) d.DeviceControl[key] = list;
                }
            }

            // Do not copy approval flags into the merged content config.
            d.ApprovedHash = overrides.ApprovedHash ?? string.Empty;
            d.RejectedHash = overrides.RejectedHash ?? string.Empty;
            return d;
        }

        public static string ComputeContentHash(VoiceCommandKeywordConfig? config)
        {
            // Hash over the merged config so "missing keys" behave as defaults.
            var merged = MergeWithDefaults(config);

            var normalized = new
            {
                requireSwitchWord = merged.RequireSwitchWord,
                switchWord = (merged.SwitchWord ?? string.Empty).Trim(),
                modeSwitch = ToSortedMap(merged.ModeSwitch),
                languageSwitch = ToSortedMap(merged.LanguageSwitch),
                wakeWords = NormalizeKeywords(merged.WakeWords).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
                deviceControl = ToSortedMap(merged.DeviceControl),
            };

            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var bytes = Encoding.UTF8.GetBytes(json);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static VoiceCommandKeywordConfig GetEffectiveConfig(VoiceCommandKeywordConfig? config)
        {
            var merged = MergeWithDefaults(config);
            var currentHash = ComputeContentHash(config);

            // If approved, use merged overrides; otherwise fall back to defaults.
            if (config != null && string.Equals(config.ApprovedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return merged;

            return CreateDefault();
        }

        public static bool IsPendingApproval(VoiceCommandKeywordConfig? config)
        {
            if (config == null) return false;
            var currentHash = ComputeContentHash(config);
            var defaultHash = ComputeContentHash(CreateDefault());
            if (string.Equals(currentHash, defaultHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(config.ApprovedHash, currentHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(config.RejectedHash, currentHash, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static SortedDictionary<string, string[]> ToSortedMap(Dictionary<string, List<string>>? map)
        {
            var sorted = new SortedDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (map == null) return sorted;

            foreach (var kv in map.OrderBy(k => k.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var key = (kv.Key ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(key)) continue;
                var list = NormalizeKeywords(kv.Value);
                sorted[key] = list.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return sorted;
        }

        private static List<string> NormalizeKeywords(IEnumerable<string>? keywords)
        {
            if (keywords == null) return new List<string>();

            return keywords
                .Select(s => (s ?? string.Empty).Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
