using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Translation
{
    /// <summary>
    /// Chooses the translation implementation based on current config.
    /// </summary>
    public class TranslationServiceManager : ITranslationService
    {
        private static readonly ILogger _logger = Log.ForContext<TranslationServiceManager>();
        private readonly AppConfig _config;
        private readonly ITranslationService _server;
        private readonly ITranslationService _python;
        private readonly ITranslationService _openAi;

        public TranslationServiceManager(AppConfig config, ITranslationService server, ITranslationService python, ITranslationService openAi)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _python = python ?? throw new ArgumentNullException(nameof(python));
            _openAi = openAi ?? throw new ArgumentNullException(nameof(openAi));
        }

        public Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            return TranslateWithFallbackAsync(text, targetLanguage, sourceLanguage, batch: null, routeHint);
        }

        public Task<string> TranslateBatchAsync(string[] texts, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            return TranslateWithFallbackAsync(null, targetLanguage, sourceLanguage, texts, routeHint);
        }

        private ITranslationService Select(string? routeHint, out string engine)
        {
            engine = ResolveEngine(routeHint);

            if (engine == "openai")
            {
                return _openAi;
            }

            // Engines supported by translators.exe are routed to the embedded translator runtime.
            if (IsEmbeddedTranslatorEngine(engine))
            {
                return _python;
            }

            return _server;
        }

        private string ResolveEngine(string? routeHint)
        {
            return NormalizeEngine(_config.ResolveTranslationEngine(routeHint));
        }

        private bool IsEmbeddedTranslatorEngine(string engine)
        {
            return _config.IsEmbeddedTranslatorEngine(engine);
        }

        private static string NormalizeEngine(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private async Task<string> TranslatePythonWithMultiTargetsAsync(string? text, string targetLanguage, string? sourceLanguage, string[]? batch, string? routeHint)
        {
            var slots = BuildTargetSlots(
                targetLanguage,
                _config.ResolveSecondaryTargetLanguage(routeHint),
                _config.ResolveTertiaryTargetLanguage(routeHint));
            if (slots.Count == 0)
            {
                return string.Empty;
            }

            var parts = new string[slots.Count];
            var hasEnabledSlot = false;
            var hasInvalidPythonOutput = false;
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (!slot.Enabled)
                {
                    parts[i] = string.Empty;
                    continue;
                }

                hasEnabledSlot = true;

                var translated = await TranslateSingleTargetAsync(_python, text, sourceLanguage, batch, slot.Code, routeHint);

                if (string.IsNullOrWhiteSpace(translated) || LooksCorrupted(translated))
                {
                    hasInvalidPythonOutput = true;
                    _logger.Warning("Python translator produced invalid output. target={Target}", slot.Code);
                    parts[i] = string.Empty;
                    continue;
                }

                parts[i] = translated ?? string.Empty;
            }

            if (hasEnabledSlot && hasInvalidPythonOutput && parts.All(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("Python translator returned invalid output.");
            }

            // Keep the same return protocol expected by MainViewModel: t1||t2||t3 (empty slots preserved).
            return string.Join("||", parts);
        }

        private async Task<string> TranslateSingleTargetAsync(ITranslationService service, string? text, string? sourceLanguage, string[]? batch, string target, string? routeHint)
        {
            if (batch != null)
            {
                return await service.TranslateBatchAsync(batch, target, sourceLanguage, routeHint);
            }

            return await service.TranslateAsync(text ?? string.Empty, target, sourceLanguage, routeHint);
        }

        private static bool LooksCorrupted(string value)
        {
            // U+FFFD usually means upstream mojibake/encoding damage.
            return value.IndexOf('\uFFFD') >= 0;
        }

        private static List<TargetSlot> BuildTargetSlots(params string?[] candidates)
        {
            var list = new List<TargetSlot>(candidates.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in candidates)
            {
                var code = (item ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(code) || code.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(TargetSlot.Disabled);
                    continue;
                }

                if (!seen.Add(code))
                {
                    list.Add(TargetSlot.Disabled);
                    continue;
                }

                list.Add(new TargetSlot(code, enabled: true));
            }

            return list;
        }

        private async Task<string> TranslateWithFallbackAsync(string? text, string targetLanguage, string? sourceLanguage, string[]? batch, string? routeHint)
        {
            var selected = Select(routeHint, out var engine);
            _logger.Debug("Translation route selected. engine={Engine}, service={Service}", engine, selected.GetType().Name);
            string result;

            if (selected == _python)
            {
                result = await TranslatePythonWithMultiTargetsAsync(text, targetLanguage, sourceLanguage, batch, routeHint);
            }
            else if (batch != null)
            {
                result = await selected.TranslateBatchAsync(batch, targetLanguage, sourceLanguage, routeHint);
            }
            else
            {
                result = await selected.TranslateAsync(text ?? string.Empty, targetLanguage, sourceLanguage, routeHint);
            }

            // If a non-backend engine is selected but returns empty, fall back to backend.
            // Python translator intentionally does NOT fall back to server.
            if (selected != _server && selected != _python && string.IsNullOrWhiteSpace(result))
            {
                _logger.Warning("Translation returned empty, fallback to server. engine={Engine}, selected={Selected}", engine, selected.GetType().Name);
                if (batch != null)
                {
                    return await _server.TranslateBatchAsync(batch, targetLanguage, sourceLanguage, routeHint);
                }
                return await _server.TranslateAsync(text ?? string.Empty, targetLanguage, sourceLanguage, routeHint);
            }

            return result;
        }

        private readonly struct TargetSlot
        {
            public static TargetSlot Disabled => new(string.Empty, enabled: false);

            public TargetSlot(string code, bool enabled)
            {
                Code = code;
                Enabled = enabled;
            }

            public string Code { get; }
            public bool Enabled { get; }
        }
    }
}
