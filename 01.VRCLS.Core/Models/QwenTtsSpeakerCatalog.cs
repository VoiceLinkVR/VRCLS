using System;
using System.Collections.Generic;
using System.Linq;

namespace VRCLS.Core.Models
{
    public static class QwenTtsSpeakerCatalog
    {
        private static readonly QwenTtsSpeakerInfo[] _speakerInfos =
        {
            new("aiden", "en"),
            new("dylan", "en"),
            new("eric", "en"),
            new("ono_anna", "ja"),
            new("ryan", "en"),
            new("serena", "en"),
            new("sohee", "ko"),
            new("uncle_fu", "zh"),
            new("vivian", "zh")
        };

        private static readonly string[] _speakers = _speakerInfos.Select(item => item.Id).ToArray();
        private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ono-anna"] = "ono_anna",
            ["ono anna"] = "ono_anna",
            ["onoanna"] = "ono_anna",
            ["uncle-fu"] = "uncle_fu",
            ["uncle fu"] = "uncle_fu",
            ["unclefu"] = "uncle_fu",
            ["cherry"] = "vivian",
            ["chelsie"] = "vivian",
            ["ethan"] = "aiden",
            ["alloy"] = "vivian",
            ["ash"] = "vivian",
            ["ballad"] = "vivian",
            ["coral"] = "vivian",
            ["echo"] = "aiden",
            ["fable"] = "aiden",
            ["nova"] = "serena",
            ["onyx"] = "aiden",
            ["sage"] = "vivian",
            ["shimmer"] = "serena",
            ["verse"] = "vivian",
            ["jada"] = "vivian",
            ["sunny"] = "serena"
        };

        public const string DefaultSpeaker = "vivian";

        public static IReadOnlyList<string> Speakers => _speakers;

        public static bool IsSupportedSpeaker(string? speaker)
        {
            return TryNormalizeSpeaker(speaker, out _);
        }

        public static string NormalizeSpeakerOrDefault(string? speaker)
        {
            return TryNormalizeSpeaker(speaker, out var normalized)
                ? normalized
                : DefaultSpeaker;
        }

        public static string NormalizeSpeakerOrEmpty(string? speaker)
        {
            return TryNormalizeSpeaker(speaker, out var normalized)
                ? normalized
                : string.Empty;
        }

        public static bool TryNormalizeSpeaker(string? speaker, out string normalized)
        {
            var value = (speaker ?? string.Empty).Trim();
            foreach (var candidate in _speakers)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = candidate;
                    return true;
                }
            }

            if (_aliases.TryGetValue(value, out var alias)
                || _aliases.TryGetValue(value.Replace('_', '-'), out alias)
                || _aliases.TryGetValue(value.Replace('-', '_'), out alias)
                || _aliases.TryGetValue(value.Replace("_", string.Empty).Replace("-", string.Empty), out alias))
            {
                normalized = alias;
                return true;
            }

            normalized = string.Empty;
            return false;
        }

        public static bool TryGetLanguageCode(string? speaker, out string language)
        {
            if (TryNormalizeSpeaker(speaker, out var normalized))
            {
                foreach (var info in _speakerInfos)
                {
                    if (string.Equals(info.Id, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        language = info.Language;
                        return true;
                    }
                }
            }

            language = string.Empty;
            return false;
        }

        private sealed record QwenTtsSpeakerInfo(string Id, string Language);
    }
}
