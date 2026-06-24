using System.Collections.Generic;
using System;
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
    public static class QwenTtsVoiceCatalogService
    {
        public static Task<IReadOnlyList<string>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(QwenTtsSpeakerCatalog.Speakers);
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
            if (!TtsEngineCatalogClient.TryBuildEnginePathUrl(
                    config,
                    engineCatalog,
                    AppConfig.TtsProviderQwen,
                    engine => engine.VoicesPath,
                    out var url))
            {
                return await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var response = await authService.SendAuthorizedAsync(
                    "Qwen TTS voices",
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
            AddVoicesFromElement(values, root);

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddVoicesFromElement(List<string> values, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                AddVoices(values, element);
                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var propertyName in new[] { "voices", "native_voices", "speakers", "items", "data", "options" })
            {
                if (element.TryGetProperty(propertyName, out var property))
                {
                    AddVoicesFromElement(values, property);
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "voices", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "native_voices", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "speakers", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "items", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "data", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "options", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "tone_presets", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    values.Add(property.Name);
                    foreach (var propertyName in new[] { "speaker", "voice", "name", "id", "value", "display_name" })
                    {
                        if (property.Value.TryGetProperty(propertyName, out var itemProperty)
                            && itemProperty.ValueKind == JsonValueKind.String)
                        {
                            values.Add(itemProperty.GetString() ?? string.Empty);
                            break;
                        }
                    }
                }
            }
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

                foreach (var propertyName in new[] { "voice", "speaker", "name", "id", "value" })
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
