using System.Text.Json;
using VRCLS.Core.Helpers;
using VRCLS.Core.Models;

namespace VRCLS.ConfigSnapshotTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            var snapshots = LoadSnapshots(options).ToList();
            if (snapshots.Count == 0)
            {
                Console.Error.WriteLine("No CONFIG_SNAPSHOT_V1 entries were found.");
                return 2;
            }

            if (options.ListOnly)
            {
                PrintSnapshotList(snapshots);
                return 0;
            }

            var selected = options.DecodeAll ? snapshots : [snapshots[^1]];
            WriteDecodedSnapshots(selected, options.OutputPath, options.DecodeAll);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static IEnumerable<SnapshotEntry> LoadSnapshots(Options options)
    {
        var index = 0;

        foreach (var base64 in options.Base64Values)
        {
            index++;
            yield return new SnapshotEntry(ConfigSnapshotLogParser.CreateBase64Entry(index, base64));
        }

        var logInputs = options.LogInputs.Count > 0
            ? options.LogInputs
            : [ConfigSnapshotLogParser.GetDefaultLogDirectory()];

        foreach (var snapshot in ConfigSnapshotLogParser.LoadFromLogInputs(logInputs))
        {
            index++;
            yield return new SnapshotEntry(snapshot with { Index = index });
        }
    }

    private static void PrintSnapshotList(IReadOnlyList<SnapshotEntry> snapshots)
    {
        Console.WriteLine("index\treason\talgorithm\tjsonBytes\tcompressedBytes\tbase64Chars\tsource\tline");
        foreach (var snapshot in snapshots)
        {
            Console.WriteLine(string.Join('\t', new[]
            {
                snapshot.Index.ToString(),
                snapshot.Reason ?? string.Empty,
                snapshot.Algorithm ?? string.Empty,
                snapshot.JsonBytes?.ToString() ?? string.Empty,
                snapshot.CompressedBytes?.ToString() ?? string.Empty,
                snapshot.Base64.Length.ToString(),
                snapshot.Source,
                snapshot.LineNumber == 0 ? string.Empty : snapshot.LineNumber.ToString()
            }));
        }
    }

    private static void WriteDecodedSnapshots(IReadOnlyList<SnapshotEntry> snapshots, string? outputPath, bool forceBatchOutput)
    {
        if (snapshots.Count == 1 && !forceBatchOutput)
        {
            var json = DecodeSnapshot(snapshots[0]);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                Console.WriteLine(json);
                return;
            }

            var targetPath = ResolveSingleOutputPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, json);
            Console.Error.WriteLine($"Wrote {targetPath}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var outputDirectory = Environment.ExpandEnvironmentVariables(outputPath);
            Directory.CreateDirectory(outputDirectory);

            foreach (var snapshot in snapshots)
            {
                var targetPath = Path.Combine(
                    outputDirectory,
                    $"config-snapshot-{snapshot.Index:0000}-{SanitizeFileName(snapshot.Reason ?? "unknown")}.json");
                File.WriteAllText(targetPath, DecodeSnapshot(snapshot));
                Console.Error.WriteLine($"Wrote {targetPath}");
            }

            return;
        }

        WriteJsonArrayToStdout(snapshots);
    }

    private static string DecodeSnapshot(SnapshotEntry snapshot)
    {
        return ConfigSnapshotLogParser.DecodeSnapshot(snapshot.Entry);
    }

    private static string ResolveSingleOutputPath(string outputPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(outputPath);
        if (Directory.Exists(expanded) || string.IsNullOrWhiteSpace(Path.GetExtension(expanded)))
        {
            return Path.Combine(expanded, "config-snapshot.json");
        }

        return Path.GetFullPath(expanded);
    }

    private static void WriteJsonArrayToStdout(IReadOnlyList<SnapshotEntry> snapshots)
    {
        using var output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();

        foreach (var snapshot in snapshots)
        {
            var json = DecodeSnapshot(snapshot);
            writer.WriteStartObject();
            writer.WriteNumber("index", snapshot.Index);
            writer.WriteString("source", snapshot.Source);
            if (snapshot.LineNumber > 0)
            {
                writer.WriteNumber("line", snapshot.LineNumber);
            }

            writer.WriteString("reason", snapshot.Reason);
            writer.WriteString("algorithm", snapshot.Algorithm);
            if (snapshot.JsonBytes.HasValue)
            {
                writer.WriteNumber("jsonBytes", snapshot.JsonBytes.Value);
            }

            if (snapshot.CompressedBytes.HasValue)
            {
                writer.WriteNumber("compressedBytes", snapshot.CompressedBytes.Value);
            }

            writer.WritePropertyName("config");
            using (var doc = JsonDocument.Parse(json))
            {
                doc.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        Console.WriteLine();
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '-');
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }


    private static void PrintUsage()
    {
        Console.WriteLine("""
VRCLS.ConfigSnapshotTool

Decode CONFIG_SNAPSHOT_V1 entries from VRCLS logs.

Usage:
  dotnet run --project tools/VRCLS.ConfigSnapshotTool -- [options]

Options:
  --log <file-or-dir>   Read one log file or all *.log files in a directory. Can be repeated.
  --base64 <value>      Decode one raw snapshot base64 value. Can be repeated.
  --all                 Decode every snapshot. Default decodes the latest snapshot.
  --out <file-or-dir>   Write decoded JSON. For --all this is treated as a directory.
  --list                List snapshot metadata without decoding JSON.
  --help                Show this help.

Examples:
  dotnet run --project tools/VRCLS.ConfigSnapshotTool --
  dotnet run --project tools/VRCLS.ConfigSnapshotTool -- --log "%APPDATA%\VRCLS\logs" --list
  dotnet run --project tools/VRCLS.ConfigSnapshotTool -- --log ".\vrcls-2026-06-07.log" --out config.json
  dotnet run --project tools/VRCLS.ConfigSnapshotTool -- --log "%APPDATA%\VRCLS\logs" --all --out decoded-configs
  dotnet run --project tools/VRCLS.ConfigSnapshotTool -- --base64 H4sIAAAAAAA...
""");
    }

    private sealed record SnapshotEntry(ConfigSnapshotLogEntry Entry)
    {
        public int Index => Entry.Index;
        public string Source => Entry.Source;
        public int LineNumber => Entry.LineNumber;
        public string? Reason => Entry.Reason;
        public string? Algorithm => Entry.Algorithm;
        public int? JsonBytes => Entry.JsonBytes;
        public int? CompressedBytes => Entry.CompressedBytes;
        public string Base64 => Entry.Base64;
    }

    private sealed class Options
    {
        public List<string> LogInputs { get; } = [];
        public List<string> Base64Values { get; } = [];
        public bool DecodeAll { get; private set; }
        public bool ListOnly { get; private set; }
        public bool ShowHelp { get; private set; }
        public string? OutputPath { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    case "--log":
                        options.LogInputs.Add(ReadValue(args, ref i, arg));
                        break;
                    case "--base64":
                        options.Base64Values.Add(ReadValue(args, ref i, arg));
                        break;
                    case "--all":
                        options.DecodeAll = true;
                        break;
                    case "--list":
                        options.ListOnly = true;
                        break;
                    case "--out":
                        options.OutputPath = ReadValue(args, ref i, arg);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {arg}");
                }
            }

            return options;
        }

        private static string ReadValue(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }

            index++;
            return args[index];
        }
    }
}
