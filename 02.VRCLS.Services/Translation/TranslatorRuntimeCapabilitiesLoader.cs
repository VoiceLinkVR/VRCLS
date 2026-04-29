using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;
using VRCLS.Core.Models;

namespace VRCLS.Services.Translation
{
    public readonly record struct TranslatorRuntimeCommand(string FileName, string Arguments, string Mode, string MetadataDirectory)
    {
        public bool HasExecutable => !string.IsNullOrWhiteSpace(FileName);

        public string CapabilitiesPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(MetadataDirectory))
                {
                    return string.Empty;
                }

                return Path.Combine(MetadataDirectory, "translator-capabilities.json");
            }
        }
    }

    public static class TranslatorRuntimeCapabilitiesLoader
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(TranslatorRuntimeCapabilitiesLoader));
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static TranslatorRuntimeCommand ResolveTranslatorCommand(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var baseDir = AppContext.BaseDirectory;

            if (!string.IsNullOrWhiteSpace(config.TranslatorsRuntimeDir))
            {
                var configuredDir = config.TranslatorsRuntimeDir.Trim();
                var configuredRuntimeExe = Path.Combine(configuredDir, "translator.exe");
                if (File.Exists(configuredRuntimeExe))
                {
                    return new TranslatorRuntimeCommand(configuredRuntimeExe, string.Empty, "exe", configuredDir);
                }
            }

            var appDataRuntimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS",
                "translator-runtime",
                "current");
            var appDataRuntimeExe = Path.Combine(appDataRuntimeDir, "translator.exe");
            if (File.Exists(appDataRuntimeExe))
            {
                return new TranslatorRuntimeCommand(appDataRuntimeExe, string.Empty, "exe", appDataRuntimeDir);
            }

            var bundledRuntimeDir = Path.Combine(baseDir, "translator");
            var bundledRuntimeExe = Path.Combine(bundledRuntimeDir, "translator.exe");
            if (File.Exists(bundledRuntimeExe))
            {
                return new TranslatorRuntimeCommand(bundledRuntimeExe, string.Empty, "exe", bundledRuntimeDir);
            }

            var legacyExe = Path.Combine(baseDir, "tools", "translator.exe");
            if (File.Exists(legacyExe))
            {
                return new TranslatorRuntimeCommand(legacyExe, string.Empty, "exe", Path.GetDirectoryName(legacyExe) ?? string.Empty);
            }

            var script = Path.Combine(baseDir, "scripts", "translator.py");
            var python = string.IsNullOrWhiteSpace(config.PythonPath) ? "python" : config.PythonPath.Trim();
            if (File.Exists(script) && !string.IsNullOrWhiteSpace(python))
            {
                return new TranslatorRuntimeCommand(python, $"-X utf8 \"{script}\"", "python", Path.GetDirectoryName(script) ?? string.Empty);
            }

            return new TranslatorRuntimeCommand(string.Empty, string.Empty, "none", string.Empty);
        }

        public static bool TryApplyInstalledRuntimeCapabilities(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var command = ResolveTranslatorCommand(config);
            var capabilitiesPath = command.CapabilitiesPath;
            if (string.IsNullOrWhiteSpace(capabilitiesPath) || !File.Exists(capabilitiesPath))
            {
                ClearCachedCapabilities(config);
                _logger.Information("Translator runtime capabilities not found. mode={Mode}, path={Path}", command.Mode, capabilitiesPath);
                return false;
            }

            try
            {
                var json = File.ReadAllText(capabilitiesPath);
                var capabilities = JsonSerializer.Deserialize<TranslationRuntimeCapabilities>(json, _jsonOptions);
                if (capabilities == null)
                {
                    ClearCachedCapabilities(config);
                    _logger.Warning("Translator runtime capabilities file is empty. path={Path}", capabilitiesPath);
                    return false;
                }

                ApplyRuntimeCapabilities(config, capabilities);
                _logger.Information(
                    "Loaded translator runtime capabilities. path={Path}, recognition={RecognitionCount}, translation={TranslationCount}, engines={EngineCount}",
                    capabilitiesPath,
                    config.SupportedRecognitionLanguages.Count,
                    config.SupportedTranslationLanguages.Count,
                    config.SupportedTranslationEngines.Count);
                return true;
            }
            catch (Exception ex)
            {
                ClearCachedCapabilities(config);
                _logger.Warning(ex, "Failed to load translator runtime capabilities. path={Path}", capabilitiesPath);
                return false;
            }
        }

        private static void ApplyRuntimeCapabilities(AppConfig config, TranslationRuntimeCapabilities capabilities)
        {
            config.SupportedRecognitionLanguages = NormalizeLanguageCatalog(capabilities.SupportedRecognitionLanguages);
            config.SupportedTranslationLanguages = NormalizeLanguageCatalog(capabilities.SupportedTranslationLanguages);
            config.SupportedTranslationEngines = NormalizeEngineList(capabilities.SupportedTranslationEngines);
        }

        private static void ClearCachedCapabilities(AppConfig config)
        {
            config.SupportedRecognitionLanguages = new List<LanguageCatalogItem>();
            config.SupportedTranslationLanguages = new List<LanguageCatalogItem>();
            config.SupportedTranslationEngines = new List<string>();
        }

        private static List<LanguageCatalogItem> NormalizeLanguageCatalog(IEnumerable<LanguageCatalogItem>? items)
        {
            var result = new List<LanguageCatalogItem>();
            if (items == null)
            {
                return result;
            }

            foreach (var item in items)
            {
                var code = item?.Code?.Trim().ToLowerInvariant() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                if (result.Any(existing => string.Equals(existing.Code, code, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var displayName = item?.DisplayName?.Trim();
                result.Add(new LanguageCatalogItem
                {
                    Code = code,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? code : displayName
                });
            }

            return result;
        }

        private static List<string> NormalizeEngineList(IEnumerable<string>? items)
        {
            if (items == null)
            {
                return new List<string>();
            }

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
