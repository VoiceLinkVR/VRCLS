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
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Update
{
    public sealed class ModelUpdateService : IModelUpdateService
    {
        private static readonly ILogger _logger = Log.ForContext<ModelUpdateService>();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly AppConfig _config;
        private readonly IModelPathResolver _modelPathResolver;
        private readonly HttpClient _httpClient;
        private readonly object _watchLock = new();
        private readonly HashSet<string> _pendingChangedFiles = new(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher? _watcher;
        private Timer? _watchDebounceTimer;
        private bool _disposed;

        public ModelUpdateService(AppConfig config, IModelPathResolver modelPathResolver)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
        }

        public event EventHandler<ModelHotReloadEventArgs>? ModelsChanged;

        public async Task<IReadOnlyList<ModelPackageInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            var endpoint = ResolveCatalogEndpoint();
            _logger.Information("Fetching model catalog. endpoint={Endpoint}", endpoint);

            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response).ConfigureAwait(false);
                _logger.Warning("Model catalog request failed. status={StatusCode}, body={Body}", response.StatusCode, body);
                return Array.Empty<ModelPackageInfo>();
            }

            var payload = await response.Content.ReadFromJsonAsync<ModelCatalogResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
            return payload?.Models ?? new List<ModelPackageInfo>();
        }

        public async Task<IReadOnlyList<ModelPackageInfo>> EnsureRequiredModelsAsync(CancellationToken cancellationToken = default)
        {
            var installed = new List<ModelPackageInfo>();
            var missingModels = await GetMissingRequiredModelsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var model in missingModels)
            {
                if (string.IsNullOrWhiteSpace(model.DownloadUrl))
                {
                    _logger.Warning("Required model type is missing in catalog. type={Type}", model.Type);
                    continue;
                }

                if (await DownloadAndInstallModelAsync(model, progress: null, cancellationToken).ConfigureAwait(false))
                {
                    installed.Add(model);
                }
            }

            return installed;
        }

        public Task<IReadOnlyList<ModelPackageInfo>> GetMissingRequiredModelsAsync(CancellationToken cancellationToken = default)
        {
            return GetMissingModelsAsync(ResolveRequiredModelTypes(), cancellationToken);
        }

        public async Task<IReadOnlyList<ModelPackageInfo>> GetMissingModelsAsync(IEnumerable<string> modelTypes, CancellationToken cancellationToken = default)
        {
            var requestedTypes = NormalizeModelTypes(modelTypes);
            if (requestedTypes.Count == 0)
            {
                return Array.Empty<ModelPackageInfo>();
            }

            var missingTypes = requestedTypes
                .Where(type => !IsModelTypeInstalled(type))
                .ToList();
            if (missingTypes.Count == 0)
            {
                return Array.Empty<ModelPackageInfo>();
            }

            var available = await GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<ModelPackageInfo>(missingTypes.Count);
            foreach (var type in missingTypes)
            {
                var candidate = SelectBestCandidate(available, type);
                if (candidate != null)
                {
                    result.Add(candidate);
                    continue;
                }

                result.Add(new ModelPackageInfo
                {
                    Name = type,
                    Type = type,
                    Required = true
                });
            }

            return result;
        }

        public async Task<bool> DownloadAndInstallModelAsync(ModelPackageInfo model, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.DownloadUrl))
            {
                return false;
            }

            if (!Uri.TryCreate(model.DownloadUrl, UriKind.Absolute, out var downloadUri))
            {
                _logger.Warning("Model url is invalid. model={Model}, url={Url}", model.Name, model.DownloadUrl);
                return false;
            }

            var cacheDir = Path.Combine(GetDataRoot(), "model-cache");
            Directory.CreateDirectory(cacheDir);
            var fileName = Path.GetFileName(downloadUri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"{SanitizeFileName(model.Name)}-{SanitizeFileName(model.Version)}.zip";
            }

            var packagePath = Path.Combine(cacheDir, fileName);
            if (!await DownloadFileAsync(downloadUri, packagePath, progress, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (!await VerifyChecksumAsync(packagePath, model.Checksum, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warning("Model checksum mismatch. model={Model}", model.Name);
                return false;
            }

            var installed = await InstallModelPackageAsync(model, packagePath, cancellationToken).ConfigureAwait(false);
            if (installed)
            {
                RaiseModelsChanged("download-install", new[] { model.Name });
            }

            return installed;
        }

        public void StartWatching()
        {
            if (_disposed || _watcher != null)
            {
                return;
            }

            var root = ResolveModelRoot();
            Directory.CreateDirectory(root);

            _watchDebounceTimer = new Timer(OnWatchDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Created += OnWatchedFileChanged;
            _watcher.Changed += OnWatchedFileChanged;
            _watcher.Renamed += OnWatchedFileChanged;
            _watcher.Deleted += OnWatchedFileChanged;
            _watcher.EnableRaisingEvents = true;

            _logger.Information("Model watcher started. root={Root}", root);
        }

        public void StopWatching()
        {
            if (_watcher == null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnWatchedFileChanged;
            _watcher.Changed -= OnWatchedFileChanged;
            _watcher.Renamed -= OnWatchedFileChanged;
            _watcher.Deleted -= OnWatchedFileChanged;
            _watcher.Dispose();
            _watcher = null;

            _watchDebounceTimer?.Dispose();
            _watchDebounceTimer = null;

            lock (_watchLock)
            {
                _pendingChangedFiles.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatching();
            _httpClient.Dispose();
        }

        private Uri ResolveCatalogEndpoint()
        {
            return StaticContentPathHelper.ResolveStaticContentUri(_config, StaticContentPathHelper.ModelCatalogRelativePath);
        }

        private HashSet<string> ResolveRequiredModelTypes()
        {
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "vad"
            };

            if (_config.RecognitionBackendModeValue == RecognitionBackendMode.Local)
            {
                required.Add(_config.LocalRecognitionModelValue == LocalRecognitionModel.SenseVoice ? "sensevoice" : "sherpa");
            }

            if (_config.ControlEnabled)
            {
                required.Add("sherpa");
            }

            return required;
        }

        private bool IsModelTypeInstalled(string type)
        {
            var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "vad" => !string.IsNullOrWhiteSpace(_modelPathResolver.ResolveVadModelPath()),
                "sherpa" => !string.IsNullOrWhiteSpace(_modelPathResolver.ResolveSherpaModelDir()),
                "sensevoice" => HasSenseVoiceModel(),
                _ => false
            };
        }

        private bool HasSenseVoiceModel()
        {
            if (!string.IsNullOrWhiteSpace(_config.SenseVoiceModelDir))
            {
                var configured = Path.Combine(_config.SenseVoiceModelDir, "model.onnx");
                if (File.Exists(configured))
                {
                    return true;
                }
            }

            var root = ResolveModelRoot();
            var defaultPath = Path.Combine(root, "sensevoice-small", "model.onnx");
            if (File.Exists(defaultPath))
            {
                return true;
            }

            if (!Directory.Exists(root))
            {
                return false;
            }

            return Directory.EnumerateFiles(root, "model.onnx", SearchOption.AllDirectories).Any();
        }

        private static ModelPackageInfo? SelectBestCandidate(IReadOnlyList<ModelPackageInfo> models, string type)
        {
            return models
                .Where(m => string.Equals(m.Type, type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => ParseVersion(m.Version))
                .FirstOrDefault();
        }

        private static Version ParseVersion(string? text)
        {
            if (Version.TryParse(text, out var version))
            {
                return version;
            }

            return new Version(0, 0, 0, 0);
        }

        private static List<string> NormalizeModelTypes(IEnumerable<string>? modelTypes)
        {
            if (modelTypes == null)
            {
                return new List<string>();
            }

            return modelTypes
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<bool> InstallModelPackageAsync(ModelPackageInfo model, string packagePath, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(packagePath);
            if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                return await InstallFromZipAsync(model, packagePath, cancellationToken).ConfigureAwait(false);
            }

            return InstallSingleFile(model, packagePath);
        }

        private async Task<bool> InstallFromZipAsync(ModelPackageInfo model, string packagePath, CancellationToken cancellationToken)
        {
            var tempExtractRoot = Path.Combine(Path.GetTempPath(), "vrcls-model-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractRoot);

            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(packagePath, tempExtractRoot, overwriteFiles: true), cancellationToken).ConfigureAwait(false);

                var sourceRoot = ResolveArchiveSourceRoot(model, tempExtractRoot);
                if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
                {
                    return false;
                }

                if (IsVadModel(model))
                {
                    var vadFile = Directory.EnumerateFiles(sourceRoot, "*.onnx", SearchOption.AllDirectories).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(vadFile))
                    {
                        return false;
                    }

                    var destination = ResolveVadDestination(model);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(vadFile, destination, overwrite: true);
                    _config.VadModelPath = destination;
                }
                else
                {
                    var destinationDir = ResolveModelDestinationDirectory(model);
                    if (Directory.Exists(destinationDir))
                    {
                        Directory.Delete(destinationDir, recursive: true);
                    }

                    Directory.CreateDirectory(destinationDir);
                    CopyDirectory(sourceRoot, destinationDir);

                    if (string.Equals(model.Type, "sherpa", StringComparison.OrdinalIgnoreCase))
                    {
                        _config.SherpaModelDir = destinationDir;
                    }
                    else if (string.Equals(model.Type, "sensevoice", StringComparison.OrdinalIgnoreCase))
                    {
                        _config.SenseVoiceModelDir = destinationDir;
                    }
                }

                PersistConfigSnapshot();
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Install model from zip failed. model={Model}, package={Package}", model.Name, packagePath);
                return false;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempExtractRoot, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

        private bool InstallSingleFile(ModelPackageInfo model, string packagePath)
        {
            try
            {
                if (IsVadModel(model))
                {
                    var destination = ResolveVadDestination(model);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(packagePath, destination, overwrite: true);
                    _config.VadModelPath = destination;
                    PersistConfigSnapshot();
                    return true;
                }

                var destinationDir = ResolveModelDestinationDirectory(model);
                Directory.CreateDirectory(destinationDir);
                var destinationFile = Path.Combine(destinationDir, Path.GetFileName(packagePath));
                File.Copy(packagePath, destinationFile, overwrite: true);

                if (string.Equals(model.Type, "sherpa", StringComparison.OrdinalIgnoreCase))
                {
                    _config.SherpaModelDir = destinationDir;
                }
                else if (string.Equals(model.Type, "sensevoice", StringComparison.OrdinalIgnoreCase))
                {
                    _config.SenseVoiceModelDir = destinationDir;
                }

                PersistConfigSnapshot();
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Install model file failed. model={Model}, file={File}", model.Name, packagePath);
                return false;
            }
        }

        private string ResolveArchiveSourceRoot(ModelPackageInfo model, string extractedRoot)
        {
            if (!string.IsNullOrWhiteSpace(model.ArchiveRoot))
            {
                var configured = Path.Combine(extractedRoot, model.ArchiveRoot);
                if (Directory.Exists(configured))
                {
                    return configured;
                }
            }

            var directories = Directory.GetDirectories(extractedRoot);
            var files = Directory.GetFiles(extractedRoot);
            if (directories.Length == 1 && files.Length == 0)
            {
                return directories[0];
            }

            return extractedRoot;
        }

        private string ResolveModelDestinationDirectory(ModelPackageInfo model)
        {
            var root = ResolveModelRoot();
            var installPath = model.InstallPath;
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                if (Path.IsPathRooted(installPath))
                {
                    return installPath;
                }

                return Path.GetFullPath(Path.Combine(root, installPath));
            }

            return Path.Combine(root, SanitizeFileName(model.Name));
        }

        private string ResolveVadDestination(ModelPackageInfo model)
        {
            var root = ResolveModelRoot();
            if (!string.IsNullOrWhiteSpace(model.InstallPath))
            {
                var installPath = model.InstallPath;
                if (Path.IsPathRooted(installPath))
                {
                    return installPath;
                }

                return Path.GetFullPath(Path.Combine(root, installPath));
            }

            return Path.Combine(root, "silero_vad.onnx");
        }

        private string ResolveModelRoot()
        {
            if (!string.IsNullOrWhiteSpace(_config.PreModelsDir))
            {
                return Path.GetFullPath(_config.PreModelsDir);
            }

            return Path.Combine(AppContext.BaseDirectory, "pre-models");
        }

        private static string GetDataRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS");
        }

        private static bool IsVadModel(ModelPackageInfo model)
        {
            return string.Equals(model.Type, "vad", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeFileName(string? text)
        {
            var value = text ?? string.Empty;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '-');
            }

            return string.IsNullOrWhiteSpace(value) ? "model" : value.Trim();
        }

        private async Task<bool> DownloadFileAsync(Uri source, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response).ConfigureAwait(false);
                _logger.Warning("Model download failed. url={Url}, status={StatusCode}, body={Body}", source, response.StatusCode, body);
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

        private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.FullPath) || !IsModelFile(e.FullPath))
            {
                return;
            }

            lock (_watchLock)
            {
                _pendingChangedFiles.Add(e.FullPath);
                _watchDebounceTimer?.Change(900, Timeout.Infinite);
            }
        }

        private void OnWatchDebounceElapsed(object? state)
        {
            List<string> files;
            lock (_watchLock)
            {
                if (_pendingChangedFiles.Count == 0)
                {
                    return;
                }

                files = _pendingChangedFiles.ToList();
                _pendingChangedFiles.Clear();
            }

            RaiseModelsChanged("filesystem-change", files);
        }

        private void RaiseModelsChanged(string reason, IReadOnlyList<string> changedFiles)
        {
            try
            {
                ModelsChanged?.Invoke(this, new ModelHotReloadEventArgs(reason, changedFiles));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ModelsChanged event handler failed.");
            }
        }

        private static bool IsModelFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".onnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);
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
                var root = GetDataRoot();
                Directory.CreateDirectory(root);
                var configPath = Path.Combine(root, "config.json");
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to persist config after model install.");
            }
        }
    }
}
