using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Translation;

namespace VRCLS.Services.Update
{
    public sealed class TranslationHotUpdateService : ITranslationHotUpdateService, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<TranslationHotUpdateService>();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly AppConfig _config;
        private readonly EmbeddedTranslatorService _embeddedTranslatorService;
        private readonly HttpClient _httpClient;

        public TranslationHotUpdateService(AppConfig config, EmbeddedTranslatorService embeddedTranslatorService)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _embeddedTranslatorService = embeddedTranslatorService ?? throw new ArgumentNullException(nameof(embeddedTranslatorService));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };
        }

        public event EventHandler<TranslationProfileUpdatedEventArgs>? ProfileUpdated;

        public async Task<TranslationProfileUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var endpoint = ResolveProfileEndpoint();
            _logger.Information("Checking translation profile updates. endpoint={Endpoint}, current={Version}", endpoint, _config.TranslationProfileVersion);

            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response).ConfigureAwait(false);
                _logger.Warning("Translation profile check failed. status={StatusCode}, body={Body}", response.StatusCode, body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TranslationProfileUpdateInfo>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> SyncLatestProfileAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var updateInfo = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            if (updateInfo == null)
            {
                return false;
            }

            var endpoint = ResolveProfileEndpoint();
            var shouldUpdateProfile = updateInfo.HasUpdate;
            var shouldUpdateTranslatorRuntime = IsTranslatorRuntimeUpdateRequired(updateInfo.Translators);
            if (!shouldUpdateProfile && !shouldUpdateTranslatorRuntime)
            {
                LoadAndApplyTranslatorRuntimeCapabilities();
                PersistConfigSnapshot();
                return false;
            }

            TranslationProfilePayload? profile = updateInfo.Profile;
            if (shouldUpdateProfile && profile == null && !string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
            {
                profile = await DownloadProfilePayloadAsync(updateInfo, endpoint, progress, cancellationToken).ConfigureAwait(false);
            }

            if (shouldUpdateProfile && profile == null)
            {
                return false;
            }

            if (profile != null)
            {
                ApplyProfile(profile, updateInfo.Version);
            }

            if (shouldUpdateTranslatorRuntime)
            {
                await DownloadAndInstallTranslatorRuntimeAsync(updateInfo.Translators!, endpoint, progress, cancellationToken).ConfigureAwait(false);
            }

            LoadAndApplyTranslatorRuntimeCapabilities();

            PersistConfigSnapshot();
            await _embeddedTranslatorService.ReloadAsync().ConfigureAwait(false);
            await _embeddedTranslatorService.WarmupAsync().ConfigureAwait(false);

            ProfileUpdated?.Invoke(this, new TranslationProfileUpdatedEventArgs(_config.TranslationProfileVersion));
            _logger.Information("Translation profile updated. version={Version}", _config.TranslationProfileVersion);
            return true;
        }

        private async Task<TranslationProfilePayload?> DownloadProfilePayloadAsync(
            TranslationProfileUpdateInfo updateInfo,
            Uri profileEndpoint,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var uri = ResolveUri(profileEndpoint, updateInfo.DownloadUrl);
            if (uri == null)
            {
                _logger.Warning("Invalid translation profile url: {Url}", updateInfo.DownloadUrl);
                return null;
            }

            var cacheDir = Path.Combine(GetDataRoot(), "translation-profiles");
            Directory.CreateDirectory(cacheDir);
            var version = string.IsNullOrWhiteSpace(updateInfo.Version) ? "latest" : updateInfo.Version;
            var profilePath = Path.Combine(cacheDir, $"profile-{SanitizeFileName(version)}.json");

            if (!await DownloadFileAsync(uri, profilePath, progress, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (!await VerifyChecksumAsync(profilePath, updateInfo.Checksum, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warning("Translation profile checksum mismatch.");
                return null;
            }

            var json = await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TranslationProfilePayload>(json, _jsonOptions);
        }

        private void ApplyProfile(TranslationProfilePayload profile, string version)
        {
            if (!string.IsNullOrWhiteSpace(profile.TranslationServer))
            {
                _config.TranslationServer = profile.TranslationServer.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profile.TranslateService))
            {
                _config.TranslateService = profile.TranslateService.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(profile.TranslationServer))
            {
                _config.TranslateService = profile.TranslationServer.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profile.TranslateServiceCap))
            {
                _config.TranslateServiceCap = profile.TranslateServiceCap.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(profile.TranslationServer))
            {
                _config.TranslateServiceCap = profile.TranslationServer.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profile.SourceLanguage))
            {
                _config.SourceLanguage = profile.SourceLanguage.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profile.TargetTranslationLanguage))
            {
                _config.TargetTranslationLanguage = profile.TargetTranslationLanguage.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profile.TargetTranslationLanguage2))
            {
                _config.TargetTranslationLanguage2 = profile.TargetTranslationLanguage2.Trim();
            }

            if (!string.IsNullOrWhiteSpace(profile.TargetTranslationLanguage3))
            {
                _config.TargetTranslationLanguage3 = profile.TargetTranslationLanguage3.Trim();
            }

            if (profile.OpenAiConfig != null)
            {
                var existingKey = _config.OpenAiConfig.ApiKey;
                _config.OpenAiConfig = profile.OpenAiConfig;
                // API key must remain user-provided; hot-update presets should never prefill credentials.
                _config.OpenAiConfig.ApiKey = existingKey;
            }

            if (profile.OpenAiPresetTemplates != null && profile.OpenAiPresetTemplates.Count > 0)
            {
                _config.OpenAiPresetTemplates = profile.OpenAiPresetTemplates
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                _config.TranslationProfileVersion = version.Trim();
            }

            PersistConfigSnapshot();
        }

        private void LoadAndApplyTranslatorRuntimeCapabilities()
        {
            if (!TranslatorRuntimeCapabilitiesLoader.TryApplyInstalledRuntimeCapabilities(_config))
            {
                _logger.Debug("Translator runtime capabilities file not found or invalid.");
            }
        }

        private Uri ResolveProfileEndpoint()
        {
            return StaticContentPathHelper.ResolveStaticContentUri(_config, StaticContentPathHelper.TranslationProfileRelativePath);
        }

        private bool IsTranslatorRuntimeUpdateRequired(TranslatorRuntimeUpdateInfo? update)
        {
            if (update == null || string.IsNullOrWhiteSpace(update.Version))
            {
                return false;
            }

            var incoming = update.Version.Trim();
            var current = (_config.TranslatorsRuntimeVersion ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(current))
            {
                return true;
            }

            return !string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase);
        }

        private async Task DownloadAndInstallTranslatorRuntimeAsync(
            TranslatorRuntimeUpdateInfo runtimeInfo,
            Uri endpoint,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInfo.DownloadUrl))
            {
                _config.TranslatorsRuntimeVersion = runtimeInfo.Version?.Trim() ?? string.Empty;
                return;
            }

            var uri = ResolveUri(endpoint, runtimeInfo.DownloadUrl);
            if (uri == null)
            {
                _logger.Warning("Invalid translator runtime url: {Url}", runtimeInfo.DownloadUrl);
                return;
            }

            var cacheDir = Path.Combine(GetDataRoot(), "translator-runtime", "packages");
            Directory.CreateDirectory(cacheDir);

            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"translator-runtime-{SanitizeFileName(runtimeInfo.Version)}.zip";
            }

            var packagePath = Path.Combine(cacheDir, fileName);
            if (!await DownloadFileAsync(uri, packagePath, progress, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (!await VerifyChecksumAsync(packagePath, runtimeInfo.Checksum, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warning("Translator runtime checksum mismatch.");
                return;
            }

            var runtimeRoot = Path.Combine(GetDataRoot(), "translator-runtime");
            var currentDir = Path.Combine(runtimeRoot, "current");
            var stagingDir = Path.Combine(runtimeRoot, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            try
            {
                var extension = Path.GetExtension(packagePath);
                if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => ZipFile.ExtractToDirectory(packagePath, stagingDir, overwriteFiles: true), cancellationToken)
                        .ConfigureAwait(false);

                    var sourceRoot = ResolveArchiveRoot(stagingDir);
                    if (Directory.Exists(currentDir))
                    {
                        Directory.Delete(currentDir, recursive: true);
                    }

                    Directory.CreateDirectory(currentDir);
                    CopyDirectory(sourceRoot, currentDir);
                }
                else
                {
                    if (Directory.Exists(currentDir))
                    {
                        Directory.Delete(currentDir, recursive: true);
                    }

                    Directory.CreateDirectory(currentDir);
                    var destination = Path.Combine(currentDir, "translator.exe");
                    File.Copy(packagePath, destination, overwrite: true);
                }

                _config.TranslatorsRuntimeVersion = runtimeInfo.Version?.Trim() ?? string.Empty;
                _config.TranslatorsRuntimeDir = currentDir;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingDir))
                    {
                        Directory.Delete(stagingDir, recursive: true);
                    }
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

        private static string ResolveArchiveRoot(string extractedRoot)
        {
            var directories = Directory.GetDirectories(extractedRoot);
            var files = Directory.GetFiles(extractedRoot);
            if (directories.Length == 1 && files.Length == 0)
            {
                return directories[0];
            }

            return extractedRoot;
        }

        private static Uri? ResolveUri(Uri baseUri, string rawPath)
        {
            var value = (rawPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                return absolute;
            }

            return new Uri(baseUri, value.TrimStart('/'));
        }

        private async Task<bool> DownloadFileAsync(Uri source, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response).ConfigureAwait(false);
                _logger.Warning("Profile download failed. url={Url}, status={StatusCode}, body={Body}", source, response.StatusCode, body);
                return false;
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = File.Create(destination);

            var totalBytes = response.Content.Headers.ContentLength.GetValueOrDefault();
            var buffer = new byte[1024 * 64];
            long written = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                if (totalBytes > 0)
                {
                    progress?.Report(written * 100d / totalBytes);
                }
            }

            progress?.Report(100d);
            return true;
        }

        private static async Task<bool> VerifyChecksumAsync(string filePath, string checksum, CancellationToken cancellationToken)
        {
            var expected = NormalizeSha256(checksum);
            if (string.IsNullOrWhiteSpace(expected))
            {
                return true;
            }

            await using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            const string prefix = "sha256:";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(prefix.Length);
            }

            return normalized.Replace("-", string.Empty).Trim();
        }

        private static string GetDataRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS");
        }

        private static string SanitizeFileName(string? text)
        {
            var value = text ?? string.Empty;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '-');
            }

            return string.IsNullOrWhiteSpace(value) ? "latest" : value.Trim();
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, directory);
                Directory.CreateDirectory(Path.Combine(destinationDir, relative));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(destinationDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private void PersistConfigSnapshot()
        {
            try
            {
                AppConfigStorage.SaveOrThrow(_config);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to persist config after translation profile update.");
            }
        }
    }
}
