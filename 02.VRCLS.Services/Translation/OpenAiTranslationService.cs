using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Translation
{
    /// <summary>
    /// OpenAI-compatible translation service (Chat Completions style).
    /// Works with OpenAI, and most OpenAI-compatible providers when BaseUrl points to the correct API root.
    /// </summary>
    public class OpenAiTranslationService : ITranslationService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<OpenAiTranslationService>();
        private readonly AppConfig _config;
        private readonly HttpClient _httpClient;

        private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
        {
            { "auto", "Auto" },
            { "zh", "Chinese" },
            { "zt", "Traditional Chinese" },
            { "en", "English" },
            { "ja", "Japanese" },
            { "ko", "Korean" },
            { "ru", "Russian" },
            { "es", "Spanish" },
            { "fr", "French" },
            { "de", "German" },
        };

        public OpenAiTranslationService(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(string.Empty);
            return TranslateInternalAsync(text, targetLanguage, sourceLanguage);
        }

        public Task<string> TranslateBatchAsync(string[] texts, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (texts == null || texts.Length == 0) return Task.FromResult(string.Empty);
            var joined = string.Join("\n", texts);
            return TranslateInternalAsync(joined, targetLanguage, sourceLanguage, isBatch: true, expectedLines: texts.Length);
        }

        private async Task<string> TranslateInternalAsync(string text, string targetLanguage, string? sourceLanguage, bool isBatch = false, int expectedLines = 0)
        {
            var cfg = _config.OpenAiConfig ?? new OpenAiConfig();
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model))
            {
                _logger.Warning("OpenAI 缈昏瘧閰嶇疆涓嶅畬鏁? (base_url/model)");
                return string.Empty;
            }

            var endpoint = BuildChatCompletionsEndpoint(cfg.BaseUrl);
            if (endpoint == null)
            {
                _logger.Warning("OpenAI base_url 鏃犳晥: {BaseUrl}", cfg.BaseUrl);
                return string.Empty;
            }

            var from = (sourceLanguage ?? _config.SourceLanguage ?? "auto").Trim();
            var to = (targetLanguage ?? "en").Trim();

            var sourceName = LanguageNames.TryGetValue(from, out var sn) ? sn : from;
            var targetName = LanguageNames.TryGetValue(to, out var tn) ? tn : to;

            var systemPrompt = BuildSystemPrompt(cfg, sourceName, targetName, isBatch, expectedLines);

            // OpenAI chat completions request.
            var payload = new Dictionary<string, object?>
            {
                ["model"] = cfg.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = text }
                },
                ["temperature"] = cfg.Temperature,
                ["max_tokens"] = cfg.MaxTokens
            };
            ApplyExtraBody(payload, cfg.ExtraBody);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());
            }

            try
            {
                var resp = await _httpClient.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.Warning("OpenAI 缈昏瘧澶辫触 - Status={Status}, Body={Body}", (int)resp.StatusCode, TrimForLog(body));
                    return string.Empty;
                }

                var content = ExtractAssistantContent(body);
                return (content ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OpenAI 缈昏瘧寮傚父 (endpoint={Endpoint})", endpoint);
                return string.Empty;
            }
        }

        private static Uri? BuildChatCompletionsEndpoint(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri))
            {
                return null;
            }
            // User should configure base_url to point to the provider's API root (e.g. .../v1/ or .../api/paas/v4/).
            return new Uri(baseUri, "chat/completions");
        }

        private static void ApplyExtraBody(Dictionary<string, object?> payload, string? extraBody)
        {
            if (string.IsNullOrWhiteSpace(extraBody))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(extraBody);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _logger.Warning("OpenAI Extra Body must be a JSON object and was ignored.");
                    return;
                }

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }
            catch (JsonException ex)
            {
                _logger.Warning(ex, "OpenAI Extra Body is not valid JSON and was ignored.");
            }
        }

        private static string BuildSystemPrompt(OpenAiConfig cfg, string sourceName, string targetName, bool isBatch, int expectedLines)
        {
            if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt))
            {
                return cfg.SystemPrompt
                    .Replace("{sourceLang}", sourceName)
                    .Replace("{targetLang}", targetName);
            }

            var basePrompt = $"""
You are a professional translator.
Translate the user's text from {sourceName} to {targetName}.

Requirements:
1) Keep the original meaning and tone.
2) Make the translation natural and fluent.
3) If the text contains obvious speech recognition errors, correct them before translating.
4) Return only the translated text. Do not add explanations, quotes, or extra content.
5) Preserve line breaks.
""";

            if (isBatch && expectedLines > 0)
            {
                basePrompt += $"\nThe input contains {expectedLines} lines. Keep the same number of lines in the output, in the same order.\n";
            }

            return basePrompt;
        }

        private static string? ExtractAssistantContent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    var first = choices.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        if (first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                        {
                            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            {
                                return content.GetString();
                            }
                        }

                        // Some providers may return a plain "text" field.
                        if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            return text.GetString();
                        }
                    }
                }

                // Common error shape: { error: { message: ... } }
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        _logger.Warning("OpenAI 缈昏瘧 error.message: {Message}", msg.GetString());
                    }
                }
            }
            catch
            {
                // ignore parse failures
            }

            return null;
        }

        private static string TrimForLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 500 ? s : (s.Substring(0, 500) + "...");
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}

