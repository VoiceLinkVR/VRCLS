using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.UI.Services
{
    public static class TtsVoiceClonePresetMarketService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<IReadOnlyList<TtsVoiceClonePresetMarketItem>> GetMarketAsync(
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
                throw new InvalidOperationException("预设市场需要使用新版后端账号认证接口。");
            }

            var url = $"{BackendUrlHelper.BuildApiBaseUrl(config)}voice-clone-presets/market";
            using var response = await authService.SendAuthorizedAsync(
                "Voice clone preset market",
                () => new HttpRequestMessage(HttpMethod.Get, url),
                (request, token) => httpClient.SendAsync(request, token),
                cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                throw new InvalidOperationException("请先完成账号登录后再打开预设市场。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await ExtractErrorMessageAsync(response, stream, cancellationToken).ConfigureAwait(false));
            }

            var payload = await JsonSerializer.DeserializeAsync<TtsVoiceClonePresetMarketListResponse>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            return (payload?.Items ?? new List<TtsVoiceClonePresetMarketItem>())
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static async Task<string> DownloadPresetPackageAsync(
            AppConfig config,
            IBackendAuthService authService,
            HttpClient httpClient,
            TtsVoiceClonePresetMarketItem item,
            CancellationToken cancellationToken = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (authService == null) throw new ArgumentNullException(nameof(authService));
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                throw new ArgumentException("市场预设 ID 不能为空。", nameof(item));
            }

            if (BackendUrlHelper.UsesLegacyBackendApi(config))
            {
                throw new InvalidOperationException("预设市场需要使用新版后端账号认证接口。");
            }

            var url = $"{BackendUrlHelper.BuildApiBaseUrl(config)}voice-clone-presets/market/{Uri.EscapeDataString(item.Id)}/download";
            using var response = await authService.SendAuthorizedAsync(
                "Download voice clone preset",
                () => new HttpRequestMessage(HttpMethod.Get, url),
                (request, token) => httpClient.SendAsync(request, token),
                cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                throw new InvalidOperationException("请先完成账号登录后再下载预设。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await ExtractErrorMessageAsync(response, stream, cancellationToken).ConfigureAwait(false));
            }

            var downloadDirectory = Path.Combine(Path.GetTempPath(), "VRCLS", "voice_clone_preset_market");
            Directory.CreateDirectory(downloadDirectory);

            var packagePath = Path.Combine(
                downloadDirectory,
                $"{TtsVoiceClonePresetPackageService.NormalizePresetId(item.Id)}-{Guid.NewGuid():N}.zip");

            await using (var fileStream = File.Create(packagePath))
            {
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(item.Sha256))
            {
                var actualSha256 = ComputeSha256(packagePath);
                if (!string.Equals(actualSha256, item.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(packagePath);
                    throw new InvalidDataException("下载的预设包校验失败，请重新下载。");
                }
            }

            return packagePath;
        }

        private static async Task<string> ExtractErrorMessageAsync(
            HttpResponseMessage response,
            Stream stream,
            CancellationToken cancellationToken)
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("detail", out var detail))
                {
                    if (detail.ValueKind == JsonValueKind.String)
                    {
                        return detail.GetString() ?? response.ReasonPhrase ?? "请求失败。";
                    }

                    if (detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString() ?? response.ReasonPhrase ?? "请求失败。";
                    }
                }
            }
            catch
            {
                // Ignore parse failures and use a generic HTTP message below.
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "预设市场请求失败：HTTP {0} {1}",
                (int)response.StatusCode,
                response.ReasonPhrase);
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    public sealed class TtsVoiceClonePresetMarketListResponse
    {
        [JsonPropertyName("items")]
        public List<TtsVoiceClonePresetMarketItem> Items { get; set; } = new();
    }

    public sealed class TtsVoiceClonePresetMarketItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("languages")]
        public List<string> Languages { get; set; } = new();

        [JsonPropertyName("providers")]
        public List<string> Providers { get; set; } = new();

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = string.Empty;

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

        public string LanguagesText => Languages == null || Languages.Count == 0
            ? "自动"
            : string.Join(", ", Languages.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));

        public string ProvidersText => Providers == null || Providers.Count == 0
            ? "通用"
            : string.Join(", ", Providers.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));

        public string FileSizeText
        {
            get
            {
                if (FileSize <= 0)
                {
                    return "未知大小";
                }

                var size = FileSize;
                var units = new[] { "B", "KB", "MB", "GB" };
                var unitIndex = 0;
                var value = (double)size;
                while (value >= 1024 && unitIndex < units.Length - 1)
                {
                    value /= 1024;
                    unitIndex++;
                }

                return $"{value:0.##} {units[unitIndex]}";
            }
        }

        public string Summary => $"{LanguagesText} · {ProvidersText} · {FileSizeText}";
    }
}
