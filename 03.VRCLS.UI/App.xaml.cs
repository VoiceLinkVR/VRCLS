using System;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.External;
using VRCLS.Services.Auth;
using VRCLS.UI.ViewModels;
using VRCLS.UI.Views;
using System.Text.Json;
using System.Linq;
using VRCLS.Services.Audio;
using VRCLS.Services.Recognition;
using VRCLS.Services.Translation;
using VRCLS.Services.Tts;
using VRCLS.Services.Input;
using VRCLS.Services.Command;
using VRCLS.UI.Services;
using VRCLS.Services.VR;
using VRCLS.Services.Update;
using VRCLS.Services.Photo;
using Serilog;
using Serilog.Events;

namespace VRCLS.UI
{
    public partial class App : Application
    {
        private sealed class StartupModelPreparationResult
        {
            public bool ContinueStartup { get; init; } = true;
            public bool LocalRecognitionDisabled { get; init; }
            public string LocalRecognitionModelName { get; init; } = string.Empty;
            public string MissingModelList { get; init; } = string.Empty;
        }

        private ServiceProvider? _serviceProvider;
        private AppConfig? _config;
        private MainViewModel? _mainViewModel;
        private RuntimeLogBuffer? _runtimeLogBuffer;
        private bool _globalExceptionHandlersRegistered;
        private Mutex? _singleInstanceMutex;

        public static IServiceProvider? Services => (Current as App)?._serviceProvider;

        public static T GetRequiredService<T>() where T : notnull
        {
            var provider = Services ?? throw new InvalidOperationException("Service provider is not initialized.");
            return provider.GetRequiredService<T>();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                if (!TryAcquireSingleInstance())
                {
                    MessageBox.Show("VRCLS 已经在运行，本次不会再启动第二个实例。", "VRCLS", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }

                _runtimeLogBuffer = new RuntimeLogBuffer();

                // 配置Serilog日志记录器
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                    .Enrich.FromLogContext()
                    .WriteTo.Sink(new RuntimeLogSink(_runtimeLogBuffer))
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        Path.Combine(GetLogDirectoryPath(), "vrcls-.log"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate:
                        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                        // Use UTF-8 with BOM so Windows tooling (e.g., PowerShell 5.1 default encoding,
                        // older Notepad versions) can reliably detect the encoding.
                        encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                        retainedFileCountLimit: 10)
                    .CreateLogger();

                RegisterGlobalExceptionHandlers();

                Log.Information("==================== VRCLS 应用程序启动 ====================");
                Log.Information("版本: {Version}", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
                Log.Information("运行环境: {Environment}", Environment.OSVersion);

                _config = LoadConfig();
                ValidateConfigOnStartup(_config);
                ApplyUiLanguage(_config);
                Log.Debug("配置加载完成，配置路径: {ConfigPath}", GetConfigPath());

                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                Log.Information("服务容器构建完成");

                var startupModelPreparation = EnsureStartupRequiredModels(_serviceProvider, _config);
                if (!startupModelPreparation.ContinueStartup)
                {
                    Log.Information("Startup cancelled because required models were not prepared.");
                    Shutdown(0);
                    return;
                }

                // Warm up embedded python translator when any translation route uses a python-based engine.
                if (_config.UsesEmbeddedTranslation("Mic") || _config.UsesEmbeddedTranslation("Desktop"))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var embedded = _serviceProvider.GetService<EmbeddedTranslatorService>();
                            if (embedded != null)
                            {
                                await embedded.WarmupAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "翻译服务预热失败");
                        }
                    });
                }

                var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                _mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
                MainWindow = mainWindow;
                mainWindow.DataContext = _mainViewModel;
                mainWindow.Show();

                if (startupModelPreparation.LocalRecognitionDisabled)
                {
                    _ = PromptForStartupLocalRecognitionModelDownloadAsync(startupModelPreparation);
                }

                var modelUpdateService = _serviceProvider.GetRequiredService<IModelUpdateService>();
                modelUpdateService.StartWatching();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunStartupUpdateJobsAsync(_serviceProvider, _config).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Startup update task failed.");
                    }
                });

                // 启动时自动登录（静默，失败后由首页和用户中心提示用户手动处理）
                var authService = _serviceProvider.GetRequiredService<IBackendAuthService>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await authService.LoginAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "启动时自动登录失败");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "应用程序启动时发生致命错误");
                ShowFriendlyErrorDialog(
                    "应用程序启动失败，程序无法继续运行。",
                    ex,
                    "启动错误",
                    MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            if (_globalExceptionHandlersRegistered)
            {
                return;
            }

            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            _globalExceptionHandlersRegistered = true;
        }

        private void UnregisterGlobalExceptionHandlers()
        {
            if (!_globalExceptionHandlersRegistered)
            {
                return;
            }

            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            _globalExceptionHandlersRegistered = false;
        }

        private static string GetLogDirectoryPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS",
                "logs");
        }

        private static string BuildFriendlyErrorMessage(string summary, Exception ex)
        {
            var technicalMessage = string.IsNullOrWhiteSpace(ex.Message)
                ? ex.GetType().Name
                : ex.Message.Trim();

            return string.Join(Environment.NewLine, new[]
            {
                summary,
                string.Empty,
                "详细信息已写入日志文件。",
                $"日志目录：{GetLogDirectoryPath()}",
                string.Empty,
                $"技术信息：{technicalMessage}"
            });
        }

        private static void ShowFriendlyErrorDialog(string summary, Exception ex, string title, MessageBoxImage image)
        {
            try
            {
                MessageBox.Show(
                    BuildFriendlyErrorMessage(summary, ex),
                    title,
                    MessageBoxButton.OK,
                    image);
            }
            catch
            {
            }
        }

        private void ConfigureServices(ServiceCollection services)
        {
            Log.Debug("开始配置依赖注入服务");

            services.AddSingleton(_config ?? new AppConfig());
            var appConfig = _config ?? new AppConfig();

            services.AddHttpClient(FastApiRecognitionService.HttpClientName, client =>
            {
                client.BaseAddress = BackendUrlHelper.BuildApiBaseAddress(appConfig);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 8,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

            services.AddHttpClient(ServerTranslationService.HttpClientName, client =>
            {
                client.BaseAddress = BackendUrlHelper.BuildApiBaseAddress(appConfig);
                client.Timeout = TimeSpan.FromSeconds(20);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 8,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

            services.AddHttpClient(ServerTtsService.HttpClientName, client =>
            {
                client.BaseAddress = BackendUrlHelper.BuildApiBaseAddress(appConfig);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 4,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

            services.AddHttpClient(PhotoTranslationService.HttpClientName, client =>
            {
                client.BaseAddress = BackendUrlHelper.BuildApiBaseAddress(appConfig);
                client.Timeout = TimeSpan.FromSeconds(90);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

            services.AddSingleton<IBackendAuthService, BackendAuthService>();
            services.AddSingleton<IOscService>(sp => new OscService(sp.GetRequiredService<AppConfig>()));
            services.AddSingleton<IModelPathResolver, ModelPathResolver>();
            services.AddSingleton<IAudioPipelineFactory, AudioPipelineFactory>();

            // Translation services: backend + optional local python translators.
            services.AddSingleton<ServerTranslationService>(sp =>
                new ServerTranslationService(
                    sp.GetRequiredService<AppConfig>(),
                    sp.GetRequiredService<IBackendAuthService>(),
                    sp.GetRequiredService<IHttpClientFactory>()));
            services.AddSingleton<EmbeddedTranslatorService>();
            services.AddSingleton<OpenAiTranslationService>();
            services.AddSingleton<ITranslationService>(sp =>
                new TranslationServiceManager(
                    sp.GetRequiredService<AppConfig>(),
                    sp.GetRequiredService<ServerTranslationService>(),
                    sp.GetRequiredService<EmbeddedTranslatorService>(),
                    sp.GetRequiredService<OpenAiTranslationService>()));

            // Update services: app update, model update, translation profile hot update.
            services.AddSingleton<IAppUpdateService, NetSparkleAppUpdateService>();
            services.AddSingleton<IModelUpdateService, ModelUpdateService>();
            services.AddSingleton<ITranslationHotUpdateService, TranslationHotUpdateService>();

            // TTS 服务：本地 + 服务器
            services.AddSingleton<SystemSpeechTtsService>();
            services.AddSingleton<ServerTtsService>(sp =>
                new ServerTtsService(
                    sp.GetRequiredService<AppConfig>(),
                    sp.GetRequiredService<IBackendAuthService>(),
                    sp.GetRequiredService<IHttpClientFactory>()));
            services.AddSingleton<ITtsService>(sp =>
                new TtsServiceManager(
                    sp.GetRequiredService<AppConfig>(),
                    sp.GetRequiredService<SystemSpeechTtsService>(),
                    sp.GetRequiredService<ServerTtsService>()));

            services.AddSingleton<IHotkeyService, HotkeyService>();
            services.AddSingleton<ICommandService>(sp =>
                new CommandService(
                    sp.GetRequiredService<AppConfig>(),
                    sp.GetRequiredService<IOscService>()));
            services.AddSingleton<IPhotoTranslationService, PhotoTranslationService>();

            // SteamVR Overlay (optional)
            services.AddSingleton<ISteamVRService>(sp =>
                new SteamVROverlayService(sp.GetRequiredService<AppConfig>()));

            services.AddSingleton<MainViewModel>();
            services.AddSingleton(_runtimeLogBuffer ?? new RuntimeLogBuffer());
            services.AddSingleton<VadCalibrationService>();
            services.AddSingleton<SettingsViewModel>(sp =>
                new SettingsViewModel(
                    sp.GetRequiredService<AppConfig>(),
                    sp.GetRequiredService<IBackendAuthService>(),
                    sp.GetRequiredService<ITtsService>(),
                    sp.GetRequiredService<ISteamVRService>(),
                    sp.GetRequiredService<IAppUpdateService>(),
                    sp.GetRequiredService<IModelUpdateService>(),
                    sp.GetRequiredService<ITranslationHotUpdateService>(),
                    sp.GetRequiredService<RuntimeLogBuffer>()));
            services.AddSingleton<MainWindow>();

            Log.Debug("依赖注入服务配置完成");
        }

        private static void ValidateConfigOnStartup(AppConfig config)
        {
            var result = config.Validate();
            if (result.IsValid)
            {
                return;
            }

            foreach (var error in result.Errors)
            {
                Log.Warning("配置校验: {Message}", error);
            }

            var message = "检测到配置项异常，程序将继续启动，但建议修复：\n\n- "
                + string.Join("\n- ", result.Errors);
            MessageBox.Show(message, "配置校验警告", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private AppConfig LoadConfig()
        {
            try
            {
                var configPath = GetConfigPath();
                Log.Debug("尝试从 {ConfigPath} 加载配置", configPath);

                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        var configChanged = false;
                        NormalizeRecognitionSourceLanguage(config);
                        configChanged |= NormalizeServiceBaseUrl(config);
                        configChanged |= NormalizeUpdateServerSettings(config, json);
                        configChanged |= NormalizeQuickStartGuideState(config, json);
                        configChanged |= NormalizeInvalidVadModelPath(config);
                        TranslatorRuntimeCapabilitiesLoader.TryApplyInstalledRuntimeCapabilities(config);
                        if (config.NormalizeLegacyVadDefaults())
                        {
                            configChanged = true;
                            Log.Information("检测到旧版 VAD 最长时长配置为 0，已迁移为默认值 {DefaultValue}s。", AppConfig.DefaultVadMaxSegmentSecondsValue);
                        }

                        if (configChanged)
                        {
                            SaveConfigToDisk(config);
                        }
                    }
                    Log.Information("配置加载成功");
                    return config ?? new AppConfig();
                }

                Log.Warning("配置文件不存在，使用默认配置");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载配置文件失败");
                Console.WriteLine($"加载配置失败: {ex.Message}");
            }

            Log.Debug("创建新的默认配置");
            var fallbackConfig = new AppConfig();
            TranslatorRuntimeCapabilitiesLoader.TryApplyInstalledRuntimeCapabilities(fallbackConfig);
            return fallbackConfig;
        }

        private static bool NormalizeInvalidVadModelPath(AppConfig config)
        {
            if (config == null
                || string.IsNullOrWhiteSpace(config.VadModelPath)
                || File.Exists(config.VadModelPath))
            {
                return false;
            }

            var bundledVadPath = Path.Combine(AppContext.BaseDirectory, "pre-models", "silero_vad.onnx");
            if (!File.Exists(bundledVadPath))
            {
                return false;
            }

            Log.Information(
                "配置中的 VAD 模型路径已失效，检测到内置模型，已清空旧路径以使用当前安装目录内置模型。OldPath={OldPath}; BundledPath={BundledPath}",
                config.VadModelPath,
                bundledVadPath);

            config.VadModelPath = string.Empty;
            return true;
        }

        private static bool NormalizeQuickStartGuideState(AppConfig config, string? rawJson)
        {
            if (config == null || JsonHasRootProperty(rawJson, "hasSeenQuickStartGuide"))
            {
                return false;
            }

            // Do not surprise existing users with the new onboarding overlay after they upgrade.
            config.HasSeenQuickStartGuide = true;
            Log.Information("检测到已有配置缺少首次引导标记，已默认为已完成引导。");
            return true;
        }

        private static bool NormalizeServiceBaseUrl(AppConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                return false;
            }

            var current = config.BaseUrl.Trim().TrimEnd('/');
            if (!string.Equals(current, AppConfig.LegacyDefaultServiceBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            config.BaseUrl = AppConfig.DefaultServiceBaseUrl;
            Log.Information("检测到旧默认 baseUrl，已迁移为: {BaseUrl}", config.BaseUrl);
            return true;
        }

        private static bool JsonHasRootProperty(string? rawJson, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(rawJson) || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(rawJson);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(propertyName, out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool NormalizeUpdateServerSettings(AppConfig config, string? rawJson)
        {
            if (config == null)
            {
                return false;
            }

            var changed = false;

            if (TryResolveConfiguredUpdateServerUrl(rawJson, out var configuredUrl))
            {
                if (!string.Equals(config.UpdateServerUrl, configuredUrl, StringComparison.Ordinal))
                {
                    config.UpdateServerUrl = configuredUrl;
                    Log.Information("已规范化 updateServerUrl: {UpdateServerUrl}", configuredUrl);
                    changed = true;
                }
            }
            else if (TryResolveLegacyUpdateServerUrl(rawJson, out var legacyUrl))
            {
                config.UpdateServerUrl = legacyUrl;
                Log.Information("检测到旧版更新配置，已迁移为单一 updateServerUrl: {UpdateServerUrl}", legacyUrl);
            }
            else if (TryNormalizeAbsoluteBaseUrl(config.UpdateServerUrl, out var normalizedCurrent))
            {
                if (!string.Equals(config.UpdateServerUrl, normalizedCurrent, StringComparison.Ordinal))
                {
                    config.UpdateServerUrl = normalizedCurrent;
                    Log.Information("已规范化 updateServerUrl: {UpdateServerUrl}", normalizedCurrent);
                    changed = true;
                }
            }
            else
            {
                config.UpdateServerUrl = AppConfig.DefaultUpdateServerUrl;
                Log.Information("updateServerUrl 为空或无效，已回填默认值: {UpdateServerUrl}", config.UpdateServerUrl);
                changed = true;
            }

            if (TryResolveConfiguredAppUpdateFeedUrl(rawJson, out var configuredFeedUrl))
            {
                if (!string.Equals(config.AppUpdateFeedUrl, configuredFeedUrl, StringComparison.Ordinal))
                {
                    config.AppUpdateFeedUrl = configuredFeedUrl;
                    Log.Information("已规范化 appUpdateFeedUrl: {AppUpdateFeedUrl}", configuredFeedUrl);
                    changed = true;
                }
            }
            else if (TryNormalizeAbsoluteAppUpdateFeedUrl(config.AppUpdateFeedUrl, out var normalizedFeedUrl))
            {
                if (!string.Equals(config.AppUpdateFeedUrl, normalizedFeedUrl, StringComparison.Ordinal))
                {
                    config.AppUpdateFeedUrl = normalizedFeedUrl;
                    Log.Information("已规范化 appUpdateFeedUrl: {AppUpdateFeedUrl}", normalizedFeedUrl);
                    changed = true;
                }
            }
            else
            {
                var derivedFeedUrl = BuildDefaultAppUpdateFeedUrl(config.UpdateServerUrl);
                if (!string.Equals(config.AppUpdateFeedUrl, derivedFeedUrl, StringComparison.Ordinal))
                {
                    config.AppUpdateFeedUrl = derivedFeedUrl;
                    Log.Information("appUpdateFeedUrl 缺失，已根据静态资源地址推导为: {AppUpdateFeedUrl}", derivedFeedUrl);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool TryResolveConfiguredUpdateServerUrl(string? rawJson, out string updateServerUrl)
        {
            updateServerUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(rawJson);
                return TryGetAbsoluteBaseUrl(document.RootElement, "updateServerUrl", out updateServerUrl);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveLegacyUpdateServerUrl(string? rawJson, out string updateServerUrl)
        {
            updateServerUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(rawJson);
                var root = document.RootElement;
                return TryGetAbsoluteBaseUrl(root, "updateManifestIndexUrl", out updateServerUrl, "manifests/update/index.json")
                    || TryGetAbsoluteBaseUrl(root, "modelCatalogUrl", out updateServerUrl, "manifests/models/list.json")
                    || TryGetAbsoluteBaseUrl(root, "translationProfileUrl", out updateServerUrl, "manifests/translations/profile.json");
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveConfiguredAppUpdateFeedUrl(string? rawJson, out string appUpdateFeedUrl)
        {
            appUpdateFeedUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(rawJson);
                if (!document.RootElement.TryGetProperty("appUpdateFeedUrl", out var property) || property.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                return TryNormalizeAbsoluteAppUpdateFeedUrl(property.GetString(), out appUpdateFeedUrl);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAbsoluteBaseUrl(JsonElement root, string propertyName, out string baseUrl, string? relativeSuffix = null)
        {
            baseUrl = string.Empty;
            if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var candidate = raw.Trim();
            if (!string.IsNullOrWhiteSpace(relativeSuffix)
                && candidate.EndsWith(relativeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(0, candidate.Length - relativeSuffix.Length);
            }

            return TryNormalizeAbsoluteBaseUrl(candidate, out baseUrl);
        }

        private static bool TryNormalizeAbsoluteBaseUrl(string? raw, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var trimmed = raw.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            {
                return false;
            }

            normalized = trimmed.EndsWith("/", StringComparison.Ordinal)
                ? trimmed
                : trimmed + "/";
            return true;
        }

        private static bool TryNormalizeAbsoluteAppUpdateFeedUrl(string? raw, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var trimmed = raw.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            {
                return false;
            }

            if (trimmed.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                normalized = trimmed;
                return true;
            }

            normalized = trimmed.EndsWith("/", StringComparison.Ordinal)
                ? trimmed + "appcast.xml"
                : trimmed + "/appcast.xml";

            return true;
        }

        private static string BuildDefaultAppUpdateFeedUrl(string staticBaseUrl)
        {
            if (!TryNormalizeAbsoluteBaseUrl(staticBaseUrl, out var normalized))
            {
                return AppConfig.DefaultAppUpdateFeedUrl;
            }

            return normalized + "app-releases/appcast.xml";
        }

        private StartupModelPreparationResult EnsureStartupRequiredModels(IServiceProvider? serviceProvider, AppConfig? config)
        {
            if (serviceProvider == null || config == null)
            {
                return new StartupModelPreparationResult();
            }

            try
            {
                return EnsureStartupRequiredModelsAsync(serviceProvider, config).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Startup required model check failed.");
                ShowOwnedMessageBox(
                    "启动时检查必需模型失败，程序无法继续启动。",
                    "VRCLS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return new StartupModelPreparationResult
                {
                    ContinueStartup = false
                };
            }
        }

        private async Task<StartupModelPreparationResult> EnsureStartupRequiredModelsAsync(IServiceProvider serviceProvider, AppConfig config)
        {
            var modelUpdateService = serviceProvider.GetService<IModelUpdateService>();
            if (modelUpdateService == null)
            {
                return new StartupModelPreparationResult();
            }

            var hasVad = await EnsureStartupModelGroupAutoDownloadAsync(
                modelUpdateService,
                new[] { "vad" },
                "VAD 模型缺失").ConfigureAwait(false);
            if (!hasVad)
            {
                return new StartupModelPreparationResult
                {
                    ContinueStartup = false
                };
            }

            if (config.RecognitionBackendModeValue != RecognitionBackendMode.Local)
            {
                return new StartupModelPreparationResult();
            }

            var recognitionType = config.LocalRecognitionModelValue == LocalRecognitionModel.SenseVoice
                ? "sensevoice"
                : "sherpa";
            var recognitionName = GetModelDisplayName(recognitionType);
            var missingModels = await modelUpdateService.GetMissingModelsAsync(new[] { recognitionType }).ConfigureAwait(false);
            if (missingModels.Count == 0)
            {
                return new StartupModelPreparationResult();
            }

            var missingModelList = FormatModelPackageList(missingModels);
            Log.Information(
                "Startup local recognition models missing. disabling local recognition. recognitionModel={RecognitionModel}, models={Models}",
                recognitionName,
                missingModelList);

            config.RecognitionBackendModeValue = RecognitionBackendMode.Remote;
            SaveConfigToDisk(config);

            return new StartupModelPreparationResult
            {
                LocalRecognitionDisabled = true,
                LocalRecognitionModelName = recognitionName,
                MissingModelList = missingModelList
            };
        }

        private async Task<bool> EnsureStartupModelGroupAutoDownloadAsync(
            IModelUpdateService modelUpdateService,
            IReadOnlyList<string> modelTypes,
            string title)
        {
            var missingModels = await modelUpdateService.GetMissingModelsAsync(modelTypes).ConfigureAwait(false);
            if (missingModels.Count == 0)
            {
                return true;
            }

            Log.Information("Startup required models missing. title={Title}, models={Models}", title, FormatModelPackageList(missingModels));
            return await DownloadStartupModelsSilentlyAsync(modelUpdateService, missingModels, title).ConfigureAwait(false);
        }

        private async Task<bool> EnsureStartupModelGroupAsync(
            IModelUpdateService modelUpdateService,
            IReadOnlyList<string> modelTypes,
            string title,
            string message,
            string primaryButtonText,
            string secondaryButtonText)
        {
            var missingModels = await modelUpdateService.GetMissingModelsAsync(modelTypes).ConfigureAwait(false);
            if (missingModels.Count == 0)
            {
                return true;
            }

            Log.Information("Startup required models missing. title={Title}, models={Models}", title, FormatModelPackageList(missingModels));
            return await ShowModelDownloadWindowAsync(
                modelUpdateService,
                missingModels,
                title,
                $"{message}\n\n当前缺失：{FormatModelPackageList(missingModels)}。",
                primaryButtonText,
                secondaryButtonText).ConfigureAwait(false);
        }

        private async Task RunStartupUpdateJobsAsync(IServiceProvider serviceProvider, AppConfig? config)
        {
            if (serviceProvider == null || config == null)
            {
                return;
            }

            if (config.AutoSyncTranslationProfile)
            {
                try
                {
                    var translationUpdateService = serviceProvider.GetService<ITranslationHotUpdateService>();
                    if (translationUpdateService != null)
                    {
                        await translationUpdateService.SyncLatestProfileAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Auto translation profile sync failed.");
                }
            }

            config.LastUpdateCheckUtc = DateTime.UtcNow;
            try
            {
                var updateService = serviceProvider.GetService<IAppUpdateService>();
                if (updateService == null)
                {
                    return;
                }

                var updateInfo = await updateService.CheckForUpdatesAsync().ConfigureAwait(false);
                if (updateService.State == AppUpdateState.ReadyToRestart && updateService.PendingUpdate != null)
                {
                    await PromptForUpdateRestartAsync(updateService, updateService.PendingUpdate).ConfigureAwait(false);
                }
                else if (updateInfo != null)
                {
                    Log.Information("New app version detected: {Version}", updateInfo.ResolvedDisplayVersion);
                    var openUpdateCenter = await ShowMessageBoxAsync(
                        $"发现新版本 {updateInfo.ResolvedDisplayVersion}。\n\n请前往“设置 > 更新”页面执行更新，是否现在打开更新页？",
                        "VRCLS 更新",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question).ConfigureAwait(false);
                    if (openUpdateCenter == MessageBoxResult.Yes)
                    {
                        await OpenUpdateCenterAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                Log.Information(ex, "Startup app update check skipped because NetSparkle is not fully configured.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto app update check failed.");
            }
            finally
            {
                SaveConfigToDisk(config);
            }
        }

        private Task<bool> ShowModelDownloadWindowAsync(
            IModelUpdateService modelUpdateService,
            IReadOnlyList<ModelPackageInfo> models,
            string title,
            string message,
            string primaryButtonText,
            string secondaryButtonText)
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null)
            {
                return Task.FromResult(false);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowModelDownloadWindow(
                    modelUpdateService,
                    models,
                    title,
                    message,
                    primaryButtonText,
                    secondaryButtonText));
            }

            return dispatcher.InvokeAsync(() => ShowModelDownloadWindow(
                modelUpdateService,
                models,
                title,
                message,
                primaryButtonText,
                secondaryButtonText)).Task;
        }

        private static bool ShowModelDownloadWindow(
            IModelUpdateService modelUpdateService,
            IReadOnlyList<ModelPackageInfo> models,
            string title,
            string message,
            string primaryButtonText,
            string secondaryButtonText)
        {
            var window = new ModelDownloadWindow(
                modelUpdateService,
                models,
                title,
                message,
                primaryButtonText,
                secondaryButtonText,
                allowCancel: true);

            if (Current?.MainWindow != null)
            {
                window.Owner = Current.MainWindow;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                // Startup-required model checks run before the main window exists.
                // Without an owner, hiding the dialog from the taskbar makes it easy
                // to end up with a "background-only" process if the dialog loses z-order.
                window.ShowInTaskbar = true;
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Loaded += (_, _) =>
                {
                    try
                    {
                        window.Topmost = true;
                        window.Activate();
                        window.Focus();
                    }
                    finally
                    {
                        window.Topmost = false;
                    }
                };
            }

            Log.Information(
                "Showing startup model download window. ownerExists={OwnerExists}, showInTaskbar={ShowInTaskbar}, title={Title}, models={Models}",
                window.Owner != null,
                window.ShowInTaskbar,
                title,
                FormatModelPackageList(models));

            var result = window.ShowDialog();
            return result == true && window.DownloadSucceeded;
        }

        private static async Task<bool> DownloadStartupModelsSilentlyAsync(
            IModelUpdateService modelUpdateService,
            IReadOnlyList<ModelPackageInfo> models,
            string title)
        {
            Log.Information(
                "Starting silent startup model download. title={Title}, models={Models}",
                title,
                FormatModelPackageList(models));

            foreach (var model in models)
            {
                if (model == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(model.DownloadUrl))
                {
                    await ShowMessageBoxAsync(
                        $"启动所需模型缺失，但更新目录中未找到 {GetModelDisplayName(model.Type)} 的下载地址，程序无法继续启动。",
                        "VRCLS",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning).ConfigureAwait(false);
                    return false;
                }

                Log.Information("Silent startup model download begin. model={Model}, type={Type}", model.Name, model.Type);
                var ok = await modelUpdateService.DownloadAndInstallModelAsync(model, progress: null).ConfigureAwait(false);
                if (!ok)
                {
                    Log.Warning("Silent startup model download failed. model={Model}, type={Type}", model.Name, model.Type);
                    await ShowMessageBoxAsync(
                        $"程序正在自动下载启动所需模型，但 {GetModelDisplayName(model.Type)} 下载或安装失败，请检查网络连接后重试。",
                        "VRCLS",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning).ConfigureAwait(false);
                    return false;
                }

                Log.Information("Silent startup model download completed. model={Model}, type={Type}", model.Name, model.Type);
            }

            return true;
        }

        private async Task PromptForStartupLocalRecognitionModelDownloadAsync(StartupModelPreparationResult result)
        {
            if (!result.LocalRecognitionDisabled)
            {
                return;
            }

            var missingModelList = string.IsNullOrWhiteSpace(result.MissingModelList)
                ? result.LocalRecognitionModelName
                : result.MissingModelList;

            var openUpdateCenter = await ShowMessageBoxAsync(
                $"启动时未检测到本地识别模型：{missingModelList}。\n\n已自动关闭“本地识别模型”开关，请前往“设置 > 更新”下载后再开启。\n\n是否现在打开更新页？",
                "本地识别模型缺失",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning).ConfigureAwait(false);
            if (openUpdateCenter == MessageBoxResult.Yes)
            {
                await OpenUpdateCenterAsync(refreshModelCatalog: true).ConfigureAwait(false);
            }
        }

        private Task OpenUpdateCenterAsync(bool refreshModelCatalog = false)
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null)
            {
                return Task.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                return OpenUpdateCenterCoreAsync(refreshModelCatalog);
            }

            return dispatcher.InvokeAsync(async () =>
            {
                await OpenUpdateCenterCoreAsync(refreshModelCatalog).ConfigureAwait(false);
            }).Task.Unwrap();
        }

        private async Task OpenUpdateCenterCoreAsync(bool refreshModelCatalog)
        {
            _mainViewModel?.OpenSettingsAtTab(MainViewModel.UpdateSettingsTabIndex);

            if (!refreshModelCatalog || _serviceProvider == null)
            {
                return;
            }

            var settingsViewModel = _serviceProvider.GetService<SettingsViewModel>();
            if (settingsViewModel != null)
            {
                await settingsViewModel.NavigateToUpdateTabAsync(refreshModelCatalog: true).ConfigureAwait(false);
            }
        }

        private static async Task PromptForUpdateRestartAsync(IAppUpdateService updateService, AppUpdateInfo? updateInfo)
        {
            var versionText = updateInfo?.ResolvedDisplayVersion ?? "新版本";
            var restartNow = await ShowMessageBoxAsync(
                $"更新 {versionText} 已准备完成，是否现在重启并应用？",
                "VRCLS 更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question).ConfigureAwait(false);
            if (restartNow != MessageBoxResult.Yes)
            {
                return;
            }

            var applied = await updateService.ApplyUpdateAndRestartAsync().ConfigureAwait(false);
            if (!applied)
            {
                await ShowMessageBoxAsync(
                    "无法安排重启更新，请稍后重试。",
                    "VRCLS 更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning).ConfigureAwait(false);
                return;
            }

            if (Current?.Dispatcher != null)
            {
                await Current.Dispatcher.InvokeAsync(() => Current.Shutdown());
            }
        }

        private static Task<MessageBoxResult> ShowMessageBoxAsync(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null)
            {
                return Task.FromResult(MessageBoxResult.None);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowOwnedMessageBox(message, title, buttons, image));
            }

            return dispatcher.InvokeAsync(() => ShowOwnedMessageBox(message, title, buttons, image)).Task;
        }

        private static MessageBoxResult ShowOwnedMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            return Current?.MainWindow == null
                ? MessageBox.Show(message, title, buttons, image)
                : MessageBox.Show(Current.MainWindow, message, title, buttons, image);
        }

        private static string FormatModelPackageList(IEnumerable<ModelPackageInfo> models)
        {
            return string.Join(", ",
                models
                    .Where(model => model != null)
                    .Select(model => GetModelDisplayName(model.Type))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string GetModelDisplayName(string? modelType)
        {
            var normalized = (modelType ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "vad" => "VAD",
                "sensevoice" => "SenseVoice",
                "sherpa" => "Sherpa",
                _ => string.IsNullOrWhiteSpace(modelType) ? "Model" : modelType.Trim()
            };
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            var exitStopwatch = Stopwatch.StartNew();
            Log.Information("VRCLS 应用程序正在退出...");
            Log.Debug("退出时的配置 - UserInfo: {UserInfo}", _config?.UserInfo);
            UnregisterGlobalExceptionHandlers();

            try
            {
                if (_config != null)
                {
                    Log.Debug("保存应用程序配置");
                    Log.Debug("保存前的配置 - UserInfo: {UserInfo}", _config.UserInfo);
                    SaveConfig(_config);
                    Log.Information("配置保存成功，耗时 {ElapsedMilliseconds} ms", exitStopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存配置失败");
                Console.WriteLine($"保存配置失败: {ex.Message}");
            }

            try
            {
                if (_mainViewModel != null)
                {
                    Log.Debug("释放 MainViewModel 资源");
                    _mainViewModel.Dispose();
                    _mainViewModel = null;
                    Log.Information("MainViewModel 释放完成，累计耗时 {ElapsedMilliseconds} ms", exitStopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放 MainViewModel 资源时出错");
            }

            try
            {
                if (_serviceProvider is IDisposable disposable)
                {
                    Log.Debug("释放服务容器");
                    disposable.Dispose();
                    Log.Information("服务容器释放完成，累计耗时 {ElapsedMilliseconds} ms", exitStopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放服务容器时出错");
            }

            Log.Information("退出流程总耗时 {ElapsedMilliseconds} ms", exitStopwatch.ElapsedMilliseconds);
            Log.Information("==================== VRCLS 应用程序已退出 ====================");
            Log.CloseAndFlush();

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        private bool TryAcquireSingleInstance()
        {
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Global\VRCLS.Singleton", out var createdNew);
                return createdNew;
            }
            catch
            {
                return true;
            }
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Fatal(e.Exception, "发生未处理的UI线程异常");
            ShowFriendlyErrorDialog(
                "程序运行时发生了未处理的异常。",
                e.Exception,
                "运行错误",
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Error(e.Exception, "发生未观察的后台任务异常");
            e.SetObserved();
        }

        private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "发生未处理的非UI线程异常。IsTerminating={IsTerminating}", e.IsTerminating);
            }
            else
            {
                Log.Fatal("发生未处理的非UI线程异常。IsTerminating={IsTerminating}, ExceptionObjectType={ExceptionObjectType}",
                    e.IsTerminating,
                    e.ExceptionObject?.GetType().FullName ?? "<null>");
            }

            try
            {
                Log.CloseAndFlush();
            }
            catch
            {
            }
        }

        internal static string NormalizeUiLanguage(string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(v))
            {
                return "zh-CN";
            }

            v = v.Replace('_', '-');
            if (v.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return "en-US";
            }

            if (v.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-CN";
            }

            return "zh-CN";
        }

        private static void NormalizeRecognitionSourceLanguage(AppConfig config)
        {
            if (config == null)
            {
                return;
            }

            var sourceLanguage = (config.SourceLanguage ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(sourceLanguage) || sourceLanguage == "auto")
            {
                config.SourceLanguage = "zh";
            }

            var micSourceLanguage = (config.MicSourceLanguage ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(micSourceLanguage) && micSourceLanguage != "auto")
            {
                config.MicSourceLanguage = micSourceLanguage;
            }

            var desktopSourceLanguage = (config.DesktopSourceLanguage ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(desktopSourceLanguage) && desktopSourceLanguage != "auto")
            {
                config.DesktopSourceLanguage = desktopSourceLanguage;
            }
        }

        internal static void ApplyUiLanguage(AppConfig? config)
        {
            var normalized = NormalizeUiLanguage(config?.UiLanguage);
            ApplyUiLanguage(normalized);
            if (config != null && !string.Equals(config.UiLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            {
                config.UiLanguage = normalized;
            }
        }

        internal static void ApplyUiLanguage(string? language)
        {
            if (Current is not App app)
            {
                return;
            }

            var normalized = NormalizeUiLanguage(language);
            var culture = new CultureInfo(normalized);

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            var merged = app.Resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source?.OriginalString;
                if (!string.IsNullOrWhiteSpace(source) &&
                    source.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase))
                {
                    merged.RemoveAt(i);
                }
            }

            merged.Add(new ResourceDictionary
            {
                Source = new Uri("Resources/Strings.zh.xaml", UriKind.Relative)
            });

            if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                merged.Add(new ResourceDictionary
                {
                    Source = new Uri("Resources/Strings.en.xaml", UriKind.Relative)
                });
            }
        }

        private static string GetConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VRCLS",
                "config.json");
        }
        internal static void SaveConfigToDisk(AppConfig config)
        {
            var configPath = GetConfigPath();
            Log.Debug("保存配置到 {ConfigPath}", configPath);
            AppConfigStorage.SaveOrThrow(config);
        }

        private void SaveConfig(AppConfig config)
        {
            try
            {
                SaveConfigToDisk(config);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "写入配置文件失败");
                throw;
            }
        }
    }
}
