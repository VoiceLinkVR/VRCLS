using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Configurations;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Events;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;
using Serilog;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.Update
{
    public sealed class NetSparkleAppUpdateService : IAppUpdateService, IDisposable
    {
        private const string BuildMetadataPublicKeyName = "NetSparklePublicKey";
        private const string PrimaryPublicKeyEnvVar = "VRCLS_NETSPARKLE_PUBLIC_KEY";
        private const string FallbackPublicKeyEnvVar = "SPARKLE_PUBLIC_KEY";

        private static readonly Serilog.ILogger _logger = Log.ForContext<NetSparkleAppUpdateService>();

        private readonly object _syncRoot = new();
        private readonly SparkleUpdater? _sparkle;
        private readonly string? _configurationError;

        private AppCastItem? _pendingUpdateItem;
        private string? _downloadedInstallerPath;
        private TaskCompletionSource<string?>? _downloadCompletionSource;
        private IProgress<double>? _activeProgress;

        public NetSparkleAppUpdateService(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            CurrentVersion = AppVersionHelper.GetCurrentVersion();

            if (TryCreateUpdater(config, out var sparkle, out var error))
            {
                _sparkle = sparkle ?? throw new InvalidOperationException("NetSparkle updater was not created.");
                _configurationError = null;
                WireUpdaterEvents(_sparkle);
            }
            else
            {
                _sparkle = null;
                _configurationError = error;
                _logger.Warning("NetSparkle app updates are disabled: {Reason}", error);
            }

            State = AppUpdateState.Idle;
        }

        public Version CurrentVersion { get; }

        public AppUpdateState State { get; private set; }

        public AppUpdateInfo? PendingUpdate { get; private set; }

        public async Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var sparkle = EnsureUpdaterConfigured();

            if (HasDownloadedUpdate())
            {
                PendingUpdate = BuildPendingUpdateInfo(_pendingUpdateItem);
                State = PendingUpdate == null ? AppUpdateState.Idle : AppUpdateState.ReadyToRestart;
                return PendingUpdate;
            }

            State = AppUpdateState.Checking;
            try
            {
                var updateInfo = await sparkle.CheckForUpdatesQuietly(false).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                _pendingUpdateItem = GetLatestUpdate(updateInfo);
                PendingUpdate = BuildPendingUpdateInfo(_pendingUpdateItem);
                State = PendingUpdate == null ? AppUpdateState.NoUpdate : AppUpdateState.UpdateAvailable;

                if (PendingUpdate == null)
                {
                    ResetDownloadedUpdateState();
                }

                return PendingUpdate;
            }
            catch (Exception ex)
            {
                State = AppUpdateState.Failed;
                _logger.Warning(ex, "Failed to check for app updates through NetSparkle.");
                throw;
            }
        }

        public async Task<bool> DownloadUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var sparkle = EnsureUpdaterConfigured();

            if (HasDownloadedUpdate())
            {
                PendingUpdate = BuildPendingUpdateInfo(_pendingUpdateItem);
                State = PendingUpdate == null ? AppUpdateState.Idle : AppUpdateState.ReadyToRestart;
                progress?.Report(100);
                return PendingUpdate != null;
            }

            if (_pendingUpdateItem == null)
            {
                await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_pendingUpdateItem == null)
            {
                State = AppUpdateState.NoUpdate;
                return false;
            }

            var completionSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_syncRoot)
            {
                _downloadCompletionSource = completionSource;
                _activeProgress = progress;
                _downloadedInstallerPath = null;
            }

            State = AppUpdateState.Downloading;
            try
            {
                using var registration = cancellationToken.Register(() => sparkle.CancelFileDownload());

                await sparkle.InitAndBeginDownload(_pendingUpdateItem).ConfigureAwait(false);
                var downloadPath = await completionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(downloadPath))
                {
                    State = AppUpdateState.Failed;
                    return false;
                }

                lock (_syncRoot)
                {
                    _downloadedInstallerPath = downloadPath;
                }

                PendingUpdate = BuildPendingUpdateInfo(_pendingUpdateItem);
                State = PendingUpdate == null ? AppUpdateState.Idle : AppUpdateState.ReadyToRestart;
                progress?.Report(100);
                return PendingUpdate != null;
            }
            catch (Exception ex)
            {
                State = AppUpdateState.Failed;
                _logger.Warning(ex, "Failed to download app update through NetSparkle.");
                throw;
            }
            finally
            {
                lock (_syncRoot)
                {
                    _downloadCompletionSource = null;
                    _activeProgress = null;
                }
            }
        }

        public async Task<bool> ApplyUpdateAndRestartAsync(CancellationToken cancellationToken = default)
        {
            var sparkle = EnsureUpdaterConfigured();
            cancellationToken.ThrowIfCancellationRequested();

            if (_pendingUpdateItem == null || string.IsNullOrWhiteSpace(_downloadedInstallerPath))
            {
                return false;
            }

            State = AppUpdateState.Applying;
            try
            {
                await sparkle.InstallUpdate(_pendingUpdateItem, _downloadedInstallerPath).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                State = AppUpdateState.Failed;
                _logger.Warning(ex, "Failed to apply app update through NetSparkle.");
                throw;
            }
        }

        public void Dispose()
        {
            if (_sparkle == null)
            {
                return;
            }

            _sparkle.DownloadMadeProgress -= OnDownloadMadeProgress;
            _sparkle.DownloadFinished -= OnDownloadFinished;
            _sparkle.DownloadCanceled -= OnDownloadCanceled;
            _sparkle.DownloadHadError -= OnDownloadHadError;
            _sparkle.DownloadedFileIsCorrupt -= OnDownloadedFileIsCorrupt;
            _sparkle.DownloadedFileThrewWhileCheckingSignature -= OnDownloadedFileThrewWhileCheckingSignature;
            _sparkle.InstallUpdateFailed -= OnInstallUpdateFailed;
            _sparkle.InstallerProcessAboutToStart -= OnInstallerProcessAboutToStart;
            _sparkle.Dispose();
        }

        private static bool TryCreateUpdater(AppConfig config, out SparkleUpdater? sparkle, out string? error)
        {
            sparkle = null;
            error = null;

            if (string.IsNullOrWhiteSpace(config.AppUpdateFeedUrl))
            {
                error = "App update feed URL is not configured.";
                return false;
            }

            if (!Uri.TryCreate(config.AppUpdateFeedUrl, UriKind.Absolute, out _))
            {
                error = $"App update feed URL is invalid: {config.AppUpdateFeedUrl}";
                return false;
            }

            var publicKey = ResolvePublicKey();
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                error = $"NetSparkle public key is missing. Set assembly metadata '{BuildMetadataPublicKeyName}' or environment variable '{PrimaryPublicKeyEnvVar}'.";
                return false;
            }

            var signatureVerifier = new Ed25519Checker(SecurityMode.Strict, publicKey.Trim(), null, true, 1024 * 1024);
            if (!signatureVerifier.HasValidKeyInformation())
            {
                error = "NetSparkle public key is invalid.";
                return false;
            }

            var applicationDirectory = ResolveApplicationDirectory();
            var restartExecutableName = ResolveRestartExecutableName();
            var installerArguments = BuildInstallerArguments(applicationDirectory);

            sparkle = new SparkleUpdater(config.AppUpdateFeedUrl.Trim(), signatureVerifier, ResolveReferenceAssembly())
            {
                Configuration = new RegistryConfiguration(new VrclsAssemblyAccessor()),
                UIFactory = null,
                UseNotificationToast = false,
                LogWriter = new SerilogNetSparkleLogger(),
                RelaunchAfterUpdate = true,
                UserInteractionMode = UserInteractionMode.DownloadNoInstall,
                CustomInstallerArguments = installerArguments,
                RestartExecutablePath = applicationDirectory,
                RestartExecutableName = restartExecutableName
            };

            ConfigureChannelFilter(sparkle, config.UpdateChannel);
            _logger.Information(
                "Configured NetSparkle updater. Feed={FeedUrl}; InstalledVersion={InstalledVersion}; DisplayVersion={DisplayVersion}; ReferenceAssembly={ReferenceAssembly}; RestartDirectory={RestartDirectory}; RestartExecutable={RestartExecutable}; InstallerArguments={InstallerArguments}",
                config.AppUpdateFeedUrl.Trim(),
                sparkle.Configuration.InstalledVersion,
                AppVersionHelper.GetDisplayVersion(),
                ResolveReferenceAssembly(),
                applicationDirectory,
                restartExecutableName,
                installerArguments);
            return true;
        }

        private static void ConfigureChannelFilter(SparkleUpdater sparkle, string? updateChannel)
        {
            if (sparkle == null || string.IsNullOrWhiteSpace(updateChannel))
            {
                return;
            }

            var filter = new ChannelAppCastFilter(sparkle.LogWriter)
            {
                RemoveOlderItems = true,
                KeepItemsWithNoChannelInfo = true
            };
            filter.ChannelSearchNames.Add(updateChannel.Trim());
            sparkle.AppCastHelper.AppCastFilter = filter;
        }

        private static string BuildInstallerArguments(string installDirectory)
        {
            return string.Join(
                " ",
                "/SP-",
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/CURRENTUSER",
                "/LOG",
                $"/DIR={QuoteInstallerArgument(installDirectory)}");
        }

        private static string QuoteInstallerArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static string ResolveApplicationDirectory()
        {
            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            {
                return NormalizeDirectoryPath(AppContext.BaseDirectory);
            }

            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrWhiteSpace(processDirectory))
                {
                    return NormalizeDirectoryPath(processDirectory);
                }
            }

            return NormalizeDirectoryPath(Directory.GetCurrentDirectory());
        }

        private static string ResolveRestartExecutableName()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var processFileName = Path.GetFileName(Environment.ProcessPath);
                if (!string.IsNullOrWhiteSpace(processFileName))
                {
                    return processFileName;
                }
            }

            return "VRCLS.exe";
        }

        private static string NormalizeDirectoryPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return fullPath;
            }

            var trimmedRoot = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(trimmed, trimmedRoot, StringComparison.OrdinalIgnoreCase)
                ? root ?? fullPath
                : trimmed;
        }

        private static string ResolveReferenceAssembly()
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null && !string.IsNullOrWhiteSpace(entryAssembly.Location))
            {
                return entryAssembly.Location;
            }

            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Environment.ProcessPath;
            }

            return Assembly.GetExecutingAssembly().Location;
        }

        private static string? ResolvePublicKey()
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var metadataKey = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(attribute.Key, BuildMetadataPublicKeyName, StringComparison.OrdinalIgnoreCase))
                ?.Value;

            if (!string.IsNullOrWhiteSpace(metadataKey))
            {
                return metadataKey;
            }

            var envValue = Environment.GetEnvironmentVariable(PrimaryPublicKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return envValue;
            }

            return Environment.GetEnvironmentVariable(FallbackPublicKeyEnvVar);
        }

        private static AppCastItem? GetLatestUpdate(UpdateInfo? updateInfo)
        {
            if (updateInfo?.Status != UpdateStatus.UpdateAvailable || updateInfo.Updates == null || updateInfo.Updates.Count == 0)
            {
                return null;
            }

            return updateInfo.Updates[0];
        }

        private SparkleUpdater EnsureUpdaterConfigured()
        {
            if (_sparkle != null)
            {
                return _sparkle;
            }

            throw new InvalidOperationException(_configurationError ?? "NetSparkle updater is not configured.");
        }

        private void WireUpdaterEvents(SparkleUpdater sparkle)
        {
            sparkle.DownloadMadeProgress += OnDownloadMadeProgress;
            sparkle.DownloadFinished += OnDownloadFinished;
            sparkle.DownloadCanceled += OnDownloadCanceled;
            sparkle.DownloadHadError += OnDownloadHadError;
            sparkle.DownloadedFileIsCorrupt += OnDownloadedFileIsCorrupt;
            sparkle.DownloadedFileThrewWhileCheckingSignature += OnDownloadedFileThrewWhileCheckingSignature;
            sparkle.InstallUpdateFailed += OnInstallUpdateFailed;
            sparkle.InstallerProcessAboutToStart += OnInstallerProcessAboutToStart;
        }

        private void OnDownloadMadeProgress(object? sender, AppCastItem item, ItemDownloadProgressEventArgs args)
        {
            lock (_syncRoot)
            {
                _activeProgress?.Report(args.ProgressPercentage);
            }
        }

        private void OnDownloadFinished(AppCastItem item, string downloadPath)
        {
            _logger.Information("NetSparkle downloaded update package: {Path}", downloadPath);

            lock (_syncRoot)
            {
                _pendingUpdateItem = item;
                _downloadedInstallerPath = downloadPath;
                _downloadCompletionSource?.TrySetResult(downloadPath);
            }
        }

        private void OnDownloadCanceled(AppCastItem item, string downloadPath)
        {
            _logger.Information("NetSparkle download canceled: {Path}", downloadPath);

            lock (_syncRoot)
            {
                _downloadCompletionSource?.TrySetCanceled();
            }
        }

        private void OnDownloadHadError(AppCastItem item, string? downloadPath, Exception exception)
        {
            _logger.Warning(exception, "NetSparkle download failed: {Path}", downloadPath);

            lock (_syncRoot)
            {
                _downloadCompletionSource?.TrySetException(exception);
            }
        }

        private void OnDownloadedFileIsCorrupt(AppCastItem item, string downloadPath)
        {
            var exception = new InvalidOperationException($"Downloaded update file is corrupt: {downloadPath}");
            _logger.Warning(exception, "NetSparkle downloaded file is corrupt.");

            lock (_syncRoot)
            {
                _downloadCompletionSource?.TrySetException(exception);
            }
        }

        private void OnDownloadedFileThrewWhileCheckingSignature(AppCastItem item, string downloadPath)
        {
            var exception = new InvalidOperationException($"Downloaded update file failed signature validation: {downloadPath}");
            _logger.Warning(exception, "NetSparkle signature validation failed for downloaded file.");

            lock (_syncRoot)
            {
                _downloadCompletionSource?.TrySetException(exception);
            }
        }

        private bool OnInstallUpdateFailed(InstallUpdateFailureReason reason, string? installPath)
        {
            _logger.Warning("NetSparkle failed to install update. Reason={Reason}; Path={Path}", reason, installPath);
            State = AppUpdateState.Failed;
            return false;
        }

        private bool OnInstallerProcessAboutToStart(Process process, string downloadFilePath)
        {
            if (process?.StartInfo != null)
            {
                _logger.Information(
                    "NetSparkle starting installer. FileName={FileName}; Arguments={Arguments}; WorkingDirectory={WorkingDirectory}; DownloadPath={DownloadPath}",
                    process.StartInfo.FileName,
                    process.StartInfo.Arguments,
                    process.StartInfo.WorkingDirectory,
                    downloadFilePath);
            }

            return true;
        }

        private bool HasDownloadedUpdate()
        {
            lock (_syncRoot)
            {
                return _pendingUpdateItem != null && !string.IsNullOrWhiteSpace(_downloadedInstallerPath);
            }
        }

        private void ResetDownloadedUpdateState()
        {
            lock (_syncRoot)
            {
                _pendingUpdateItem = null;
                _downloadedInstallerPath = null;
            }
        }

        private static AppUpdateInfo? BuildPendingUpdateInfo(AppCastItem? item)
        {
            if (item == null)
            {
                return null;
            }

            return new AppUpdateInfo
            {
                TargetVersion = item.Version ?? string.Empty,
                DisplayVersion = string.IsNullOrWhiteSpace(item.ShortVersion) ? item.Version ?? string.Empty : item.ShortVersion,
                ReleaseNotes = item.Description ?? string.Empty,
                DownloadSizeBytes = item.UpdateSize
            };
        }

        private sealed class SerilogNetSparkleLogger : NetSparkleUpdater.Interfaces.ILogger
        {
            public void PrintMessage(string message, params object[]? args)
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    return;
                }

                if (args == null || args.Length == 0)
                {
                    _logger.Debug("NetSparkle: {Message}", message);
                    return;
                }

                _logger.Debug("NetSparkle: " + message, args);
            }
        }

        private sealed class VrclsAssemblyAccessor : IAssemblyAccessor
        {
            private readonly Assembly _assembly;

            public VrclsAssemblyAccessor(Assembly? assembly = null)
            {
                _assembly = assembly
                    ?? Assembly.GetEntryAssembly()
                    ?? Assembly.GetExecutingAssembly();
            }

            public string AssemblyCompany => GetAttribute<AssemblyCompanyAttribute>(attribute => attribute.Company, "VoiceLinkVR");

            public string AssemblyCopyright => GetAttribute<AssemblyCopyrightAttribute>(attribute => attribute.Copyright, string.Empty);

            public string AssemblyDescription => GetAttribute<AssemblyDescriptionAttribute>(attribute => attribute.Description, string.Empty);

            public string AssemblyTitle => GetAttribute<AssemblyTitleAttribute>(attribute => attribute.Title, _assembly.GetName().Name ?? "VRCLS");

            public string AssemblyProduct => GetAttribute<AssemblyProductAttribute>(attribute => attribute.Product, AssemblyTitle);

            public string AssemblyVersion => AppVersionHelper.GetCurrentVersion(_assembly).ToString();

            private string GetAttribute<TAttribute>(Func<TAttribute, string?> selector, string fallback)
                where TAttribute : Attribute
            {
                var attribute = _assembly.GetCustomAttribute<TAttribute>();
                var value = attribute == null ? null : selector(attribute);
                return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            }
        }
    }
}
