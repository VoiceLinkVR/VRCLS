using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Photo
{
    public sealed class PhotoTranslationService : IPhotoTranslationService
    {
        public const string HttpClientName = "VRCLS.PhotoApi";

        private static readonly ILogger _logger = Log.ForContext<PhotoTranslationService>();
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp"
        };
        private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan ProcessedRetention = TimeSpan.FromMinutes(10);

        private readonly AppConfig _config;
        private readonly HttpClient _httpClient;
        private readonly IBackendAuthService _authService;
        private readonly object _watchLock = new();
        private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _recentProcessed = new(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;
        private bool _disposed;

        public PhotoTranslationService(
            AppConfig config,
            IBackendAuthService authService,
            IHttpClientFactory? httpClientFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _httpClient = httpClientFactory?.CreateClient(HttpClientName) ?? new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(90)
            };
        }

        public event EventHandler<TranscriptionResult>? TextRecognized;

        public bool IsRunning => _watcher?.EnableRaisingEvents == true;

        public void Start()
        {
            if (_disposed || !_config.EnablePhotoTranslation)
            {
                return;
            }

            var photoRoot = ResolvePhotoRoot();
            var watchRoot = ResolveWatchRoot(photoRoot);
            if (string.IsNullOrWhiteSpace(watchRoot) || !Directory.Exists(watchRoot))
            {
                _logger.Warning("Photo translation watcher not started. watchRoot missing: {WatchRoot}", watchRoot);
                return;
            }

            Stop();

            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(watchRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Created += OnWatchedFileChanged;
            _watcher.Changed += OnWatchedFileChanged;
            _watcher.Renamed += OnWatchedFileChanged;
            _watcher.EnableRaisingEvents = true;

            _logger.Information("Photo translation watcher started. photoRoot={PhotoRoot}, watchRoot={WatchRoot}", photoRoot, watchRoot);
        }

        public void Stop()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnWatchedFileChanged;
                _watcher.Changed -= OnWatchedFileChanged;
                _watcher.Renamed -= OnWatchedFileChanged;
                _watcher.Dispose();
                _watcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            lock (_watchLock)
            {
                _pendingFiles.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _httpClient.Dispose();
        }

        private string ResolvePhotoRoot()
        {
            if (!string.IsNullOrWhiteSpace(_config.PhotoRootPath))
            {
                return Path.GetFullPath(_config.PhotoRootPath.Trim());
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "VRChat");
        }

        private static string ResolveWatchRoot(string photoRoot)
        {
            if (Directory.Exists(photoRoot))
            {
                return photoRoot;
            }

            var parent = Path.GetDirectoryName(photoRoot);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }

            return photoRoot;
        }

        private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                var fullPath = e.FullPath;
                if (!ShouldQueueFile(fullPath))
                {
                    return;
                }

                lock (_watchLock)
                {
                    _pendingFiles.Add(fullPath);
                    _debounceTimer?.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to queue photo translation task.");
            }
        }

        private bool ShouldQueueFile(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            var extension = Path.GetExtension(fullPath);
            if (!SupportedExtensions.Contains(extension))
            {
                return false;
            }

            var fileName = Path.GetFileName(fullPath);
            if (!fileName.StartsWith("VRChat_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var photoRoot = ResolvePhotoRoot();
            if (!fullPath.StartsWith(photoRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private void OnDebounceElapsed(object? state)
        {
            List<string> files;
            lock (_watchLock)
            {
                files = _pendingFiles.ToList();
                _pendingFiles.Clear();
            }

            if (files.Count == 0)
            {
                return;
            }

            _ = Task.Run(() => ProcessPendingFilesAsync(files));
        }

        private async Task ProcessPendingFilesAsync(IReadOnlyCollection<string> files)
        {
            foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (IsRecentlyProcessed(file))
                    {
                        continue;
                    }

                    if (!await WaitUntilFileReadyAsync(file).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var result = await TranslatePhotoAsync(file).ConfigureAwait(false);
                    if (result == null)
                    {
                        continue;
                    }

                    MarkProcessed(file);
                    TextRecognized?.Invoke(this, result);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Photo translation failed. file={File}", file);
                }
            }
        }

        private bool IsRecentlyProcessed(string fullPath)
        {
            lock (_watchLock)
            {
                PruneProcessed_NoLock();
                return _recentProcessed.TryGetValue(fullPath, out var processedAtUtc)
                    && DateTime.UtcNow - processedAtUtc <= ProcessedRetention;
            }
        }

        private void MarkProcessed(string fullPath)
        {
            lock (_watchLock)
            {
                PruneProcessed_NoLock();
                _recentProcessed[fullPath] = DateTime.UtcNow;
            }
        }

        private void PruneProcessed_NoLock()
        {
            var cutoff = DateTime.UtcNow - ProcessedRetention;
            foreach (var item in _recentProcessed.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToArray())
            {
                _recentProcessed.Remove(item);
            }
        }

        private static async Task<bool> WaitUntilFileReadyAsync(string fullPath)
        {
            const int maxAttempts = 8;
            long previousLength = -1;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!File.Exists(fullPath))
                {
                    return false;
                }

                try
                {
                    var info = new FileInfo(fullPath);
                    var currentLength = info.Length;

                    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (currentLength > 0 && currentLength == previousLength)
                    {
                        return true;
                    }

                    previousLength = currentLength;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                await Task.Delay(350).ConfigureAwait(false);
            }

            return false;
        }

        private async Task<TranscriptionResult?> TranslatePhotoAsync(string fullPath)
        {
            var targets = ResolveTargetLanguages();
            if (targets.Count == 0)
            {
                _logger.Debug("Photo translation skipped because no target languages are enabled.");
                return null;
            }

            var sourceLanguage = "auto";
            var fileBytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
            var mediaType = GetMediaType(fullPath);

            HttpRequestMessage CreateRequest()
            {
                var requestUrl = new Uri($"{BackendUrlHelper.BuildApiBaseUrl(_config)}func/photoTranslate", UriKind.Absolute);
                var content = new MultipartFormDataContent();

                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                content.Add(fileContent, "file", Path.GetFileName(fullPath));
                content.Add(new StringContent(sourceLanguage), "sourceLanguage");
                content.Add(new StringContent(targets[0]), "targetLanguage");

                if (targets.Count > 1)
                {
                    content.Add(new StringContent(targets[1]), "targetLanguage2");
                }

                if (targets.Count > 2)
                {
                    content.Add(new StringContent(targets[2]), "targetLanguage3");
                }

                return new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = content
                };
            }

            using var response = await SendRequestWithRetryAsync(CreateRequest).ConfigureAwait(false);
            if (response == null)
            {
                _logger.Warning("Photo translation skipped because auth token is missing.");
                return null;
            }

            if ((int)response.StatusCode == 430)
            {
                var tooManyContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _authService.NotifyTooManyRequests(source: "图片翻译", backendDetail: tooManyContent, channel: "Photo", stage: "photo");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.Warning("Photo translation API failed. status={StatusCode}, file={File}, body={Body}", response.StatusCode, fullPath, error);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<PhotoTranslationApiResponse>().ConfigureAwait(false);
            if (payload == null)
            {
                return null;
            }

            var original = payload.Text ?? string.Empty;
            var t1 = payload.TranslatedText ?? string.Empty;
            var t2 = payload.TranslatedText2 ?? string.Empty;
            var t3 = payload.TranslatedText3 ?? string.Empty;

            if (string.IsNullOrWhiteSpace(original)
                && string.IsNullOrWhiteSpace(t1)
                && string.IsNullOrWhiteSpace(t2)
                && string.IsNullOrWhiteSpace(t3))
            {
                return null;
            }

            return new TranscriptionResult
            {
                Source = "Photo",
                Text = string.Join("||", new[] { original, t1, t2, t3 }),
                Original = original,
                Translation = t1,
                Translation2 = t2,
                Translation3 = t3,
                SourceLanguage = payload.DetectedSourceLanguage ?? sourceLanguage,
                TargetLanguage = targets.ElementAtOrDefault(0) ?? string.Empty,
                TargetLanguage2 = targets.ElementAtOrDefault(1) ?? string.Empty,
                TargetLanguage3 = targets.ElementAtOrDefault(2) ?? string.Empty,
                IsFinal = true,
                Confidence = 1.0f,
                CapturedAtUtc = File.GetLastWriteTimeUtc(fullPath)
            };
        }

        private async Task<HttpResponseMessage?> SendRequestWithRetryAsync(
            Func<HttpRequestMessage> createRequest)
        {
            return await _authService.SendAuthorizedAsync(
                "Photo translation",
                createRequest,
                (request, cancellationToken) => _httpClient.SendAsync(request, cancellationToken)).ConfigureAwait(false);
        }

        private IReadOnlyList<string> ResolveTargetLanguages()
        {
            var textSendTargets = _config.ResolveEnabledTargetLanguages("TextSend");
            if (textSendTargets.Count > 0)
            {
                return textSendTargets;
            }

            var micTargets = _config.ResolveEnabledTargetLanguages("Mic");
            if (micTargets.Count > 0)
            {
                return micTargets;
            }

            var fallback = (_config.TargetTranslationLanguage ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fallback) && !string.Equals(fallback, "none", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { fallback };
            }

            return Array.Empty<string>();
        }
        private static string GetMediaType(string fullPath)
        {
            return Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png"
            };
        }

        private sealed class PhotoTranslationApiResponse
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("translatedText")]
            public string TranslatedText { get; set; } = string.Empty;

            [JsonPropertyName("translatedText2")]
            public string TranslatedText2 { get; set; } = string.Empty;

            [JsonPropertyName("translatedText3")]
            public string TranslatedText3 { get; set; } = string.Empty;

            [JsonPropertyName("detectedSourceLanguage")]
            public string DetectedSourceLanguage { get; set; } = string.Empty;
        }
    }
}
