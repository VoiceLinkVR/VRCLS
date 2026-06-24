using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VRCLS.Core.Models;

namespace VRCLS.Core.Helpers
{
    public static class ConfigSnapshotLogParser
    {
        private static readonly Regex FieldRegex = new(
            @"(?<key>reason|algorithm|jsonBytes|compressedBytes|base64)=(?<value>\S+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex TimestampRegex = new(
            @"^\[(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static IReadOnlyList<ConfigSnapshotLogEntry> LoadFromLogInputs(IEnumerable<string>? inputs = null)
        {
            var logInputs = inputs?.Where(input => !string.IsNullOrWhiteSpace(input)).ToList();
            if (logInputs == null || logInputs.Count == 0)
            {
                logInputs = new List<string> { GetDefaultLogDirectory() };
            }

            var entries = new List<ConfigSnapshotLogEntry>();
            var index = 0;

            foreach (var logFile in ExpandLogFiles(logInputs))
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(logFile))
                {
                    lineNumber++;
                    if (!TryParseLine(line, logFile, lineNumber, ++index, out var entry))
                    {
                        index--;
                        continue;
                    }

                    entries.Add(entry);
                }
            }

            return entries;
        }

        public static IReadOnlyList<ConfigSnapshotLogEntry> ParseLogText(
            string? text,
            string source,
            int startIndex = 0)
        {
            var entries = new List<ConfigSnapshotLogEntry>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return entries;
            }

            using var reader = new StringReader(text);
            var lineNumber = 0;
            var index = startIndex;

            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (!TryParseLine(line, source, lineNumber, ++index, out var entry))
                {
                    index--;
                    continue;
                }

                entries.Add(entry);
            }

            return entries;
        }

        public static ConfigSnapshotLogEntry CreateBase64Entry(int index, string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ArgumentException("Snapshot base64 is required.", nameof(base64));
            }

            return new ConfigSnapshotLogEntry(
                Index: index,
                Source: "(base64)",
                LineNumber: 0,
                Timestamp: null,
                Reason: "direct",
                Algorithm: "GZip",
                JsonBytes: null,
                CompressedBytes: null,
                Base64: base64,
                RawLine: string.Empty);
        }

        public static string DecodeSnapshot(ConfigSnapshotLogEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (!string.Equals(entry.Algorithm, "GZip", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Algorithm))
            {
                throw new NotSupportedException($"Unsupported snapshot algorithm: {entry.Algorithm}");
            }

            return AppConfigStorage.DecodeCompressedConfigSnapshot(entry.Base64);
        }

        public static string GetDefaultLogDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS",
                "logs");
        }

        public static IEnumerable<string> ExpandLogFiles(IEnumerable<string> inputs)
        {
            foreach (var input in inputs)
            {
                var path = Environment.ExpandEnvironmentVariables(input);
                if (File.Exists(path))
                {
                    yield return Path.GetFullPath(path);
                    continue;
                }

                if (Directory.Exists(path))
                {
                    foreach (var file in Directory
                        .EnumerateFiles(path, "*.log", SearchOption.TopDirectoryOnly)
                        .OrderBy(File.GetLastWriteTimeUtc)
                        .ThenBy(file => file, StringComparer.OrdinalIgnoreCase))
                    {
                        yield return Path.GetFullPath(file);
                    }

                    continue;
                }

                throw new FileNotFoundException($"Log input was not found: {input}", input);
            }
        }

        private static bool TryParseLine(
            string line,
            string source,
            int lineNumber,
            int index,
            out ConfigSnapshotLogEntry entry)
        {
            entry = default!;
            if (!MayContainConfigSnapshot(line))
            {
                return false;
            }

            var fields = ParseFields(line);
            if (!fields.TryGetValue("base64", out var base64) || string.IsNullOrWhiteSpace(base64))
            {
                return false;
            }

            entry = new ConfigSnapshotLogEntry(
                Index: index,
                Source: source,
                LineNumber: lineNumber,
                Timestamp: TryParseTimestamp(line),
                Reason: GetField(fields, "reason"),
                Algorithm: GetField(fields, "algorithm"),
                JsonBytes: TryParseInt(GetField(fields, "jsonBytes")),
                CompressedBytes: TryParseInt(GetField(fields, "compressedBytes")),
                Base64: base64,
                RawLine: line);
            return true;
        }

        private static bool MayContainConfigSnapshot(string line)
        {
            return line.Contains("base64=", StringComparison.Ordinal)
                && (line.Contains("CONFIG_SNAPSHOT_V1", StringComparison.Ordinal)
                    || line.Contains("配置快照压缩数据已记录", StringComparison.Ordinal));
        }

        private static Dictionary<string, string> ParseFields(string line)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in FieldRegex.Matches(line))
            {
                var key = match.Groups["key"].Value;
                var value = match.Groups["value"].Value.Trim().TrimEnd(';', ',');
                fields[key] = value;
            }

            return fields;
        }

        private static string? GetField(IReadOnlyDictionary<string, string> fields, string key)
        {
            return fields.TryGetValue(key, out var value) ? value : null;
        }

        private static int? TryParseInt(string? text)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static DateTimeOffset? TryParseTimestamp(string line)
        {
            var match = TimestampRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                match.Groups["timestamp"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var timestamp)
                ? timestamp
                : null;
        }
    }
}
