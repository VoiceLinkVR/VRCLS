using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VRCLS.Core.Models;

namespace VRCLS.UI.Services
{
    public static class TtsVoiceClonePresetPackageService
    {
        private const string ManifestFileName = "manifest.json";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static string UserPresetRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS",
                "voice_clone_presets");

        public static void ExportPreset(TtsVoiceClonePresetConfig preset, string destinationPackagePath)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            if (string.IsNullOrWhiteSpace(destinationPackagePath))
            {
                throw new ArgumentException("导出路径不能为空。", nameof(destinationPackagePath));
            }

            var tempRoot = CreateTempDirectory("vrcls_voice_preset_export_");
            try
            {
                var exportPreset = ClonePreset(preset);
                exportPreset.Enabled = true;
                ValidatePresetHasEnabledAutoProfile(exportPreset, "导出的音色预设");

                var audioDirectory = Path.Combine(tempRoot, "audio");
                Directory.CreateDirectory(audioDirectory);

                var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < exportPreset.Profiles.Count; index++)
                {
                    var profile = exportPreset.Profiles[index];
                    if (string.IsNullOrWhiteSpace(profile.ReferenceAudioPath))
                    {
                        continue;
                    }

                    var sourcePath = ResolveReferenceAudioPath(profile.ReferenceAudioPath);
                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException($"参考音频文件不存在：{sourcePath}", sourcePath);
                    }

                    var packageFileName = CreateUniquePackageFileName(sourcePath, index + 1, usedFileNames);
                    var targetPath = Path.Combine(audioDirectory, packageFileName);
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    profile.ReferenceAudioPath = $"audio/{packageFileName}";
                }

                var manifestPath = Path.Combine(tempRoot, ManifestFileName);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(exportPreset, JsonOptions), Utf8NoBom);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPackagePath) ?? ".");
                if (File.Exists(destinationPackagePath))
                {
                    File.Delete(destinationPackagePath);
                }

                ZipFile.CreateFromDirectory(tempRoot, destinationPackagePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        public static TtsVoiceClonePresetConfig ImportPresetPackage(
            string packagePath,
            IEnumerable<string> existingPresetIds)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                throw new FileNotFoundException("预设包文件不存在。", packagePath);
            }

            var tempRoot = CreateTempDirectory("vrcls_voice_preset_import_");
            try
            {
                ExtractZipSafely(packagePath, tempRoot);

                var manifestPath = ResolveManifestPath(tempRoot);
                var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? tempRoot;
                var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                var preset = JsonSerializer.Deserialize<TtsVoiceClonePresetConfig>(manifestJson, JsonOptions)
                    ?? throw new InvalidDataException("预设包 manifest.json 为空。");

                NormalizeImportedPreset(preset, existingPresetIds);
                ValidateImportedPresetAudio(preset, manifestDirectory);

                var destinationDirectory = ResolveUniqueInstalledDirectory(preset.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory) ?? UserPresetRoot);
                CopyDirectory(manifestDirectory, destinationDirectory);

                foreach (var profile in preset.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(profile.ReferenceAudioPath))
                    {
                        continue;
                    }

                    var relativePath = NormalizePackageRelativePath(profile.ReferenceAudioPath);
                    var installedPath = Path.GetFullPath(Path.Combine(destinationDirectory, relativePath));
                    EnsurePathInside(installedPath, destinationDirectory);
                    profile.ReferenceAudioPath = installedPath;
                }

                File.WriteAllText(
                    Path.Combine(destinationDirectory, ManifestFileName),
                    JsonSerializer.Serialize(ToRelativePresetManifest(preset, destinationDirectory), JsonOptions),
                    Utf8NoBom);

                return preset;
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        public static string NormalizePresetId(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else if (ch == '-' || ch == '_' || ch == '.')
                {
                    builder.Append(ch);
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    builder.Append('-');
                }
            }

            return builder.ToString().Trim('-', '_', '.');
        }

        public static string ResolveReferenceAudioPath(string path)
        {
            var trimmed = (path ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            return Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
        }

        private static void NormalizeImportedPreset(
            TtsVoiceClonePresetConfig preset,
            IEnumerable<string> existingPresetIds)
        {
            var name = (preset.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "导入音色预设";
            }

            var baseId = NormalizePresetId(preset.Id);
            if (string.IsNullOrWhiteSpace(baseId))
            {
                baseId = NormalizePresetId(name);
            }

            if (string.IsNullOrWhiteSpace(baseId))
            {
                baseId = "voice-preset";
            }

            var usedIds = new HashSet<string>(existingPresetIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var candidate = baseId;
            var duplicateIndex = 2;
            while (usedIds.Contains(candidate) || Directory.Exists(Path.Combine(UserPresetRoot, candidate)))
            {
                candidate = $"{baseId}-{duplicateIndex++}";
            }

            preset.Id = candidate;
            preset.Name = name;
            preset.Enabled = true;
            preset.Profiles = (preset.Profiles ?? new List<TtsVoiceCloneProfileConfig>())
                .Where(profile => profile != null)
                .Select(profile =>
                {
                    var targetLanguages = (profile.TargetLanguages ?? new List<string>())
                        .Where(language => !string.IsNullOrWhiteSpace(language))
                        .Select(language => language.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (targetLanguages.Count == 0)
                    {
                        targetLanguages.Add("*");
                    }

                    return new TtsVoiceCloneProfileConfig
                    {
                        Enabled = profile.Enabled,
                        Name = (profile.Name ?? string.Empty).Trim(),
                        Provider = (profile.Provider ?? string.Empty).Trim(),
                        TargetLanguages = targetLanguages,
                        ReferenceAudioPath = (profile.ReferenceAudioPath ?? string.Empty).Trim(),
                        ReferenceText = (profile.ReferenceText ?? string.Empty).Trim(),
                        CloneLanguage = (profile.CloneLanguage ?? string.Empty).Trim()
                    };
                })
                .ToList();

            if (preset.Profiles.Count == 0)
            {
                throw new InvalidDataException("预设包至少需要一条参考音频配置。");
            }
        }

        private static void ValidateImportedPresetAudio(TtsVoiceClonePresetConfig preset, string manifestDirectory)
        {
            var enabledProfiles = ValidatePresetHasEnabledAutoProfile(preset, "预设包");

            for (var index = 0; index < enabledProfiles.Count; index++)
            {
                var profile = enabledProfiles[index];
                if (string.IsNullOrWhiteSpace(profile.ReferenceAudioPath))
                {
                    throw new InvalidDataException($"第 {index + 1} 条参考配置缺少参考音频。");
                }

                var relativePath = NormalizePackageRelativePath(profile.ReferenceAudioPath);
                var audioPath = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath));
                EnsurePathInside(audioPath, manifestDirectory);
                if (!File.Exists(audioPath))
                {
                    throw new FileNotFoundException($"预设包内缺少参考音频：{relativePath}", audioPath);
                }

                if (IsExplicitQwenProfile(profile)
                    && string.IsNullOrWhiteSpace(profile.ReferenceText))
                {
                    throw new InvalidDataException($"第 {index + 1} 条 Qwen-TTS 参考配置缺少参考音频文本。");
                }
            }
        }

        private static List<TtsVoiceCloneProfileConfig> ValidatePresetHasEnabledAutoProfile(
            TtsVoiceClonePresetConfig preset,
            string subject)
        {
            var enabledProfiles = (preset.Profiles ?? new List<TtsVoiceCloneProfileConfig>())
                .Where(profile => profile != null && profile.Enabled)
                .ToList();
            if (enabledProfiles.Count == 0)
            {
                throw new InvalidDataException($"{subject}至少需要一条启用的参考音频配置。");
            }

            if (!enabledProfiles.Any(HasAutoTargetLanguage))
            {
                throw new InvalidDataException($"{subject}至少需要一条适用语言为自动语言的启用参考配置。");
            }

            return enabledProfiles;
        }

        private static bool HasAutoTargetLanguage(TtsVoiceCloneProfileConfig profile)
        {
            return profile.TargetLanguages == null
                || profile.TargetLanguages.Count == 0
                || profile.TargetLanguages.Any(IsAutoTargetLanguage);
        }

        private static bool IsAutoTargetLanguage(string? language)
        {
            var normalized = (language ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "*", StringComparison.Ordinal)
                || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitQwenProfile(TtsVoiceCloneProfileConfig profile)
        {
            var provider = (profile.Provider ?? string.Empty).Trim().ToLowerInvariant();
            return provider is AppConfig.TtsProviderQwen or "qwen-tts";
        }

        private static TtsVoiceClonePresetConfig ToRelativePresetManifest(
            TtsVoiceClonePresetConfig preset,
            string installedDirectory)
        {
            var clone = ClonePreset(preset);
            foreach (var profile in clone.Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.ReferenceAudioPath))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(profile.ReferenceAudioPath);
                EnsurePathInside(fullPath, installedDirectory);
                profile.ReferenceAudioPath = Path.GetRelativePath(installedDirectory, fullPath).Replace('\\', '/');
            }

            return clone;
        }

        private static TtsVoiceClonePresetConfig ClonePreset(TtsVoiceClonePresetConfig preset)
        {
            return new TtsVoiceClonePresetConfig
            {
                Id = (preset.Id ?? string.Empty).Trim(),
                Name = (preset.Name ?? string.Empty).Trim(),
                Enabled = preset.Enabled,
                Profiles = (preset.Profiles ?? new List<TtsVoiceCloneProfileConfig>())
                    .Where(profile => profile != null)
                    .Select(profile => new TtsVoiceCloneProfileConfig
                    {
                        Enabled = profile.Enabled,
                        Name = profile.Name ?? string.Empty,
                        Provider = profile.Provider ?? string.Empty,
                        TargetLanguages = profile.TargetLanguages?.ToList() ?? new List<string>(),
                        ReferenceAudioPath = profile.ReferenceAudioPath ?? string.Empty,
                        ReferenceText = profile.ReferenceText ?? string.Empty,
                        CloneLanguage = profile.CloneLanguage ?? string.Empty
                    })
                    .ToList()
            };
        }

        private static string ResolveUniqueInstalledDirectory(string presetId)
        {
            Directory.CreateDirectory(UserPresetRoot);
            var directory = Path.Combine(UserPresetRoot, presetId);
            if (!Directory.Exists(directory))
            {
                return directory;
            }

            var index = 2;
            while (true)
            {
                var candidate = Path.Combine(UserPresetRoot, $"{presetId}-{index++}");
                if (!Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string ResolveManifestPath(string extractedRoot)
        {
            var rootManifest = Path.Combine(extractedRoot, ManifestFileName);
            if (File.Exists(rootManifest))
            {
                return rootManifest;
            }

            var manifests = Directory
                .EnumerateFiles(extractedRoot, ManifestFileName, SearchOption.AllDirectories)
                .ToList();
            if (manifests.Count == 0)
            {
                throw new InvalidDataException("预设包缺少 manifest.json。");
            }

            if (manifests.Count > 1)
            {
                throw new InvalidDataException("预设包包含多个 manifest.json，请只保留一个预设清单。");
            }

            return manifests[0];
        }

        private static void ExtractZipSafely(string packagePath, string destinationDirectory)
        {
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FullName) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var normalizedEntryName = entry.FullName.Replace('\\', '/');
                if (Path.IsPathRooted(normalizedEntryName)
                    || normalizedEntryName.Split('/').Any(part => part == ".."))
                {
                    throw new InvalidDataException($"预设包包含非法路径：{entry.FullName}");
                }

                var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, normalizedEntryName));
                EnsurePathInside(targetPath, destinationDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? destinationDirectory);
                entry.ExtractToFile(targetPath, overwrite: false);
            }
        }

        private static string NormalizePackageRelativePath(string path)
        {
            var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized)
                || Path.IsPathRooted(normalized)
                || normalized.Split('/').Any(part => part == ".."))
            {
                throw new InvalidDataException($"预设包包含非法参考音频路径：{path}");
            }

            return normalized;
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relativeDirectory));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativeFile = Path.GetRelativePath(sourceDirectory, file);
                var targetFile = Path.Combine(destinationDirectory, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile) ?? destinationDirectory);
                File.Copy(file, targetFile, overwrite: true);
            }
        }

        private static void EnsurePathInside(string path, string root)
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"路径超出预设包目录：{path}");
            }
        }

        private static string CreateUniquePackageFileName(
            string sourcePath,
            int index,
            HashSet<string> usedFileNames)
        {
            var fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"reference-{index}.wav";
            }

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalidChar, '_');
            }

            var extension = Path.GetExtension(fileName);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = $"reference-{index}";
            }

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".wav";
            }

            var candidate = fileName;
            var duplicateIndex = 2;
            while (!usedFileNames.Add(candidate))
            {
                candidate = $"{stem}-{duplicateIndex++}{extension}";
            }

            return candidate;
        }

        private static string CreateTempDirectory(string prefix)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Temporary files are best-effort cleanup.
            }
        }
    }
}
