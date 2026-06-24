using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
        private readonly HashSet<string> _processingFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _recentProcessed = new(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;
        private string _watchRoot = string.Empty;
        private bool _watcherRunning;
        private bool _nextPhotoTranslationArmed;
        private int _activePhotoTranslationCount;
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
                Timeout = TimeSpan.FromSeconds(240)
            };
        }

        public event EventHandler<TranscriptionResult>? TextRecognized;
        public event EventHandler<bool>? NextPhotoTranslationArmedChanged;
        public event EventHandler<bool>? PhotoTranslationProcessingChanged;

        public bool IsRunning
        {
            get
            {
                lock (_watchLock)
                {
                    return _watcherRunning;
                }
            }
        }
        public bool IsNextPhotoTranslationArmed
        {
            get
            {
                lock (_watchLock)
                {
                    return _nextPhotoTranslationArmed;
                }
            }
        }

        public bool IsPhotoTranslationProcessing
        {
            get
            {
                lock (_watchLock)
                {
                    return _activePhotoTranslationCount > 0;
                }
            }
        }

        public void Start()
        {
            if (_disposed || (!_config.EnablePhotoTranslation && !IsNextPhotoTranslationArmed))
            {
                return;
            }

            EnsureWatcherStarted();
        }

        public bool RequestNextPhotoTranslation()
        {
            if (_disposed)
            {
                return false;
            }

            SetNextPhotoTranslationArmed(true);
            if (EnsureWatcherStarted())
            {
                return true;
            }

            SetNextPhotoTranslationArmed(false);
            return false;
        }

        public void CancelNextPhotoTranslation()
        {
            SetNextPhotoTranslationArmed(false);
            if (!_config.EnablePhotoTranslation)
            {
                StopWatcher(disarmNextPhotoRequest: false);
            }
        }

        public async Task<TranscriptionResult?> TranslateImageAsync(string imagePath)
        {
            if (_disposed)
            {
                return null;
            }

            var fullPath = NormalizeFilePath(imagePath);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                _logger.Warning("Photo translation skipped because image path is missing. path={ImagePath}", imagePath);
                return null;
            }

            BeginPhotoTranslationProcessing();
            try
            {
                return await TranslatePhotoAsync(fullPath).ConfigureAwait(false);
            }
            finally
            {
                EndPhotoTranslationProcessing();
            }
        }

        private bool EnsureWatcherStarted()
        {
            var photoRoot = ResolvePhotoRoot();
            var watchRoot = ResolveWatchRoot(photoRoot);
            if (string.IsNullOrWhiteSpace(watchRoot) || !Directory.Exists(watchRoot))
            {
                _logger.Warning("Photo translation watcher not started. watchRoot missing: {WatchRoot}", watchRoot);
                return false;
            }

            if (_watcher?.EnableRaisingEvents == true
                && string.Equals(_watchRoot, watchRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            StopWatcher(disarmNextPhotoRequest: false);

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
            _watchRoot = watchRoot;
            lock (_watchLock)
            {
                _watcherRunning = true;
            }

            _logger.Information("Photo translation watcher started. photoRoot={PhotoRoot}, watchRoot={WatchRoot}", photoRoot, watchRoot);
            return true;
        }

        public void Stop()
        {
            StopWatcher(disarmNextPhotoRequest: true);
        }

        private void StopWatcher(bool disarmNextPhotoRequest)
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
            _watchRoot = string.Empty;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            lock (_watchLock)
            {
                _watcherRunning = false;
                _pendingFiles.Clear();
                _processingFiles.Clear();
            }

            var processingChanged = false;
            lock (_watchLock)
            {
                if (_activePhotoTranslationCount > 0)
                {
                    _activePhotoTranslationCount = 0;
                    processingChanged = true;
                }
            }

            if (processingChanged)
            {
                RaisePhotoTranslationProcessingChanged(false);
            }

            if (disarmNextPhotoRequest)
            {
                SetNextPhotoTranslationArmed(false);
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
                var fullPath = NormalizeFilePath(e.FullPath);
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
                var fullPath = NormalizeFilePath(file);
                try
                {
                    if (!IsRunning || !HasPhotoTranslationDemand())
                    {
                        continue;
                    }

                    if (!TryBeginProcessingFile(fullPath))
                    {
                        continue;
                    }

                    try
                    {
                        if (!TryReserveTranslationForReadyFile())
                        {
                            continue;
                        }

                        BeginPhotoTranslationProcessing();
                        _logger.Debug("Photo translation processing started. file={File}", fullPath);
                        try
                        {
                            if (!await WaitUntilFileReadyAsync(fullPath).ConfigureAwait(false))
                            {
                                _logger.Debug("Photo translation skipped because file was not ready. file={File}", fullPath);
                                continue;
                            }

                            var result = await TranslatePhotoAsync(fullPath).ConfigureAwait(false);
                            if (result == null)
                            {
                                continue;
                            }

                            if (!IsRunning)
                            {
                                _logger.Debug("Photo translation result discarded because watcher stopped. file={File}", fullPath);
                                continue;
                            }

                            MarkProcessed(fullPath);
                            TextRecognized?.Invoke(this, result);
                        }
                        finally
                        {
                            _logger.Debug("Photo translation processing ended. file={File}", fullPath);
                            EndPhotoTranslationProcessing();
                        }
                    }
                    finally
                    {
                        EndProcessingFile(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Photo translation failed. file={File}", fullPath);
                }
            }
        }

        private bool HasPhotoTranslationDemand()
        {
            if (_config.EnablePhotoTranslation)
            {
                return true;
            }

            lock (_watchLock)
            {
                return _nextPhotoTranslationArmed;
            }
        }

        private bool TryReserveTranslationForReadyFile()
        {
            var shouldTranslate = _config.EnablePhotoTranslation;
            var shouldDisarm = false;

            lock (_watchLock)
            {
                if (_nextPhotoTranslationArmed)
                {
                    _nextPhotoTranslationArmed = false;
                    shouldTranslate = true;
                    shouldDisarm = true;
                }
            }

            if (shouldDisarm)
            {
                RaiseNextPhotoTranslationArmedChanged(false);
            }

            return shouldTranslate;
        }

        private void SetNextPhotoTranslationArmed(bool isArmed)
        {
            var changed = false;
            lock (_watchLock)
            {
                if (_nextPhotoTranslationArmed != isArmed)
                {
                    _nextPhotoTranslationArmed = isArmed;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseNextPhotoTranslationArmedChanged(isArmed);
            }
        }

        private void RaiseNextPhotoTranslationArmedChanged(bool isArmed)
        {
            try
            {
                NextPhotoTranslationArmedChanged?.Invoke(this, isArmed);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Photo translation armed-state handler failed.");
            }
        }

        private void BeginPhotoTranslationProcessing()
        {
            var changed = false;
            lock (_watchLock)
            {
                _activePhotoTranslationCount++;
                changed = _activePhotoTranslationCount == 1;
            }

            if (changed)
            {
                RaisePhotoTranslationProcessingChanged(true);
            }
        }

        private void EndPhotoTranslationProcessing()
        {
            var changed = false;
            lock (_watchLock)
            {
                if (_activePhotoTranslationCount > 0)
                {
                    _activePhotoTranslationCount--;
                    changed = _activePhotoTranslationCount == 0;
                }
            }

            if (changed)
            {
                RaisePhotoTranslationProcessingChanged(false);
            }
        }

        private void RaisePhotoTranslationProcessingChanged(bool isProcessing)
        {
            try
            {
                PhotoTranslationProcessingChanged?.Invoke(this, isProcessing);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Photo translation processing-state handler failed.");
            }
        }

        private bool TryBeginProcessingFile(string fullPath)
        {
            var normalized = NormalizeFilePath(fullPath);
            lock (_watchLock)
            {
                PruneProcessed_NoLock();
                if (_recentProcessed.TryGetValue(normalized, out var processedAtUtc)
                    && DateTime.UtcNow - processedAtUtc <= ProcessedRetention)
                {
                    return false;
                }

                return _processingFiles.Add(normalized);
            }
        }

        private void EndProcessingFile(string fullPath)
        {
            var normalized = NormalizeFilePath(fullPath);
            lock (_watchLock)
            {
                _processingFiles.Remove(normalized);
            }
        }

        private void MarkProcessed(string fullPath)
        {
            var normalized = NormalizeFilePath(fullPath);
            lock (_watchLock)
            {
                PruneProcessed_NoLock();
                _recentProcessed[normalized] = DateTime.UtcNow;
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
            var resultMode = AppConfig.NormalizePhotoTranslationMode(_config.PhotoTranslationMode);
            var targets = ResolveTargetLanguages();
            if (targets.Count == 0)
            {
                _logger.Debug("Photo translation skipped because no target languages are enabled.");
                return CreatePhotoTranslationMessageResult(
                    fullPath,
                    resultMode,
                    targets,
                    "拍照翻译失败：未配置拍照翻译目标语言。");
            }

            var sourceLanguage = "auto";
            var fileBytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
            var mediaType = GetMediaType(fullPath);
            _logger.Debug("Photo translation request. file={File}, targetLanguages={TargetLanguages}, mode={Mode}", fullPath, string.Join(",", targets), resultMode);

            HttpRequestMessage CreateRequest(string relativePath)
            {
                var requestUrl = new Uri($"{BackendUrlHelper.BuildApiBaseUrl(_config)}{relativePath}", UriKind.Absolute);
                var content = new MultipartFormDataContent();
                var engine = ResolvePhotoTranslationEngine(resultMode);

                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                content.Add(fileContent, "file", Path.GetFileName(fullPath));
                content.Add(new StringContent(sourceLanguage), "sourceLanguage");
                content.Add(new StringContent(targets[0]), "targetLanguage");
                content.Add(new StringContent(targets.ElementAtOrDefault(1) ?? "none"), "targetLanguage2");
                content.Add(new StringContent(targets.ElementAtOrDefault(2) ?? "none"), "targetLanguage3");
                content.Add(new StringContent(resultMode), "mode");
                content.Add(new StringContent(resultMode), "displayMode");
                content.Add(new StringContent(resultMode), "resultMode");
                content.Add(new StringContent(engine), "engine");

                return new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = content
                };
            }

            HttpResponseMessage? response = null;
            var endpointUsed = string.Empty;
            var endpoints = GetPhotoTranslationEndpointCandidates();
            try
            {
                for (var i = 0; i < endpoints.Length; i++)
                {
                    var relativePath = endpoints[i];
                    response = await SendRequestWithRetryAsync(() => CreateRequest(relativePath)).ConfigureAwait(false);
                    if (response == null)
                    {
                        _logger.Warning("Photo translation skipped because auth token is missing.");
                        return CreatePhotoTranslationMessageResult(
                            fullPath,
                            resultMode,
                            targets,
                            "拍照翻译失败：未获取到后端授权，请重新登录后再试。");
                    }

                    if (IsEndpointFallbackStatus(response.StatusCode) && i < endpoints.Length - 1)
                    {
                        _logger.Debug("Photo translation endpoint unavailable. endpoint={Endpoint}, status={StatusCode}; trying fallback.", relativePath, response.StatusCode);
                        response.Dispose();
                        response = null;
                        continue;
                    }

                    endpointUsed = relativePath;
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.Warning(ex, "Photo translation API request timed out. file={File}", fullPath);
                return CreatePhotoTranslationMessageResult(
                    fullPath,
                    resultMode,
                    targets,
                    string.Equals(resultMode, AppConfig.PhotoTranslationModeImage, StringComparison.OrdinalIgnoreCase)
                        ? "拍照翻译失败：请求超时，PaddleOCR 图片翻译可能仍在处理或后端无响应。"
                        : "拍照翻译失败：请求超时，直接图片翻译可能仍在处理或后端无响应。");
            }
            catch (HttpRequestException ex)
            {
                _logger.Warning(ex, "Photo translation API request failed. file={File}", fullPath);
                return CreatePhotoTranslationMessageResult(
                    fullPath,
                    resultMode,
                    targets,
                    "拍照翻译失败：无法连接后端图片翻译接口。",
                    ex.Message);
            }

            if (response == null)
            {
                return CreatePhotoTranslationMessageResult(
                    fullPath,
                    resultMode,
                    targets,
                    "拍照翻译失败：后端图片翻译接口没有返回响应。");
            }

            using (response)
            {
                if ((int)response.StatusCode == 430)
                {
                    var tooManyContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _authService.NotifyTooManyRequests(source: "图片翻译", backendDetail: tooManyContent, channel: "Photo", stage: "photo");
                    return CreatePhotoTranslationMessageResult(
                        fullPath,
                        resultMode,
                        targets,
                        "拍照翻译失败：今日图片翻译额度或频率已达到限制。",
                        ExtractErrorDetail(tooManyContent));
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _logger.Warning("Photo translation API failed. endpoint={Endpoint}, status={StatusCode}, file={File}, body={Body}", endpointUsed, response.StatusCode, fullPath, error);
                    var errorDetail = ExtractErrorDetail(error);
                    return CreatePhotoTranslationMessageResult(
                        fullPath,
                        resultMode,
                        targets,
                        BuildPhotoTranslationFailureMessage(response.StatusCode, errorDetail),
                        errorDetail);
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.Debug(
                    "Photo translation API response. endpoint={Endpoint}, file={File}, targetLanguage={TargetLanguage}, mode={Mode}, bodyLength={BodyLength}",
                    endpointUsed,
                    fullPath,
                    targets[0],
                    resultMode,
                    responseBody.Length);

                ParsedPhotoTranslationPayload payload;
                try
                {
                    payload = ParsePhotoTranslationPayload(responseBody);
                }
                catch (JsonException ex)
                {
                    _logger.Warning(ex, "Photo translation API returned invalid JSON. file={File}, body={Body}", fullPath, responseBody);
                    return CreatePhotoTranslationMessageResult(
                        fullPath,
                        resultMode,
                        targets,
                        "拍照翻译失败：后端返回了无法解析的结果。",
                        responseBody);
                }

                var translatedImagePath = await ResolvePhotoImageReferenceAsync(payload.ImageReference, fullPath).ConfigureAwait(false);
                var original = payload.Text ?? string.Empty;
                var t1 = payload.TranslatedText ?? string.Empty;
                var t2 = payload.TranslatedText2 ?? string.Empty;
                var t3 = payload.TranslatedText3 ?? string.Empty;

                if (string.IsNullOrWhiteSpace(original)
                    && string.IsNullOrWhiteSpace(t1)
                    && string.IsNullOrWhiteSpace(t2)
                    && string.IsNullOrWhiteSpace(t3)
                    && string.IsNullOrWhiteSpace(translatedImagePath))
                {
                    return CreatePhotoTranslationMessageResult(
                        fullPath,
                        resultMode,
                        targets,
                        "拍照翻译没有返回可显示的文字或图片结果。");
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
                    TargetLanguage2 = string.Empty,
                    TargetLanguage3 = string.Empty,
                    PhotoTranslationMode = resultMode,
                    PhotoSourceImagePath = fullPath,
                    PhotoTranslatedImagePath = translatedImagePath,
                    IsFinal = true,
                    Confidence = 1.0f,
                    CapturedAtUtc = File.GetLastWriteTimeUtc(fullPath)
                };
            }
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
            return _config.ResolveEnabledTargetLanguages("Photo");
        }

        private static string[] GetPhotoTranslationEndpointCandidates()
        {
            return new[]
            {
                "func/photoTranslate"
            };
        }

        private static string ResolvePhotoTranslationEngine(string resultMode)
        {
            return string.Equals(resultMode, AppConfig.PhotoTranslationModeImage, StringComparison.OrdinalIgnoreCase)
                ? "paddleocr"
                : "gemma";
        }

        private static TranscriptionResult CreatePhotoTranslationMessageResult(
            string fullPath,
            string resultMode,
            IReadOnlyList<string> targets,
            string message,
            string? detail = null)
        {
            var displayDetail = TrimForDisplay(detail);
            return new TranscriptionResult
            {
                Source = "Photo",
                Text = string.Join("||", new[] { displayDetail, message, string.Empty, string.Empty }),
                Original = displayDetail,
                Translation = message,
                SourceLanguage = "auto",
                TargetLanguage = targets.ElementAtOrDefault(0) ?? string.Empty,
                TargetLanguage2 = targets.ElementAtOrDefault(1) ?? string.Empty,
                TargetLanguage3 = targets.ElementAtOrDefault(2) ?? string.Empty,
                PhotoTranslationMode = AppConfig.PhotoTranslationModeText,
                PhotoSourceImagePath = fullPath,
                PhotoTranslatedImagePath = string.Empty,
                IsFinal = true,
                Confidence = 0.0f,
                CapturedAtUtc = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.UtcNow
            };
        }

        private static string BuildPhotoTranslationFailureMessage(HttpStatusCode statusCode, string errorDetail)
        {
            if (statusCode == HttpStatusCode.ServiceUnavailable
                && (errorDetail.Contains("OCR image translation upstream", StringComparison.OrdinalIgnoreCase)
                    || errorDetail.Contains("All connection attempts failed", StringComparison.OrdinalIgnoreCase)))
            {
                return "拍照翻译失败：后端无法连接 PaddleOCR 图片翻译服务，请检查 OCR_IMAGE_TRANSLATE_BASE_URL 和 PaddleOCR 服务状态。";
            }

            if (statusCode == HttpStatusCode.NotFound)
            {
                return "拍照翻译失败：后端没有找到图片翻译接口 /func/photoTranslate。";
            }

            return $"拍照翻译失败：后端返回 {(int)statusCode} {statusCode}。";
        }

        private static string ExtractErrorDetail(string responseBody)
        {
            var body = (responseBody ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var detail = FindStringProperty(document.RootElement, "detail", "message", "error", "error_description");
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    return TrimForDisplay(detail, 500);
                }
            }
            catch (JsonException)
            {
            }

            if (body.StartsWith("<", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return TrimForDisplay(body, 500);
        }

        private static string TrimForDisplay(string? value, int maxLength = 300)
        {
            var text = (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Trim();
            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }

            if (text.Length <= maxLength)
            {
                return text;
            }

            return text[..maxLength] + "...";
        }

        private static bool IsEndpointFallbackStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.NotFound
                || statusCode == HttpStatusCode.MethodNotAllowed;
        }

        private static ParsedPhotoTranslationPayload ParsePhotoTranslationPayload(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new ParsedPhotoTranslationPayload();
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            return new ParsedPhotoTranslationPayload
            {
                Text = FindStringProperty(root, "text", "ocrText", "ocr_text", "originalText", "original_text", "sourceText", "source_text", "recognizedText", "recognized_text"),
                TranslatedText = FindStringProperty(root, "translatedText", "translated_text", "translation", "resultText", "result_text", "targetText", "target_text"),
                TranslatedText2 = FindStringProperty(root, "translatedText2", "translated_text2", "translation2", "targetText2", "target_text2"),
                TranslatedText3 = FindStringProperty(root, "translatedText3", "translated_text3", "translation3", "targetText3", "target_text3"),
                DetectedSourceLanguage = FindStringProperty(root, "detectedSourceLanguage", "detected_source_language", "sourceLanguage", "source_language"),
                ImageReference = FindStringProperty(root,
                    "translatedImagePath",
                    "translated_image_path",
                    "resultImagePath",
                    "result_image_path",
                    "imagePath",
                    "image_path",
                    "translatedImageUrl",
                    "translated_image_url",
                    "resultImageUrl",
                    "result_image_url",
                    "imageUrl",
                    "image_url",
                    "translatedImageBase64",
                    "translated_image_base64",
                    "resultImageBase64",
                    "result_image_base64",
                    "imageBase64",
                    "image_base64",
                    "translatedImage",
                    "translated_image",
                    "resultImage",
                    "result_image",
                    "image")
            };
        }

        private static string FindStringProperty(JsonElement root, params string[] propertyNames)
        {
            if (TryFindStringProperty(root, propertyNames, out var value))
            {
                return value;
            }

            foreach (var wrapperName in new[] { "data", "result", "payload" })
            {
                if (TryGetObjectProperty(root, wrapperName, out var nested))
                {
                    value = FindStringProperty(nested, propertyNames);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return TryFindStringPropertyRecursive(root, propertyNames, 5, out value)
                ? value
                : string.Empty;
        }

        private static bool TryFindStringProperty(JsonElement element, string[] propertyNames, out string value)
        {
            value = string.Empty;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var propertyName in propertyNames)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => property.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => string.Empty
                    };
                    return !string.IsNullOrWhiteSpace(value);
                }
            }

            return false;
        }

        private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindStringPropertyRecursive(
            JsonElement element,
            string[] propertyNames,
            int depth,
            out string value)
        {
            value = string.Empty;
            if (depth <= 0)
            {
                return false;
            }

            if (TryFindStringProperty(element, propertyNames, out value))
            {
                return true;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (TryFindStringPropertyRecursive(property.Value, propertyNames, depth - 1, out value))
                        {
                            return true;
                        }
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (TryFindStringPropertyRecursive(item, propertyNames, depth - 1, out value))
                        {
                            return true;
                        }
                    }
                    break;
            }

            return false;
        }

        private async Task<string> ResolvePhotoImageReferenceAsync(string? imageReference, string sourceImagePath)
        {
            var reference = (imageReference ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(reference))
            {
                return string.Empty;
            }

            if (TryDecodeBase64Image(reference, out var imageBytes, out var extension))
            {
                return await SavePhotoImageBytesAsync(imageBytes, extension).ConfigureAwait(false);
            }

            if (TryNormalizeImagePathReference(reference, sourceImagePath, out var pathReference))
            {
                if (TryCreateHttpUri(pathReference, out var uri))
                {
                    return await DownloadPhotoImageAsync(uri, sourceImagePath).ConfigureAwait(false);
                }

                return pathReference;
            }

            if (TryResolveBackendImageUri(reference, out var backendUri))
            {
                return await DownloadPhotoImageAsync(backendUri, sourceImagePath).ConfigureAwait(false);
            }

            return reference;
        }

        private static async Task<string> SavePhotoImageBytesAsync(byte[] imageBytes, string extension)
        {
            var outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRCLS",
                "PhotoTranslation");
            Directory.CreateDirectory(outputDirectory);

            var outputPath = Path.Combine(
                outputDirectory,
                $"photo-translation-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}");
            await File.WriteAllBytesAsync(outputPath, imageBytes).ConfigureAwait(false);
            return outputPath;
        }

        private static bool TryNormalizeImagePathReference(string reference, string sourceImagePath, out string normalized)
        {
            normalized = string.Empty;
            if (reference.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    var localPath = uri.LocalPath;
                    if (File.Exists(localPath))
                    {
                        normalized = localPath;
                        return true;
                    }

                    return false;
                }

                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    normalized = reference;
                    return true;
                }
            }

            if (Path.IsPathRooted(reference))
            {
                var fullPath = Path.GetFullPath(reference);
                if (File.Exists(fullPath))
                {
                    normalized = fullPath;
                    return true;
                }

                return false;
            }

            string extension;
            try
            {
                extension = Path.GetExtension(reference);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!SupportedExtensions.Contains(extension))
            {
                return false;
            }

            var sourceDirectory = Path.GetDirectoryName(sourceImagePath);
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
            {
                var combined = Path.GetFullPath(Path.Combine(sourceDirectory, reference));
                if (File.Exists(combined))
                {
                    normalized = combined;
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveBackendImageUri(string reference, out Uri uri)
        {
            uri = default!;
            if (TryCreateHttpUri(reference, out uri))
            {
                return true;
            }

            var normalized = reference.Trim();
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var looksLikeImageRoute = normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains('/')
                || normalized.Contains('?');
            try
            {
                var extension = Path.GetExtension(normalized.Split('?', '#')[0]);
                looksLikeImageRoute = looksLikeImageRoute || SupportedExtensions.Contains(extension);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!looksLikeImageRoute)
            {
                return false;
            }

            var serviceRoot = BackendUrlHelper.BuildServiceRootUrl(_config).TrimEnd('/') + "/";
            if (!Uri.TryCreate(serviceRoot, UriKind.Absolute, out var baseUri))
            {
                return false;
            }

            uri = normalized.StartsWith("/", StringComparison.Ordinal)
                ? new Uri(baseUri, normalized.TrimStart('/'))
                : new Uri(baseUri, normalized);
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private async Task<string> DownloadPhotoImageAsync(Uri uri, string sourceImagePath)
        {
            try
            {
                using var response = await SendRequestWithRetryAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, uri)).ConfigureAwait(false);
                if (response == null)
                {
                    return uri.ToString();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning("Photo translation image download failed. url={Url}, status={StatusCode}", uri, response.StatusCode);
                    return uri.ToString();
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (imageBytes.Length == 0)
                {
                    return uri.ToString();
                }

                var extension = DetectImageExtension(imageBytes, GuessImageExtension(uri.LocalPath, sourceImagePath));
                return await SavePhotoImageBytesAsync(imageBytes, extension).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Photo translation image download failed. url={Url}", uri);
                return uri.ToString();
            }
        }

        private static bool TryCreateHttpUri(string reference, out Uri uri)
        {
            uri = default!;
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var candidate))
            {
                return false;
            }

            if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            uri = candidate;
            return true;
        }

        private static string GuessImageExtension(string? reference, string sourceImagePath)
        {
            foreach (var candidate in new[] { reference, sourceImagePath })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                try
                {
                    var extension = Path.GetExtension(candidate.Split('?', '#')[0]);
                    if (SupportedExtensions.Contains(extension))
                    {
                        return extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                            ? ".jpg"
                            : extension.ToLowerInvariant();
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return ".png";
        }

        private static bool TryDecodeBase64Image(string reference, out byte[] imageBytes, out string extension)
        {
            imageBytes = Array.Empty<byte>();
            extension = ".png";

            var raw = reference.Trim();
            var commaIndex = raw.IndexOf(',');
            if (raw.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            {
                extension = raw[..commaIndex].Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
                            raw[..commaIndex].Contains("jpg", StringComparison.OrdinalIgnoreCase)
                    ? ".jpg"
                    : raw[..commaIndex].Contains("webp", StringComparison.OrdinalIgnoreCase)
                        ? ".webp"
                        : ".png";
                raw = raw[(commaIndex + 1)..];
            }

            try
            {
                imageBytes = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                return false;
            }

            extension = DetectImageExtension(imageBytes, extension);
            return imageBytes.Length > 0;
        }

        private static string DetectImageExtension(byte[] imageBytes, string fallback)
        {
            if (imageBytes.Length >= 4
                && imageBytes[0] == 0x89
                && imageBytes[1] == 0x50
                && imageBytes[2] == 0x4E
                && imageBytes[3] == 0x47)
            {
                return ".png";
            }

            if (imageBytes.Length >= 2 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
            {
                return ".jpg";
            }

            if (imageBytes.Length >= 12
                && imageBytes[0] == 0x52
                && imageBytes[1] == 0x49
                && imageBytes[2] == 0x46
                && imageBytes[3] == 0x46
                && imageBytes[8] == 0x57
                && imageBytes[9] == 0x45
                && imageBytes[10] == 0x42
                && imageBytes[11] == 0x50)
            {
                return ".webp";
            }

            return string.IsNullOrWhiteSpace(fallback) ? ".png" : fallback;
        }

        private static string NormalizeFilePath(string fullPath)
        {
            return string.IsNullOrWhiteSpace(fullPath)
                ? string.Empty
                : Path.GetFullPath(fullPath.Trim());
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

        private sealed class ParsedPhotoTranslationPayload
        {
            public string Text { get; set; } = string.Empty;
            public string TranslatedText { get; set; } = string.Empty;
            public string TranslatedText2 { get; set; } = string.Empty;
            public string TranslatedText3 { get; set; } = string.Empty;
            public string DetectedSourceLanguage { get; set; } = string.Empty;
            public string ImageReference { get; set; } = string.Empty;
        }
    }
}
