using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;
using VRCLS.Services.Auth;

namespace VRCLS.Services.Translation
{
    /// <summary>
    /// Default translation service calling the backend text translation API.
    /// </summary>
    public class ServerTranslationService : ITranslationService, IDisposable
    {
        public const string HttpClientName = "VRCLS.TranslationApi";
        private static readonly ILogger _logger = Log.ForContext<ServerTranslationService>();
        private readonly AppConfig _config;
        private readonly HttpClient _httpClient;
        private readonly IBackendAuthService _authService;
        private readonly IDisposable? _ownedAuthDisposable;

        public ServerTranslationService(AppConfig config)
            : this(config, null, null)
        {
        }

        public ServerTranslationService(
            AppConfig config,
            IBackendAuthService? authService,
            IHttpClientFactory? httpClientFactory = null)
        {
            _logger.Debug("鍒濆鍖?ServerTranslationService");
            _config = config ?? throw new ArgumentNullException(nameof(config));
            var baseAddress = BackendUrlHelper.BuildApiBaseAddress(config);
            _httpClient = httpClientFactory?.CreateClient(HttpClientName) ?? CreateFallbackHttpClient();
            _logger.Information("ServerTranslationService 鍒濆鍖栧畬鎴?- API鍦板潃: {BaseAddress}", baseAddress);

            if (authService != null)
            {
                _authService = authService;
            }
            else
            {
                // Backward compatible: tests may new() this directly.
                var owned = new BackendAuthService(config);
                _authService = owned;
                _ownedAuthDisposable = owned;
            }
        }

        private static HttpClient CreateFallbackHttpClient()
        {
            return new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        public Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                _logger.Warning("尝试翻译空文本");
                return Task.FromResult(string.Empty);
            }
            _logger.Verbose("缈昏瘧璇锋眰 - 鏂囨湰闀垮害: {Length}, 婧愯瑷€: {SourceLang}, 鐩爣璇█: {TargetLang}", text.Length, sourceLanguage ?? _config.SourceLanguage, targetLanguage);
            return TranslateBatchAsync(new[] { text }, targetLanguage, sourceLanguage, routeHint);
        }

        public async Task<string> TranslateBatchAsync(string[] texts, string targetLanguage, string? sourceLanguage = null, string? routeHint = null)
        {
            if (texts == null || texts.Length == 0)
            {
                return string.Empty;
            }

            var effectiveTargets = ResolveEffectiveTargets(targetLanguage, routeHint);
            if (effectiveTargets.Length == 0)
            {
                _logger.Debug("[Translation] No enabled translation targets for route {RouteHint}", routeHint ?? string.Empty);
                return string.Empty;
            }

            var joinedInput = string.Join("\n", texts);
            var request = new WebTranslateRequest
            {
                Text = joinedInput,
                SourceLanguage = sourceLanguage ?? _config.SourceLanguage,
                TargetLanguage = effectiveTargets.ElementAtOrDefault(0) ?? string.Empty,
                TargetLanguage2 = effectiveTargets.ElementAtOrDefault(1),
                TargetLanguage3 = effectiveTargets.ElementAtOrDefault(2)
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var primaryResp = await SendWithAuthRetryAsync(
                    operation: "webtranslate",
                    createRequest: () => CreateTranslateRequest("func/webtranslate", request));

                stopwatch.Stop();

                if (primaryResp == null)
                {
                    return string.Empty;
                }

                if (primaryResp.IsSuccessStatusCode)
                {
                    var result = await primaryResp.Content.ReadFromJsonAsync<TranslationApiResponse>();
                    _logger.Information("[Translation] webtranslate success in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                    return BuildOutput(result);
                }

                if ((int)primaryResp.StatusCode == 430)
                {
                    var tooManyContent = await primaryResp.Content.ReadAsStringAsync();
                    _authService.NotifyTooManyRequests(source: "翻译服务", backendDetail: tooManyContent, channel: routeHint, stage: string.Equals(routeHint, "TextSend", StringComparison.OrdinalIgnoreCase) ? "textsend" : "translation", originalText: joinedInput);
                    _logger.Warning("[Translation] webtranslate returned 430 too many requests. response: {Response}", tooManyContent);
                    return string.Empty;
                }

                if (primaryResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.Warning("[Translation] webtranslate returned 401 after centralized auth retry.");
                    return string.Empty;
                }

                var primaryError = await primaryResp.Content.ReadAsStringAsync();
                _logger.Warning("[Translation] webtranslate failed - status: {StatusCode}, response: {Response}",
                    primaryResp.StatusCode, primaryError);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Warning(ex, "[Translation] webtranslate exception in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            }

            return string.Empty;
        }

        private string[] ResolveEffectiveTargets(string targetLanguage, string? routeHint)
        {
            var targets = new[]
            {
                targetLanguage,
                _config.ResolveSecondaryTargetLanguage(routeHint),
                _config.ResolveTertiaryTargetLanguage(routeHint)
            };

            return targets
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
        }

        private HttpRequestMessage CreateTranslateRequest(string relativePath, WebTranslateRequest request)
        {
            var requestUrl = new Uri($"{BackendUrlHelper.BuildApiBaseUrl(_config)}{relativePath}", UriKind.Absolute);
            return new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = JsonContent.Create(request)
            };
        }

        private async Task<HttpResponseMessage?> SendWithAuthRetryAsync(string operation, Func<HttpRequestMessage> createRequest)
        {
            var response = await _authService.SendAuthorizedAsync(
                $"Translation {operation}",
                createRequest,
                (request, cancellationToken) => _httpClient.SendAsync(request, cancellationToken)).ConfigureAwait(false);
            if (response == null)
            {
                _logger.Warning("[Translation] Missing token for {Operation}", operation);
            }

            return response;
        }

        private string BuildOutput(TranslationApiResponse? r)
        {
            if (r == null) return string.Empty;
            var parts = new[]
            {
                r.TranslatedText ?? string.Empty,
                r.TranslatedText2 ?? string.Empty,
                r.TranslatedText3 ?? string.Empty
            };
            // Preserve empty slots so callers can keep t1/t2/t3 aligned with configured targets.
            return string.Join("||", parts);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _ownedAuthDisposable?.Dispose();
        }

        private class WebTranslateRequest
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("sourceLanguage")]
            public string SourceLanguage { get; set; } = "auto";

            [JsonPropertyName("targetLanguage")]
            public string TargetLanguage { get; set; } = "en";

            [JsonPropertyName("targetLanguage2")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? TargetLanguage2 { get; set; }

            [JsonPropertyName("targetLanguage3")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? TargetLanguage3 { get; set; }
        }

        private class TranslationApiResponse
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("translatedText")]
            public string TranslatedText { get; set; } = string.Empty;

            [JsonPropertyName("translatedText2")]
            public string TranslatedText2 { get; set; } = string.Empty;

            [JsonPropertyName("translatedText3")]
            public string TranslatedText3 { get; set; } = string.Empty;
        }

    }
}

