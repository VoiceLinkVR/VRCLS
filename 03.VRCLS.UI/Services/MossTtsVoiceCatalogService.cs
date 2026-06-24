using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Tts;

namespace VRCLS.UI.Services
{
    public static class MossTtsVoiceCatalogService
    {
        private static readonly string[] _voices =
        {
            "Junhao",
            "Zhiming",
            "Weiguo",
            "Xiaoyu",
            "Yuewen",
            "Lingyu",
            "Trump",
            "Ava",
            "Bella",
            "Adam",
            "Nathan",
            "Soyo",
            "Saki",
            "Mortis",
            "Umiri",
            "Mei",
            "Anon",
            "Arisa"
        };

        public static Task<IReadOnlyList<string>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(
                _voices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        public static async Task<IReadOnlyList<string>> GetVoicesAsync(
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
                return await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
            }

            var engineCatalog = await TtsEngineCatalogClient.GetEnginesAsync(
                config,
                authService,
                httpClient,
                cancellationToken).ConfigureAwait(false);
            var url = TtsEngineCatalogClient.TryBuildEnginePathUrl(
                config,
                engineCatalog,
                AppConfig.TtsProviderMoss,
                engine => engine.VoicesPath,
                out var engineVoicesUrl)
                ? engineVoicesUrl
                : $"{BackendUrlHelper.BuildApiBaseUrl(config)}tts/moss/voices";
            try
            {
                using var response = await authService.SendAuthorizedAsync(
                    "MOSS TTS voices",
                    () => new HttpRequestMessage(HttpMethod.Get, url),
                    (request, token) => httpClient.SendAsync(request, token),
                    cancellationToken).ConfigureAwait(false);

                if (response == null || !response.IsSuccessStatusCode)
                {
                    return await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var voices = ParseVoices(document.RootElement);
                return voices.Count > 0
                    ? voices
                    : await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static IReadOnlyList<string> ParseVoices(JsonElement root)
        {
            var values = new List<string>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                AddVoices(values, root);
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("voices", out var voicesElement)
                && voicesElement.ValueKind == JsonValueKind.Array)
            {
                AddVoices(values, voicesElement);
            }

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddVoices(List<string> values, JsonElement voicesElement)
        {
            foreach (var item in voicesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    values.Add(item.GetString() ?? string.Empty);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var propertyName in new[] { "voice", "name", "id" })
                {
                    if (item.TryGetProperty(propertyName, out var property)
                        && property.ValueKind == JsonValueKind.String)
                    {
                        values.Add(property.GetString() ?? string.Empty);
                        break;
                    }
                }
            }
        }
    }
}
