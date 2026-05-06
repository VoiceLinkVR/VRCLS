using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.UI.Services;
using VRCLS.UI.Views;

namespace VRCLS.UI.ViewModels
{
    public class SettingsViewModel : ObservableObject, IDisposable
    {
        private readonly AppConfig _config;
        private readonly IBackendAuthService _authService;
        private static readonly Serilog.ILogger _logger = Serilog.Log.ForContext<SettingsViewModel>();
        // App-localized names for translation tags that are not consistently localized by CultureInfo.
        private static readonly IReadOnlyDictionary<string, string> SpecialChineseTranslationLanguageNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ace"] = "亚齐语（拉丁文）",
                ["arb"] = "阿拉伯语（罗马化）",
                ["ary"] = "摩洛哥阿拉伯语",
                ["ast"] = "阿斯图里亚斯语",
                ["ban"] = "巴厘语",
                ["bbc"] = "巴塔克托巴语",
                ["bho"] = "博杰普尔语",
                ["bik"] = "比科尔语",
                ["brx"] = "博多语",
                ["co"] = "科西嘉语",
                ["crs"] = "塞舌尔法语克里奥尔语",
                ["doi"] = "多格拉语",
                ["en-gb"] = "英语（英国）",
                ["epo"] = "世界语",
                ["fur"] = "弗留利语",
                ["fy"] = "弗里西语",
                ["gom"] = "孔卡尼语",
                ["hil"] = "希利盖农语",
                ["hne"] = "恰蒂斯加尔语",
                ["iba"] = "伊班语",
                ["ikt"] = "因纽因纳克顿语",
                ["ilo"] = "伊洛卡诺语",
                ["iu-latn"] = "因纽特语（拉丁文）",
                ["jam"] = "牙买加土语",
                ["jav"] = "爪哇语",
                ["kea"] = "佛得角克里奥尔语",
                ["kri"] = "克里奥语",
                ["ks"] = "克什米尔语",
                ["la"] = "拉丁语",
                ["lij"] = "利古里亚语（热那亚语）",
                ["lim"] = "林堡语",
                ["lmo"] = "伦巴第语",
                ["lug"] = "干达语",
                ["lzh"] = "中文（文言文）",
                ["mfe"] = "毛里求斯克里奥尔语",
                ["min"] = "米南佳保语（拉丁文）",
                ["mn-cyrl"] = "蒙古语（西里尔文）",
                ["mn-mong"] = "蒙古语（传统蒙文）",
                ["mwr"] = "马尔瓦里语",
                ["nno"] = "新挪威语",
                ["nso"] = "北索托语",
                ["nya"] = "尼扬贾语",
                ["pam"] = "邦板牙语",
                ["pap"] = "帕皮阿门托语",
                ["pnb"] = "旁遮普语（沙木基文）",
                ["run"] = "隆迪语",
                ["sa"] = "梵语",
                ["scn"] = "西西里语",
                ["srd"] = "撒丁语",
                ["st"] = "塞索托语",
                ["tet"] = "德顿语",
                ["tgk"] = "塔吉克语",
                ["tn"] = "茨瓦纳语",
                ["tpi"] = "托克皮辛语",
                ["vec"] = "威尼斯语",
                ["war"] = "瓦赖语",
                ["ydd"] = "意第绪语（东部）"
            };
        private static readonly IReadOnlyDictionary<string, string> SpecialEnglishTranslationLanguageNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["arz"] = "Arabic (Egyptian)",
                ["fj"] = "Fijian",
                ["ht"] = "Haitian Creole",
                ["kmr"] = "Kurdish (Northern)",
                ["mai"] = "Maithili",
                ["mww"] = "Hmong Daw",
                ["otq"] = "Queretaro Otomi",
                ["sm"] = "Samoan",
                ["su"] = "Sundanese",
                ["tlh-latn"] = "Klingon (Latin)",
                ["ty"] = "Tahitian",
                ["yua"] = "Yucatec Maya",
                ["yue"] = "Cantonese (Traditional)"
            };

        // 密码更新事件，用于通知UI更新PasswordBox
        public event Action<string>? PasswordUpdated;

        // 标记是否正在加载配置（加载时不触发token失效）
        private bool _isLoadingConfig;

        // 服务器与鉴权
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _baseUrl = string.Empty;
        private string _apiIp = string.Empty;
        private int _apiPort;
        private string _connectionStatus = string.Empty;
        private bool _isBaseUrlLocked = true;
        private bool _useLegacyBackendApi;
        private int _selectedSettingsTabIndex;
        private string _websiteAuthorizationUserCode = string.Empty;
        private string _websiteAuthorizationUrl = string.Empty;
        private string _websiteAuthorizationStatus = string.Empty;
        private string _lastOpenedAuthorizationUrl = string.Empty;

        // 用户中心：发电与激活
        private string _donationUsername = string.Empty;
        private string _donationPassword = string.Empty;
        private string _donationAction = "renew";
        private string _selectedPlanType = "basic";
        private int _donationMonths = 1;
        private string _donationPrice = "5.00";
        private string _donationStatus = string.Empty;
        private string _donationRemark = string.Empty;
        private string _donationUrl = string.Empty;
        private bool _isDonationSubmitting;
        private bool _isCurrentLoginTestAccount;
        private bool _needsFormalDonationCredentials;
        private bool _isPaymentGuideLoading;
        private bool _hasPaymentGuide;
        private bool _isGuideAccountRegistered;
        private string _paymentGuideStatus = string.Empty;
        private string _paymentGuideToken = string.Empty;
        private string _displayPlanName = string.Empty;
        private decimal _displayMonthlyPrice;
        private string _displayResourceSummary = string.Empty;
        private string _donationGuideExplanation = string.Empty;
        private int _donationPriceStep = 5;
        private int _resourceIncrementPerStep;
        private UserCenterPaymentGuideOption? _renewGuideOption;
        private UserCenterPaymentGuideOption? _upgradeGuideOption;
        private UserCenterPaymentGuideOption? _downgradeGuideOption;
        private int _donationWizardStepIndex;
        private bool _hasPendingDonationRequest;
        private string _pendingDonationAction = "renew";
        private int _pendingDonationMonths = 1;
        private decimal _pendingDonationMonthlyPrice = 5m;
        private int? _pendingDonationExpectedDailyQuota;
        private DateTimeOffset? _pendingDonationMinimumExpirationUtc;
        private string _pendingDonationOverview = string.Empty;
        private string _donationVerificationStatus = string.Empty;
        private bool _isDonationVerificationRunning;
        private int _donationVerificationAttempt;
        private bool _donationVerificationSucceeded;
        private string _activationMethod = "remark";
        private string _activationInput = string.Empty;
        private string _activationStatus = string.Empty;
        private bool _isActivationSubmitting;
        private string _accountUsageStatus = string.Empty;
        private string _accountRemainingDailySummary = "-";
        private string _accountDailyLimitSummary = "-";
        private string _accountAddonBalanceSummary = string.Empty;
        private string _accountUsageSummary = string.Empty;
        private string _accountUsageRangeSummary = string.Empty;
        private string _accountExpirationSummary = string.Empty;
        // 使用 UTC 日期作为默认统计区间，避免本地时区与服务器 UTC 不一致
        private DateTime? _accountUsageStartDate = DateTime.UtcNow.Date.AddDays(-6);
        private DateTime? _accountUsageEndDate = DateTime.UtcNow.Date;
        private bool _isAccountUsageSubmitting;
        private bool _isAccountOverviewRefreshing;
        private bool _suppressPlanSelectionSync;

        // 音频设置 - 麦克风
        private string _micName = "default";
        private int _voiceMode;
        private bool _autoStartMic;
        private bool _dynamicThreshold;
        private double _customThreshold;
        private string _voiceHotKey = string.Empty;
        private string _micPressingKey = string.Empty;

        // 音频设置 - 桌面音频
        private string _gameMicName = "default";
        private int _gameVoiceMode;
        private bool _autoStartDesktop;
        private double _gameCustomThreshold;
        private string _gameVoiceHotKey = string.Empty;
        private string _capPressingKey = string.Empty;
        private int _desktopAudioSourceMode;
        private int _selectedApplicationAudioProcessId;

        // 识别设置
        private bool _useLocalRecognition;
        private string _localModelStatusText = string.Empty;
        private bool _isChangingLocalRecognition;

        // Control recognition (resident local model) settings
        private bool _controlEnabled = false;
        private bool _commandFeedbackEnabled = true;
        private bool _controlAutoStart = true;
        private int _controlMicSourceMode;
        private string _controlMicName = "default";
        private bool _controlRequireWakeForCommands = true;
        private double _controlWakeWindowSeconds = 8.0;

        // Experimental keyword actions
        private string _experimentalKeywordActionsJson = "[]";
        private string _experimentalKeywordActionsStatus = "当前没有实验规则。";

        // VAD 设置
        private double _micVadMinSilenceDuration = 0.8;
        private double _micVadMaxSegmentSeconds = 10;
        private double _micVadThreshold = 0.5;
        private double _desktopVadMinSilenceDuration = 0.8;
        private double _desktopVadMaxSegmentSeconds = 10;
        private double _desktopVadThreshold = 0.5;
        private double _vadMinSilenceDuration = 0.8;
        private double _vadMaxSegmentSeconds = 10;
        private double _vadThreshold = 0.5;
        private double _localStreamingEndpointSilenceSeconds = 1.2;

        // 翻译设置
        private string _sourceLanguage = "zh";
        private string _micSourceLanguage = "zh";
        private string _desktopSourceLanguage = "zh";
        private string _micTargetTranslationLanguage = "en";
        private string _micTargetTranslationLanguage2 = "none";
        private string _micTargetTranslationLanguage3 = "none";
        private string _desktopTargetTranslationLanguage = "en";
        private string _desktopTargetTranslationLanguage2 = "none";
        private string _desktopTargetTranslationLanguage3 = "none";
        private string _textSendTargetTranslationLanguage = "en";
        private string _textSendTargetTranslationLanguage2 = "none";
        private string _textSendTargetTranslationLanguage3 = "none";
        private string _micTranslationServer = "developer";
        private string _desktopTranslationServer = "developer";
        private bool _filterEmoji = false;  // 修改：改为bool类型，默认false
        private string _translatorsDefaultRegion = "auto";
        // Main window transcript language mask: 1=Original, 2=T1, 4=T2, 8=T3
        private int _mainWindowLanguageMask = (int)TranscriptLanguageMask.All;


        // OSC设置
        private string _oscIp = "127.0.0.1";
        private int _oscPort = 9000;
        private string _vrcChatboxFormatNew = string.Empty;
        private bool _oscChatboxNotificationSfx = true;
        private bool _enableOscServer;
        private string _oscServerIp = "127.0.0.1";
        private int _oscServerPort = 9003;
        private bool _oscShutdown;

        // VRChat集成
        private bool _textInSteamVr;
        private int _steamVrHand;
        private double _steamVrSize = 0.15;
        private int _steamVrAnchorMode;
        private double _steamVrOffsetX;
        private double _steamVrOffsetY;
        private double _steamVrOffsetZ;
        private double _steamVrRotationX;
        private double _steamVrRotationY;
        private double _steamVrRotationZ;
        private bool _steamVrAutoLaunch;
        private double _steamVrNonTextOpacity = 1.0;
        private string _steamVrStatus = string.Empty;
        private string _steamVrStartupStatus = string.Empty;
        private bool _steamVrCursorFlipU = true;
        private bool _steamVrCursorFlipV = false;
        private bool _vrcBitmapLedColor = true;
        private int _vrcBitmapLedRow = 8;
        private int _vrcBitmapLedCol = 16;

        // TTS设置
        private bool _enableTts;
        private int _ttsMode;
        private int _ttsEngine;
        private string _ttsProvider = AppConfig.DefaultTtsProvider;
        private string _ttsOutputName = "default";
        private string _ttsVoiceName = string.Empty;
        private double _ttsSpeed = 1.0;
        private int _qwenTtsToneMode = (int)VRCLS.Core.Models.QwenTtsToneMode.Custom;
        private string _qwenTtsInstruct = AppConfig.DefaultQwenTtsInstruct;
        private int _ttsInterruptMode = 1;
        private const string LegacyDefaultTtsTestText = "这是一段TTS测试文本";
        private static readonly Dictionary<string, string> _ttsSampleTextByLanguage = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zh"] = "这是语音测试。",
            ["en"] = "This is a voice test.",
            ["ja"] = "これは音声テストです。",
            ["ko"] = "이것은 음성 테스트입니다.",
            ["fr"] = "Ceci est un test vocal.",
            ["de"] = "Dies ist ein Sprachtest.",
            ["es"] = "Esta es una prueba de voz.",
            ["it"] = "Questo e un test vocale.",
            ["pt"] = "Este e um teste de voz.",
            ["ru"] = "Это голосовой тест.",
            ["uk"] = "Це голосовий тест.",
            ["pl"] = "To jest test glosowy.",
            ["tr"] = "Bu bir ses testidir.",
            ["ar"] = "هذا اختبار صوتي.",
            ["hi"] = "यह एक वॉइस टेस्ट है।",
            ["th"] = "นี่คือการทดสอบเสียง",
            ["vi"] = "Day la bai kiem tra giong noi.",
            ["id"] = "Ini adalah uji suara."
        };
        private string _ttsTestText = string.Empty;
        private bool _localizedSpeech;
        private bool _localizedCapture;
        private string _openAiApiKey = string.Empty;
        private string _openAiBaseUrl = "https://open.bigmodel.cn/api/paas/v4/";
        private string _openAiModel = "glm-4-flash";
        private double _openAiTemperature = 0.3;
        private int _openAiMaxTokens = 1000;
        private string _openAiSystemPrompt = string.Empty;
        private string _openAiExtraBody = string.Empty;
        private string _selectedOpenAiPreset = string.Empty;

        private string _currentSessionLogText = string.Empty;
        private string _currentSessionLogPath = string.Empty;
        private bool _isLogAutoRefreshEnabled = true;

        private AppUpdateInfo? _pendingAppUpdate;
        private AppUpdateState _appUpdateState = AppUpdateState.Idle;
        private string _appVersion = string.Empty;
        private string _updateStatusText = "No update check yet";
        private string _appUpdateActionText = "Download update";
        private double _updateProgress;
        private string _modelUpdateStatusText = "Refresh to load model catalog";
        private double _modelDownloadProgress;
        private string _translationProfileVersion = string.Empty;
        private string _translationUpdateStatusText = "Not synced";
        private double _translationUpdateProgress;
        private bool _hasPendingTranslationConfigChanges;

        // TTS selection masks (bitmask): 1=original, 2=translation1, 4=translation2, 8=translation3
        private int _micTtsSpeakMask = 2;
        private int _desktopTtsSpeakMask = 2;

        // 外观设置
        private bool _darkMode;
        private bool _transparentWindow;
        private string _transparentWindowSize = "large";
        private int _overlayWidth = 800;
        private int _overlayHeight = 500;
        private bool _copyBox;
        private int _copyLanguageMask = (int)TranscriptLanguageMask.Translation1;
        private string _uiLanguage = "zh-CN";

        // 高级设置
        private string _chatboxOscMixTemplate = string.Empty;

        public ObservableCollection<string> MicDevices { get; } = new();
        public ObservableCollection<string> DesktopAudioDevices { get; } = new();
        public ObservableCollection<ApplicationAudioProcessOption> ApplicationAudioProcesses { get; } = new();
        public ObservableCollection<TranslationEngineOption> TranslationServiceOptions { get; } = new();
        public ObservableCollection<string> OpenAiPresetNames { get; } = new();
        public ObservableCollection<DonationActionOption> DonationActions { get; } = new();
        public ObservableCollection<int> DonationMonthOptions { get; } = new(Enumerable.Range(1, 12));
        public ObservableCollection<ActivationMethodOption> ActivationMethods { get; } = new();
        public ObservableCollection<LanguageOption> TranslatorRegions { get; } = new();
        public ObservableCollection<LanguageOption> SourceLanguages { get; } = new();
        public ObservableCollection<LanguageOption> TargetLanguages { get; } = new();
        public ObservableCollection<LanguageOption> UiLanguages { get; } = new();

        public ObservableCollection<string> TtsOutputDevices { get; } = new();
        public ObservableCollection<ModelPackageInfo> AvailableModelPackages { get; } = new();
        public ObservableCollection<AccountUsageDailyStatRow> AccountUsageDailyStats { get; } = new();

        public ICommand LoginCommand { get; }
        public ICommand ResetToDefaultCommand { get; }
        public ICommand OpenWebsiteUserCenterCommand { get; }
        public ICommand StartWebsiteLoginForMigrationCommand { get; }
        public ICommand OpenConnectionEntryCommand { get; }
        public ICommand TestTtsCommand { get; }
        public ICommand ResetKeywordsToDefaultCommand { get; }
        public ICommand CheckAppUpdateCommand { get; }
        public ICommand ExecuteAppUpdateActionCommand { get; }
        public ICommand RefreshModelCatalogCommand { get; }
        public ICommand InstallModelCommand { get; }
        public ICommand SyncTranslationProfileCommand { get; }
        public ICommand OpenLogFileCommand { get; }
        public ICommand RefreshLogCommand { get; }
        public ICommand ToggleBaseUrlLockCommand { get; }
        public ICommand LoadPaymentGuideCommand { get; }
        public ICommand CreateDonationLinkCommand { get; }
        public ICommand IncreaseDonationPriceCommand { get; }
        public ICommand DecreaseDonationPriceCommand { get; }
        public ICommand ActivateOrderCommand { get; }
        public ICommand QueryAccountUsageCommand { get; }
        public ICommand RefreshAccountOverviewCommand { get; }
        public ICommand DonationWizardNextCommand { get; }
        public ICommand DonationWizardBackCommand { get; }
        public ICommand RetryDonationVerificationCommand { get; }
        public ICommand RefreshApplicationAudioProcessesCommand { get; }

        // Event to notify MainViewModel when control settings change
        public event EventHandler<ControlSettingsChangedEventArgs>? ControlSettingsChanged;

        private readonly ITtsService _ttsService;
        private readonly ISteamVRService _steamVrService;
        private readonly IAppUpdateService _appUpdateService;
        private readonly IModelUpdateService _modelUpdateService;
        private readonly ITranslationHotUpdateService _translationHotUpdateService;
        private readonly RuntimeLogBuffer _runtimeLogBuffer;
        private readonly DispatcherTimer _logAutoRefreshTimer;
        private readonly HttpClient _userCenterHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private bool _disposed;

        public SettingsViewModel(AppConfig config, IBackendAuthService authService, ITtsService ttsService, ISteamVRService steamVrService, IAppUpdateService appUpdateService, IModelUpdateService modelUpdateService, ITranslationHotUpdateService translationHotUpdateService, RuntimeLogBuffer runtimeLogBuffer)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
            _steamVrService = steamVrService ?? throw new ArgumentNullException(nameof(steamVrService));
            _appUpdateService = appUpdateService ?? throw new ArgumentNullException(nameof(appUpdateService));
            _modelUpdateService = modelUpdateService ?? throw new ArgumentNullException(nameof(modelUpdateService));
            _translationHotUpdateService = translationHotUpdateService ?? throw new ArgumentNullException(nameof(translationHotUpdateService));
            _runtimeLogBuffer = runtimeLogBuffer ?? throw new ArgumentNullException(nameof(runtimeLogBuffer));
            _logAutoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _logAutoRefreshTimer.Tick += OnLogAutoRefreshTimerTick;
            MicDevices.Add(MainViewModel.DefaultDeviceDisplayName);
            DesktopAudioDevices.Add(MainViewModel.DefaultDeviceDisplayName);
            ConfigureLanguageGrouping(SourceLanguages);
            ConfigureLanguageGrouping(TargetLanguages);
            LoginCommand = new RelayCommand(async () => await Login());
            ResetToDefaultCommand = new RelayCommand(async () => await ResetToDefault());
            OpenWebsiteUserCenterCommand = new RelayCommand(OpenWebsiteUserCenter);
            StartWebsiteLoginForMigrationCommand = new RelayCommand(async () => await StartWebsiteLoginForMigrationAsync());
            OpenConnectionEntryCommand = new RelayCommand(async () => await OpenConnectionEntryAsync());
            TestTtsCommand = new RelayCommand(async () => await TestTts());
            ResetKeywordsToDefaultCommand = new RelayCommand(ResetKeywordsToDefault);
            CheckAppUpdateCommand = new RelayCommand(async () => await CheckAppUpdateAsync());
            ExecuteAppUpdateActionCommand = new RelayCommand(async () => await ExecuteAppUpdateActionAsync(), CanExecuteAppUpdateAction);
            RefreshModelCatalogCommand = new RelayCommand(async () => await RefreshModelCatalogAsync());
            InstallModelCommand = new RelayCommand<ModelPackageInfo>(async model => await InstallModelAsync(model), model => model != null);
            SyncTranslationProfileCommand = new RelayCommand(async () => await SyncTranslationProfileAsync());
            OpenLogFileCommand = new RelayCommand(OpenCurrentSessionLogFile, () => !string.IsNullOrWhiteSpace(CurrentSessionLogPath));
            RefreshLogCommand = new RelayCommand(LoadCurrentSessionLog);
            ToggleBaseUrlLockCommand = new RelayCommand(ToggleBaseUrlLock);
            LoadPaymentGuideCommand = new RelayCommand(async () => await LoadPaymentGuideAsync(), CanLoadPaymentGuide);
            CreateDonationLinkCommand = new RelayCommand(async () => await CreateDonationLinkAsync(), CanCreateDonationLink);
            IncreaseDonationPriceCommand = new RelayCommand(IncreaseDonationPrice, CanAdjustDonationPrice);
            DecreaseDonationPriceCommand = new RelayCommand(DecreaseDonationPrice, CanAdjustDonationPrice);
            ActivateOrderCommand = new RelayCommand(async () => await ActivateOrderAsync(), CanActivateOrder);
            QueryAccountUsageCommand = new RelayCommand(async () => await QueryAccountUsageAsync(), () => !IsAccountUsageSubmitting);
            RefreshAccountOverviewCommand = new RelayCommand(async () => await RefreshAccountOverviewAsync(), () => !IsAccountOverviewRefreshing);
            DonationWizardNextCommand = new RelayCommand(async () => await AdvanceDonationWizardAsync(), CanAdvanceDonationWizard);
            DonationWizardBackCommand = new RelayCommand(GoBackDonationWizard, CanGoBackDonationWizard);
            RetryDonationVerificationCommand = new RelayCommand(async () => await StartDonationVerificationAsync(), CanRetryDonationVerification);
            RefreshApplicationAudioProcessesCommand = new RelayCommand(RefreshApplicationAudioProcesses);
            AppVersion = AppVersionHelper.GetDisplayVersion();
            TranslationProfileVersion = _config.TranslationProfileVersion;
            RefreshDesktopAudioDevices();
            RefreshTtsOutputDevices();
            RefreshUiLanguageOptions();
            RefreshLocalizedOptionCollections();
            LoadFromConfig();
            ApplyLogAutoRefreshState();
            SyncPendingAppUpdateFromService();

            _modelUpdateService.ModelsChanged += OnModelsChanged;
            _translationHotUpdateService.ProfileUpdated += OnTranslationProfileUpdated;

            ApplyAuthState(_authService.State);
            _authService.StateChanged += OnAuthStateChanged;

            // SteamVR overlay status (best-effort; overlay is optional)
            try
            {
                _steamVrService.RefreshStartupRegistrationStatus();
                SteamVrStatus = _steamVrService.Status;
                SteamVrStartupStatus = _steamVrService.StartupRegistrationStatus;
                _steamVrService.StatusChanged += OnSteamVrStatusChanged;
            }
            catch
            {
                // ignore; status is non-critical
            }
        }

        private void OnModelsChanged(object? sender, EventArgs e)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(UpdateLocalModelStatus);
                }
                else
                {
                    UpdateLocalModelStatus();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Model change event handler failed.");
            }
        }

        private void OnAuthStateChanged(object? sender, BackendAuthState state)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(() => ApplyAuthState(state));
                }
                else
                {
                    ApplyAuthState(state);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "更新连接状态失败");
            }
        }

        private void OnSteamVrStatusChanged(object? sender, string status)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => SteamVrStatus = status);
            }
            else
            {
                SteamVrStatus = status;
            }
        }

        private async Task Login()
        {
            if (UseLegacyBackendApi)
            {
                WebsiteAuthorizationStatus = GetUiString("AuthStatusLegacyLoggingIn", "正在登录旧版后端...");
                var legacyState = await _authService.LoginWithLegacyCredentialsAsync(Username, Password);
                ApplyAuthState(legacyState);
                return;
            }

            if (IsWebsiteAuthorizationInProgress && !string.IsNullOrWhiteSpace(WebsiteAuthorizationUrl))
            {
                _lastOpenedAuthorizationUrl = string.Empty;
                OpenAuthorizationUrlIfNeeded(WebsiteAuthorizationUrl);
                return;
            }

            _lastOpenedAuthorizationUrl = string.Empty;
            WebsiteAuthorizationStatus = GetUiString("UserCenterWebsiteLoginStarting", "正在启动官网授权登录...");
            var websiteState = await _authService.StartWebsiteLoginAsync();
            ApplyAuthState(websiteState);
        }

        private async Task OpenConnectionEntryAsync()
        {
            if (UseLegacyBackendApi)
            {
                return;
            }

            if (_authService.State.Status == BackendAuthStatus.LoggingIn && !HasWebsiteAuthorizationPrompt(_authService.State))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(WebsiteAuthorizationUrl))
            {
                _lastOpenedAuthorizationUrl = string.Empty;
                OpenAuthorizationUrlIfNeeded(WebsiteAuthorizationUrl);
                return;
            }

            if (IsWebsiteAuthorizedSession)
            {
                OpenWebsiteUserCenter();
                return;
            }

            await Login();
        }

        private async Task ResetToDefault()
        {
            _lastOpenedAuthorizationUrl = string.Empty;
            WebsiteAuthorizationStatus = string.Empty;
            WebsiteAuthorizationUserCode = string.Empty;
            WebsiteAuthorizationUrl = string.Empty;
            if (UseLegacyBackendApi)
            {
                WebsiteAuthorizationStatus = GetUiString("UserCenterLegacyLogoutStatus", "正在清除旧版后端登录状态...");
                var state = await _authService.LogoutAsync();
                ApplyAuthState(state);
            }
            else if (IsWebsiteAuthorizedSession || _config.UserInfo.IsWebsiteAuthorized)
            {
                WebsiteAuthorizationStatus = GetUiString("UserCenterLogoutStatus", "正在退出官网登录并切换到匿名试用模式...");
                var state = await _authService.LogoutAsync();
                ApplyAuthState(state);
            }
            else
            {
                var state = await _authService.SwitchToAnonymousTestModeAsync();
                ApplyAuthState(state);
            }
            Username = _config.UserInfo.Username;
            Password = string.Empty;
            DonationUsername = string.Empty;
            DonationPassword = string.Empty;
            ResetPaymentGuide(clearStatus: true);
            PaymentGuideStatus = string.Empty;
            PasswordUpdated?.Invoke(string.Empty);
        }

        private void OpenWebsiteUserCenter()
        {
            OpenExternalUrl(BackendUrlHelper.BuildPortalBaseUrl(_config));
        }

        private async Task StartWebsiteLoginForMigrationAsync()
        {
            if (UseLegacyBackendApi)
            {
                UseLegacyBackendApi = false;
            }

            if (IsWebsiteAuthorizedSession)
            {
                OpenWebsiteUserCenter();
                return;
            }

            await Login();
        }

        private static void OpenExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }

        private static string NormalizePlanType(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "upgrade" => "upgrade",
                "custom" => "custom",
                _ => "basic"
            };
        }

        private static decimal ParseDonationPrice(string? value)
        {
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice)
                ? parsedPrice
                : 0m;
        }

        private static bool ShouldSeedDonationCredentials(UserInfo userInfo)
        {
            return !userInfo.IsAnonymousTest;
        }

        private void SetDonationActions(IEnumerable<string> actionCodes)
        {
            DonationActions.Clear();
            foreach (var actionCode in actionCodes
                         .Where(static code => !string.IsNullOrWhiteSpace(code))
                         .Select(code => code.Trim().ToLowerInvariant())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DonationActions.Add(new DonationActionOption(actionCode, GetDonationActionDisplayName(actionCode)));
            }

            if (DonationActions.Count == 0)
            {
                DonationActions.Add(new DonationActionOption("renew", GetUiString("UserCenterActionRenew", "续费")));
                DonationActions.Add(new DonationActionOption("upgrade", GetUiString("UserCenterActionUpgrade", "升级")));
            }

            OnPropertyChanged(nameof(PendingDonationActionDisplayName));
        }

        private void RefreshDonationActions()
        {
            var existingActionCodes = DonationActions
                .Select(item => item.Code)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (existingActionCodes.Count == 0)
            {
                existingActionCodes.Add("renew");
                existingActionCodes.Add("upgrade");
            }

            SetDonationActions(existingActionCodes);
        }

        private void ResetDonationActions()
        {
            SetDonationActions(new[] { "renew", "upgrade" });
        }

        private void PopulateDonationActions(UserCenterPaymentGuideResponse payload)
        {
            SetDonationActions(payload.AllowedActions ?? Enumerable.Empty<string>());
        }

        private void OnTranslationProfileUpdated(object? sender, TranslationProfileUpdatedEventArgs e)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                void Refresh()
                {
                    TranslationProfileVersion = _config.TranslationProfileVersion;
                    RefreshRecognitionLanguageOptions();
                    RefreshTranslationLanguageOptions();
                    RefreshTranslationEngineOptions();
                }

                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(Refresh);
                }
                else
                {
                    Refresh();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Translation profile update event handler failed.");
            }
        }

        private string GetDonationActionDisplayName(string actionCode)
        {
            return actionCode switch
            {
                "upgrade" => GetUiString("UserCenterActionUpgrade", "升级"),
                "downgrade" => GetUiString("UserCenterActionDowngrade", "降档"),
                _ => GetUiString("UserCenterActionRenew", "续费")
            };
        }

        private static decimal NormalizeGuidePrice(decimal price, UserCenterPaymentGuideOption option)
        {
            var min = option.MinMonthlyPrice ?? option.DefaultMonthlyPrice ?? price;
            var step = Math.Max(1, option.Step ?? 5);

            var normalized = Math.Max(price, min);
            if (option.MaxMonthlyPrice.HasValue)
            {
                normalized = Math.Min(normalized, option.MaxMonthlyPrice.Value);
            }

            var offset = normalized - min;
            var snapped = min + Math.Round(offset / step, MidpointRounding.AwayFromZero) * step;
            snapped = Math.Max(snapped, min);
            if (option.MaxMonthlyPrice.HasValue)
            {
                snapped = Math.Min(snapped, option.MaxMonthlyPrice.Value);
            }

            return snapped;
        }

        private UserCenterPaymentGuideOption? GetCurrentDonationGuideOption()
        {
            return DonationAction switch
            {
                "upgrade" => _upgradeGuideOption,
                "downgrade" => _downgradeGuideOption,
                _ => _renewGuideOption
            };
        }

        private int? GetCurrentDonationGuideQuotaPreview()
        {
            var option = GetCurrentDonationGuideOption();
            if (option == null || !option.TargetResourceQuotaPreview.HasValue)
            {
                return null;
            }

            return option.TargetResourceQuotaPreview.Value;
        }

        private void ResetPaymentGuide(bool clearStatus = false)
        {
            _renewGuideOption = null;
            _upgradeGuideOption = null;
            _downgradeGuideOption = null;
            HasPaymentGuide = false;
            IsGuideAccountRegistered = false;
            PaymentGuideToken = string.Empty;
            DisplayPlanName = string.Empty;
            DisplayMonthlyPrice = 0m;
            DisplayResourceSummary = string.Empty;
            DonationGuideExplanation = string.Empty;
            DonationPriceStep = 5;
            ResourceIncrementPerStep = 0;
            if (clearStatus)
            {
                PaymentGuideStatus = string.Empty;
            }
            ClearPendingDonationRequest(showStaleMessage: false, clearStatusText: true);
            ResetDonationVerificationState(clearStatusText: true);
            ResetDonationActions();
            OnPropertyChanged(nameof(IsDonationPriceReadOnly));
            OnPropertyChanged(nameof(ResourceRuleSummary));
            OnPropertyChanged(nameof(DonationPriceGuideText));
            OnPropertyChanged(nameof(GuideAccountStatusText));
            OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
        }

        private void ApplyCurrentGuideOption()
        {
            var option = GetCurrentDonationGuideOption();
            if (option == null)
            {
                OnPropertyChanged(nameof(IsDonationPriceReadOnly));
                OnPropertyChanged(nameof(DonationPriceGuideText));
                return;
            }

            // For unregistered accounts: allow selecting the first plan price (do not force it back to 5.00).
            if (!IsGuideAccountRegistered && string.Equals(DonationAction, "renew", StringComparison.OrdinalIgnoreCase))
            {
                OnPropertyChanged(nameof(IsDonationPriceReadOnly));
                OnPropertyChanged(nameof(DonationPriceGuideText));
                OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
                return;
            }

            var currentPrice = ParseDonationPrice(DonationPrice);
            var sourcePrice = currentPrice > 0m
                ? currentPrice
                : option.DefaultMonthlyPrice ?? option.MinMonthlyPrice ?? 5m;
            DonationPrice = NormalizeGuidePrice(sourcePrice, option).ToString("F2", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(IsDonationPriceReadOnly));
            OnPropertyChanged(nameof(DonationPriceGuideText));
            OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
        }

        private bool CanLoadPaymentGuide()
        {
            return false;
        }

        private void OnSelectedPlanTypeChanged(string normalized)
        {
            _suppressPlanSelectionSync = true;
            try
            {
                switch (normalized)
                {
                    case "basic":
                        DonationAction = "renew";
                        DonationPrice = "5.00";
                        break;
                    case "upgrade":
                        DonationAction = "upgrade";
                        DonationPrice = "15.00";
                        break;
                    case "custom":
                        if (string.IsNullOrWhiteSpace(DonationPrice))
                        {
                            DonationPrice = IsRenewAction ? "5.00" : "15.00";
                        }
                        break;
                }
            }
            finally
            {
                _suppressPlanSelectionSync = false;
            }

            OnPropertyChanged(nameof(IsBasicPlanSelected));
            OnPropertyChanged(nameof(IsUpgradePlanSelected));
            OnPropertyChanged(nameof(IsCustomPlanSelected));
            OnPropertyChanged(nameof(DonationTotalPrice));
        }

        private void SyncSelectedPlanTypeFromDonationState()
        {
            if (_suppressPlanSelectionSync)
            {
                return;
            }

            var parsedPrice = ParseDonationPrice(DonationPrice);
            var normalized = string.Equals(DonationAction, "upgrade", StringComparison.OrdinalIgnoreCase) && parsedPrice == 15m
                ? "upgrade"
                : string.Equals(DonationAction, "renew", StringComparison.OrdinalIgnoreCase) && parsedPrice == 5m
                    ? "basic"
                    : "custom";

            if (SetField(ref _selectedPlanType, normalized))
            {
                OnPropertyChanged(nameof(IsBasicPlanSelected));
                OnPropertyChanged(nameof(IsUpgradePlanSelected));
                OnPropertyChanged(nameof(IsCustomPlanSelected));
            }
        }

        private void ResetAccountUsageOverview()
        {
            AccountRemainingDailySummary = "-";
            AccountDailyLimitSummary = "-";
            AccountAddonBalanceSummary = string.Empty;
            AccountUsageSummary = string.Empty;
            AccountExpirationSummary = "-";
        }

        private void ResetAccountUsageRange()
        {
            AccountUsageRangeSummary = string.Empty;
            AccountUsageDailyStats.Clear();
        }

        public void PrepareDonationWizard()
        {
            ResetPaymentGuide(clearStatus: true);
            ClearPendingDonationRequest(showStaleMessage: false, clearStatusText: true);
            ResetDonationVerificationState(clearStatusText: true);
            DonationWizardStepIndex = 0;
            PaymentGuideStatus = "账号管理、续费、订单和额度调整请在官网用户中心完成。";
            OpenWebsiteUserCenter();
        }

        private void ClearPendingDonationRequest(bool showStaleMessage, bool clearStatusText)
        {
            var hadPendingRequest = _hasPendingDonationRequest || !string.IsNullOrWhiteSpace(_donationUrl);

            _hasPendingDonationRequest = false;
            _pendingDonationAction = "renew";
            _pendingDonationMonths = 1;
            _pendingDonationMonthlyPrice = 5m;
            _pendingDonationExpectedDailyQuota = null;
            _pendingDonationMinimumExpirationUtc = null;
            PendingDonationOverview = string.Empty;
            DonationRemark = string.Empty;
            DonationUrl = string.Empty;
            OnPropertyChanged(nameof(PendingDonationActionDisplayName));
            OnPropertyChanged(nameof(PendingDonationMonthlyPriceDisplay));
            OnPropertyChanged(nameof(PendingDonationMonthsDisplay));
            OnPropertyChanged(nameof(PendingDonationExpectedDailyQuotaDisplay));
            OnPropertyChanged(nameof(PendingDonationMinimumExpirationDisplay));
            OnPropertyChanged(nameof(PendingDonationTotalPrice));

            if (clearStatusText)
            {
                DonationStatus = string.Empty;
            }
            else if (showStaleMessage && hadPendingRequest)
            {
                DonationStatus = GetUiString("UserCenterDonationLinkStale", "方案已变更，请重新生成发电链接。");
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private void CapturePendingDonationRequest(decimal monthlyPrice, int months)
        {
            _hasPendingDonationRequest = true;
            _pendingDonationAction = DonationAction;
            _pendingDonationMonths = months;
            _pendingDonationMonthlyPrice = monthlyPrice;
            _pendingDonationExpectedDailyQuota = TryCalculateDailyQuotaFromMonthlyPrice(monthlyPrice) ?? GetCurrentDonationGuideQuotaPreview();
            _pendingDonationMinimumExpirationUtc = DateTimeOffset.UtcNow.AddDays(months * 30).AddHours(-12);
            PendingDonationOverview = string.Format(
                CultureInfo.InvariantCulture,
                GetUiString("UserCenterDonationOverviewText", "{0} · ￥ {1:F2}/月 · {2} 个月"),
                GetDonationActionDisplayName(_pendingDonationAction),
                _pendingDonationMonthlyPrice,
                _pendingDonationMonths);
            OnPropertyChanged(nameof(PendingDonationActionDisplayName));
            OnPropertyChanged(nameof(PendingDonationMonthlyPriceDisplay));
            OnPropertyChanged(nameof(PendingDonationMonthsDisplay));
            OnPropertyChanged(nameof(PendingDonationExpectedDailyQuotaDisplay));
            OnPropertyChanged(nameof(PendingDonationMinimumExpirationDisplay));
            OnPropertyChanged(nameof(PendingDonationTotalPrice));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ResetDonationVerificationState(bool clearStatusText)
        {
            DonationVerificationAttempt = 0;
            DonationVerificationSucceeded = false;
            IsDonationVerificationRunning = false;
            if (clearStatusText)
            {
                DonationVerificationStatus = string.Empty;
            }
        }

        private bool CanAdvanceDonationWizard()
        {
            return false;
        }

        private Task AdvanceDonationWizardAsync()
        {
            DonationStatus = "客户端不再本地创建或激活订单，请打开官网用户中心处理。";
            OpenWebsiteUserCenter();
            return Task.CompletedTask;
        }

        private bool CanGoBackDonationWizard()
        {
            return DonationWizardStepIndex > 0 && !IsDonationVerificationRunning;
        }

        private void GoBackDonationWizard()
        {
            if (!CanGoBackDonationWizard())
            {
                return;
            }

            DonationWizardStepIndex = Math.Max(0, DonationWizardStepIndex - 1);
        }

        private bool CanRetryDonationVerification()
        {
            return false;
        }

        private Task LoadPaymentGuideAsync()
        {
            ResetPaymentGuide(clearStatus: true);
            PaymentGuideStatus = "客户端不再读取本地支付方案，请打开官网用户中心查看套餐、订单和额度。";
            OpenWebsiteUserCenter();
            return Task.CompletedTask;
        }

        private bool CanCreateDonationLink()
        {
            return false;
        }

        private bool CanAdjustDonationPrice()
        {
            var option = GetCurrentDonationGuideOption();
            if (option == null)
            {
                return false;
            }

            if (!HasPaymentGuide)
            {
                return false;
            }

            if (option.ReadOnly)
            {
                return false;
            }

            if (!IsGuideAccountRegistered && !string.Equals(DonationAction, "renew", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private void IncreaseDonationPrice()
        {
            AdjustDonationPrice(+1);
        }

        private void DecreaseDonationPrice()
        {
            AdjustDonationPrice(-1);
        }

        private void AdjustDonationPrice(int direction)
        {
            var option = GetCurrentDonationGuideOption();
            if (option == null)
            {
                return;
            }

            var step = Math.Max(1m, option.Step ?? DonationPriceStep);
            var min = option.MinMonthlyPrice ?? option.DefaultMonthlyPrice ?? step;
            var max = option.MaxMonthlyPrice;
            if (!decimal.TryParse(DonationPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var current))
            {
                current = option.DefaultMonthlyPrice ?? min;
            }

            var next = current + step * direction;
            next = Math.Max(min, next);
            if (max.HasValue)
            {
                next = Math.Min(max.Value, next);
            }

            DonationPrice = next.ToString("F2", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
        }

        private Task CreateDonationLinkAsync()
        {
            DonationStatus = "客户端不再本地创建订单或发电链接，请打开官网用户中心处理。";
            OpenWebsiteUserCenter();
            return Task.CompletedTask;
        }

        private bool CanActivateOrder()
        {
            return false;
        }

        private Task ActivateOrderAsync()
        {
            ActivationStatus = "客户端不再本地激活订单，请打开官网用户中心处理。";
            OpenWebsiteUserCenter();
            return Task.CompletedTask;
        }

        private Task StartDonationVerificationAsync()
        {
            DonationVerificationStatus = "客户端不再本地校验订单激活结果，请打开官网用户中心查看订单和额度状态。";
            OpenWebsiteUserCenter();
            return Task.CompletedTask;
        }

        private DonationVerificationResult ValidateDonationVerification(UserCenterAccountUsageResponse payload)
        {
            var errors = new List<string>();

            if (payload.IsTestUser)
            {
                errors.Add(GetUiString("UserCenterUsageTestUserBlocked", "当前模式不支持查询到期和用量。"));
            }

            if (_pendingDonationExpectedDailyQuota.HasValue)
            {
                if (!payload.DailyLimit.HasValue)
                {
                    errors.Add(GetUiString("UserCenterVerificationQuotaMissing", "服务端暂未返回当前方案资源数。"));
                }
                else if (payload.DailyLimit.Value != _pendingDonationExpectedDailyQuota.Value)
                {
                    errors.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        GetUiString("UserCenterVerificationQuotaMismatch", "当前资源数为 {0}/day，和本次发电预期的 {1}/day 不一致。"),
                        payload.DailyLimit.Value,
                        _pendingDonationExpectedDailyQuota.Value));
                }
            }

            if (_pendingDonationMinimumExpirationUtc.HasValue)
            {
                if (!payload.ExpirationUtc.HasValue)
                {
                    errors.Add(GetUiString("UserCenterVerificationExpirationMissing", "服务端暂未返回新的到期时间。"));
                }
                else if (payload.ExpirationUtc.Value < _pendingDonationMinimumExpirationUtc.Value)
                {
                    errors.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        GetUiString("UserCenterVerificationExpirationMismatch", "当前到期时间为 {0:yyyy-MM-dd HH:mm:ss}，尚未达到本次发电后的预期。"),
                        payload.ExpirationUtc.Value.ToLocalTime()));
                }
            }

            if (errors.Count == 0)
            {
                return new DonationVerificationResult(
                    true,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        GetUiString("UserCenterVerificationSuccess", "自动激活校验通过，当前资源数与到期时间已经和本次发电一致。")));
            }

            return new DonationVerificationResult(false, string.Join(" ", errors));
        }

        private async Task<AccountUsageFetchResult> FetchAccountUsageAsync(DateTime startDate, DateTime endDate)
        {
            try
            {
                var requestUrl = $"{BackendUrlHelper.BuildV2AccountBaseUrl(_config)}usage" +
                    $"?start_date={startDate:yyyy-MM-dd}&end_date={endDate:yyyy-MM-dd}";
                using var response = await _authService.SendAuthorizedAsync(
                    "UserCenter account usage",
                    () => new HttpRequestMessage(HttpMethod.Get, requestUrl),
                    (request, cancellationToken) =>
                    {
                        return _userCenterHttpClient.SendAsync(request, cancellationToken);
                    });
                if (response == null)
                {
                    return AccountUsageFetchResult.LoginRequired();
                }

                var raw = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.Warning("FetchAccountUsageAsync returned 401 after centralized auth retry. detail={Detail}", ExtractErrorMessage(raw));
                    return AccountUsageFetchResult.LoginRequired();
                }

                if (!response.IsSuccessStatusCode)
                {
                    return AccountUsageFetchResult.Fail(ExtractErrorMessage(raw));
                }

                var payload = JsonSerializer.Deserialize<UserCenterAccountUsageResponse>(raw);
                if (payload == null)
                {
                    return AccountUsageFetchResult.Fail(GetUiString("UserCenterUsageEmptyResponse", "查询结果为空，请稍后重试。"));
                }

                return AccountUsageFetchResult.Success(payload);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "FetchAccountUsageAsync failed.");
                return AccountUsageFetchResult.Fail(ex.Message);
            }
        }

        private void ApplyAccountOverviewPayload(UserCenterAccountUsageResponse payload)
        {
            var dailyLimitText = payload.DailyLimit?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var todaySuccessCount = payload.TodaySuccessCount ?? payload.DailySuccessCount;
            var usedResourceValue = payload.TodayDailyResourceUsed ?? payload.ResourceUsed ?? 0;
            var usedText = usedResourceValue.ToString(CultureInfo.InvariantCulture);
            var requestText = todaySuccessCount.ToString(CultureInfo.InvariantCulture);
            var planLabel = !string.IsNullOrWhiteSpace(payload.PlanName)
                ? payload.PlanName!.Trim()
                : GetUiString("UserCenterUsagePermanent", "长期有效");
            var addonBalance = payload.AddonResourceBalance ?? 0;

            AccountRemainingDailySummary = payload.RemainingDaily?.ToString(CultureInfo.InvariantCulture) ?? "-";
            AccountDailyLimitSummary = dailyLimitText;
            AccountAddonBalanceSummary = addonBalance > 0
                ? $"加油包余额 {addonBalance.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;
            AccountUsageSummary = addonBalance > 0
                ? $"当前套餐 {planLabel}，今日请求 {requestText} 次，资源消耗 {usedText}/{dailyLimitText}，加油包余额 {addonBalance.ToString(CultureInfo.InvariantCulture)}"
                : $"当前套餐 {planLabel}，今日请求 {requestText} 次，资源消耗 {usedText}/{dailyLimitText}";

            if (payload.ExpirationUtc.HasValue)
            {
                var utcNow = DateTimeOffset.UtcNow;
                var remaining = payload.ExpirationUtc.Value - utcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    AccountExpirationSummary = GetUiString("UserCenterUsageExpired", "已过期");
                }
                else
                {
                    var days = Math.Ceiling(remaining.TotalDays);
                    AccountExpirationSummary = $"{days.ToString(CultureInfo.InvariantCulture)} 天";
                }
            }
            else
            {
                AccountExpirationSummary = GetUiString("UserCenterUsagePermanent", "长期有效");
            }
        }

        private void ApplyAccountUsagePayload(UserCenterAccountUsageResponse payload, DateTime startDate, DateTime endDate)
        {
            var dailyRows = BuildDailyUsageRows(payload, startDate, endDate);
            foreach (var row in dailyRows)
            {
                AccountUsageDailyStats.Add(row);
            }

            var totalSuccess = dailyRows.Sum(r => r.SuccessCount);
            var limitValues = dailyRows.Where(r => r.DailyLimit.HasValue).Select(r => r.DailyLimit!.Value).ToList();
            var remainingValues = dailyRows.Where(r => r.RemainingDaily.HasValue).Select(r => r.RemainingDaily!.Value).ToList();
            var totalLimitText = limitValues.Count > 0 ? limitValues.Sum().ToString(CultureInfo.InvariantCulture) : "-";
            var totalRemainingText = remainingValues.Count > 0 ? remainingValues.Sum().ToString(CultureInfo.InvariantCulture) : "-";

            AccountUsageRangeSummary = $"{totalSuccess.ToString(CultureInfo.InvariantCulture)}/{totalLimitText} (剩余 {totalRemainingText})";

            AccountUsageStatus = $"{GetUiString("UserCenterUsageQuerySuccess", "查询成功 (按 UTC 日统计)")} ({dailyRows.Count.ToString(CultureInfo.InvariantCulture)} 天)";
        }

        private async Task QueryAccountUsageAsync()
        {
            if (UseLegacyBackendApi)
            {
                AccountUsageStatus = GetUiString("UserCenterUsageLegacyModeBlocked", "旧版兼容模式不支持官网账户概况，请切换到新版接口后查看。");
                ResetAccountUsageRange();
                return;
            }

            if (_authService.State.IsAnonymousMode)
            {
                AccountUsageStatus = GetUiString("UserCenterUsageTestUserBlocked", "匿名试用模式不显示正式账户用量，请前往官网授权登录后查看。");
                ResetAccountUsageRange();
                return;
            }

            var startDate = AccountUsageStartDate?.Date;
            var endDate = AccountUsageEndDate?.Date;
            if (!startDate.HasValue || !endDate.HasValue)
            {
                AccountUsageStatus = GetUiString("UserCenterUsageDateRequired", "请选择查询开始日期和结束日期。");
                return;
            }

            if (startDate.Value > endDate.Value)
            {
                AccountUsageStatus = GetUiString("UserCenterUsageDateRangeInvalid", "查询开始日期不能晚于结束日期。");
                return;
            }

            IsAccountUsageSubmitting = true;
            AccountUsageStatus = GetUiString("UserCenterUsageQuerying", "正在查询账户用量与到期时间...");
            ResetAccountUsageRange();
            try
            {
                var usageResult = await FetchAccountUsageAsync(startDate.Value, endDate.Value);
                if (usageResult.RequiresLogin)
                {
                    AccountUsageStatus = GetUiString("UserCenterUsageNeedLogin", "请先登录正式账户后再查询。");
                    ResetAccountUsageRange();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(usageResult.ErrorMessage))
                {
                    AccountUsageStatus = $"{GetUiString("UserCenterUsageQueryFailed", "查询失败")}: {usageResult.ErrorMessage}";
                    return;
                }

                var payload = usageResult.Payload;
                if (payload == null)
                {
                    AccountUsageStatus = GetUiString("UserCenterUsageEmptyResponse", "查询结果为空，请稍后重试。");
                    ResetAccountUsageRange();
                    return;
                }

                if (payload.IsTestUser)
                {
                    AccountUsageStatus = GetUiString("UserCenterUsageTestUserBlocked", "当前模式不支持查询到期和用量。");
                    ResetAccountUsageRange();
                    return;
                }

                ApplyAccountUsagePayload(payload, startDate.Value, endDate.Value);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "QueryAccountUsageAsync failed.");
                AccountUsageStatus = $"{GetUiString("UserCenterUsageQueryFailed", "查询失败")}: {ex.Message}";
            }
            finally
            {
                IsAccountUsageSubmitting = false;
            }
        }

        private static List<AccountUsageDailyStatRow> BuildDailyUsageRows(UserCenterAccountUsageResponse payload, DateTime startDate, DateTime endDate)
        {
            var byDate = new Dictionary<DateTime, AccountUsageDailyStatRow>();
            if (payload.DailyStats != null)
            {
                foreach (var item in payload.DailyStats)
                {
                    var date = ParseUsageDate(item);
                    if (!date.HasValue)
                    {
                        continue;
                    }

                    var used = item.DailySuccessCount ?? item.SuccessCount ?? item.Used ?? 0;
                    var limit = item.DailyLimit ?? item.Limit;
                    var remaining = item.RemainingDaily ?? item.Remaining;
                    byDate[date.Value.Date] = new AccountUsageDailyStatRow(date.Value.Date, used, limit, remaining);
                }
            }

            var rows = byDate.Values
                .Where(r => r.Date >= startDate.Date && r.Date <= endDate.Date)
                .OrderByDescending(r => r.Date)
                .ToList();
            if (rows.Count == 0)
            {
                rows.Add(new AccountUsageDailyStatRow(endDate.Date, payload.DailySuccessCount, payload.DailyLimit, payload.RemainingDaily));
            }

            return rows;
        }

        private static DateTime? ParseUsageDate(UserCenterAccountUsageDailyResponse item)
        {
            foreach (var candidate in new[] { item.Date, item.Day, item.DateUtc })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                {
                    return dto.ToLocalTime().Date;
                }

                if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                {
                    return dt.Date;
                }
            }

            return null;
        }

        private void ToggleBaseUrlLock()
        {
            if (!_isBaseUrlLocked)
            {
                IsBaseUrlLocked = true;
                return;
            }

            var title = GetUiString("UnlockBaseUrlConfirmTitle", "Unlock Server Address");
            var message = GetUiString("UnlockBaseUrlConfirmMessage", "Editing server address may break connectivity. Unlock and continue?");
            var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                IsBaseUrlLocked = false;
            }
        }

        private static string GetUiString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key) as string ?? fallback;
        }

        private bool IsEnglishUiLanguage()
        {
            return string.Equals(App.NormalizeUiLanguage(_uiLanguage), "en-US", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsJapaneseUiLanguage()
        {
            return string.Equals(App.NormalizeUiLanguage(_uiLanguage), "ja-JP", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshLocalizedOptionCollections()
        {
            RefreshActivationMethodOptions();
            RefreshTranslatorRegionOptions();
            RefreshDonationActions();
            RefreshRecognitionLanguageOptions();
            RefreshTranslationLanguageOptions();
            RefreshTranslationEngineOptions();
            ApplyAuthState(_authService.State);
        }

        private void RefreshActivationMethodOptions()
        {
            ActivationMethods.Clear();
            ActivationMethods.Add(new ActivationMethodOption(
                "remark",
                GetUiString("ActivationMethodRemark", "订单备注激活")));
            ActivationMethods.Add(new ActivationMethodOption(
                "out_trade_no",
                GetUiString("ActivationMethodOutTradeNo", "商家订单号激活")));
        }

        private void RefreshTranslatorRegionOptions()
        {
            TranslatorRegions.Clear();
            TranslatorRegions.Add(new LanguageOption("CN", GetUiString("TranslatorRegion_CN", "中国大陆")));
            TranslatorRegions.Add(new LanguageOption("EN", GetUiString("TranslatorRegion_EN", "其他")));
        }

        private void RefreshUiLanguageOptions()
        {
            UiLanguages.Clear();
            UiLanguages.Add(new LanguageOption("zh-CN", "中文"));
            UiLanguages.Add(new LanguageOption("en-US", "English"));
            UiLanguages.Add(new LanguageOption("ja-JP", "日本語"));
        }

        private async Task CheckAppUpdateAsync()
        {
            try
            {
                UpdateStatusText = "Checking updates...";
                UpdateProgress = 0;
                _appUpdateState = AppUpdateState.Checking;
                _pendingAppUpdate = await _appUpdateService.CheckForUpdatesAsync();
                if (_pendingAppUpdate != null)
                {
                    UpdateStatusText = $"New version available: {_pendingAppUpdate.ResolvedDisplayVersion}";
                    _appUpdateState = _appUpdateService.State;
                }
                else
                {
                    UpdateStatusText = "You are already on the latest version.";
                    _appUpdateState = AppUpdateState.NoUpdate;
                }

                UpdateAppUpdateActionState();
            }
            catch (InvalidOperationException ex)
            {
                _logger.Information(ex, "CheckAppUpdateAsync skipped because NetSparkle is not fully configured.");
                _pendingAppUpdate = null;
                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = ex.Message;
                UpdateAppUpdateActionState();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "CheckAppUpdateAsync failed.");
                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = $"Update check failed: {ex.Message}";
                UpdateAppUpdateActionState();
            }
            finally
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ExecuteAppUpdateActionAsync()
        {
            if (_appUpdateState == AppUpdateState.ReadyToRestart)
            {
                await ApplyPendingAppUpdateAsync();
                return;
            }

            if (_pendingAppUpdate == null)
            {
                UpdateStatusText = "No update is ready to download.";
                return;
            }

            try
            {
                _appUpdateState = AppUpdateState.Downloading;
                UpdateAppUpdateActionState();
                UpdateStatusText = "Downloading update...";
                UpdateProgress = 0;
                var progress = new Progress<double>(value => UpdateProgress = value);
                var downloaded = await _appUpdateService.DownloadUpdateAsync(progress);
                if (!downloaded)
                {
                    _appUpdateState = AppUpdateState.Failed;
                    UpdateStatusText = "Update download failed.";
                    UpdateAppUpdateActionState();
                    return;
                }

                SyncPendingAppUpdateFromService();
                UpdateStatusText = _pendingAppUpdate == null
                    ? "Update downloaded."
                    : $"Update ready: {_pendingAppUpdate.ResolvedDisplayVersion}. Restart to apply.";
            }
            catch (InvalidOperationException ex)
            {
                _logger.Information(ex, "ExecuteAppUpdateActionAsync skipped because NetSparkle is not fully configured.");
                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = ex.Message;
                UpdateAppUpdateActionState();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ExecuteAppUpdateActionAsync failed during download.");
                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = $"Update download failed: {ex.Message}";
                UpdateAppUpdateActionState();
            }
            finally
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ApplyPendingAppUpdateAsync()
        {
            var versionText = _pendingAppUpdate?.ResolvedDisplayVersion ?? "the downloaded update";
            var applyNow = MessageBox.Show(
                $"Update {versionText} is ready. Restart now to apply it?",
                "VRCLS Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (applyNow != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _appUpdateState = AppUpdateState.Applying;
                UpdateAppUpdateActionState();
                UpdateStatusText = "Update is being applied. VRCLS will now exit.";
                var applied = await _appUpdateService.ApplyUpdateAndRestartAsync();
                if (applied)
                {
                    Application.Current?.Shutdown();
                    return;
                }

                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = "Failed to schedule app restart for update.";
                UpdateAppUpdateActionState();
            }
            catch (InvalidOperationException ex)
            {
                _logger.Information(ex, "ApplyPendingAppUpdateAsync skipped because NetSparkle is not fully configured.");
                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = ex.Message;
                UpdateAppUpdateActionState();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ApplyPendingAppUpdateAsync failed.");
                _appUpdateState = AppUpdateState.Failed;
                UpdateStatusText = $"Failed to apply update: {ex.Message}";
                UpdateAppUpdateActionState();
            }
            finally
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool CanExecuteAppUpdateAction()
        {
            return _appUpdateState is AppUpdateState.UpdateAvailable or AppUpdateState.ReadyToRestart;
        }

        private void SyncPendingAppUpdateFromService()
        {
            _pendingAppUpdate = _appUpdateService.PendingUpdate;
            _appUpdateState = _appUpdateService.State;

            if (_appUpdateState == AppUpdateState.ReadyToRestart && _pendingAppUpdate != null)
            {
                UpdateStatusText = $"Update ready: {_pendingAppUpdate.ResolvedDisplayVersion}. Restart to apply.";
                UpdateProgress = 100;
            }

            UpdateAppUpdateActionState();
        }

        private void UpdateAppUpdateActionState()
        {
            AppUpdateActionText = _appUpdateState == AppUpdateState.ReadyToRestart
                ? "Restart to update"
                : "Download update";
        }

        private async Task RefreshModelCatalogAsync()
        {
            try
            {
                ModelUpdateStatusText = "Fetching model catalog...";
                var models = await _modelUpdateService.GetAvailableModelsAsync();
                AvailableModelPackages.Clear();
                foreach (var model in models.OrderBy(m => m.Type).ThenBy(m => m.Name))
                {
                    AvailableModelPackages.Add(model);
                }

                ModelUpdateStatusText = AvailableModelPackages.Count == 0
                    ? "Model catalog is empty."
                    : $"Loaded {AvailableModelPackages.Count} model packages.";
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "RefreshModelCatalogAsync failed.");
                ModelUpdateStatusText = $"Failed to fetch model catalog: {ex.Message}";
            }
        }

        private async Task InstallModelAsync(ModelPackageInfo? model)
        {
            if (model == null)
            {
                return;
            }

            try
            {
                var ok = await DownloadAndInstallModelsAsync(new[] { model });
                if (ok)
                {
                    ModelUpdateStatusText = $"Model installed: {model.Name} ({model.Version})";
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "InstallModelAsync failed.");
                ModelUpdateStatusText = $"Model install failed: {ex.Message}";
            }
        }

        private async Task SyncTranslationProfileAsync()
        {
            try
            {
                TranslationUpdateStatusText = "Syncing translation profile...";
                TranslationUpdateProgress = 0;
                var progress = new Progress<double>(value => TranslationUpdateProgress = value);
                var updated = await _translationHotUpdateService.SyncLatestProfileAsync(progress);
                TranslationProfileVersion = _config.TranslationProfileVersion;
                TranslationUpdateStatusText = updated
                    ? $"Translation profile updated to {TranslationProfileVersion}"
                    : "Translation profile is already up to date.";

                MicTranslationServer = _config.ResolveTranslationEngine("Mic");
                DesktopTranslationServer = _config.ResolveTranslationEngine("Desktop");
                RefreshRecognitionLanguageOptions();
                RefreshTranslationLanguageOptions();
                RefreshTranslationEngineOptions();
                RefreshOpenAiPresetNames();
                SelectedOpenAiPreset = _config.SelectedOpenAiPreset;
                HasPendingTranslationConfigChanges = false;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SyncTranslationProfileAsync failed.");
                TranslationUpdateStatusText = $"Translation sync failed: {ex.Message}";
            }
        }

        private void ApplyAuthState(BackendAuthState state)
        {
            IsCurrentLoginTestAccount = state.IsAnonymousMode;
            NeedsFormalDonationCredentials = false;
            var localizedError = AuthUiText.LocalizeErrorMessage(state.ErrorMessage);

            switch (state.Status)
            {
                case BackendAuthStatus.LoggingIn:
                    if (state.IsLegacyCredentials)
                    {
                        ConnectionStatus = GetUiString("AuthStatusLegacyLoggingIn", "正在登录旧版后端...");
                        WebsiteAuthorizationUserCode = string.Empty;
                        WebsiteAuthorizationUrl = string.Empty;
                        WebsiteAuthorizationStatus = ConnectionStatus;
                    }
                    else if (HasWebsiteAuthorizationPrompt(state))
                    {
                        var authorizationUrl = BackendUrlHelper.ResolvePortalUrl(
                            _config,
                            state.VerificationUriComplete ?? state.VerificationUri,
                            "/app/authorize");
                        ConnectionStatus = GetUiString("AuthStatusWebsiteAwaitingAuthorization", "等待官网授权...");
                        WebsiteAuthorizationUserCode = state.UserCode ?? string.Empty;
                        WebsiteAuthorizationUrl = authorizationUrl;
                        WebsiteAuthorizationStatus = string.IsNullOrWhiteSpace(state.UserCode)
                            ? ConnectionStatus
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                GetUiString("AuthStatusWebsiteAuthorizationPrompt", "请在浏览器中完成官网授权。授权码：{0}"),
                                state.UserCode);
                        OpenAuthorizationUrlIfNeeded(authorizationUrl);
                    }
                    else if (state.IsWebsiteAuthorized)
                    {
                        ConnectionStatus = GetUiString("AuthStatusWebsiteRefreshingSession", "正在恢复官网登录状态...");
                        WebsiteAuthorizationUserCode = string.Empty;
                        WebsiteAuthorizationUrl = string.Empty;
                        WebsiteAuthorizationStatus = ConnectionStatus;
                    }
                    else
                    {
                        ConnectionStatus = GetUiString("AuthStatusSwitchingAnonymous", "正在切换匿名试用模式...");
                        WebsiteAuthorizationUserCode = string.Empty;
                        WebsiteAuthorizationUrl = string.Empty;
                        WebsiteAuthorizationStatus = ConnectionStatus;
                    }
                    break;
                case BackendAuthStatus.LoggedIn:
                    WebsiteAuthorizationUserCode = string.Empty;
                    WebsiteAuthorizationUrl = string.Empty;
                    if (state.HasToken)
                    {
                        ConnectionStatus = state.IsAnonymousMode
                            ? GetUiString("AuthStatusAnonymousMode", "当前处于匿名试用模式")
                            : state.IsLegacyCredentials
                                ? GetUiString("AuthStatusLegacyConnected", "已登录旧版后端")
                                : GetUiString("AuthStatusWebsiteConnected", "已通过官网授权登录");
                        if (state.IsWebsiteAuthorized)
                        {
                            WebsiteAuthorizationStatus = GetUiString("AuthStatusWebsiteAuthorizationCompleted", "官网授权登录已完成。");
                            _ = RefreshAccountOverviewAsync();
                        }
                        else if (state.IsLegacyCredentials)
                        {
                            WebsiteAuthorizationStatus = GetUiString("AuthStatusLegacyLoginCompleted", "旧版后端登录已完成。");
                        }
                        else
                        {
                            WebsiteAuthorizationStatus = string.Empty;
                        }
                    }
                    else
                    {
                        ConnectionStatus = GetUiString("AuthStatusTokenEmpty", "连接失败: Token为空");
                        WebsiteAuthorizationStatus = ConnectionStatus;
                    }
                    break;
                case BackendAuthStatus.Failed:
                    WebsiteAuthorizationUserCode = string.Empty;
                    WebsiteAuthorizationUrl = string.Empty;
                    ConnectionStatus = string.IsNullOrWhiteSpace(localizedError)
                        ? GetUiString("AuthStatusConnectionFailed", "连接失败")
                        : string.Format(
                            CultureInfo.CurrentCulture,
                            GetUiString("AuthStatusConnectionFailedWithReason", "连接失败: {0}"),
                            localizedError);
                    WebsiteAuthorizationStatus = ConnectionStatus;
                    break;
                case BackendAuthStatus.NotLoggedIn:
                default:
                    WebsiteAuthorizationUserCode = string.Empty;
                    WebsiteAuthorizationUrl = string.Empty;
                    WebsiteAuthorizationStatus = string.Empty;
                    ConnectionStatus = state.IsAnonymousMode
                        ? GetUiString("AuthStatusAnonymousDisconnected", "匿名试用模式未连接")
                        : string.IsNullOrWhiteSpace(localizedError)
                            ? GetUiString("AuthStatusNotConnected", "未连接")
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                GetUiString("AuthStatusConnectionFailedWithReason", "连接失败: {0}"),
                                localizedError);
                    break;
            }

            RefreshAuthDisplayBindings();
        }

        private void OpenAuthorizationUrlIfNeeded(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || string.Equals(url, _lastOpenedAuthorizationUrl, StringComparison.Ordinal))
            {
                return;
            }

            _lastOpenedAuthorizationUrl = url;
            try
            {
                OpenExternalUrl(url);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Open website authorization URL failed.");
            }
        }

        private static bool HasWebsiteAuthorizationPrompt(BackendAuthState state)
        {
            return state.Status == BackendAuthStatus.LoggingIn
                && state.IsWebsiteAuthorized
                && (!string.IsNullOrWhiteSpace(state.UserCode)
                    || !string.IsNullOrWhiteSpace(state.VerificationUriComplete)
                    || !string.IsNullOrWhiteSpace(state.VerificationUri));
        }

        private void RefreshAuthDisplayBindings()
        {
            OnPropertyChanged(nameof(DisplayUsername));
            OnPropertyChanged(nameof(AuthModeSummary));
            OnPropertyChanged(nameof(UsernameAliasSummary));
            OnPropertyChanged(nameof(UserUuidSummary));
            OnPropertyChanged(nameof(EmailSummary));
            OnPropertyChanged(nameof(ConnectionBadgeStatus));
            OnPropertyChanged(nameof(IsWebsiteAuthorizedSession));
            OnPropertyChanged(nameof(IsWebsiteAuthorizationInProgress));
            OnPropertyChanged(nameof(IsModernBackendApi));
            OnPropertyChanged(nameof(ShouldShowLegacyMigrationGuide));
            OnPropertyChanged(nameof(ConnectionSectionTitle));
            OnPropertyChanged(nameof(ConnectionSectionDescription));
            OnPropertyChanged(nameof(ConnectionEntryHint));
            OnPropertyChanged(nameof(BaseUrlDescription));
            OnPropertyChanged(nameof(BaseUrlHint));
            OnPropertyChanged(nameof(LoginButtonText));
            OnPropertyChanged(nameof(ResetLoginButtonText));
            OnPropertyChanged(nameof(WebsiteAuthorizationUserCode));
            OnPropertyChanged(nameof(WebsiteAuthorizationUrl));
            OnPropertyChanged(nameof(WebsiteAuthorizationStatus));
            CommandManager.InvalidateRequerySuggested();
        }

public void SyncFromMainViewModel(MainViewModel mainViewModel)
        {
            if (mainViewModel == null) return;

            // Updating ItemsSource can cause ComboBox selection to re-evaluate and push a new value
            // back into the binding. Guard with _isLoadingConfig to avoid overwriting persisted config.
            var currentMic = MicName;

            _isLoadingConfig = true;
            try
            {
                MicDevices.Clear();

                // Always keep "default device" option at the top.
                MicDevices.Add(MainViewModel.DefaultDeviceDisplayName);

                foreach (var device in mainViewModel.MicDevices.Distinct())
                {
                    if (string.IsNullOrWhiteSpace(device)) continue;
                    if (string.Equals(device, MainViewModel.DefaultDeviceDisplayName, StringComparison.OrdinalIgnoreCase)) continue;
                    MicDevices.Add(device);
                }

                // If the previously selected device is not currently enumerated, keep it visible
                // so that opening Settings does not silently revert to default.
                if (!string.IsNullOrWhiteSpace(currentMic) && !MicDevices.Contains(currentMic))
                {
                    MicDevices.Add(currentMic);
                }

                MicName = currentMic;
            }
            finally
            {
                _isLoadingConfig = false;
            }

            HasPendingTranslationConfigChanges = false;

            _logger?.Information("SettingsViewModel同步MainViewModel设备列表");
        }

        public void PersistConfig()
        {
            try
            {
                VRCLS.UI.App.SaveConfigToDisk(_config);
                HasPendingTranslationConfigChanges = false;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "保存配置失败");
            }
        }

        private async Task RefreshAccountOverviewAsync()
        {
            if (UseLegacyBackendApi)
            {
                ResetAccountUsageOverview();
                return;
            }

            if (_authService.State.IsAnonymousMode)
            {
                ResetAccountUsageOverview();
                return;
            }

            IsAccountOverviewRefreshing = true;
            try
            {
                var today = DateTime.UtcNow.Date;
                var usageResult = await FetchAccountUsageAsync(today, today);
                if (usageResult.RequiresLogin || !string.IsNullOrWhiteSpace(usageResult.ErrorMessage) || usageResult.Payload == null || usageResult.Payload.IsTestUser)
                {
                    return;
                }

                ApplyAccountOverviewPayload(usageResult.Payload);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "RefreshAccountOverviewAsync failed.");
            }
            finally
            {
                IsAccountOverviewRefreshing = false;
            }
        }

        public bool HasPendingTranslationConfigChanges
        {
            get => _hasPendingTranslationConfigChanges;
            private set => SetField(ref _hasPendingTranslationConfigChanges, value);
        }

        private void MarkTranslationConfigChanged()
        {
            if (_isLoadingConfig)
            {
                return;
            }

            HasPendingTranslationConfigChanges = true;
        }

        private void LoadFromConfig()
        {
            _logger.Debug("SettingsViewModel: 开始从配置加载数据");
            _isLoadingConfig = true;

            try
            {
                EnsureApplicationAudioDefaults();

                // 服务器与鉴权
                _logger.Debug("SettingsViewModel: 加载前的配置 - UserInfo: {UserInfo}", _config.UserInfo);
                Username = _config.UserInfo.Username;
                Password = _config.UserInfo.Password;
                DonationUsername = ShouldSeedDonationCredentials(_config.UserInfo) ? Username : string.Empty;
                DonationPassword = ShouldSeedDonationCredentials(_config.UserInfo) ? Password : string.Empty;
                DonationAction = "renew";
                DonationMonths = 1;
                DonationPrice = "5.00";
                SelectedPlanType = "basic";
                DonationStatus = string.Empty;
                DonationRemark = string.Empty;
                DonationUrl = string.Empty;
                ResetPaymentGuide(clearStatus: true);
                ClearPendingDonationRequest(showStaleMessage: false, clearStatusText: true);
                ResetDonationVerificationState(clearStatusText: true);
                DonationWizardStepIndex = 0;
                ActivationMethod = "remark";
                ActivationInput = string.Empty;
                ActivationStatus = string.Empty;
                AccountUsageStatus = string.Empty;
                ResetAccountUsageOverview();
                // 初始区间与默认值保持一致：按 UTC 日往前 6 天
                AccountUsageStartDate = DateTime.UtcNow.Date.AddDays(-6);
                AccountUsageEndDate = DateTime.UtcNow.Date;
                AccountUsageDailyStats.Clear();
                _logger.Debug("SettingsViewModel: 加载后 - Username: {Username}, Password: {Password}", Username, !string.IsNullOrEmpty(Password) ? "****" : "(empty)");
                BaseUrl = _config.BaseUrl;
                UseLegacyBackendApi = _config.UseLegacyBackendApi;
                IsBaseUrlLocked = true;
                ApiIp = _config.ApiIp;
                ApiPort = _config.ApiPort;

            // 音频设置 - 麦克风（将配置值转换为显示名）
            MicName = _config.MicName == MainViewModel.DefaultDeviceConfigValue
                ? MainViewModel.DefaultDeviceDisplayName
                : _config.MicName;
            VoiceMode = NormalizeVoiceMode(_config.VoiceMode);
            AutoStartMic = _config.AutoStartMic;
            DynamicThreshold = _config.DynamicThreshold;
            CustomThreshold = _config.CustomThreshold;
            VoiceHotKey = _config.VoiceHotKey;
            MicPressingKey = _config.MicPressingKey;

            // 音频设置 - 桌面音频
            var configuredDesktopDevice = string.IsNullOrWhiteSpace(_config.GameMicName)
                ? MainViewModel.DefaultDeviceDisplayName
                : (_config.GameMicName == MainViewModel.DefaultDeviceConfigValue
                    ? MainViewModel.DefaultDeviceDisplayName
                    : _config.GameMicName);
            if (!DesktopAudioDevices.Contains(configuredDesktopDevice))
            {
                DesktopAudioDevices.Add(configuredDesktopDevice);
            }
            GameMicName = configuredDesktopDevice;
            if (_config.DesktopAudioSourceModeValue == VRCLS.Core.Models.DesktopAudioSourceMode.Application
                && !IsApplicationAudioCaptureSupported)
            {
                _logger.Warning(
                    "当前系统版本不支持指定应用音频，已将监听来源从应用音频回退到系统监听音频。Build={Build}",
                    AudioPlatformHelper.GetCurrentWindowsBuildNumber());
                _config.DesktopAudioSourceMode = (int)VRCLS.Core.Models.DesktopAudioSourceMode.System;
            }

            DesktopAudioSourceMode = _config.DesktopAudioSourceMode;
            EnsureSelectedApplicationAudioProcessPresent();
            SelectedApplicationAudioProcessId = ResolveInitialApplicationAudioProcessSelection();
            GameVoiceMode = _config.GameVoiceMode;
            AutoStartDesktop = _config.AutoStartDesktop;
            GameCustomThreshold = _config.GameCustomThreshold;
            GameVoiceHotKey = _config.GameVoiceHotKey;
            CapPressingKey = _config.CapPressingKey;

            // 识别设置
            UseLocalRecognition = string.Equals(_config.RecognitionMode, "local", StringComparison.OrdinalIgnoreCase);
            UpdateLocalModelStatus();

            ControlEnabled = _config.ControlEnabled;
            CommandFeedbackEnabled = _config.CommandFeedbackEnabled;
            ControlAutoStart = _config.ControlAutoStart;
            ControlMicSourceMode = _config.ControlMicSourceMode;
            ControlMicName = _config.ControlMicName == MainViewModel.DefaultDeviceConfigValue
                ? MainViewModel.DefaultDeviceDisplayName
                : _config.ControlMicName;
            ControlRequireWakeForCommands = _config.ControlRequireWakeForCommands;
            ControlWakeWindowSeconds = _config.ControlWakeWindowSeconds;

            LoadExperimentalKeywordActionsFromConfig();

            // VAD 设置
            var micVadRoute = _config.ResolveVadRoute("Mic");
            MicVadMinSilenceDuration = micVadRoute.MinSilenceDuration;
            MicVadMaxSegmentSeconds = micVadRoute.MaxSegmentSeconds;
            MicVadThreshold = micVadRoute.Threshold;

            var desktopVadRoute = _config.ResolveVadRoute("Desktop");
            DesktopVadMinSilenceDuration = desktopVadRoute.MinSilenceDuration;
            DesktopVadMaxSegmentSeconds = desktopVadRoute.MaxSegmentSeconds;
            DesktopVadThreshold = desktopVadRoute.Threshold;

            VadMinSilenceDuration = _config.VadMinSilenceDuration;
            VadMaxSegmentSeconds = _config.VadMaxSegmentSeconds;
            VadThreshold = _config.VadThreshold;
            LocalStreamingEndpointSilenceSeconds = _config.LocalStreamingEndpointSilenceSeconds;

            // 翻译设置
            SourceLanguage = _config.SourceLanguage;
            MicSourceLanguage = _config.ResolveSourceLanguage("Mic");
            DesktopSourceLanguage = _config.ResolveSourceLanguage("Desktop");
            MicTargetTranslationLanguage = _config.ResolvePrimaryTargetLanguage("Mic");
            MicTargetTranslationLanguage2 = _config.ResolveSecondaryTargetLanguage("Mic");
            MicTargetTranslationLanguage3 = _config.ResolveTertiaryTargetLanguage("Mic");
            DesktopTargetTranslationLanguage = _config.ResolvePrimaryTargetLanguage("Desktop");
            DesktopTargetTranslationLanguage2 = _config.ResolveSecondaryTargetLanguage("Desktop");
            DesktopTargetTranslationLanguage3 = _config.ResolveTertiaryTargetLanguage("Desktop");
            TextSendTargetTranslationLanguage = _config.ResolvePrimaryTargetLanguage("TextSend");
            TextSendTargetTranslationLanguage2 = _config.ResolveSecondaryTargetLanguage("TextSend");
            TextSendTargetTranslationLanguage3 = _config.ResolveTertiaryTargetLanguage("TextSend");
            MicTranslationServer = _config.ResolveTranslationEngine("Mic");
            DesktopTranslationServer = _config.ResolveTranslationEngine("Desktop");
            TranslatorsDefaultRegion = string.IsNullOrWhiteSpace(_config.TranslatorsDefaultRegion)
                ? "CN"
                : _config.TranslatorsDefaultRegion.Trim().ToUpperInvariant();
            FilterEmoji = _config.FilterEmoji == "true";
            MainWindowLanguageMask = NormalizeMainWindowLanguageMask(_config.MainWindowLanguageMask);
            CopyLanguageMask = NormalizeCopyLanguageMask(_config.CopyLanguageMask);
            RefreshTranslationEngineOptions();


            // OSC设置
            OscIp = _config.OscIp;
            OscPort = _config.OscPort;
            VrcChatboxFormatNew = _config.VrcChatboxFormatNew;
            OscChatboxNotificationSfx = _config.OscChatboxNotificationSfx;
            EnableOscServer = _config.EnableOscServer;
            OscServerIp = _config.OscServerIp;
            OscServerPort = _config.OscServerPort;
            OscShutdown = _config.OscShutdown;

            // VRChat集成
            TextInSteamVr = _config.TextInSteamVr;
            SteamVrHand = _config.SteamVrHand;
            SteamVrSize = _config.SteamVrSize;
            SteamVrAnchorMode = _config.SteamVrAnchorMode;
            var steamVrOffset = _config.GetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue);
            var steamVrRotation = _config.GetSteamVrAnchorRotation(_config.SteamVrAnchorModeValue);
            SteamVrOffsetX = steamVrOffset.X;
            SteamVrOffsetY = steamVrOffset.Y;
            SteamVrOffsetZ = steamVrOffset.Z;
            SteamVrRotationX = steamVrRotation.X;
            SteamVrRotationY = steamVrRotation.Y;
            SteamVrRotationZ = steamVrRotation.Z;
            SteamVrAutoLaunch = _config.SteamVrAutoLaunch;
            SteamVrNonTextOpacity = _config.SteamVrNonTextOpacity;
            SteamVrStartupStatus = _steamVrService.StartupRegistrationStatus;
            SteamVrCursorFlipU = _config.SteamVrCursorFlipU;
            SteamVrCursorFlipV = _config.SteamVrCursorFlipV;
            VrcBitmapLedColor = _config.VrcBitmapLedColor;
            VrcBitmapLedRow = _config.VrcBitmapLedRow;
            VrcBitmapLedCol = _config.VrcBitmapLedCol;

            // TTS设置
            EnableTts = _config.TtsToggle != 0;
            TtsMode = _config.TtsToggle;
            TtsEngine = _config.TtsEngine;
            TtsProvider = _config.TtsProvider;
            TtsOutputName = _config.TtsOutputName;
            TtsVoiceName = _config.TtsVoiceName;
            TtsSpeed = _config.TtsSpeed;
            QwenTtsToneMode = _config.QwenTtsToneMode;
            QwenTtsInstruct = string.IsNullOrWhiteSpace(_config.QwenTtsInstruct)
                ? AppConfig.DefaultQwenTtsInstruct
                : _config.QwenTtsInstruct;
            TtsInterruptMode = _config.TtsInterruptMode;
            MicTtsSpeakMask = _config.MicTtsSpeakMask;
            DesktopTtsSpeakMask = _config.DesktopTtsSpeakMask;
            LocalizedSpeech = _config.LocalizedSpeech;
            LocalizedCapture = _config.LocalizedCapture;
            OpenAiApiKey = _config.OpenAiConfig.ApiKey;
            OpenAiBaseUrl = _config.OpenAiConfig.BaseUrl;
            OpenAiModel = _config.OpenAiConfig.Model;
            OpenAiTemperature = _config.OpenAiConfig.Temperature;
            OpenAiMaxTokens = _config.OpenAiConfig.MaxTokens;
            OpenAiSystemPrompt = _config.OpenAiConfig.SystemPrompt;
            OpenAiExtraBody = _config.OpenAiConfig.ExtraBody;
            RefreshOpenAiPresetNames();
            SelectedOpenAiPreset = _config.SelectedOpenAiPreset;

            // 外观设置
            UiLanguage = _config.UiLanguage;
            OverlayWidth = _config.OverlayWidth;
            OverlayHeight = _config.OverlayHeight;
            CopyBox = _config.CopyBox;

            // 高级设置
            ChatboxOscMixTemplate = _config.ChatboxOscMixTemplate;

            _logger.Information("SettingsViewModel: 配置数据加载完成，共加载 {PropertyCount} 个属性", typeof(SettingsViewModel).GetProperties().Length);
            }
            finally
            {
                _isLoadingConfig = false;
            }
        }

        private void SyncConnectionCredentialsFromConfig()
        {
            var previousIsLoadingConfig = _isLoadingConfig;
            _isLoadingConfig = true;
            try
            {
                Username = _config.UserInfo.Username;
                Password = _config.UserInfo.Password;
            }
            finally
            {
                _isLoadingConfig = previousIsLoadingConfig;
            }

            DonationUsername = ShouldSeedDonationCredentials(_config.UserInfo) ? Username : string.Empty;
            DonationPassword = ShouldSeedDonationCredentials(_config.UserInfo) ? Password : string.Empty;
            PasswordUpdated?.Invoke(Password);
        }

        public void ReloadFromConfig()
        {
            LoadFromConfig();
        }

        // 识别设置
        public bool UseLocalRecognition
        {
            get => _useLocalRecognition;
            set
            {
                if (value == _useLocalRecognition)
                {
                    return;
                }

                if (_isLoadingConfig)
                {
                    ApplyUseLocalRecognitionValue(value);
                    return;
                }

                if (!value)
                {
                    ApplyUseLocalRecognitionValue(false);
                    return;
                }

                _ = EnsureLocalRecognitionModelsAndEnableAsync();
            }
        }

        public string AppVersion
        {
            get => _appVersion;
            private set => SetField(ref _appVersion, value);
        }

        public string UpdateStatusText
        {
            get => _updateStatusText;
            private set => SetField(ref _updateStatusText, value);
        }

        public string AppUpdateActionText
        {
            get => _appUpdateActionText;
            private set => SetField(ref _appUpdateActionText, value);
        }

        public double UpdateProgress
        {
            get => _updateProgress;
            private set => SetField(ref _updateProgress, value);
        }

        public string ModelUpdateStatusText
        {
            get => _modelUpdateStatusText;
            private set => SetField(ref _modelUpdateStatusText, value);
        }

        public double ModelDownloadProgress
        {
            get => _modelDownloadProgress;
            private set => SetField(ref _modelDownloadProgress, value);
        }

        public string TranslationProfileVersion
        {
            get => _translationProfileVersion;
            private set => SetField(ref _translationProfileVersion, value);
        }

        public string TranslationUpdateStatusText
        {
            get => _translationUpdateStatusText;
            private set => SetField(ref _translationUpdateStatusText, value);
        }

        public double TranslationUpdateProgress
        {
            get => _translationUpdateProgress;
            private set => SetField(ref _translationUpdateProgress, value);
        }

        public string LocalModelStatusText
        {
            get => _localModelStatusText;
            private set => SetField(ref _localModelStatusText, value);
        }

        public System.Windows.Media.Brush LocalModelStatusColor
        {
            get
            {
                if (_localModelStatusText.Contains("ready") || _localModelStatusText.Contains("available"))
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                if (_localModelStatusText.Contains("not found") || _localModelStatusText.Contains("missing"))
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54));
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158));
            }
        }

        private void UpdateLocalModelStatus()
        {
            try
            {
                var resolver = App.GetRequiredService<IModelPathResolver>();
                var missingModels = new List<string>();
                if (string.IsNullOrWhiteSpace(resolver.ResolveVadModelPath()))
                {
                    missingModels.Add("VAD");
                }

                var recognitionModelName = GetCurrentLocalRecognitionModelDisplayName();
                var recognitionModelInstalled = _config.LocalRecognitionModelValue == LocalRecognitionModel.SenseVoice
                    ? !string.IsNullOrWhiteSpace(ResolveSenseVoiceModelDir())
                    : !string.IsNullOrWhiteSpace(resolver.ResolveSherpaModelDir());
                if (!recognitionModelInstalled)
                {
                    missingModels.Add(recognitionModelName);
                }

                LocalModelStatusText = missingModels.Count == 0
                    ? $"Local recognition models: ready ({recognitionModelName} + VAD)"
                    : $"Local recognition models missing: {string.Join(", ", missingModels)}";
                OnPropertyChanged(nameof(LocalModelStatusColor));
            }
            catch (Exception ex)
            {
                LocalModelStatusText = $"检查模型状态失败: {ex.Message}";
                OnPropertyChanged(nameof(LocalModelStatusColor));
            }
        }

        public bool ControlEnabled
        {
            get => _controlEnabled;
            set
            {
                if (SetField(ref _controlEnabled, value))
                {
                    _config.ControlEnabled = value;
                    if (!_isLoadingConfig)
                    {
                        ControlSettingsChanged?.Invoke(this, new ControlSettingsChangedEventArgs
                        {
                            ChangeType = ControlSettingsChangeType.EnabledChanged,
                            IsEnabled = value
                        });
                    }
                }
            }
        }

        public bool CommandFeedbackEnabled
        {
            get => _commandFeedbackEnabled;
            set
            {
                if (SetField(ref _commandFeedbackEnabled, value))
                {
                    _config.CommandFeedbackEnabled = value;
                }
            }
        }

        public bool ControlAutoStart
        {
            get => _controlAutoStart;
            set
            {
                if (SetField(ref _controlAutoStart, value))
                {
                    _config.ControlAutoStart = value;
                }
            }
        }

        public int ControlMicSourceMode
        {
            get => _controlMicSourceMode;
            set
            {
                if (SetField(ref _controlMicSourceMode, value))
                {
                    _config.ControlMicSourceMode = value;
                    OnPropertyChanged(nameof(ShowControlMicDeviceSelector));
                    if (!_isLoadingConfig && ControlEnabled)
                    {
                        ControlSettingsChanged?.Invoke(this, new ControlSettingsChangedEventArgs
                        {
                            ChangeType = ControlSettingsChangeType.MicSourceChanged,
                            NewMicDevice = GetEffectiveControlMicDevice()
                        });
                    }
                }
            }
        }

        // Whether to show the independent mic device selector
        public bool ShowControlMicDeviceSelector => ControlMicSourceMode == 1;

        public string ControlMicName
        {
            get => _controlMicName;
            set
            {
                if (SetField(ref _controlMicName, value))
                {
                    _config.ControlMicName = value == MainViewModel.DefaultDeviceDisplayName
                        ? MainViewModel.DefaultDeviceConfigValue
                        : value;
                    if (!_isLoadingConfig && ControlEnabled && ControlMicSourceMode == 1)
                    {
                        ControlSettingsChanged?.Invoke(this, new ControlSettingsChangedEventArgs
                        {
                            ChangeType = ControlSettingsChangeType.MicSourceChanged,
                            NewMicDevice = GetEffectiveControlMicDevice()
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Get the effective mic device for control recognition based on current settings.
        /// </summary>
        public string GetEffectiveControlMicDevice()
        {
            if (ControlMicSourceMode == 0)
            {
                // Same as main mic
                var mainMic = _config.MicName;
                return string.IsNullOrWhiteSpace(mainMic) || mainMic == MainViewModel.DefaultDeviceConfigValue
                    ? MainViewModel.DefaultDeviceConfigValue
                    : mainMic;
            }
            else
            {
                // Independent device
                var controlMic = _config.ControlMicName;
                return string.IsNullOrWhiteSpace(controlMic) || controlMic == MainViewModel.DefaultDeviceConfigValue
                    ? MainViewModel.DefaultDeviceConfigValue
                    : controlMic;
            }
        }

        public bool ControlRequireWakeForCommands
        {
            get => _controlRequireWakeForCommands;
            set
            {
                if (SetField(ref _controlRequireWakeForCommands, value))
                {
                    _config.ControlRequireWakeForCommands = value;
                }
            }
        }

        public double ControlWakeWindowSeconds
        {
            get => _controlWakeWindowSeconds;
            set
            {
                if (SetField(ref _controlWakeWindowSeconds, value))
                {
                    _config.ControlWakeWindowSeconds = (float)value;
                }
            }
        }

        // 服务器与鉴权
        public string Username
        {
            get => _username;
            set
            {
                var previous = _username;
                if (SetField(ref _username, value))
                {
                    _config.UserInfo.Username = value;
                    if (!IsCurrentLoginTestAccount
                        && (string.IsNullOrWhiteSpace(DonationUsername) || string.Equals(DonationUsername, previous, StringComparison.Ordinal)))
                    {
                        DonationUsername = value;
                    }
                    OnPropertyChanged(nameof(DisplayUsername));
                    OnPropertyChanged(nameof(UsernameAliasSummary));
                    if (!_isLoadingConfig)
                    {
                        _authService.ResetForCredentialChange(UseLegacyBackendApi);
                    }
                }
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                var previous = _password;
                if (SetField(ref _password, value))
                {
                    _config.UserInfo.Password = value;
                    if (!IsCurrentLoginTestAccount
                        && (string.IsNullOrWhiteSpace(DonationPassword) || string.Equals(DonationPassword, previous, StringComparison.Ordinal)))
                    {
                        DonationPassword = value;
                    }
                    if (!_isLoadingConfig)
                    {
                        _authService.ResetForCredentialChange(UseLegacyBackendApi);
                    }
                }
            }
        }

        private void ApplyUseLocalRecognitionValue(bool value)
        {
            if (SetField(ref _useLocalRecognition, value))
            {
                _config.RecognitionMode = value ? "local" : "remote";
                UpdateLocalModelStatus();
                return;
            }

            _config.RecognitionMode = value ? "local" : "remote";
            UpdateLocalModelStatus();
        }

        private async Task EnsureLocalRecognitionModelsAndEnableAsync()
        {
            if (_isChangingLocalRecognition)
            {
                OnPropertyChanged(nameof(UseLocalRecognition));
                return;
            }

            _isChangingLocalRecognition = true;
            try
            {
                var missingModels = await _modelUpdateService.GetMissingModelsAsync(GetLocalRecognitionRequiredModelTypes());
                if (missingModels.Count > 0)
                {
                    var modelList = FormatModelPackageList(missingModels);
                    var choice = ShowOwnedMessageBox(
                        $"启用本地识别前需要先下载以下模型：{modelList}。\n\n是否现在打开“更新”页进行下载？",
                        "本地识别模型缺失",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (choice == MessageBoxResult.Yes)
                    {
                        await NavigateToUpdateTabAsync(refreshModelCatalog: true);
                    }

                    ModelUpdateStatusText = $"Local recognition was not enabled because required models are missing: {modelList}";
                    ModelDownloadProgress = 0;
                    OnPropertyChanged(nameof(UseLocalRecognition));
                    UpdateLocalModelStatus();
                    return;
                }

                ApplyUseLocalRecognitionValue(true);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "EnsureLocalRecognitionModelsAndEnableAsync failed.");
                ModelUpdateStatusText = $"Failed to prepare local recognition models: {ex.Message}";
                OnPropertyChanged(nameof(UseLocalRecognition));
                UpdateLocalModelStatus();
            }
            finally
            {
                _isChangingLocalRecognition = false;
            }
        }

        public async Task NavigateToUpdateTabAsync(bool refreshModelCatalog = false)
        {
            SelectedSettingsTabIndex = MainViewModel.UpdateSettingsTabIndex;
            if (refreshModelCatalog)
            {
                await RefreshModelCatalogAsync();
            }
        }

        private static MessageBoxResult ShowOwnedMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            var owner = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive)
                ?? Application.Current?.MainWindow;

            return owner == null
                ? MessageBox.Show(message, title, buttons, image)
                : MessageBox.Show(owner, message, title, buttons, image);
        }

        private async Task<bool> DownloadAndInstallModelsAsync(IReadOnlyList<ModelPackageInfo> models)
        {
            if (models == null || models.Count == 0)
            {
                return true;
            }

            ModelDownloadProgress = 0;
            for (var index = 0; index < models.Count; index++)
            {
                var model = models[index];
                if (model == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(model.DownloadUrl))
                {
                    ModelUpdateStatusText = $"Model package is missing from catalog: {GetModelDisplayName(model.Type)}";
                    return false;
                }

                ModelUpdateStatusText = models.Count == 1
                    ? $"Installing model: {model.Name}"
                    : $"Installing model {index + 1}/{models.Count}: {model.Name}";

                var currentIndex = index;
                var progress = new Progress<double>(value =>
                {
                    var baseProgress = currentIndex * 100d / models.Count;
                    var span = 100d / models.Count;
                    ModelDownloadProgress = Math.Min(100d, baseProgress + value * span / 100d);
                });

                var ok = await _modelUpdateService.DownloadAndInstallModelAsync(model, progress);
                if (!ok)
                {
                    ModelUpdateStatusText = $"Model install failed: {model.Name}";
                    return false;
                }
            }

            ModelDownloadProgress = 100;
            UpdateLocalModelStatus();
            return true;
        }

        private IReadOnlyList<string> GetLocalRecognitionRequiredModelTypes()
        {
            var modelType = _config.LocalRecognitionModelValue == LocalRecognitionModel.SenseVoice
                ? "sensevoice"
                : "sherpa";

            return new[] { "vad", modelType };
        }

        private string GetCurrentLocalRecognitionModelDisplayName()
        {
            return _config.LocalRecognitionModelValue == LocalRecognitionModel.SenseVoice
                ? "SenseVoice"
                : "Sherpa";
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

        private static string FormatModelPackageList(IEnumerable<ModelPackageInfo> models)
        {
            return string.Join(", ",
                models
                    .Where(model => model != null)
                    .Select(model => GetModelDisplayName(model.Type))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private string? ResolveSenseVoiceModelDir()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.SenseVoiceModelDir))
                {
                    var configured = Path.Combine(_config.SenseVoiceModelDir, "model.onnx");
                    if (File.Exists(configured))
                    {
                        return _config.SenseVoiceModelDir;
                    }
                }

                foreach (var root in EnumerateModelRoots())
                {
                    var defaultDir = Path.Combine(root, "sensevoice-small");
                    if (File.Exists(Path.Combine(defaultDir, "model.onnx")))
                    {
                        return defaultDir;
                    }

                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    var modelPath = Directory.EnumerateFiles(root, "model.onnx", SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(modelPath))
                    {
                        return Path.GetDirectoryName(modelPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "ResolveSenseVoiceModelDir failed.");
            }

            return null;
        }

        private IEnumerable<string> EnumerateModelRoots()
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(_config.PreModelsDir))
            {
                try
                {
                    roots.Add(Path.GetFullPath(_config.PreModelsDir));
                }
                catch
                {
                }
            }

            roots.Add(Path.Combine(AppContext.BaseDirectory, "pre-models"));
            return roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public string DisplayUsername
        {
            get
            {
                var userInfo = _config.UserInfo;
                if (_authService.State.IsAnonymousMode || userInfo.IsAnonymousTest)
                {
                    return GetUiString("UserCenterAnonymousDisplayName", "匿名试用模式");
                }

                if (_authService.State.IsLegacyCredentials || userInfo.IsLegacyCredentials)
                {
                    if (!string.IsNullOrWhiteSpace(userInfo.Username))
                    {
                        return userInfo.Username;
                    }

                    return GetUiString("UserCenterLegacyDisplayName", "旧版后端账户");
                }

                if (!string.IsNullOrWhiteSpace(userInfo.UserUuid))
                {
                    var value = userInfo.UserUuid.Trim();
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        GetUiString("UserCenterUuidDisplayName", "用户 {0}"),
                        value.Length <= 8 ? value : value[..8]);
                }

                if (!string.IsNullOrWhiteSpace(userInfo.Username))
                {
                    return userInfo.Username;
                }

                return GetUiString("UserCenterNotLoggedInUser", "未登录账户");
            }
        }

        public string AuthModeSummary => _authService.State.AuthMode switch
        {
            BackendAuthMode.AnonymousTest => GetUiString("AuthModeAnonymous", "匿名试用模式"),
            BackendAuthMode.WebsiteAuthorized => GetUiString("AuthModeWebsiteAuthorized", "官网授权登录"),
            BackendAuthMode.LegacyCredentials => GetUiString("AuthModeLegacyCredentials", "旧版账号登录"),
            _ => GetUiString("AuthModeNotLoggedIn", "未登录")
        };

        public bool IsWebsiteAuthorizedSession =>
            _authService.State.IsWebsiteAuthorized || _config.UserInfo.IsWebsiteAuthorized;

        public bool IsWebsiteAuthorizationInProgress =>
            !UseLegacyBackendApi
            && HasWebsiteAuthorizationPrompt(_authService.State);

        public string UsernameAliasSummary
        {
            get
            {
                if (_authService.State.IsAnonymousMode || _config.UserInfo.IsAnonymousTest)
                {
                    return GetUiString("UserCenterAnonymousAlias", "匿名试用无固定用户名");
                }

                if (_authService.State.IsLegacyCredentials || _config.UserInfo.IsLegacyCredentials)
                {
                    return string.IsNullOrWhiteSpace(_config.UserInfo.Username)
                        ? "-"
                        : _config.UserInfo.Username;
                }

                return string.IsNullOrWhiteSpace(_config.UserInfo.Username)
                    ? "-"
                    : _config.UserInfo.Username;
            }
        }

        public string UserUuidSummary
        {
            get
            {
                if (_authService.State.IsAnonymousMode || _config.UserInfo.IsAnonymousTest)
                {
                    return GetUiString("UserCenterAnonymousUuid", "匿名试用不使用正式 user_uuid");
                }

                if (_authService.State.IsLegacyCredentials || _config.UserInfo.IsLegacyCredentials)
                {
                    return GetUiString("UserCenterLegacyUuid", "旧版后端不提供 user_uuid");
                }

                return string.IsNullOrWhiteSpace(_config.UserInfo.UserUuid)
                    ? "-"
                    : _config.UserInfo.UserUuid;
            }
        }

        public string EmailSummary
        {
            get
            {
                if (_authService.State.IsAnonymousMode || _config.UserInfo.IsAnonymousTest)
                {
                    return GetUiString("UserCenterAnonymousEmail", "匿名试用无邮箱");
                }

                if (_authService.State.IsLegacyCredentials || _config.UserInfo.IsLegacyCredentials)
                {
                    return GetUiString("UserCenterLegacyEmail", "旧版后端不提供邮箱");
                }

                var email = string.IsNullOrWhiteSpace(_config.UserInfo.Email) ? "-" : _config.UserInfo.Email;
                if (email == "-")
                {
                    return email;
                }

                return _config.UserInfo.EmailVerified
                    ? string.Format(CultureInfo.CurrentCulture, GetUiString("EmailVerifiedSuffix", "{0} (已验证)"), email)
                    : string.Format(CultureInfo.CurrentCulture, GetUiString("EmailUnverifiedSuffix", "{0} (未验证)"), email);
            }
        }

        public string WebsiteAuthorizationUserCode
        {
            get => _websiteAuthorizationUserCode;
            set => SetField(ref _websiteAuthorizationUserCode, value);
        }

        public string WebsiteAuthorizationUrl
        {
            get => _websiteAuthorizationUrl;
            set => SetField(ref _websiteAuthorizationUrl, value);
        }

        public string WebsiteAuthorizationStatus
        {
            get => _websiteAuthorizationStatus;
            set => SetField(ref _websiteAuthorizationStatus, value);
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                if (SetField(ref _baseUrl, value))
                {
                    _config.BaseUrl = value;
                    if (!_isLoadingConfig)
                    {
                        WebsiteAuthorizationStatus = string.Empty;
                        WebsiteAuthorizationUserCode = string.Empty;
                        WebsiteAuthorizationUrl = string.Empty;
                        ResetAccountUsageOverview();
                        ResetAccountUsageRange();
                        _authService.ResetForBaseUrlChange(UseLegacyBackendApi);
                    }
                }
            }
        }

        public bool UseLegacyBackendApi
        {
            get => _useLegacyBackendApi;
            set
            {
                if (!SetField(ref _useLegacyBackendApi, value))
                {
                    return;
                }

                _config.UseLegacyBackendApi = value;
                WebsiteAuthorizationStatus = string.Empty;
                WebsiteAuthorizationUserCode = string.Empty;
                WebsiteAuthorizationUrl = string.Empty;
                ResetAccountUsageOverview();
                ResetAccountUsageRange();
                RefreshAuthDisplayBindings();

                if (!_isLoadingConfig)
                {
                    _authService.ResetForBackendModeSwitch(value);
                    SyncConnectionCredentialsFromConfig();
                }
            }
        }

        public bool IsBaseUrlLocked
        {
            get => _isBaseUrlLocked;
            set => SetField(ref _isBaseUrlLocked, value);
        }

        public bool IsModernBackendApi => !UseLegacyBackendApi;

        public bool ShouldShowLegacyMigrationGuide =>
            UseLegacyBackendApi
            || _authService.State.IsLegacyCredentials
            || _config.UserInfo.IsLegacyCredentials
            || !IsWebsiteAuthorizedSession
            || !_config.UserInfo.EmailVerified;

        public string ConnectionSectionTitle => UseLegacyBackendApi
            ? GetUiString("UserCenterConnectionSectionTitleLegacy", "旧版后端连接")
            : GetUiString("UserCenterConnectionSectionTitleModern", "官网授权与连接");

        public string ConnectionSectionDescription => UseLegacyBackendApi
            ? GetUiString("UserCenterConnectionSectionDescriptionLegacy", "兼容旧版后端时，使用用户名和密码直接调用你填写的 Base URL 下的 login 接口获取 token。")
            : GetUiString("UserCenterConnectionSectionDescriptionModern", "客户端不会接收或保存官网密码。点击登录后会在浏览器中完成授权并返回 Token。");

        public string ConnectionEntryHint => UseLegacyBackendApi
            ? string.Empty
            : IsWebsiteAuthorizationInProgress
                ? GetUiString("UserCenterConnectionEntryHintPending", "点击这里重新打开认证网页。")
                : _authService.State.Status == BackendAuthStatus.LoggingIn
                    ? string.Empty
                    : IsWebsiteAuthorizedSession
                        ? GetUiString("UserCenterConnectionEntryHintAccountCenter", "点击这里打开账户中心。")
                        : GetUiString("UserCenterConnectionEntryHintLogin", "点击这里开始官网授权登录。");

        public string BaseUrlDescription => UseLegacyBackendApi
            ? GetUiString("UserCenterBaseUrlDescriptionLegacy", "旧版后端模式下请填写旧版接口前缀地址，客户端不会自动改写为 /api；末尾斜杠可自动补齐。")
            : GetUiString("UserCenterBaseUrlDescriptionModern", "官网授权与账户查询都会基于此地址进行。");

        public string BaseUrlHint => UseLegacyBackendApi
            ? GetUiString("UserCenterBaseUrlHintLegacy", "https://example.com/api")
            : GetUiString("UserCenterBaseUrlHintModern", "Base URL");

        public string LoginButtonText => UseLegacyBackendApi
            ? GetUiString("UserCenterLoginButtonLegacy", "旧版登录")
            : IsWebsiteAuthorizationInProgress
                ? GetUiString("UserCenterOpenAuthorizationButton", "打开认证网页")
                : IsWebsiteAuthorizedSession
                    ? GetUiString("UserCenterLoginButtonModern", "重新登录")
                    : GetUiString("UserCenterLoginButtonModernInitial", "官网授权登录");

        public string ResetLoginButtonText => UseLegacyBackendApi
            ? GetUiString("UserCenterResetLoginButtonLegacy", "清除登录")
            : GetUiString("UserCenterResetLoginButtonModern", "匿名模式");

        public string ApiIp
        {
            get => _apiIp;
            set
            {
                if (SetField(ref _apiIp, value))
                {
                    _config.ApiIp = value;
                }
            }
        }

        public int ApiPort
        {
            get => _apiPort;
            set
            {
                if (SetField(ref _apiPort, value))
                {
                    _config.ApiPort = value;
                }
            }
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set
            {
                if (SetField(ref _connectionStatus, value))
                {
                    OnPropertyChanged(nameof(ConnectionBadgeStatus));
                }
            }
        }

        public string ConnectionBadgeStatus
        {
            get
            {
                if (_authService.State.Status == BackendAuthStatus.Failed
                    || (_authService.State.Status == BackendAuthStatus.NotLoggedIn
                        && !string.IsNullOrWhiteSpace(_authService.State.ErrorMessage)))
                {
                    return GetUiString("AuthStatusConnectionFailed", "连接失败");
                }

                return ConnectionStatus;
            }
        }

        public int SelectedSettingsTabIndex
        {
            get => _selectedSettingsTabIndex;
            set => SetField(ref _selectedSettingsTabIndex, Math.Max(0, value));
        }

        public string DonationUsername
        {
            get => _donationUsername;
            set
            {
                if (SetField(ref _donationUsername, value))
                {
                    if (!_isLoadingConfig)
                    {
                        ResetPaymentGuide(clearStatus: false);
                        PaymentGuideStatus = GetUiString("UserCenterGuideReloadRequired", "请先读取当前方案后再生成发电链接。");
                    }
                    ResetDonationVerificationState(clearStatusText: true);
                    CommandManager.InvalidateRequerySuggested();
                    (IncreaseDonationPriceCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (DecreaseDonationPriceCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string DonationPassword
        {
            get => _donationPassword;
            set
            {
                if (SetField(ref _donationPassword, value))
                {
                    if (!_isLoadingConfig)
                    {
                        ResetPaymentGuide(clearStatus: false);
                        PaymentGuideStatus = GetUiString("UserCenterGuideReloadRequired", "请先读取当前方案后再生成发电链接。");
                    }
                    ResetDonationVerificationState(clearStatusText: true);
                    CommandManager.InvalidateRequerySuggested();
                    (IncreaseDonationPriceCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (DecreaseDonationPriceCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string DonationAction
        {
            get => _donationAction;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "renew" : value.Trim().ToLowerInvariant();
                if (SetField(ref _donationAction, normalized))
                {
                    ClearPendingDonationRequest(showStaleMessage: true, clearStatusText: false);
                    ResetDonationVerificationState(clearStatusText: true);
                    OnPropertyChanged(nameof(IsRenewAction));
                    SyncSelectedPlanTypeFromDonationState();
                    ApplyCurrentGuideOption();
                }
            }
        }

        public string SelectedPlanType
        {
            get => _selectedPlanType;
            set
            {
                var normalized = NormalizePlanType(value);
                if (SetField(ref _selectedPlanType, normalized))
                {
                    OnSelectedPlanTypeChanged(normalized);
                }
            }
        }

        public bool IsBasicPlanSelected
        {
            get => string.Equals(SelectedPlanType, "basic", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedPlanType = "basic";
                }
            }
        }

        public bool IsUpgradePlanSelected
        {
            get => string.Equals(SelectedPlanType, "upgrade", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedPlanType = "upgrade";
                }
            }
        }

        public bool IsCustomPlanSelected
        {
            get => string.Equals(SelectedPlanType, "custom", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedPlanType = "custom";
                }
            }
        }

        public bool IsRenewAction => string.Equals(DonationAction, "renew", StringComparison.OrdinalIgnoreCase);

        public int DonationMonths
        {
            get => _donationMonths;
            set
            {
                var normalized = Math.Clamp(value, 1, 12);
                if (SetField(ref _donationMonths, normalized))
                {
                    ClearPendingDonationRequest(showStaleMessage: true, clearStatusText: false);
                    ResetDonationVerificationState(clearStatusText: true);
                    OnPropertyChanged(nameof(DonationTotalPrice));
                    OnPropertyChanged(nameof(PendingDonationTotalPrice));
                }
            }
        }

        public string DonationPrice
        {
            get => _donationPrice;
            set
            {
                if (SetField(ref _donationPrice, value))
                {
                    ClearPendingDonationRequest(showStaleMessage: true, clearStatusText: false);
                    ResetDonationVerificationState(clearStatusText: true);
                    SyncSelectedPlanTypeFromDonationState();
                    OnPropertyChanged(nameof(DonationTotalPrice));
                    OnPropertyChanged(nameof(DonationPriceGuideText));
                    OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
                }
            }
        }

        public decimal DonationTotalPrice => Math.Round(Math.Max(0m, ParseDonationPrice(DonationPrice)) * DonationMonths, 2);

        public string DonationStatus
        {
            get => _donationStatus;
            set => SetField(ref _donationStatus, value);
        }

        public int DonationWizardStepIndex
        {
            get => _donationWizardStepIndex;
            private set
            {
                if (SetField(ref _donationWizardStepIndex, Math.Clamp(value, 0, 3)))
                {
                    OnPropertyChanged(nameof(IsDonationWizardCredentialsStep));
                    OnPropertyChanged(nameof(IsDonationWizardPaymentStep));
                    OnPropertyChanged(nameof(IsDonationWizardConfirmStep));
                    OnPropertyChanged(nameof(IsDonationWizardVerificationStep));
                    OnPropertyChanged(nameof(DonationWizardNextButtonText));
                    OnPropertyChanged(nameof(CanCloseDonationWizard));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsDonationWizardCredentialsStep => DonationWizardStepIndex == 0;

        public bool IsDonationWizardPaymentStep => DonationWizardStepIndex == 1;

        public bool IsDonationWizardConfirmStep => DonationWizardStepIndex == 2;

        public bool IsDonationWizardVerificationStep => DonationWizardStepIndex == 3;

        public string DonationWizardNextButtonText => DonationWizardStepIndex switch
        {
            0 => GetUiString("下一步", "下一步"),
            1 => GetUiString("下一步", "下一步"),
            2 => GetUiString("UserCenterStartVerification", "开始验证"),
            _ => GetUiString("完成", "完成")
        };

        public bool CanCloseDonationWizard => !IsDonationVerificationRunning;

        public string PendingDonationOverview
        {
            get => _pendingDonationOverview;
            private set => SetField(ref _pendingDonationOverview, value);
        }

        public string PendingDonationActionDisplayName => GetDonationActionDisplayName(_hasPendingDonationRequest ? _pendingDonationAction : DonationAction);

        public string PendingDonationMonthlyPriceDisplay
            => (_hasPendingDonationRequest ? _pendingDonationMonthlyPrice : ParseDonationPrice(DonationPrice)).ToString("F2", CultureInfo.InvariantCulture);

        public int PendingDonationMonthsDisplay => _hasPendingDonationRequest ? _pendingDonationMonths : DonationMonths;

        public decimal PendingDonationTotalPrice => Math.Round((_hasPendingDonationRequest ? _pendingDonationMonthlyPrice : ParseDonationPrice(DonationPrice)) * PendingDonationMonthsDisplay, 2);

        public string PendingDonationExpectedDailyQuotaDisplay
            => _pendingDonationExpectedDailyQuota.HasValue
                ? $"{_pendingDonationExpectedDailyQuota.Value}/day"
                : GetUiString("UserCenterVerificationPendingQuota", "等待服务端返回");

        public string PendingDonationMinimumExpirationDisplay
            => _pendingDonationMinimumExpirationUtc.HasValue
                ? _pendingDonationMinimumExpirationUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : GetUiString("UserCenterVerificationPendingExpiration", "等待服务端返回");

        public string DonationVerificationStatus
        {
            get => _donationVerificationStatus;
            private set => SetField(ref _donationVerificationStatus, value);
        }

        public bool IsDonationVerificationRunning
        {
            get => _isDonationVerificationRunning;
            private set
            {
                if (SetField(ref _isDonationVerificationRunning, value))
                {
                    OnPropertyChanged(nameof(CanCloseDonationWizard));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public int DonationVerificationAttempt
        {
            get => _donationVerificationAttempt;
            private set => SetField(ref _donationVerificationAttempt, Math.Clamp(value, 0, 3));
        }

        public bool DonationVerificationSucceeded
        {
            get => _donationVerificationSucceeded;
            private set => SetField(ref _donationVerificationSucceeded, value);
        }

        public bool IsCurrentLoginTestAccount
        {
            get => _isCurrentLoginTestAccount;
            private set => SetField(ref _isCurrentLoginTestAccount, value);
        }

        public bool NeedsFormalDonationCredentials
        {
            get => _needsFormalDonationCredentials;
            private set => SetField(ref _needsFormalDonationCredentials, value);
        }

        public bool IsPaymentGuideLoading
        {
            get => _isPaymentGuideLoading;
            private set
            {
                if (SetField(ref _isPaymentGuideLoading, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasPaymentGuide
        {
            get => _hasPaymentGuide;
            private set
            {
                if (SetField(ref _hasPaymentGuide, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                    OnPropertyChanged(nameof(GuideAccountStatusText));
                    OnPropertyChanged(nameof(IsDonationPriceReadOnly));
                    OnPropertyChanged(nameof(DonationPriceGuideText));
                    OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
                }
            }
        }

        public bool IsGuideAccountRegistered
        {
            get => _isGuideAccountRegistered;
            private set
            {
                if (SetField(ref _isGuideAccountRegistered, value))
                {
                    OnPropertyChanged(nameof(GuideAccountStatusText));
                    OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
                    ApplyCurrentGuideOption();
                }
            }
        }

        public string GuideAccountStatusText
        {
            get
            {
                if (!HasPaymentGuide)
                {
                    return string.Empty;
                }

                return IsGuideAccountRegistered
                    ? string.Empty
                    : GetUiString("UserCenterGuideUnregisteredHint", "首次开通：请在下方选择金额（5 元步进）。");
            }
        }

        public string PaymentGuideStatus
        {
            get => _paymentGuideStatus;
            private set => SetField(ref _paymentGuideStatus, value);
        }

        public string PaymentGuideToken
        {
            get => _paymentGuideToken;
            private set => SetField(ref _paymentGuideToken, value);
        }

        public string DisplayPlanName
        {
            get => _displayPlanName;
            private set => SetField(ref _displayPlanName, value);
        }

        public decimal DisplayMonthlyPrice
        {
            get => _displayMonthlyPrice;
            private set => SetField(ref _displayMonthlyPrice, value);
        }

        public string DisplayResourceSummary
        {
            get => _displayResourceSummary;
            private set => SetField(ref _displayResourceSummary, value);
        }

        public string DonationGuideExplanation
        {
            get => _donationGuideExplanation;
            private set => SetField(ref _donationGuideExplanation, value);
        }

        public int DonationPriceStep
        {
            get => _donationPriceStep;
            private set
            {
                if (SetField(ref _donationPriceStep, value))
                {
                    OnPropertyChanged(nameof(ResourceRuleSummary));
                    OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
                }
            }
        }

        public int ResourceIncrementPerStep
        {
            get => _resourceIncrementPerStep;
            private set
            {
                if (SetField(ref _resourceIncrementPerStep, value))
                {
                    OnPropertyChanged(nameof(ResourceRuleSummary));
                    OnPropertyChanged(nameof(SelectedDonationPriceOptionQuotaPreview));
                }
            }
        }

        public bool IsDonationPriceReadOnly
        {
            get
            {
                var option = GetCurrentDonationGuideOption();
                if (option == null)
                {
                    return false;
                }

                if (option.ReadOnly)
                {
                    return true;
                }

                if (option.MinMonthlyPrice.HasValue
                    && option.MaxMonthlyPrice.HasValue
                    && option.MinMonthlyPrice.Value == option.MaxMonthlyPrice.Value)
                {
                    return true;
                }

                return false;
            }
        }

        public string ResourceRuleSummary
            => ResourceIncrementPerStep > 0
                ? string.Format(CultureInfo.InvariantCulture, GetUiString("UserCenterGuideRuleSummary", "每 {0} 元固定对应 {1} 资源，具体档位由服务端计算。"), DonationPriceStep, ResourceIncrementPerStep)
                : GetUiString("UserCenterGuideRulePending", "资源规则以服务端返回为准。");

        public string DonationPriceGuideText
        {
            get
            {
                var option = GetCurrentDonationGuideOption();
                if (option == null)
                {
                    return GetUiString("UserCenterGuideReloadRequired", "请先读取当前方案后再生成发电链接。");
                }

                if (!IsGuideAccountRegistered && string.Equals(DonationAction, "renew", StringComparison.OrdinalIgnoreCase))
                {
                    return GetUiString("UserCenterDonationFirstPurchaseHint", "首次开通请自选月档位，金额按 5 元步进。");
                }

                var min = option.MinMonthlyPrice ?? option.DefaultMonthlyPrice ?? 0m;
                var max = option.MaxMonthlyPrice ?? option.DefaultMonthlyPrice ?? min;
                return min == max
                    ? string.Format(CultureInfo.InvariantCulture, GetUiString("UserCenterDonationPriceFixed", "当前操作固定为 ￥ {0:F2} / 月。"), min)
                    : string.Format(CultureInfo.InvariantCulture, GetUiString("UserCenterDonationPriceRange", "当前操作支持 ￥ {0:F2} - ￥ {1:F2} / 月，步进 {2} 元。"), min, max, Math.Max(1, option.Step ?? DonationPriceStep));
            }
        }

        public string SelectedDonationPriceOptionQuotaPreview
        {
            get
            {
                if (!HasPaymentGuide)
                {
                    return string.Empty;
                }

                var quota = TryCalculateDailyQuotaFromDonationPrice();
                if (!quota.HasValue)
                {
                    quota = GetCurrentDonationGuideQuotaPreview();
                }

                if (!quota.HasValue)
                {
                    return string.Empty;
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    GetUiString("UserCenterGuideTargetQuotaPreview", "本次操作后预计资源：{0}/day"),
                    quota.Value);
            }
        }

        private int? TryCalculateDailyQuotaFromDonationPrice()
        {
            var monthlyPrice = ParseDonationPrice(DonationPrice);
            return TryCalculateDailyQuotaFromMonthlyPrice(monthlyPrice);
        }

        private int? TryCalculateDailyQuotaFromMonthlyPrice(decimal monthlyPrice)
        {
            if (DonationPriceStep <= 0 || ResourceIncrementPerStep <= 0)
            {
                return null;
            }

            if (monthlyPrice <= 0m)
            {
                return null;
            }

            var units = (int)Math.Floor(monthlyPrice / DonationPriceStep);
            units = Math.Max(1, units);
            return units * ResourceIncrementPerStep;
        }

        public string DonationRemark
        {
            get => _donationRemark;
            set => SetField(ref _donationRemark, value);
        }

        public string DonationUrl
        {
            get => _donationUrl;
            set
            {
                if (SetField(ref _donationUrl, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsDonationSubmitting
        {
            get => _isDonationSubmitting;
            private set
            {
                if (SetField(ref _isDonationSubmitting, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ActivationMethod
        {
            get => _activationMethod;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "remark" : value.Trim().ToLowerInvariant();
                if (SetField(ref _activationMethod, normalized))
                {
                    OnPropertyChanged(nameof(ActivationInputHint));
                    OnPropertyChanged(nameof(IsActivationByRemark));
                    OnPropertyChanged(nameof(IsActivationByOrderNo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsActivationByRemark
        {
            get => string.Equals(ActivationMethod, "remark", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    ActivationMethod = "remark";
                }
            }
        }

        public bool IsActivationByOrderNo
        {
            get => string.Equals(ActivationMethod, "out_trade_no", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    ActivationMethod = "out_trade_no";
                }
            }
        }

        public string ActivationInput
        {
            get => _activationInput;
            set
            {
                if (SetField(ref _activationInput, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ActivationInputHint
            => ActivationMethod == "out_trade_no"
                ? GetUiString("商家订单号", "商家订单号")
                : GetUiString("订单备注", "订单备注");

        public string ActivationStatus
        {
            get => _activationStatus;
            set => SetField(ref _activationStatus, value);
        }

        public string AccountRemainingDailySummary
        {
            get => _accountRemainingDailySummary;
            set => SetField(ref _accountRemainingDailySummary, value);
        }

        public string AccountDailyLimitSummary
        {
            get => _accountDailyLimitSummary;
            set => SetField(ref _accountDailyLimitSummary, value);
        }

        public string AccountAddonBalanceSummary
        {
            get => _accountAddonBalanceSummary;
            set => SetField(ref _accountAddonBalanceSummary, value);
        }

        public bool IsActivationSubmitting
        {
            get => _isActivationSubmitting;
            private set
            {
                if (SetField(ref _isActivationSubmitting, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string AccountUsageStatus
        {
            get => _accountUsageStatus;
            set => SetField(ref _accountUsageStatus, value);
        }

        public string AccountUsageSummary
        {
            get => _accountUsageSummary;
            set => SetField(ref _accountUsageSummary, value);
        }

        public string AccountUsageRangeSummary
        {
            get => _accountUsageRangeSummary;
            set => SetField(ref _accountUsageRangeSummary, value);
        }

        public DateTime? AccountUsageStartDate
        {
            get => _accountUsageStartDate;
            set
            {
                if (SetField(ref _accountUsageStartDate, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public DateTime? AccountUsageEndDate
        {
            get => _accountUsageEndDate;
            set
            {
                if (SetField(ref _accountUsageEndDate, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string AccountExpirationSummary
        {
            get => _accountExpirationSummary;
            set => SetField(ref _accountExpirationSummary, value);
        }

        public bool IsAccountUsageSubmitting
        {
            get => _isAccountUsageSubmitting;
            private set
            {
                if (SetField(ref _isAccountUsageSubmitting, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // 音频设置 - 麦克风
        public string MicName
        {
            get => _micName;
            set
            {
                if (EqualityComparer<string>.Default.Equals(_micName, value))
                {
                    return;
                }

                _micName = value;

                // Persist selected display name back to config value.
                if (!_isLoadingConfig)
                {
                    _config.MicName = value == MainViewModel.DefaultDeviceDisplayName
                        ? MainViewModel.DefaultDeviceConfigValue
                        : value;

                    PersistConfig();
                }

                OnPropertyChanged();
            }
        }

        public int VoiceMode
        {
            get => _voiceMode;
            set
            {
                var normalizedValue = NormalizeVoiceMode(value);
                if (_voiceMode == normalizedValue)
                {
                    return;
                }

                _voiceMode = normalizedValue;
                _config.VoiceMode = normalizedValue;

                OnPropertyChanged();
                // Notify dependent visibility bindings.
                OnPropertyChanged(nameof(ShowMicHotKey));
                OnPropertyChanged(nameof(ShowMicPressKey));
            }
        }

        public bool AutoStartMic
        {
            get => _autoStartMic;
            set
            {
                if (SetField(ref _autoStartMic, value))
                {
                    _config.AutoStartMic = value;
                }
            }
        }

        public bool ShowMicHotKey => VoiceMode == 1;

        // 是否显示麦克风按住说话输入框 (按住说话模式)
        public bool ShowMicPressKey => VoiceMode == 2;

        private static int NormalizeVoiceMode(int value) => value switch
        {
            1 => 1,
            2 => 2,
            _ => 0
        };

        public bool DynamicThreshold
        {
            get => _dynamicThreshold;
            set
            {
                if (SetField(ref _dynamicThreshold, value))
                {
                    _config.DynamicThreshold = value;
                }
            }
        }

        public double CustomThreshold
        {
            get => _customThreshold;
            set
            {
                if (SetField(ref _customThreshold, value))
                {
                    _config.CustomThreshold = (float)value;
                }
            }
        }

        public string VoiceHotKey
        {
            get => _voiceHotKey;
            set
            {
                if (EqualityComparer<string>.Default.Equals(_voiceHotKey, value))
                {
                    return;
                }

                _voiceHotKey = value;
                _config.VoiceHotKey = value;
                OnPropertyChanged();
            }
        }

        public string MicPressingKey
        {
            get => _micPressingKey;
            set
            {
                if (EqualityComparer<string>.Default.Equals(_micPressingKey, value))
                {
                    return;
                }

                _micPressingKey = value;
                _config.MicPressingKey = value;
                OnPropertyChanged();
            }
        }

        public string GameMicName
        {
            get => _gameMicName;
            set
            {
                if (EqualityComparer<string>.Default.Equals(_gameMicName, value))
                {
                    return;
                }

                _gameMicName = value;
                _config.GameMicName = value == MainViewModel.DefaultDeviceDisplayName
                    ? MainViewModel.DefaultDeviceConfigValue
                    : value;
                OnPropertyChanged();
            }
        }

        public int DesktopAudioSourceMode
        {
            get => _desktopAudioSourceMode;
            set
            {
                var requestedValue = value;
                if (requestedValue == (int)VRCLS.Core.Models.DesktopAudioSourceMode.Application
                    && !IsApplicationAudioCaptureSupported)
                {
                    requestedValue = (int)VRCLS.Core.Models.DesktopAudioSourceMode.System;

                    if (!_isLoadingConfig)
                    {
                        MessageBox.Show(
                            ApplicationAudioSupportStatusText,
                            GetUiString("监听音频", "监听音频"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }

                if (_desktopAudioSourceMode == requestedValue)
                {
                    if (requestedValue != value)
                    {
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(ShowDesktopLoopbackDeviceSelector));
                        OnPropertyChanged(nameof(ShowApplicationAudioProcessSelector));
                    }

                    return;
                }

                _desktopAudioSourceMode = requestedValue;
                _config.DesktopAudioSourceMode = requestedValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowDesktopLoopbackDeviceSelector));
                OnPropertyChanged(nameof(ShowApplicationAudioProcessSelector));

                if (!_isLoadingConfig
                    && requestedValue == (int)VRCLS.Core.Models.DesktopAudioSourceMode.Application)
                {
                    RefreshApplicationAudioProcesses();
                }
            }
        }

        public int SelectedApplicationAudioProcessId
        {
            get => _selectedApplicationAudioProcessId;
            set
            {
                if (_selectedApplicationAudioProcessId == value)
                {
                    return;
                }

                _selectedApplicationAudioProcessId = value;
                _config.AppAudioTargetProcessId = value;

                var selected = ApplicationAudioProcesses.FirstOrDefault(option => option.ProcessId == value);
                _config.AppAudioTargetProcessName = value <= 0
                    ? string.Empty
                    : selected?.ProcessName ?? _config.AppAudioTargetProcessName;
                OnPropertyChanged();
            }
        }

        public bool ShowDesktopLoopbackDeviceSelector
            => DesktopAudioSourceMode == (int)VRCLS.Core.Models.DesktopAudioSourceMode.System;

        public bool ShowApplicationAudioProcessSelector
            => DesktopAudioSourceMode == (int)VRCLS.Core.Models.DesktopAudioSourceMode.Application;

        public bool IsApplicationAudioCaptureSupported
            => AudioPlatformHelper.IsApplicationLoopbackSupported();

        public string ApplicationAudioSupportStatusText
            => string.Format(
                CultureInfo.InvariantCulture,
                GetUiString(
                    "应用监听音频系统不支持说明",
                    "当前系统不支持指定应用音频。需要更新过的 Windows 10 2004+ 或更新系统，当前 Build={1}。"),
                AudioPlatformHelper.ApplicationLoopbackMinimumBuild,
                AudioPlatformHelper.GetCurrentWindowsBuildNumber());

        public int GameVoiceMode
        {
            get => _gameVoiceMode;
            set
            {
                if (_gameVoiceMode == value)
                {
                    return;
                }

                _gameVoiceMode = value;
                _config.GameVoiceMode = value;

                OnPropertyChanged();
                // Notify dependent visibility bindings.
                OnPropertyChanged(nameof(ShowGameHotKey));
                OnPropertyChanged(nameof(ShowGamePressKey));
            }
        }

        public bool AutoStartDesktop
        {
            get => _autoStartDesktop;
            set
            {
                if (SetField(ref _autoStartDesktop, value))
                {
                    _config.AutoStartDesktop = value;
                }
            }
        }

        public bool ShowGameHotKey => GameVoiceMode == 1;

        // 是否显示桌面按住说话输入框 (按住说话模式)
        public bool ShowGamePressKey => GameVoiceMode == 2;

        public double GameCustomThreshold
        {
            get => _gameCustomThreshold;
            set
            {
                if (SetField(ref _gameCustomThreshold, value))
                {
                    _config.GameCustomThreshold = (float)value;
                }
            }
        }

        public string GameVoiceHotKey
        {
            get => _gameVoiceHotKey;
            set
            {
                if (EqualityComparer<string>.Default.Equals(_gameVoiceHotKey, value))
                {
                    return;
                }

                _gameVoiceHotKey = value;
                _config.GameVoiceHotKey = value;
                OnPropertyChanged();
            }
        }

        public string CapPressingKey
        {
            get => _capPressingKey;
            set
            {
                if (EqualityComparer<string>.Default.Equals(_capPressingKey, value))
                {
                    return;
                }

                _capPressingKey = value;
                _config.CapPressingKey = value;
                OnPropertyChanged();
            }
        }

        public double VadMinSilenceDuration
        {
            get => _vadMinSilenceDuration;
            set
            {
                if (SetField(ref _vadMinSilenceDuration, value))
                {
                    _config.VadMinSilenceDuration = (float)value;
                }
            }
        }

        public bool IsAccountOverviewRefreshing
        {
            get => _isAccountOverviewRefreshing;
            private set
            {
                if (SetField(ref _isAccountOverviewRefreshing, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public double MicVadMinSilenceDuration
        {
            get => _micVadMinSilenceDuration;
            set
            {
                var clamped = Math.Clamp(value, 0.05, 5.0);
                if (SetField(ref _micVadMinSilenceDuration, clamped))
                {
                    _config.MicVadMinSilenceDuration = (float)clamped;
                }
            }
        }

        public double DesktopVadMinSilenceDuration
        {
            get => _desktopVadMinSilenceDuration;
            set
            {
                var clamped = Math.Clamp(value, 0.05, 5.0);
                if (SetField(ref _desktopVadMinSilenceDuration, clamped))
                {
                    _config.DesktopVadMinSilenceDuration = (float)clamped;
                }
            }
        }

        public double VadMaxSegmentSeconds
        {
            get => _vadMaxSegmentSeconds;
            set
            {
                var clamped = Math.Clamp(value, 1.0, 60.0);
                if (SetField(ref _vadMaxSegmentSeconds, clamped))
                {
                    _config.VadMaxSegmentSeconds = (float)clamped;
                }
            }
        }

        public double MicVadMaxSegmentSeconds
        {
            get => _micVadMaxSegmentSeconds;
            set
            {
                var clamped = Math.Clamp(value, 1.0, 60.0);
                if (SetField(ref _micVadMaxSegmentSeconds, clamped))
                {
                    _config.MicVadMaxSegmentSeconds = (float)clamped;
                }
            }
        }

        public double DesktopVadMaxSegmentSeconds
        {
            get => _desktopVadMaxSegmentSeconds;
            set
            {
                var clamped = Math.Clamp(value, 1.0, 60.0);
                if (SetField(ref _desktopVadMaxSegmentSeconds, clamped))
                {
                    _config.DesktopVadMaxSegmentSeconds = (float)clamped;
                }
            }
        }

        public double VadThreshold
        {
            get => _vadThreshold;
            set
            {
                var clamped = Math.Clamp(value, 0.01, 0.99);
                if (SetField(ref _vadThreshold, clamped))
                {
                    _config.VadThreshold = (float)clamped;
                }
            }
        }

        public double LocalStreamingEndpointSilenceSeconds
        {
            get => _localStreamingEndpointSilenceSeconds;
            set
            {
                var clamped = Math.Clamp(value, 0.8, 3.0);
                if (SetField(ref _localStreamingEndpointSilenceSeconds, clamped))
                {
                    _config.LocalStreamingEndpointSilenceSeconds = (float)clamped;
                }
            }
        }

        // 翻译设置
        public string SourceLanguage
        {
            get => _sourceLanguage;
            set
            {
                var normalized = NormalizeRecognitionSourceLanguage(value);
                if (SetField(ref _sourceLanguage, normalized))
                {
                    _config.SourceLanguage = normalized;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string MicSourceLanguage
        {
            get => _micSourceLanguage;
            set
            {
                var normalized = NormalizeRecognitionSourceLanguage(value);
                if (SetField(ref _micSourceLanguage, normalized))
                {
                    _config.MicSourceLanguage = normalized;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string DesktopSourceLanguage
        {
            get => _desktopSourceLanguage;
            set
            {
                var normalized = NormalizeRecognitionSourceLanguage(value);
                if (SetField(ref _desktopSourceLanguage, normalized))
                {
                    _config.DesktopSourceLanguage = normalized;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string MicTargetTranslationLanguage
        {
            get => _micTargetTranslationLanguage;
            set
            {
                if (SetField(ref _micTargetTranslationLanguage, value))
                {
                    _config.MicTargetTranslationLanguage = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string MicTargetTranslationLanguage2
        {
            get => _micTargetTranslationLanguage2;
            set
            {
                if (SetField(ref _micTargetTranslationLanguage2, value))
                {
                    _config.MicTargetTranslationLanguage2 = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string MicTargetTranslationLanguage3
        {
            get => _micTargetTranslationLanguage3;
            set
            {
                if (SetField(ref _micTargetTranslationLanguage3, value))
                {
                    _config.MicTargetTranslationLanguage3 = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string DesktopTargetTranslationLanguage
        {
            get => _desktopTargetTranslationLanguage;
            set
            {
                if (SetField(ref _desktopTargetTranslationLanguage, value))
                {
                    _config.DesktopTargetTranslationLanguage = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string DesktopTargetTranslationLanguage2
        {
            get => _desktopTargetTranslationLanguage2;
            set
            {
                if (SetField(ref _desktopTargetTranslationLanguage2, value))
                {
                    _config.DesktopTargetTranslationLanguage2 = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string DesktopTargetTranslationLanguage3
        {
            get => _desktopTargetTranslationLanguage3;
            set
            {
                if (SetField(ref _desktopTargetTranslationLanguage3, value))
                {
                    _config.DesktopTargetTranslationLanguage3 = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string TextSendTargetTranslationLanguage
        {
            get => _textSendTargetTranslationLanguage;
            set
            {
                if (SetField(ref _textSendTargetTranslationLanguage, value))
                {
                    _config.TextSendTargetTranslationLanguage = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string TextSendTargetTranslationLanguage2
        {
            get => _textSendTargetTranslationLanguage2;
            set
            {
                if (SetField(ref _textSendTargetTranslationLanguage2, value))
                {
                    _config.TextSendTargetTranslationLanguage2 = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string TextSendTargetTranslationLanguage3
        {
            get => _textSendTargetTranslationLanguage3;
            set
            {
                if (SetField(ref _textSendTargetTranslationLanguage3, value))
                {
                    _config.TextSendTargetTranslationLanguage3 = NormalizeLanguageCode(value);
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string MicTranslationServer
        {
            get => _micTranslationServer;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "developer" : value.Trim().ToLowerInvariant();
                if (SetField(ref _micTranslationServer, normalized))
                {
                    _config.TranslateService = normalized;
                    SyncSharedTranslationServer();
                    OnPropertyChanged(nameof(IsAnyTranslationRouteUsingOpenAi));
                    MarkTranslationConfigChanged();
                }
            }
        }

        public double MicVadThreshold
        {
            get => _micVadThreshold;
            set
            {
                var clamped = Math.Clamp(value, 0.01, 0.99);
                if (SetField(ref _micVadThreshold, clamped))
                {
                    _config.MicVadThreshold = (float)clamped;
                }
            }
        }

        public double DesktopVadThreshold
        {
            get => _desktopVadThreshold;
            set
            {
                var clamped = Math.Clamp(value, 0.01, 0.99);
                if (SetField(ref _desktopVadThreshold, clamped))
                {
                    _config.DesktopVadThreshold = (float)clamped;
                }
            }
        }

        public string DesktopTranslationServer
        {
            get => _desktopTranslationServer;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "developer" : value.Trim().ToLowerInvariant();
                if (SetField(ref _desktopTranslationServer, normalized))
                {
                    _config.TranslateServiceCap = normalized;
                    SyncSharedTranslationServer();
                    OnPropertyChanged(nameof(IsAnyTranslationRouteUsingOpenAi));
                    MarkTranslationConfigChanged();
                }
            }
        }

        public bool IsAnyTranslationRouteUsingOpenAi
            => string.Equals(_micTranslationServer, "openai", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_desktopTranslationServer, "openai", StringComparison.OrdinalIgnoreCase);

        private void SyncSharedTranslationServer()
        {
            _config.TranslationServer = string.Equals(_config.TranslateService, _config.TranslateServiceCap, StringComparison.OrdinalIgnoreCase)
                ? _config.TranslateService
                : string.Empty;
        }

        public string TranslatorsDefaultRegion
        {
            get => _translatorsDefaultRegion;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "CN" : value.Trim().ToUpperInvariant();
                if (normalized != "CN" && normalized != "EN")
                {
                    normalized = "CN";
                }

                if (SetField(ref _translatorsDefaultRegion, normalized))
                {
                    _config.TranslatorsDefaultRegion = normalized;
                }
            }
        }

        public bool FilterEmoji
        {
            get => _filterEmoji;
            set
            {
                if (SetField(ref _filterEmoji, value))
                {
                    _config.FilterEmoji = value ? "true" : "false";
                }
            }
        }

        // OSC设置
        public string OscIp
        {
            get => _oscIp;
            set
            {
                if (SetField(ref _oscIp, value))
                {
                    _config.OscIp = value;
                }
            }
        }

        public int OscPort
        {
            get => _oscPort;
            set
            {
                if (SetField(ref _oscPort, value))
                {
                    _config.OscPort = value;
                }
            }
        }

        public string VrcChatboxFormatNew
        {
            get => _vrcChatboxFormatNew;
            set
            {
                if (SetField(ref _vrcChatboxFormatNew, value))
                {
                    _config.VrcChatboxFormatNew = value;
                }
            }
        }

        public bool OscChatboxNotificationSfx
        {
            get => _oscChatboxNotificationSfx;
            set
            {
                if (SetField(ref _oscChatboxNotificationSfx, value))
                {
                    _config.OscChatboxNotificationSfx = value;
                }
            }
        }

        public bool EnableOscServer
        {
            get => _enableOscServer;
            set
            {
                if (SetField(ref _enableOscServer, value))
                {
                    _config.EnableOscServer = value;
                }
            }
        }

        public string OscServerIp
        {
            get => _oscServerIp;
            set
            {
                if (SetField(ref _oscServerIp, value))
                {
                    _config.OscServerIp = value;
                }
            }
        }

        public int OscServerPort
        {
            get => _oscServerPort;
            set
            {
                if (SetField(ref _oscServerPort, value))
                {
                    _config.OscServerPort = value;
                }
            }
        }

        public bool OscShutdown
        {
            get => _oscShutdown;
            set
            {
                if (SetField(ref _oscShutdown, value))
                {
                    _config.OscShutdown = value;
                }
            }
        }

        // VRChat集成
        public bool TextInSteamVr
        {
            get => _textInSteamVr;
            set
            {
                if (SetField(ref _textInSteamVr, value))
                {
                    _config.TextInSteamVr = value;
                    if (value)
                    {
                        // Make it obvious immediately (otherwise overlay can be transparent until first transcript arrives).
                        _steamVrService.SetHand(Math.Clamp(_config.SteamVrHand, 0, 1));
                        _steamVrService.SetSize(_config.SteamVrSize);
                        _steamVrService.SetAnchorMode(_config.SteamVrAnchorModeValue);
                        var offset = _config.GetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue);
                        var rotation = _config.GetSteamVrAnchorRotation(_config.SteamVrAnchorModeValue);
                        _steamVrService.SetPositionOffset(offset.X, offset.Y, offset.Z);
                        _steamVrService.SetRotationOffset(rotation.X, rotation.Y, rotation.Z);
                        _steamVrService.RefreshStartupRegistrationStatus();
                        _steamVrService.SetVisible(true);
                        _ = _steamVrService.InitializeAsync();
                        _steamVrService.UpdateText("VRCLS SteamVR Overlay 已启用");
                        SteamVrStartupStatus = _steamVrService.StartupRegistrationStatus;
                    }
                    else
                    {
                        _steamVrService.SetVisible(false);
                    }
                }
            }
        }

        public int SteamVrHand
        {
            get => _steamVrHand;
            set
            {
                var normalized = Math.Clamp(value, 0, 1);
                if (SetField(ref _steamVrHand, normalized))
                {
                    _config.SteamVrHand = normalized;
                    _steamVrService.SetHand(normalized);
                }
            }
        }

        public double SteamVrSize
        {
            get => _steamVrSize;
            set
            {
                if (SetField(ref _steamVrSize, value))
                {
                    _config.SteamVrSize = (float)value;
                    _steamVrService.SetSize((float)value);
                }
            }
        }

        public int SteamVrAnchorMode
        {
            get => _steamVrAnchorMode;
            set
            {
                var normalized = Enum.IsDefined(typeof(SteamVrOverlayAnchorMode), value)
                    ? value
                    : (int)SteamVrOverlayAnchorMode.Palm;
                if (SetField(ref _steamVrAnchorMode, normalized))
                {
                    _config.SteamVrAnchorMode = normalized;
                    _steamVrService.SetAnchorMode((SteamVrOverlayAnchorMode)normalized);
                    var offset = _config.GetSteamVrAnchorOffset((SteamVrOverlayAnchorMode)normalized);
                    var rotation = _config.GetSteamVrAnchorRotation((SteamVrOverlayAnchorMode)normalized);
                    SetField(ref _steamVrOffsetX, offset.X, nameof(SteamVrOffsetX));
                    SetField(ref _steamVrOffsetY, offset.Y, nameof(SteamVrOffsetY));
                    SetField(ref _steamVrOffsetZ, offset.Z, nameof(SteamVrOffsetZ));
                    SetField(ref _steamVrRotationX, rotation.X, nameof(SteamVrRotationX));
                    SetField(ref _steamVrRotationY, rotation.Y, nameof(SteamVrRotationY));
                    SetField(ref _steamVrRotationZ, rotation.Z, nameof(SteamVrRotationZ));
                    _steamVrService.SetPositionOffset(offset.X, offset.Y, offset.Z);
                    _steamVrService.SetRotationOffset(rotation.X, rotation.Y, rotation.Z);
                }
            }
        }

        public double SteamVrOffsetX
        {
            get => _steamVrOffsetX;
            set
            {
                if (SetField(ref _steamVrOffsetX, value))
                {
                    _config.SetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue, (float)value, (float)_steamVrOffsetY, (float)_steamVrOffsetZ);
                    _steamVrService.SetPositionOffset((float)_steamVrOffsetX, (float)_steamVrOffsetY, (float)_steamVrOffsetZ);
                }
            }
        }

        public double SteamVrOffsetY
        {
            get => _steamVrOffsetY;
            set
            {
                if (SetField(ref _steamVrOffsetY, value))
                {
                    _config.SetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue, (float)_steamVrOffsetX, (float)value, (float)_steamVrOffsetZ);
                    _steamVrService.SetPositionOffset((float)_steamVrOffsetX, (float)_steamVrOffsetY, (float)_steamVrOffsetZ);
                }
            }
        }

        public double SteamVrOffsetZ
        {
            get => _steamVrOffsetZ;
            set
            {
                if (SetField(ref _steamVrOffsetZ, value))
                {
                    _config.SetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue, (float)_steamVrOffsetX, (float)_steamVrOffsetY, (float)value);
                    _steamVrService.SetPositionOffset((float)_steamVrOffsetX, (float)_steamVrOffsetY, (float)_steamVrOffsetZ);
                }
            }
        }

        public double SteamVrRotationX
        {
            get => _steamVrRotationX;
            set
            {
                if (SetField(ref _steamVrRotationX, value))
                {
                    _config.SetSteamVrAnchorRotation(_config.SteamVrAnchorModeValue, (float)value, (float)_steamVrRotationY, (float)_steamVrRotationZ);
                    _steamVrService.SetRotationOffset((float)_steamVrRotationX, (float)_steamVrRotationY, (float)_steamVrRotationZ);
                }
            }
        }

        public double SteamVrRotationY
        {
            get => _steamVrRotationY;
            set
            {
                if (SetField(ref _steamVrRotationY, value))
                {
                    _config.SetSteamVrAnchorRotation(_config.SteamVrAnchorModeValue, (float)_steamVrRotationX, (float)value, (float)_steamVrRotationZ);
                    _steamVrService.SetRotationOffset((float)_steamVrRotationX, (float)_steamVrRotationY, (float)_steamVrRotationZ);
                }
            }
        }

        public double SteamVrRotationZ
        {
            get => _steamVrRotationZ;
            set
            {
                if (SetField(ref _steamVrRotationZ, value))
                {
                    _config.SetSteamVrAnchorRotation(_config.SteamVrAnchorModeValue, (float)_steamVrRotationX, (float)_steamVrRotationY, (float)value);
                    _steamVrService.SetRotationOffset((float)_steamVrRotationX, (float)_steamVrRotationY, (float)_steamVrRotationZ);
                }
            }
        }

        public bool SteamVrAutoLaunch
        {
            get => _steamVrAutoLaunch;
            set
            {
                if (SetField(ref _steamVrAutoLaunch, value))
                {
                    _config.SteamVrAutoLaunch = value;
                    _steamVrService.ConfigureStartupAutoLaunch(value);
                    SteamVrStartupStatus = _steamVrService.StartupRegistrationStatus;
                    if (_isLoadingConfig)
                    {
                        return;
                    }

                    try
                    {
                        VRCLS.UI.App.SaveConfigToDisk(_config);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to save SteamVR auto-launch config.");
                    }
                }
            }
        }

        public double SteamVrNonTextOpacity
        {
            get => _steamVrNonTextOpacity;
            set
            {
                var normalized = Math.Clamp(value, 0d, 1d);
                if (SetField(ref _steamVrNonTextOpacity, normalized))
                {
                    _config.SteamVrNonTextOpacity = (float)normalized;
                }
            }
        }

        public bool SteamVrCursorFlipU
        {
            get => _steamVrCursorFlipU;
            set
            {
                if (SetField(ref _steamVrCursorFlipU, value))
                {
                    _config.SteamVrCursorFlipU = value;
                }
            }
        }

        public bool SteamVrCursorFlipV
        {
            get => _steamVrCursorFlipV;
            set
            {
                if (SetField(ref _steamVrCursorFlipV, value))
                {
                    _config.SteamVrCursorFlipV = value;
                }
            }
        }

        public string SteamVrStatus
        {
            get => _steamVrStatus;
            private set => SetField(ref _steamVrStatus, value);
        }

        public string SteamVrStartupStatus
        {
            get => _steamVrStartupStatus;
            private set => SetField(ref _steamVrStartupStatus, value);
        }

        public bool VrcBitmapLedColor
        {
            get => _vrcBitmapLedColor;
            set
            {
                if (SetField(ref _vrcBitmapLedColor, value))
                {
                    _config.VrcBitmapLedColor = value;
                }
            }
        }

        public int VrcBitmapLedRow
        {
            get => _vrcBitmapLedRow;
            set
            {
                if (SetField(ref _vrcBitmapLedRow, value))
                {
                    _config.VrcBitmapLedRow = value;
                }
            }
        }

        public int VrcBitmapLedCol
        {
            get => _vrcBitmapLedCol;
            set
            {
                if (SetField(ref _vrcBitmapLedCol, value))
                {
                    _config.VrcBitmapLedCol = value;
                }
            }
        }

        // TTS设置
        public bool EnableTts
        {
            get => _enableTts;
            set
            {
                if (SetField(ref _enableTts, value))
                {
                    // 如果开启，默认设置为模式1（翻译模式麦克风）
                    if (value && _config.TtsToggle == 0)
                    {
                        _config.TtsToggle = 1;
                        TtsMode = 1;
                        OnPropertyChanged(nameof(TtsMode));
                    }
                    // 如果关闭，设置为0
                    else if (!value)
                    {
                        _config.TtsToggle = 0;
                    }
                }
            }
        }

        public int TtsMode
        {
            get => _ttsMode;
            set
            {
                if (SetField(ref _ttsMode, value))
                {
                    _config.TtsToggle = value;
                    // 更新开关状态
                    var shouldEnable = value != 0;
                    if (_enableTts != shouldEnable)
                    {
                        _enableTts = shouldEnable;
                        OnPropertyChanged(nameof(EnableTts));
                    }
                }
            }
        }

        public int TtsEngine
        {
            get => _ttsEngine;
            set
            {
                if (SetField(ref _ttsEngine, value))
                {
                    _config.TtsEngine = value;
                    OnPropertyChanged(nameof(IsServerTtsEngine));
                    OnPropertyChanged(nameof(ShowQwenTtsToneSettings));
                    OnPropertyChanged(nameof(IsQwenTtsToneSpecified));
                }
            }
        }

        public bool IsServerTtsEngine => TtsEngine == 0;

        public string TtsProvider
        {
            get => _ttsProvider;
            set
            {
                var normalized = AppConfig.NormalizeTtsProvider(value);
                if (SetField(ref _ttsProvider, normalized))
                {
                    _config.TtsProvider = normalized;
                    OnPropertyChanged(nameof(IsQwenTtsProvider));
                    OnPropertyChanged(nameof(TtsVoiceHint));
                    OnPropertyChanged(nameof(ShowQwenTtsToneSettings));
                    OnPropertyChanged(nameof(IsQwenTtsToneSpecified));
                }
                else
                {
                    _config.TtsProvider = normalized;
                }
            }
        }

        public bool IsQwenTtsProvider =>
            string.Equals(TtsProvider, AppConfig.TtsProviderQwen, StringComparison.OrdinalIgnoreCase);

        public bool ShowQwenTtsToneSettings => IsServerTtsEngine && IsQwenTtsProvider;

        public bool IsQwenTtsToneSpecified =>
            ShowQwenTtsToneSettings && QwenTtsToneMode == (int)VRCLS.Core.Models.QwenTtsToneMode.Custom;

        public string TtsVoiceHint => IsQwenTtsProvider
            ? GetUiString("QwenTtsVoiceHint", "Qwen-TTS voice alias (alloy/echo/fable/onyx/nova/shimmer)")
            : GetUiString("EdgeTtsVoiceHint", "Edge-TTS voice (customizable)");

        public string TtsOutputName
        {
            get => _ttsOutputName;
            set
            {
                if (SetField(ref _ttsOutputName, value))
                {
                    _config.TtsOutputName = value;
                }
            }
        }

        public string TtsVoiceName
        {
            get => _ttsVoiceName;
            set
            {
                var normalized = (value ?? string.Empty).Trim();
                if (SetField(ref _ttsVoiceName, normalized))
                {
                    _config.TtsVoiceName = normalized;
                }
            }
        }

        public double TtsSpeed
        {
            get => _ttsSpeed;
            set
            {
                var clamped = Math.Clamp(value, 0.5, 2.0);
                if (SetField(ref _ttsSpeed, clamped))
                {
                    _config.TtsSpeed = (float)clamped;
                }
            }
        }

        public int QwenTtsToneMode
        {
            get => _qwenTtsToneMode;
            set
            {
                var normalized = Enum.IsDefined(typeof(VRCLS.Core.Models.QwenTtsToneMode), value)
                    ? value
                    : (int)VRCLS.Core.Models.QwenTtsToneMode.Custom;
                if (SetField(ref _qwenTtsToneMode, normalized))
                {
                    _config.QwenTtsToneMode = normalized;
                    OnPropertyChanged(nameof(IsQwenTtsToneSpecified));
                }
            }
        }

        public string QwenTtsInstruct
        {
            get => _qwenTtsInstruct;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetField(ref _qwenTtsInstruct, normalized))
                {
                    _config.QwenTtsInstruct = normalized;
                }
            }
        }

        public int TtsInterruptMode
        {
            get => _ttsInterruptMode;
            set
            {
                var mode = value == 0 ? 0 : 1;
                if (SetField(ref _ttsInterruptMode, mode))
                {
                    _config.TtsInterruptMode = mode;
                }
            }
        }

        public int MicTtsSpeakMask
        {
            get => _micTtsSpeakMask;
            set
            {
                if (SetField(ref _micTtsSpeakMask, value))
                {
                    _config.MicTtsSpeakMask = value;
                    OnPropertyChanged(nameof(MicTtsSpeakOriginal));
                    OnPropertyChanged(nameof(MicTtsSpeakTranslation1));
                    OnPropertyChanged(nameof(MicTtsSpeakTranslation2));
                    OnPropertyChanged(nameof(MicTtsSpeakTranslation3));
                }
            }
        }

        public int DesktopTtsSpeakMask
        {
            get => _desktopTtsSpeakMask;
            set
            {
                if (SetField(ref _desktopTtsSpeakMask, value))
                {
                    _config.DesktopTtsSpeakMask = value;
                    OnPropertyChanged(nameof(DesktopTtsSpeakOriginal));
                    OnPropertyChanged(nameof(DesktopTtsSpeakTranslation1));
                    OnPropertyChanged(nameof(DesktopTtsSpeakTranslation2));
                    OnPropertyChanged(nameof(DesktopTtsSpeakTranslation3));
                }
            }
        }

        public bool MicTtsSpeakOriginal
        {
            get => (MicTtsSpeakMask & 1) != 0;
            set => MicTtsSpeakMask = SetMaskBit(MicTtsSpeakMask, 1, value);
        }

        public bool MicTtsSpeakTranslation1
        {
            get => (MicTtsSpeakMask & 2) != 0;
            set => MicTtsSpeakMask = SetMaskBit(MicTtsSpeakMask, 2, value);
        }

        public bool MicTtsSpeakTranslation2
        {
            get => (MicTtsSpeakMask & 4) != 0;
            set => MicTtsSpeakMask = SetMaskBit(MicTtsSpeakMask, 4, value);
        }

        public bool MicTtsSpeakTranslation3
        {
            get => (MicTtsSpeakMask & 8) != 0;
            set => MicTtsSpeakMask = SetMaskBit(MicTtsSpeakMask, 8, value);
        }

        public bool DesktopTtsSpeakOriginal
        {
            get => (DesktopTtsSpeakMask & 1) != 0;
            set => DesktopTtsSpeakMask = SetMaskBit(DesktopTtsSpeakMask, 1, value);
        }

        public bool DesktopTtsSpeakTranslation1
        {
            get => (DesktopTtsSpeakMask & 2) != 0;
            set => DesktopTtsSpeakMask = SetMaskBit(DesktopTtsSpeakMask, 2, value);
        }

        public bool DesktopTtsSpeakTranslation2
        {
            get => (DesktopTtsSpeakMask & 4) != 0;
            set => DesktopTtsSpeakMask = SetMaskBit(DesktopTtsSpeakMask, 4, value);
        }

        public bool DesktopTtsSpeakTranslation3
        {
            get => (DesktopTtsSpeakMask & 8) != 0;
            set => DesktopTtsSpeakMask = SetMaskBit(DesktopTtsSpeakMask, 8, value);
        }

        public int MainWindowLanguageMask
        {
            get => _mainWindowLanguageMask;
            set
            {
                var normalized = value & (int)TranscriptLanguageMask.All;
                if (normalized == 0)
                {
                    normalized = (int)TranscriptLanguageMask.Translation1;
                }

                if (SetField(ref _mainWindowLanguageMask, normalized))
                {
                    _config.MainWindowLanguageMask = (TranscriptLanguageMask)normalized;
                    OnPropertyChanged(nameof(MainWindowShowOriginal));
                    OnPropertyChanged(nameof(MainWindowShowTranslation1));
                    OnPropertyChanged(nameof(MainWindowShowTranslation2));
                    OnPropertyChanged(nameof(MainWindowShowTranslation3));
                }
            }
        }

        public bool MainWindowShowOriginal
        {
            get => (MainWindowLanguageMask & 1) != 0;
            set => MainWindowLanguageMask = SetMaskBit(MainWindowLanguageMask, 1, value);
        }

        public bool MainWindowShowTranslation1
        {
            get => (MainWindowLanguageMask & 2) != 0;
            set => MainWindowLanguageMask = SetMaskBit(MainWindowLanguageMask, 2, value);
        }

        public bool MainWindowShowTranslation2
        {
            get => (MainWindowLanguageMask & 4) != 0;
            set => MainWindowLanguageMask = SetMaskBit(MainWindowLanguageMask, 4, value);
        }

        public bool MainWindowShowTranslation3
        {
            get => (MainWindowLanguageMask & 8) != 0;
            set => MainWindowLanguageMask = SetMaskBit(MainWindowLanguageMask, 8, value);
        }

        private static int SetMaskBit(int mask, int bit, bool enabled)
        {
            return enabled ? (mask | bit) : (mask & ~bit);
        }

        private static int NormalizeMainWindowLanguageMask(TranscriptLanguageMask mask)
        {
            var normalized = mask & TranscriptLanguageMask.All;
            if (normalized == TranscriptLanguageMask.None)
            {
                normalized = TranscriptLanguageMask.Translation1;
            }

            return (int)normalized;
        }

        private static int NormalizeCopyLanguageMask(TranscriptLanguageMask mask)
        {
            var normalized = mask & TranscriptLanguageMask.All;
            if (normalized == TranscriptLanguageMask.None)
            {
                normalized = TranscriptLanguageMask.Translation1;
            }

            return (int)normalized;
        }


        public string TtsTestText
        {
            get => _ttsTestText;
            set => SetField(ref _ttsTestText, value);
        }

        private void RefreshTtsOutputDevices()
        {
            TtsOutputDevices.Clear();
            TtsOutputDevices.Add("default");

            try
            {
                var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (!string.IsNullOrWhiteSpace(device.FriendlyName) && !TtsOutputDevices.Contains(device.FriendlyName))
                    {
                        TtsOutputDevices.Add(device.FriendlyName);
                    }
                }
            }
            catch
            {
                // Fall back to WaveOut capabilities below.
            }

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                if (!string.IsNullOrWhiteSpace(caps.ProductName) && !TtsOutputDevices.Contains(caps.ProductName))
                {
                    TtsOutputDevices.Add(caps.ProductName);
                }
            }
        }

        private void RefreshDesktopAudioDevices()
        {
            DesktopAudioDevices.Clear();
            DesktopAudioDevices.Add(MainViewModel.DefaultDeviceDisplayName);

            try
            {
                var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (!string.IsNullOrWhiteSpace(device.FriendlyName) && !DesktopAudioDevices.Contains(device.FriendlyName))
                    {
                        DesktopAudioDevices.Add(device.FriendlyName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "刷新桌面音频设备列表失败");
            }
        }

        private void RefreshApplicationAudioProcesses()
        {
            EnsureApplicationAudioDefaults();

            var previousSelectedId = _selectedApplicationAudioProcessId;
            var previousProcessName = _config.AppAudioTargetProcessName;

            ApplicationAudioProcesses.Clear();

            try
            {
                var processes = Process
                    .GetProcesses()
                    .Where(process =>
                    {
                        try
                        {
                            return !process.HasExited
                                && process.Id != Environment.ProcessId
                                && !string.IsNullOrWhiteSpace(process.ProcessName)
                                && process.SessionId > 0;
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(process => process.Id)
                    .Select(CreateApplicationAudioProcessOption)
                    .Where(option => option != null)
                    .Cast<ApplicationAudioProcessOption>()
                    .ToList();

                foreach (var option in processes)
                {
                    ApplicationAudioProcesses.Add(option);
                }

                EnsureSelectedApplicationAudioProcessPresent();

                var resolvedSelectionId = 0;
                if (previousSelectedId > 0 && ApplicationAudioProcesses.Any(option => option.ProcessId == previousSelectedId))
                {
                    resolvedSelectionId = previousSelectedId;
                }
                else
                {
                    var normalizedName = NormalizeProcessName(previousProcessName);
                    var matched = ApplicationAudioProcesses.FirstOrDefault(option =>
                        string.Equals(NormalizeProcessName(option.ProcessName), normalizedName, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        resolvedSelectionId = matched.ProcessId;
                    }
                }

                if (resolvedSelectionId > 0)
                {
                    SelectedApplicationAudioProcessId = resolvedSelectionId;
                }
                else if (ApplicationAudioProcesses.Any(option => option.ProcessId == _config.AppAudioTargetProcessId))
                {
                    SelectedApplicationAudioProcessId = _config.AppAudioTargetProcessId;
                }

                _logger.Information("已刷新应用监听音频进程列表，数量: {Count}", ApplicationAudioProcesses.Count);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "刷新应用监听音频进程列表失败");
                EnsureSelectedApplicationAudioProcessPresent();
            }
        }

        private ApplicationAudioProcessOption? CreateApplicationAudioProcessOption(Process process)
        {
            try
            {
                var processName = process.ProcessName;
                var title = string.Empty;

                try
                {
                    title = process.MainWindowTitle?.Trim() ?? string.Empty;
                }
                catch
                {
                    title = string.Empty;
                }

                var displayName = string.IsNullOrWhiteSpace(title)
                    ? processName
                    : title;

                return new ApplicationAudioProcessOption(process.Id, processName, displayName, false);
            }
            catch
            {
                return null;
            }
            finally
            {
                process.Dispose();
            }
        }

        private void EnsureSelectedApplicationAudioProcessPresent()
        {
            if (_config.AppAudioTargetProcessId <= 0 && string.IsNullOrWhiteSpace(_config.AppAudioTargetProcessName))
            {
                return;
            }

            if (ApplicationAudioProcesses.Any(option => option.ProcessId == _config.AppAudioTargetProcessId))
            {
                return;
            }

            var normalizedName = NormalizeProcessName(_config.AppAudioTargetProcessName);
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                var matched = ApplicationAudioProcesses.FirstOrDefault(option =>
                    string.Equals(NormalizeProcessName(option.ProcessName), normalizedName, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    _config.AppAudioTargetProcessId = matched.ProcessId;
                    _config.AppAudioTargetProcessName = matched.ProcessName;
                    return;
                }
            }

            var displayName = string.IsNullOrWhiteSpace(_config.AppAudioTargetProcessName)
                ? "未运行的应用"
                : $"{_config.AppAudioTargetProcessName} (未运行)";

            ApplicationAudioProcesses.Add(new ApplicationAudioProcessOption(
                _config.AppAudioTargetProcessId,
                _config.AppAudioTargetProcessName,
                displayName,
                true));
        }

        private void EnsureApplicationAudioDefaults()
        {
            if (string.IsNullOrWhiteSpace(_config.AppAudioTargetProcessName))
            {
                _config.AppAudioTargetProcessName = AppConfig.DefaultApplicationAudioTargetProcessName;
            }
        }

        private int ResolveInitialApplicationAudioProcessSelection()
        {
            if (_config.AppAudioTargetProcessId > 0)
            {
                return _config.AppAudioTargetProcessId;
            }

            var normalizedName = NormalizeProcessName(_config.AppAudioTargetProcessName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return 0;
            }

            var matched = ApplicationAudioProcesses.FirstOrDefault(option =>
                string.Equals(NormalizeProcessName(option.ProcessName), normalizedName, StringComparison.OrdinalIgnoreCase));
            return matched?.ProcessId ?? 0;
        }

        private static string NormalizeProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return string.Empty;
            }

            var trimmed = processName.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed)
                : trimmed;
        }

        private async Task TestTts()
        {
            try
            {
                var voiceName = IsServerTtsEngine && !string.IsNullOrWhiteSpace(TtsVoiceName)
                    ? TtsVoiceName.Trim()
                    : null;
                var language = ResolveTtsLanguageFromVoice(voiceName);
                var text = ResolveTtsTextForPlayback(voiceName, preferUserCustomText: true);
                await _ttsService.SpeakAsync(text, language, voiceName: voiceName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "TTS测试失败");
                MessageBox.Show($"TTS测试失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task PreviewTtsVoiceAsync(string voiceName)
        {
            if (string.IsNullOrWhiteSpace(voiceName))
            {
                return;
            }

            var normalizedVoice = voiceName.Trim();
            var language = ResolveTtsLanguageFromVoice(normalizedVoice);
            var sampleText = ResolveTtsTextForPlayback(normalizedVoice, preferUserCustomText: false);
            await _ttsService.SpeakAsync(sampleText, language, voiceName: normalizedVoice);
        }

        private string ResolveTtsTextForPlayback(string? voiceName, bool preferUserCustomText)
        {
            var customText = TtsTestText?.Trim() ?? string.Empty;
            var hasCustomText = !string.IsNullOrWhiteSpace(customText)
                && !string.Equals(customText, LegacyDefaultTtsTestText, StringComparison.Ordinal);

            if (preferUserCustomText && hasCustomText)
            {
                return customText;
            }

            var language = ResolveTtsLanguageFromVoice(voiceName);
            return BuildLanguageAwareTtsSampleText(language);
        }

        private string ResolveTtsLanguageFromVoice(string? voiceName)
        {
            if (!string.IsNullOrWhiteSpace(voiceName))
            {
                if (IsQwenTtsProvider && TryResolveQwenVoiceLanguage(voiceName, out var qwenLanguage))
                {
                    return qwenLanguage;
                }

                var segments = voiceName.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0 && !string.IsNullOrWhiteSpace(segments[0]))
                {
                    return segments[0].ToLowerInvariant();
                }
            }

            var source = NormalizeLanguageCode(SourceLanguage);
            if (string.IsNullOrWhiteSpace(source) || string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "zh";
            }

            var sourceSegments = source.Split('-', StringSplitOptions.RemoveEmptyEntries);
            return sourceSegments.Length > 0 && !string.IsNullOrWhiteSpace(sourceSegments[0])
                ? sourceSegments[0].ToLowerInvariant()
                : "zh";
        }

        private static bool TryResolveQwenVoiceLanguage(string voiceName, out string language)
        {
            language = string.Empty;
            var normalized = (voiceName ?? string.Empty).Trim().ToLowerInvariant();
            language = normalized switch
            {
                "echo" or "fable" or "aiden" => "en",
                "nova" => "ko",
                "shimmer" => "ja",
                "alloy" or "onyx" or "vivian" => "zh",
                _ => string.Empty
            };

            return !string.IsNullOrWhiteSpace(language);
        }

        private static string BuildLanguageAwareTtsSampleText(string language)
        {
            var normalized = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
            if (_ttsSampleTextByLanguage.TryGetValue(normalized, out var sample))
            {
                return sample;
            }

            return $"This is a voice test in {normalized}.";
        }

        public bool LocalizedSpeech
        {
            get => _localizedSpeech;
            set
            {
                if (SetField(ref _localizedSpeech, value))
                {
                    _config.LocalizedSpeech = value;
                }
            }
        }

        public bool LocalizedCapture
        {
            get => _localizedCapture;
            set
            {
                if (SetField(ref _localizedCapture, value))
                {
                    _config.LocalizedCapture = value;
                }
            }
        }

        public string OpenAiApiKey
        {
            get => _openAiApiKey;
            set
            {
                if (SetField(ref _openAiApiKey, value))
                {
                    _config.OpenAiConfig.ApiKey = value;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string OpenAiBaseUrl
        {
            get => _openAiBaseUrl;
            set
            {
                if (SetField(ref _openAiBaseUrl, value))
                {
                    _config.OpenAiConfig.BaseUrl = value;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string OpenAiModel
        {
            get => _openAiModel;
            set
            {
                if (SetField(ref _openAiModel, value))
                {
                    _config.OpenAiConfig.Model = value;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public double OpenAiTemperature
        {
            get => _openAiTemperature;
            set
            {
                var v = Math.Clamp(value, 0.0, 2.0);
                if (SetField(ref _openAiTemperature, v))
                {
                    _config.OpenAiConfig.Temperature = (float)v;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public int OpenAiMaxTokens
        {
            get => _openAiMaxTokens;
            set
            {
                var v = Math.Max(1, value);
                if (SetField(ref _openAiMaxTokens, v))
                {
                    _config.OpenAiConfig.MaxTokens = v;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string OpenAiSystemPrompt
        {
            get => _openAiSystemPrompt;
            set
            {
                if (SetField(ref _openAiSystemPrompt, value))
                {
                    _config.OpenAiConfig.SystemPrompt = value ?? string.Empty;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string OpenAiExtraBody
        {
            get => _openAiExtraBody;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetField(ref _openAiExtraBody, normalized))
                {
                    _config.OpenAiConfig.ExtraBody = normalized;
                    MarkTranslationConfigChanged();
                }
            }
        }

        public string SelectedOpenAiPreset
        {
            get => _selectedOpenAiPreset;
            set
            {
                var normalized = value ?? string.Empty;
                if (!SetField(ref _selectedOpenAiPreset, normalized))
                {
                    return;
                }

                _config.SelectedOpenAiPreset = normalized;
                MarkTranslationConfigChanged();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                var preset = _config.OpenAiPresetTemplates?
                    .FirstOrDefault(item => string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase));
                if (preset == null)
                {
                    return;
                }

                // Preset only applies endpoint/model/prompt style, never API key.
                OpenAiBaseUrl = string.IsNullOrWhiteSpace(preset.BaseUrl) ? OpenAiBaseUrl : preset.BaseUrl;
                OpenAiModel = string.IsNullOrWhiteSpace(preset.Model) ? OpenAiModel : preset.Model;
                OpenAiTemperature = preset.Temperature;
                OpenAiMaxTokens = preset.MaxTokens;
                OpenAiSystemPrompt = preset.SystemPrompt ?? string.Empty;
                OpenAiExtraBody = preset.ExtraBody ?? string.Empty;
            }
        }

        public string CurrentSessionLogText
        {
            get => _currentSessionLogText;
            private set => SetField(ref _currentSessionLogText, value);
        }

        public string CurrentSessionLogPath
        {
            get => _currentSessionLogPath;
            private set
            {
                if (SetField(ref _currentSessionLogPath, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // 外观设置
        public string UiLanguage
        {
            get => _uiLanguage;
            set
            {
                var normalized = App.NormalizeUiLanguage(value);
                if (SetField(ref _uiLanguage, normalized))
                {
                    _config.UiLanguage = normalized;
                    if (!_isLoadingConfig)
                    {
                        App.ApplyUiLanguage(normalized);
                        RefreshLocalizedOptionCollections();
                    }
                }
            }
        }

        public bool DarkMode
        {
            get => _darkMode;
            set
            {
                if (SetField(ref _darkMode, value))
                {
                    _config.DarkMode = value;
                }
            }
        }

        public bool TransparentWindow
        {
            get => _transparentWindow;
            set
            {
                if (SetField(ref _transparentWindow, value))
                {
                    _config.TransparentWindow = value;
                }
            }
        }

        public string TransparentWindowSize
        {
            get => _transparentWindowSize;
            set
            {
                if (SetField(ref _transparentWindowSize, value))
                {
                    _config.TransparentWindowSize = value;
                }
            }
        }

        public int OverlayWidth
        {
            get => _overlayWidth;
            set
            {
                if (SetField(ref _overlayWidth, value))
                {
                    _config.OverlayWidth = value;
                }
            }
        }

        public int OverlayHeight
        {
            get => _overlayHeight;
            set
            {
                if (SetField(ref _overlayHeight, value))
                {
                    _config.OverlayHeight = value;
                }
            }
        }

        public bool CopyBox
        {
            get => _copyBox;
            set
            {
                if (SetField(ref _copyBox, value))
                {
                    _config.CopyBox = value;
                }
            }
        }

        public bool IsLogAutoRefreshEnabled
        {
            get => _isLogAutoRefreshEnabled;
            set
            {
                if (SetField(ref _isLogAutoRefreshEnabled, value))
                {
                    ApplyLogAutoRefreshState();
                }
            }
        }

        public int CopyLanguageMask
        {
            get => _copyLanguageMask;
            set
            {
                var normalized = value & (int)TranscriptLanguageMask.All;
                if (normalized == 0)
                {
                    normalized = (int)TranscriptLanguageMask.Translation1;
                }

                if (SetField(ref _copyLanguageMask, normalized))
                {
                    _config.CopyLanguageMask = (TranscriptLanguageMask)normalized;
                    OnPropertyChanged(nameof(CopyOriginal));
                    OnPropertyChanged(nameof(CopyTranslation1));
                    OnPropertyChanged(nameof(CopyTranslation2));
                    OnPropertyChanged(nameof(CopyTranslation3));
                }
            }
        }

        public bool CopyOriginal
        {
            get => (CopyLanguageMask & 1) != 0;
            set => CopyLanguageMask = SetMaskBit(CopyLanguageMask, 1, value);
        }

        public bool CopyTranslation1
        {
            get => (CopyLanguageMask & 2) != 0;
            set => CopyLanguageMask = SetMaskBit(CopyLanguageMask, 2, value);
        }

        public bool CopyTranslation2
        {
            get => (CopyLanguageMask & 4) != 0;
            set => CopyLanguageMask = SetMaskBit(CopyLanguageMask, 4, value);
        }

        public bool CopyTranslation3
        {
            get => (CopyLanguageMask & 8) != 0;
            set => CopyLanguageMask = SetMaskBit(CopyLanguageMask, 8, value);
        }

        // 高级设置
        public string ChatboxOscMixTemplate
        {
            get => _chatboxOscMixTemplate;
            set
            {
                if (SetField(ref _chatboxOscMixTemplate, value))
                {
                    _config.ChatboxOscMixTemplate = value;
                }
            }
        }

        // ========== Experimental Keyword Actions ==========

        public string ExperimentalKeywordActionsJson
        {
            get => _experimentalKeywordActionsJson;
            set
            {
                if (SetField(ref _experimentalKeywordActionsJson, value))
                {
                    if (TryDeserializeExperimentalKeywordActions(value, out var scripts, out var errorMessage))
                    {
                        _config.Scripts = scripts;
                        ExperimentalKeywordActionsStatus = scripts.Count == 0
                            ? "当前没有实验规则。"
                            : $"已配置 {scripts.Count} 条实验规则。";
                    }
                    else
                    {
                        ExperimentalKeywordActionsStatus = errorMessage;
                    }
                }
            }
        }

        public string ExperimentalKeywordActionsStatus
        {
            get => _experimentalKeywordActionsStatus;
            private set => SetField(ref _experimentalKeywordActionsStatus, value);
        }

        public string ExperimentalKeywordActionsExample =>
            """
            [
              {
                "action": "切换高兴表情",
                "text": ["开心模式", "切开心"],
                "vrcActions": [
                  {
                    "vrcPath": "/avatar/parameters/Face",
                    "vrcValue": 1,
                    "vrcValueType": "int",
                    "sleeptime": 0
                  }
                ]
              },
              {
                "action": "打开特效",
                "text": ["打开特效", "开特效"],
                "vrcActions": [
                  {
                    "vrcPath": "/avatar/parameters/EffectOn",
                    "vrcValue": true,
                    "vrcValueType": "bool",
                    "sleeptime": 0
                  }
                ]
              }
            ]
            """;

        private void ResetKeywordsToDefault()
        {
            LoadExperimentalKeywordActionsFromConfig();
        }

        private void LoadExperimentalKeywordActionsFromConfig()
        {
            _experimentalKeywordActionsJson = SerializeExperimentalKeywordActions(_config.Scripts);
            OnPropertyChanged(nameof(ExperimentalKeywordActionsJson));

            ExperimentalKeywordActionsStatus = _config.Scripts == null || _config.Scripts.Count == 0
                ? "当前没有实验规则。"
                : $"已配置 {_config.Scripts.Count} 条实验规则。";
        }

        private static string SerializeExperimentalKeywordActions(IEnumerable<VoiceScript>? scripts)
        {
            var normalizedScripts = scripts?.ToList() ?? new List<VoiceScript>();
            return JsonSerializer.Serialize(normalizedScripts, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static bool TryDeserializeExperimentalKeywordActions(
            string json,
            out List<VoiceScript> scripts,
            out string errorMessage)
        {
            scripts = new List<VoiceScript>();
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(json))
            {
                return true;
            }

            try
            {
                scripts = JsonSerializer.Deserialize<List<VoiceScript>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<VoiceScript>();

                for (var i = 0; i < scripts.Count; i++)
                {
                    var script = scripts[i];
                    script.Action = (script.Action ?? string.Empty).Trim();
                    script.Text = (script.Text ?? new List<string>())
                        .Select(item => (item ?? string.Empty).Trim())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    script.VrcActions ??= new List<VrcAction>();

                    if (string.IsNullOrWhiteSpace(script.Action))
                    {
                        errorMessage = $"第 {i + 1} 条实验规则缺少 action。";
                        return false;
                    }

                    if (script.Text.Count == 0)
                    {
                        errorMessage = $"第 {i + 1} 条实验规则至少需要一个 text 关键词。";
                        return false;
                    }

                    if (script.VrcActions.Count == 0)
                    {
                        errorMessage = $"第 {i + 1} 条实验规则至少需要一个 vrcActions 动作。";
                        return false;
                    }

                    for (var actionIndex = 0; actionIndex < script.VrcActions.Count; actionIndex++)
                    {
                        var action = script.VrcActions[actionIndex];
                        action.VrcPath = (action.VrcPath ?? string.Empty).Trim();
                        action.VrcValueType = (action.VrcValueType ?? string.Empty).Trim();

                        if (string.IsNullOrWhiteSpace(action.VrcPath))
                        {
                            errorMessage = $"第 {i + 1} 条实验规则的第 {actionIndex + 1} 个动作缺少 vrcPath。";
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(action.VrcValueType))
                        {
                            errorMessage = $"第 {i + 1} 条实验规则的第 {actionIndex + 1} 个动作缺少 vrcValueType。";
                            return false;
                        }
                    }
                }

                errorMessage = scripts.Count == 0
                    ? "当前没有实验规则。"
                    : $"已配置 {scripts.Count} 条实验规则。";
                return true;
            }
            catch (JsonException ex)
            {
                errorMessage = $"JSON 解析失败: {ex.Message}";
                return false;
            }
        }

        private void RefreshRecognitionLanguageOptions()
        {
            var options = BuildLanguageOptions(
                _config.SupportedRecognitionLanguages,
                GetFallbackRecognitionLanguages(),
                GetConfiguredRecognitionLanguageCodes(),
                includeNoneOption: false);

            ReplaceLanguageOptions(SourceLanguages, options);
        }

        private void RefreshTranslationLanguageOptions()
        {
            var options = BuildLanguageOptions(
                _config.SupportedTranslationLanguages,
                GetFallbackTranslationLanguages(),
                GetConfiguredTranslationLanguageCodes(),
                includeNoneOption: true);

            ReplaceLanguageOptions(TargetLanguages, options);
        }

        private IReadOnlyList<LanguageOption> BuildLanguageOptions(
            IEnumerable<LanguageCatalogItem>? catalog,
            IReadOnlyList<LanguageOption> fallback,
            IEnumerable<string> configuredCodes,
            bool includeNoneOption)
        {
            var commonLanguageOrder = BuildCommonLanguageOrder(fallback, includeNoneOption);
            var options = new List<LanguageOption>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddOption(string? code, string? displayName)
            {
                var normalized = NormalizeLanguageCode(code ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!seen.Add(normalized))
                {
                    return;
                }

                var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
                    ? ResolveLanguageDisplayName(normalized, null)
                    : ResolveLanguageDisplayName(normalized, displayName.Trim());
                options.Add(new LanguageOption(normalized, resolvedDisplayName));
            }

            if (catalog != null)
            {
                foreach (var item in catalog)
                {
                    AddOption(item?.Code, item?.DisplayName);
                }
            }

            if (options.Count == 0)
            {
                foreach (var item in fallback)
                {
                    AddOption(item.Code, item.DisplayName);
                }
            }

            foreach (var code in configuredCodes)
            {
                AddOption(code, code);
            }

            if (includeNoneOption)
            {
                options.Add(new LanguageOption("none", GetUiString("LanguageOption_None", "不翻译")));
            }

            return ApplyLanguageGrouping(options, commonLanguageOrder);
        }

        private static IReadOnlyList<string> BuildCommonLanguageOrder(IReadOnlyList<LanguageOption> fallback, bool includeNoneOption)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCode(string? code)
            {
                var groupKey = GetLanguageGroupKey(code);
                if (string.IsNullOrWhiteSpace(groupKey) || !seen.Add(groupKey))
                {
                    return;
                }

                ordered.Add(groupKey);
            }

            if (includeNoneOption)
            {
                AddCode("none");
            }

            foreach (var item in fallback)
            {
                AddCode(item.Code);
            }

            return ordered;
        }

        private IReadOnlyList<LanguageOption> ApplyLanguageGrouping(
            IReadOnlyList<LanguageOption> options,
            IReadOnlyList<string> commonLanguageOrder)
        {
            var commonGroupName = GetUiString("LanguageGroup_Common", "常用语言");
            var otherGroupName = GetUiString("LanguageGroup_Other", "其他语言");
            var ordered = new List<LanguageOption>(options.Count);
            var addedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var groupKey in commonLanguageOrder)
            {
                foreach (var option in options)
                {
                    if (!string.Equals(GetLanguageGroupKey(option.Code), groupKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (addedCodes.Add(option.Code))
                    {
                        ordered.Add(new LanguageOption(option.Code, option.DisplayName, commonGroupName));
                    }
                }
            }

            foreach (var option in options)
            {
                if (addedCodes.Add(option.Code))
                {
                    ordered.Add(new LanguageOption(option.Code, option.DisplayName, otherGroupName));
                }
            }

            return ordered;
        }

        private string ResolveLanguageDisplayName(string code, string? fallbackDisplayName)
        {
            var normalized = NormalizeLanguageCode(code).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var preferredCatalogDisplayName = TryGetPreferredCatalogLanguageDisplayName(normalized, fallbackDisplayName);
            if (!string.IsNullOrWhiteSpace(preferredCatalogDisplayName))
            {
                return preferredCatalogDisplayName;
            }

            var localizedFromCulture = TryGetLocalizedLanguageDisplayName(normalized);
            if (!string.IsNullOrWhiteSpace(localizedFromCulture))
            {
                return localizedFromCulture;
            }

            if (!string.IsNullOrWhiteSpace(fallbackDisplayName))
            {
                return fallbackDisplayName.Trim();
            }

            return normalized;
        }

        private string? TryGetPreferredCatalogLanguageDisplayName(string code, string? fallbackDisplayName)
        {
            var trimmedFallback = string.IsNullOrWhiteSpace(fallbackDisplayName)
                ? null
                : fallbackDisplayName.Trim();

            if (IsEnglishUiLanguage())
            {
                if (!string.IsNullOrWhiteSpace(trimmedFallback)
                    && !string.Equals(trimmedFallback, code, StringComparison.OrdinalIgnoreCase)
                    && LooksLikeLatinDisplayName(trimmedFallback))
                {
                    return trimmedFallback;
                }

                return SpecialEnglishTranslationLanguageNames.TryGetValue(code, out var englishDisplayName)
                    ? englishDisplayName
                    : null;
            }

            if (IsJapaneseUiLanguage())
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(trimmedFallback)
                && !string.Equals(trimmedFallback, code, StringComparison.OrdinalIgnoreCase)
                && ContainsCjkCharacters(trimmedFallback))
            {
                return trimmedFallback;
            }

            return SpecialChineseTranslationLanguageNames.TryGetValue(code, out var chineseDisplayName)
                ? chineseDisplayName
                : null;
        }

        private static bool LooksLikeLatinDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || ContainsCjkCharacters(value))
            {
                return false;
            }

            return value.Any(static ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
        }

        private static bool ContainsCjkCharacters(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Any(static ch => ch >= '\u4E00' && ch <= '\u9FFF');
        }

        private string? TryGetLocalizedLanguageDisplayName(string code)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(code);
                var displayName = IsEnglishUiLanguage()
                    ? culture.EnglishName
                    : culture.DisplayName;

                if (displayName.StartsWith("Unknown Language", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return displayName;
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }

        private static void ReplaceLanguageOptions(ObservableCollection<LanguageOption> target, IReadOnlyList<LanguageOption> options)
        {
            target.Clear();
            foreach (var item in options)
            {
                target.Add(item);
            }
        }

        private static void ConfigureLanguageGrouping(ObservableCollection<LanguageOption> options)
        {
            var view = CollectionViewSource.GetDefaultView(options);
            if (view == null)
            {
                return;
            }

            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LanguageOption.GroupName)));
            }
        }

        private IEnumerable<string> GetConfiguredRecognitionLanguageCodes()
        {
            yield return _config.SourceLanguage;
            yield return _config.MicSourceLanguage;
            yield return _config.DesktopSourceLanguage;
        }

        private IEnumerable<string> GetConfiguredTranslationLanguageCodes()
        {
            yield return _config.TargetTranslationLanguage;
            yield return _config.TargetTranslationLanguage2;
            yield return _config.TargetTranslationLanguage3;
            yield return _config.MicTargetTranslationLanguage;
            yield return _config.MicTargetTranslationLanguage2;
            yield return _config.MicTargetTranslationLanguage3;
            yield return _config.DesktopTargetTranslationLanguage;
            yield return _config.DesktopTargetTranslationLanguage2;
            yield return _config.DesktopTargetTranslationLanguage3;
            yield return _config.TextSendTargetTranslationLanguage;
            yield return _config.TextSendTargetTranslationLanguage2;
            yield return _config.TextSendTargetTranslationLanguage3;
        }

        private static IReadOnlyList<LanguageOption> GetFallbackRecognitionLanguages()
        {
            return new[]
            {
                new LanguageOption("zh", "中文"),
                new LanguageOption("en", "英语"),
                new LanguageOption("ja", "日语"),
                new LanguageOption("ko", "韩语"),
                new LanguageOption("de", "德语"),
                new LanguageOption("fr", "法语"),
                new LanguageOption("es", "西班牙语"),
                new LanguageOption("ru", "俄语")
            };
        }

        private static IReadOnlyList<LanguageOption> GetFallbackTranslationLanguages()
        {
            return new[]
            {
                new LanguageOption("en", "英语"),
                new LanguageOption("zh", "中文"),
                new LanguageOption("ja", "日语"),
                new LanguageOption("ko", "韩语"),
                new LanguageOption("de", "德语"),
                new LanguageOption("fr", "法语"),
                new LanguageOption("es", "西班牙语"),
                new LanguageOption("ru", "俄语")
            };
        }

        private static IReadOnlyList<string> GetFallbackTranslationEngines()
        {
            return new[]
            {
                "google",
                "bing",
                "baidu",
                "yandex",
                "deepl"
            };
        }

        private void RefreshTranslationEngineOptions()
        {
            var engines = new List<string> { "developer" };
            if (_config.SupportedTranslationEngines != null)
            {
                engines.AddRange(_config.SupportedTranslationEngines
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim().ToLowerInvariant())
                    .Where(item => item != "developer" && item != "openai"));
            }

            if (engines.Count == 1)
            {
                engines.AddRange(GetFallbackTranslationEngines());
            }

            engines.Add("openai");
            var normalized = engines
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            TranslationServiceOptions.Clear();
            foreach (var code in normalized)
            {
                TranslationServiceOptions.Add(new TranslationEngineOption(code, GetTranslationEngineDisplayName(code)));
            }

            if (!normalized.Contains(_micTranslationServer, StringComparer.OrdinalIgnoreCase))
            {
                MicTranslationServer = normalized.FirstOrDefault() ?? "developer";
            }

            if (!normalized.Contains(_desktopTranslationServer, StringComparer.OrdinalIgnoreCase))
            {
                DesktopTranslationServer = normalized.FirstOrDefault() ?? "developer";
            }
        }

        private static string GetTranslationEngineDisplayName(string code)
        {
            var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
            var key = $"TranslationEngine_{normalized}";
            var localized = Application.Current?.TryFindResource(key) as string;
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }

            return HumanizeTranslationEngineCode(normalized);
        }

        private static string HumanizeTranslationEngineCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            return code switch
            {
                "qqfanyi" => "QQ Fanyi",
                "qqtransmart" => "QQ TranSmart",
                "cloudtranslation" => "Cloud Translation",
                "languagewire" => "Language Wire",
                "modernmt" => "Modern MT",
                "mymemory" => "MyMemory",
                "translateme" => "TranslateMe",
                "translatecom" => "Translate.com",
                "volcengine" => "Volc Engine",
                "iflytek" => "iFlytek",
                "iflyrec" => "iFlyrec",
                _ => SplitCamelCaseWords(code)
            };
        }

        private static string SplitCamelCaseWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if (i > 0 &&
                    current != '-' &&
                    current != '_' &&
                    char.IsUpper(current) &&
                    (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1])))
                {
                    builder.Append(' ');
                }

                builder.Append(current == '-' || current == '_' ? ' ' : current);
            }

            var tokens = builder
                .ToString()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Length switch
                {
                    <= 2 => token.ToUpperInvariant(),
                    _ => char.ToUpperInvariant(token[0]) + token.Substring(1)
                });

            return string.Join(" ", tokens);
        }

        private void RefreshOpenAiPresetNames()
        {
            OpenAiPresetNames.Clear();
            OpenAiPresetNames.Add(string.Empty);

            var templates = _config.OpenAiPresetTemplates ?? new List<OpenAiPresetTemplate>();
            foreach (var template in templates)
            {
                if (template == null || string.IsNullOrWhiteSpace(template.Name))
                {
                    continue;
                }

                if (!OpenAiPresetNames.Contains(template.Name))
                {
                    OpenAiPresetNames.Add(template.Name);
                }
            }
        }

        private void LoadCurrentSessionLog()
        {
            try
            {
                CurrentSessionLogPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VRCLS",
                    "logs",
                    $"vrcls-{DateTime.Now:yyyyMMdd}.log");

                var snapshot = _runtimeLogBuffer.GetSnapshot();
                CurrentSessionLogText = string.IsNullOrWhiteSpace(snapshot)
                    ? "暂无运行日志。"
                    : snapshot;
            }
            catch (Exception ex)
            {
                CurrentSessionLogText = $"Load log failed: {ex.Message}";
            }
        }

        private void ApplyLogAutoRefreshState()
        {
            if (IsLogAutoRefreshEnabled)
            {
                LoadCurrentSessionLog();
                _logAutoRefreshTimer.Start();
                return;
            }

            _logAutoRefreshTimer.Stop();
        }

        private void OnLogAutoRefreshTimerTick(object? sender, EventArgs e)
        {
            LoadCurrentSessionLog();
        }

        private void OpenCurrentSessionLogFile()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(CurrentSessionLogPath))
                {
                    return;
                }

                var path = CurrentSessionLogPath.Trim();
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                    return;
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", directory)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OpenCurrentSessionLogFile failed.");
            }
        }

        private static string ExtractErrorMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "unknown error";
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("detail", out var detail))
                {
                    return raw;
                }

                return detail.ValueKind switch
                {
                    JsonValueKind.String => detail.GetString() ?? raw,
                    JsonValueKind.Object when detail.TryGetProperty("message", out var msg) => msg.GetString() ?? raw,
                    _ => detail.ToString()
                };
            }
            catch
            {
                return raw;
            }
        }

        private static string NormalizeLanguageCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var trimmed = value.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) return trimmed;
            return trimmed.Substring(0, colon).Trim();
        }

        private static string GetLanguageGroupKey(string? value)
        {
            var normalized = NormalizeLanguageCode(value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            var separatorIndex = normalized.IndexOfAny(new[] { '-', '_' });
            return separatorIndex > 0
                ? normalized.Substring(0, separatorIndex)
                : normalized;
        }

        private static string NormalizeRecognitionSourceLanguage(string value)
        {
            var normalized = NormalizeLanguageCode(value).Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) || normalized == "auto"
                ? "zh"
                : normalized;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

              try
              {
                  _modelUpdateService.ModelsChanged -= OnModelsChanged;
                  _translationHotUpdateService.ProfileUpdated -= OnTranslationProfileUpdated;
                  _authService.StateChanged -= OnAuthStateChanged;
                  _steamVrService.StatusChanged -= OnSteamVrStatusChanged;
                  _logAutoRefreshTimer.Tick -= OnLogAutoRefreshTimerTick;
                  _logAutoRefreshTimer.Stop();
              }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SettingsViewModel 取消事件订阅失败");
            }

            _userCenterHttpClient.Dispose();
        }

        private sealed class UserCenterPaymentLinkRequest
        {
            [JsonPropertyName("username")]
            public string Username { get; set; } = string.Empty;

            [JsonPropertyName("password")]
            public string Password { get; set; } = string.Empty;

            [JsonPropertyName("action")]
            public string Action { get; set; } = "renew";

            [JsonPropertyName("months")]
            public int Months { get; set; } = 1;

            [JsonPropertyName("target_monthly_price")]
            public decimal TargetMonthlyPrice { get; set; } = 5.00m;

            [JsonPropertyName("guide_token")]
            public string? GuideToken { get; set; }

            [JsonPropertyName("plan_price")]
            public decimal PlanPrice { get; set; } = 5.00m;

            [JsonPropertyName("custom_price")]
            public decimal CustomPrice { get; set; } = 5.00m;
        }

        private sealed class UserCenterPaymentGuideRequest
        {
            [JsonPropertyName("username")]
            public string Username { get; set; } = string.Empty;

            [JsonPropertyName("password")]
            public string Password { get; set; } = string.Empty;
        }

        private sealed class UserCenterPaymentGuideResponse
        {
            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("is_test_user")]
            public bool IsTestUser { get; set; }

            [JsonPropertyName("is_registered")]
            public bool IsRegistered { get; set; }

            [JsonPropertyName("is_expired")]
            public bool IsExpired { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            [JsonPropertyName("price_step")]
            public int? PriceStep { get; set; }

            [JsonPropertyName("resource_increment_per_step")]
            public int? ResourceIncrementPerStep { get; set; }

            [JsonPropertyName("display_plan_name")]
            public string? DisplayPlanName { get; set; }

            [JsonPropertyName("display_monthly_price")]
            public decimal? DisplayMonthlyPrice { get; set; }

            [JsonPropertyName("display_resource_quota")]
            public int? DisplayResourceQuota { get; set; }

            [JsonPropertyName("display_resource_summary")]
            public string? DisplayResourceSummary { get; set; }

            [JsonPropertyName("anchor_reason")]
            public string? AnchorReason { get; set; }

            [JsonPropertyName("allowed_actions")]
            public List<string>? AllowedActions { get; set; }

            [JsonPropertyName("renew_option")]
            public UserCenterPaymentGuideOption? RenewOption { get; set; }

            [JsonPropertyName("upgrade_option")]
            public UserCenterPaymentGuideOption? UpgradeOption { get; set; }

            [JsonPropertyName("downgrade_option")]
            public UserCenterPaymentGuideOption? DowngradeOption { get; set; }

            [JsonPropertyName("guide_token")]
            public string? GuideToken { get; set; }
        }

        private sealed class UserCenterPaymentGuideOption
        {
            [JsonPropertyName("allowed")]
            public bool Allowed { get; set; }

            [JsonPropertyName("readonly")]
            public bool ReadOnly { get; set; }

            [JsonPropertyName("default_monthly_price")]
            public decimal? DefaultMonthlyPrice { get; set; }

            [JsonPropertyName("min_monthly_price")]
            public decimal? MinMonthlyPrice { get; set; }

            [JsonPropertyName("max_monthly_price")]
            public decimal? MaxMonthlyPrice { get; set; }

            [JsonPropertyName("step")]
            public int? Step { get; set; }

            [JsonPropertyName("target_resource_quota_preview")]
            public int? TargetResourceQuotaPreview { get; set; }
        }

        private sealed class UserCenterPaymentLinkResponse
        {
            [JsonPropertyName("donation_url")]
            public string? DonationUrl { get; set; }

            [JsonPropertyName("remark")]
            public string? Remark { get; set; }

            [JsonPropertyName("custom_id")]
            public string? CustomId { get; set; }
        }

        private sealed class UserCenterActivateRequest
        {
            [JsonPropertyName("remark")]
            public string? Remark { get; set; }

            [JsonPropertyName("custom_id")]
            public string? CustomId { get; set; }

            [JsonPropertyName("out_trade_no")]
            public string? OutTradeNo { get; set; }
        }

        private sealed class UserCenterActivateResponse
        {
            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }
        }

        private sealed class UserCenterAccountUsageResponse
        {
            [JsonPropertyName("is_test_user")]
            public bool IsTestUser { get; set; }

            [JsonPropertyName("plan_name")]
            public string? PlanName { get; set; }

            [JsonPropertyName("plan_price")]
            public decimal? PlanPrice { get; set; }

            [JsonPropertyName("plan_ref")]
            public string? PlanRef { get; set; }

            [JsonPropertyName("pricing_mode")]
            public string? PricingMode { get; set; }

            [JsonPropertyName("expiration_utc")]
            public DateTimeOffset? ExpirationUtc { get; set; }

            [JsonPropertyName("daily_limit")]
            public int? DailyLimit { get; set; }

            [JsonPropertyName("remaining_daily")]
            public int? RemainingDaily { get; set; }

            [JsonPropertyName("daily_success_count")]
            public int DailySuccessCount { get; set; }

            [JsonPropertyName("today_success_count")]
            public int? TodaySuccessCount { get; set; }

            [JsonPropertyName("today_daily_resource_used")]
            public int? TodayDailyResourceUsed { get; set; }

            [JsonPropertyName("resource_used")]
            public int? ResourceUsed { get; set; }

            [JsonPropertyName("addon_resource_balance")]
            public int? AddonResourceBalance { get; set; }

            [JsonPropertyName("addon_resource_total")]
            public int? AddonResourceTotal { get; set; }

            [JsonPropertyName("daily_stats")]
            public List<UserCenterAccountUsageDailyResponse>? DailyStats { get; set; }
        }

        private sealed class UserCenterAccountUsageDailyResponse
        {
            [JsonPropertyName("date")]
            public string? Date { get; set; }

            [JsonPropertyName("day")]
            public string? Day { get; set; }

            [JsonPropertyName("date_utc")]
            public string? DateUtc { get; set; }

            [JsonPropertyName("daily_success_count")]
            public int? DailySuccessCount { get; set; }

            [JsonPropertyName("success_count")]
            public int? SuccessCount { get; set; }

            [JsonPropertyName("used")]
            public int? Used { get; set; }

            [JsonPropertyName("daily_limit")]
            public int? DailyLimit { get; set; }

            [JsonPropertyName("limit")]
            public int? Limit { get; set; }

            [JsonPropertyName("remaining_daily")]
            public int? RemainingDaily { get; set; }

            [JsonPropertyName("remaining")]
            public int? Remaining { get; set; }
        }

        private sealed class DonationVerificationResult
        {
            public DonationVerificationResult(bool success, string message)
            {
                Success = success;
                Message = message;
            }

            public bool Success { get; }

            public string Message { get; }
        }

        private sealed class AccountUsageFetchResult
        {
            private AccountUsageFetchResult(UserCenterAccountUsageResponse? payload, string? errorMessage, bool requiresLogin)
            {
                Payload = payload;
                ErrorMessage = errorMessage;
                RequiresLogin = requiresLogin;
            }

            public UserCenterAccountUsageResponse? Payload { get; }

            public string? ErrorMessage { get; }

            public bool RequiresLogin { get; }

            public static AccountUsageFetchResult Success(UserCenterAccountUsageResponse payload) => new(payload, null, false);

            public static AccountUsageFetchResult Fail(string? errorMessage) => new(null, errorMessage, false);

            public static AccountUsageFetchResult LoginRequired() => new(null, null, true);
        }
    }

    public sealed class AccountUsageDailyStatRow
    {
        public AccountUsageDailyStatRow(DateTime date, int successCount, int? dailyLimit, int? remainingDaily)
        {
            Date = date;
            SuccessCount = successCount;
            DailyLimit = dailyLimit;
            RemainingDaily = remainingDaily;
        }

        public DateTime Date { get; }
        public int SuccessCount { get; }
        public int? DailyLimit { get; }
        public int? RemainingDaily { get; }
    }

    public sealed class DonationActionOption
    {
        public DonationActionOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public string Code { get; }
        public string DisplayName { get; }
    }

    public sealed class ActivationMethodOption
    {
        public ActivationMethodOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public string Code { get; }
        public string DisplayName { get; }
    }

    public sealed class LanguageOption
    {
        public LanguageOption(string code, string displayName, string groupName = "")
        {
            Code = code;
            DisplayName = displayName;
            GroupName = groupName;
        }

        public string Code { get; }
        public string DisplayName { get; }
        public string GroupName { get; }
        public override string ToString() => DisplayName;
    }

    public sealed class TranslationEngineOption
    {
        public TranslationEngineOption(string code, string displayName)
        {
            Code = code ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string Code { get; }
        public string DisplayName { get; }
    }

    public sealed class ApplicationAudioProcessOption
    {
        public ApplicationAudioProcessOption(int processId, string processName, string displayName, bool isOffline)
        {
            ProcessId = processId;
            ProcessName = processName ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            IsOffline = isOffline;
        }

        public int ProcessId { get; }
        public string ProcessName { get; }
        public string DisplayName { get; }
        public bool IsOffline { get; }
    }

    // Event args for control settings changes
    public class ControlSettingsChangedEventArgs : EventArgs
    {
        public ControlSettingsChangeType ChangeType { get; set; }
        public bool IsEnabled { get; set; }
        public string? NewMicDevice { get; set; }
    }

    public enum ControlSettingsChangeType
    {
        EnabledChanged,
        MicSourceChanged
    }
}
