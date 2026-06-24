using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using Serilog;

namespace VRCLS.Services.Tts
{
    public static class TtsEngineCatalogClient
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(TtsEngineCatalogClient));
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<TtsEngineCatalog?> GetEnginesAsync(
            AppConfig config,
            IBackendAuthService authService,
            HttpClient httpClient,
            CancellationToken cancellationToken = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (authService == null) throw new ArgumentNullException(nameof(authService));
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));

            if (BackendUrlHelper.UsesLegacyBackendApi(config))
            {
                return null;
            }

            var url = $"{BackendUrlHelper.BuildApiBaseUrl(config)}tts/engines";
            try
            {
                using var response = await authService.SendAuthorizedAsync(
                    "TTS engines",
                    () => new HttpRequestMessage(HttpMethod.Get, url),
                    (request, token) => httpClient.SendAsync(request, token),
                    cancellationToken).ConfigureAwait(false);

                if (response == null)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug("TTS engines request failed - statusCode: {StatusCode}", (int)response.StatusCode);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<TtsEngineCatalog>(
                    stream,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to load TTS engines metadata.");
                return null;
            }
        }

        public static string BuildBackendPathUrl(AppConfig config, string? path, string fallbackApiRelativePath)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var normalizedFallback = (fallbackApiRelativePath ?? string.Empty).Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                return $"{BackendUrlHelper.BuildApiBaseUrl(config)}{normalizedFallback}";
            }

            var normalizedPath = path.Trim();
            if (Uri.TryCreate(normalizedPath, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            if (normalizedPath.StartsWith("/", StringComparison.Ordinal))
            {
                return $"{BackendUrlHelper.BuildServiceRootUrl(config)}{normalizedPath}";
            }

            return $"{BackendUrlHelper.BuildApiBaseUrl(config)}{normalizedPath.TrimStart('/')}";
        }

        public static bool TryBuildEnginePathUrl(
            AppConfig config,
            TtsEngineCatalog? catalog,
            string provider,
            Func<TtsEngineInfo, string?> pathSelector,
            out string url)
        {
            url = string.Empty;
            if (catalog?.GetEngine(provider) is not { } engine)
            {
                return false;
            }

            var path = pathSelector(engine);
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            url = BuildBackendPathUrl(config, path, string.Empty);
            return true;
        }
    }
}
