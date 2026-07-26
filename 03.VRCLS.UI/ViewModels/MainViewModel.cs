using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VRCLS.Core.Helpers;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;
using VRCLS.Services.Audio;
using VRCLS.Services.Translation;
using VRCLS.UI;
using VRCLS.UI.Services;
using VRCLS.UI.Views;
using VRCLS.Services.Tts;
using VRCLS.Services.Command;
using VRCLS.Services.Photo;
using Serilog;
namespace VRCLS.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<MainViewModel>();
        private readonly AppConfig _config;
        private readonly IOscService _oscService;
        private readonly IAudioPipelineFactory _pipelineFactory;
        private readonly ITranslationService _translationService;
        private readonly ITtsService _ttsService;
        private readonly IHotkeyService _hotkeyService;
        private readonly ICommandService _commandService;
        private readonly IBackendAuthService _authService;
        private readonly ISteamVRService _steamVrService;
        private readonly IModelUpdateService _modelUpdateService;
        private readonly IPhotoTranslationService _photoTranslationService;
        private readonly VadCalibrationService _vadCalibrationService;
        private readonly MicrophonePassthroughService _microphonePassthroughService;
        public const int UserCenterTabIndex = 0;
        public const int RecognitionSettingsTabIndex = 1;
        public const int AppearanceSettingsTabIndex = 6;
        public const int UpdateSettingsTabIndex = 7;
        private string _connectionState = "Disconnected";
        private string _appVersionText = string.Empty;
        private string _homeAlertMessage = string.Empty;
        private string _rateLimitNoticeMessage = string.Empty;
        private readonly object _homeAlertLock = new();
        private readonly object _rateLimitNoticeLock = new();
        private readonly HashSet<string> _rateLimitSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rateLimitRules = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _homeAlertCts;
        private static readonly TimeSpan HomeAlertDuration = TimeSpan.FromSeconds(8);
        private static readonly Regex RateLimitRulePattern = new(
            @"\d+\s*/\s*(?:day|hour|minute|min|second)|\d+\s+per\s+\d+\s+(?:day|hour|minute|min|second)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TranslationBoilerplatePrefixPattern = new(
            @"^\s*(?:(?:以下|下面|这是|這是)?\s*(?:翻译|翻譯|译文|譯文)(?:结果|結果)?\s*(?:是|如下|为|為)?\s*[:：]\s*|(?:翻訳|訳文|訳)(?:結果)?\s*(?:は|です)?\s*[:：]\s*|(?:here(?:'s| is)?(?:\s+the)?|the)\s+(?:translation|translated text)(?:\s+is)?\s*[:：]\s*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private string _lastAccountReminder = string.Empty;
        private string _lastAuthFailureReminder = string.Empty;
        private readonly object _oscSendLock = new();
        private CancellationTokenSource? _oscSendCts;
        private DateTime _lastOscSendUtc = DateTime.MinValue;
        private string _lastOscMessage = string.Empty;
        private static readonly TimeSpan OscSendMinInterval = TimeSpan.FromMilliseconds(350);
        private readonly object _oscMixLock = new();
        private string _lastOscClientData = string.Empty;
        private string _lastOscServerData = string.Empty;
        private DateTime _lastOscClientDataUtc = DateTime.MinValue;
        private DateTime _lastOscServerDataUtc = DateTime.MinValue;
        private static readonly TimeSpan OscMixDataRetention = TimeSpan.FromSeconds(30);
        private DateTime _lastClipboardUtc = DateTime.MinValue;
        private string _lastClipboardText = string.Empty;
        private static readonly TimeSpan ClipboardMinInterval = TimeSpan.FromMilliseconds(500);
        private static readonly HashSet<string> PhotoTranslationDirectImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp"
        };
        private static readonly HashSet<string> PhotoTranslationClipboardImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
        };
        private static readonly TimeSpan TtsStartDelay = TimeSpan.FromMilliseconds(80);
        private DateTime _lastMicVolumeUiUtc = DateTime.MinValue;
        private DateTime _lastDesktopVolumeUiUtc = DateTime.MinValue;
        private static readonly TimeSpan VolumeUiInterval = TimeSpan.FromMilliseconds(50);
        private readonly ConcurrentQueue<LogItem> _pendingLogs = new();
        private readonly ConcurrentQueue<TranscriptItem> _pendingTranscripts = new();
        private DispatcherTimer? _uiFlushTimer;
        private DispatcherTimer? _pipelineRestartTimer;
        private DispatcherTimer? _desktopApplicationTargetRetryTimer;
        private static readonly TimeSpan UiFlushInterval = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan PipelineRestartDebounceInterval = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan DesktopApplicationTargetRetryInterval = TimeSpan.FromSeconds(2);
        private const int UiFlushBatchSize = 40;
        private const string DesktopApplicationPendingCaptureKey = "app:pending";
        private int _uiFlushScheduled;
        private int _ttsScheduleVersion;
        private bool _pendingMicPipelineRestart;
        private bool _pendingDesktopPipelineRestart;
        private bool _isApplyingPipelineRestart;
        private bool _runtimeServicesInitialized;
        private bool _appliedOscServerEnabled;
        private string _appliedOscServerIp = string.Empty;
        private int _appliedOscServerPort;
        private bool _isRollingBackOscServerSettings;
        private string _lastRecognitionBackendSignature = string.Empty;
        private BackendAuthMode _lastRecognitionAuthMode;
        private readonly object _steamVrUpdateLock = new();
        private CancellationTokenSource? _steamVrUpdateCts;
        private DateTime _lastSteamVrUpdateUtc = DateTime.MinValue;
        private static readonly TimeSpan SteamVrUpdateMinInterval = TimeSpan.FromMilliseconds(120);
        // �豸���Ƴ�����UI��ʾ�� vs ���ô洢ֵ
        public static string DefaultDeviceDisplayName => Application.Current?.TryFindResource("DefaultDeviceDisplayName") as string ?? "系统默认设备";
        public const string DefaultDeviceConfigValue = "default";
        private AudioPipelineService? _micPipeline;
        private AudioPipelineService? _desktopPipeline;
        private bool _micVadCalibrationSubscribed;
        private bool _desktopVadCalibrationSubscribed;
        private string _micPipelineSignature = string.Empty;
        private string _desktopPipelineSignature = string.Empty;
        private string _micCaptureDevice = string.Empty;
        private string _desktopCaptureDevice = string.Empty;
        private DateTime _micDisableGraceUntilUtc = DateTime.MinValue;
        private DateTime _desktopDisableGraceUntilUtc = DateTime.MinValue;
        private static readonly TimeSpan ResultDrainGrace = TimeSpan.FromSeconds(3);
        private DateTime _micCaptureStartUtc = DateTime.MinValue;
        private DateTime _desktopCaptureStartUtc = DateTime.MinValue;
        private readonly object _captureWindowLock = new();
        private readonly List<(DateTime StartUtc, DateTime EndUtc)> _micCaptureWindows = new();
        private readonly List<(DateTime StartUtc, DateTime EndUtc)> _desktopCaptureWindows = new();
        private static readonly TimeSpan CaptureWindowRetention = TimeSpan.FromMinutes(2);
        private const int MaxCaptureWindows = 6;
        private bool _isMicListening;
        private bool _isDesktopListening;
        private bool _isOverlayVisible = true;
        private bool _isOverlayPinned = true;
        private bool _isOverlayLocked;
        private double _mainWindowOpacity = AppConfig.DefaultMainWindowOpacity;
        private double _mainWindowBackgroundOpacity = AppConfig.DefaultMainWindowBackgroundOpacity;
        private bool _showOriginal;
        private bool _showTranslation1;
        private bool _showTranslation2;
        private bool _showTranslation3;
        private bool _updatingMainLanguageMask;
        private string _currentSource = "Mic";
        private string _micText = string.Empty;
        private string _desktopText = string.Empty;
        private string _micTranslation = string.Empty;
        private string _desktopTranslation = string.Empty;
        private string _selectedMicDevice = "default";
        private bool _isMicSpeaking;
        private bool _isDesktopSpeaking;
        private float _micVolume;
        private float _desktopVolume;
        private bool _isModelHotReloading;
        private bool _isSendingManualText;
        private bool _isPhotoTranslationArmed;
        private bool _isPhotoTranslationProcessing;
        private int _currentPhotoTranslationResultIndex = -1;
        private PhotoTranslationResultItem? _currentPhotoTranslationResult;
        private string _manualInputText = string.Empty;
        private string _uiLanguage = "en-US";
        private bool _isUiLanguageMenuOpen;
        private bool _isQuickStartGuideVisible;
        private int _quickStartStep = 1;
        private SettingsWindow? _settingsWindow;
        private PhotoTranslationWindow? _photoTranslationWindow;
        private bool _isPhotoTranslationSessionOpen;
        private bool _closingPhotoTranslationWindow;
        public ObservableCollection<string> MicDevices { get; } = new();
        public ObservableCollection<LogItem> Logs { get; } = new();
        public ObservableCollection<TranscriptItem> Transcripts { get; } = new();
        public ObservableCollection<PhotoTranslationResultItem> PhotoTranslationResults { get; } = new();
        public ObservableCollection<LanguageOption> UiLanguages { get; } = new();
        public ICommand ToggleMicCommand { get; }
        public ICommand ToggleDesktopCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand ExportLogsCommand { get; }
        public ICommand HideOverlayCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand SendManualTextCommand { get; }
        public ICommand PasteManualTextCommand { get; }
        public ICommand RequestPhotoTranslationCommand { get; }
        public ICommand PastePhotoTranslationImageCommand { get; }
        public ICommand CopyPhotoTranslationResultCommand { get; }
        public ICommand ClearPhotoTranslationResultsCommand { get; }
        public ICommand PreviousPhotoTranslationResultCommand { get; }
        public ICommand NextPhotoTranslationResultCommand { get; }
        public ICommand CopyTranscriptCommand { get; }
        public ICommand OpenQuickStartGuideCommand { get; }
        public ICommand DismissQuickStartGuideCommand { get; }
        public ICommand NextQuickStartStepCommand { get; }
        public ICommand PrevQuickStartStepCommand { get; }
        public ICommand OpenQuickStartRecognitionCommand { get; }
        public ICommand OpenQuickStartUpdateCommand { get; }
        public ICommand OpenDonationGuideCommand { get; }
        public ICommand CloseHomeAlertCommand { get; }
        public ICommand CloseRateLimitNoticeCommand { get; }
        public MainViewModel(AppConfig config, IOscService oscService, IAudioPipelineFactory pipelineFactory,
            ITranslationService translationService, ITtsService ttsService, IHotkeyService hotkeyService,
            ICommandService commandService, ISteamVRService steamVrService, IModelUpdateService modelUpdateService,
            IBackendAuthService authService, VadCalibrationService vadCalibrationService,
            MicrophonePassthroughService microphonePassthroughService,
            IPhotoTranslationService photoTranslationService)
        {
            _logger.Debug("初始化 MainViewModel");
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _oscService = oscService ?? throw new ArgumentNullException(nameof(oscService));
            _oscService.MessageReceived += OnOscMessageReceived;
            _pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
            _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
            _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
            _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
            _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _steamVrService = steamVrService ?? throw new ArgumentNullException(nameof(steamVrService));
            _modelUpdateService = modelUpdateService ?? throw new ArgumentNullException(nameof(modelUpdateService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _vadCalibrationService = vadCalibrationService ?? throw new ArgumentNullException(nameof(vadCalibrationService));
            _microphonePassthroughService = microphonePassthroughService ?? throw new ArgumentNullException(nameof(microphonePassthroughService));
            _photoTranslationService = photoTranslationService ?? throw new ArgumentNullException(nameof(photoTranslationService));
            AppVersionText = BuildAppVersionText();
            var normalizedMainMask = NormalizeLanguageMask(_config.MainWindowLanguageMask);
            if (normalizedMainMask != _config.MainWindowLanguageMask)
            {
                _config.MainWindowLanguageMask = normalizedMainMask;
            }
            _isOverlayPinned = _config.MainWindowPinned;
            MainWindowOpacity = AppConfig.NormalizeMainWindowOpacity(_config.MainWindowOpacity);
            _config.MainWindowOpacity = MainWindowOpacity;
            MainWindowBackgroundOpacity = AppConfig.NormalizeMainWindowBackgroundOpacity(_config.MainWindowBackgroundOpacity);
            _config.MainWindowBackgroundOpacity = MainWindowBackgroundOpacity;
            _uiLanguage = App.NormalizeUiLanguage(_config.UiLanguage);
            _config.UiLanguage = _uiLanguage;
            RefreshUiLanguageOptions();
            _isQuickStartGuideVisible = !_config.HasSeenQuickStartGuide;
            ApplyMainWindowLanguageMask(normalizedMainMask);
            _modelUpdateService.ModelsChanged += OnModelsChanged;
            // ���ļ�Ȩ״̬�仯
            _authService.StateChanged += OnAuthStateChanged;
            _authService.ErrorOccurred += OnServiceError;
            _authService.UserNotification += OnAuthUserNotification;
            _authService.AccountStatusUpdated += OnAccountStatusUpdated;
            _vadCalibrationService.MonitoringChanged += OnVadCalibrationMonitoringChanged;
            _ttsService.ErrorOccurred += OnServiceError;
            _lastRecognitionBackendSignature = BuildRecognitionBackendSignature();
            _lastRecognitionAuthMode = _authService.State.AuthMode;
            UpdateConnectionState(_authService.State);
            if (ShouldRefreshWebsiteAccountStatus(_authService.State))
            {
                _ = RefreshAccountStatusAsync();
            }
            ToggleMicCommand = new RelayCommand(ToggleMic);
            ToggleDesktopCommand = new RelayCommand(ToggleDesktop);
            ClearLogsCommand = new RelayCommand(() =>
            {
                _pendingLogs.Clear();
                Logs.Clear();
                _logger.Information("用户清空日志");
            });
            ExportLogsCommand = new RelayCommand(ExportLogs);
            HideOverlayCommand = new RelayCommand(HideOverlay);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenQuickStartGuideCommand = new RelayCommand(OpenQuickStartGuide);
            DismissQuickStartGuideCommand = new RelayCommand(DismissQuickStartGuide);
            NextQuickStartStepCommand = new RelayCommand(NextQuickStartStep);
            PrevQuickStartStepCommand = new RelayCommand(PrevQuickStartStep);
            OpenQuickStartRecognitionCommand = new RelayCommand(OpenQuickStartRecognition);
            OpenQuickStartUpdateCommand = new RelayCommand(OpenQuickStartUpdate);
            SendManualTextCommand = new RelayCommand(SendManualText, CanSendManualText);
            PasteManualTextCommand = new RelayCommand(PasteManualText);
            RequestPhotoTranslationCommand = new RelayCommand(TogglePhotoTranslation);
            PastePhotoTranslationImageCommand = new RelayCommand(PastePhotoTranslationImageFromClipboard);
            CopyPhotoTranslationResultCommand = new RelayCommand<PhotoTranslationResultItem>(CopyPhotoTranslationResult);
            ClearPhotoTranslationResultsCommand = new RelayCommand(ClearPhotoTranslationResults);
            PreviousPhotoTranslationResultCommand = new RelayCommand(ShowPreviousPhotoTranslationResult, CanShowPreviousPhotoTranslationResult);
            NextPhotoTranslationResultCommand = new RelayCommand(ShowNextPhotoTranslationResult, CanShowNextPhotoTranslationResult);
            CopyTranscriptCommand = new RelayCommand<TranscriptItem>(CopyTranscript);
            OpenDonationGuideCommand = new RelayCommand(OpenDonationGuide);
            CloseHomeAlertCommand = new RelayCommand(CloseHomeAlert);
            CloseRateLimitNoticeCommand = new RelayCommand(CloseRateLimitNotice);
            InitializeUiFlushTimer();
            MicDevices.Add(DefaultDeviceDisplayName);
            // �����ü���ʱ���� "default" ת��Ϊ��ʾ��
            var configMicName = string.IsNullOrWhiteSpace(_config.MicName) ? DefaultDeviceConfigValue : _config.MicName;
            SelectedMicDevice = configMicName == DefaultDeviceConfigValue ? DefaultDeviceDisplayName : configMicName;
            _logger.Information("MainViewModel 核心初始化完成 - 麦克风设备: {MicDevice}", SelectedMicDevice);
        }

        public void InitializeRuntimeServices()
        {
            if (_runtimeServicesInitialized)
            {
                return;
            }

            _runtimeServicesInitialized = true;
            _logger.Debug("初始化 MainViewModel 运行时服务");

            RefreshDevices();
            InitializePipelines();
            EnsureMicPassthroughState();
            InitializeHealthMonitor();
            InitializeHotkeys();
            InitializeCommandService();
            InitializeSteamVrOverlay();
            InitializeOscServer();
            InitializePhotoTranslation();
            _logger.Information("MainViewModel 运行时服务初始化完成 - 麦克风设备: {MicDevice}", SelectedMicDevice);
        }
        private void InitializeOscServer()
        {
            _appliedOscServerEnabled = false;
            _appliedOscServerIp = _config.OscServerIp;
            _appliedOscServerPort = _config.OscServerPort;

            try
            {
                if (_config.EnableOscServer)
                {
                    _oscService.StartOscServer(_config.OscServerPort);
                    _appliedOscServerEnabled = true;
                    _appliedOscServerIp = _config.OscServerIp;
                    _appliedOscServerPort = _config.OscServerPort;
                    _logger.Information("OSC服务器已启动 - ip={Ip}, port={Port}", _config.OscServerIp, _config.OscServerPort);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "启动 OSC 服务失败");
            }
        }
        private void InitializeSteamVrOverlay()
        {
            try
            {
                if (_config.SteamVrDiagnosticLogging)
                {
                    _logger.Information(
                        "Initializing SteamVR overlay integration. textInSteamVr={TextInSteamVr}, autoLaunch={AutoLaunch}, hand={Hand}, size={Size}, anchorMode={AnchorMode}",
                        _config.TextInSteamVr,
                        _config.SteamVrAutoLaunch,
                        _config.SteamVrHand,
                        _config.SteamVrSize,
                        _config.SteamVrAnchorModeValue);
                }
                _steamVrService.Connected += OnSteamVrConnected;
                _steamVrService.Disconnected += OnSteamVrDisconnected;
                _steamVrService.ActionRequested += OnSteamVrActionRequested;
                _steamVrService.SetHand(Math.Clamp(_config.SteamVrHand, 0, 1));
                _steamVrService.SetTrackingDeviceMode(_config.SteamVrTrackingDeviceModeValue);
                _steamVrService.SetSize(_config.SteamVrSize);
                _steamVrService.SetAnchorMode(_config.SteamVrAnchorModeValue);
                var offset = _config.GetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue);
                var rotation = _config.GetSteamVrAnchorRotation(_config.SteamVrAnchorModeValue);
                _steamVrService.SetPositionOffset(offset.X, offset.Y, offset.Z);
                _steamVrService.SetRotationOffset(rotation.X, rotation.Y, rotation.Z);
                if (_config.TextInSteamVr)
                {
                    if (_config.SteamVrDiagnosticLogging)
                    {
                        _logger.Information("SteamVR text overlay is enabled at startup; queueing overlay initialization.");
                    }
                    _steamVrService.SetVisible(true);
                    _ = _steamVrService.InitializeAsync();
                    RequestSteamVrOverlayUpdate();
                }
                else
                {
                    if (_config.SteamVrDiagnosticLogging)
                    {
                        _logger.Information("SteamVR text overlay is disabled at startup; queueing registration/status refresh only.");
                    }
                    _steamVrService.RefreshStartupRegistrationStatus();
                    _steamVrService.IdentifyCurrentProcess();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "初始化 SteamVR Overlay 失败");
            }
        }
        private void OnSteamVrActionRequested(object? sender, SteamVrOverlayAction action)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(() => OnSteamVrActionRequested(sender, action));
                    return;
                }
                switch (action.Type)
                {
                    case SteamVrOverlayActionType.ToggleMic:
                        SetMicListeningState(!IsMicListening, logStateChange: true);
                        break;
                    case SteamVrOverlayActionType.ToggleDesktop:
                        SetDesktopListeningState(!IsDesktopListening, logStateChange: true);
                        break;
                    case SteamVrOverlayActionType.TogglePanelSourceMic:
                    {
                        var next = _config.SteamVrPanelSourceMask ^ TranscriptSourceMask.Mic;
                        if ((next & TranscriptSourceMask.All) == TranscriptSourceMask.None)
                        {
                            next = TranscriptSourceMask.Mic;
                        }
                        _config.SteamVrPanelSourceMask = next;
                        PersistConfigSafe();
                        break;
                    }
                    case SteamVrOverlayActionType.TogglePanelSourceDesktop:
                    {
                        var next = _config.SteamVrPanelSourceMask ^ TranscriptSourceMask.Desktop;
                        if ((next & TranscriptSourceMask.All) == TranscriptSourceMask.None)
                        {
                            next = TranscriptSourceMask.Desktop;
                        }
                        _config.SteamVrPanelSourceMask = next;
                        PersistConfigSafe();
                        break;
                    }
                    case SteamVrOverlayActionType.TogglePanelLangOriginal:
                    {
                        var next = _config.SteamVrPanelLanguageMask ^ TranscriptLanguageMask.Original;
                        if ((next & TranscriptLanguageMask.All) == TranscriptLanguageMask.None)
                        {
                            next = TranscriptLanguageMask.Original;
                        }
                        _config.SteamVrPanelLanguageMask = next;
                        PersistConfigSafe();
                        break;
                    }
                    case SteamVrOverlayActionType.TogglePanelLangTranslation1:
                    {
                        var next = _config.SteamVrPanelLanguageMask ^ TranscriptLanguageMask.Translation1;
                        if ((next & TranscriptLanguageMask.All) == TranscriptLanguageMask.None)
                        {
                            next = TranscriptLanguageMask.Translation1;
                        }
                        _config.SteamVrPanelLanguageMask = next;
                        PersistConfigSafe();
                        break;
                    }
                    case SteamVrOverlayActionType.TogglePanelLangTranslation2:
                    {
                        var next = _config.SteamVrPanelLanguageMask ^ TranscriptLanguageMask.Translation2;
                        if ((next & TranscriptLanguageMask.All) == TranscriptLanguageMask.None)
                        {
                            next = TranscriptLanguageMask.Translation2;
                        }
                        _config.SteamVrPanelLanguageMask = next;
                        PersistConfigSafe();
                        break;
                    }
                    case SteamVrOverlayActionType.TogglePanelLangTranslation3:
                    {
                        var next = _config.SteamVrPanelLanguageMask ^ TranscriptLanguageMask.Translation3;
                        if ((next & TranscriptLanguageMask.All) == TranscriptLanguageMask.None)
                        {
                            next = TranscriptLanguageMask.Translation3;
                        }
                        _config.SteamVrPanelLanguageMask = next;
                        PersistConfigSafe();
                        break;
                    }
                    case SteamVrOverlayActionType.CopyTranscript:
                        CopySteamVrTranscript(action.Transcript);
                        break;
                    case SteamVrOverlayActionType.SetAnchorPalm:
                        _config.SteamVrAnchorModeValue = SteamVrOverlayAnchorMode.Palm;
                        _steamVrService.SetAnchorMode(SteamVrOverlayAnchorMode.Palm);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetAnchorBackOfHand:
                        _config.SteamVrAnchorModeValue = SteamVrOverlayAnchorMode.BackOfHand;
                        _steamVrService.SetAnchorMode(SteamVrOverlayAnchorMode.BackOfHand);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetAnchorHead:
                        _config.SteamVrAnchorModeValue = SteamVrOverlayAnchorMode.Head;
                        _steamVrService.SetAnchorMode(SteamVrOverlayAnchorMode.Head);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetHandRight:
                        _config.SteamVrHand = 0;
                        _steamVrService.SetHand(0);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetHandLeft:
                        _config.SteamVrHand = 1;
                        _steamVrService.SetHand(1);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetTrackingDeviceAuto:
                        _config.SteamVrTrackingDeviceModeValue = SteamVrTrackingDeviceMode.Auto;
                        _steamVrService.SetTrackingDeviceMode(SteamVrTrackingDeviceMode.Auto);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetTrackingDeviceController:
                        _config.SteamVrTrackingDeviceModeValue = SteamVrTrackingDeviceMode.Controller;
                        _steamVrService.SetTrackingDeviceMode(SteamVrTrackingDeviceMode.Controller);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SetTrackingDeviceHandTracking:
                        _config.SteamVrTrackingDeviceModeValue = SteamVrTrackingDeviceMode.HandTracking;
                        _steamVrService.SetTrackingDeviceMode(SteamVrTrackingDeviceMode.HandTracking);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.IncreaseOverlaySize:
                    {
                        var next = Math.Clamp(_config.SteamVrSize + 0.01f, 0.05f, 0.5f);
                        _config.SteamVrSize = next;
                        _steamVrService.SetSize(next);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    }
                    case SteamVrOverlayActionType.DecreaseOverlaySize:
                    {
                        var next = Math.Clamp(_config.SteamVrSize - 0.01f, 0.05f, 0.5f);
                        _config.SteamVrSize = next;
                        _steamVrService.SetSize(next);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    }
                    case SteamVrOverlayActionType.IncreasePanelFontSize:
                    {
                        _config.SteamVrFontScale = Math.Clamp(_config.SteamVrFontScale + 0.05f, 0.70f, 1.40f);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    }
                    case SteamVrOverlayActionType.DecreasePanelFontSize:
                    {
                        _config.SteamVrFontScale = Math.Clamp(_config.SteamVrFontScale - 0.05f, 0.70f, 1.40f);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    }
                    case SteamVrOverlayActionType.ResetOverlayOffset:
                        _config.SetSteamVrAnchorOffset(_config.SteamVrAnchorModeValue, 0f, 0f, 0f);
                        _steamVrService.SetPositionOffset(0f, 0f, 0f);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.ToggleSteamVrAutoLaunch:
                        _config.SteamVrAutoLaunch = !_config.SteamVrAutoLaunch;
                        _steamVrService.ConfigureStartupAutoLaunch(_config.SteamVrAutoLaunch);
                        PersistConfigSafe();
                        RefreshSteamVrSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.ToggleTts:
                        _config.TtsToggle = _config.TtsToggle == 0 ? 1 : 0;
                        ClearPendingTts("steamvr tts toggle changed");
                        EnsureMicPassthroughState();
                        PersistConfigSafe();
                        RefreshSteamVrTtsSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.ToggleTtsMicPassthrough:
                        _config.TtsMicPassthroughEnabled = !_config.TtsMicPassthroughEnabled;
                        EnsureMicPassthroughState();
                        PersistConfigSafe();
                        RefreshSteamVrTtsSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.DecreaseTtsMicPassthroughVolume:
                        _config.TtsMicPassthroughVolume = Math.Clamp(_config.TtsMicPassthroughVolume - 0.05f, 0f, 4f);
                        EnsureMicPassthroughState();
                        PersistConfigSafe();
                        RefreshSteamVrTtsSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.IncreaseTtsMicPassthroughVolume:
                        _config.TtsMicPassthroughVolume = Math.Clamp(_config.TtsMicPassthroughVolume + 0.05f, 0f, 4f);
                        EnsureMicPassthroughState();
                        PersistConfigSafe();
                        RefreshSteamVrTtsSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.ToggleMicTtsOriginal:
                        ToggleSteamVrTtsSpeakMask(isDesktop: false, bit: 1);
                        break;
                    case SteamVrOverlayActionType.ToggleMicTtsTranslation1:
                        ToggleSteamVrTtsSpeakMask(isDesktop: false, bit: 2);
                        break;
                    case SteamVrOverlayActionType.ToggleMicTtsTranslation2:
                        ToggleSteamVrTtsSpeakMask(isDesktop: false, bit: 4);
                        break;
                    case SteamVrOverlayActionType.ToggleMicTtsTranslation3:
                        ToggleSteamVrTtsSpeakMask(isDesktop: false, bit: 8);
                        break;
                    case SteamVrOverlayActionType.ToggleDesktopTtsOriginal:
                        ToggleSteamVrTtsSpeakMask(isDesktop: true, bit: 1);
                        break;
                    case SteamVrOverlayActionType.ToggleDesktopTtsTranslation1:
                        ToggleSteamVrTtsSpeakMask(isDesktop: true, bit: 2);
                        break;
                    case SteamVrOverlayActionType.ToggleDesktopTtsTranslation2:
                        ToggleSteamVrTtsSpeakMask(isDesktop: true, bit: 4);
                        break;
                    case SteamVrOverlayActionType.ToggleDesktopTtsTranslation3:
                        ToggleSteamVrTtsSpeakMask(isDesktop: true, bit: 8);
                        break;
                    case SteamVrOverlayActionType.SelectMicSourceLanguage:
                        _config.MicSourceLanguage = NormalizeSelectedMicRecognitionLanguage(action.LanguageCode);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectMicTargetLanguage:
                        _config.MicTargetTranslationLanguage = NormalizeSelectedTranslationLanguage(action.LanguageCode, AppConfig.DefaultMicTargetTranslationLanguage);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectMicTargetLanguage2:
                        _config.MicTargetTranslationLanguage2 = NormalizeSelectedTranslationLanguage(action.LanguageCode);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectMicTargetLanguage3:
                        _config.MicTargetTranslationLanguage3 = NormalizeSelectedTranslationLanguage(action.LanguageCode);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectDesktopSourceLanguage:
                        _config.DesktopSourceLanguage = NormalizeSelectedRecognitionLanguage(action.LanguageCode, AppConfig.DefaultDesktopSourceLanguage);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectDesktopTargetLanguage:
                        _config.DesktopTargetTranslationLanguage = NormalizeSelectedTranslationLanguage(action.LanguageCode, AppConfig.DefaultDesktopTargetTranslationLanguage);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectDesktopTargetLanguage2:
                        _config.DesktopTargetTranslationLanguage2 = NormalizeSelectedTranslationLanguage(action.LanguageCode);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.SelectDesktopTargetLanguage3:
                        _config.DesktopTargetTranslationLanguage3 = NormalizeSelectedTranslationLanguage(action.LanguageCode);
                        PersistConfigSafe();
                        RefreshSteamVrLanguageSettingsViewModel();
                        break;
                    case SteamVrOverlayActionType.RequestPhotoTranslation:
                        RequestPhotoTranslation();
                        break;
                    case SteamVrOverlayActionType.CancelPhotoTranslation:
                        CancelPhotoTranslation();
                        break;
                    case SteamVrOverlayActionType.ClosePhotoTranslation:
                        StopPhotoTranslationSession(closeWindow: true, addLog: true, "已关闭 VR 拍照翻译面板，停止监听 VRChat 照片。");
                        break;
                    default:
                        break;
                }
                RequestSteamVrOverlayUpdate();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "更新 SteamVR Overlay 面板失败");
            }
        }

        private void InitializePhotoTranslation()
        {
            try
            {
                IsPhotoTranslationArmed = _photoTranslationService.IsNextPhotoTranslationArmed;
                IsPhotoTranslationProcessing = _photoTranslationService.IsPhotoTranslationProcessing;
                _photoTranslationService.TextRecognized += OnPhotoTextRecognized;
                _photoTranslationService.NextPhotoTranslationArmedChanged += OnNextPhotoTranslationArmedChanged;
                _photoTranslationService.PhotoTranslationProcessingChanged += OnPhotoTranslationProcessingChanged;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "初始化拍照翻译失败");
            }
        }

        private async void OnPhotoTextRecognized(object? sender, TranscriptionResult result)
        {
            try
            {
                await HandleRecognitionResultAsync(result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Photo recognition handler failed.");
            }
        }

        private void OnNextPhotoTranslationArmedChanged(object? sender, bool isArmed)
        {
            QueueUi(() =>
            {
                IsPhotoTranslationArmed = isArmed;
                RequestSteamVrOverlayUpdate();
            }, DispatcherPriority.Background);
        }

        private void OnPhotoTranslationProcessingChanged(object? sender, bool isProcessing)
        {
            QueueUi(() =>
            {
                IsPhotoTranslationProcessing = isProcessing;
                RequestSteamVrOverlayUpdate();
            }, DispatcherPriority.Background);
        }

        private void HandlePhotoTranslationResult(TranscriptionResult result, string original)
        {
            var mode = AppConfig.NormalizePhotoTranslationMode(result.PhotoTranslationMode);
            var item = new PhotoTranslationResultItem
            {
                Original = original,
                Translation = result.Translation ?? string.Empty,
                Translation2 = result.Translation2 ?? string.Empty,
                Translation3 = result.Translation3 ?? string.Empty,
                SourceImagePath = result.PhotoSourceImagePath ?? string.Empty,
                ResultImagePath = result.PhotoTranslatedImagePath ?? string.Empty,
                DisplayMode = mode,
                Timestamp = result.CapturedAtUtc == DateTime.MinValue
                    ? DateTime.Now
                    : result.CapturedAtUtc.ToLocalTime()
            };

            if (!_isPhotoTranslationSessionOpen)
            {
                _logger.Debug("Discard photo translation result because the photo translation window is closed. sourceImage={SourceImage}", item.SourceImagePath);
                return;
            }

            if (string.IsNullOrWhiteSpace(item.DisplayText) && string.IsNullOrWhiteSpace(item.ResultImagePath))
            {
                _logger.Debug("Skip empty photo translation result.");
                return;
            }

            QueueUi(() =>
            {
                EnsurePhotoTranslationWindowVisible();
                PhotoTranslationResults.Insert(0, item);
                TrimCollection(PhotoTranslationResults, 80);
                SetCurrentPhotoTranslationResultIndex(0);
                OnPropertyChanged(nameof(HasPhotoTranslationResults));
                _logger.Debug(
                    "Queued photo translation result. mode={Mode}, hasText={HasText}, imagePath={ImagePath}",
                    item.DisplayMode,
                    !string.IsNullOrWhiteSpace(item.DisplayText),
                    item.ResultImagePath);
                RequestSteamVrOverlayUpdate();
            }, DispatcherPriority.Background);

            var logMessage = !string.IsNullOrWhiteSpace(item.DisplayText)
                ? item.DisplayText.Replace(Environment.NewLine, " | ")
                : item.ResultImagePath;
            AddLog("Photo", logMessage);
        }

        private void EnsurePhotoTranslationWindowVisible()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(EnsurePhotoTranslationWindowVisible), DispatcherPriority.Background);
                return;
            }

            if (_photoTranslationWindow == null)
            {
                _photoTranslationWindow = new PhotoTranslationWindow
                {
                    DataContext = this,
                    Owner = Application.Current?.MainWindow
                };
                _photoTranslationWindow.Closed += OnPhotoTranslationWindowClosed;
            }

            if (!_photoTranslationWindow.IsVisible)
            {
                _photoTranslationWindow.Show();
            }

            if (_photoTranslationWindow.WindowState == WindowState.Minimized)
            {
                _photoTranslationWindow.WindowState = WindowState.Normal;
            }

            _photoTranslationWindow.Activate();
        }

        private void OnPhotoTranslationWindowClosed(object? sender, EventArgs e)
        {
            _photoTranslationWindow = null;
            if (_closingPhotoTranslationWindow)
            {
                return;
            }

            StopPhotoTranslationSession(closeWindow: false, addLog: true, "已关闭拍照翻译窗口，停止监听 VRChat 照片。");
        }

        private void OpenPhotoTranslationSession()
        {
            _isPhotoTranslationSessionOpen = true;
            EnsurePhotoTranslationWindowVisible();
            RequestSteamVrOverlayUpdate();
        }

        private void StopPhotoTranslationSession(bool closeWindow, bool addLog, string? logMessage = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.BeginInvoke(
                        new Action(() => StopPhotoTranslationSession(closeWindow, addLog, logMessage)),
                        DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to marshal photo translation stop to UI thread.");
                }
                return;
            }

            try
            {
                _isPhotoTranslationSessionOpen = false;
                _photoTranslationService.Stop();
                IsPhotoTranslationArmed = false;
                IsPhotoTranslationProcessing = false;

                if (closeWindow)
                {
                    ClosePhotoTranslationWindow();
                }

                if (addLog && !string.IsNullOrWhiteSpace(logMessage))
                {
                    AddLog("Photo", logMessage, "Info");
                }

                RequestSteamVrOverlayUpdate();
            }
            catch (Exception ex)
            {
                AddLog("Photo", $"停止拍照翻译失败: {ex.Message}", "Warning");
                _logger.Warning(ex, "Failed to stop photo translation session.");
                RequestSteamVrOverlayUpdate();
            }
        }

        private void ClosePhotoTranslationWindow()
        {
            var window = _photoTranslationWindow;
            if (window == null)
            {
                return;
            }

            _closingPhotoTranslationWindow = true;
            try
            {
                window.Close();
            }
            finally
            {
                _closingPhotoTranslationWindow = false;
                if (ReferenceEquals(_photoTranslationWindow, window))
                {
                    _photoTranslationWindow = null;
                }
            }
        }

        private static void RefreshSteamVrSettingsViewModel()
        {
            try
            {
                var settingsViewModel = App.GetRequiredService<SettingsViewModel>();
                settingsViewModel.ReloadFromConfig();
            }
            catch
            {
            }
        }

        private static void RefreshSteamVrLanguageSettingsViewModel()
        {
            try
            {
                var settingsViewModel = App.GetRequiredService<SettingsViewModel>();
                settingsViewModel.ReloadTranslationLanguageSettingsFromConfig();
            }
            catch
            {
            }
        }

        private static void RefreshSteamVrTtsSettingsViewModel()
        {
            try
            {
                var settingsViewModel = App.GetRequiredService<SettingsViewModel>();
                settingsViewModel.ReloadTtsSettingsFromConfig();
            }
            catch
            {
            }
        }

        private void ToggleSteamVrTtsSpeakMask(bool isDesktop, int bit)
        {
            if (bit is not (1 or 2 or 4 or 8))
            {
                return;
            }

            if (isDesktop)
            {
                _config.DesktopTtsSpeakMask = ToggleMaskBit(_config.DesktopTtsSpeakMask, bit);
            }
            else
            {
                _config.MicTtsSpeakMask = ToggleMaskBit(_config.MicTtsSpeakMask, bit);
            }

            ClearPendingTts("steamvr tts content changed");
            PersistConfigSafe();
            RefreshSteamVrTtsSettingsViewModel();
        }

        private static int ToggleMaskBit(int mask, int bit)
        {
            return (mask & bit) != 0
                ? mask & ~bit
                : mask | bit;
        }

        private void OnSteamVrConnected(object? sender, EventArgs e)
        {
            _logger.Information("SteamVR Overlay 已连接");
        }
        private void OnSteamVrDisconnected(object? sender, EventArgs e)
        {
            _logger.Information("SteamVR Overlay 已断开");
            StopPhotoTranslationSession(closeWindow: true, addLog: false);
        }
        private void UpdateSteamVrOverlayPanel()
        {
            try
            {
                if (!_config.TextInSteamVr) return;

                var sourceMask = NormalizeSourceMask(_config.SteamVrPanelSourceMask);
                var languageMask = NormalizeLanguageMask(_config.SteamVrPanelLanguageMask);

                // Keep it small to avoid excessive allocations / uploads.
                var mainItems = Transcripts
                    .Where(t => SourceMatchesMask(t.Source ?? string.Empty, sourceMask))
                    .Take(30)
                    .Select(t => new SteamVrOverlayTranscript
                    {
                        Source = t.Source ?? string.Empty,
                        Original = t.Original ?? string.Empty,
                        Translation = t.Translation ?? string.Empty,
                        Translation2 = t.Translation2 ?? string.Empty,
                        Translation3 = t.Translation3 ?? string.Empty,
                        PhotoTranslationMode = t.PhotoTranslationMode ?? string.Empty,
                        PhotoImagePath = t.PhotoImagePath ?? string.Empty,
                        Timestamp = t.Timestamp
                    })
                    .ToArray();
                var includePhotoItems = _isPhotoTranslationSessionOpen
                    || IsPhotoTranslationArmed
                    || IsPhotoTranslationProcessing;
                var photoItems = includePhotoItems
                    ? PhotoTranslationResults
                        .Take(12)
                        .Select(t => new SteamVrOverlayTranscript
                        {
                            Source = "Photo",
                            Original = t.Original ?? string.Empty,
                            Translation = t.Translation ?? string.Empty,
                            Translation2 = t.Translation2 ?? string.Empty,
                            Translation3 = t.Translation3 ?? string.Empty,
                            PhotoTranslationMode = t.DisplayMode ?? string.Empty,
                            PhotoImagePath = t.ResultImagePath ?? string.Empty,
                            Timestamp = t.Timestamp
                        })
                        .ToArray()
                    : Array.Empty<SteamVrOverlayTranscript>();
                var items = mainItems
                    .Concat(photoItems)
                    .ToArray();

                _steamVrService.UpdatePanel(new SteamVrOverlayPanelState
                {
                    IsMicListening = IsMicListening,
                    IsDesktopListening = IsDesktopListening,
                    IsPhotoTranslationArmed = IsPhotoTranslationArmed,
                    IsPhotoTranslationProcessing = IsPhotoTranslationProcessing,
                    MicVolume = MicVolume,
                    DesktopVolume = DesktopVolume,
                    IsTtsEnabled = _config.TtsToggle != 0,
                    IsTtsMicPassthroughEnabled = _config.TtsMicPassthroughEnabled,
                    TtsMicPassthroughVolume = _config.TtsMicPassthroughVolume,
                    MicTtsSpeakMask = _config.MicTtsSpeakMask,
                    DesktopTtsSpeakMask = _config.DesktopTtsSpeakMask,
                    NonTextOpacity = Math.Clamp(_config.SteamVrNonTextOpacity, 0f, 1f),
                    FontScale = Math.Clamp(_config.SteamVrFontScale, 0.70f, 1.40f),
                    MicTextColor = AppConfig.NormalizeHexColor(_config.SteamVrMicTextColor, AppConfig.DefaultSteamVrMicTextColor),
                    DesktopTextColor = AppConfig.NormalizeHexColor(_config.SteamVrDesktopTextColor, AppConfig.DefaultSteamVrDesktopTextColor),
                    SourceMask = sourceMask,
                    LanguageMask = languageMask,
                    UiLanguage = _config.UiLanguage,
                    MicSourceLanguage = _config.ResolveSourceLanguage("Mic"),
                    MicTargetLanguage = _config.ResolvePrimaryTargetLanguage("Mic"),
                    MicTargetLanguage2 = _config.ResolveSecondaryTargetLanguage("Mic"),
                    MicTargetLanguage3 = _config.ResolveTertiaryTargetLanguage("Mic"),
                    DesktopSourceLanguage = _config.ResolveSourceLanguage("Desktop"),
                    DesktopTargetLanguage = _config.ResolvePrimaryTargetLanguage("Desktop"),
                    DesktopTargetLanguage2 = _config.ResolveSecondaryTargetLanguage("Desktop"),
                    DesktopTargetLanguage3 = _config.ResolveTertiaryTargetLanguage("Desktop"),
                    Transcripts = items
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to update SteamVR overlay panel.");
            }
        }
        private void RequestSteamVrOverlayUpdate()
        {
            if (!_config.TextInSteamVr) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(RequestSteamVrOverlayUpdate), DispatcherPriority.Background);
                return;
            }
            var now = DateTime.UtcNow;
            lock (_steamVrUpdateLock)
            {
                var elapsed = now - _lastSteamVrUpdateUtc;
                if (elapsed >= SteamVrUpdateMinInterval)
                {
                    _lastSteamVrUpdateUtc = now;
                    FlushPendingUiItems();
                    UpdateSteamVrOverlayPanel();
                    return;
                }
                _steamVrUpdateCts?.Cancel();
                _steamVrUpdateCts?.Dispose();
                _steamVrUpdateCts = new CancellationTokenSource();
                var token = _steamVrUpdateCts.Token;
                var delay = SteamVrUpdateMinInterval - elapsed;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;
                        QueueUi(() =>
                        {
                            lock (_steamVrUpdateLock)
                            {
                                _lastSteamVrUpdateUtc = DateTime.UtcNow;
                            }
                            FlushPendingUiItems();
                            UpdateSteamVrOverlayPanel();
                        }, DispatcherPriority.Background);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                });
            }
        }
        private void OnAuthStateChanged(object? sender, BackendAuthState state)
        {
            if (_lastRecognitionAuthMode != state.AuthMode)
            {
                _logger.Information(
                    "Recognition auth mode changed. from={PreviousMode}, to={CurrentMode}. Restarting pipelines to isolate sessions.",
                    _lastRecognitionAuthMode,
                    state.AuthMode);
                _lastRecognitionAuthMode = state.AuthMode;
                QueuePipelineRestart(restartMic: true, restartDesktop: true);
            }

            QueueUi(() =>
            {
                UpdateConnectionState(state);
                if (ShouldRefreshWebsiteAccountStatus(state))
                {
                    ClearAuthFailureAlert();
                    _ = RefreshAccountStatusAsync();
                    return;
                }

                if (state.Status == BackendAuthStatus.LoggedIn && state.HasToken)
                {
                    ClearAuthFailureAlert();
                    ClearAccountReminderAlert();
                    return;
                }

                var reminder = BuildAuthFailureReminder(state);
                if (!string.IsNullOrWhiteSpace(reminder))
                {
                    _lastAuthFailureReminder = reminder;
                    ShowHomeAlert(reminder, autoHide: false);
                    return;
                }

                ClearAuthFailureAlert();
            }, DispatcherPriority.Background);
        }

        private void OnAuthUserNotification(object? sender, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (IsRateLimitMessage(message))
            {
                return;
            }

            AddLog("Server", message, "Warning");
            QueueUi(() => ShowHomeAlert(message, autoHide: true), DispatcherPriority.Background);
        }

        private void OnServiceError(object? sender, ServiceErrorEvent error)
        {
            if (error == null)
            {
                return;
            }

            switch (error.Code)
            {
                case ServiceErrorCode.RateLimit:
                    QueueUi(() => HandleRateLimitNotification(error), DispatcherPriority.Background);
                    return;

                case ServiceErrorCode.AudioCaptureStartFailed:
                    HandleAudioCaptureStartFailed(error);
                    return;

                case ServiceErrorCode.AudioCaptureStoppedUnexpectedly:
                    HandleAudioCaptureStoppedUnexpectedly(error);
                    return;

                case ServiceErrorCode.AudioCaptureDeviceFallback:
                    HandleAudioCaptureDeviceFallback(error);
                    return;

                case ServiceErrorCode.AudioProcessingRepeatedFailures:
                    HandleAudioProcessingRepeatedFailures(error);
                    return;

                case ServiceErrorCode.RecognitionAuthRequired:
                    NotifyServiceWarning("Recognition", "识别服务需要登录账户。");
                    return;

                case ServiceErrorCode.RecognitionAuthExpired:
                    NotifyServiceWarning("Recognition", "识别服务登录状态已过期，请重新登录后再试。");
                    return;

                case ServiceErrorCode.RecognitionFailed:
                {
                    var reason = ResolveServiceErrorReason(error);
                    NotifyServiceWarning("Recognition", string.IsNullOrWhiteSpace(reason)
                        ? "本地语音识别失败。"
                        : $"本地语音识别失败：{reason}");
                    return;
                }

                case ServiceErrorCode.TtsRateLimit:
                    return;

                case ServiceErrorCode.TtsAuthRequired:
                    NotifyServiceWarning("TTS", "使用云端语音合成前请先登录账户。");
                    return;

                case ServiceErrorCode.TtsAuthExpired:
                    NotifyServiceWarning("TTS", "登录状态已过期，请重新登录后再试。");
                    return;

                case ServiceErrorCode.TtsClientUpgradeRequired:
                    NotifyTtsClientUpgradeRequired(error);
                    return;

                case ServiceErrorCode.TtsHttpError:
                {
                    var statusCode = error.GetContext("StatusCode");
                    NotifyServiceWarning("TTS", FormatTtsFailureMessage(statusCode, ResolveServiceErrorReason(error)));
                    return;
                }

                case ServiceErrorCode.TtsFailed:
                    NotifyServiceWarning("TTS", FormatTtsFailureMessage(null, ResolveServiceErrorReason(error)));
                    return;
            }
        }

        private void HandleAudioCaptureStartFailed(ServiceErrorEvent error)
        {
            var source = ResolveAudioErrorSource(error);
            var reason = ResolveServiceErrorReason(error);
            var message = string.IsNullOrWhiteSpace(reason)
                ? $"{source}启动失败。"
                : $"{source}启动失败：{reason}";

            AddLog("Audio", message, "Error");
        }

        private void HandleAudioCaptureStoppedUnexpectedly(ServiceErrorEvent error)
        {
            var source = ResolveAudioErrorSource(error);
            var reason = ResolveServiceErrorReason(error);
            var message = string.IsNullOrWhiteSpace(reason)
                ? $"{source}录音异常停止，正在尝试自动恢复。"
                : $"{source}录音异常停止，正在尝试自动恢复：{reason}";

            NotifyServiceWarning("Audio", message);
            QueueAudioPipelineRestartFromError(error);
        }

        private void HandleAudioCaptureDeviceFallback(ServiceErrorEvent error)
        {
            var source = ResolveAudioErrorSource(error);
            var requestedDevice = error.GetContext("RequestedDevice");
            var message = string.IsNullOrWhiteSpace(requestedDevice)
                ? $"{source}设备不可用，已回退系统默认设备。"
                : $"{source}设备“{requestedDevice}”不可用，已回退系统默认设备。";

            NotifyServiceWarning("Audio", message);
        }

        private void HandleAudioProcessingRepeatedFailures(ServiceErrorEvent error)
        {
            var source = ResolveAudioErrorSource(error);
            var reason = ResolveServiceErrorReason(error);
            var message = string.IsNullOrWhiteSpace(reason)
                ? $"{source}音频处理连续失败，正在尝试自动恢复。"
                : reason.Contains(source, StringComparison.OrdinalIgnoreCase)
                    ? reason
                    : $"{source}音频处理连续失败，正在尝试自动恢复：{reason}";

            NotifyServiceWarning("Audio", message);
            QueueAudioPipelineRestartFromError(error);
        }

        private void QueueAudioPipelineRestartFromError(ServiceErrorEvent error)
        {
            var pipeline = error.GetContext("Pipeline");
            if (string.Equals(pipeline, "Mic", StringComparison.OrdinalIgnoreCase))
            {
                QueuePipelineRestart(restartMic: true, restartDesktop: false);
                return;
            }

            if (string.Equals(pipeline, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                QueuePipelineRestart(restartMic: false, restartDesktop: true);
            }
        }

        private static string ResolveAudioErrorSource(ServiceErrorEvent error)
        {
            var source = error.GetContext("Source");
            if (!string.IsNullOrWhiteSpace(source))
            {
                return source.Trim();
            }

            var pipeline = error.GetContext("Pipeline");
            return string.Equals(pipeline, "Mic", StringComparison.OrdinalIgnoreCase)
                ? "麦克风"
                : "监听音频";
        }

        private static string FormatTtsFailureMessage(string? statusCode, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return string.IsNullOrWhiteSpace(statusCode)
                    ? "语音合成失败，请稍后重试。"
                    : $"语音合成失败（HTTP {statusCode}）。";
            }

            if (reason.StartsWith("语音合成失败", StringComparison.OrdinalIgnoreCase))
            {
                return reason;
            }

            return string.IsNullOrWhiteSpace(statusCode)
                ? $"语音合成失败：{reason}"
                : $"语音合成失败（HTTP {statusCode}）：{reason}";
        }

        private void NotifyTtsClientUpgradeRequired(ServiceErrorEvent error)
        {
            var message = ResolveServiceErrorReason(error);
            if (string.IsNullOrWhiteSpace(message))
            {
                message = "请升级新版客户端以支持 Opus 流式语音合成。";
            }

            AddLog("TTS", message, "Warning");
            QueueUi(() => ShowHomeAlert(message, autoHide: false), DispatcherPriority.Background);
        }

        private static string ResolveServiceErrorReason(ServiceErrorEvent error)
        {
            return NormalizeServiceErrorReason(error.GetContext("Reason") ?? error.Exception?.Message);
        }

        private static string NormalizeServiceErrorReason(string? reason)
        {
            var normalized = string.Join(
                " ",
                (reason ?? string.Empty)
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Replace("\t", " ", StringComparison.Ordinal)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length > 160)
            {
                normalized = normalized[..160].TrimEnd() + "...";
            }

            return normalized;
        }

        private void NotifyServiceWarning(string source, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            AddLog(source, message, "Warning");
            QueueUi(() => ShowHomeAlert(message, autoHide: true), DispatcherPriority.Background);
        }

        private void HandleRateLimitNotification(string message)
        {
            var info = ParseRateLimitNotification(message);
            ApplyRateLimitNotification(info);
        }

        private void HandleRateLimitNotification(ServiceErrorEvent error)
        {
            var info = ParseRateLimitNotification(error);
            ApplyRateLimitNotification(info);
        }

        private void ApplyRateLimitNotification(RateLimitNotificationInfo info)
        {
            if (info == null)
            {
                return;
            }

            var changed = false;
            string combinedMessage;

            lock (_rateLimitNoticeLock)
            {
                if (!string.IsNullOrWhiteSpace(info.Source))
                {
                    changed |= _rateLimitSources.Add(info.Source);
                }

                foreach (var rule in info.Rules)
                {
                    changed |= _rateLimitRules.Add(rule);
                }

                combinedMessage = BuildRateLimitNoticeMessage();
            }

            var shouldLog = changed || !string.Equals(RateLimitNoticeMessage, combinedMessage, StringComparison.Ordinal);
            RateLimitNoticeMessage = combinedMessage;

            if (shouldLog)
            {
                AddLog("Server", combinedMessage, "Warning");
            }

            ApplyRateLimitLiveState(info);

            if (!ShouldAppendRateLimitedTranscript(info))
            {
                return;
            }

            EnqueueTranscript(new TranscriptItem
            {
                Source = BuildRateLimitedTranscriptSource(info),
                Original = BuildRateLimitedTranscriptOriginal(info),
                Translation = BuildRateLimitedTranscriptMessage(info),
                Translation2 = string.Empty,
                Translation3 = string.Empty,
                StatusTag = GetUiString("RateLimitStatusTag", "Triggered · Rate limited"),
                RequestStage = NormalizeRateLimitStage(info.Stage),
                IsRateLimited = true,
                IsIncomplete = true,
                Timestamp = DateTime.Now
            });
        }

        private string BuildRateLimitNoticeMessage()
        {
            var defaultMessage = GetUiString("RateLimitNoticeDefault", "Too many requests. Please try again later.");
            var suffix = GetUiString("RateLimitNoticeSuffix", "If you need to unlock request quota, click Donate and support server operations via Afdian in User Center, or contact the server administrator.");
            var sourcePrefix = GetUiString("RateLimitSourcePrefix", "Triggered by");
            var rulePrefix = GetUiString("RateLimitRulePrefix", "Limited rules");

            var lines = new List<string> { defaultMessage };
            if (_rateLimitSources.Count > 0)
            {
                lines.Add($"{sourcePrefix}: {string.Join("\u3001", _rateLimitSources)}");
            }

            if (_rateLimitRules.Count > 0)
            {
                lines.Add($"{rulePrefix}: {string.Join("\uff1b", _rateLimitRules)}");
            }

            lines.Add(suffix);
            return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        private static RateLimitNotificationInfo ParseRateLimitNotification(string message)
        {
            var info = new RateLimitNotificationInfo();
            if (string.IsNullOrWhiteSpace(message))
            {
                return info;
            }

            if (message.StartsWith("[RATE_LIMIT]|", StringComparison.Ordinal))
            {
                var payload = message.Substring("[RATE_LIMIT]|".Length);
                foreach (var part in payload.Split('|'))
                {
                    if (part.StartsWith("source=", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Source = UnescapeRateLimitValue(part.Substring("source=".Length)).Trim();
                        continue;
                    }

                    if (part.StartsWith("rules=", StringComparison.OrdinalIgnoreCase))
                    {
                        var rawRules = UnescapeRateLimitValue(part.Substring("rules=".Length));
                        foreach (var rule in rawRules.Split(new[] { "||", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!string.IsNullOrWhiteSpace(rule))
                            {
                                info.Rules.Add(rule.Trim());
                            }
                        }

                        continue;
                    }

                    if (part.StartsWith("channel=", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Channel = UnescapeRateLimitValue(part.Substring("channel=".Length)).Trim();
                        continue;
                    }

                    if (part.StartsWith("stage=", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Stage = NormalizeRateLimitStage(UnescapeRateLimitValue(part.Substring("stage=".Length)));
                        continue;
                    }

                    if (part.StartsWith("original=", StringComparison.OrdinalIgnoreCase))
                    {
                        info.OriginalText = UnescapeRateLimitValue(part.Substring("original=".Length));
                        continue;
                    }

                    if (part.StartsWith("segmentId=", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(UnescapeRateLimitValue(part.Substring("segmentId=".Length)), out var segmentId))
                    {
                        info.SegmentId = segmentId;
                    }
                }
            }
            else
            {
                var markerIndex = message.IndexOf("\u8bf7\u6c42\u8fc7\u4e8e\u9891\u7e41", StringComparison.OrdinalIgnoreCase);
                if (markerIndex > 0)
                {
                    info.Source = message.Substring(0, markerIndex).Trim();
                }

                foreach (Match match in RateLimitRulePattern.Matches(message))
                {
                    var rule = match.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(rule))
                    {
                        info.Rules.Add(rule);
                    }
                }
            }

            var dedupRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            info.Rules = info.Rules
                .Select(r => r.Replace("  ", " ", StringComparison.Ordinal).Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r) && dedupRules.Add(r))
                .ToList();
            info.Channel = NormalizeRateLimitChannel(info.Channel, info.Stage);
            info.Stage = NormalizeRateLimitStage(info.Stage);
            return info;
        }

        private static RateLimitNotificationInfo ParseRateLimitNotification(ServiceErrorEvent error)
        {
            var info = new RateLimitNotificationInfo
            {
                Source = error.GetContext("Source")?.Trim() ?? string.Empty,
                Channel = error.GetContext("Channel")?.Trim() ?? string.Empty,
                Stage = NormalizeRateLimitStage(error.GetContext("Stage")),
                OriginalText = error.GetContext("OriginalText") ?? string.Empty,
                SegmentId = error.GetContextInt64("SegmentId")
            };

            var rawRules = error.GetContext("Rules");
            if (!string.IsNullOrWhiteSpace(rawRules))
            {
                foreach (var rule in rawRules.Split(new[] { "||", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(rule))
                    {
                        info.Rules.Add(rule.Trim());
                    }
                }
            }

            if (info.Rules.Count == 0)
            {
                var backendDetail = error.GetContext("BackendDetail");
                if (!string.IsNullOrWhiteSpace(backendDetail))
                {
                    foreach (Match match in RateLimitRulePattern.Matches(backendDetail))
                    {
                        var rule = match.Value.Trim();
                        if (!string.IsNullOrWhiteSpace(rule))
                        {
                            info.Rules.Add(rule);
                        }
                    }
                }
            }

            var dedupRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            info.Rules = info.Rules
                .Select(r => r.Replace("  ", " ", StringComparison.Ordinal).Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r) && dedupRules.Add(r))
                .ToList();
            info.Channel = NormalizeRateLimitChannel(info.Channel, info.Stage);
            return info;
        }

        private void ApplyRateLimitLiveState(RateLimitNotificationInfo info)
        {
            var stage = NormalizeRateLimitStage(info.Stage);
            if (!string.Equals(stage, "recognition", StringComparison.Ordinal)
                && !string.Equals(stage, "translation", StringComparison.Ordinal)
                && !string.Equals(stage, "textsend", StringComparison.Ordinal))
            {
                return;
            }

            var channel = NormalizeRateLimitChannel(info.Channel, info.Stage);
            var liveMessage = BuildRateLimitedLiveMessage(info);
            var originalText = info.OriginalText ?? string.Empty;

            switch (channel)
            {
                case "Desktop":
                    if (!string.Equals(stage, "recognition", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(originalText))
                    {
                        DesktopText = originalText;
                    }

                    DesktopTrans = liveMessage;
                    CurrentSource = "Desktop";
                    break;

                case "TextSend":
                    if (!string.IsNullOrWhiteSpace(originalText))
                    {
                        MicText = originalText;
                    }

                    MicTrans = liveMessage;
                    CurrentSource = "TextSend";
                    break;

                default:
                    if (!string.Equals(stage, "recognition", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(originalText))
                    {
                        MicText = originalText;
                    }

                    MicTrans = liveMessage;
                    CurrentSource = "Mic";
                    break;
            }
        }

        private bool ShouldAppendRateLimitedTranscript(RateLimitNotificationInfo info)
        {
            var stage = NormalizeRateLimitStage(info.Stage);
            return string.Equals(stage, "recognition", StringComparison.Ordinal)
                || string.Equals(stage, "translation", StringComparison.Ordinal)
                || string.Equals(stage, "textsend", StringComparison.Ordinal);
        }

        private string BuildRateLimitedTranscriptSource(RateLimitNotificationInfo info)
        {
            var channel = NormalizeRateLimitChannel(info.Channel, info.Stage);
            return channel switch
            {
                "Desktop" => "Desktop",
                "TextSend" => "TextSend",
                _ => "Mic"
            };
        }

        private string BuildRateLimitedTranscriptOriginal(RateLimitNotificationInfo info)
        {
            var stage = NormalizeRateLimitStage(info.Stage);
            if (string.Equals(stage, "translation", StringComparison.Ordinal)
                || string.Equals(stage, "textsend", StringComparison.Ordinal))
            {
                return info.OriginalText ?? string.Empty;
            }

            return string.Empty;
        }

        private string BuildRateLimitedTranscriptMessage(RateLimitNotificationInfo info)
        {
            return NormalizeRateLimitStage(info.Stage) switch
            {
                "recognition" => GetUiString("RateLimitRecognitionTriggered", "[Rate limited] Recognition was triggered, but the server returned no text."),
                "textsend" => GetUiString("RateLimitTextSendTriggered", "[Rate limited] Text translation was triggered, but no result was returned."),
                _ => GetUiString("RateLimitTranslationTriggered", "[Rate limited] Translation was triggered, but no translation result was returned.")
            };
        }

        private string BuildRateLimitedLiveMessage(RateLimitNotificationInfo info)
        {
            return NormalizeRateLimitStage(info.Stage) switch
            {
                "recognition" => GetUiString("RateLimitRecognitionLive", "[Rate limited] Recognition was triggered, but no text was returned."),
                _ => GetUiString("RateLimitTranslationLive", "[Rate limited] This translation did not complete.")
            };
        }

        private static string NormalizeRateLimitStage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().ToLowerInvariant();
        }

        private static string NormalizeRateLimitChannel(string? value, string? stage = null)
        {
            if (string.Equals(NormalizeRateLimitStage(stage), "textsend", StringComparison.Ordinal))
            {
                return "TextSend";
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return "Mic";
            }

            var normalized = value.Trim();
            if (string.Equals(normalized, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return "Desktop";
            }

            if (string.Equals(normalized, "TextSend", StringComparison.OrdinalIgnoreCase))
            {
                return "TextSend";
            }

            return "Mic";
        }

        private static string UnescapeRateLimitValue(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch
            {
                return value;
            }
        }

        private static bool IsRateLimitMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.StartsWith("[RATE_LIMIT]|", StringComparison.Ordinal)
                || message.Contains("\u8bf7\u6c42\u8fc7\u4e8e\u9891\u7e41", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too many request", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenDonationGuide()
        {
            OpenExternalUrl(BackendUrlHelper.ResolvePortalUrl(_config, "/billing"));
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

        private void CloseHomeAlert()
        {
            lock (_homeAlertLock)
            {
                _homeAlertCts?.Cancel();
                _homeAlertCts?.Dispose();
                _homeAlertCts = null;
            }
            ClearHomeAlert();
        }

        private void CloseRateLimitNotice()
        {
            lock (_rateLimitNoticeLock)
            {
                _rateLimitSources.Clear();
                _rateLimitRules.Clear();
            }
            RateLimitNoticeMessage = string.Empty;
        }

        private sealed class RateLimitNotificationInfo
        {
            public string Source { get; set; } = string.Empty;
            public List<string> Rules { get; set; } = new();
            public string Channel { get; set; } = string.Empty;
            public string Stage { get; set; } = string.Empty;
            public string OriginalText { get; set; } = string.Empty;
            public long? SegmentId { get; set; }
        }

        private static string GetUiString(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key) as string ?? fallback;
        }

        private void RefreshUiLanguageOptions()
        {
            var selected = App.NormalizeUiLanguage(_uiLanguage);
            UiLanguages.Clear();
            UiLanguages.Add(new LanguageOption("en-US", GetUiString("UiLanguage_en-US", "English")));
            UiLanguages.Add(new LanguageOption("zh-CN", GetUiString("UiLanguage_zh-CN", "Chinese")));
            UiLanguages.Add(new LanguageOption("ja-JP", GetUiString("UiLanguage_ja-JP", "Japanese")));
            UiLanguages.Add(new LanguageOption("fr-FR", GetUiString("UiLanguage_fr-FR", "French")));
            UiLanguages.Add(new LanguageOption("ru-RU", GetUiString("UiLanguage_ru-RU", "Russian")));
            _uiLanguage = selected;
            OnPropertyChanged(nameof(UiLanguage));
        }

        private void SyncSettingsUiLanguage(string normalized)
        {
            if (_settingsWindow == null)
            {
                return;
            }

            try
            {
                if (App.Services?.GetService(typeof(SettingsViewModel)) is SettingsViewModel settingsViewModel
                    && !string.Equals(settingsViewModel.UiLanguage, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    settingsViewModel.UiLanguage = normalized;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to sync settings UI language.");
            }
        }

        private void OnAccountStatusUpdated(object? sender, AccountStatusInfo status)
        {
            var reminder = BuildAccountReminder(status);
            if (string.IsNullOrWhiteSpace(reminder))
            {
                ClearAccountReminderAlert();
                return;
            }

            if (!string.Equals(reminder, _lastAccountReminder, StringComparison.Ordinal))
            {
                _lastAccountReminder = reminder;
                AddLog("Account", reminder, "Warning");
            }

            var shouldAutoHide = !(status.IsExpired || !status.IsActive);
            QueueUi(() => ShowHomeAlert(reminder, shouldAutoHide), DispatcherPriority.Background);
        }

        private string BuildAccountReminder(AccountStatusInfo status)
        {
            if (status.IsAnonymousMode)
            {
                return string.Empty;
            }

            if (!status.IsActive)
            {
                return GetUiString("AccountAbnormalReminder", "Account status requires attention. Please open the website User Center.");
            }

            if (status.IsExpired)
            {
                return GetUiString("AccountExpiredReminder", "Account has expired or fallen back to the free tier. Please open the website User Center for details.");
            }

            // 服务器返回的到期时间为 UTC（ExpirationUtc），统一在客户端用 UTC 计算剩余时间，
            // 避免本地时区与服务器 UTC 不一致导致的误差。
            TimeSpan? remaining = null;

            if (status.ExpirationUtc.HasValue)
            {
                var utcNow = DateTimeOffset.UtcNow;
                remaining = status.ExpirationUtc.Value - utcNow;
            }
            else if (status.RemainingSeconds.HasValue)
            {
                remaining = TimeSpan.FromSeconds(Math.Max(0, status.RemainingSeconds.Value));
            }

            // Some backends only return active/expired flags and omit expiration details for normal accounts.
            // In that case we should treat the account as healthy instead of surfacing a false "abnormal" alert.
            if (remaining is null)
            {
                return string.Empty;
            }

            if (remaining.Value <= TimeSpan.Zero)
            {
                return GetUiString("AccountExpiredReminder", "Account has expired or fallen back to the free tier. Please open the website User Center for details.");
            }

            var remainingValue = remaining.Value;
            if (remainingValue > TimeSpan.FromDays(7))
            {
                return string.Empty;
            }

            if (remainingValue >= TimeSpan.FromDays(1))
            {
                return $"Account expires in {Math.Floor(remainingValue.TotalDays)}d {remainingValue.Hours}h.";
            }

            return $"Account expires in {Math.Max(1, (int)Math.Ceiling(remainingValue.TotalHours))}h.";
        }

        private string BuildAuthFailureReminder(BackendAuthState state)
        {
            if (state.Status != BackendAuthStatus.Failed && state.Status != BackendAuthStatus.NotLoggedIn)
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(state.ErrorMessage))
            {
                return string.Empty;
            }

            var localizedError = AuthUiText.LocalizeErrorMessage(state.ErrorMessage);
            if (string.IsNullOrWhiteSpace(localizedError))
            {
                return string.Empty;
            }

            var prefix = GetUiString("AccountAbnormalReminder", "Account status requires attention. Please open the website User Center or sign in again.");
            return $"{prefix} ({localizedError})";
        }

        private async Task RefreshAccountStatusAsync()
        {
            if (!ShouldRefreshWebsiteAccountStatus(_authService.State))
            {
                ClearAccountReminderAlert();
                return;
            }

            try
            {
                await _authService.GetAccountStatusAsync();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to refresh account status.");
            }
        }

        private static bool ShouldRefreshWebsiteAccountStatus(BackendAuthState state)
        {
            return state.Status == BackendAuthStatus.LoggedIn &&
                   state.HasToken &&
                   state.IsWebsiteAuthorized;
        }

        private void ClearAuthFailureAlert()
        {
            if (string.IsNullOrWhiteSpace(_lastAuthFailureReminder))
            {
                return;
            }

            var reminder = _lastAuthFailureReminder;
            _lastAuthFailureReminder = string.Empty;
            ClearHomeAlertIfMatches(reminder);
        }

        private void ClearAccountReminderAlert()
        {
            if (string.IsNullOrWhiteSpace(_lastAccountReminder))
            {
                return;
            }

            var reminder = _lastAccountReminder;
            _lastAccountReminder = string.Empty;
            ClearHomeAlertIfMatches(reminder);
        }

        private void ClearHomeAlertIfMatches(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || !string.Equals(HomeAlertMessage, message, StringComparison.Ordinal))
            {
                return;
            }

            CloseHomeAlert();
        }

        private void ShowHomeAlert(string message, bool autoHide)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            HomeAlertMessage = message;

            lock (_homeAlertLock)
            {
                _homeAlertCts?.Cancel();
                _homeAlertCts?.Dispose();
                _homeAlertCts = null;

                if (!autoHide)
                {
                    return;
                }

                _homeAlertCts = new CancellationTokenSource();
                var token = _homeAlertCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(HomeAlertDuration, token);
                        if (!token.IsCancellationRequested)
                        {
                            QueueUi(ClearHomeAlert, DispatcherPriority.Background);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // ignored
                    }
                }, token);
            }
        }

        private void ClearHomeAlert()
        {
            HomeAlertMessage = string.Empty;
        }
        private void UpdateConnectionState(BackendAuthState state)
        {
            ConnectionState = state.Status switch
            {
                BackendAuthStatus.LoggedIn => "Connected",
                BackendAuthStatus.LoggingIn => "Connecting",
                _ => "Disconnected"
            };
            OnPropertyChanged(nameof(ConnectionStateText));
        }
        private void InitializePipelines()
        {
            _logger.Debug("Initialize audio pipelines.");
            try
            {
                CreateMicPipeline();
                CreateDesktopPipeline();
                EnsureMicCaptureStarted();
                EnsureDesktopCaptureStarted();
                ApplyListeningDefaultsForMode();
                AddLog("System", "音频管线已初始化并开始采集。");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to initialize audio pipelines.");
                AddLog("System", $"音频管线初始化失败：{ex.Message}", "Error");
            }
        }
        private void OnModelsChanged(object? sender, ModelHotReloadEventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => _ = ReloadPipelinesAfterModelUpdateAsync(e)));
                return;
            }
            _ = ReloadPipelinesAfterModelUpdateAsync(e);
        }
        private Task ReloadPipelinesAfterModelUpdateAsync(ModelHotReloadEventArgs args)
        {
            if (_isModelHotReloading)
            {
                return Task.CompletedTask;
            }
            _isModelHotReloading = true;
            try
            {
                AddLog("Model", $"Detected model update ({args.Reason}), reloading audio pipeline...");
                _micPipelineSignature = string.Empty;
                _desktopPipelineSignature = string.Empty;
                EnsureMicCaptureStarted(forceRestart: true);
                EnsureDesktopCaptureStarted(forceRestart: true);
                AddLog("Model", "Model hot reload completed.");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Model hot reload failed.");
                AddLog("Model", $"Model hot reload failed: {ex.Message}", "Warning");
            }
            finally
            {
                _isModelHotReloading = false;
            }
            return Task.CompletedTask;
        }
        private void InitializeHotkeys()
        {
            _logger.Debug("初始化热键系统");
            try
            {
                // ע���ȼ��¼�
                _hotkeyService.HotkeyPressed += OnHotkeyPressed;
                _hotkeyService.KeyStateChanged += OnKeyStateChanged;
                // ��������ע���ȼ�
                RegisterHotkeysFromConfig();
                // ����ȼ�����
                _hotkeyService.Start();
                AddLog("System", "热键系统初始化完成");
                _logger.Information("热键系统初始化成功");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "初始化热键系统失败");
                AddLog("System", $"初始化热键系统失败: {ex.Message}", "Error");
            }
        }
        private void RegisterHotkeysFromConfig()
        {
            // Mic mode: 1 = toggle, 2 = push-to-talk.
            if (_config.VoiceTriggerModeValue == VoiceTriggerMode.HotkeyToggle && !string.IsNullOrWhiteSpace(_config.VoiceHotKey))
            {
                _hotkeyService.RegisterHotkey(_config.VoiceHotKey, "mic_toggle");
                _logger.Debug("Register mic toggle hotkey: {Hotkey}", _config.VoiceHotKey);
            }
            else if (_config.VoiceTriggerModeValue == VoiceTriggerMode.PressToTalk && !string.IsNullOrWhiteSpace(_config.MicPressingKey))
            {
                _hotkeyService.RegisterPressKey(_config.MicPressingKey, "mic_press", 120);
                _logger.Debug("Register mic press key: {Key}", _config.MicPressingKey);
            }
            // Desktop mode: 1 = toggle, 2 = push-to-talk.
            if (_config.GameVoiceTriggerModeValue == VoiceTriggerMode.HotkeyToggle && !string.IsNullOrWhiteSpace(_config.GameVoiceHotKey))
            {
                _hotkeyService.RegisterHotkey(_config.GameVoiceHotKey, "desktop_toggle");
                _logger.Debug("Register desktop toggle hotkey: {Hotkey}", _config.GameVoiceHotKey);
            }
            else if (_config.GameVoiceTriggerModeValue == VoiceTriggerMode.PressToTalk && !string.IsNullOrWhiteSpace(_config.CapPressingKey))
            {
                _hotkeyService.RegisterPressKey(_config.CapPressingKey, "desktop_press", 120);
                _logger.Debug("Register desktop press key: {Key}", _config.CapPressingKey);
            }
        }
        private void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
        {
            _logger.Debug("Hotkey triggered: {Type} ({Id})", e.HotkeyType, e.HotkeyId);
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (e.HotkeyType)
                {
                    case "mic_toggle" when _config.VoiceTriggerModeValue == VoiceTriggerMode.HotkeyToggle:
                        ToggleMic();
                        AddLog("Hotkey", $"Hotkey toggled mic: {(IsMicListening ? "on" : "off")}");
                        break;
                    case "desktop_toggle" when _config.GameVoiceTriggerModeValue == VoiceTriggerMode.HotkeyToggle:
                        ToggleDesktop();
                        AddLog("Hotkey", $"Hotkey toggled desktop: {(IsDesktopListening ? "on" : "off")}");
                        break;
                }
            }));
        }
        private void OnKeyStateChanged(object? sender, KeyStateEventArgs e)
        {
            _logger.Debug("Key state changed: {Type} {Key} = {State}", e.KeyType, e.KeyName, e.IsPressed);
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (e.KeyType)
                {
                    case "mic_press" when _config.VoiceTriggerModeValue == VoiceTriggerMode.PressToTalk:
                        HandleMicPressKey(e.IsPressed);
                        break;
                    case "desktop_press" when _config.GameVoiceTriggerModeValue == VoiceTriggerMode.PressToTalk:
                        HandleDesktopPressKey(e.IsPressed);
                        break;
                }
            }));
        }
        private void HandleMicPressKey(bool isPressed)
        {
            if (isPressed && !IsMicListening)
            {
                StartMicCapture();
                AddLog("Hotkey", "按住说话: 麦克风开启");
            }
            else if (!isPressed && IsMicListening)
            {
                StopMicCapture();
                AddLog("Hotkey", "按住说话: 麦克风关闭");
            }
        }
        private void HandleDesktopPressKey(bool isPressed)
        {
            if (isPressed && !IsDesktopListening)
            {
                StartDesktopCapture();
                AddLog("Hotkey", "按住说话: 桌面开启");
            }
            else if (!isPressed && IsDesktopListening)
            {
                StopDesktopCapture();
                AddLog("Hotkey", "按住说话: 桌面关闭");
            }
        }
        private void InitializeCommandService()
        {
            _logger.Debug("初始化命令服务");
            try
            {
                _commandService.RegisterDefaultCommands();
                SyncCommandServiceState();
                ReloadExperimentalKeywordActions();
                _commandService.CommandExecuted += OnCommandExecuted;
                _commandService.ActionRequested += OnCommandActionRequested;
                AddLog("System", "实验性语音关键词操作已初始化");
                _logger.Information("实验性语音关键词操作初始化成功");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "初始化命令服务失败");
                AddLog("System", $"初始化命令服务失败: {ex.Message}", "Error");
            }
        }

        private void ReloadExperimentalKeywordActions()
        {
            try
            {
                _commandService.LoadUserScripts(_config.Scripts ?? new List<VoiceScript>());
                _logger.Information("已加载 {Count} 条实验性语音关键词规则", _config.Scripts?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "重新加载实验性语音关键词规则失败");
            }
        }
        private void SyncCommandServiceState()
        {
            try
            {
                _commandService.IsEnabled = _config.ControlEnabled;
                _logger.Information("命令服务状态已同步 - Enabled: {Enabled}", _commandService.IsEnabled);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "同步命令服务状态失败");
            }
        }
        private void OnCommandExecuted(object? sender, CommandExecutedEventArgs e)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (e.Success)
                {
                    AddLog("Command", $"执行关键词操作: {e.CommandName}");
                    _logger.Information("命令执行成功: {Command}", e.CommandName);
                    try
                    {
                        VRCLS.UI.App.SaveConfigToDisk(_config);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "保存配置失败(命令触发)");
                    }
                    RequestSteamVrOverlayUpdate();
                    // ���������������ʾ����򲻲��� TTS��������Ƶ�豸��ͻ��
                    // ֻ����δ������ʾ���������� TTS ʱ�Ų����������
                    if (!_config.CommandFeedbackEnabled && _config.TtsToggle != 0 && !string.IsNullOrEmpty(e.FeedbackText))
                    {
                        // ʹ�����Ĳ��ŷ���
                        await _ttsService.SpeakAsync(e.FeedbackText, language: "zh");
                    }
                }
                else
                {
                    AddLog("Command", $"命令失败: {e.CommandName} - {e.Message}", "Error");
                    _logger.Warning("命令执行失败: {Command}, 错误: {Error}", e.CommandName, e.Message);
                }
            }));
        }
        private void OnCommandActionRequested(object? sender, CommandActionEventArgs e)
        {
            try
            {
                if (e == null || e.Action == CommandActionType.None) return;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    switch (e.Action)
                    {
                        case CommandActionType.MicToggle:
                            ToggleMic();
                            break;
                        case CommandActionType.MicOn:
                            if (!IsMicListening) ToggleMic();
                            break;
                        case CommandActionType.MicOff:
                            if (IsMicListening) ToggleMic();
                            break;
                        case CommandActionType.DesktopToggle:
                            ToggleDesktop();
                            break;
                        case CommandActionType.DesktopOn:
                            if (!IsDesktopListening) ToggleDesktop();
                            break;
                        case CommandActionType.DesktopOff:
                            if (IsDesktopListening) ToggleDesktop();
                            break;
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Command action handling failed");
            }
        }
        private void StartMicCapture()
        {
            try
            {
                EnsureMicCaptureStarted();
                IsMicListening = true;
                _micDisableGraceUntilUtc = DateTime.MinValue;
                MarkCaptureStart("Mic");
                SetMicRecognitionEnabled(true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to enable mic listening.");
            }
            finally
            {
                RequestSteamVrOverlayUpdate();
            }
        }
        private void StopMicCapture()
        {
            IsMicListening = false;
            _micDisableGraceUntilUtc = DateTime.UtcNow + ResultDrainGrace;
            MarkCaptureStop("Mic");
            SetMicRecognitionEnabled(false, drainCurrentSpeech: true);
            RequestSteamVrOverlayUpdate();
        }
        private void StartDesktopCapture()
        {
            try
            {
                EnsureDesktopCaptureStarted();
                IsDesktopListening = true;
                _desktopDisableGraceUntilUtc = DateTime.MinValue;
                MarkCaptureStart("Desktop");
                if (IsDesktopApplicationCapturePending())
                {
                    SetDesktopRecognitionEnabled(false);
                    ScheduleDesktopApplicationTargetRetry();
                }
                else
                {
                    StopDesktopApplicationTargetRetry();
                    SetDesktopRecognitionEnabled(true);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to enable desktop listening.");
            }
            finally
            {
                RequestSteamVrOverlayUpdate();
            }
        }
        private void StopDesktopCapture()
        {
            IsDesktopListening = false;
            StopDesktopApplicationTargetRetry();
            _desktopDisableGraceUntilUtc = DateTime.UtcNow + ResultDrainGrace;
            MarkCaptureStop("Desktop");
            SetDesktopRecognitionEnabled(false, drainCurrentSpeech: true);
            RequestSteamVrOverlayUpdate();
        }
        private void SetMicListeningState(bool enabled, bool logStateChange)
        {
            if (enabled)
            {
                StartMicCapture();
            }
            else
            {
                StopMicCapture();
            }

            if (logStateChange)
            {
                AddLog("System", enabled ? "Mic listening enabled" : "Mic listening disabled");
            }
        }
        private void SetDesktopListeningState(bool enabled, bool logStateChange)
        {
            if (enabled)
            {
                StartDesktopCapture();
            }
            else
            {
                StopDesktopCapture();
            }

            if (logStateChange)
            {
                AddLog("System", enabled ? "Monitor audio enabled" : "Monitor audio disabled");
            }
        }
        private void ApplyListeningDefaultsForMode()
        {
            IsMicListening = _config.AutoStartMic;
            IsDesktopListening = _config.AutoStartDesktop;
            _micDisableGraceUntilUtc = DateTime.MinValue;
            _desktopDisableGraceUntilUtc = DateTime.MinValue;
            if (IsMicListening) MarkCaptureStart("Mic");
            if (IsDesktopListening) MarkCaptureStart("Desktop");
            SetMicRecognitionEnabled(IsMicListening);
            if (IsDesktopApplicationCapturePending())
            {
                SetDesktopRecognitionEnabled(false);
                if (IsDesktopListening)
                {
                    ScheduleDesktopApplicationTargetRetry();
                }
                else
                {
                    StopDesktopApplicationTargetRetry();
                }
            }
            else
            {
                SetDesktopRecognitionEnabled(IsDesktopListening);
            }
            RequestSteamVrOverlayUpdate();
        }
        private void SetMicRecognitionEnabled(bool enabled, bool drainCurrentSpeech = false)
        {
            try
            {
                _micPipeline?.SetRecognitionEnabled(enabled, drainCurrentSpeech);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to update mic recognition state.");
            }
        }
        private void SetDesktopRecognitionEnabled(bool enabled, bool drainCurrentSpeech = false)
        {
            try
            {
                _desktopPipeline?.SetRecognitionEnabled(enabled, drainCurrentSpeech);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to update desktop recognition state.");
            }
        }
        private void QueuePipelineRestart(bool restartMic, bool restartDesktop)
        {
            if (restartMic)
            {
                _pendingMicPipelineRestart = true;
            }
            if (restartDesktop)
            {
                _pendingDesktopPipelineRestart = true;
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                ApplyPendingPipelineRestart();
                return;
            }
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => QueuePipelineRestart(restartMic, restartDesktop)), DispatcherPriority.Background);
                return;
            }
            if (_pipelineRestartTimer == null)
            {
                _pipelineRestartTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = PipelineRestartDebounceInterval
                };
                _pipelineRestartTimer.Tick += OnPipelineRestartTimerTick;
            }
            _pipelineRestartTimer.Stop();
            _pipelineRestartTimer.Start();
        }
        private void OnPipelineRestartTimerTick(object? sender, EventArgs e)
        {
            _pipelineRestartTimer?.Stop();
            ApplyPendingPipelineRestart();
        }
        private void ApplyPendingPipelineRestart()
        {
            if (_isApplyingPipelineRestart)
            {
                if (_pipelineRestartTimer != null)
                {
                    _pipelineRestartTimer.Stop();
                    _pipelineRestartTimer.Start();
                }

                return;
            }

            var restartMic = _pendingMicPipelineRestart;
            var restartDesktop = _pendingDesktopPipelineRestart;
            _pendingMicPipelineRestart = false;
            _pendingDesktopPipelineRestart = false;

            if (!restartMic && !restartDesktop)
            {
                return;
            }

            _isApplyingPipelineRestart = true;
            try
            {
                if (restartMic)
                {
                    try
                    {
                        if (IsMicListening)
                        {
                            EnsureMicCaptureStarted(forceRestart: true);
                            SetMicRecognitionEnabled(true);
                        }
                        else
                        {
                            CreateMicPipeline();
                            SetMicRecognitionEnabled(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to apply pipeline settings (mic).");
                    }
                }
                if (restartDesktop)
                {
                    try
                    {
                        if (IsDesktopListening)
                        {
                            EnsureDesktopCaptureStarted(forceRestart: true);
                            if (IsDesktopApplicationCapturePending())
                            {
                                SetDesktopRecognitionEnabled(false);
                                ScheduleDesktopApplicationTargetRetry();
                            }
                            else
                            {
                                SetDesktopRecognitionEnabled(true);
                            }
                        }
                        else
                        {
                            CreateDesktopPipeline();
                            SetDesktopRecognitionEnabled(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to apply pipeline settings (desktop).");
                    }
                }
            }
            finally
            {
                _isApplyingPipelineRestart = false;
                if (_pendingMicPipelineRestart || _pendingDesktopPipelineRestart)
                {
                    QueuePipelineRestart(restartMic: false, restartDesktop: false);
                }
            }
        }
        private void EnsureMicCaptureStarted(bool forceRestart = false)
        {
            CreateMicPipeline();
            var pipeline = _micPipeline ?? throw new InvalidOperationException("Mic pipeline not initialized.");
            var deviceName = ResolveConfiguredMicCaptureDevice();
            var needRestart = forceRestart
                || !pipeline.IsCapturing
                || !string.Equals(_micCaptureDevice, deviceName, StringComparison.OrdinalIgnoreCase);
            if (!needRestart)
            {
                SetMicRecognitionEnabled(IsMicListening);
                return;
            }
            if (pipeline.IsCapturing)
            {
                pipeline.StopCapture();
            }
            pipeline.StartCapture(new AudioCaptureRequest
            {
                DeviceId = deviceName,
                IsLoopback = false
            });
            _micCaptureDevice = deviceName;
            SetMicRecognitionEnabled(IsMicListening);
            _logger.Information("Mic capture running on device: {Device}", deviceName);
        }
        private string ResolveConfiguredMicCaptureDevice()
        {
            var deviceName = string.IsNullOrWhiteSpace(_config.MicName) ? DefaultDeviceConfigValue : _config.MicName;
            return string.Equals(deviceName, DefaultDeviceDisplayName, StringComparison.OrdinalIgnoreCase)
                ? DefaultDeviceConfigValue
                : deviceName;
        }
        private void EnsureMicPassthroughState(bool forceRestart = false)
        {
            try
            {
                if (!_config.TtsMicPassthroughEnabled || _config.TtsToggle == 0)
                {
                    _microphonePassthroughService.Stop();
                    return;
                }

                var micDevice = ResolveConfiguredMicCaptureDevice();
                var outputDevice = string.IsNullOrWhiteSpace(_config.TtsOutputName)
                    ? DefaultDeviceConfigValue
                    : _config.TtsOutputName;

                _microphonePassthroughService.SetVolume(_config.TtsMicPassthroughVolume);
                if (!forceRestart
                    && _microphonePassthroughService.IsRunning
                    && string.Equals(_microphonePassthroughService.MicDeviceName, micDevice, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_microphonePassthroughService.OutputDeviceName, outputDevice, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _microphonePassthroughService.Start(micDevice, outputDevice);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to update microphone passthrough.");
                AddLog("TTS", $"麦克风原声透传启动失败：{ex.Message}", "Warning");
            }
        }
        private void EnsureDesktopCaptureStarted(bool forceRestart = false)
        {
            CreateDesktopPipeline();
            var pipeline = _desktopPipeline ?? throw new InvalidOperationException("Desktop pipeline not initialized.");
            var captureRequest = BuildDesktopCaptureRequest(allowDeferredApplicationTarget: true);
            if (captureRequest == null)
            {
                if (pipeline.IsCapturing)
                {
                    pipeline.StopCapture();
                }

                _desktopCaptureDevice = DesktopApplicationPendingCaptureKey;
                SetDesktopRecognitionEnabled(false);
                _logger.Information("Desktop capture deferred until the configured application is running.");
                if (IsDesktopListening)
                {
                    ScheduleDesktopApplicationTargetRetry();
                }
                return;
            }

            var captureKey = BuildDesktopCaptureKey(captureRequest);
            var needRestart = forceRestart
                || !pipeline.IsCapturing
                || !string.Equals(_desktopCaptureDevice, captureKey, StringComparison.OrdinalIgnoreCase);
            if (!needRestart)
            {
                SetDesktopRecognitionEnabled(IsDesktopListening);
                return;
            }
            if (pipeline.IsCapturing)
            {
                pipeline.StopCapture();
            }
            pipeline.StartCapture(captureRequest);
            _desktopCaptureDevice = captureKey;
            StopDesktopApplicationTargetRetry();
            SetDesktopRecognitionEnabled(IsDesktopListening);
            _logger.Information("Desktop capture running. Target={Target}", captureKey);
        }
        private bool IsDesktopApplicationCapturePending()
            => string.Equals(_desktopCaptureDevice, DesktopApplicationPendingCaptureKey, StringComparison.OrdinalIgnoreCase);

        private void ScheduleDesktopApplicationTargetRetry()
        {
            if (_config.DesktopAudioSourceModeValue != DesktopAudioSourceMode.Application || !IsDesktopListening)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ScheduleDesktopApplicationTargetRetry), DispatcherPriority.Background);
                return;
            }

            if (_desktopApplicationTargetRetryTimer == null)
            {
                _desktopApplicationTargetRetryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = DesktopApplicationTargetRetryInterval
                };
                _desktopApplicationTargetRetryTimer.Tick += OnDesktopApplicationTargetRetryTick;
            }

            if (!_desktopApplicationTargetRetryTimer.IsEnabled)
            {
                _desktopApplicationTargetRetryTimer.Start();
            }
        }

        private void StopDesktopApplicationTargetRetry()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(StopDesktopApplicationTargetRetry), DispatcherPriority.Background);
                return;
            }

            _desktopApplicationTargetRetryTimer?.Stop();
        }

        private void OnDesktopApplicationTargetRetryTick(object? sender, EventArgs e)
        {
            if (!IsDesktopListening || _config.DesktopAudioSourceModeValue != DesktopAudioSourceMode.Application)
            {
                StopDesktopApplicationTargetRetry();
                return;
            }

            try
            {
                EnsureDesktopCaptureStarted(forceRestart: true);
                if (!IsDesktopApplicationCapturePending())
                {
                    StopDesktopApplicationTargetRetry();
                    SetDesktopRecognitionEnabled(true);
                    AddLog("System", "已检测到目标应用，监听音频已自动恢复。");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Retrying desktop application audio target failed.");
            }
        }
        private async void OnMicTextRecognized(object? sender, TranscriptionResult result)
        {
            try
            {
                await HandleRecognitionResultAsync(result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Mic recognition handler failed.");
            }
        }

        private async void OnDesktopTextRecognized(object? sender, TranscriptionResult result)
        {
            try
            {
                await HandleRecognitionResultAsync(result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Desktop recognition handler failed.");
            }
        }

        private void OnMicPipelineError(object? sender, ServiceErrorEvent error)
        {
            OnServiceError(sender, error);
        }

        private void OnDesktopPipelineError(object? sender, ServiceErrorEvent error)
        {
            OnServiceError(sender, error);
        }

        private void OnMicVolumeLevelChanged(object? sender, VolumeLevelChangedEventArgs e)
        {
            var now = DateTime.UtcNow;
            if (now - _lastMicVolumeUiUtc < VolumeUiInterval) return;
            _lastMicVolumeUiUtc = now;
            QueueUi(() => MicVolume = e.Level, DispatcherPriority.Background);
        }

        private void OnDesktopVolumeLevelChanged(object? sender, VolumeLevelChangedEventArgs e)
        {
            var now = DateTime.UtcNow;
            if (now - _lastDesktopVolumeUiUtc < VolumeUiInterval) return;
            _lastDesktopVolumeUiUtc = now;
            QueueUi(() => DesktopVolume = e.Level, DispatcherPriority.Background);
        }

        private void OnMicSpeechStateChanged(object? sender, SpeechStateChangedEventArgs e)
            => QueueUi(() => IsMicSpeaking = IsMicListening && e.IsSpeaking, DispatcherPriority.Background);

        private void OnDesktopSpeechStateChanged(object? sender, SpeechStateChangedEventArgs e)
            => QueueUi(() => IsDesktopSpeaking = IsDesktopListening && e.IsSpeaking, DispatcherPriority.Background);

        private void UnsubscribePipelineEvents(AudioPipelineService? pipeline, bool isMic)
        {
            if (pipeline == null) return;

            if (isMic)
            {
                pipeline.TextRecognized -= OnMicTextRecognized;
                pipeline.VolumeLevelChanged -= OnMicVolumeLevelChanged;
                pipeline.SpeechStateChanged -= OnMicSpeechStateChanged;
                pipeline.ErrorOccurred -= OnMicPipelineError;
                if (_micVadCalibrationSubscribed)
                {
                    pipeline.VadConfidenceMeasured -= OnMicVadConfidenceMeasured;
                    _micVadCalibrationSubscribed = false;
                }
            }
            else
            {
                pipeline.TextRecognized -= OnDesktopTextRecognized;
                pipeline.VolumeLevelChanged -= OnDesktopVolumeLevelChanged;
                pipeline.SpeechStateChanged -= OnDesktopSpeechStateChanged;
                pipeline.ErrorOccurred -= OnDesktopPipelineError;
                if (_desktopVadCalibrationSubscribed)
                {
                    pipeline.VadConfidenceMeasured -= OnDesktopVadConfidenceMeasured;
                    _desktopVadCalibrationSubscribed = false;
                }
            }
        }

        private void CreateMicPipeline()
        {
            var signature = BuildPipelineSignature("Mic", false);
            if (_micPipeline != null && string.Equals(_micPipelineSignature, signature, StringComparison.Ordinal))
            {
                _logger.Debug("Mic pipeline reused. signature={Signature}", signature);
                return;
            }

            try
            {
                UnsubscribePipelineEvents(_micPipeline, isMic: true);
                _micPipeline?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose old mic pipeline; rebuilding.");
            }

            _micPipeline = _pipelineFactory.CreatePipeline("Mic", false);
            _micPipelineSignature = signature;
            _logger.Debug("Mic pipeline created. signature={Signature}", signature);
            _micPipeline.TextRecognized += OnMicTextRecognized;
            _micPipeline.VolumeLevelChanged += OnMicVolumeLevelChanged;
            _micPipeline.SpeechStateChanged += OnMicSpeechStateChanged;
            _micPipeline.ErrorOccurred += OnMicPipelineError;
            UpdateVadCalibrationSubscription("Mic");
        }

        private void CreateDesktopPipeline()
        {
            var signature = BuildPipelineSignature("Desktop", true);
            if (_desktopPipeline != null && string.Equals(_desktopPipelineSignature, signature, StringComparison.Ordinal))
            {
                _logger.Debug("Desktop pipeline reused. signature={Signature}", signature);
                return;
            }

            try
            {
                UnsubscribePipelineEvents(_desktopPipeline, isMic: false);
                _desktopPipeline?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose old desktop pipeline; rebuilding.");
            }

            _desktopPipeline = _pipelineFactory.CreatePipeline("Desktop", true);
            _desktopPipelineSignature = signature;
            _logger.Debug("Desktop pipeline created. signature={Signature}", signature);
            _desktopPipeline.TextRecognized += OnDesktopTextRecognized;
            _desktopPipeline.VolumeLevelChanged += OnDesktopVolumeLevelChanged;
            _desktopPipeline.SpeechStateChanged += OnDesktopSpeechStateChanged;
            _desktopPipeline.ErrorOccurred += OnDesktopPipelineError;
            UpdateVadCalibrationSubscription("Desktop");
        }

        private void OnVadCalibrationMonitoringChanged(object? sender, VadCalibrationMonitoringChangedEventArgs e)
        {
            QueueUi(() => UpdateVadCalibrationSubscription(e.Source), DispatcherPriority.Background);
        }

        private void UpdateVadCalibrationSubscription(string source)
        {
            var isDesktop = string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase);
            var isMonitoring = _vadCalibrationService.IsMonitoring(source);

            if (isMonitoring)
            {
                if (isDesktop)
                {
                    CreateDesktopPipeline();
                }
                else
                {
                    CreateMicPipeline();
                }
            }

            var pipeline = isDesktop ? _desktopPipeline : _micPipeline;
            var usesVad = pipeline?.UsesVad == true;

            _vadCalibrationService.PublishAvailability(source, isMonitoring && usesVad);

            if (isDesktop)
            {
                if (isMonitoring && usesVad && !_desktopVadCalibrationSubscribed)
                {
                    pipeline!.VadConfidenceMeasured += OnDesktopVadConfidenceMeasured;
                    _desktopVadCalibrationSubscribed = true;
                }
                else if ((!isMonitoring || !usesVad) && _desktopVadCalibrationSubscribed && pipeline != null)
                {
                    pipeline.VadConfidenceMeasured -= OnDesktopVadConfidenceMeasured;
                    _desktopVadCalibrationSubscribed = false;
                }

                return;
            }

            if (isMonitoring && usesVad && !_micVadCalibrationSubscribed)
            {
                pipeline!.VadConfidenceMeasured += OnMicVadConfidenceMeasured;
                _micVadCalibrationSubscribed = true;
            }
            else if ((!isMonitoring || !usesVad) && _micVadCalibrationSubscribed && pipeline != null)
            {
                pipeline.VadConfidenceMeasured -= OnMicVadConfidenceMeasured;
                _micVadCalibrationSubscribed = false;
            }
        }

        private void OnMicVadConfidenceMeasured(object? sender, VadConfidenceMeasuredEventArgs e)
            => _vadCalibrationService.PublishMeasurement("Mic", e);

        private void OnDesktopVadConfidenceMeasured(object? sender, VadConfidenceMeasuredEventArgs e)
            => _vadCalibrationService.PublishMeasurement("Desktop", e);

        private string BuildPipelineSignature(string source, bool isLoopback)
        {
            var mode = _config.RecognitionBackendModeValue;
            var vadRoute = _config.ResolveVadRoute(source);
            return string.Join("|",
                source,
                isLoopback ? "loopback" : "capture",
                mode,
                BuildRecognitionBackendSignature(),
                _config.LocalRecognitionModelValue,
                vadRoute.MinSilenceDuration.ToString("F3"),
                vadRoute.MaxSegmentSeconds.ToString("F3"),
                vadRoute.Threshold.ToString("F3"));
        }

        private string BuildRecognitionBackendSignature()
        {
            return string.Join("|",
                BackendUrlHelper.UsesLegacyBackendApi(_config) ? "legacy" : "modern",
                BackendUrlHelper.BuildApiBaseUrl(_config).Trim());
        }

        private AudioCaptureRequest? BuildDesktopCaptureRequest(bool allowDeferredApplicationTarget)
        {
            var sourceMode = _config.DesktopAudioSourceModeValue;
            if (sourceMode == DesktopAudioSourceMode.Application && !AudioPlatformHelper.IsApplicationLoopbackSupported())
            {
                var currentBuild = AudioPlatformHelper.GetCurrentWindowsBuildNumber();
                _logger.Warning(
                    "Application loopback is not supported on the current Windows build {Build}. Falling back to system monitor audio.",
                    currentBuild);
                _config.DesktopAudioSourceMode = (int)DesktopAudioSourceMode.System;
                PersistConfigSafe();
                AddLog(
                    "System",
                    $"当前系统不支持指定应用音频，已自动回退为系统监听音频。需要更新过的 Windows 10 2004+ 或更新系统，当前 Build={currentBuild}。",
                    "Warning");
                sourceMode = DesktopAudioSourceMode.System;
            }

            if (sourceMode == DesktopAudioSourceMode.Application)
            {
                var target = ResolveDesktopApplicationCaptureTarget(allowDeferredWhenUnavailable: allowDeferredApplicationTarget);
                if (target == null)
                {
                    return null;
                }

                return new AudioCaptureRequest
                {
                    DeviceId = DefaultDeviceConfigValue,
                    IsLoopback = true,
                    DesktopSourceMode = DesktopAudioSourceMode.Application,
                    TargetProcessId = (uint)target.Value.ProcessId,
                    TargetProcessName = target.Value.ProcessName
                };
            }

            var deviceName = string.IsNullOrWhiteSpace(_config.GameMicName) ? DefaultDeviceConfigValue : _config.GameMicName;
            if (string.Equals(deviceName, DefaultDeviceDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                deviceName = DefaultDeviceConfigValue;
            }

            return new AudioCaptureRequest
            {
                DeviceId = deviceName,
                IsLoopback = true,
                DesktopSourceMode = DesktopAudioSourceMode.System
            };
        }

        private (int ProcessId, string ProcessName)? ResolveDesktopApplicationCaptureTarget(bool allowDeferredWhenUnavailable)
        {
            if (string.IsNullOrWhiteSpace(_config.AppAudioTargetProcessName))
            {
                _config.AppAudioTargetProcessName = AppConfig.DefaultApplicationAudioTargetProcessName;
            }

            var normalizedProcessName = NormalizeProcessName(_config.AppAudioTargetProcessName);
            if (string.IsNullOrWhiteSpace(normalizedProcessName))
            {
                if (allowDeferredWhenUnavailable)
                {
                    return null;
                }

                throw new InvalidOperationException("应用监听音频未选择目标应用程序。");
            }

            if (_config.AppAudioTargetProcessId > 0)
            {
                try
                {
                    using var process = Process.GetProcessById(_config.AppAudioTargetProcessId);
                    if (IsUsableApplicationAudioProcess(process))
                    {
                        var processName = process.ProcessName;
                        if (string.Equals(NormalizeProcessName(processName), normalizedProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            _config.AppAudioTargetProcessName = processName;
                            return (process.Id, processName);
                        }

                        _logger.Warning(
                            "忽略过期的应用监听 PID 缓存。CachedPid={CachedPid}, ExpectedProcess={ExpectedProcess}, ActualProcess={ActualProcess}",
                            _config.AppAudioTargetProcessId,
                            normalizedProcessName,
                            processName);
                    }
                }
                catch
                {
                }

                _config.AppAudioTargetProcessId = 0;
            }

            var processes = Process.GetProcessesByName(normalizedProcessName);
            try
            {
                foreach (var process in processes.OrderBy(p => p.Id))
                {
                    if (!IsUsableApplicationAudioProcess(process))
                    {
                        continue;
                    }

                    _config.AppAudioTargetProcessId = process.Id;
                    _config.AppAudioTargetProcessName = process.ProcessName;
                    return (process.Id, process.ProcessName);
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }

            if (allowDeferredWhenUnavailable)
            {
                return null;
            }

            throw new InvalidOperationException($"未找到正在运行的目标应用：{normalizedProcessName}");
        }

        private static bool IsUsableApplicationAudioProcess(Process process)
        {
            try
            {
                return process.Id != Environment.ProcessId
                    && !process.HasExited
                    && !string.IsNullOrWhiteSpace(process.ProcessName)
                    && process.SessionId > 0;
            }
            catch
            {
                return false;
            }
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

        private static string BuildDesktopCaptureKey(AudioCaptureRequest request)
        {
            return request.IsApplicationLoopback
                ? $"app:{request.TargetProcessId}:{request.TargetProcessName}"
                : $"system:{request.DeviceId}";
        }

        private bool ShouldProcessSourceResult(TranscriptionResult result)
        {
            var source = result.Source ?? string.Empty;
            var capturedAtUtc = result.CapturedAtUtc;
            if (string.Equals(source, "Mic", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMicListening) return true;
                return IsCapturedWithinRecentWindows(capturedAtUtc, _micCaptureWindows)
                    || DateTime.UtcNow <= _micDisableGraceUntilUtc;
            }
            if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                if (IsDesktopListening) return true;
                return IsCapturedWithinRecentWindows(capturedAtUtc, _desktopCaptureWindows)
                    || DateTime.UtcNow <= _desktopDisableGraceUntilUtc;
            }
            return true;
        }

        private bool IsCapturedWithinRecentWindows(DateTime capturedAtUtc, List<(DateTime StartUtc, DateTime EndUtc)> windows)
        {
            if (capturedAtUtc == DateTime.MinValue)
            {
                return false;
            }

            lock (_captureWindowLock)
            {
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    var window = windows[i];
                    if (capturedAtUtc >= window.StartUtc && capturedAtUtc <= window.EndUtc)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void MarkCaptureStart(string source)
        {
            var now = DateTime.UtcNow;
            lock (_captureWindowLock)
            {
                if (string.Equals(source, "Mic", StringComparison.OrdinalIgnoreCase))
                {
                    _micCaptureStartUtc = now;
                }
                else if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase))
                {
                    _desktopCaptureStartUtc = now;
                }
            }
        }

        private void MarkCaptureStop(string source)
        {
            var endUtc = DateTime.UtcNow + GetCaptureTailAllowance(source);
            lock (_captureWindowLock)
            {
                if (string.Equals(source, "Mic", StringComparison.OrdinalIgnoreCase))
                {
                    var startUtc = _micCaptureStartUtc;
                    _micCaptureStartUtc = DateTime.MinValue;
                    AddCaptureWindow(_micCaptureWindows, startUtc, endUtc);
                }
                else if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase))
                {
                    var startUtc = _desktopCaptureStartUtc;
                    _desktopCaptureStartUtc = DateTime.MinValue;
                    AddCaptureWindow(_desktopCaptureWindows, startUtc, endUtc);
                }
            }
        }

        private TimeSpan GetCaptureTailAllowance(string source)
        {
            var minSilence = _config.ResolveVadRoute(source).MinSilenceDuration;
            var tailSeconds = Math.Max(1.0f, minSilence + 0.5f);
            return TimeSpan.FromSeconds(Math.Min(tailSeconds, 6.0f));
        }

        private void AddCaptureWindow(List<(DateTime StartUtc, DateTime EndUtc)> windows, DateTime startUtc, DateTime endUtc)
        {
            if (startUtc == DateTime.MinValue || startUtc > endUtc)
            {
                startUtc = endUtc;
            }

            windows.Add((startUtc, endUtc));
            PruneCaptureWindows(windows);
        }

        private void PruneCaptureWindows(List<(DateTime StartUtc, DateTime EndUtc)> windows)
        {
            var cutoff = DateTime.UtcNow - CaptureWindowRetention;
            windows.RemoveAll(w => w.EndUtc < cutoff);
            if (windows.Count > MaxCaptureWindows)
            {
                windows.RemoveRange(0, windows.Count - MaxCaptureWindows);
            }
        }
        private void ReportPythonTranslationSlotWarnings(string? routeHint, string primary, string secondary, string third)
        {
            if (!IsPythonTranslationEngineSelected(routeHint))
            {
                return;
            }
            var primaryTarget = _config.ResolvePrimaryTargetLanguage(routeHint);
            var secondaryTarget = _config.ResolveSecondaryTargetLanguage(routeHint);
            var tertiaryTarget = _config.ResolveTertiaryTargetLanguage(routeHint);
            WarnTranslationSlotIfMissing(primaryTarget, primary);
            WarnTranslationSlotIfMissing(secondaryTarget, secondary, primaryTarget);
            WarnTranslationSlotIfMissing(tertiaryTarget, third, primaryTarget, secondaryTarget);
        }
        private void WarnTranslationSlotIfMissing(string? targetLanguage, string translatedText, params string?[] previousLanguages)
        {
            var normalized = NormalizeLanguageCode(targetLanguage);
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "none")
            {
                return;
            }
            foreach (var previous in previousLanguages)
            {
                if (NormalizeLanguageCode(previous) == normalized)
                {
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(translatedText))
            {
                return;
            }
            AddLog("Translation", $"Python translation failed for target '{normalized}'.", "Warning");
        }
                private bool IsPythonTranslationEngineSelected(string? routeHint = null)
        {
            if (string.IsNullOrWhiteSpace(routeHint))
            {
                return _config.UsesEmbeddedTranslation("Mic") || _config.UsesEmbeddedTranslation("Desktop");
            }

            return _config.UsesEmbeddedTranslation(routeHint);
        }
        private static string NormalizeLanguageCode(string? value)
        {
            return (value ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        }

        private static string NormalizeSelectedRecognitionLanguage(string? value, string defaultLanguage)
        {
            var normalized = NormalizeLanguageCode(value);
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeLanguageCode(defaultLanguage);
            }

            return normalized;
        }

        private string NormalizeSelectedMicRecognitionLanguage(string? value)
        {
            var normalized = NormalizeSelectedRecognitionLanguage(value, AppConfig.DefaultMicSourceLanguage);
            return _config.RecognitionBackendModeValue == RecognitionBackendMode.Local
                ? AppConfig.CoerceLocalRecognitionSourceLanguage(normalized)
                : normalized;
        }

        private static string NormalizeSelectedTranslationLanguage(string? value, string emptyFallback = "none")
        {
            var normalized = NormalizeLanguageCode(value);
            return string.IsNullOrWhiteSpace(normalized)
                ? NormalizeLanguageCode(emptyFallback)
                : normalized;
        }

        private static TranscriptSourceMask NormalizeSourceMask(TranscriptSourceMask mask)
        {
            mask &= TranscriptSourceMask.All;
            return mask == TranscriptSourceMask.None ? TranscriptSourceMask.All : mask;
        }

        private static TranscriptLanguageMask NormalizeLanguageMask(TranscriptLanguageMask mask)
        {
            mask &= TranscriptLanguageMask.All;
            return mask == TranscriptLanguageMask.None
                ? (TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1)
                : mask;
        }

        private static TranscriptLanguageMask NormalizeCopyLanguageMask(TranscriptLanguageMask mask)
        {
            mask &= TranscriptLanguageMask.All;
            return mask == TranscriptLanguageMask.None
                ? TranscriptLanguageMask.Translation1
                : mask;
        }

        private static bool SourceMatchesMask(string source, TranscriptSourceMask mask)
        {
            if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return (mask & TranscriptSourceMask.Desktop) != 0;
            }
            if (string.Equals(source, "Photo", StringComparison.OrdinalIgnoreCase))
            {
                return (mask & TranscriptSourceMask.Photo) != 0;
            }
            return (mask & TranscriptSourceMask.Mic) != 0;
        }

        private (string sourceLang, string targetLang) ResolveTranslationLanguages(TranscriptionResult result)
        {
            var sourceLang = string.IsNullOrWhiteSpace(result.SourceLanguage) ? _config.ResolveSourceLanguage(result.Source) : result.SourceLanguage;
            var targetLang = string.IsNullOrWhiteSpace(result.TargetLanguage)
                ? _config.ResolvePrimaryTargetLanguage(result.Source)
                : result.TargetLanguage;
            return (sourceLang ?? string.Empty, targetLang ?? string.Empty);
        }

        private string GetEffectiveMode(string? source)
        {
            return _config.HasEnabledTranslationTarget(source) ? "translation" : "text";
        }
        private static Task YieldToUiRenderAsync()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task;
        }
        private async Task HandleRecognitionResultAsync(TranscriptionResult result)
        {
            if (result == null)
            {
                _logger.Warning("收到空的识别结果");
                return;
            }
            _logger.Verbose("处理识别结果 - 来源: {Source}, 文本长度: {Length}, 是否最终: {IsFinal}",
                result.Source, result.Text?.Length ?? 0, result.IsFinal);
            if (!ShouldProcessSourceResult(result))
            {
                _logger.Debug("Ignore recognition result outside capture window. source={Source}, segment={SegmentId}, capturedAtUtc={CapturedAtUtc:o}",
                    result.Source ?? string.Empty, result.SegmentId, result.CapturedAtUtc);
                return;
            }
            // Stream partial text to UI immediately; skip heavy post-processing for non-final results.
            if (!result.IsFinal)
            {
                var partialText = !string.IsNullOrWhiteSpace(result.Original)
                    ? result.Original
                    : result.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(partialText))
                {
                    var partialSource = result.Source ?? string.Empty;
                    var partialMode = GetEffectiveMode(partialSource);
                    var isTextModePartial = partialMode == "text";
                    QueueUi(() =>
                    {
                        if (partialSource == "Desktop")
                        {
                            if (isTextModePartial)
                            {
                                DesktopText = string.Empty;
                                DesktopTrans = partialText;
                            }
                            else
                            {
                                DesktopText = partialText;
                                DesktopTrans = string.Empty;
                            }
                        }
                        else
                        {
                            if (isTextModePartial)
                            {
                                MicText = string.Empty;
                                MicTrans = partialText;
                            }
                            else
                            {
                                MicText = partialText;
                                MicTrans = string.Empty;
                            }
                        }
                        CurrentSource = partialSource;
                    }, DispatcherPriority.Background);
                    if (_config.TextInSteamVr)
                    {
                        RequestSteamVrOverlayUpdate();
                    }
                }
                return;
            }
            string original = !string.IsNullOrEmpty(result.Original)
                ? result.Original
                : (result.Text ?? string.Empty).Split(new[] { "||" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            var resultSource = result.Source ?? string.Empty;
            original = NormalizeTranscriptText(original, resultSource, nameof(TranscriptItem.Original));
            result.Translation = NormalizeTranslationText(result.Translation, resultSource, nameof(TranscriptionResult.Translation));
            result.Translation2 = NormalizeTranslationText(result.Translation2, resultSource, nameof(TranscriptionResult.Translation2));
            result.Translation3 = NormalizeTranslationText(result.Translation3, resultSource, nameof(TranscriptionResult.Translation3));
            if (string.Equals(resultSource, "Photo", StringComparison.OrdinalIgnoreCase))
            {
                HandlePhotoTranslationResult(result, original);
                return;
            }
            var currentMode = GetEffectiveMode(resultSource);
            var isTextMode = currentMode == "text";
            if (!string.IsNullOrWhiteSpace(original))
            {
                var immediateSource = resultSource;
                var immediateText = original;
                QueueUi(() =>
                {
                    if (immediateSource == "Desktop")
                    {
                        if (isTextMode)
                        {
                            DesktopText = string.Empty;
                            DesktopTrans = immediateText;
                        }
                        else
                        {
                            DesktopText = immediateText;
                            DesktopTrans = string.Empty;
                        }
                    }
                    else
                    {
                        if (isTextMode)
                        {
                            MicText = string.Empty;
                            MicTrans = immediateText;
                        }
                        else
                        {
                            MicText = immediateText;
                            MicTrans = string.Empty;
                        }
                    }
                    CurrentSource = immediateSource;
                }, DispatcherPriority.Background);
                if (_config.TextInSteamVr)
                {
                    RequestSteamVrOverlayUpdate();
                }
            }
            // ����Ƿ�Ϊ�������˷����봥�����
            if (_config.ControlEnabled
                && string.Equals(result.Source, "Mic", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(original))
            {
                var isCommand = _commandService.ProcessText(original, result.Source);
                if (isCommand)
                {
                    _logger.Debug("识别结果作为命令处理: {Text}", original);
                    return; // �����Ѵ�������ټ�������Ϊ��ͨ�ı�
                }
            }
            var translations = new[]
            {
                result.Translation,
                result.Translation2,
                result.Translation3
            }.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            _logger.Debug("原始文本: '{Original}', 翻译数量: {TranslationCount}", original, translations.Length);
            bool shouldTranslate = currentMode != "text" && currentMode != "control";
            // �������ʶ���޷�������Ҫ���룬���÷������
            if (shouldTranslate && translations.Length == 0 && !string.IsNullOrWhiteSpace(original))
            {
                _logger.Debug("翻译结果为空，调用翻译服务 (模式: {Mode})", currentMode);
                // ֻ����һ�η������API ֧��һ���Է�������Ŀ�����Եķ���
                var (translateSourceLang, translateTargetLang) = ResolveTranslationLanguages(result);
                _logger.Debug("Translation request language pair. source={SourceLang}, target={TargetLang}, audioSource={AudioSource}", translateSourceLang, translateTargetLang, result.Source);
                string translationResult = string.Empty;
                try
                {
                    translationResult = await _translationService.TranslateAsync(original, translateTargetLang, translateSourceLang, result.Source);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Translation service failed. mode={Mode}, source={Source}", currentMode, result.Source);
                    AddLog("Translation", $"Translation failed: {ex.Message}", "Warning");
                }
                if (!string.IsNullOrWhiteSpace(translationResult))
                {
                    // Translation service returns: "t1||t2||t3".
                    var translationParts = translationResult.Split(new[] { "||" }, StringSplitOptions.None);
                    var primary = NormalizeTranslationText(
                        translationParts.Length > 0 ? translationParts[0] : string.Empty,
                        resultSource,
                        nameof(TranscriptionResult.Translation));
                    var secondary = NormalizeTranslationText(
                        translationParts.Length > 1 ? translationParts[1] : string.Empty,
                        resultSource,
                        nameof(TranscriptionResult.Translation2));
                    var third = NormalizeTranslationText(
                        translationParts.Length > 2 ? translationParts[2] : string.Empty,
                        resultSource,
                        nameof(TranscriptionResult.Translation3));
                    _logger.Debug("Translation parsed - t1: {Primary}, t2: {Secondary}, t3: {Third}",
                        primary, secondary, third);
                    ReportPythonTranslationSlotWarnings(result.Source, primary, secondary, third);
                    // Persist translations back to result so downstream features (e.g. TTS) can use the correct slots.
                    result.Translation = primary;
                    result.Translation2 = secondary;
                    result.Translation3 = third;
                    result.SourceLanguage = string.IsNullOrWhiteSpace(result.SourceLanguage) ? translateSourceLang : result.SourceLanguage;
                    result.TargetLanguage = string.IsNullOrWhiteSpace(result.TargetLanguage) ? translateTargetLang : result.TargetLanguage;
                    result.TargetLanguage2 = string.IsNullOrWhiteSpace(result.TargetLanguage2) ? _config.ResolveSecondaryTargetLanguage(result.Source) : result.TargetLanguage2;
                    result.TargetLanguage3 = string.IsNullOrWhiteSpace(result.TargetLanguage3) ? _config.ResolveTertiaryTargetLanguage(result.Source) : result.TargetLanguage3;
                    translations = new[] { primary, secondary, third }.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
                    _logger.Information("Translation completed - {Count} outputs", translations.Length);
                }
            }
            else if (!shouldTranslate)
            {
                _logger.Debug("当前模式 {Mode} 不需要翻译，清空翻译结果", currentMode);
                result.Translation = string.Empty;
                result.Translation2 = string.Empty;
                result.Translation3 = string.Empty;
                translations = Array.Empty<string>();
            }

            var photoImagePath = string.Equals(resultSource, "Photo", StringComparison.OrdinalIgnoreCase)
                ? (!string.IsNullOrWhiteSpace(result.PhotoTranslatedImagePath)
                    ? result.PhotoTranslatedImagePath
                    : result.PhotoSourceImagePath)
                : string.Empty;
            var hasPhotoImageResult = !string.IsNullOrWhiteSpace(photoImagePath);

            if (string.IsNullOrWhiteSpace(original) && translations.Length == 0 && !hasPhotoImageResult)
            {
                // Avoid producing empty rows in the transparent main window.
                _logger.Debug("Skip empty recognition result (Source={Source}, SegmentId={SegmentId})", result.Source, result.SegmentId);
                return;
            }

            string joinedTranslation = string.Join(Environment.NewLine, translations);
            _logger.Debug("Joined translation length: {Length}", joinedTranslation.Length);
            var rateLimitPayload = string.Join(" ", new[]
            {
                original,
                result.Translation,
                result.Translation2,
                result.Translation3,
                joinedTranslation
            }.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (IsRateLimitMessage(rateLimitPayload))
            {
                QueueUi(() => HandleRateLimitNotification(rateLimitPayload), DispatcherPriority.Background);
                return;
            }
            var displayTranslation = joinedTranslation;
            var displayOriginal = original;
            if (isTextMode)
            {
                displayTranslation = string.IsNullOrWhiteSpace(joinedTranslation) ? original : joinedTranslation;
                displayOriginal = string.Empty;
            }
            var displayOriginalText = displayOriginal ?? string.Empty;
            var displayTranslationText = displayTranslation ?? string.Empty;
            var displayTranslation1 = result.Translation ?? string.Empty;
            var displayTranslation2 = result.Translation2 ?? string.Empty;
            var displayTranslation3 = result.Translation3 ?? string.Empty;
            if (isTextMode && string.IsNullOrWhiteSpace(displayTranslation1)
                && string.IsNullOrWhiteSpace(displayTranslation2)
                && string.IsNullOrWhiteSpace(displayTranslation3))
            {
                displayTranslation1 = original;
            }
            var logMessage = hasPhotoImageResult && string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(joinedTranslation)
                ? $"Image result: {Path.GetFileName(photoImagePath)}"
                : string.IsNullOrWhiteSpace(original)
                    ? joinedTranslation
                    : string.IsNullOrEmpty(joinedTranslation)
                        ? original
                        : $"{original} -> {joinedTranslation}";
            var transcript = new TranscriptItem
            {
                Source = resultSource,
                Original = displayOriginalText,
                Translation = displayTranslation1,
                Translation2 = displayTranslation2,
                Translation3 = displayTranslation3,
                StatusTag = string.Equals(resultSource, "Photo", StringComparison.OrdinalIgnoreCase) ? "Photo" : string.Empty,
                PhotoTranslationMode = result.PhotoTranslationMode ?? string.Empty,
                PhotoImagePath = photoImagePath,
                Timestamp = DateTime.Now
            };
            QueueUi(() =>
            {
                if (resultSource == "Desktop")
                {
                    DesktopText = displayOriginalText;
                    DesktopTrans = displayTranslationText;
                }
                else
                {
                    MicText = displayOriginalText;
                    MicTrans = displayTranslationText;
                }
                CurrentSource = resultSource;
            }, DispatcherPriority.Background);
            EnqueueTranscript(transcript);
            AddLog(resultSource, logMessage);
            if (!string.IsNullOrEmpty(original))
            {
                // Desktop audio and photo OCR only update local overlays; they do not push chatbox OSC.
                if (!string.Equals(resultSource, "Desktop", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(resultSource, "Photo", StringComparison.OrdinalIgnoreCase)
                    && !_config.OscShutdown)
                {
                    string chatboxMessage = FormatOscMixedChatboxMessage(
                        FormatChatbox(original, translations, resultSource),
                        updateClientData: true);
                    _logger.Debug("OSC chatbox payload length: {Length}", chatboxMessage.Length);
                    if (!string.IsNullOrWhiteSpace(chatboxMessage))
                    {
                        SendChatboxThrottled(chatboxMessage);
                    }
                }
            }
            // SteamVR Overlay��������ã�
            if (_config.TextInSteamVr)
            {
                if (!_steamVrService.IsConnected)
                {
                    _ = _steamVrService.InitializeAsync();
                }
                RequestSteamVrOverlayUpdate();
            }
            // TTS �� UI �߳�֮����ã���������
            // TTS: run outside UI thread to avoid blocking rendering.
            if (_config.TtsToggle != 0 && IsTtsEligibleSource(resultSource))
            {
                var ttsItems = BuildTtsSpeakItems(result, original);
                if (ttsItems.Count > 0)
                {
                    var ttsVersion = Volatile.Read(ref _ttsScheduleVersion);
                    _ = Task.Run(async () =>
                    {
                        await YieldToUiRenderAsync();
                        await Task.Delay(TtsStartDelay);
                        if (ttsVersion != Volatile.Read(ref _ttsScheduleVersion))
                        {
                            _logger.Debug("Skip stale delayed TTS task.");
                            return;
                        }

                        await SpeakConfiguredAsync(ttsItems, ttsVersion);
                    });
                }
                else
                {
                    _logger.Debug(
                        "Skip TTS because no selected speak item is available. source={Source}, micMask={MicMask}, desktopMask={DesktopMask}",
                        resultSource,
                        _config.MicTtsSpeakMask,
                        _config.DesktopTtsSpeakMask);
                }
            }
            else if (_config.TtsToggle != 0)
            {
                _logger.Debug("Skip TTS for non-audio source. source={Source}", resultSource);
            }
        }

        private static bool IsTtsEligibleSource(string? source)
        {
            return string.Equals(source, "Mic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "TextSend", StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<(string text, string lang, string outputDeviceName)> BuildTtsSpeakItems(TranscriptionResult result, string original)
        {
            var mask = string.Equals(result.Source, "Desktop", StringComparison.OrdinalIgnoreCase)
                ? _config.DesktopTtsSpeakMask
                : _config.MicTtsSpeakMask;
            if (mask == 0)
            {
                return Array.Empty<(string text, string lang, string outputDeviceName)>();
            }

            var outputDeviceName = ResolveTtsOutputDeviceName(result.Source);
            var sourceLang = string.IsNullOrWhiteSpace(result.SourceLanguage) ? _config.ResolveSourceLanguage(result.Source) : result.SourceLanguage;
            var t1Lang = string.IsNullOrWhiteSpace(result.TargetLanguage) ? _config.ResolvePrimaryTargetLanguage(result.Source) : result.TargetLanguage;
            var t2Lang = string.IsNullOrWhiteSpace(result.TargetLanguage2) ? _config.ResolveSecondaryTargetLanguage(result.Source) : result.TargetLanguage2;
            var t3Lang = string.IsNullOrWhiteSpace(result.TargetLanguage3) ? _config.ResolveTertiaryTargetLanguage(result.Source) : result.TargetLanguage3;
            var items = new List<(string text, string lang, string outputDeviceName)>(4);
            if ((mask & 1) != 0 && !string.IsNullOrWhiteSpace(original) && IsTtsLanguageEnabled(sourceLang))
            {
                items.Add((original, sourceLang, outputDeviceName));
            }

            if ((mask & 2) != 0 && !string.IsNullOrWhiteSpace(result.Translation) && IsTtsLanguageEnabled(t1Lang))
            {
                items.Add((result.Translation, t1Lang, outputDeviceName));
            }

            if ((mask & 4) != 0 && !string.IsNullOrWhiteSpace(result.Translation2) && IsTtsLanguageEnabled(t2Lang))
            {
                items.Add((result.Translation2, t2Lang, outputDeviceName));
            }

            if ((mask & 8) != 0 && !string.IsNullOrWhiteSpace(result.Translation3) && IsTtsLanguageEnabled(t3Lang))
            {
                items.Add((result.Translation3, t3Lang, outputDeviceName));
            }

            return items;
        }

        private static bool IsTtsLanguageEnabled(string? language)
        {
            return !string.IsNullOrWhiteSpace(language)
                && !string.Equals(language, "none", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveTtsOutputDeviceName(string? source)
        {
            var outputDeviceName = string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase)
                ? _config.DesktopTtsOutputName
                : _config.TtsOutputName;

            return string.IsNullOrWhiteSpace(outputDeviceName)
                ? DefaultDeviceConfigValue
                : outputDeviceName.Trim();
        }

        private async Task SpeakConfiguredAsync(IReadOnlyList<(string text, string lang, string outputDeviceName)> items, int scheduleVersion)
        {
            try
            {
                if (items.Count == 0)
                {
                    return;
                }

                if (_config.TtsInterruptPolicyValue == TtsInterruptPolicy.InterruptCurrent)
                {
                    _ttsService.Stop();
                }

                foreach (var (text, lang, outputDeviceName) in items)
                {
                    if (scheduleVersion != Volatile.Read(ref _ttsScheduleVersion))
                    {
                        _logger.Debug("Stop TTS batch because settings changed.");
                        return;
                    }

                    await _ttsService.SpeakAsync(text, lang, null, outputDeviceName);
                    if (_config.TtsInterruptPolicyValue == TtsInterruptPolicy.Queue)
                    {
                        await Task.Delay(80);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "TTS 播放失败");
            }
        }
        private void ToggleMic()
        {
            try
            {
                SetMicListeningState(!IsMicListening, logStateChange: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to toggle mic listening.");
                AddLog("System", $"切换麦克风监听失败：{ex.Message}", "Error");
            }
            finally
            {
                RequestSteamVrOverlayUpdate();
            }
        }
        private void ToggleDesktop()
        {
            try
            {
                SetDesktopListeningState(!IsDesktopListening, logStateChange: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to toggle desktop listening.");
                AddLog("System", $"切换监听音频失败：{ex.Message}", "Error");
            }
            finally
            {
                RequestSteamVrOverlayUpdate();
            }
        }
        private void RefreshDevices()
        {
            MicDevices.Clear();
            MicDevices.Add(DefaultDeviceDisplayName);

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (!string.IsNullOrWhiteSpace(device.FriendlyName) && !MicDevices.Contains(device.FriendlyName))
                    {
                        MicDevices.Add(device.FriendlyName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "刷新麦克风设备列表失败");
            }
        }
        private void ExportLogs()
        {
            FlushPendingUiItems();
            if (Logs.Count == 0)
            {
                AddLog("System", "当前没有可导出的日志", "Warning");
                return;
            }
            try
            {
                var exportDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "VRCLS");
                Directory.CreateDirectory(exportDir);
                var file = Path.Combine(exportDir, $"VRCLS-Logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllLines(
                    file,
                    Logs.Select(log => $"[{log.Timestamp:HH:mm:ss}] {log.Source}: {log.Text}"),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                AddLog("System", $"日志已导出到 {file}");
            }
            catch (Exception ex)
            {
                AddLog("System", $"导出日志失败: {ex.Message}", "Error");
            }
        }
        private void InitializeUiFlushTimer()
        {
            if (_uiFlushTimer != null) return;
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _uiFlushTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = UiFlushInterval
            };
            _uiFlushTimer.Tick += OnUiFlushTimerTick;
            _uiFlushTimer.Start();
        }
        private void OnUiFlushTimerTick(object? sender, EventArgs e)
        {
            FlushPendingUiItems();
        }
        private void FlushPendingUiItems()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(FlushPendingUiItems), DispatcherPriority.Background);
                return;
            }
            if (_pendingLogs.IsEmpty && _pendingTranscripts.IsEmpty) return;
            var logCount = 0;
            while (logCount < UiFlushBatchSize && _pendingLogs.TryDequeue(out var log))
            {
                Logs.Insert(0, log);
                logCount++;
            }
            TrimCollection(Logs, 200);
            var transcriptCount = 0;
            while (transcriptCount < UiFlushBatchSize && _pendingTranscripts.TryDequeue(out var transcript))
            {
                Transcripts.Insert(0, transcript);
                transcriptCount++;
            }
            TrimCollection(Transcripts, 200);
        }
        private void EnqueueLog(LogItem item)
        {
            _pendingLogs.Enqueue(item);
            ScheduleUiFlush();
        }
        private void EnqueueTranscript(TranscriptItem item)
        {
            if (item == null)
            {
                return;
            }

            item.Original = NormalizeTranscriptText(item.Original, item.Source, nameof(TranscriptItem.Original));
            item.Translation = NormalizeTranscriptText(item.Translation, item.Source, nameof(TranscriptItem.Translation));
            item.Translation2 = NormalizeTranscriptText(item.Translation2, item.Source, nameof(TranscriptItem.Translation2));
            item.Translation3 = NormalizeTranscriptText(item.Translation3, item.Source, nameof(TranscriptItem.Translation3));

            _pendingTranscripts.Enqueue(item);
            ScheduleUiFlush();
        }

        private static string NormalizeTranscriptText(string? text, string source, string field)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // The transparent main window uses TextBlock with a fixed LineHeight.
            // If upstream services return extra blank lines (or mixed CR/LF sequences),
            // they become very noticeable visually.
            //
            // Normalize all newlines to \n and drop whitespace-only lines.
            var hadCr = text.Contains('\r', StringComparison.Ordinal);
            var normalized = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);

            var lines = normalized.Split('\n');
            var kept = new List<string>(lines.Length);
            var blankLineCount = 0;
            var invisibleOnlyLineCount = 0;
            foreach (var line in lines)
            {
                if (IsVisiblyEmptyLine(line, out var removedInvisibleCount))
                {
                    blankLineCount++;
                    if (removedInvisibleCount > 0)
                    {
                        invisibleOnlyLineCount++;
                    }
                    continue;
                }

                kept.Add(line.Trim());
            }

            var cleaned = kept.Count == 0 ? string.Empty : string.Join("\n", kept);
            if (blankLineCount > 0)
            {
                _logger.Debug(
                    "Transcript contains blank/invisible lines (Source={Source}, Field={Field}, Lines={Lines}, BlankLines={BlankLines}, InvisibleOnlyLines={InvisibleOnlyLines}, HadCR={HadCR}, Length={Length}, Preview={Preview})",
                    source,
                    field,
                    lines.Length,
                    blankLineCount,
                    invisibleOnlyLineCount,
                    hadCr,
                    text.Length,
                    TrimForDebug(text));
            }
            else if (hadCr)
            {
                _logger.Debug(
                    "Transcript contains CR newlines (Source={Source}, Field={Field}, Lines={Lines}, HadCR={HadCR}, Length={Length}, Preview={Preview})",
                    source,
                    field,
                    lines.Length,
                    hadCr,
                    text.Length,
                    TrimForDebug(text));
            }

            return cleaned;
        }

        private static string NormalizeTranslationText(string? text, string source, string field)
        {
            var cleaned = NormalizeTranscriptText(text, source, field);
            var stripped = StripTranslationBoilerplate(cleaned);
            if (!string.Equals(cleaned, stripped, StringComparison.Ordinal))
            {
                _logger.Debug(
                    "Removed translation boilerplate (Source={Source}, Field={Field}, BeforeLength={BeforeLength}, AfterLength={AfterLength}, Preview={Preview})",
                    source,
                    field,
                    cleaned.Length,
                    stripped.Length,
                    TrimForDebug(cleaned));
            }

            return stripped;
        }

        private static string StripTranslationBoilerplate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var cleaned = TranslationBoilerplatePrefixPattern.Replace(text.Trim(), string.Empty, 1).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return string.Empty;
            }

            var lines = cleaned.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            if (lines.Length > 1)
            {
                if (IsTranslationBoilerplateLine(lines[0]))
                {
                    return string.Join("\n", lines.Skip(1)).Trim();
                }

                if (lines.Take(lines.Length - 1).Any(IsTranslationBoilerplateLine)
                    && TryUnwrapQuotedLine(lines[^1], out var unwrappedLastLine))
                {
                    return unwrappedLastLine;
                }
            }

            if (TryUnwrapQuotedLine(cleaned, out var unwrapped))
            {
                return unwrapped;
            }

            return cleaned;
        }

        private static bool IsTranslationBoilerplateLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var normalized = line.Trim();
            if (TranslationBoilerplatePrefixPattern.IsMatch(normalized))
            {
                return true;
            }

            var hasTranslationTerm = normalized.Contains("翻译", StringComparison.Ordinal)
                || normalized.Contains("翻譯", StringComparison.Ordinal)
                || normalized.Contains("译文", StringComparison.Ordinal)
                || normalized.Contains("譯文", StringComparison.Ordinal)
                || normalized.Contains("翻訳", StringComparison.Ordinal)
                || normalized.Contains("訳す", StringComparison.Ordinal)
                || normalized.Contains("訳せ", StringComparison.Ordinal)
                || normalized.Contains("translation", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("translated text", StringComparison.OrdinalIgnoreCase);
            if (!hasTranslationTerm)
            {
                return false;
            }

            return normalized.Contains("以下", StringComparison.Ordinal)
                || normalized.Contains("下面", StringComparison.Ordinal)
                || normalized.Contains("如下", StringComparison.Ordinal)
                || normalized.Contains("とりあえず", StringComparison.Ordinal)
                || normalized.Contains("以下のよう", StringComparison.Ordinal)
                || normalized.Contains("here", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("following", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("this is", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("can be translated", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryUnwrapQuotedLine(string text, out string unwrapped)
        {
            unwrapped = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.Length < 2)
            {
                return false;
            }

            var first = trimmed[0];
            var last = trimmed[^1];
            var isQuoted = (first == '“' && last == '”')
                || (first == '「' && last == '」')
                || (first == '『' && last == '』')
                || (first == '"' && last == '"')
                || (first == '\'' && last == '\'')
                || (first == '`' && last == '`');
            if (!isQuoted)
            {
                return false;
            }

            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (string.IsNullOrWhiteSpace(inner))
            {
                return false;
            }

            unwrapped = inner;
            return true;
        }

        private static bool IsVisiblyEmptyLine(string line, out int removedInvisibleCount)
        {
            removedInvisibleCount = 0;
            if (string.IsNullOrWhiteSpace(line))
            {
                return true;
            }

            // Some upstream services return "blank" lines that actually contain invisible Unicode
            // format characters (e.g. U+200B). WPF will still create a line box for them, resulting
            // in very noticeable empty rows because the main window uses a fixed LineHeight.
            var hasNonWhitespace = false;
            foreach (var ch in line)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                // Treat all Unicode "Format" characters as invisible.
                // This includes common troublemakers like:
                // - U+200B ZERO WIDTH SPACE
                // - U+FEFF ZERO WIDTH NO-BREAK SPACE
                // - bidi marks (LRM/RLM etc.)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format)
                {
                    removedInvisibleCount++;
                    continue;
                }

                hasNonWhitespace = true;
                break;
            }

            return !hasNonWhitespace;
        }

        private static string TrimForDebug(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var s = text.Replace("\r", "\\r", StringComparison.Ordinal)
                        .Replace("\n", "\\n", StringComparison.Ordinal)
                        .Replace("\u200B", "\\u200B", StringComparison.Ordinal)
                        .Replace("\uFEFF", "\\uFEFF", StringComparison.Ordinal)
                        .Trim();
            return s.Length <= 220 ? s : (s.Substring(0, 220) + "...");
        }
        private void ScheduleUiFlush()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }
            if (Interlocked.Exchange(ref _uiFlushScheduled, 1) == 1)
            {
                return;
            }
            dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _uiFlushScheduled, 0);
                FlushPendingUiItems();
                if (!_pendingLogs.IsEmpty || !_pendingTranscripts.IsEmpty)
                {
                    ScheduleUiFlush();
                }
            }), DispatcherPriority.Background);
        }
        private static void TrimCollection<T>(ObservableCollection<T> collection, int maxCount)
        {
            while (collection.Count > maxCount)
            {
                collection.RemoveAt(collection.Count - 1);
            }
        }
        private void QueueUi(Action action, DispatcherPriority priority = DispatcherPriority.Background)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }
            dispatcher.BeginInvoke(action, priority);
        }
        private void QueueClipboardSet(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var now = DateTime.UtcNow;
            if (text == _lastClipboardText && now - _lastClipboardUtc < ClipboardMinInterval)
            {
                return;
            }
            _lastClipboardText = text;
            _lastClipboardUtc = now;
            QueueUi(() =>
            {
                try
                {
                    Clipboard.SetText(text);
                    _logger.Debug("Clipboard updated.");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to update clipboard.");
                }
            }, DispatcherPriority.Background);
        }

        private void CopyTranscript(TranscriptItem? transcript)
        {
            if (transcript == null)
            {
                return;
            }

            CopyTranscriptToClipboard(
                transcript.Original,
                transcript.Translation,
                transcript.Translation2,
                transcript.Translation3,
                "main window");
        }

        private void CopyPhotoTranslationResult(PhotoTranslationResultItem? result)
        {
            if (result == null)
            {
                return;
            }

            var text = result.DisplayText;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = result.ResultImagePath;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                QueueClipboardSet(text);
                _logger.Debug("Queued clipboard update from photo translation window.");
            }
        }

        private void ShowPreviousPhotoTranslationResult()
        {
            var count = PhotoTranslationResults.Count;
            if (count <= 0)
            {
                return;
            }

            var currentIndex = _currentPhotoTranslationResultIndex < 0
                ? 0
                : Math.Clamp(_currentPhotoTranslationResultIndex, 0, count - 1);
            SetCurrentPhotoTranslationResultIndex((currentIndex + 1) % count);
        }

        private bool CanShowPreviousPhotoTranslationResult()
        {
            return PhotoTranslationResults.Count > 1;
        }

        private void ShowNextPhotoTranslationResult()
        {
            var count = PhotoTranslationResults.Count;
            if (count <= 0)
            {
                return;
            }

            var currentIndex = _currentPhotoTranslationResultIndex < 0
                ? 0
                : Math.Clamp(_currentPhotoTranslationResultIndex, 0, count - 1);
            SetCurrentPhotoTranslationResultIndex((currentIndex + count - 1) % count);
        }

        private bool CanShowNextPhotoTranslationResult()
        {
            return PhotoTranslationResults.Count > 1;
        }

        private void SetCurrentPhotoTranslationResultIndex(int requestedIndex)
        {
            var nextIndex = PhotoTranslationResults.Count == 0
                ? -1
                : Math.Clamp(requestedIndex, 0, PhotoTranslationResults.Count - 1);
            var nextItem = nextIndex >= 0 ? PhotoTranslationResults[nextIndex] : null;

            var changed = _currentPhotoTranslationResultIndex != nextIndex
                || !ReferenceEquals(_currentPhotoTranslationResult, nextItem);
            _currentPhotoTranslationResultIndex = nextIndex;
            _currentPhotoTranslationResult = nextItem;

            if (changed)
            {
                OnPropertyChanged(nameof(CurrentPhotoTranslationResult));
                OnPropertyChanged(nameof(CurrentPhotoTranslationPageText));
                OnPropertyChanged(nameof(CanNavigatePhotoTranslationPrevious));
                OnPropertyChanged(nameof(CanNavigatePhotoTranslationNext));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void ClearPhotoTranslationResults()
        {
            QueueUi(() =>
            {
                PhotoTranslationResults.Clear();
                SetCurrentPhotoTranslationResultIndex(-1);
                OnPropertyChanged(nameof(HasPhotoTranslationResults));
                RequestSteamVrOverlayUpdate();
            }, DispatcherPriority.Background);
        }

        private void CopySteamVrTranscript(SteamVrOverlayTranscript? transcript)
        {
            if (transcript == null)
            {
                return;
            }

            CopyTranscriptToClipboard(
                transcript.Original,
                transcript.Translation,
                transcript.Translation2,
                transcript.Translation3,
                "steamvr panel");
        }

        private void CopyTranscriptToClipboard(
            string? original,
            string? translation1,
            string? translation2,
            string? translation3,
            string trigger)
        {
            if (!_config.CopyBox)
            {
                return;
            }

            var mask = NormalizeCopyLanguageMask(_config.CopyLanguageMask);
            if (mask != _config.CopyLanguageMask)
            {
                _config.CopyLanguageMask = mask;
                PersistConfigSafe();
            }

            var text = BuildClipboardText(original, translation1, translation2, translation3, mask);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            QueueClipboardSet(text);
            _logger.Debug("Queued clipboard update from {Trigger}.", trigger);
        }

        private static string BuildClipboardText(
            string? original,
            string? translation1,
            string? translation2,
            string? translation3,
            TranscriptLanguageMask mask)
        {
            var lines = new List<string>(4);

            if ((mask & TranscriptLanguageMask.Translation1) != 0 && !string.IsNullOrWhiteSpace(translation1))
            {
                lines.Add(translation1.Trim());
            }

            if ((mask & TranscriptLanguageMask.Translation2) != 0 && !string.IsNullOrWhiteSpace(translation2))
            {
                lines.Add(translation2.Trim());
            }

            if ((mask & TranscriptLanguageMask.Translation3) != 0 && !string.IsNullOrWhiteSpace(translation3))
            {
                lines.Add(translation3.Trim());
            }

            if ((mask & TranscriptLanguageMask.Original) != 0 && !string.IsNullOrWhiteSpace(original))
            {
                lines.Add(original.Trim());
            }

            return string.Join(Environment.NewLine, lines);
        }

        private void OnOscMessageReceived(object? sender, OscMessageReceivedEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Address))
            {
                return;
            }

            if (!string.Equals(e.Address, "/chatbox/input", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("Received non-chatbox OSC message. message={Message}", e.Message);
                return;
            }

            var text = ExtractExternalOscText(e);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Debug("Ignore /chatbox/input OSC message without text payload. message={Message}", e.Message);
                return;
            }

            var externalText = text.Trim();
            _logger.Information(
                "Received external OSC input. length={Length}, useAsTextInput={UseAsTextInput}",
                externalText.Length,
                _config.UseExternalOscInputAsTextInput);
            AddLog("OSC", $"收到外部 OSC 输入: {externalText}");

            if (_config.UseExternalOscInputAsTextInput)
            {
                _ = ProcessExternalOscTextInputAsync(externalText);
                return;
            }

            var mixedMessage = FormatOscMixedChatboxMessage(
                serverData: externalText,
                updateServerData: true);
            if (!_config.OscShutdown && !string.IsNullOrWhiteSpace(mixedMessage))
            {
                SendChatboxThrottled(mixedMessage);
            }
        }

        private async Task ProcessExternalOscTextInputAsync(string externalText)
        {
            try
            {
                await ProcessTextInputAsync(externalText).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to process external OSC text input.");
                AddLog("OSC", $"外部 OSC 文字输入处理失败: {ex.Message}", "Warning");
            }
        }

        private static string ExtractExternalOscText(OscMessageReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Text))
            {
                return e.Text;
            }

            foreach (var value in e.Values)
            {
                if (value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private string FormatOscMixedChatboxMessage(
            string? clientData = null,
            string? serverData = null,
            bool updateClientData = false,
            bool updateServerData = false)
        {
            var now = DateTime.UtcNow;
            string effectiveClientData;
            string effectiveServerData;

            lock (_oscMixLock)
            {
                if (updateClientData)
                {
                    _lastOscClientData = clientData ?? string.Empty;
                    _lastOscClientDataUtc = now;
                }

                if (updateServerData)
                {
                    _lastOscServerData = serverData ?? string.Empty;
                    _lastOscServerDataUtc = now;
                }

                effectiveClientData = now - _lastOscClientDataUtc <= OscMixDataRetention
                    ? _lastOscClientData
                    : string.Empty;
                effectiveServerData = now - _lastOscServerDataUtc <= OscMixDataRetention
                    ? _lastOscServerData
                    : string.Empty;
            }

            var template = _config.ChatboxOscMixTemplate;
            if (string.IsNullOrWhiteSpace(template))
            {
                return NormalizeChatboxOutput(clientData ?? serverData ?? string.Empty);
            }

            var result = template
                .Replace("{clientdata}", effectiveClientData)
                .Replace("{serverdata}", effectiveServerData);

            return NormalizeChatboxOutput(result);
        }

        private void SendChatboxThrottled(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            CancellationTokenSource? cts;
            lock (_oscSendLock)
            {
                _oscSendCts?.Cancel();
                _oscSendCts?.Dispose();
                _oscSendCts = new CancellationTokenSource();
                cts = _oscSendCts;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    TimeSpan delay;
                    lock (_oscSendLock)
                    {
                        if (message == _lastOscMessage && now - _lastOscSendUtc < OscSendMinInterval)
                        {
                            return;
                        }
                        var elapsed = now - _lastOscSendUtc;
                        delay = elapsed < OscSendMinInterval ? OscSendMinInterval - elapsed : TimeSpan.Zero;
                        _lastOscMessage = message;
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    }
                    if (cts.IsCancellationRequested) return;
                    _oscService.SendChatbox(message, triggerNotificationSfx: _config.OscChatboxNotificationSfx);
                    lock (_oscSendLock)
                    {
                        _lastOscSendUtc = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "OSC send failed");
                }
            });
        }
        private void AddLog(string source, string message, string level = "Info")
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // Also write to Serilog
            switch (level.ToLowerInvariant())
            {
                case "error":
                    _logger.Error("[{Source}] {Message}", source, message);
                    break;
                case "warning":
                    _logger.Warning("[{Source}] {Message}", source, message);
                    break;
                case "debug":
                    _logger.Debug("[{Source}] {Message}", source, message);
                    break;
                default:
                    _logger.Information("[{Source}] {Message}", source, message);
                    break;
            }
            EnqueueLog(new LogItem(source, message, level));

            if (level.Equals("error", StringComparison.OrdinalIgnoreCase) && !IsRateLimitMessage(message))
            {
                QueueUi(() => ShowHomeAlert(message, autoHide: true), DispatcherPriority.Background);
            }
        }
        private void HideOverlay()
        {
            IsOverlayVisible = false;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var window = Application.Current?.MainWindow;
                if (window != null)
                {
                    window.WindowState = WindowState.Minimized;
                }
            });
        }
        private void ReloadHotkeysFromConfig()
        {
            try
            {
                _hotkeyService.UnregisterHotkey("mic_toggle");
                _hotkeyService.UnregisterPressKey("mic_press");
                _hotkeyService.UnregisterHotkey("desktop_toggle");
                _hotkeyService.UnregisterPressKey("desktop_press");
                RegisterHotkeysFromConfig();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to reload hotkey settings.");
            }
        }
        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var settingsViewModel = sender as SettingsViewModel;
            var isSettingsReloading = settingsViewModel?.IsLoadingConfig == true;

            if (!isSettingsReloading && ShouldClearTtsQueueOnSettingsChange(e.PropertyName))
            {
                ClearPendingTts($"settings changed: {e.PropertyName}");
            }

            if (e.PropertyName is nameof(SettingsViewModel.VoiceMode)
                or nameof(SettingsViewModel.GameVoiceMode)
                or nameof(SettingsViewModel.VoiceHotKey)
                or nameof(SettingsViewModel.GameVoiceHotKey)
                or nameof(SettingsViewModel.MicPressingKey)
                or nameof(SettingsViewModel.CapPressingKey))
            {
                ReloadHotkeysFromConfig();
                if (e.PropertyName == nameof(SettingsViewModel.VoiceMode))
                {
                    var wasMicListening = IsMicListening;
                    var voiceMode = _config.VoiceTriggerModeValue;
                    IsMicListening = voiceMode == VoiceTriggerMode.Continuous;
                    _micDisableGraceUntilUtc = DateTime.MinValue;
                    if (IsMicListening && !wasMicListening) MarkCaptureStart("Mic");
                    if (!IsMicListening && wasMicListening) MarkCaptureStop("Mic");
                    SetMicRecognitionEnabled(IsMicListening);
                    RequestSteamVrOverlayUpdate();
                }
                if (e.PropertyName == nameof(SettingsViewModel.GameVoiceMode))
                {
                    var wasDesktopListening = IsDesktopListening;
                    IsDesktopListening = _config.GameVoiceTriggerModeValue == VoiceTriggerMode.Continuous;
                    _desktopDisableGraceUntilUtc = DateTime.MinValue;
                    if (IsDesktopListening && !wasDesktopListening) MarkCaptureStart("Desktop");
                    if (!IsDesktopListening && wasDesktopListening) MarkCaptureStop("Desktop");
                    if (IsDesktopApplicationCapturePending())
                    {
                        SetDesktopRecognitionEnabled(false);
                        if (IsDesktopListening)
                        {
                            ScheduleDesktopApplicationTargetRetry();
                        }
                        else
                        {
                            StopDesktopApplicationTargetRetry();
                        }
                    }
                    else
                    {
                        SetDesktopRecognitionEnabled(IsDesktopListening);
                    }
                    RequestSteamVrOverlayUpdate();
                }
            }
            if (e.PropertyName == nameof(SettingsViewModel.ControlEnabled))
            {
                SyncCommandServiceState();
            }
            if (e.PropertyName == nameof(SettingsViewModel.ExperimentalKeywordActionsJson))
            {
                ReloadExperimentalKeywordActions();
            }
            if (settingsViewModel is { } settingsVm
                && (e.PropertyName is nameof(SettingsViewModel.EnableOscServer)
                    or nameof(SettingsViewModel.OscServerIp)
                    or nameof(SettingsViewModel.OscServerPort)))
            {
                ApplyOscServerRuntimeSettings(settingsVm);
            }
            if (e.PropertyName == nameof(SettingsViewModel.MicName))
            {
                try { EnsureMicCaptureStarted(); } catch (Exception ex) { _logger.Warning(ex, "Failed to switch mic device."); }
                EnsureMicPassthroughState(forceRestart: true);
            }
            if (e.PropertyName is nameof(SettingsViewModel.EnableTts)
                or nameof(SettingsViewModel.TtsMode)
                or nameof(SettingsViewModel.TtsOutputName)
                or nameof(SettingsViewModel.TtsMicPassthroughEnabled)
                or nameof(SettingsViewModel.TtsMicPassthroughVolume))
            {
                EnsureMicPassthroughState(forceRestart: e.PropertyName == nameof(SettingsViewModel.TtsOutputName));
            }
            if (e.PropertyName is nameof(SettingsViewModel.EnableTts)
                or nameof(SettingsViewModel.TtsMode)
                or nameof(SettingsViewModel.TtsMicPassthroughEnabled)
                or nameof(SettingsViewModel.TtsMicPassthroughVolume)
                or nameof(SettingsViewModel.MicTtsSpeakMask)
                or nameof(SettingsViewModel.DesktopTtsSpeakMask)
                or nameof(SettingsViewModel.MicTtsSpeakOriginal)
                or nameof(SettingsViewModel.MicTtsSpeakTranslation1)
                or nameof(SettingsViewModel.MicTtsSpeakTranslation2)
                or nameof(SettingsViewModel.MicTtsSpeakTranslation3)
                or nameof(SettingsViewModel.DesktopTtsSpeakOriginal)
                or nameof(SettingsViewModel.DesktopTtsSpeakTranslation1)
                or nameof(SettingsViewModel.DesktopTtsSpeakTranslation2)
                or nameof(SettingsViewModel.DesktopTtsSpeakTranslation3))
            {
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName is nameof(SettingsViewModel.GameMicName)
                or nameof(SettingsViewModel.DesktopAudioSourceMode)
                or nameof(SettingsViewModel.SelectedApplicationAudioProcessId))
            {
                try
                {
                    EnsureDesktopCaptureStarted();
                    if (IsDesktopApplicationCapturePending())
                    {
                        SetDesktopRecognitionEnabled(false);
                        if (IsDesktopListening)
                        {
                            ScheduleDesktopApplicationTargetRetry();
                        }
                        else
                        {
                            StopDesktopApplicationTargetRetry();
                        }
                    }
                    else
                    {
                        SetDesktopRecognitionEnabled(IsDesktopListening);
                    }
                }
                catch (Exception ex) { _logger.Warning(ex, "Failed to switch desktop capture target."); }
            }
            if (e.PropertyName == nameof(SettingsViewModel.UseLocalRecognition))
            {
                try
                {
                    var mode = _config.RecognitionBackendModeValue;
                    _logger.Information("Recognition mode changed. mode={Mode}", mode);

                    if (IsMicListening)
                    {
                        EnsureMicCaptureStarted(forceRestart: true);
                        SetMicRecognitionEnabled(true);
                    }
                    else
                    {
                        CreateMicPipeline();
                        SetMicRecognitionEnabled(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to switch recognition mode.");
                }
            }
            if (e.PropertyName == nameof(SettingsViewModel.MainWindowLanguageMask))
            {
                var mask = NormalizeLanguageMask(_config.MainWindowLanguageMask);
                _config.MainWindowLanguageMask = mask;
                ApplyMainWindowLanguageMask(mask);
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName == nameof(SettingsViewModel.MainWindowOpacityPercent))
            {
                MainWindowOpacity = AppConfig.NormalizeMainWindowOpacity(_config.MainWindowOpacity);
            }
            if (e.PropertyName == nameof(SettingsViewModel.MainWindowBackgroundOpacityPercent))
            {
                MainWindowBackgroundOpacity = AppConfig.NormalizeMainWindowBackgroundOpacity(_config.MainWindowBackgroundOpacity);
            }
            if (e.PropertyName == nameof(SettingsViewModel.UiLanguage))
            {
                UiLanguage = _config.UiLanguage;
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName is nameof(SettingsViewModel.MicSourceLanguage)
                or nameof(SettingsViewModel.DesktopSourceLanguage)
                or nameof(SettingsViewModel.MicTargetTranslationLanguage)
                or nameof(SettingsViewModel.MicTargetTranslationLanguage2)
                or nameof(SettingsViewModel.MicTargetTranslationLanguage3)
                or nameof(SettingsViewModel.DesktopTargetTranslationLanguage)
                or nameof(SettingsViewModel.DesktopTargetTranslationLanguage2)
                or nameof(SettingsViewModel.DesktopTargetTranslationLanguage3))
            {
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName == nameof(SettingsViewModel.SteamVrNonTextOpacity))
            {
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName == nameof(SettingsViewModel.SteamVrFontScale))
            {
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName is nameof(SettingsViewModel.SteamVrMicTextColor)
                or nameof(SettingsViewModel.SteamVrDesktopTextColor))
            {
                RequestSteamVrOverlayUpdate();
            }
            if (e.PropertyName is nameof(SettingsViewModel.MicVadMinSilenceDuration)
                or nameof(SettingsViewModel.MicVadMaxSegmentSeconds)
                or nameof(SettingsViewModel.MicVadThreshold)
                or nameof(SettingsViewModel.DesktopVadMinSilenceDuration)
                or nameof(SettingsViewModel.DesktopVadMaxSegmentSeconds)
                or nameof(SettingsViewModel.DesktopVadThreshold))
            {
                QueuePipelineRestart(restartMic: true, restartDesktop: true);
            }
            if (e.PropertyName is nameof(SettingsViewModel.UseLegacyBackendApi)
                or nameof(SettingsViewModel.BaseUrl))
            {
                var currentSignature = BuildRecognitionBackendSignature();
                if (!string.Equals(_lastRecognitionBackendSignature, currentSignature, StringComparison.Ordinal))
                {
                    _logger.Information(
                        "Recognition backend route changed. from={PreviousSignature}, to={CurrentSignature}. Restarting pipelines.",
                        _lastRecognitionBackendSignature,
                        currentSignature);
                    _lastRecognitionBackendSignature = currentSignature;
                    QueuePipelineRestart(restartMic: true, restartDesktop: true);
                }
            }
            if (e.PropertyName == nameof(SettingsViewModel.LocalStreamingEndpointSilenceSeconds))
            {
                QueuePipelineRestart(restartMic: true, restartDesktop: false);
            }
            if (e.PropertyName == nameof(SettingsViewModel.TranslatorsDefaultRegion))
            {
                if (IsPythonTranslationEngineSelected())
                {
                    try
                    {
                        var embedded = App.GetRequiredService<EmbeddedTranslatorService>();
                        _ = embedded.ReloadAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to reload embedded translator.");
                    }
                }
            }
        }

        private void ApplyOscServerRuntimeSettings(SettingsViewModel settingsVm)
        {
            if (_isRollingBackOscServerSettings)
            {
                return;
            }

            var requestedEnabled = _config.EnableOscServer;
            var requestedIp = string.IsNullOrWhiteSpace(_config.OscServerIp) ? "127.0.0.1" : _config.OscServerIp.Trim();
            var requestedPort = _config.OscServerPort;

            try
            {
                if (!System.Net.IPAddress.TryParse(requestedIp, out _))
                {
                    throw new FormatException($"Invalid OSC server IP address: {requestedIp}");
                }

                if (requestedPort < 1 || requestedPort > 65535)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(requestedPort),
                        requestedPort,
                        "OSC server port must be between 1 and 65535.");
                }

                if (requestedEnabled)
                {
                    if (_appliedOscServerEnabled
                        && _appliedOscServerPort == requestedPort
                        && string.Equals(_appliedOscServerIp, requestedIp, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (_appliedOscServerEnabled)
                    {
                        _oscService.RestartOscServer(requestedPort);
                    }
                    else
                    {
                        _oscService.StartOscServer(requestedPort);
                    }
                }
                else if (_appliedOscServerEnabled)
                {
                    _oscService.StopOscServer();
                }

                _appliedOscServerEnabled = requestedEnabled;
                _appliedOscServerIp = requestedIp;
                _appliedOscServerPort = requestedPort;
                _logger.Information(
                    "OSC server runtime settings applied. enabled={Enabled}, ip={Ip}, port={Port}",
                    requestedEnabled,
                    requestedIp,
                    requestedPort);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "Failed to apply OSC server runtime settings. requestedEnabled={Enabled}, requestedIp={Ip}, requestedPort={Port}",
                    requestedEnabled,
                    requestedIp,
                    requestedPort);

                RestoreAppliedOscServerRuntimeState();
                RollbackOscServerSettings(settingsVm);
                ShowOscServerApplyFailedMessage(ex);
            }
        }

        private void RestoreAppliedOscServerRuntimeState()
        {
            try
            {
                _config.OscServerIp = _appliedOscServerIp;
                if (_appliedOscServerEnabled)
                {
                    _oscService.RestartOscServer(_appliedOscServerPort);
                }
                else
                {
                    _oscService.StopOscServer();
                }
            }
            catch (Exception restoreEx)
            {
                _logger.Warning(
                    restoreEx,
                    "Failed to restore previous OSC server runtime state. enabled={Enabled}, ip={Ip}, port={Port}",
                    _appliedOscServerEnabled,
                    _appliedOscServerIp,
                    _appliedOscServerPort);
            }
        }

        private void RollbackOscServerSettings(SettingsViewModel settingsVm)
        {
            _isRollingBackOscServerSettings = true;
            try
            {
                _config.EnableOscServer = _appliedOscServerEnabled;
                _config.OscServerIp = _appliedOscServerIp;
                _config.OscServerPort = _appliedOscServerPort;
                settingsVm.EnableOscServer = _appliedOscServerEnabled;
                settingsVm.OscServerIp = _appliedOscServerIp;
                settingsVm.OscServerPort = _appliedOscServerPort;
            }
            finally
            {
                _isRollingBackOscServerSettings = false;
            }
        }

        private static void ShowOscServerApplyFailedMessage(Exception ex)
        {
            var title = GetUiString("OscServerApplyFailedTitle", "OSC server settings failed");
            var prefix = GetUiString(
                "OscServerApplyFailedMessage",
                "Failed to apply OSC server settings. The UI has been restored to the last valid value.");
            var details = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            MessageBox.Show(
                $"{prefix}{Environment.NewLine}{Environment.NewLine}{details}",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private bool ShouldClearTtsQueueOnSettingsChange(string? propertyName)
        {
            return propertyName is nameof(SettingsViewModel.EnableTts)
                or nameof(SettingsViewModel.TtsMode)
                or nameof(SettingsViewModel.MicTtsSpeakMask)
                or nameof(SettingsViewModel.DesktopTtsSpeakMask)
                or nameof(SettingsViewModel.MicTtsSpeakOriginal)
                or nameof(SettingsViewModel.MicTtsSpeakTranslation1)
                or nameof(SettingsViewModel.MicTtsSpeakTranslation2)
                or nameof(SettingsViewModel.MicTtsSpeakTranslation3)
                or nameof(SettingsViewModel.DesktopTtsSpeakOriginal)
                or nameof(SettingsViewModel.DesktopTtsSpeakTranslation1)
                or nameof(SettingsViewModel.DesktopTtsSpeakTranslation2)
                or nameof(SettingsViewModel.DesktopTtsSpeakTranslation3)
                or nameof(SettingsViewModel.TtsProvider)
                or nameof(SettingsViewModel.TtsVoiceName)
                or nameof(SettingsViewModel.QwenTtsVoiceName)
                or nameof(SettingsViewModel.MossTtsVoiceName)
                or nameof(SettingsViewModel.OmniVoiceTtsVoiceName)
                or nameof(SettingsViewModel.TtsOutputName)
                or nameof(SettingsViewModel.DesktopTtsOutputName)
                or nameof(SettingsViewModel.TtsSpeed)
                or nameof(SettingsViewModel.TtsVolume)
                or nameof(SettingsViewModel.QwenTtsToneMode)
                or nameof(SettingsViewModel.QwenTtsInstruct)
                or nameof(SettingsViewModel.QwenTtsVoiceMode)
                or nameof(SettingsViewModel.QwenTtsCloneReferenceAudioPath)
                or nameof(SettingsViewModel.QwenTtsCloneReferenceText)
                or nameof(SettingsViewModel.QwenTtsCloneLanguage)
                or nameof(SettingsViewModel.MossTtsVoiceMode)
                or nameof(SettingsViewModel.MossTtsCloneReferenceAudioPath)
                or nameof(SettingsViewModel.MossTtsCloneLanguage)
                or nameof(SettingsViewModel.OmniVoiceTtsVoiceMode)
                or nameof(SettingsViewModel.OmniVoiceTtsCloneReferenceAudioPath)
                or nameof(SettingsViewModel.OmniVoiceTtsCloneReferenceText)
                or nameof(SettingsViewModel.OmniVoiceTtsCloneLanguage)
                or nameof(SettingsViewModel.SelectedTtsVoiceClonePresetId)
                or nameof(SettingsViewModel.SourceLanguage)
                or nameof(SettingsViewModel.MicSourceLanguage)
                or nameof(SettingsViewModel.DesktopSourceLanguage)
                or nameof(SettingsViewModel.MicTargetTranslationLanguage)
                or nameof(SettingsViewModel.MicTargetTranslationLanguage2)
                or nameof(SettingsViewModel.MicTargetTranslationLanguage3)
                or nameof(SettingsViewModel.DesktopTargetTranslationLanguage)
                or nameof(SettingsViewModel.DesktopTargetTranslationLanguage2)
                or nameof(SettingsViewModel.DesktopTargetTranslationLanguage3);
        }

        private void ClearPendingTts(string reason)
        {
            Interlocked.Increment(ref _ttsScheduleVersion);
            try
            {
                _ttsService.Stop();
                _logger.Debug("Cleared pending TTS tasks. reason={Reason}", reason);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to clear pending TTS tasks. reason={Reason}", reason);
            }
        }
        private void ReconcileAudioCaptureAfterSettingsClosed()
        {
            try
            {
                var desiredMicDevice = ResolveConfiguredMicCaptureDevice();
                if (_micPipeline == null
                    || !_micPipeline.IsCapturing
                    || !string.Equals(_micCaptureDevice, desiredMicDevice, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureMicCaptureStarted(forceRestart: true);
                }
                else
                {
                    SetMicRecognitionEnabled(IsMicListening);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to reconcile mic capture after settings closed.");
            }

            try
            {
                var desiredDesktopRequest = BuildDesktopCaptureRequest(allowDeferredApplicationTarget: true);
                var desiredDesktopKey = desiredDesktopRequest == null
                    ? DesktopApplicationPendingCaptureKey
                    : BuildDesktopCaptureKey(desiredDesktopRequest);

                if (_desktopPipeline == null
                    || !_desktopPipeline.IsCapturing
                    || !string.Equals(_desktopCaptureDevice, desiredDesktopKey, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureDesktopCaptureStarted(forceRestart: true);
                }

                if (IsDesktopApplicationCapturePending())
                {
                    SetDesktopRecognitionEnabled(false);
                    if (IsDesktopListening)
                    {
                        ScheduleDesktopApplicationTargetRetry();
                    }
                    else
                    {
                        StopDesktopApplicationTargetRetry();
                    }
                }
                else
                {
                    StopDesktopApplicationTargetRetry();
                    SetDesktopRecognitionEnabled(IsDesktopListening);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to reconcile desktop capture after settings closed.");
            }

            EnsureMicPassthroughState(forceRestart: true);
        }
        private void OpenSettings()
        {
            if (IsQuickStartGuideVisible && QuickStartStep >= 6)
            {
                // The final quick-start step asks the user to open Settings.
                DismissQuickStartGuide();
            }

            OpenSettings(null);
        }

        private void OpenQuickStartGuide()
        {
            QuickStartStep = 1;
            IsQuickStartGuideVisible = true;
        }

        private void DismissQuickStartGuide()
        {
            IsQuickStartGuideVisible = false;
            QuickStartStep = 1;
            if (_config.HasSeenQuickStartGuide)
            {
                return;
            }

            _config.HasSeenQuickStartGuide = true;
            PersistConfigSafe();
        }

        private void NextQuickStartStep()
        {
            if (QuickStartStep < 6)
            {
                QuickStartStep++;
            }
            else
            {
                DismissQuickStartGuide();
            }
        }

        private void PrevQuickStartStep()
        {
            if (QuickStartStep > 1)
            {
                QuickStartStep--;
            }
        }

        private void OpenQuickStartRecognition()
        {
            DismissQuickStartGuide();
            OpenSettingsAtTab(RecognitionSettingsTabIndex);
        }

        private void OpenQuickStartUpdate()
        {
            DismissQuickStartGuide();
            OpenSettingsAtTab(UpdateSettingsTabIndex);
        }

        public void OpenSettingsAtTab(int targetTabIndex)
        {
            OpenSettings(targetTabIndex);
        }

        private void OpenSettings(int? targetTabIndex)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var settingsVm = App.GetRequiredService<SettingsViewModel>();
                settingsVm.SyncFromMainViewModel(this);
                if (targetTabIndex.HasValue)
                {
                    settingsVm.SelectedSettingsTabIndex = targetTabIndex.Value;
                }
                settingsVm.PropertyChanged -= OnSettingsPropertyChanged;
                settingsVm.PropertyChanged += OnSettingsPropertyChanged;
                if (_settingsWindow != null && _settingsWindow.IsVisible)
                {
                    // Reuse existing window; avoid multiple Settings windows.
                    _settingsWindow.DataContext = settingsVm;
                    _settingsWindow.Activate();
                    _settingsWindow.Focus();
                    return;
                }
                _settingsWindow = new SettingsWindow(settingsVm);
                NonTopmostPopupHelper.AttachIndependent(_settingsWindow, Application.Current?.MainWindow);
                _settingsWindow.Closed += (_, __) =>
                {
                    try
                    {
                        ReconcileAudioCaptureAfterSettingsClosed();
                    }
                    finally
                    {
                        settingsVm.PropertyChanged -= OnSettingsPropertyChanged;
                        _settingsWindow = null;
                    }
                };
                _settingsWindow.Show();
                _settingsWindow.Activate();
            });
        }
        private void PasteManualText()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    ManualInputText = Clipboard.GetText();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to paste text from clipboard.");
                AddLog("TextSend", $"Paste failed: {ex.Message}", "Warning");
            }
        }
        private bool CanSendManualText()
        {
            return !IsSendingManualText && !string.IsNullOrWhiteSpace(ManualInputText);
        }
        private void SendManualText()
        {
            _ = SendManualTextAsync();
        }

        private void PastePhotoTranslationImageFromClipboard()
        {
            _ = PastePhotoTranslationImageFromClipboardAsync();
        }

        private async Task PastePhotoTranslationImageFromClipboardAsync()
        {
            try
            {
                OpenPhotoTranslationSession();
                if (IsPhotoTranslationProcessing || _photoTranslationService.IsPhotoTranslationProcessing)
                {
                    AddLog("Photo", "图片翻译正在处理中，请稍后再粘贴。", "Warning");
                    return;
                }

                var imagePath = ResolveClipboardPhotoTranslationImagePath();
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    AddLog("Photo", "剪贴板中没有可用于图片翻译的图片。", "Warning");
                    return;
                }

                AddLog("Photo", "已从剪贴板读取图片，正在进行图片翻译。");
                var result = await _photoTranslationService.TranslateImageAsync(imagePath).ConfigureAwait(false);
                if (result == null)
                {
                    AddLog("Photo", "剪贴板图片翻译没有返回结果。", "Warning");
                    return;
                }

                await HandleRecognitionResultAsync(result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddLog("Photo", $"粘贴图片翻译失败: {ex.Message}", "Warning");
                _logger.Warning(ex, "Failed to translate pasted clipboard image.");
            }
        }

        private static string ResolveClipboardPhotoTranslationImagePath()
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var item in Clipboard.GetFileDropList())
                {
                    var filePath = item as string;
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        continue;
                    }

                    if (TryResolveClipboardImageFile(filePath, out var imagePath))
                    {
                        return imagePath;
                    }
                }
            }

            if (!Clipboard.ContainsImage())
            {
                return string.Empty;
            }

            var clipboardImage = Clipboard.GetImage();
            return clipboardImage == null
                ? string.Empty
                : SaveBitmapSourceAsPhotoTranslationPng(clipboardImage);
        }

        private static bool TryResolveClipboardImageFile(string filePath, out string imagePath)
        {
            imagePath = string.Empty;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(filePath);
            var extension = Path.GetExtension(fullPath);
            if (PhotoTranslationDirectImageExtensions.Contains(extension))
            {
                imagePath = fullPath;
                return true;
            }

            if (!PhotoTranslationClipboardImageExtensions.Contains(extension))
            {
                return false;
            }

            try
            {
                imagePath = SaveBitmapSourceAsPhotoTranslationPng(LoadBitmapSourceFromFile(fullPath));
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to convert clipboard image file. file={File}", fullPath);
                return false;
            }
        }

        private static BitmapSource LoadBitmapSourceFromFile(string imagePath)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            if (bitmap.CanFreeze && !bitmap.IsFrozen)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }

        private static string SaveBitmapSourceAsPhotoTranslationPng(BitmapSource bitmapSource)
        {
            if (bitmapSource.CanFreeze && !bitmapSource.IsFrozen)
            {
                bitmapSource.Freeze();
            }

            var outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRCLS",
                "PhotoTranslation",
                "Clipboard");
            Directory.CreateDirectory(outputDirectory);

            var outputPath = Path.Combine(
                outputDirectory,
                $"clipboard-photo-{DateTime.UtcNow:yyyyMMddHHmmssfff}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
            return outputPath;
        }

        private void TogglePhotoTranslation()
        {
            if (_isPhotoTranslationSessionOpen
                || _photoTranslationService.IsRunning
                || IsPhotoTranslationArmed
                || _photoTranslationService.IsNextPhotoTranslationArmed
                || IsPhotoTranslationProcessing
                || _photoTranslationService.IsPhotoTranslationProcessing)
            {
                StopPhotoTranslationSession(closeWindow: true, addLog: true, "已关闭拍照翻译窗口，停止监听 VRChat 照片。");
                return;
            }

            RequestPhotoTranslation();
        }

        private void RequestPhotoTranslation()
        {
            try
            {
                OpenPhotoTranslationSession();
                var accepted = _photoTranslationService.RequestNextPhotoTranslation();
                IsPhotoTranslationArmed = accepted && _photoTranslationService.IsNextPhotoTranslationArmed;
                if (!accepted)
                {
                    _isPhotoTranslationSessionOpen = false;
                    ClosePhotoTranslationWindow();
                }
                AddLog(
                    "Photo",
                    accepted
                        ? "已开始等待下一张 VRChat 照片用于拍照翻译。"
                        : "拍照翻译监听启动失败，请检查 VRChat 图片目录配置。",
                    accepted ? "Info" : "Warning");
                RequestSteamVrOverlayUpdate();
            }
            catch (Exception ex)
            {
                _isPhotoTranslationSessionOpen = false;
                IsPhotoTranslationArmed = false;
                ClosePhotoTranslationWindow();
                AddLog("Photo", $"启动下一张照片翻译失败: {ex.Message}", "Warning");
                _logger.Warning(ex, "Failed to request next photo translation.");
                RequestSteamVrOverlayUpdate();
            }
        }

        private void CancelPhotoTranslation()
        {
            try
            {
                StopPhotoTranslationSession(closeWindow: true, addLog: true, "已取消拍照翻译并停止监听 VRChat 照片。");
            }
            catch (Exception ex)
            {
                AddLog("Photo", $"取消拍照翻译失败: {ex.Message}", "Warning");
                _logger.Warning(ex, "Failed to cancel next photo translation.");
                RequestSteamVrOverlayUpdate();
            }
        }

        private async Task SendManualTextAsync()
        {
            var input = ManualInputText?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }
            IsSendingManualText = true;
            try
            {
                await ProcessTextInputAsync(input).ConfigureAwait(false);
                QueueUi(() => ManualInputText = string.Empty, DispatcherPriority.Background);
            }
            finally
            {
                IsSendingManualText = false;
            }
        }

        private async Task ProcessTextInputAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            var normalizedInput = input.Trim();
            var result = new TranscriptionResult
            {
                Text = normalizedInput,
                Original = normalizedInput,
                Source = "TextSend",
                SourceLanguage = _config.ResolveSourceLanguage("TextSend"),
                TargetLanguage = _config.ResolvePrimaryTargetLanguage("TextSend"),
                TargetLanguage2 = _config.ResolveSecondaryTargetLanguage("TextSend"),
                TargetLanguage3 = _config.ResolveTertiaryTargetLanguage("TextSend"),
                IsFinal = true,
                Confidence = 1.0f,
                CapturedAtUtc = DateTime.UtcNow
            };

            await HandleRecognitionResultAsync(result).ConfigureAwait(false);
        }
        private static string BuildAppVersionText()
        {
            try
            {
                var displayVersion = AppVersionHelper.GetDisplayVersion();
                return string.IsNullOrWhiteSpace(displayVersion) ? "VRCLS" : $"VRCLS v{displayVersion}";
            }
            catch
            {
                return "VRCLS";
            }
        }

        private static Brush CreateMainWindowBackgroundBrush(double opacity)
        {
            var alpha = (byte)Math.Round(AppConfig.NormalizeMainWindowBackgroundOpacity(opacity) * byte.MaxValue);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            brush.Freeze();
            return brush;
        }

        private void PersistConfigSafe()
        {
            try
            {
                VRCLS.UI.App.SaveConfigToDisk(_config);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to save config.");
            }
        }

        private void ApplyMainWindowLanguageMask(TranscriptLanguageMask mask)
        {
            _updatingMainLanguageMask = true;
            try
            {
                SetField(ref _showOriginal, (mask & TranscriptLanguageMask.Original) != 0, nameof(ShowOriginal));
                SetField(ref _showTranslation1, (mask & TranscriptLanguageMask.Translation1) != 0, nameof(ShowTranslation1));
                SetField(ref _showTranslation2, (mask & TranscriptLanguageMask.Translation2) != 0, nameof(ShowTranslation2));
                SetField(ref _showTranslation3, (mask & TranscriptLanguageMask.Translation3) != 0, nameof(ShowTranslation3));
            }
            finally
            {
                _updatingMainLanguageMask = false;
            }
        }

        private void UpdateMainWindowLanguageMaskFromToggles()
        {
            if (_updatingMainLanguageMask) return;
            var mask = TranscriptLanguageMask.None;
            if (_showOriginal) mask |= TranscriptLanguageMask.Original;
            if (_showTranslation1) mask |= TranscriptLanguageMask.Translation1;
            if (_showTranslation2) mask |= TranscriptLanguageMask.Translation2;
            if (_showTranslation3) mask |= TranscriptLanguageMask.Translation3;
            if (mask == TranscriptLanguageMask.None)
            {
                mask = TranscriptLanguageMask.Translation1;
                ApplyMainWindowLanguageMask(mask);
            }
            _config.MainWindowLanguageMask = mask;
            RequestSteamVrOverlayUpdate();
            PersistConfigSafe();
        }

        private string FormatChatbox(string original, string[] translations, string? source)
        {
            var mode = GetEffectiveMode(source);
            var t1 = translations.ElementAtOrDefault(0) ?? string.Empty;
            var t2 = translations.ElementAtOrDefault(1) ?? string.Empty;
            var t3 = translations.ElementAtOrDefault(2) ?? string.Empty;
            string template = mode == "text"
                ? _config.VrcChatboxFormatText ?? "{text}"
                : _config.VrcChatboxFormatNew ?? "{translatedText}\n{translatedText2}\n{translatedText3}\n{text}";
            if (string.IsNullOrWhiteSpace(template)) return string.Empty;
            string result = template
                .Replace("{text}", original)
                .Replace("{translatedText}", t1)
                .Replace("{translatedText2}", t2)
                .Replace("{translatedText3}", t3);
            return NormalizeChatboxOutput(result);
        }

        private static string NormalizeChatboxOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            return string.Join("\n", lines);
        }

        public bool IsMicListening
        {
            get => _isMicListening;
            set => SetField(ref _isMicListening, value);
        }
        public bool IsDesktopListening
        {
            get => _isDesktopListening;
            set => SetField(ref _isDesktopListening, value);
        }
        public bool IsOverlayVisible
        {
            get => _isOverlayVisible;
            set => SetField(ref _isOverlayVisible, value);
        }
        public bool IsOverlayPinned
        {
            get => _isOverlayPinned;
            set
            {
                if (SetField(ref _isOverlayPinned, value))
                {
                    _config.MainWindowPinned = value;
                    PersistConfigSafe();
                }
            }
        }
        public bool IsOverlayLocked
        {
            get => _isOverlayLocked;
            set => SetField(ref _isOverlayLocked, value);
        }
        public double MainWindowOpacity
        {
            get => _mainWindowOpacity;
            private set => SetField(ref _mainWindowOpacity, value);
        }
        public double MainWindowBackgroundOpacity
        {
            get => _mainWindowBackgroundOpacity;
            private set
            {
                if (SetField(ref _mainWindowBackgroundOpacity, value))
                {
                    OnPropertyChanged(nameof(MainWindowBackgroundBrush));
                }
            }
        }
        public Brush MainWindowBackgroundBrush => CreateMainWindowBackgroundBrush(_mainWindowBackgroundOpacity);
        public bool ShowOriginal
        {
            get => _showOriginal;
            set
            {
                if (SetField(ref _showOriginal, value))
                {
                    UpdateMainWindowLanguageMaskFromToggles();
                }
            }
        }

        public bool ShowTranslation1
        {
            get => _showTranslation1;
            set
            {
                if (SetField(ref _showTranslation1, value))
                {
                    UpdateMainWindowLanguageMaskFromToggles();
                }
            }
        }

        public bool ShowTranslation2
        {
            get => _showTranslation2;
            set
            {
                if (SetField(ref _showTranslation2, value))
                {
                    UpdateMainWindowLanguageMaskFromToggles();
                }
            }
        }

        public bool ShowTranslation3
        {
            get => _showTranslation3;
            set
            {
                if (SetField(ref _showTranslation3, value))
                {
                    UpdateMainWindowLanguageMaskFromToggles();
                }
            }
        }

        public string MicText
        {
            get => _micText;
            set => SetField(ref _micText, value);
        }
        public string MicTrans
        {
            get => _micTranslation;
            set => SetField(ref _micTranslation, value);
        }
        public string DesktopText
        {
            get => _desktopText;
            set => SetField(ref _desktopText, value);
        }
        public string DesktopTrans
        {
            get => _desktopTranslation;
            set => SetField(ref _desktopTranslation, value);
        }
        public string SelectedMicDevice
        {
            get => _selectedMicDevice;
            set
            {
                if (SetField(ref _selectedMicDevice, value))
                {
                    // ���浽����ʱ������ʾ��ת��������ֵ
                    _config.MicName = value == DefaultDeviceDisplayName ? DefaultDeviceConfigValue : value;
                }
            }
        }
        public string CurrentSource
        {
            get => _currentSource;
            set => SetField(ref _currentSource, value);
        }
        public bool IsMicSpeaking
        {
            get => _isMicSpeaking;
            set => SetField(ref _isMicSpeaking, value);
        }
        public bool IsDesktopSpeaking
        {
            get => _isDesktopSpeaking;
            set => SetField(ref _isDesktopSpeaking, value);
        }
        public float MicVolume
        {
            get => _micVolume;
            set => SetField(ref _micVolume, value);
        }
        public float DesktopVolume
        {
            get => _desktopVolume;
            set => SetField(ref _desktopVolume, value);
        }
        public string ManualInputText
        {
            get => _manualInputText;
            set
            {
                if (SetField(ref _manualInputText, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }
        public bool IsSendingManualText
        {
            get => _isSendingManualText;
            set
            {
                if (SetField(ref _isSendingManualText, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }
        public bool IsPhotoTranslationArmed
        {
            get => _isPhotoTranslationArmed;
            set => SetField(ref _isPhotoTranslationArmed, value);
        }
        public bool IsPhotoTranslationProcessing
        {
            get => _isPhotoTranslationProcessing;
            set => SetField(ref _isPhotoTranslationProcessing, value);
        }
        public bool HasPhotoTranslationResults => PhotoTranslationResults.Count > 0;
        public PhotoTranslationResultItem? CurrentPhotoTranslationResult => _currentPhotoTranslationResult;
        public string CurrentPhotoTranslationPageText => PhotoTranslationResults.Count == 0
            ? string.Empty
            : $"{_currentPhotoTranslationResultIndex + 1} / {PhotoTranslationResults.Count}";
        public bool CanNavigatePhotoTranslationPrevious => CanShowPreviousPhotoTranslationResult();
        public bool CanNavigatePhotoTranslationNext => CanShowNextPhotoTranslationResult();
        public string ConnectionState
        {
            get => _connectionState;
            set => SetField(ref _connectionState, value);
        }
        public string AppVersionText
        {
            get => _appVersionText;
            set => SetField(ref _appVersionText, value);
        }
        public string UiLanguage
        {
            get => _uiLanguage;
            set
            {
                var normalized = App.NormalizeUiLanguage(value);
                if (SetField(ref _uiLanguage, normalized))
                {
                    _config.UiLanguage = normalized;
                    App.ApplyUiLanguage(normalized);
                    RefreshUiLanguageOptions();
                    IsUiLanguageMenuOpen = false;
                    OnPropertyChanged(nameof(ConnectionStateText));
                    SyncSettingsUiLanguage(normalized);
                    PersistConfigSafe();
                    RequestSteamVrOverlayUpdate();
                }
            }
        }
        public bool IsUiLanguageMenuOpen
        {
            get => _isUiLanguageMenuOpen;
            set => SetField(ref _isUiLanguageMenuOpen, value);
        }
        public bool IsQuickStartGuideVisible
        {
            get => _isQuickStartGuideVisible;
            set => SetField(ref _isQuickStartGuideVisible, value);
        }
        public int QuickStartStep
        {
            get => _quickStartStep;
            set => SetField(ref _quickStartStep, value);
        }
        public string HomeAlertMessage
        {
            get => _homeAlertMessage;
            set => SetField(ref _homeAlertMessage, value);
        }
        public string RateLimitNoticeMessage
        {
            get => _rateLimitNoticeMessage;
            set => SetField(ref _rateLimitNoticeMessage, value);
        }
                public string ConnectionStateText => ConnectionState switch
        {
            "Connected" => GetUiString("ConnectionStateConnected", "已连接"),
            "Connecting" => GetUiString("ConnectionStateConnecting", "连接中"),
            _ => GetUiString("ConnectionStateDisconnected", "未连接")
        };
        public void Dispose()
        {
            var disposeStopwatch = Stopwatch.StartNew();
            _logger.Information("释放 MainViewModel 资源");
            try
            {
                if (_uiFlushTimer != null)
                {
                    _uiFlushTimer.Stop();
                    _uiFlushTimer.Tick -= OnUiFlushTimerTick;
                    _uiFlushTimer = null;
                }
                DisposeHealthMonitor();
                if (_pipelineRestartTimer != null)
                {
                    _pipelineRestartTimer.Stop();
                    _pipelineRestartTimer.Tick -= OnPipelineRestartTimerTick;
                    _pipelineRestartTimer = null;
                }
                if (_desktopApplicationTargetRetryTimer != null)
                {
                    _desktopApplicationTargetRetryTimer.Stop();
                    _desktopApplicationTargetRetryTimer.Tick -= OnDesktopApplicationTargetRetryTick;
                    _desktopApplicationTargetRetryTimer = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to stop UI flush timer.");
            }
            try
            {
                lock (_steamVrUpdateLock)
                {
                    _steamVrUpdateCts?.Cancel();
                    _steamVrUpdateCts?.Dispose();
                    _steamVrUpdateCts = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose SteamVR update throttle.");
            }
            try
            {
                _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
                _hotkeyService.KeyStateChanged -= OnKeyStateChanged;
                _hotkeyService.Stop();
                _logger.Debug("热键服务已停止");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "停止热键服务失败");
            }
            try
            {
                _microphonePassthroughService.Stop();
                _logger.Debug("麦克风原声透传已停止");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "停止麦克风原声透传失败");
            }
            try
            {
                _vadCalibrationService.MonitoringChanged -= OnVadCalibrationMonitoringChanged;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "释放 VAD 校准订阅失败");
            }
            try
            {
                _logger.Debug("准备释放麦克风管道。IsMicListening={IsMicListening}, HasMicPipeline={HasMicPipeline}", IsMicListening, _micPipeline != null);
                UnsubscribePipelineEvents(_micPipeline, isMic: true);
                _micPipeline?.Dispose();
                _logger.Debug("麦克风管道已释放，累计耗时 {ElapsedMilliseconds} ms", disposeStopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "释放麦克风管道失败");
            }
            try
            {
                _logger.Debug(
                    "准备释放监听音频管道。IsDesktopListening={IsDesktopListening}, HasDesktopPipeline={HasDesktopPipeline}, SourceMode={SourceMode}",
                    IsDesktopListening,
                    _desktopPipeline != null,
                    _config.DesktopAudioSourceModeValue);
                UnsubscribePipelineEvents(_desktopPipeline, isMic: false);
                _desktopPipeline?.Dispose();
                _logger.Debug("监听音频管道已释放，累计耗时 {ElapsedMilliseconds} ms", disposeStopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "释放桌面管道失败");
            }
            try
            {
                if (_commandService is IDisposable disposableCmd)
                {
                    disposableCmd.Dispose();
                }
                _commandService.CommandExecuted -= OnCommandExecuted;
                _logger.Debug("命令服务已释放");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "释放命令服务失败");
            }
            try
            {
                _modelUpdateService.ModelsChanged -= OnModelsChanged;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to unsubscribe model update events.");
            }
            try
            {
                _photoTranslationService.TextRecognized -= OnPhotoTextRecognized;
                _photoTranslationService.NextPhotoTranslationArmedChanged -= OnNextPhotoTranslationArmedChanged;
                _photoTranslationService.PhotoTranslationProcessingChanged -= OnPhotoTranslationProcessingChanged;
                _photoTranslationService.Stop();
                _isPhotoTranslationSessionOpen = false;
                ClosePhotoTranslationWindow();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "停止拍照翻译服务失败");
            }
            try
            {
                _ttsService.ErrorOccurred -= OnServiceError;
                _authService.StateChanged -= OnAuthStateChanged;
                _authService.ErrorOccurred -= OnServiceError;
                _authService.UserNotification -= OnAuthUserNotification;
                _authService.AccountStatusUpdated -= OnAccountStatusUpdated;
                _logger.Debug("认证服务事件已取消订阅");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "取消订阅认证服务事件失败");
            }
            try
            {
                lock (_homeAlertLock)
                {
                    _homeAlertCts?.Cancel();
                    _homeAlertCts?.Dispose();
                    _homeAlertCts = null;
                }

                lock (_rateLimitNoticeLock)
                {
                    _rateLimitSources.Clear();
                    _rateLimitRules.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to dispose home alert throttle.");
            }
            try
            {
                _oscService.MessageReceived -= OnOscMessageReceived;
                lock (_oscSendLock)
                {
                    _oscSendCts?.Cancel();
                    _oscSendCts?.Dispose();
                    _oscSendCts = null;
                }
                _oscService.StopOscServer();
                _logger.Debug("OSC 服务器已停止");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "停止 OSC 服务失败");
            }
            try
            {
                _steamVrService.Connected -= OnSteamVrConnected;
                _steamVrService.Disconnected -= OnSteamVrDisconnected;
                _steamVrService.ActionRequested -= OnSteamVrActionRequested;
                _logger.Debug("SteamVR 服务事件已取消订阅");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "取消订阅 SteamVR 服务事件失败");
            }
            _logger.Information("MainViewModel 资源释放完成，总耗时 {ElapsedMilliseconds} ms", disposeStopwatch.ElapsedMilliseconds);
        }
    }
}
