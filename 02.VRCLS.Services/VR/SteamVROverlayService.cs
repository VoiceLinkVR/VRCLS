using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Valve.VR;
using VRCLS.Core.Interfaces;
using VRCLS.Core.Models;

namespace VRCLS.Services.VR
{
    /// <summary>
    /// Minimal SteamVR overlay that renders text to a bitmap and uploads it via SetOverlayRaw.
    /// If SteamVR/OpenVR isn't available, this service becomes a no-op.
    /// </summary>
    public class SteamVROverlayService : ISteamVRService
    {
        private static readonly ILogger _logger = Log.ForContext<SteamVROverlayService>();
        private static readonly string AppVersionLabel = BuildAppVersionLabel();
        private readonly AppConfig _config;
        private readonly object _lock = new();
        private readonly SemaphoreSlim _openVrOperationGate = new(1, 1);
        private readonly TrackedDevicePose_t[] _trackedDevicePoses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
        private int _openVrOperationSequence;
        private const string SteamVrAppKey = "vrcls.overlay";
        private static readonly uint VrControllerStateSize = (uint)Marshal.SizeOf<VRControllerState_t>();

        private bool _initInProgress;
        private bool _restartInProgress;
        private bool _disposed;
        private bool _desiredVisible;
        private bool _appliedVisible;
        private ulong _overlayHandle;
        private ulong _cursorOverlayHandle;
        private ulong _photoOverlayHandle;
        private ulong _photoImageOverlayHandle;
        private bool _cursorOverlayIsSubview;
        private bool _cursorOverlayUsesNativeCursor;
        private bool _cursorOverlayFirstVisibleLogEmitted;
        private bool _photoOverlayFirstVisibleLogEmitted;
        private bool _photoPanelAppliedVisible;
        private bool _photoImageOverlayAppliedVisible;
        private bool _photoPanelDismissed;
        private int _hand;
        private float _size;
        private string _lastText = string.Empty;
        private string _status = "Not initialized";
        private string _startupRegistrationStatus = "SteamVR auto-launch not configured";
        private CancellationTokenSource? _attachRetryCts;
        private DateTime _lastControllerMissingLogUtc = DateTime.MinValue;
        private uint _panelTrackedDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
        private HmdMatrix34_t _panelTransformInDeviceSpace;
        private float _panelAppliedWidthMeters;
        private bool _lastTriggerPressed;
        private uint _cursorRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
        private float _cursorPointX, _cursorPointY, _cursorPointZ;
        private float _cursorNormalX, _cursorNormalY, _cursorNormalZ;
        private float _cursorLocalX, _cursorLocalY, _cursorLocalZ;
        private float _cursorRayDistanceMeters = CursorLaserDefaultLengthMeters;
        private float _cursorAppliedRayDistanceMeters = float.NaN;
        private uint _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;

        // Simple interactive "dashboard-like" panel rendering.
        // Match "wrist/palm" usage: less horizontal, bigger readable text.
        private const int PanelWidth = 820;
        private const int PanelHeight = 720;
        private const int PhotoPanelWidth = 760;
        private const int PhotoPanelHeight = 520;
        private const int PhotoPanelTextureScale = 2;
        private const float PhotoImageOverlayDepthOffsetMeters = 0.003f;
        private const int PhotoImageOverlayFallbackMaxDimension = 4096;
        private const float BaseTranscriptFontSize = 22f;
        private const int CursorDotTextureSize = 64;
        private const float CursorDotOverlayWidthMeters = 0.018f;
        private const float CursorPanelDepthOffsetMeters = 0.003f;
        private const float PhotoPanelWidthScale = 0.86f;
        private const float PhotoPanelGapMeters = 0.030f;
        private const float PhotoPanelDepthOffsetMeters = 0.006f;
        private const float CursorLaserDefaultLengthMeters = 0.7f;
        private const float CursorLaserMinLengthMeters = 0.05f;
        private const float CursorLaserMaxLengthMeters = 5.0f;
        private const bool DrawCursorInPanelBackup = false;
        private const uint PanelOverlaySortOrder = 0;
        private const uint PhotoOverlaySortOrder = 20;
        private const uint PhotoImageOverlaySortOrder = 30;
        private const uint CursorOverlaySortOrder = 100;
        private static readonly Color SourceDesktopColor = Color.FromArgb(170, 255, 152, 0);
        private static readonly Color SourceMicColor = Color.FromArgb(170, 76, 175, 80);
        private static readonly Color SourcePhotoColor = Color.FromArgb(170, 33, 150, 243);
        private static readonly Color ToggleOffColor = Color.FromArgb(200, 140, 140, 140);
        private static readonly Color ToggleOpenColor = Color.FromArgb(235, 76, 175, 80);

        private SteamVrOverlayPanelState _panelState = new();
        private bool _panelDirty = true;
        private int _panelStateHash;
        private DateTime _lastPanelRenderUtc = DateTime.MinValue;
        private IReadOnlyList<SteamVrOverlayTranscript> _photoPanelTranscripts = Array.Empty<SteamVrOverlayTranscript>();
        private bool _photoPanelDirty = true;
        private int _photoPanelStateHash;
        private int _photoPanelLatestTranscriptHash;
        private int _photoPanelPageIndex;
        private int _photoPanelTextScrollOffset;
        private bool _photoPanelTextDragging;
        private float _photoPanelTextDragLastY;
        private int _photoPanelTextDragPageIndex = -1;
        private int _photoImageOverlayContentHash;
        private string _photoImageOverlayPath = string.Empty;
        private int _photoImageOverlaySourceWidth;
        private int _photoImageOverlaySourceHeight;
        private DateTime _lastPhotoPanelRenderUtc = DateTime.MinValue;
        private HmdMatrix34_t _photoPanelTransformInDeviceSpace;
        private float _photoPanelAppliedWidthMeters;
        private static readonly TimeSpan PanelRenderMinInterval = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan PhotoPanelRenderMinInterval = TimeSpan.FromMilliseconds(80);
        private static readonly TimeSpan PanelClickDebounceInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan PanelUnlockConfirmInterval = TimeSpan.FromSeconds(3);
        private bool _panelLocked;
        private DateTime _panelUnlockConfirmUntilUtc = DateTime.MinValue;
        private bool _dropdownOpen;
        private bool _settingsOpen;
        private SteamVrLanguagePickerTarget _languagePickerTarget = SteamVrLanguagePickerTarget.None;
        private int _languagePickerPageIndex;
        private int _scrollIndex;
        private int _selectedIndex;
        private DateTime _lastPanelClickUtc = DateTime.MinValue;
        private float _lastPanelClickX = float.NaN;
        private float _lastPanelClickY = float.NaN;
        private float _cursorU = 0.5f; // 0..1
        private float _cursorV = 0.2f; // 0..1
        private bool _cursorValid;
        private DateTime _lastCursorUpdateUtc = DateTime.MinValue;
        private DateTime _lastControllerPointerPollUtc = DateTime.MinValue;
        private DateTime _lastOverlayEventPollUtc = DateTime.MinValue;
        private DateTime _lastPlacementRefreshUtc = DateTime.MinValue;
        private bool _cursorDirty;
        private float _cursorAppliedU = float.NaN;
        private float _cursorAppliedV = float.NaN;
        private bool _cursorAppliedVisible;
        private DateTime _lastCursorOverlayFailureUtc = DateTime.MinValue;
        private DateTime _lastCursorOverlayFailureLogUtc = DateTime.MinValue;
        private List<uint> _pointerDeviceCandidatesCache = new();
        private DateTime _pointerDeviceCandidatesCacheUtc = DateTime.MinValue;
        private int _pointerDeviceCandidatesCacheHand = -1;
        private SteamVrTrackingDeviceMode _pointerDeviceCandidatesCacheMode = SteamVrTrackingDeviceMode.Auto;
        private CancellationTokenSource? _eventLoopCts;
        private static readonly TimeSpan IdleControllerPointerPollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ActiveControllerPointerPollInterval = TimeSpan.FromMilliseconds(24);
        private static readonly TimeSpan IdleOverlayEventPollInterval = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan ActiveOverlayEventPollInterval = TimeSpan.FromMilliseconds(90);
        private static readonly TimeSpan EventLoopIdleInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan EventLoopActiveInteractionInterval = TimeSpan.FromMilliseconds(24);
        private static readonly TimeSpan EventLoopRenderInterval = TimeSpan.FromMilliseconds(33);
        private static readonly TimeSpan CursorLostGraceInterval = TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan CursorOverlayFailureRetryInterval = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan PlacementRefreshInterval = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan PointerDeviceCandidateCacheInterval = TimeSpan.FromSeconds(1);
        public bool IsConnected { get; private set; }

        public string Status
        {
            get
            {
                lock (_lock) return _status;
            }
        }

        public string StartupRegistrationStatus
        {
            get
            {
                lock (_lock) return _startupRegistrationStatus;
            }
        }

        public event EventHandler? Connected;
        public event EventHandler? Disconnected;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? StartupRegistrationStatusChanged;
        public event EventHandler<SteamVrOverlayAction>? ActionRequested;

        private static string BuildAppVersionLabel()
        {
            try
            {
                var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                return version == null ? "" : $"v{version}";
            }
            catch
            {
                return "";
            }
        }

        private bool IsDiagnosticLoggingEnabled => _config.SteamVrDiagnosticLogging;

        private void LogDiagnosticDebug(string messageTemplate, params object?[] propertyValues)
        {
            if (IsDiagnosticLoggingEnabled)
            {
                _logger.Debug(messageTemplate, propertyValues);
            }
        }

        private void LogDiagnosticInformation(string messageTemplate, params object?[] propertyValues)
        {
            if (IsDiagnosticLoggingEnabled)
            {
                _logger.Information(messageTemplate, propertyValues);
            }
        }

        public SteamVROverlayService(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _hand = Math.Clamp(_config.SteamVrHand, 0, 1);
            _config.SteamVrHand = _hand;
            _size = _config.SteamVrSize;
            _desiredVisible = _config.TextInSteamVr;
            _startupRegistrationStatus = _config.SteamVrAutoLaunch
                ? "SteamVR auto-launch pending registration"
                : "SteamVR auto-launch disabled";
        }

        public Task InitializeAsync()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    LogDiagnosticDebug("SteamVR overlay InitializeAsync ignored because service is disposed.");
                    return Task.CompletedTask;
                }

                // InitializeAsync is called when users want the overlay enabled.
                _desiredVisible = true;

                if (IsConnected || _initInProgress || _restartInProgress)
                {
                    LogDiagnosticDebug(
                        "SteamVR overlay InitializeAsync skipped. connected={Connected}, initInProgress={InitInProgress}, restartInProgress={RestartInProgress}",
                        IsConnected,
                        _initInProgress,
                        _restartInProgress);
                    return Task.CompletedTask;
                }

                _initInProgress = true;
            }

            LogDiagnosticInformation(
                "SteamVR overlay initialization queued. hand={Hand}, size={Size}, anchorMode={AnchorMode}, autoLaunch={AutoLaunch}",
                _hand,
                _size,
                _config.SteamVrAnchorModeValue,
                _config.SteamVrAutoLaunch);

            return Task.Run(async () =>
            {
                try
                {
                    // Retry a bit to handle:
                    // - SteamVR started after the app
                    // - Controllers appearing late
                    // NOTE: If users enable SteamVR overlay before launching SteamVR, we should keep waiting and
                    // auto-retry instead of giving up and requiring manual re-toggle.
                    const int fastAttempts = 10;
                    for (var attempt = 1; ; attempt++)
                    {
                        if (_disposed) return;
                        if (IsConnected) return;

                        if (!_desiredVisible)
                        {
                            SetStatus("SteamVR overlay disabled");
                            return;
                        }

                        // Useful for diagnosis when users report "toggle on but nothing shows".
                        var openVrApiPath = Path.Combine(AppContext.BaseDirectory, "openvr_api.dll");
                        if (!File.Exists(openVrApiPath))
                        {
                            SetStatus($"openvr_api.dll missing: {openVrApiPath}");
                            _logger.Warning("openvr_api.dll missing next to app: {Path}", openVrApiPath);
                            return; // Don't spam retries if the native dll is missing.
                        }

                        var steamVrRunning = IsSteamVrRunning();
                        if (!steamVrRunning)
                        {
                            if (ShouldLogOpenVrRetryAttempt(attempt, fastAttempts))
                            {
                                LogDiagnosticDebug(
                                    "SteamVR overlay init waiting for SteamVR process. attempt={Attempt}, fastAttempts={FastAttempts}",
                                    attempt,
                                    fastAttempts);
                            }

                            SetStatus(attempt <= fastAttempts
                                ? $"Waiting for SteamVR... ({attempt}/{fastAttempts})"
                                : "Waiting for SteamVR... (auto retry)");
                            await Task.Delay(attempt <= fastAttempts ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            continue;
                        }

                        var hmdPresent = SafeIsHmdPresent();
                        if (!hmdPresent)
                        {
                            if (ShouldLogOpenVrRetryAttempt(attempt, fastAttempts))
                            {
                                LogDiagnosticDebug(
                                    "SteamVR overlay init waiting for HMD. attempt={Attempt}, fastAttempts={FastAttempts}",
                                    attempt,
                                    fastAttempts);
                            }

                            SetStatus(attempt <= fastAttempts
                                ? $"SteamVR running, waiting for HMD... ({attempt}/{fastAttempts})"
                                : "SteamVR running, waiting for HMD... (auto retry)");
                            await Task.Delay(attempt <= fastAttempts ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            continue;
                        }

                        if (ShouldLogOpenVrRetryAttempt(attempt, fastAttempts))
                        {
                            LogDiagnosticDebug("SteamVR overlay init attempt {Attempt}: SteamVR process and HMD are ready.", attempt);
                        }

                        if (await TryInitializeOnceAsync().ConfigureAwait(false))
                        {
                            LogDiagnosticInformation("SteamVR overlay initialization task completed successfully. attempt={Attempt}", attempt);
                            return;
                        }

                        // Backoff if init fails for any reason (SteamVR still booting, overlay subsystem not ready, etc).
                        var backoffSeconds = Math.Min(5.0, 1.0 + Math.Min(attempt, 20) * 0.2);
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "SteamVR overlay init exception");
                    SetStatus($"SteamVR overlay init exception: {ex.GetType().Name}");
                    SetDisconnected();
                    CleanupFailedInit();
                }
                finally
                {
                    lock (_lock) _initInProgress = false;
                }
            });
        }

        public void SetVisible(bool visible)
        {
            ulong handle = 0;
            ulong cursorHandle = 0;
            ulong photoHandle = 0;
            ulong photoImageHandle = 0;
            var canToggleNow = false;
            lock (_lock)
            {
                if (_disposed) return;
                _desiredVisible = visible;
                if (visible) _panelDirty = true;
                canToggleNow = IsConnected && _overlayHandle != 0;
                if (canToggleNow) handle = _overlayHandle;
                cursorHandle = _cursorOverlayHandle;
                photoHandle = _photoOverlayHandle;
                photoImageHandle = _photoImageOverlayHandle;
                if (canToggleNow && _appliedVisible == visible) return;
            }

            if (!visible)
            {
                SetStatus("SteamVR overlay disabled");
            }

            if (!canToggleNow) return;

            if (visible && !ApplyPlacementInternal())
            {
                return;
            }

            try
            {
                var overlay = Valve.VR.OpenVR.Overlay;
                if (overlay == null) return;
                if (visible)
                {
                    overlay.ShowOverlay(handle);
                }
                else
                {
                    overlay.HideOverlay(handle);
                    if (cursorHandle != 0)
                    {
                        overlay.HideOverlay(cursorHandle);
                    }
                    if (photoHandle != 0)
                    {
                        overlay.HideOverlay(photoHandle);
                    }
                    if (photoImageHandle != 0)
                    {
                        overlay.HideOverlay(photoImageHandle);
                    }
                }

                lock (_lock)
                {
                    _appliedVisible = visible;
                    if (!visible)
                    {
                        _cursorAppliedVisible = false;
                        _photoPanelAppliedVisible = false;
                        ResetPhotoImageOverlayStateLocked(resetContent: false);
                    }
                }

                ApplyPhotoPanelVisibility(overlay);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "SetVisible failed");
            }
        }

        public void UpdateText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_lock)
            {
                if (_disposed) return;
                if (text == _lastText) return;
                _lastText = text;
                _panelDirty = true;
            }
        }

        public void UpdatePanel(SteamVrOverlayPanelState state)
        {
            if (state == null) return;

            // Snapshot the list to avoid concurrent modification issues (UI thread collection etc).
            // Treat the state as immutable after this point.
            var frozen = state.Transcripts is null
                ? Array.Empty<SteamVrOverlayTranscript>()
                : state.Transcripts.ToArray();
            var mainTranscripts = frozen
                .Where(t => !IsPhotoTranscript(t))
                .ToArray();
            var photoTranscripts = frozen
                .Where(IsPhotoTranscript)
                .Take(12)
                .ToArray();
            var hash = ComputePanelStateHash(state, mainTranscripts);
            var photoHash = ComputePhotoPanelStateHash(state, photoTranscripts);
            var latestPhotoHash = ComputePhotoTranscriptIdentityHash(photoTranscripts.Length > 0 ? photoTranscripts[0] : null);

            lock (_lock)
            {
                if (_disposed) return;

                var panelChanged = hash != _panelStateHash;
                var photoChanged = photoHash != _photoPanelStateHash;
                var photoProcessingStarted = state.IsPhotoTranslationProcessing && !_panelState.IsPhotoTranslationProcessing;
                if (!panelChanged && !photoChanged)
                {
                    return;
                }

                if (panelChanged)
                {
                    _panelStateHash = hash;
                    _panelState = new SteamVrOverlayPanelState
                    {
                        IsMicListening = state.IsMicListening,
                        IsDesktopListening = state.IsDesktopListening,
                        IsPhotoTranslationArmed = state.IsPhotoTranslationArmed,
                        IsPhotoTranslationProcessing = state.IsPhotoTranslationProcessing,
                        MicVolume = state.MicVolume,
                        DesktopVolume = state.DesktopVolume,
                        IsTtsEnabled = state.IsTtsEnabled,
                        IsTtsMicPassthroughEnabled = state.IsTtsMicPassthroughEnabled,
                        TtsMicPassthroughVolume = Math.Clamp(state.TtsMicPassthroughVolume, 0f, 4f),
                        MicTtsSpeakMask = state.MicTtsSpeakMask,
                        DesktopTtsSpeakMask = state.DesktopTtsSpeakMask,
                        NonTextOpacity = Math.Clamp(state.NonTextOpacity, 0f, 1f),
                        FontScale = Math.Clamp(state.FontScale, 0.70f, 1.40f),
                        MicTextColor = AppConfig.NormalizeHexColor(state.MicTextColor, AppConfig.DefaultSteamVrMicTextColor),
                        DesktopTextColor = AppConfig.NormalizeHexColor(state.DesktopTextColor, AppConfig.DefaultSteamVrDesktopTextColor),
                        SourceMask = state.SourceMask & ~TranscriptSourceMask.Photo,
                        LanguageMask = state.LanguageMask,
                        UiLanguage = state.UiLanguage,
                        MicSourceLanguage = state.MicSourceLanguage,
                        MicTargetLanguage = state.MicTargetLanguage,
                        MicTargetLanguage2 = state.MicTargetLanguage2,
                        MicTargetLanguage3 = state.MicTargetLanguage3,
                        DesktopSourceLanguage = state.DesktopSourceLanguage,
                        DesktopTargetLanguage = state.DesktopTargetLanguage,
                        DesktopTargetLanguage2 = state.DesktopTargetLanguage2,
                        DesktopTargetLanguage3 = state.DesktopTargetLanguage3,
                        Transcripts = mainTranscripts
                    };

                    if (_selectedIndex >= mainTranscripts.Length)
                    {
                        _selectedIndex = mainTranscripts.Length == 0 ? 0 : mainTranscripts.Length - 1;
                    }

                    _panelDirty = true;
                }

                if (photoChanged)
                {
                    _photoPanelStateHash = photoHash;
                    _photoPanelTranscripts = photoTranscripts;
                    _photoPanelDirty = true;
                    if (photoProcessingStarted
                        || (latestPhotoHash != 0 && latestPhotoHash != _photoPanelLatestTranscriptHash))
                    {
                        _photoPanelDismissed = false;
                        _photoPanelPageIndex = 0;
                        ResetPhotoPanelTextScrollLocked();
                    }
                    else if (_photoPanelPageIndex >= photoTranscripts.Length)
                    {
                        _photoPanelPageIndex = Math.Max(0, photoTranscripts.Length - 1);
                        ResetPhotoPanelTextScrollLocked();
                    }
                    else if (photoTranscripts.Length == 0)
                    {
                        ResetPhotoPanelTextScrollLocked();
                    }
                    _photoPanelLatestTranscriptHash = latestPhotoHash;
                }
            }
        }

        private static int QuantizeVolume(float value)
        {
            if (value <= 0f) return 0;
            if (value >= 1f) return 100;
            return (int)Math.Round(value * 100f);
        }

        private static int ComputePanelStateHash(SteamVrOverlayPanelState state, SteamVrOverlayTranscript[] transcripts)
        {
            var hash = new HashCode();
            hash.Add(state.IsMicListening);
            hash.Add(state.IsDesktopListening);
            hash.Add(state.IsPhotoTranslationArmed);
            hash.Add(state.IsPhotoTranslationProcessing);
            hash.Add(QuantizeVolume(state.MicVolume));
            hash.Add(QuantizeVolume(state.DesktopVolume));
            hash.Add(state.IsTtsEnabled);
            hash.Add(state.IsTtsMicPassthroughEnabled);
            hash.Add((int)Math.Round(Math.Clamp(state.TtsMicPassthroughVolume, 0f, 4f) * 100f));
            hash.Add(state.MicTtsSpeakMask);
            hash.Add(state.DesktopTtsSpeakMask);
            hash.Add((int)Math.Round(Math.Clamp(state.NonTextOpacity, 0f, 1f) * 100f));
            hash.Add((int)Math.Round(Math.Clamp(state.FontScale, 0.70f, 1.40f) * 100f));
            hash.Add(AppConfig.NormalizeHexColor(state.MicTextColor, AppConfig.DefaultSteamVrMicTextColor));
            hash.Add(AppConfig.NormalizeHexColor(state.DesktopTextColor, AppConfig.DefaultSteamVrDesktopTextColor));
            hash.Add((int)state.SourceMask);
            hash.Add((int)state.LanguageMask);
            AddLanguageSettingsToHash(ref hash, state);
            hash.Add(transcripts.Length);
            for (var i = 0; i < transcripts.Length; i++)
            {
                var t = transcripts[i];
                hash.Add(t.Source ?? string.Empty);
                hash.Add(t.Original ?? string.Empty);
                hash.Add(t.Translation ?? string.Empty);
                hash.Add(t.Translation2 ?? string.Empty);
                hash.Add(t.Translation3 ?? string.Empty);
                hash.Add(t.PhotoTranslationMode ?? string.Empty);
                hash.Add(t.PhotoImagePath ?? string.Empty);
                hash.Add(t.Timestamp.Ticks);
            }
            return hash.ToHashCode();
        }

        private static void AddLanguageSettingsToHash(ref HashCode hash, SteamVrOverlayPanelState state)
        {
            hash.Add(state.UiLanguage ?? string.Empty);
            hash.Add(state.MicSourceLanguage ?? string.Empty);
            hash.Add(state.MicTargetLanguage ?? string.Empty);
            hash.Add(state.MicTargetLanguage2 ?? string.Empty);
            hash.Add(state.MicTargetLanguage3 ?? string.Empty);
            hash.Add(state.DesktopSourceLanguage ?? string.Empty);
            hash.Add(state.DesktopTargetLanguage ?? string.Empty);
            hash.Add(state.DesktopTargetLanguage2 ?? string.Empty);
            hash.Add(state.DesktopTargetLanguage3 ?? string.Empty);
        }

        private static int ComputePhotoPanelStateHash(SteamVrOverlayPanelState state, SteamVrOverlayTranscript[] transcripts)
        {
            var hash = new HashCode();
            hash.Add(state.IsPhotoTranslationArmed);
            hash.Add(state.IsPhotoTranslationProcessing);
            hash.Add((int)Math.Round(Math.Clamp(state.NonTextOpacity, 0f, 1f) * 100f));
            hash.Add((int)Math.Round(Math.Clamp(state.FontScale, 0.70f, 1.40f) * 100f));
            hash.Add((int)state.LanguageMask);
            hash.Add(state.UiLanguage ?? string.Empty);
            hash.Add(transcripts.Length);
            for (var i = 0; i < transcripts.Length; i++)
            {
                var t = transcripts[i];
                hash.Add(t.Original ?? string.Empty);
                hash.Add(t.Translation ?? string.Empty);
                hash.Add(t.Translation2 ?? string.Empty);
                hash.Add(t.Translation3 ?? string.Empty);
                hash.Add(t.PhotoTranslationMode ?? string.Empty);
                hash.Add(t.PhotoImagePath ?? string.Empty);
                hash.Add(t.Timestamp.Ticks);
            }
            return hash.ToHashCode();
        }

        private static bool IsPhotoTranscript(SteamVrOverlayTranscript? transcript)
        {
            return transcript != null
                && string.Equals(transcript.Source, "Photo", StringComparison.OrdinalIgnoreCase);
        }

        private static int ComputePhotoTranscriptIdentityHash(SteamVrOverlayTranscript? transcript)
        {
            if (transcript == null) return 0;

            var hash = new HashCode();
            hash.Add(transcript.Original ?? string.Empty);
            hash.Add(transcript.Translation ?? string.Empty);
            hash.Add(transcript.Translation2 ?? string.Empty);
            hash.Add(transcript.Translation3 ?? string.Empty);
            hash.Add(transcript.PhotoTranslationMode ?? string.Empty);
            hash.Add(transcript.PhotoImagePath ?? string.Empty);
            hash.Add(transcript.Timestamp.Ticks);
            return hash.ToHashCode();
        }

        private bool TryConsumePanelDirtyLocked(DateTime now)
        {
            if (!_panelDirty) return false;
            if (now - _lastPanelRenderUtc < PanelRenderMinInterval) return false;
            _panelDirty = false;
            return true;
        }

        private bool TryConsumePhotoPanelDirtyLocked(DateTime now)
        {
            if (!_photoPanelDirty) return false;
            if (now - _lastPhotoPanelRenderUtc < PhotoPanelRenderMinInterval) return false;
            _photoPanelDirty = false;
            return true;
        }

        private SteamVrOverlayPanelState BuildPhotoPanelStateSnapshotLocked()
        {
            return new SteamVrOverlayPanelState
            {
                IsMicListening = _panelState.IsMicListening,
                IsDesktopListening = _panelState.IsDesktopListening,
                IsPhotoTranslationArmed = _panelState.IsPhotoTranslationArmed,
                IsPhotoTranslationProcessing = _panelState.IsPhotoTranslationProcessing,
                MicVolume = _panelState.MicVolume,
                DesktopVolume = _panelState.DesktopVolume,
                IsTtsEnabled = _panelState.IsTtsEnabled,
                IsTtsMicPassthroughEnabled = _panelState.IsTtsMicPassthroughEnabled,
                TtsMicPassthroughVolume = _panelState.TtsMicPassthroughVolume,
                MicTtsSpeakMask = _panelState.MicTtsSpeakMask,
                DesktopTtsSpeakMask = _panelState.DesktopTtsSpeakMask,
                NonTextOpacity = _panelState.NonTextOpacity,
                FontScale = _panelState.FontScale,
                SourceMask = TranscriptSourceMask.Photo,
                LanguageMask = _panelState.LanguageMask,
                UiLanguage = _panelState.UiLanguage,
                PhotoPanelIndex = _photoPanelPageIndex,
                PhotoPanelTextScrollOffset = _photoPanelTextScrollOffset,
                Transcripts = _photoPanelTranscripts
            };
        }

        private void ResetPhotoPanelTextScrollLocked()
        {
            _photoPanelTextScrollOffset = 0;
            _photoPanelTextDragging = false;
            _photoPanelTextDragLastY = 0f;
            _photoPanelTextDragPageIndex = -1;
        }

        private void MarkPhotoPanelDirtyForInteractionLocked()
        {
            _photoPanelDirty = true;
            _lastPhotoPanelRenderUtc = DateTime.MinValue;
        }

        private void ResetPhotoImageOverlayStateLocked(bool resetContent)
        {
            _photoImageOverlayAppliedVisible = false;
            if (!resetContent)
            {
                return;
            }

            _photoImageOverlayContentHash = 0;
            _photoImageOverlayPath = string.Empty;
            _photoImageOverlaySourceWidth = 0;
            _photoImageOverlaySourceHeight = 0;
        }

        private bool ShouldDrawCursorInPanelLocked()
        {
            return DrawCursorInPanelBackup;
        }

        private void MarkCursorPanelDirtyIfNeededLocked()
        {
            if (ShouldDrawCursorInPanelLocked())
            {
                _panelDirty = true;
            }
        }

        private bool IsPanelUnlockConfirmPendingLocked(DateTime now)
        {
            if (!_panelLocked)
            {
                _panelUnlockConfirmUntilUtc = DateTime.MinValue;
                return false;
            }

            if (_panelUnlockConfirmUntilUtc == DateTime.MinValue)
            {
                return false;
            }

            if (now <= _panelUnlockConfirmUntilUtc)
            {
                return true;
            }

            _panelUnlockConfirmUntilUtc = DateTime.MinValue;
            _panelDirty = true;
            return false;
        }

        private void ResetPanelLockStateLocked()
        {
            _panelLocked = false;
            _panelUnlockConfirmUntilUtc = DateTime.MinValue;
        }

        private uint GetPreferredPointerDeviceIndex(CVRSystem system)
        {
            if (system == null) return Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;

            var candidates = GetPointerDeviceCandidates(system);
            return candidates.Count > 0
                ? candidates[0]
                : Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
        }

        private List<uint> GetPointerDeviceCandidates(CVRSystem system, uint primaryDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            var candidates = new List<uint>();
            if (system == null) return candidates;

            int panelHand;
            SteamVrTrackingDeviceMode trackingMode;
            lock (_lock)
            {
                panelHand = _hand;
                trackingMode = _config.SteamVrTrackingDeviceModeValue;
            }

            // Device discovery is expensive because hand-tracking detection reads several OpenVR
            // string properties. Cache the discovered base list briefly, then only prioritize the
            // last hit device per poll.
            var cachedCandidates = GetCachedPointerDeviceCandidates(system, panelHand, trackingMode);
            if (primaryDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid
                && cachedCandidates.Contains(primaryDeviceIndex))
            {
                candidates.Add(primaryDeviceIndex);
            }
            else
            {
                AddPointerCandidateIfUsable(system, candidates, trackingMode, primaryDeviceIndex);
            }
            for (var i = 0; i < cachedCandidates.Count; i++)
            {
                var deviceIndex = cachedCandidates[i];
                if (deviceIndex == primaryDeviceIndex || candidates.Contains(deviceIndex))
                {
                    continue;
                }

                candidates.Add(deviceIndex);
            }

            return candidates;
        }

        private List<uint> GetCachedPointerDeviceCandidates(CVRSystem system, int panelHand, SteamVrTrackingDeviceMode trackingMode)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (now - _pointerDeviceCandidatesCacheUtc < PointerDeviceCandidateCacheInterval
                    && _pointerDeviceCandidatesCacheHand == panelHand
                    && _pointerDeviceCandidatesCacheMode == trackingMode)
                {
                    return new List<uint>(_pointerDeviceCandidatesCache);
                }
            }

            var candidates = BuildPointerDeviceCandidates(system, panelHand, trackingMode);

            lock (_lock)
            {
                _pointerDeviceCandidatesCache = new List<uint>(candidates);
                _pointerDeviceCandidatesCacheUtc = now;
                _pointerDeviceCandidatesCacheHand = panelHand;
                _pointerDeviceCandidatesCacheMode = trackingMode;
            }

            return candidates;
        }

        private List<uint> BuildPointerDeviceCandidates(CVRSystem system, int panelHand, SteamVrTrackingDeviceMode trackingMode)
        {
            var candidates = new List<uint>();
            foreach (var mode in ExpandTrackingDeviceModes(trackingMode))
            {
                AddPointerCandidatesForMode(system, candidates, mode, panelHand, Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid);
            }

            return candidates;
        }

        private static void AddPointerCandidateIfUsable(
            CVRSystem system,
            List<uint> candidates,
            SteamVrTrackingDeviceMode trackingMode,
            uint deviceIndex)
        {
            if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return;
            if (deviceIndex >= Valve.VR.OpenVR.k_unMaxTrackedDeviceCount) return;
            if (candidates.Contains(deviceIndex)) return;

            foreach (var mode in ExpandTrackingDeviceModes(trackingMode))
            {
                if (IsDeviceUsableForTrackingMode(system, deviceIndex, mode))
                {
                    candidates.Add(deviceIndex);
                    return;
                }
            }
        }

        private void InvalidatePointerDeviceCandidateCacheLocked()
        {
            _pointerDeviceCandidatesCache.Clear();
            _pointerDeviceCandidatesCacheUtc = DateTime.MinValue;
            _pointerDeviceCandidatesCacheHand = -1;
        }

        private void AddPointerCandidatesForMode(
            CVRSystem system,
            List<uint> candidates,
            SteamVrTrackingDeviceMode mode,
            int panelHand,
            uint primaryDeviceIndex)
        {
            void AddCandidate(uint deviceIndex)
            {
                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return;
                if (deviceIndex >= Valve.VR.OpenVR.k_unMaxTrackedDeviceCount) return;
                if (candidates.Contains(deviceIndex)) return;
                if (!IsDeviceUsableForTrackingMode(system, deviceIndex, mode)) return;

                candidates.Add(deviceIndex);
            }

            AddCandidate(primaryDeviceIndex);

            // Palm panel is easiest to point at with the opposite hand.
            var preferredRole = panelHand == 1
                ? ETrackedControllerRole.RightHand
                : ETrackedControllerRole.LeftHand;
            var fallbackRole = preferredRole == ETrackedControllerRole.LeftHand
                ? ETrackedControllerRole.RightHand
                : ETrackedControllerRole.LeftHand;

            AddCandidate(system.GetTrackedDeviceIndexForControllerRole(preferredRole));
            AddCandidate(system.GetTrackedDeviceIndexForControllerRole(fallbackRole));

            AddSortedDevices(system, ETrackedDeviceClass.Controller, AddCandidate);
            AddSortedDevices(system, ETrackedDeviceClass.GenericTracker, AddCandidate);

            for (uint i = 0; i < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                AddCandidate(i);
            }
        }

        private static IReadOnlyList<SteamVrTrackingDeviceMode> ExpandTrackingDeviceModes(SteamVrTrackingDeviceMode mode)
        {
            return mode switch
            {
                SteamVrTrackingDeviceMode.Controller => new[] { SteamVrTrackingDeviceMode.Controller },
                SteamVrTrackingDeviceMode.HandTracking => new[] { SteamVrTrackingDeviceMode.HandTracking },
                _ => new[] { SteamVrTrackingDeviceMode.Controller, SteamVrTrackingDeviceMode.HandTracking }
            };
        }

        private static void AddSortedDevices(CVRSystem system, ETrackedDeviceClass deviceClass, Action<uint> addCandidate)
        {
            try
            {
                var sorted = new uint[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
                var count = system.GetSortedTrackedDeviceIndicesOfClass(
                    deviceClass,
                    sorted,
                    Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid);
                var max = Math.Min((int)count, sorted.Length);
                for (var i = 0; i < max; i++)
                {
                    addCandidate(sorted[i]);
                }
            }
            catch { }
        }

        private static bool IsDeviceUsableForTrackingMode(CVRSystem system, uint deviceIndex, SteamVrTrackingDeviceMode mode)
        {
            try
            {
                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return false;
                if (deviceIndex >= Valve.VR.OpenVR.k_unMaxTrackedDeviceCount) return false;
                if (!system.IsTrackedDeviceConnected(deviceIndex)) return false;

                var deviceClass = system.GetTrackedDeviceClass(deviceIndex);
                var isHandTracking = IsHandTrackingDevice(system, deviceIndex);

                return mode switch
                {
                    SteamVrTrackingDeviceMode.HandTracking =>
                        isHandTracking && (deviceClass == ETrackedDeviceClass.Controller || deviceClass == ETrackedDeviceClass.GenericTracker),
                    SteamVrTrackingDeviceMode.Controller =>
                        !isHandTracking && deviceClass == ETrackedDeviceClass.Controller,
                    _ => deviceClass == ETrackedDeviceClass.Controller || deviceClass == ETrackedDeviceClass.GenericTracker
                };
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHandTrackingDevice(CVRSystem system, uint deviceIndex)
        {
            var signature =
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_ControllerType_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_InputProfilePath_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_RegisteredDeviceType_String);

            return signature.Contains("hand", StringComparison.OrdinalIgnoreCase)
                || signature.Contains("skeleton", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTrackedDeviceString(CVRSystem system, uint deviceIndex, ETrackedDeviceProperty property)
        {
            try
            {
                var error = ETrackedPropertyError.TrackedProp_Success;
                var builder = new StringBuilder(256);
                var count = system.GetStringTrackedDeviceProperty(deviceIndex, property, builder, (uint)builder.Capacity, ref error);
                if (error != ETrackedPropertyError.TrackedProp_Success || count == 0)
                {
                    return string.Empty;
                }

                return builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void SetHand(int hand)
        {
            var normalized = Math.Clamp(hand, 0, 1);
            lock (_lock)
            {
                _hand = normalized;
                _config.SteamVrHand = normalized;
                InvalidatePointerDeviceCandidateCacheLocked();
                _panelDirty = true;
            }

            if (!_disposed && IsConnected)
            {
                ApplyPlacementInternal();
            }
        }

        public void SetTrackingDeviceMode(SteamVrTrackingDeviceMode mode)
        {
            var normalized = Enum.IsDefined(typeof(SteamVrTrackingDeviceMode), mode)
                ? mode
                : SteamVrTrackingDeviceMode.Auto;

            lock (_lock)
            {
                _config.SteamVrTrackingDeviceModeValue = normalized;
                _cursorRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorRayDistanceMeters = CursorLaserDefaultLengthMeters;
                _cursorAppliedRayDistanceMeters = float.NaN;
                _cursorValid = false;
                _cursorDirty = true;
                InvalidatePointerDeviceCandidateCacheLocked();
                _panelDirty = true;
            }

            if (!_disposed && IsConnected)
            {
                ApplyPlacementInternal();
            }
        }

        public void SetSize(float size)
        {
            var normalized = Math.Clamp(size, 0.05f, 0.5f);
            lock (_lock)
            {
                _size = normalized;
                _config.SteamVrSize = normalized;
                if (!_disposed && IsConnected && _overlayHandle != 0)
                {
                    ApplySizeInternal(normalized);
                    ApplyCursorSizeInternal(_panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : normalized);
                    ApplyPhotoPanelTransformFromCurrentPlacement();
                    _cursorDirty = true;
                }

                _panelDirty = true;
                _photoPanelDirty = true;
            }
        }

        public void SetAnchorMode(SteamVrOverlayAnchorMode anchorMode)
        {
            lock (_lock)
            {
                _config.SteamVrAnchorModeValue = anchorMode;
                _panelDirty = true;
            }

            if (!_disposed && IsConnected)
            {
                ApplyPlacementInternal();
            }
        }

        public void SetPositionOffset(float offsetX, float offsetY, float offsetZ)
        {
            lock (_lock)
            {
                _config.SetSteamVrAnchorOffset(
                    _config.SteamVrAnchorModeValue,
                    ClampOffset(offsetX, 0.45f),
                    ClampOffset(offsetY, 0.45f),
                    ClampOffset(offsetZ, 0.75f));
                _panelDirty = true;
            }

            if (!_disposed && IsConnected)
            {
                ApplyPlacementInternal();
            }
        }

        public void SetRotationOffset(float rotationXDeg, float rotationYDeg, float rotationZDeg)
        {
            lock (_lock)
            {
                _config.SetSteamVrAnchorRotation(
                    _config.SteamVrAnchorModeValue,
                    ClampRotation(rotationXDeg),
                    ClampRotation(rotationYDeg),
                    ClampRotation(rotationZDeg));
                _panelDirty = true;
            }

            if (!_disposed && IsConnected)
            {
                ApplyPlacementInternal();
            }
        }

        public void ConfigureStartupAutoLaunch(bool enabled)
        {
            lock (_lock)
            {
                _config.SteamVrAutoLaunch = enabled;
            }

            LogDiagnosticInformation("SteamVR auto-launch configuration requested. enabled={Enabled}", enabled);
            SetStartupRegistrationStatus(enabled
                ? "SteamVR auto-launch update queued"
                : "SteamVR auto-launch disable queued");
            QueueOpenVrOperation(
                "SteamVR startup registration update",
                () => EnsureSteamVrApplicationRegistration(enabled));
        }

        public void RefreshStartupRegistrationStatus()
        {
            var enabled = _config.SteamVrAutoLaunch;
            LogDiagnosticDebug("SteamVR startup registration refresh requested. autoLaunch={AutoLaunch}", enabled);
            SetStartupRegistrationStatus(enabled
                ? "Checking SteamVR auto-launch registration..."
                : "SteamVR auto-launch disabled");
            QueueOpenVrOperation(
                "SteamVR startup registration refresh",
                () => EnsureSteamVrApplicationRegistration(enabled, refreshOnly: true));
        }

        public void IdentifyCurrentProcess()
        {
            LogDiagnosticDebug("SteamVR current process identification requested. processId={ProcessId}", Environment.ProcessId);
            QueueOpenVrOperation(
                "SteamVR process identification",
                TryIdentifyCurrentProcessInternal);
        }

        private void ApplyHandInternal(int hand)
        {
            var system = Valve.VR.OpenVR.System;
            var overlay = Valve.VR.OpenVR.Overlay;
            if (system == null || overlay == null) return;

            // 0=右 1=左 2=双手(暂按右手处理)
            var role = hand switch
            {
                1 => ETrackedControllerRole.LeftHand,
                _ => ETrackedControllerRole.RightHand
            };

            var deviceIndex = system.GetTrackedDeviceIndexForControllerRole(role);
            if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                // Common on startup: controllers not detected yet.
                // Keep waiting instead of falling back to HMD for hand-anchored overlays.
                var now = DateTime.UtcNow;
                if ((now - _lastControllerMissingLogUtc) > TimeSpan.FromSeconds(5))
                {
                    _lastControllerMissingLogUtc = now;
                    _logger.Debug("未找到手柄设备 {Role}", role);
                    SetStatus("SteamVR overlay connected (waiting for controller)");
                }
                return;
            }
            else
            {
                SetStatus("SteamVR overlay connected");
            }

            // Relative transform to controller/HMD.
            HmdMatrix34_t transform;
            if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd)
            {
                // HMD fallback: simple HUD-like placement.
                transform = new HmdMatrix34_t
                {
                    m0 = 1, m5 = 1, m10 = 1,
                    m3 = 0.0f,
                    m7 = -0.18f,
                    m11 = -0.75f
                };
            }
            else
            {
                // Match the original VRCLS-master palm transform (python openvr) so the panel sits on the palm
                // with a readable orientation.
                var x = hand == 1 ? 0.03f : -0.03f;
                var y = -0.04f;
                var z = 0.06f;

                var xRot = -90.0 * Math.PI / 180.0;
                var yRot = (hand == 1 ? 30.0 : -30.0) * Math.PI / 180.0;
                var zRot = (hand == 1 ? -90.0 : 90.0) * Math.PI / 180.0;

                var cosX = (float)Math.Cos(xRot);
                var sinX = (float)Math.Sin(xRot);
                var cosY = (float)Math.Cos(yRot);
                var sinY = (float)Math.Sin(yRot);
                var cosZ = (float)Math.Cos(zRot);
                var sinZ = (float)Math.Sin(zRot);

                transform = new HmdMatrix34_t
                {
                    // ZYX rotation order (same as python code)
                    m0 = cosZ * cosY,
                    m1 = cosZ * sinY * sinX - sinZ * cosX,
                    m2 = cosZ * sinY * cosX + sinZ * sinX,

                    m4 = sinZ * cosY,
                    m5 = sinZ * sinY * sinX + cosZ * cosX,
                    m6 = sinZ * sinY * cosX - cosZ * sinX,

                    m8 = -sinY,
                    m9 = cosY * sinX,
                    m10 = cosY * cosX,

                    m3 = x,
                    m7 = y,
                    m11 = z
                };
            }

            var err = overlay.SetOverlayTransformTrackedDeviceRelative(_overlayHandle, deviceIndex, ref transform);
            if (err != EVROverlayError.None)
            {
                _logger.Debug("SetOverlayTransformTrackedDeviceRelative 失败: {Error}", err);
                if (err == EVROverlayError.InvalidHandle || err == EVROverlayError.UnknownOverlay)
                {
                    RequestRestart($"SetOverlayTransformTrackedDeviceRelative failed: {err}");
                }
            }

            lock (_lock)
            {
                _panelTrackedDeviceIndex = deviceIndex;
                _panelTransformInDeviceSpace = transform;
                // Cursor transform depends on the panel overlay transform.
                _cursorDirty = true;
            }
        }

        private bool ApplyPlacementInternal()
        {
            var system = Valve.VR.OpenVR.System;
            var overlay = Valve.VR.OpenVR.Overlay;
            if (system == null || overlay == null) return false;

            if (!TryBuildPlacementTransform(system, out var deviceIndex, out var transform))
            {
                HideOverlayUntilControllerAvailable(overlay);
                return false;
            }

            var err = overlay.SetOverlayTransformTrackedDeviceRelative(_overlayHandle, deviceIndex, ref transform);
            if (err != EVROverlayError.None)
            {
                _logger.Debug("SetOverlayTransformTrackedDeviceRelative failed: {Error}", err);
                if (err == EVROverlayError.InvalidHandle || err == EVROverlayError.UnknownOverlay)
                {
                    RequestRestart($"SetOverlayTransformTrackedDeviceRelative failed: {err}");
                }

                return false;
            }
            overlay.SetOverlaySortOrder(_overlayHandle, PanelOverlaySortOrder);

            lock (_lock)
            {
                _panelTrackedDeviceIndex = deviceIndex;
                _panelTransformInDeviceSpace = transform;
                _cursorDirty = true;
            }

            ApplyPhotoPanelTransform(overlay, deviceIndex, transform);
            ApplyDesiredVisibilityAfterPlacement(overlay);
            ApplyPhotoPanelVisibility(overlay);
            return true;
        }

        private bool TryBuildPlacementTransform(CVRSystem system, out uint deviceIndex, out HmdMatrix34_t transform)
        {
            deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
            transform = default;

            HmdMatrix34_t baseTransform;
            var anchorMode = _config.SteamVrAnchorModeValue;
            var hand = _hand;
            var trackingMode = _config.SteamVrTrackingDeviceModeValue;
            var offset = _config.GetSteamVrAnchorOffset(anchorMode);
            var rotation = _config.GetSteamVrAnchorRotation(anchorMode);

            if (anchorMode == SteamVrOverlayAnchorMode.Head)
            {
                deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd;
                baseTransform = BuildTransform(0.00f, -0.18f, -0.75f, 0.0, 0.0, 0.0);
            }
            else
            {
                if (!TryResolveTrackingDeviceForPlacement(system, hand, trackingMode, out deviceIndex, out var resolvedHand, out var resolvedRole, out var usedFallbackHand))
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastControllerMissingLogUtc) > TimeSpan.FromSeconds(5))
                    {
                        _lastControllerMissingLogUtc = now;
                        _logger.Debug(
                            "Tracking device not found for SteamVR overlay placement. requestedHand={RequestedHand}, anchorMode={AnchorMode}, trackingMode={TrackingMode}",
                            hand,
                            anchorMode,
                            trackingMode);
                        SetStatus("SteamVR overlay connected (waiting for controller)");
                    }

                    deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd;
                    resolvedHand = hand;
                    resolvedRole = ETrackedControllerRole.Invalid;
                    usedFallbackHand = false;
                    return false;
                }

                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd)
                {
                    SetStatus("SteamVR overlay connected (using HMD fallback)");
                }
                else if (usedFallbackHand)
                {
                    SetStatus($"SteamVR overlay connected (using {resolvedRole} {trackingMode})");
                }
                else
                {
                    SetStatus("SteamVR overlay connected");
                }

                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd)
                {
                    baseTransform = BuildTransform(0.00f, -0.18f, -0.75f, 0.0, 0.0, 0.0);
                }
                else if (anchorMode == SteamVrOverlayAnchorMode.BackOfHand)
                {
                    if (resolvedHand == 1)
                    {
                        baseTransform = BuildTransform(
                            -0.07424337f,
                            -0.14610124f,
                            0.22851482f,
                            90.0,
                            52.586586,
                            -90.0);
                    }
                    else
                    {
                        baseTransform = BuildTransform(
                            -0.010f,
                            0.056f,
                            -0.018f,
                            90.0,
                            12.0,
                            90.0);
                    }
                }
                else
                {
                    baseTransform = BuildTransform(
                        resolvedHand == 1 ? 0.03f : -0.03f,
                        -0.04f,
                        0.06f,
                        -90.0,
                        resolvedHand == 1 ? 30.0 : -30.0,
                        resolvedHand == 1 ? -90.0 : 90.0);
                }
            }

            var rotationTransform = BuildTransform(
                0f,
                0f,
                0f,
                ClampRotation(rotation.X),
                ClampRotation(rotation.Y),
                ClampRotation(rotation.Z));
            var offsetTransform = CreateTranslation(
                ClampOffset(offset.X, 0.45f),
                ClampOffset(offset.Y, 0.45f),
                ClampOffset(offset.Z, 0.75f));
            transform = Multiply(Multiply(baseTransform, rotationTransform), offsetTransform);
            return true;
        }

        private enum PlacementDeviceAvailability
        {
            Active,
            Tracked,
            Connected
        }

        private static bool TryResolveTrackingDeviceForPlacement(
            CVRSystem system,
            int hand,
            SteamVrTrackingDeviceMode trackingMode,
            out uint deviceIndex,
            out int resolvedHand,
            out ETrackedControllerRole resolvedRole,
            out bool usedFallbackHand)
        {
            resolvedHand = Math.Clamp(hand, 0, 1);
            var requestedHand = resolvedHand;
            var preferredRole = requestedHand == 1
                ? ETrackedControllerRole.LeftHand
                : ETrackedControllerRole.RightHand;

            var poses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
            var hasPoseSnapshot = TryGetTrackedDevicePoseSnapshot(system, poses);

            foreach (var availability in ExpandPlacementAvailabilityPasses(trackingMode))
            {
                var mode = availability.Mode;
                var requiredAvailability = availability.Availability;

                if (TryUsePlacementDevice(system, system.GetTrackedDeviceIndexForControllerRole(preferredRole), mode, requiredAvailability, poses, hasPoseSnapshot, requestedHand, preferredRole, false, out deviceIndex, out resolvedHand, out resolvedRole, out usedFallbackHand))
                {
                    return true;
                }

                if (TryFindPlacementDeviceByRole(system, preferredRole, mode, requiredAvailability, poses, hasPoseSnapshot, requestedHand, false, out deviceIndex, out resolvedHand, out resolvedRole, out usedFallbackHand))
                {
                    return true;
                }

                if (TryFindPlacementDeviceForRequestedHand(system, preferredRole, mode, requiredAvailability, poses, hasPoseSnapshot, requestedHand, out deviceIndex, out resolvedHand, out resolvedRole, out usedFallbackHand))
                {
                    return true;
                }
            }

            deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
            resolvedRole = preferredRole;
            usedFallbackHand = false;
            return false;
        }

        private static IReadOnlyList<(SteamVrTrackingDeviceMode Mode, PlacementDeviceAvailability Availability)> ExpandPlacementAvailabilityPasses(SteamVrTrackingDeviceMode mode)
        {
            if (mode == SteamVrTrackingDeviceMode.Auto)
            {
                return new[]
                {
                    (SteamVrTrackingDeviceMode.Controller, PlacementDeviceAvailability.Active),
                    (SteamVrTrackingDeviceMode.HandTracking, PlacementDeviceAvailability.Active),
                    (SteamVrTrackingDeviceMode.HandTracking, PlacementDeviceAvailability.Tracked),
                    (SteamVrTrackingDeviceMode.Controller, PlacementDeviceAvailability.Tracked),
                    (SteamVrTrackingDeviceMode.Controller, PlacementDeviceAvailability.Connected),
                    (SteamVrTrackingDeviceMode.HandTracking, PlacementDeviceAvailability.Connected)
                };
            }

            var normalized = mode == SteamVrTrackingDeviceMode.HandTracking
                ? SteamVrTrackingDeviceMode.HandTracking
                : SteamVrTrackingDeviceMode.Controller;
            return new[]
            {
                (normalized, PlacementDeviceAvailability.Active),
                (normalized, PlacementDeviceAvailability.Tracked),
                (normalized, PlacementDeviceAvailability.Connected)
            };
        }

        private static bool TryUsePlacementDevice(
            CVRSystem system,
            uint candidateDeviceIndex,
            SteamVrTrackingDeviceMode mode,
            PlacementDeviceAvailability requiredAvailability,
            TrackedDevicePose_t[] poses,
            bool hasPoseSnapshot,
            int requestedHand,
            ETrackedControllerRole role,
            bool isFallbackHand,
            out uint deviceIndex,
            out int resolvedHand,
            out ETrackedControllerRole resolvedRole,
            out bool usedFallbackHand)
        {
            deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
            resolvedHand = requestedHand;
            resolvedRole = role;
            usedFallbackHand = false;

            if (!IsDeviceUsableForTrackingMode(system, candidateDeviceIndex, mode))
            {
                return false;
            }
            if (!MeetsPlacementDeviceAvailability(system, candidateDeviceIndex, requiredAvailability, poses, hasPoseSnapshot))
            {
                return false;
            }

            deviceIndex = candidateDeviceIndex;
            resolvedRole = role;
            resolvedHand = role == ETrackedControllerRole.LeftHand
                ? 1
                : role == ETrackedControllerRole.RightHand
                    ? 0
                    : requestedHand;
            usedFallbackHand = isFallbackHand;
            return true;
        }

        private static bool TryFindPlacementDeviceByRole(
            CVRSystem system,
            ETrackedControllerRole role,
            SteamVrTrackingDeviceMode mode,
            PlacementDeviceAvailability requiredAvailability,
            TrackedDevicePose_t[] poses,
            bool hasPoseSnapshot,
            int requestedHand,
            bool isFallbackHand,
            out uint deviceIndex,
            out int resolvedHand,
            out ETrackedControllerRole resolvedRole,
            out bool usedFallbackHand)
        {
            for (uint i = 0; i < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                try
                {
                    if (system.GetControllerRoleForTrackedDeviceIndex(i) != role)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (TryUsePlacementDevice(system, i, mode, requiredAvailability, poses, hasPoseSnapshot, requestedHand, role, isFallbackHand, out deviceIndex, out resolvedHand, out resolvedRole, out usedFallbackHand))
                {
                    return true;
                }
            }

            deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
            resolvedHand = requestedHand;
            resolvedRole = role;
            usedFallbackHand = false;
            return false;
        }

        private static bool TryFindPlacementDeviceForRequestedHand(
            CVRSystem system,
            ETrackedControllerRole requestedRole,
            SteamVrTrackingDeviceMode mode,
            PlacementDeviceAvailability requiredAvailability,
            TrackedDevicePose_t[] poses,
            bool hasPoseSnapshot,
            int requestedHand,
            out uint deviceIndex,
            out int resolvedHand,
            out ETrackedControllerRole resolvedRole,
            out bool usedFallbackHand)
        {
            for (uint i = 0; i < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                if (!IsDeviceUsableForTrackingMode(system, i, mode))
                {
                    continue;
                }
                if (!MeetsPlacementDeviceAvailability(system, i, requiredAvailability, poses, hasPoseSnapshot))
                {
                    continue;
                }

                var role = GetPlacementRoleForDevice(system, i);
                if (role != requestedRole)
                {
                    continue;
                }

                deviceIndex = i;
                resolvedRole = role;
                resolvedHand = role == ETrackedControllerRole.LeftHand ? 1 : 0;
                usedFallbackHand = false;
                return true;
            }

            deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
            resolvedHand = requestedHand;
            resolvedRole = requestedRole;
            usedFallbackHand = false;
            return false;
        }

        private static ETrackedControllerRole GetPlacementRoleForDevice(CVRSystem system, uint deviceIndex)
        {
            try
            {
                var role = system.GetControllerRoleForTrackedDeviceIndex(deviceIndex);
                if (IsHandRole(role))
                {
                    return role;
                }
            }
            catch { }

            try
            {
                var error = ETrackedPropertyError.TrackedProp_Success;
                var value = system.GetInt32TrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_ControllerRoleHint_Int32, ref error);
                if (error == ETrackedPropertyError.TrackedProp_Success)
                {
                    var role = (ETrackedControllerRole)value;
                    if (IsHandRole(role))
                    {
                        return role;
                    }
                }
            }
            catch { }

            var signature =
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_ControllerType_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_InputProfilePath_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String) + " " +
                GetTrackedDeviceString(system, deviceIndex, ETrackedDeviceProperty.Prop_RegisteredDeviceType_String);

            var hasLeft = signature.Contains("left", StringComparison.OrdinalIgnoreCase);
            var hasRight = signature.Contains("right", StringComparison.OrdinalIgnoreCase);
            if (hasLeft == hasRight)
            {
                return ETrackedControllerRole.Invalid;
            }

            return hasLeft ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
        }

        private static bool IsHandRole(ETrackedControllerRole role)
        {
            return role == ETrackedControllerRole.LeftHand || role == ETrackedControllerRole.RightHand;
        }

        private static bool TryGetTrackedDevicePoseSnapshot(CVRSystem system, TrackedDevicePose_t[] poses)
        {
            try
            {
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool MeetsPlacementDeviceAvailability(
            CVRSystem system,
            uint deviceIndex,
            PlacementDeviceAvailability requiredAvailability,
            TrackedDevicePose_t[] poses,
            bool hasPoseSnapshot)
        {
            return requiredAvailability switch
            {
                PlacementDeviceAvailability.Active => IsDeviceActiveForPlacement(system, deviceIndex, poses, hasPoseSnapshot),
                PlacementDeviceAvailability.Tracked => HasValidTrackedDevicePose(deviceIndex, poses, hasPoseSnapshot),
                _ => true
            };
        }

        private static bool IsDeviceActiveForPlacement(
            CVRSystem system,
            uint deviceIndex,
            TrackedDevicePose_t[] poses,
            bool hasPoseSnapshot)
        {
            if (!HasValidTrackedDevicePose(deviceIndex, poses, hasPoseSnapshot))
            {
                return false;
            }

            try
            {
                var activityLevel = system.GetTrackedDeviceActivityLevel(deviceIndex);
                return activityLevel == EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction
                    || activityLevel == EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction_Timeout;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasValidTrackedDevicePose(uint deviceIndex, TrackedDevicePose_t[] poses, bool hasPoseSnapshot)
        {
            return hasPoseSnapshot
                && deviceIndex < poses.Length
                && poses[deviceIndex].bDeviceIsConnected
                && poses[deviceIndex].bPoseIsValid;
        }

        private void HideOverlayUntilControllerAvailable(CVROverlay overlay)
        {
            ulong overlayHandle;
            ulong cursorOverlayHandle;
            ulong photoOverlayHandle;
            ulong photoImageOverlayHandle;
            lock (_lock)
            {
                overlayHandle = _overlayHandle;
                cursorOverlayHandle = _cursorOverlayHandle;
                photoOverlayHandle = _photoOverlayHandle;
                photoImageOverlayHandle = _photoImageOverlayHandle;
                _panelTrackedDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorRayDistanceMeters = CursorLaserDefaultLengthMeters;
                _cursorAppliedRayDistanceMeters = float.NaN;
                _cursorValid = false;
                _cursorDirty = true;
                _photoPanelAppliedVisible = false;
                ResetPhotoImageOverlayStateLocked(resetContent: false);
                _photoPanelAppliedWidthMeters = 0f;
                _photoPanelPageIndex = 0;
                ResetPhotoPanelTextScrollLocked();
                ResetPanelLockStateLocked();
                InvalidatePointerDeviceCandidateCacheLocked();
            }

            try
            {
                if (overlayHandle != 0)
                {
                    overlay.HideOverlay(overlayHandle);
                }

                if (cursorOverlayHandle != 0)
                {
                    overlay.HideOverlay(cursorOverlayHandle);
                }

                if (photoOverlayHandle != 0)
                {
                    overlay.HideOverlay(photoOverlayHandle);
                }

                if (photoImageOverlayHandle != 0)
                {
                    overlay.HideOverlay(photoImageOverlayHandle);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to hide SteamVR overlay while waiting for controller.");
            }

            lock (_lock)
            {
                _appliedVisible = false;
                _photoPanelAppliedVisible = false;
            }
        }

        private void ApplyDesiredVisibilityAfterPlacement(CVROverlay overlay)
        {
            ulong overlayHandle;
            var desiredVisible = false;
            var appliedVisible = false;
            lock (_lock)
            {
                overlayHandle = _overlayHandle;
                desiredVisible = _desiredVisible;
                appliedVisible = _appliedVisible;
            }

            if (overlayHandle == 0 || desiredVisible == appliedVisible)
            {
                return;
            }

            try
            {
                if (desiredVisible)
                {
                    overlay.ShowOverlay(overlayHandle);
                }
                else
                {
                    overlay.HideOverlay(overlayHandle);
                }

                lock (_lock)
                {
                    _appliedVisible = desiredVisible;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to apply SteamVR overlay visibility after placement.");
            }
        }

        private static float ClampOffset(float value, float maxAbs)
        {
            return Math.Clamp(value, -maxAbs, maxAbs);
        }

        private static float ClampRotation(float value)
        {
            return Math.Clamp(value, -180f, 180f);
        }

        private static HmdMatrix34_t CreateTranslation(float x, float y, float z)
        {
            return new HmdMatrix34_t
            {
                m0 = 1,
                m5 = 1,
                m10 = 1,
                m3 = x,
                m7 = y,
                m11 = z
            };
        }

        private static HmdMatrix34_t BuildTransform(float x, float y, float z, double xRotDeg, double yRotDeg, double zRotDeg)
        {
            var xRot = xRotDeg * Math.PI / 180.0;
            var yRot = yRotDeg * Math.PI / 180.0;
            var zRot = zRotDeg * Math.PI / 180.0;

            var cosX = (float)Math.Cos(xRot);
            var sinX = (float)Math.Sin(xRot);
            var cosY = (float)Math.Cos(yRot);
            var sinY = (float)Math.Sin(yRot);
            var cosZ = (float)Math.Cos(zRot);
            var sinZ = (float)Math.Sin(zRot);

            return new HmdMatrix34_t
            {
                m0 = cosZ * cosY,
                m1 = cosZ * sinY * sinX - sinZ * cosX,
                m2 = cosZ * sinY * cosX + sinZ * sinX,
                m4 = sinZ * cosY,
                m5 = sinZ * sinY * sinX + cosZ * cosX,
                m6 = sinZ * sinY * cosX - cosZ * sinX,
                m8 = -sinY,
                m9 = cosY * sinX,
                m10 = cosY * cosX,
                m3 = x,
                m7 = y,
                m11 = z
            };
        }

        private void ApplySizeInternal(float size)
        {
            var overlay = Valve.VR.OpenVR.Overlay;
            if (overlay == null) return;

            overlay.SetOverlayWidthInMeters(_overlayHandle, size);
            // Best-effort: query back the applied width to keep cursor mapping accurate near edges.
            var applied = size;
            try
            {
                var w = 0f;
                var err = overlay.GetOverlayWidthInMeters(_overlayHandle, ref w);
                if (err == EVROverlayError.None && w > 0.0001f)
                {
                    applied = w;
                }
            }
            catch { }

            lock (_lock) _panelAppliedWidthMeters = applied;
        }

        private void ApplyCursorSizeInternal(float panelWidthMeters)
        {
            var overlay = Valve.VR.OpenVR.Overlay;
            if (overlay == null) return;
            if (_cursorOverlayHandle == 0) return;

            // Independent cursor overlay; it is positioned at the panel intersection point.
            overlay.SetOverlayWidthInMeters(_cursorOverlayHandle, CursorDotOverlayWidthMeters);
        }

        private void ApplyPhotoPanelTransformFromCurrentPlacement()
        {
            var overlay = Valve.VR.OpenVR.Overlay;
            if (overlay == null) return;

            uint panelDeviceIndex;
            HmdMatrix34_t panelTransform;
            lock (_lock)
            {
                if (_photoOverlayHandle == 0) return;
                panelDeviceIndex = _panelTrackedDeviceIndex;
                panelTransform = _panelTransformInDeviceSpace;
            }

            if (panelDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                return;
            }

            ApplyPhotoPanelTransform(overlay, panelDeviceIndex, panelTransform);
        }

        private void ApplyPhotoPanelTransform(CVROverlay overlay, uint panelDeviceIndex, in HmdMatrix34_t panelTransform)
        {
            ulong photoHandle;
            float panelWidthMeters;
            lock (_lock)
            {
                photoHandle = _photoOverlayHandle;
                panelWidthMeters = _panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size;
            }

            if (overlay == null || photoHandle == 0)
            {
                return;
            }

            if (panelDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                return;
            }

            var panelHeightMeters = panelWidthMeters * (PanelHeight / (float)PanelWidth);
            var photoWidthMeters = Math.Clamp(panelWidthMeters * PhotoPanelWidthScale, 0.05f, 0.45f);
            var photoHeightMeters = photoWidthMeters * (PhotoPanelHeight / (float)PhotoPanelWidth);
            var localY = (panelHeightMeters + photoHeightMeters) * 0.5f + PhotoPanelGapMeters;
            var photoLocalT = CreateTranslation(0f, localY, PhotoPanelDepthOffsetMeters);
            var photoTransform = Multiply(panelTransform, photoLocalT);

            var widthErr = overlay.SetOverlayWidthInMeters(photoHandle, photoWidthMeters);
            if (widthErr == EVROverlayError.InvalidHandle || widthErr == EVROverlayError.UnknownOverlay)
            {
                RequestRestart($"Photo overlay width failed: {widthErr}");
                return;
            }

            var transformErr = overlay.SetOverlayTransformTrackedDeviceRelative(photoHandle, panelDeviceIndex, ref photoTransform);
            if (transformErr == EVROverlayError.InvalidHandle || transformErr == EVROverlayError.UnknownOverlay)
            {
                RequestRestart($"Photo overlay transform failed: {transformErr}");
                return;
            }

            lock (_lock)
            {
                _photoPanelTransformInDeviceSpace = photoTransform;
                _photoPanelAppliedWidthMeters = photoWidthMeters;
            }

            ApplyPhotoImageOverlayTransformIfVisible(overlay, panelDeviceIndex, photoTransform, photoWidthMeters);
        }

        private bool ShouldShowPhotoPanelLocked()
        {
            return _desiredVisible
                && _appliedVisible
                && _photoOverlayHandle != 0
                && !_photoPanelDismissed
                && (_panelState.IsPhotoTranslationProcessing || (_photoPanelTranscripts?.Count ?? 0) > 0);
        }

        private void ApplyPhotoPanelVisibility(CVROverlay overlay)
        {
            ulong photoHandle;
            ulong photoImageHandle;
            bool shouldShow;
            bool appliedVisible;
            bool imageAppliedVisible;
            lock (_lock)
            {
                if (_disposed) return;
                photoHandle = _photoOverlayHandle;
                photoImageHandle = _photoImageOverlayHandle;
                shouldShow = ShouldShowPhotoPanelLocked();
                appliedVisible = _photoPanelAppliedVisible;
                imageAppliedVisible = _photoImageOverlayAppliedVisible;
            }

            if (overlay == null || photoHandle == 0)
            {
                return;
            }

                if (shouldShow == appliedVisible && (shouldShow || !imageAppliedVisible))
                {
                    return;
                }

            try
            {
                var panelVisibilityChanged = shouldShow != appliedVisible;
                if (shouldShow != appliedVisible)
                {
                    var err = shouldShow
                        ? overlay.ShowOverlay(photoHandle)
                        : overlay.HideOverlay(photoHandle);
                    if (err == EVROverlayError.InvalidHandle || err == EVROverlayError.UnknownOverlay)
                    {
                        RequestRestart($"Photo overlay visibility failed: {err}");
                        return;
                    }
                }

                if (!shouldShow && imageAppliedVisible && photoImageHandle != 0)
                {
                    overlay.HideOverlay(photoImageHandle);
                }

                lock (_lock)
                {
                    _photoPanelAppliedVisible = shouldShow;
                    if (!shouldShow)
                    {
                        ResetPhotoImageOverlayStateLocked(resetContent: false);
                    }
                    if (panelVisibilityChanged && shouldShow && !_photoOverlayFirstVisibleLogEmitted)
                    {
                        _photoOverlayFirstVisibleLogEmitted = true;
                        _logger.Debug("SteamVR photo translation panel visible. photoOverlayHandle={PhotoOverlayHandle}", photoHandle);
                    }
                    if (panelVisibilityChanged)
                    {
                        _logger.Debug(
                            "SteamVR photo translation panel visibility changed. visible={Visible}, transcripts={TranscriptCount}, processing={Processing}, dismissed={Dismissed}",
                            shouldShow,
                            _photoPanelTranscripts?.Count ?? 0,
                            _panelState.IsPhotoTranslationProcessing,
                            _photoPanelDismissed);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "SteamVR photo panel visibility update failed.");
            }
        }

        private void UpdatePhotoImageOverlay(CVROverlay overlay, SteamVrOverlayPanelState state)
        {
            if (overlay == null || state == null)
            {
                return;
            }

            ulong imageHandle;
            bool shouldShowPanel;
            bool imageAppliedVisible;
            uint panelDeviceIndex;
            HmdMatrix34_t photoTransform;
            float photoWidthMeters;
            int currentContentHash;

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                imageHandle = _photoImageOverlayHandle;
                shouldShowPanel = ShouldShowPhotoPanelLocked() && _photoPanelAppliedVisible;
                imageAppliedVisible = _photoImageOverlayAppliedVisible;
                panelDeviceIndex = _panelTrackedDeviceIndex;
                photoTransform = _photoPanelTransformInDeviceSpace;
                photoWidthMeters = _photoPanelAppliedWidthMeters;
                currentContentHash = _photoImageOverlayContentHash;
            }

            if (imageHandle == 0)
            {
                return;
            }

            if (!shouldShowPanel
                || panelDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid
                || photoWidthMeters <= 0.0001f
                || !TryGetCurrentPhotoImageItem(state, out var item))
            {
                HidePhotoImageOverlay(overlay, imageHandle, imageAppliedVisible, resetContent: false);
                return;
            }

            var contentHash = ComputePhotoImageOverlayContentHash(item);
            var needsUpload = contentHash == 0 || contentHash != currentContentHash;
            int sourceWidth;
            int sourceHeight;
            int textureWidth;
            int textureHeight;

            if (needsUpload)
            {
                var err = UploadPhotoImageOverlayTexture(imageHandle, item.PhotoImagePath, out sourceWidth, out sourceHeight, out textureWidth, out textureHeight);
                if (err != EVROverlayError.None || sourceWidth <= 0 || sourceHeight <= 0)
                {
                    _logger.Debug(
                        "SteamVR photo image overlay upload failed. error={Error}, path={ImagePath}, width={Width}, height={Height}",
                        err,
                        item.PhotoImagePath,
                        sourceWidth,
                        sourceHeight);
                    HidePhotoImageOverlay(overlay, imageHandle, imageAppliedVisible, resetContent: true);
                    return;
                }

                _logger.Debug(
                    "SteamVR photo image overlay texture uploaded. path={ImagePath}, source={SourceWidth}x{SourceHeight}, texture={TextureWidth}x{TextureHeight}",
                    item.PhotoImagePath,
                    sourceWidth,
                    sourceHeight,
                    textureWidth,
                    textureHeight);

                lock (_lock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _photoImageOverlayContentHash = contentHash;
                    _photoImageOverlayPath = item.PhotoImagePath;
                    _photoImageOverlaySourceWidth = sourceWidth;
                    _photoImageOverlaySourceHeight = sourceHeight;
                }
            }
            else
            {
                lock (_lock)
                {
                    sourceWidth = _photoImageOverlaySourceWidth;
                    sourceHeight = _photoImageOverlaySourceHeight;
                }

                if (sourceWidth <= 0 || sourceHeight <= 0)
                {
                    var err = UploadPhotoImageOverlayTexture(imageHandle, item.PhotoImagePath, out sourceWidth, out sourceHeight, out textureWidth, out textureHeight);
                    if (err != EVROverlayError.None || sourceWidth <= 0 || sourceHeight <= 0)
                    {
                        HidePhotoImageOverlay(overlay, imageHandle, imageAppliedVisible, resetContent: true);
                        return;
                    }

                    lock (_lock)
                    {
                        _photoImageOverlaySourceWidth = sourceWidth;
                        _photoImageOverlaySourceHeight = sourceHeight;
                    }

                    _logger.Debug(
                        "SteamVR photo image overlay texture re-uploaded. path={ImagePath}, source={SourceWidth}x{SourceHeight}, texture={TextureWidth}x{TextureHeight}",
                        item.PhotoImagePath,
                        sourceWidth,
                        sourceHeight,
                        textureWidth,
                        textureHeight);
                }
            }

            var transformApplied = ApplyPhotoImageOverlayTransform(overlay, panelDeviceIndex, photoTransform, photoWidthMeters, sourceWidth, sourceHeight);
            if (!transformApplied)
            {
                HidePhotoImageOverlay(overlay, imageHandle, imageAppliedVisible, resetContent: false);
                return;
            }

            lock (_lock)
            {
                imageAppliedVisible = _photoImageOverlayAppliedVisible;
            }

            if (!imageAppliedVisible)
            {
                var showErr = overlay.ShowOverlay(imageHandle);
                if (showErr == EVROverlayError.InvalidHandle || showErr == EVROverlayError.UnknownOverlay)
                {
                    RequestRestart($"Photo image overlay show failed: {showErr}");
                    return;
                }
                if (showErr != EVROverlayError.None)
                {
                    _logger.Debug("SteamVR photo image overlay show failed. error={Error}", showErr);
                    return;
                }

                lock (_lock)
                {
                    _photoImageOverlayAppliedVisible = true;
                }

                _logger.Debug(
                    "SteamVR photo image overlay visible. path={ImagePath}, source={SourceWidth}x{SourceHeight}, photoWidthMeters={PhotoWidthMeters:0.000}",
                    item.PhotoImagePath,
                    sourceWidth,
                    sourceHeight,
                    photoWidthMeters);
            }
        }

        private bool ApplyPhotoImageOverlayTransformIfVisible(
            CVROverlay overlay,
            uint panelDeviceIndex,
            in HmdMatrix34_t photoTransform,
            float photoWidthMeters)
        {
            int sourceWidth;
            int sourceHeight;
            bool appliedVisible;
            lock (_lock)
            {
                if (_disposed || _photoImageOverlayHandle == 0)
                {
                    return false;
                }

                appliedVisible = _photoImageOverlayAppliedVisible;
                sourceWidth = _photoImageOverlaySourceWidth;
                sourceHeight = _photoImageOverlaySourceHeight;
            }

            return appliedVisible
                && ApplyPhotoImageOverlayTransform(overlay, panelDeviceIndex, photoTransform, photoWidthMeters, sourceWidth, sourceHeight);
        }

        private bool ApplyPhotoImageOverlayTransform(
            CVROverlay overlay,
            uint panelDeviceIndex,
            in HmdMatrix34_t photoTransform,
            float photoWidthMeters,
            int sourceWidth,
            int sourceHeight)
        {
            ulong imageHandle;
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }
                imageHandle = _photoImageOverlayHandle;
            }

            if (overlay == null
                || imageHandle == 0
                || panelDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid
                || photoWidthMeters <= 0.0001f
                || sourceWidth <= 0
                || sourceHeight <= 0)
            {
                return false;
            }

            var contentRect = GetPhotoPanelContentRect();
            var pageRect = GetPhotoPanelNavigationLayout(contentRect).PageRect;
            var imageRect = GetPhotoImageRect(pageRect, sourceWidth, sourceHeight);
            if (imageRect.Width <= 0.1f || imageRect.Height <= 0.1f)
            {
                return false;
            }

            var photoHeightMeters = photoWidthMeters * (PhotoPanelHeight / (float)PhotoPanelWidth);
            var imageWidthMeters = imageRect.Width * (photoWidthMeters / PhotoPanelWidth);
            var imageCenterX = imageRect.X + imageRect.Width * 0.5f;
            var imageCenterY = imageRect.Y + imageRect.Height * 0.5f;
            var localX = (imageCenterX - PhotoPanelWidth * 0.5f) * (photoWidthMeters / PhotoPanelWidth);
            var localY = (PhotoPanelHeight * 0.5f - imageCenterY) * (photoHeightMeters / PhotoPanelHeight);
            var imageLocalT = CreateTranslation(localX, localY, PhotoImageOverlayDepthOffsetMeters);
            var imageTransform = Multiply(photoTransform, imageLocalT);

            var widthErr = overlay.SetOverlayWidthInMeters(imageHandle, imageWidthMeters);
            if (widthErr == EVROverlayError.InvalidHandle || widthErr == EVROverlayError.UnknownOverlay)
            {
                RequestRestart($"Photo image overlay width failed: {widthErr}");
                return false;
            }
            if (widthErr != EVROverlayError.None)
            {
                _logger.Debug("SteamVR photo image overlay width failed. error={Error}, width={Width}", widthErr, imageWidthMeters);
                return false;
            }

            var bounds = new VRTextureBounds_t
            {
                uMin = 0f,
                vMin = 0f,
                uMax = 1f,
                vMax = 1f
            };
            var boundsErr = overlay.SetOverlayTextureBounds(imageHandle, ref bounds);
            if (boundsErr != EVROverlayError.None)
            {
                _logger.Debug("SteamVR photo image overlay bounds failed. error={Error}", boundsErr);
                return false;
            }

            var transformErr = overlay.SetOverlayTransformTrackedDeviceRelative(imageHandle, panelDeviceIndex, ref imageTransform);
            if (transformErr == EVROverlayError.InvalidHandle || transformErr == EVROverlayError.UnknownOverlay)
            {
                RequestRestart($"Photo image overlay transform failed: {transformErr}");
                return false;
            }
            if (transformErr != EVROverlayError.None)
            {
                _logger.Debug("SteamVR photo image overlay transform failed. error={Error}", transformErr);
                return false;
            }

            return true;
        }

        private void HidePhotoImageOverlay(CVROverlay overlay, ulong imageHandle, bool appliedVisible, bool resetContent)
        {
            if (overlay != null && imageHandle != 0 && appliedVisible)
            {
                try
                {
                    overlay.HideOverlay(imageHandle);
                }
                catch { }
            }

            if (overlay != null && imageHandle != 0 && resetContent)
            {
                try
                {
                    overlay.ClearOverlayTexture(imageHandle);
                }
                catch { }
            }

            lock (_lock)
            {
                ResetPhotoImageOverlayStateLocked(resetContent);
            }
        }

        private static bool TryGetCurrentPhotoImageItem(SteamVrOverlayPanelState state, out SteamVrOverlayTranscript item)
        {
            item = default!;
            var items = state.Transcripts ?? Array.Empty<SteamVrOverlayTranscript>();
            if (items.Count == 0)
            {
                return false;
            }

            var currentIndex = Math.Clamp(state.PhotoPanelIndex, 0, items.Count - 1);
            var currentItem = items[currentIndex];
            if (!IsPhotoImageResultDrawable(currentItem))
            {
                return false;
            }

            item = currentItem;
            return true;
        }

        private static int ComputePhotoImageOverlayContentHash(SteamVrOverlayTranscript item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.PhotoImagePath))
            {
                return 0;
            }

            var hash = new HashCode();
            hash.Add(item.PhotoImagePath);
            hash.Add(item.Timestamp.Ticks);
            try
            {
                var info = new FileInfo(item.PhotoImagePath);
                if (info.Exists)
                {
                    hash.Add(info.Length);
                    hash.Add(info.LastWriteTimeUtc.Ticks);
                }
            }
            catch { }

            return hash.ToHashCode();
        }

        private static EVROverlayError UploadPhotoImageOverlayTexture(
            ulong overlayHandle,
            string imagePath,
            out int width,
            out int height,
            out int textureWidth,
            out int textureHeight)
        {
            width = 0;
            height = 0;
            textureWidth = 0;
            textureHeight = 0;
            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var image = Image.FromStream(stream);
                width = image.Width;
                height = image.Height;
                if (width <= 0 || height <= 0)
                {
                    return EVROverlayError.InvalidTexture;
                }

                EVROverlayError firstError = EVROverlayError.None;
                foreach (var candidate in BuildPhotoImageTextureSizes(width, height))
                {
                    EVROverlayError err;
                    try
                    {
                        err = UploadPhotoImageBitmap(overlayHandle, image, candidate.Width, candidate.Height);
                    }
                    catch (OutOfMemoryException)
                    {
                        err = EVROverlayError.InvalidTexture;
                    }
                    catch
                    {
                        err = EVROverlayError.UnknownOverlay;
                    }

                    if (err == EVROverlayError.None)
                    {
                        textureWidth = candidate.Width;
                        textureHeight = candidate.Height;
                        return EVROverlayError.None;
                    }

                    if (firstError == EVROverlayError.None)
                    {
                        firstError = err;
                    }

                    _logger.Debug(
                        "SteamVR photo image overlay texture upload candidate failed. error={Error}, path={ImagePath}, texture={TextureWidth}x{TextureHeight}",
                        err,
                        imagePath,
                        candidate.Width,
                        candidate.Height);
                }

                return firstError == EVROverlayError.None ? EVROverlayError.InvalidTexture : firstError;
            }
            catch (OutOfMemoryException)
            {
                return EVROverlayError.InvalidTexture;
            }
            catch
            {
                return EVROverlayError.UnknownOverlay;
            }
        }

        private static IEnumerable<(int Width, int Height)> BuildPhotoImageTextureSizes(int sourceWidth, int sourceHeight)
        {
            var emitted = new HashSet<(int Width, int Height)>();
            foreach (var maxDimension in new[]
            {
                Math.Max(sourceWidth, sourceHeight),
                PhotoImageOverlayFallbackMaxDimension,
                3072,
                2048,
                1920
            })
            {
                var size = FitImageSize(sourceWidth, sourceHeight, maxDimension);
                if (size.Width <= 0 || size.Height <= 0 || !emitted.Add(size))
                {
                    continue;
                }

                yield return size;
            }
        }

        private static (int Width, int Height) FitImageSize(int sourceWidth, int sourceHeight, int maxDimension)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || maxDimension <= 0)
            {
                return (0, 0);
            }

            var longest = Math.Max(sourceWidth, sourceHeight);
            if (longest <= maxDimension)
            {
                return (sourceWidth, sourceHeight);
            }

            var scale = maxDimension / (float)longest;
            return (
                Math.Max(1, (int)Math.Round(sourceWidth * scale)),
                Math.Max(1, (int)Math.Round(sourceHeight * scale)));
        }

        private static EVROverlayError UploadPhotoImageBitmap(ulong overlayHandle, Image image, int width, int height)
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = width == image.Width && height == image.Height
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Color.Transparent);
                g.DrawImage(image, new Rectangle(0, 0, width, height));
            }

            return UploadBitmap(overlayHandle, bmp);
        }

        private static EVROverlayError UploadText(ulong overlayHandle, string text)
        {
            // Keep bitmap reasonably small; OpenVR will scale based on overlay width in meters.
            const int width = 1024;
            const int height = 256;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0, 0, 0, 0));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using var font = new Font("Microsoft YaHei UI", 40, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.White);
                var rect = new RectangleF(16, 16, width - 32, height - 32);

                using var sf = new StringFormat
                {
                    Trimming = StringTrimming.None,
                    FormatFlags = 0
                };

                g.DrawString(text, font, brush, rect, sf);
            }

            var overlay = Valve.VR.OpenVR.Overlay;
            if (overlay == null) return EVROverlayError.InvalidHandle;

            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // unDepth = bytes per pixel (4)
                var err = overlay.SetOverlayRaw(overlayHandle, data.Scan0, (uint)width, (uint)height, 4);
                if (err != EVROverlayError.None)
                {
                    // Usually means overlay not ready/visible or invalid handle.
                    // Keep it debug-level to avoid log spam.
                    Log.Debug("SetOverlayRaw failed: {Error}", err);
                }
                return err;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private void SetDisconnected()
        {
            lock (_lock)
            {
                if (!IsConnected) return;
                IsConnected = false;
            }

            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private void CleanupFailedInit()
        {
            var stopwatch = Stopwatch.StartNew();
            var overlayHandle = _overlayHandle;
            var cursorOverlayHandle = _cursorOverlayHandle;
            var photoOverlayHandle = _photoOverlayHandle;
            var photoImageOverlayHandle = _photoImageOverlayHandle;
            LogDiagnosticDebug(
                "SteamVR cleanup after failed init started. overlayHandle={OverlayHandle}, cursorOverlayHandle={CursorOverlayHandle}, photoOverlayHandle={PhotoOverlayHandle}, photoImageOverlayHandle={PhotoImageOverlayHandle}, connected={Connected}",
                overlayHandle,
                cursorOverlayHandle,
                photoOverlayHandle,
                photoImageOverlayHandle,
                IsConnected);

            try
            {
                try
                {
                    _eventLoopCts?.Cancel();
                    _eventLoopCts?.Dispose();
                    _eventLoopCts = null;
                }
                catch { }

                var overlay = Valve.VR.OpenVR.Overlay;
                if (overlay != null && _overlayHandle != 0)
                {
                    try { overlay.HideOverlay(_overlayHandle); } catch { }
                    try { overlay.DestroyOverlay(_overlayHandle); } catch { }
                    _overlayHandle = 0;
                }

                if (overlay != null && _cursorOverlayHandle != 0)
                {
                    try { overlay.HideOverlay(_cursorOverlayHandle); } catch { }
                    try { overlay.DestroyOverlay(_cursorOverlayHandle); } catch { }
                    _cursorOverlayHandle = 0;
                }

                if (overlay != null && _photoOverlayHandle != 0)
                {
                    try { overlay.HideOverlay(_photoOverlayHandle); } catch { }
                    try { overlay.DestroyOverlay(_photoOverlayHandle); } catch { }
                    _photoOverlayHandle = 0;
                }

                if (overlay != null && _photoImageOverlayHandle != 0)
                {
                    try { overlay.HideOverlay(_photoImageOverlayHandle); } catch { }
                    try { overlay.DestroyOverlay(_photoImageOverlayHandle); } catch { }
                    _photoImageOverlayHandle = 0;
                }

                _cursorAppliedVisible = false;
                _cursorAppliedU = float.NaN;
                _cursorAppliedV = float.NaN;
                _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorAppliedRayDistanceMeters = float.NaN;
                _cursorRayDistanceMeters = CursorLaserDefaultLengthMeters;
                _cursorOverlayIsSubview = false;
                _cursorOverlayUsesNativeCursor = false;
                _cursorOverlayFirstVisibleLogEmitted = false;
                _photoOverlayFirstVisibleLogEmitted = false;
                _photoPanelAppliedVisible = false;
                ResetPhotoImageOverlayStateLocked(resetContent: true);
                _photoPanelDismissed = false;
                _photoPanelAppliedWidthMeters = 0f;
                _photoPanelLatestTranscriptHash = 0;
                _photoPanelPageIndex = 0;
                ResetPhotoPanelTextScrollLocked();
                ResetPanelLockStateLocked();
                _cursorDirty = false;
                _panelStateHash = 0;
                _photoPanelStateHash = 0;
                _lastPanelRenderUtc = DateTime.MinValue;
                _lastPhotoPanelRenderUtc = DateTime.MinValue;
                InvalidatePointerDeviceCandidateCacheLocked();
            }
            catch { }

            var shutdownWatch = Stopwatch.StartNew();
            try { Valve.VR.OpenVR.Shutdown(); }
            catch (Exception ex)
            {
                if (IsDiagnosticLoggingEnabled)
                {
                    _logger.Debug(ex, "OpenVR.Shutdown failed during failed-init cleanup.");
                }
            }
            finally
            {
                shutdownWatch.Stop();
                stopwatch.Stop();
                LogDiagnosticDebug(
                    "SteamVR cleanup after failed init finished. shutdownMs={ShutdownMs}, elapsedMs={ElapsedMs}",
                    shutdownWatch.ElapsedMilliseconds,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        private static bool IsSteamVrRunning()
        {
            try
            {
                // SteamVR core processes on Windows. "vrserver" is the most reliable indicator.
                return Process.GetProcessesByName("vrserver").Length > 0
                    || Process.GetProcessesByName("vrmonitor").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeIsHmdPresent()
        {
            try
            {
                return Valve.VR.OpenVR.IsHmdPresent();
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldLogOpenVrRetryAttempt(int attempt, int fastAttempts)
        {
            return attempt == 1
                || attempt == fastAttempts
                || attempt == fastAttempts + 1
                || attempt % 12 == 0;
        }

        private void EnsureSteamVrApplicationRegistration(bool enabled, bool refreshOnly = false)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var manifestPath = EnsureSteamVrManifestWritten();
                var hasOpenVrContext = IsConnected || Valve.VR.OpenVR.System != null;
                var steamVrRunning = hasOpenVrContext || IsSteamVrRunning();
                LogDiagnosticInformation(
                    "SteamVR registration check started. enabled={Enabled}, refreshOnly={RefreshOnly}, hasOpenVrContext={HasOpenVrContext}, steamVrRunning={SteamVrRunning}, manifestPath={ManifestPath}",
                    enabled,
                    refreshOnly,
                    hasOpenVrContext,
                    steamVrRunning,
                    manifestPath);

                if (!hasOpenVrContext && !steamVrRunning)
                {
                    LogDiagnosticInformation(
                        "SteamVR registration postponed because SteamVR is not running. enabled={Enabled}, refreshOnly={RefreshOnly}",
                        enabled,
                        refreshOnly);
                    SetStartupRegistrationStatus(enabled
                        ? "SteamVR auto-launch pending. Start SteamVR once to finish registration."
                        : "SteamVR auto-launch disabled");
                    return;
                }

                var shouldShutdown = false;
                if (!hasOpenVrContext)
                {
                    var initError = EVRInitError.None;
                    var initWatch = Stopwatch.StartNew();
                    LogDiagnosticInformation("OpenVR.Init background start for SteamVR registration.");
                    Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
                    initWatch.Stop();
                    LogDiagnosticInformation(
                        "OpenVR.Init background returned for SteamVR registration. error={Error}, elapsedMs={ElapsedMs}",
                        initError,
                        initWatch.ElapsedMilliseconds);
                    if (initError != EVRInitError.None)
                    {
                        SetStartupRegistrationStatus($"SteamVR registration init failed: {initError}");
                        return;
                    }

                    shouldShutdown = true;
                }

                try
                {
                    var applications = Valve.VR.OpenVR.Applications;
                    if (applications == null)
                    {
                        SetStartupRegistrationStatus("SteamVR applications interface unavailable");
                        return;
                    }

                    var addErr = applications.AddApplicationManifest(manifestPath, false);
                    LogDiagnosticDebug("SteamVR AddApplicationManifest returned. error={Error}, manifestPath={ManifestPath}", addErr, manifestPath);
                    if (addErr != EVRApplicationError.None && addErr != EVRApplicationError.AppKeyAlreadyExists)
                    {
                        SetStartupRegistrationStatus($"SteamVR manifest registration failed: {FormatApplicationError(applications, addErr)}");
                        return;
                    }

                    if (!refreshOnly || enabled)
                    {
                        var autoErr = applications.SetApplicationAutoLaunch(SteamVrAppKey, enabled);
                        LogDiagnosticDebug("SteamVR SetApplicationAutoLaunch returned. enabled={Enabled}, error={Error}", enabled, autoErr);
                        if (autoErr != EVRApplicationError.None)
                        {
                            SetStartupRegistrationStatus($"SteamVR auto-launch update failed: {FormatApplicationError(applications, autoErr)}");
                            return;
                        }
                    }

                    var autoLaunch = applications.GetApplicationAutoLaunch(SteamVrAppKey);
                    LogDiagnosticInformation(
                        "SteamVR registration state refreshed. enabledRequested={Enabled}, autoLaunch={AutoLaunch}, refreshOnly={RefreshOnly}",
                        enabled,
                        autoLaunch,
                        refreshOnly);
                    SetStartupRegistrationStatus(autoLaunch
                        ? "Registered with SteamVR auto-launch"
                        : "Registered with SteamVR (auto-launch off)");

                    if (enabled)
                    {
                        TryIdentifyCurrentProcessInternal(applications);
                    }
                }
                finally
                {
                    if (shouldShutdown)
                    {
                        try { Valve.VR.OpenVR.Shutdown(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                SetStartupRegistrationStatus($"SteamVR registration failed: {ex.GetType().Name}");
                _logger.Warning(ex, "SteamVR app registration failed. enabled={Enabled}, refreshOnly={RefreshOnly}, elapsedMs={ElapsedMs}", enabled, refreshOnly, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                stopwatch.Stop();
                LogDiagnosticDebug(
                    "SteamVR registration check finished. enabled={Enabled}, refreshOnly={RefreshOnly}, elapsedMs={ElapsedMs}, startupStatus={StartupStatus}",
                    enabled,
                    refreshOnly,
                    stopwatch.ElapsedMilliseconds,
                    StartupRegistrationStatus);
            }
        }

        private void TryIdentifyCurrentProcessInternal()
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var hasOpenVrContext = IsConnected || Valve.VR.OpenVR.System != null;
                var steamVrRunning = hasOpenVrContext || IsSteamVrRunning();
                LogDiagnosticDebug(
                    "SteamVR IdentifyApplication start. processId={ProcessId}, hasOpenVrContext={HasOpenVrContext}, steamVrRunning={SteamVrRunning}",
                    Environment.ProcessId,
                    hasOpenVrContext,
                    steamVrRunning);

                if (!hasOpenVrContext && !steamVrRunning)
                {
                    LogDiagnosticDebug("SteamVR IdentifyApplication skipped because SteamVR is not running.");
                    return;
                }

                var shouldShutdown = false;
                if (!hasOpenVrContext)
                {
                    var initError = EVRInitError.None;
                    var initWatch = Stopwatch.StartNew();
                    LogDiagnosticDebug("OpenVR.Init background start for SteamVR IdentifyApplication.");
                    Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
                    initWatch.Stop();
                    LogDiagnosticDebug(
                        "OpenVR.Init background returned for SteamVR IdentifyApplication. error={Error}, elapsedMs={ElapsedMs}",
                        initError,
                        initWatch.ElapsedMilliseconds);
                    if (initError != EVRInitError.None)
                    {
                        LogDiagnosticInformation("SteamVR IdentifyApplication skipped because OpenVR.Init failed. error={Error}", initError);
                        return;
                    }

                    shouldShutdown = true;
                }

                try
                {
                    var applications = Valve.VR.OpenVR.Applications;
                    if (applications == null)
                    {
                        LogDiagnosticInformation("SteamVR IdentifyApplication skipped because OpenVR.Applications is unavailable.");
                        return;
                    }

                    TryIdentifyCurrentProcessInternal(applications);
                }
                finally
                {
                    if (shouldShutdown)
                    {
                        try { Valve.VR.OpenVR.Shutdown(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Identify SteamVR application failed. elapsedMs={ElapsedMs}", stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                stopwatch.Stop();
                LogDiagnosticDebug("SteamVR IdentifyApplication finished. elapsedMs={ElapsedMs}", stopwatch.ElapsedMilliseconds);
            }
        }

        private void TryIdentifyCurrentProcessInternal(CVRApplications applications)
        {
            try
            {
                var identifyError = applications.IdentifyApplication((uint)Environment.ProcessId, SteamVrAppKey);
                if (identifyError == EVRApplicationError.None)
                {
                    LogDiagnosticDebug("SteamVR IdentifyApplication succeeded. processId={ProcessId}, appKey={AppKey}", Environment.ProcessId, SteamVrAppKey);
                }
                else
                {
                    LogDiagnosticInformation(
                        "SteamVR IdentifyApplication returned non-success. processId={ProcessId}, appKey={AppKey}, error={Error}",
                        Environment.ProcessId,
                        SteamVrAppKey,
                        identifyError);
                }
            }
            catch (Exception ex)
            {
                if (IsDiagnosticLoggingEnabled)
                {
                    _logger.Debug(ex, "SteamVR IdentifyApplication threw.");
                }
            }
        }

        private static string EnsureSteamVrManifestWritten()
        {
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "vrcls.vrmanifest");
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vrcls_logo.png");
            var workingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            var manifest = new
            {
                source = "builtin",
                applications = new object[]
                {
                    new
                    {
                        app_key = SteamVrAppKey,
                        launch_type = "binary",
                        binary_path_windows = exePath,
                        working_directory = workingDirectory,
                        arguments = string.Empty,
                        is_dashboard_overlay = false,
                        image_path = File.Exists(iconPath) ? iconPath : string.Empty,
                        strings = new
                        {
                            en_us = new
                            {
                                name = "VRCLS",
                                description = "VRCLS SteamVR subtitle overlay"
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(manifestPath, json);
            return manifestPath;
        }

        private static string FormatApplicationError(CVRApplications applications, EVRApplicationError error)
        {
            try
            {
                return applications.GetApplicationsErrorNameFromEnum(error);
            }
            catch
            {
                return error.ToString();
            }
        }

        private void SetStartupRegistrationStatus(string status)
        {
            EventHandler<string>? handler = null;
            var changed = false;

            lock (_lock)
            {
                if (_startupRegistrationStatus != status)
                {
                    _startupRegistrationStatus = status;
                    changed = true;
                    handler = StartupRegistrationStatusChanged;
                }
            }

            if (changed)
            {
                try { handler?.Invoke(this, status); } catch { }
            }
        }

        private void QueueOpenVrOperation(string operationName, Action operation)
        {
            if (operation == null) return;

            var operationId = Interlocked.Increment(ref _openVrOperationSequence);

            lock (_lock)
            {
                if (_disposed)
                {
                    LogDiagnosticDebug("OpenVR operation not queued because service is disposed. id={OperationId}, operation={OperationName}", operationId, operationName);
                    return;
                }
            }

            LogDiagnosticDebug(
                "OpenVR operation queued. id={OperationId}, operation={OperationName}, connected={Connected}, desiredVisible={DesiredVisible}",
                operationId,
                operationName,
                IsConnected,
                _desiredVisible);

            _ = Task.Run(async () =>
            {
                var waitWatch = Stopwatch.StartNew();
                await _openVrOperationGate.WaitAsync().ConfigureAwait(false);
                waitWatch.Stop();
                var operationWatch = Stopwatch.StartNew();

                try
                {
                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            operationWatch.Stop();
                            LogDiagnosticDebug(
                                "OpenVR operation skipped after waiting because service is disposed. id={OperationId}, operation={OperationName}, waitMs={WaitMs}",
                                operationId,
                                operationName,
                                waitWatch.ElapsedMilliseconds);
                            return;
                        }
                    }

                    LogDiagnosticDebug(
                        "OpenVR operation started. id={OperationId}, operation={OperationName}, waitMs={WaitMs}",
                        operationId,
                        operationName,
                        waitWatch.ElapsedMilliseconds);

                    operation();
                    operationWatch.Stop();
                    LogDiagnosticInformation(
                        "OpenVR operation completed. id={OperationId}, operation={OperationName}, waitMs={WaitMs}, elapsedMs={ElapsedMs}, connected={Connected}",
                        operationId,
                        operationName,
                        waitWatch.ElapsedMilliseconds,
                        operationWatch.ElapsedMilliseconds,
                        IsConnected);
                }
                catch (Exception ex)
                {
                    operationWatch.Stop();
                    _logger.Warning(
                        ex,
                        "OpenVR operation failed. id={OperationId}, operation={OperationName}, waitMs={WaitMs}, elapsedMs={ElapsedMs}",
                        operationId,
                        operationName,
                        waitWatch.ElapsedMilliseconds,
                        operationWatch.ElapsedMilliseconds);
                }
                finally
                {
                    _openVrOperationGate.Release();
                }
            });
        }

        private async Task<bool> TryInitializeOnceAsync()
        {
            var waitWatch = Stopwatch.StartNew();
            LogDiagnosticDebug("SteamVR overlay init waiting for OpenVR operation gate.");
            await _openVrOperationGate.WaitAsync().ConfigureAwait(false);
            waitWatch.Stop();
            var initWatch = Stopwatch.StartNew();
            var success = false;
            try
            {
                LogDiagnosticDebug("SteamVR overlay init acquired OpenVR operation gate. waitMs={WaitMs}", waitWatch.ElapsedMilliseconds);
                success = TryInitializeOnce();
                return success;
            }
            finally
            {
                initWatch.Stop();
                LogDiagnosticInformation(
                    "SteamVR overlay init OpenVR section finished. success={Success}, waitMs={WaitMs}, elapsedMs={ElapsedMs}, connected={Connected}",
                    success,
                    waitWatch.ElapsedMilliseconds,
                    initWatch.ElapsedMilliseconds,
                    IsConnected);
                _openVrOperationGate.Release();
            }
        }

        private bool TryInitializeOnce()
        {
            var totalWatch = Stopwatch.StartNew();
            try
            {
                SetStatus("Initializing SteamVR overlay...");

                var error = EVRInitError.None;
                var openVrInitWatch = Stopwatch.StartNew();
                if (IsDiagnosticLoggingEnabled)
                {
                    LogDiagnosticInformation(
                        "OpenVR.Init overlay start. processId={ProcessId}, steamVrRunning={SteamVrRunning}, hmdPresent={HmdPresent}",
                        Environment.ProcessId,
                        IsSteamVrRunning(),
                        SafeIsHmdPresent());
                }
                Valve.VR.OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
                openVrInitWatch.Stop();
                LogDiagnosticInformation(
                    "OpenVR.Init overlay returned. error={Error}, elapsedMs={ElapsedMs}",
                    error,
                    openVrInitWatch.ElapsedMilliseconds);
                if (error != EVRInitError.None)
                {
                    _logger.Warning("SteamVR 初始化失败 {Error}", error);
                    SetStatus($"SteamVR init failed: {error}");
                    SetDisconnected();
                    CleanupFailedInit();
                    return false;
                }

                var overlay = Valve.VR.OpenVR.Overlay;
                if (overlay == null)
                {
                    _logger.Warning("OpenVR.Overlay unavailable after successful OpenVR.Init.");
                    SetStatus("OpenVR.Overlay unavailable");
                    SetDisconnected();
                    CleanupFailedInit();
                    return false;
                }

                var createOverlayWatch = Stopwatch.StartNew();
                var overlayError = overlay.CreateOverlay("vrcls.subtitle", "VRCLS Subtitle", ref _overlayHandle);
                createOverlayWatch.Stop();
                LogDiagnosticDebug(
                    "SteamVR CreateOverlay returned. error={Error}, elapsedMs={ElapsedMs}, overlayHandle={OverlayHandle}",
                    overlayError,
                    createOverlayWatch.ElapsedMilliseconds,
                    _overlayHandle);
                if (overlayError != EVROverlayError.None)
                {
                    _logger.Warning("创建 Overlay 失败: {Error}", overlayError);
                    SetStatus($"CreateOverlay failed: {overlayError}");
                    SetDisconnected();
                    CleanupFailedInit();
                    return false;
                }

                // Separate non-interactive cursor overlay. Render the panel intersection point as
                // its own overlay instead of re-uploading the full panel texture for cursor motion.
                // Do not bind it through OpenVR's native cursor API; that enables SteamVR laser-mouse
                // mode and steals input from VRChat.
                var cursorHandle = 0UL;
                try
                {
                    var key = $"vrcls.cursor.{DateTime.UtcNow.Ticks}";
                    var name = $"VRCLS Cursor {DateTime.UtcNow.Ticks}";
                    var cursorErr = overlay.CreateOverlay(key, name, ref cursorHandle);
                    var cursorIsSubview = false;

                    if (cursorErr == EVROverlayError.None)
                    {
                        _cursorOverlayHandle = cursorHandle;
                        _cursorOverlayIsSubview = cursorIsSubview;
                        _cursorOverlayUsesNativeCursor = false;
                        _cursorOverlayFirstVisibleLogEmitted = false;
                        overlay.SetOverlayAlpha(_cursorOverlayHandle, 1.0f);
                        overlay.SetOverlaySortOrder(_cursorOverlayHandle, CursorOverlaySortOrder);
                        overlay.SetOverlayFlag(_cursorOverlayHandle, VROverlayFlags.SortWithNonSceneOverlays, true);
                        overlay.SetOverlayWidthInMeters(_cursorOverlayHandle, CursorDotOverlayWidthMeters);
                        overlay.SetOverlayColor(_cursorOverlayHandle, 1.0f, 1.0f, 1.0f);
                        // Keep cursor overlay non-interactive as well (avoid stealing app input).
                        overlay.SetOverlayInputMethod(_cursorOverlayHandle, VROverlayInputMethod.None);
                        overlay.SetOverlayFlag(_cursorOverlayHandle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
                        var uploadErr = UploadCursorTexture(_cursorOverlayHandle);
                        _logger.Debug(
                            "SteamVR cursor texture upload returned. error={Error}, cursorOverlayHandle={CursorOverlayHandle}, isSubview={IsSubview}, size={Size}",
                            uploadErr,
                            _cursorOverlayHandle,
                            cursorIsSubview,
                            CursorDotTextureSize);
                        if (uploadErr != EVROverlayError.None)
                        {
                            overlay.HideOverlay(_cursorOverlayHandle);
                            overlay.DestroyOverlay(_cursorOverlayHandle);
                            _cursorOverlayHandle = 0;
                            _cursorOverlayIsSubview = false;
                            _logger.Debug("SteamVR cursor texture upload failed; cursor overlay disabled. error={Error}", uploadErr);
                        }
                        else
                        {
                            _cursorOverlayUsesNativeCursor = false;
                            _cursorU = 0.5f;
                            _cursorV = 0.5f;
                            _cursorRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                            _cursorRayDistanceMeters = CursorLaserDefaultLengthMeters;
                            _cursorValid = false;
                            _lastCursorUpdateUtc = DateTime.MinValue;
                            _cursorDirty = false;
                            overlay.HideOverlay(_cursorOverlayHandle);
                            _logger.Debug(
                                "SteamVR cursor using non-interactive {CursorOverlayKind} overlay. cursorOverlayHandle={CursorOverlayHandle}",
                                "panel-dot",
                                _cursorOverlayHandle);
                        }
                    }
                    else
                    {
                        _logger.Debug("Create cursor overlay failed: {Error}", cursorErr);
                    }
                }
                catch
                {
                    // best-effort; panel still works without a separate cursor overlay
                }

                try
                {
                    var photoHandle = 0UL;
                    var key = $"vrcls.photo.{DateTime.UtcNow.Ticks}";
                    var name = $"VRCLS Photo Translation {DateTime.UtcNow.Ticks}";
                    var photoErr = overlay.CreateOverlay(key, name, ref photoHandle);
                    if (photoErr == EVROverlayError.None)
                    {
                        _photoOverlayHandle = photoHandle;
                        _photoOverlayFirstVisibleLogEmitted = false;
                        _photoPanelAppliedVisible = false;
                        overlay.SetOverlayAlpha(_photoOverlayHandle, 1.0f);
                        overlay.SetOverlaySortOrder(_photoOverlayHandle, PhotoOverlaySortOrder);
                        overlay.SetOverlayFlag(_photoOverlayHandle, VROverlayFlags.SortWithNonSceneOverlays, true);
                        overlay.SetOverlayWidthInMeters(_photoOverlayHandle, Math.Clamp(_size * PhotoPanelWidthScale, 0.05f, 0.45f));
                        overlay.SetOverlayColor(_photoOverlayHandle, 1.0f, 1.0f, 1.0f);
                        overlay.SetOverlayInputMethod(_photoOverlayHandle, VROverlayInputMethod.None);
                        overlay.SetOverlayFlag(_photoOverlayHandle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
                        overlay.HideOverlay(_photoOverlayHandle);
                        _logger.Debug("SteamVR photo translation overlay created. photoOverlayHandle={PhotoOverlayHandle}", _photoOverlayHandle);
                    }
                    else
                    {
                        _logger.Debug("Create photo translation overlay failed: {Error}", photoErr);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Create photo translation overlay exception.");
                }

                try
                {
                    var photoImageHandle = 0UL;
                    var key = $"vrcls.photo.image.{DateTime.UtcNow.Ticks}";
                    var name = $"VRCLS Photo Translation Image {DateTime.UtcNow.Ticks}";
                    var photoImageErr = overlay.CreateOverlay(key, name, ref photoImageHandle);
                    if (photoImageErr == EVROverlayError.None)
                    {
                        _photoImageOverlayHandle = photoImageHandle;
                        _photoImageOverlayAppliedVisible = false;
                        _photoImageOverlayContentHash = 0;
                        _photoImageOverlayPath = string.Empty;
                        _photoImageOverlaySourceWidth = 0;
                        _photoImageOverlaySourceHeight = 0;
                        overlay.SetOverlayAlpha(_photoImageOverlayHandle, 1.0f);
                        overlay.SetOverlaySortOrder(_photoImageOverlayHandle, PhotoImageOverlaySortOrder);
                        overlay.SetOverlayFlag(_photoImageOverlayHandle, VROverlayFlags.SortWithNonSceneOverlays, true);
                        overlay.SetOverlayColor(_photoImageOverlayHandle, 1.0f, 1.0f, 1.0f);
                        overlay.SetOverlayTextureColorSpace(_photoImageOverlayHandle, EColorSpace.Gamma);
                        overlay.SetOverlayInputMethod(_photoImageOverlayHandle, VROverlayInputMethod.None);
                        overlay.SetOverlayFlag(_photoImageOverlayHandle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
                        overlay.HideOverlay(_photoImageOverlayHandle);
                        _logger.Debug("SteamVR photo image overlay created. photoImageOverlayHandle={PhotoImageOverlayHandle}", _photoImageOverlayHandle);
                    }
                    else
                    {
                        _logger.Debug("Create photo image overlay failed: {Error}", photoImageErr);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Create photo image overlay exception.");
                }

                // Enable interactive events so we can implement a "dashboard-like" panel with a cursor dot.
                try
                {
                    try
                    {
                        var mouseScale = new HmdVector2_t { v0 = PanelWidth, v1 = PanelHeight };
                        var mouseScaleErr = overlay.SetOverlayMouseScale(_overlayHandle, ref mouseScale);
                        LogDiagnosticDebug("SteamVR SetOverlayMouseScale returned. error={Error}, width={Width}, height={Height}", mouseScaleErr, PanelWidth, PanelHeight);
                    }
                    catch (Exception ex)
                    {
                        LogDiagnosticDebug("SteamVR SetOverlayMouseScale exception: {ExceptionType}", ex.GetType().Name);
                    }

                    // Keep the panel non-interactive so SteamVR does not steal VRChat input. Cursor
                    // position and clicks are handled by controller ray polling below.
                    overlay.SetOverlayInputMethod(_overlayHandle, VROverlayInputMethod.None);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.HideLaserIntersection, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.SendVRTouchpadEvents, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.SendVRDiscreteScrollEvents, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.SendVRSmoothScrollEvents, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.ShowTouchPadScrollWheel, false);
                    _logger.Debug("SteamVR panel input mode applied. nativeCursor=false");
                }
                catch { }

                ApplySizeInternal(_size);
                ApplyCursorSizeInternal(_panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size);
                var placementApplied = ApplyPlacementInternal();
                LogDiagnosticDebug(
                    "SteamVR overlay placement applied. hand={Hand}, size={Size}, anchorMode={AnchorMode}, applied={Applied}",
                    _hand,
                    _size,
                    _config.SteamVrAnchorModeValue,
                    placementApplied);
                EnsureSteamVrApplicationRegistration(_config.SteamVrAutoLaunch, refreshOnly: true);
                TryIdentifyCurrentProcessInternal();
                StartAttachRetryLoop();
                StartEventLoop();

                overlay.SetOverlayAlpha(_overlayHandle, 1.0f);
                if (_desiredVisible && placementApplied)
                {
                    overlay.ShowOverlay(_overlayHandle);
                }
                else
                {
                    overlay.HideOverlay(_overlayHandle);
                }
                _appliedVisible = _desiredVisible && placementApplied;

                IsConnected = true;
                if (placementApplied)
                {
                    SetStatus("SteamVR overlay connected");
                }
                totalWatch.Stop();
                LogDiagnosticInformation(
                    "SteamVR Overlay initialized. hand={Hand}, size={Size}, desiredVisible={DesiredVisible}, overlayHandle={OverlayHandle}, cursorOverlayHandle={CursorOverlayHandle}, photoOverlayHandle={PhotoOverlayHandle}, elapsedMs={ElapsedMs}",
                    _hand,
                    _size,
                    _desiredVisible,
                    _overlayHandle,
                    _cursorOverlayHandle,
                    _photoOverlayHandle,
                    totalWatch.ElapsedMilliseconds);
                _logger.Information("SteamVR Overlay 初始化完成(hand={Hand}, size={Size})", _hand, _size);
                Connected?.Invoke(this, EventArgs.Empty);

                // Push a visible initial frame (render loop will upload).
                lock (_lock)
                {
                    _panelDirty = true;
                    _photoPanelDirty = true;
                    _lastPanelRenderUtc = DateTime.MinValue;
                    _lastPhotoPanelRenderUtc = DateTime.MinValue;
                }

                return true;
            }
            catch (DllNotFoundException ex)
            {
                // Typically missing openvr_api.dll / wrong architecture.
                _logger.Warning(ex, "OpenVR native DLL load failed");
                SetStatus("OpenVR native DLL load failed (openvr_api.dll)");
                SetDisconnected();
                CleanupFailedInit();
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SteamVR overlay init exception");
                SetStatus($"SteamVR overlay init exception: {ex.GetType().Name}");
                SetDisconnected();
                CleanupFailedInit();
                return false;
            }
        }

        private void RequestRestart(string reason)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (!_desiredVisible) return;
                if (_restartInProgress) return;
                _restartInProgress = true;
            }

            LogDiagnosticInformation("SteamVR overlay repair queued. reason={Reason}", reason);

            _ = Task.Run(async () =>
            {
                try
                {
                    const int maxRepairAttempts = 3;
                    for (var attempt = 1; attempt <= maxRepairAttempts; attempt++)
                    {
                        if (_disposed) return;
                        if (!_desiredVisible) return;

                        SetStatus($"Repairing SteamVR overlay ({attempt}/{maxRepairAttempts}): {reason}");

                        try
                        {
                            CleanupFailedInit();
                            SetDisconnected();
                        }
                        catch { }

                        // Give SteamVR a moment to settle.
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

                        if (!IsSteamVrRunning() || !SafeIsHmdPresent())
                        {
                            LogDiagnosticDebug(
                                "SteamVR overlay repair waiting for SteamVR/HMD. attempt={Attempt}, reason={Reason}",
                                attempt,
                                reason);
                            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                            continue;
                        }

                        if (await TryInitializeOnceAsync().ConfigureAwait(false))
                        {
                            LogDiagnosticInformation("SteamVR overlay repair completed. attempt={Attempt}, reason={Reason}", attempt, reason);
                            return;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                    }

                    SetStatus("SteamVR overlay repair failed (manual retry needed)");
                }
                finally
                {
                    lock (_lock) _restartInProgress = false;
                    LogDiagnosticDebug("SteamVR overlay repair task finished. reason={Reason}", reason);
                }
            });
        }

        private void SetStatus(string status)
        {
            EventHandler<string>? handler = null;
            var changed = false;

            lock (_lock)
            {
                if (_status != status)
                {
                    _status = status;
                    changed = true;
                    handler = StatusChanged;
                }
            }

            if (changed)
            {
                try { handler?.Invoke(this, status); } catch { }
            }
        }

        private void StartEventLoop()
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_eventLoopCts != null) return;
                _eventLoopCts = new CancellationTokenSource();
            }

            var token = _eventLoopCts.Token;
            _ = Task.Run(async () =>
            {
                var evt = new VREvent_t();
                var evtSize = (uint)Marshal.SizeOf<VREvent_t>();

                while (!token.IsCancellationRequested)
                {
                    ulong handle;
                    ulong photoHandle;
                    var now = DateTime.UtcNow;
                    SteamVrOverlayPanelState state;
                    SteamVrOverlayPanelState photoState;
                    var dropdownOpen = false;
                    var scrollIndex = 0;
                    var selectedIndex = 0;
                    var cursorU = 0.5f;
                    var cursorV = 0.5f;
                    var cursorValid = false;
                    var lastText = string.Empty;
                    var shouldRender = false;
                    var shouldRenderPhoto = false;
                    var drawCursorDotInPanel = false;
                    var settingsOpen = false;
                    var cursorDirty = false;
                    var cursorAppliedVisible = false;
                    var photoPanelOpen = false;
                    var panelLocked = false;
                    var unlockConfirmPending = false;
                    var loopActive = false;
                    var anchorMode = SteamVrOverlayAnchorMode.Palm;
                    var trackingDeviceMode = SteamVrTrackingDeviceMode.Auto;
                    var steamVrHand = 0;
                    var panelSize = 0.15f;
                    var startupAutoLaunch = false;
                    var panelText = SteamVrPanelText.Create(_config.UiLanguage);
                    var languageRoutes = new SteamVrLanguageRouteSnapshot();
                    var languagePickerTarget = SteamVrLanguagePickerTarget.None;
                    var languagePickerPageIndex = 0;
                    IReadOnlyList<SteamVrLanguagePickerOption> languagePickerOptions = Array.Empty<SteamVrLanguagePickerOption>();

                    lock (_lock)
                    {
                        if (_disposed || !IsConnected || _overlayHandle == 0 || !_desiredVisible)
                        {
                            shouldRender = false;
                            shouldRenderPhoto = false;
                            handle = 0;
                            photoHandle = 0;
                            state = _panelState;
                            photoState = BuildPhotoPanelStateSnapshotLocked();
                        }
                        else
                        {
                            handle = _overlayHandle;
                            photoHandle = _photoOverlayHandle;
                            state = _panelState;
                            photoState = BuildPhotoPanelStateSnapshotLocked();
                            dropdownOpen = _dropdownOpen;
                            scrollIndex = _scrollIndex;
                            selectedIndex = _selectedIndex;
                            cursorU = _cursorU;
                            cursorV = _cursorV;
                            cursorValid = _cursorValid;
                            lastText = _lastText;
                            drawCursorDotInPanel = ShouldDrawCursorInPanelLocked();
                            settingsOpen = _settingsOpen;
                            cursorDirty = _cursorDirty;
                            cursorAppliedVisible = _cursorAppliedVisible;
                            photoPanelOpen = !_photoPanelDismissed && (_photoPanelTranscripts?.Count ?? 0) > 0;
                            panelLocked = _panelLocked;
                            unlockConfirmPending = IsPanelUnlockConfirmPendingLocked(now);
                            loopActive = cursorValid || dropdownOpen || settingsOpen || cursorDirty || cursorAppliedVisible || _photoPanelDirty || _photoPanelAppliedVisible || unlockConfirmPending;
                            anchorMode = _config.SteamVrAnchorModeValue;
                            trackingDeviceMode = _config.SteamVrTrackingDeviceModeValue;
                            steamVrHand = _hand;
                            panelSize = _size;
                            startupAutoLaunch = _config.SteamVrAutoLaunch;
                            panelText = SteamVrPanelText.Create(FirstNonEmpty(_config.UiLanguage, state.UiLanguage));
                            languageRoutes = BuildLanguageRouteSnapshot(state, panelText);
                            languagePickerTarget = _languagePickerTarget;
                            languagePickerPageIndex = _languagePickerPageIndex;
                            languagePickerOptions = BuildLanguagePickerOptions(state, panelText, languagePickerTarget);

                            shouldRender = TryConsumePanelDirtyLocked(now);
                            shouldRenderPhoto = TryConsumePhotoPanelDirtyLocked(now);
                        }
                    }

                    try
                    {
                        if (handle != 0)
                        {
                            var overlay = Valve.VR.OpenVR.Overlay;
                            if (overlay != null)
                            {
                                if (TryRefreshPlacementForActiveTrackingDevice(now))
                                {
                                    loopActive = true;
                                    lock (_lock)
                                    {
                                        cursorDirty = _cursorDirty;
                                        cursorAppliedVisible = _cursorAppliedVisible;
                                    }
                                }

                                var activeInteraction = cursorValid || dropdownOpen || settingsOpen || photoPanelOpen;
                                if (TryMarkOverlayEventPoll(now, activeInteraction))
                                {
                                    // Drain pending events, but don't call into OpenVR every frame while idle.
                                    while (overlay.PollNextOverlayEvent(handle, ref evt, evtSize))
                                    {
                                        HandleOverlayEvent(evt);
                                    }

                                    lock (_lock)
                                    {
                                        cursorValid = _cursorValid;
                                        cursorDirty = _cursorDirty;
                                        cursorAppliedVisible = _cursorAppliedVisible;
                                    }

                                    activeInteraction = cursorValid || dropdownOpen || settingsOpen || photoPanelOpen;
                                    loopActive = loopActive || activeInteraction || cursorDirty || cursorAppliedVisible;
                                }

                                // If SteamVR isn't sending mouse events (common for palm-attached overlays),
                                // compute intersection ourselves so a cursor dot still appears when the user points.
                                var polledPointer = false;
                                if (TryMarkControllerPointerPoll(now, activeInteraction))
                                {
                                    TryUpdateCursorFromControllerRay(handle, overlay);

                                    lock (_lock)
                                    {
                                        cursorValid = _cursorValid;
                                        cursorDirty = _cursorDirty;
                                        cursorAppliedVisible = _cursorAppliedVisible;
                                    }

                                    activeInteraction = cursorValid || dropdownOpen || settingsOpen || photoPanelOpen;
                                    loopActive = loopActive || activeInteraction || cursorDirty || cursorAppliedVisible;

                                    // If the cursor is hitting the panel, treat trigger as click without making the overlay interactive.
                                    TryHandlePhotoPanelControllerClick(photoHandle, overlay);
                                    TryHandleControllerClick(handle);
                                    polledPointer = true;
                                }

                                // Apply cursor overlay transform (cheap) without re-uploading the big panel texture.
                                if (activeInteraction || cursorDirty || cursorAppliedVisible || polledPointer)
                                {
                                    ApplyCursorOverlayIfNeeded(handle, overlay);
                                }

                                ApplyPhotoPanelVisibility(overlay);
                                UpdatePhotoImageOverlay(overlay, photoState);
                            }
                        }
                    }
                    catch
                    {
                        // ignore; we'll keep polling
                    }

                    // HandleOverlayEvent may have set _panelDirty, so re-check without losing the flag.
                    if (!shouldRender)
                    {
                        now = DateTime.UtcNow;
                        lock (_lock)
                        {
                            if (!_disposed && IsConnected && _overlayHandle == handle && _desiredVisible && TryConsumePanelDirtyLocked(now))
                            {
                                shouldRender = true;

                                // refresh snapshot (cursor might be updated)
                                state = _panelState;
                                photoState = BuildPhotoPanelStateSnapshotLocked();
                                dropdownOpen = _dropdownOpen;
                                scrollIndex = _scrollIndex;
                                selectedIndex = _selectedIndex;
                                cursorU = _cursorU;
                                cursorV = _cursorV;
                                cursorValid = _cursorValid;
                                lastText = _lastText;
                                drawCursorDotInPanel = ShouldDrawCursorInPanelLocked();
                                settingsOpen = _settingsOpen;
                                cursorDirty = _cursorDirty;
                                cursorAppliedVisible = _cursorAppliedVisible;
                                photoPanelOpen = !_photoPanelDismissed && (_photoPanelTranscripts?.Count ?? 0) > 0;
                                panelLocked = _panelLocked;
                                unlockConfirmPending = IsPanelUnlockConfirmPendingLocked(now);
                                anchorMode = _config.SteamVrAnchorModeValue;
                                trackingDeviceMode = _config.SteamVrTrackingDeviceModeValue;
                                steamVrHand = _hand;
                                panelSize = _size;
                                startupAutoLaunch = _config.SteamVrAutoLaunch;
                                panelText = SteamVrPanelText.Create(FirstNonEmpty(_config.UiLanguage, state.UiLanguage));
                                languageRoutes = BuildLanguageRouteSnapshot(state, panelText);
                                languagePickerTarget = _languagePickerTarget;
                                languagePickerPageIndex = _languagePickerPageIndex;
                                languagePickerOptions = BuildLanguagePickerOptions(state, panelText, languagePickerTarget);
                            }
                        }
                    }

                    if (!shouldRenderPhoto)
                    {
                        now = DateTime.UtcNow;
                        lock (_lock)
                        {
                            if (!_disposed && IsConnected && _photoOverlayHandle == photoHandle && _desiredVisible && TryConsumePhotoPanelDirtyLocked(now))
                            {
                                shouldRenderPhoto = true;
                                photoState = BuildPhotoPanelStateSnapshotLocked();
                            }
                        }
                    }

                    if (handle != 0 && shouldRender)
                    {
                        try
                        {
                            var err = RenderAndUploadPanel(handle, state, dropdownOpen, scrollIndex, selectedIndex, drawCursorDotInPanel, cursorU, cursorV, cursorValid, lastText, settingsOpen, anchorMode, trackingDeviceMode, steamVrHand, panelSize, startupAutoLaunch, photoPanelOpen, panelLocked, unlockConfirmPending, panelText, languageRoutes, languagePickerTarget, languagePickerOptions, languagePickerPageIndex);
                            if (err == EVROverlayError.None)
                            {
                                lock (_lock)
                                {
                                    _lastPanelRenderUtc = DateTime.UtcNow;
                                }
                            }
                            if (err == EVROverlayError.InvalidHandle || err == EVROverlayError.UnknownOverlay)
                            {
                                RequestRestart($"Render failed: {err}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "SteamVR panel render failed");
                            RequestRestart($"Render exception: {ex.GetType().Name}");
                        }
                    }

                    if (photoHandle != 0 && shouldRenderPhoto)
                    {
                        try
                        {
                            var err = RenderAndUploadPhotoPanel(photoHandle, photoState);
                            if (err == EVROverlayError.None)
                            {
                                lock (_lock)
                                {
                                    _lastPhotoPanelRenderUtc = DateTime.UtcNow;
                                }

                                var overlay = Valve.VR.OpenVR.Overlay;
                                if (overlay != null)
                                {
                                    UpdatePhotoImageOverlay(overlay, photoState);
                                }
                            }
                            if (err == EVROverlayError.InvalidHandle || err == EVROverlayError.UnknownOverlay)
                            {
                                RequestRestart($"Photo render failed: {err}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "SteamVR photo panel render failed");
                            RequestRestart($"Photo render exception: {ex.GetType().Name}");
                        }
                    }

                    try
                    {
                        var delay = shouldRender || shouldRenderPhoto
                            ? EventLoopRenderInterval
                            : loopActive
                                ? EventLoopActiveInteractionInterval
                                : EventLoopIdleInterval;
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, token);
        }

        private bool TryMarkControllerPointerPoll(DateTime now, bool activeInteraction)
        {
            var interval = activeInteraction
                ? ActiveControllerPointerPollInterval
                : IdleControllerPointerPollInterval;

            lock (_lock)
            {
                if (now - _lastControllerPointerPollUtc < interval)
                {
                    return false;
                }

                _lastControllerPointerPollUtc = now;
                return true;
            }
        }

        private bool TryMarkPlacementRefresh(DateTime now)
        {
            lock (_lock)
            {
                if (now - _lastPlacementRefreshUtc < PlacementRefreshInterval)
                {
                    return false;
                }

                _lastPlacementRefreshUtc = now;
                return true;
            }
        }

        private bool TryRefreshPlacementForActiveTrackingDevice(DateTime now)
        {
            if (!TryMarkPlacementRefresh(now))
            {
                return false;
            }

            bool shouldRefresh;
            uint currentDeviceIndex;
            SteamVrTrackingDeviceMode trackingMode;
            lock (_lock)
            {
                currentDeviceIndex = _panelTrackedDeviceIndex;
                trackingMode = _config.SteamVrTrackingDeviceModeValue;
                shouldRefresh = !_disposed
                    && IsConnected
                    && _desiredVisible
                    && _overlayHandle != 0
                    && _config.SteamVrAnchorModeValue != SteamVrOverlayAnchorMode.Head
                    && (trackingMode == SteamVrTrackingDeviceMode.Auto
                        || currentDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid
                        || currentDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd);
            }

            if (shouldRefresh
                && trackingMode == SteamVrTrackingDeviceMode.Auto
                && currentDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid
                && currentDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd)
            {
                var system = Valve.VR.OpenVR.System;
                if (system != null && IsDeviceCurrentlyActiveForPlacement(system, currentDeviceIndex))
                {
                    return false;
                }
            }

            return shouldRefresh && ApplyPlacementInternal();
        }

        private static bool IsDeviceCurrentlyActiveForPlacement(CVRSystem system, uint deviceIndex)
        {
            var poses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
            return TryGetTrackedDevicePoseSnapshot(system, poses)
                && IsDeviceActiveForPlacement(system, deviceIndex, poses, hasPoseSnapshot: true);
        }

        private bool TryMarkOverlayEventPoll(DateTime now, bool activeInteraction)
        {
            var interval = activeInteraction
                ? ActiveOverlayEventPollInterval
                : IdleOverlayEventPollInterval;

            lock (_lock)
            {
                if (now - _lastOverlayEventPollUtc < interval)
                {
                    return false;
                }

                _lastOverlayEventPollUtc = now;
                return true;
            }
        }

        private void TryUpdateCursorFromControllerRay(ulong overlayHandle, CVROverlay overlay)
        {
            try
            {
                var system = Valve.VR.OpenVR.System;
                if (system == null) return;

                uint panelDeviceIndex;
                float panelWidthMeters;
                bool flipU, flipV;
                bool visible;
                uint primaryDeviceIndex;
                lock (_lock)
                {
                    panelDeviceIndex = _panelTrackedDeviceIndex;
                    panelWidthMeters = _panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size;
                    flipU = _config.SteamVrCursorFlipU;
                    flipV = _config.SteamVrCursorFlipV;
                    visible = _desiredVisible && _appliedVisible && _overlayHandle == overlayHandle;
                    primaryDeviceIndex = _cursorRayDeviceIndex;
                }
                if (!visible || panelDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return;

                var panelHeightMeters = panelWidthMeters * (PanelHeight / (float)PanelWidth);

                var deviceCandidates = GetPointerDeviceCandidates(system, primaryDeviceIndex);
                if (deviceCandidates.Count == 0) return;

                // Get latest poses.
                var poses = _trackedDevicePoses;
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

                uint hitDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                VROverlayIntersectionResults_t hitResult = default;
                hitResult.fDistance = float.MaxValue;

                foreach (var deviceIndex in deviceCandidates)
                {
                    if (deviceIndex >= poses.Length) continue;
                    var pose = poses[deviceIndex];
                    if (!pose.bPoseIsValid) continue;

                    var m = pose.mDeviceToAbsoluteTracking;
                    var origin = new HmdVector3_t { v0 = m.m3, v1 = m.m7, v2 = m.m11 };
                    // Controller forward is typically -Z in OpenVR.
                    var dir = new HmdVector3_t { v0 = -m.m2, v1 = -m.m6, v2 = -m.m10 };

                    var p = new VROverlayIntersectionParams_t
                    {
                        eOrigin = ETrackingUniverseOrigin.TrackingUniverseStanding,
                        vSource = origin,
                        vDirection = dir
                    };

                    var r = new VROverlayIntersectionResults_t();
                    if (!overlay.ComputeOverlayIntersection(overlayHandle, ref p, ref r))
                    {
                        continue;
                    }

                    if (!IsOverlayIntersectionHitFrontFacing(p, r))
                    {
                        continue;
                    }

                    if (r.fDistance >= hitResult.fDistance)
                    {
                        continue;
                    }

                    hitDeviceIndex = deviceIndex;
                    hitResult = r;
                }

                if (hitDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    lock (_lock)
                    {
                        if (_disposed) return;

                        // If we haven't had any cursor updates in a while, hide the dot.
                        if (_cursorValid && (DateTime.UtcNow - _lastCursorUpdateUtc) > CursorLostGraceInterval)
                        {
                            _cursorValid = false;
                            _cursorDirty = true;
                            MarkCursorPanelDirtyIfNeededLocked();
                        }
                        return;
                    }
                }

                var panelPoint = OverlayCoordinatesToPanelPixels(hitResult.vUVs.v0, hitResult.vUVs.v1, flipU, flipV, preferPixels: false);
                var localX = (panelPoint.U - 0.5f) * panelWidthMeters;
                var localY = (0.5f - panelPoint.V) * panelHeightMeters;
                var localZ = 0f;

                lock (_lock)
                {
                    if (_disposed) return;

                    var rayDistance = Math.Clamp(hitResult.fDistance, CursorLaserMinLengthMeters, CursorLaserMaxLengthMeters);

                    // Keep raw hit data for diagnostics; use OpenVR overlay coordinates for UI mapping.
                    _cursorPointX = hitResult.vPoint.v0;
                    _cursorPointY = hitResult.vPoint.v1;
                    _cursorPointZ = hitResult.vPoint.v2;
                    _cursorNormalX = hitResult.vNormal.v0;
                    _cursorNormalY = hitResult.vNormal.v1;
                    _cursorNormalZ = hitResult.vNormal.v2;

                    _cursorLocalX = localX;
                    _cursorLocalY = localY;
                    _cursorLocalZ = localZ;

                    var changed = !_cursorValid
                        || _cursorRayDeviceIndex != hitDeviceIndex
                        || Math.Abs(rayDistance - _cursorRayDistanceMeters) > 0.008f
                        || Math.Abs(panelPoint.U - _cursorU) > 0.0015f
                        || Math.Abs(panelPoint.V - _cursorV) > 0.0015f;
                    if (changed)
                    {
                        _cursorRayDeviceIndex = hitDeviceIndex;
                        _cursorRayDistanceMeters = rayDistance;
                        _cursorU = panelPoint.U;
                        _cursorV = panelPoint.V;
                        _cursorValid = true;
                        _lastCursorUpdateUtc = DateTime.UtcNow;
                        _cursorDirty = true;
                        MarkCursorPanelDirtyIfNeededLocked();
                    }
                }
            }
            catch
            {
                // ignore; best-effort cursor
            }
        }

        private static bool IsOverlayIntersectionHitFrontFacing(
            in VROverlayIntersectionParams_t parameters,
            in VROverlayIntersectionResults_t results)
        {
            var toSourceX = parameters.vSource.v0 - results.vPoint.v0;
            var toSourceY = parameters.vSource.v1 - results.vPoint.v1;
            var toSourceZ = parameters.vSource.v2 - results.vPoint.v2;

            var normalX = results.vNormal.v0;
            var normalY = results.vNormal.v1;
            var normalZ = results.vNormal.v2;
            var normalLength = MathF.Sqrt((normalX * normalX) + (normalY * normalY) + (normalZ * normalZ));
            if (normalLength <= 0.000001f)
            {
                return false;
            }

            var dot = ((normalX / normalLength) * toSourceX)
                + ((normalY / normalLength) * toSourceY)
                + ((normalZ / normalLength) * toSourceZ);
            return dot >= 0f;
        }

        private void TryHandleControllerClick(ulong overlayHandle)
        {
            try
            {
                var system = Valve.VR.OpenVR.System;
                if (system == null) return;

                // Only react when we're actually pointing at the panel.
                float cursorU, cursorV;
                bool valid;
                lock (_lock)
                {
                    if (_disposed || !IsConnected || !_desiredVisible || _overlayHandle != overlayHandle) return;
                    valid = _cursorValid;
                    cursorU = _cursorU;
                    cursorV = _cursorV;
                }
                if (!valid) return;

                uint primaryDeviceIndex;
                lock (_lock) primaryDeviceIndex = _cursorRayDeviceIndex;

                var pressed = false;
                uint pressedDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                var deviceCandidates = GetPointerDeviceCandidates(system, primaryDeviceIndex);
                for (var i = 0; i < deviceCandidates.Count; i++)
                {
                    var state = new VRControllerState_t();
                    var ok = system.GetControllerState(deviceCandidates[i], ref state, VrControllerStateSize);
                    if (!ok) continue;

                    if (!IsControllerTriggerPressed(state))
                    {
                        continue;
                    }

                    pressed = true;
                    pressedDeviceIndex = deviceCandidates[i];
                    break;
                }

                var doClick = false;
                var px = cursorU * PanelWidth;
                var py = cursorV * PanelHeight;
                lock (_lock)
                {
                    if (pressed && !_lastTriggerPressed)
                    {
                        doClick = true;
                    }

                    _lastTriggerPressed = pressed;
                }

                if (doClick)
                {
                    _logger.Debug(
                        "SteamVR panel trigger click accepted. deviceIndex={DeviceIndex}, x={X:0.0}, y={Y:0.0}, u={U:0.000}, v={V:0.000}",
                        pressedDeviceIndex,
                        px,
                        py,
                        cursorU,
                        cursorV);
                    HandlePanelClick(px, py);
                }
            }
            catch
            {
                // ignore; best-effort
            }
        }

        private void TryHandlePhotoPanelControllerClick(ulong photoOverlayHandle, CVROverlay overlay)
        {
            try
            {
                if (photoOverlayHandle == 0)
                {
                    return;
                }

                var system = Valve.VR.OpenVR.System;
                if (system == null) return;

                bool visible;
                bool flipU;
                bool flipV;
                uint primaryDeviceIndex;
                lock (_lock)
                {
                    if (_disposed || !IsConnected || !_desiredVisible || _photoOverlayHandle != photoOverlayHandle)
                    {
                        return;
                    }

                    visible = !_panelLocked
                        && _photoPanelAppliedVisible
                        && !_photoPanelDismissed
                        && (_photoPanelTranscripts?.Count ?? 0) > 0;
                    flipU = _config.SteamVrCursorFlipU;
                    flipV = _config.SteamVrCursorFlipV;
                    primaryDeviceIndex = _cursorRayDeviceIndex;
                }

                if (!visible)
                {
                    return;
                }

                var deviceCandidates = GetPointerDeviceCandidates(system, primaryDeviceIndex);
                if (deviceCandidates.Count == 0) return;

                var poses = _trackedDevicePoses;
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

                uint hitDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                VROverlayIntersectionResults_t hitResult = default;
                hitResult.fDistance = float.MaxValue;

                foreach (var deviceIndex in deviceCandidates)
                {
                    if (deviceIndex >= poses.Length) continue;
                    var pose = poses[deviceIndex];
                    if (!pose.bPoseIsValid) continue;

                    var m = pose.mDeviceToAbsoluteTracking;
                    var origin = new HmdVector3_t { v0 = m.m3, v1 = m.m7, v2 = m.m11 };
                    var dir = new HmdVector3_t { v0 = -m.m2, v1 = -m.m6, v2 = -m.m10 };
                    var parameters = new VROverlayIntersectionParams_t
                    {
                        eOrigin = ETrackingUniverseOrigin.TrackingUniverseStanding,
                        vSource = origin,
                        vDirection = dir
                    };
                    var result = new VROverlayIntersectionResults_t();
                    if (!overlay.ComputeOverlayIntersection(photoOverlayHandle, ref parameters, ref result))
                    {
                        continue;
                    }
                    if (!IsOverlayIntersectionHitFrontFacing(parameters, result))
                    {
                        continue;
                    }
                    if (result.fDistance >= hitResult.fDistance)
                    {
                        continue;
                    }

                    hitDeviceIndex = deviceIndex;
                    hitResult = result;
                }

                if (hitDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    lock (_lock)
                    {
                        _photoPanelTextDragging = false;
                        _photoPanelTextDragPageIndex = -1;
                    }
                    return;
                }

                var point = OverlayCoordinatesToPhotoPanelPixels(hitResult.vUVs.v0, hitResult.vUVs.v1, flipU, flipV, preferPixels: false);
                var pressed = false;
                foreach (var deviceIndex in deviceCandidates)
                {
                    var state = new VRControllerState_t();
                    var ok = system.GetControllerState(deviceIndex, ref state, VrControllerStateSize);
                    if (ok && IsControllerTriggerPressed(state))
                    {
                        pressed = true;
                        break;
                    }
                }

                var triggerStarted = false;
                lock (_lock)
                {
                    if (pressed && !_lastTriggerPressed)
                    {
                        triggerStarted = true;
                    }
                    else if (!pressed)
                    {
                        _photoPanelTextDragging = false;
                        _photoPanelTextDragPageIndex = -1;
                    }

                    _lastTriggerPressed = pressed;
                }

                if (!pressed)
                {
                    return;
                }

                if (triggerStarted)
                {
                    _logger.Debug(
                        "SteamVR photo panel trigger accepted. deviceIndex={DeviceIndex}, x={X:0.0}, y={Y:0.0}, u={U:0.000}, v={V:0.000}",
                        hitDeviceIndex,
                        point.PanelX,
                        point.PanelY,
                        point.U,
                        point.V);
                    HandlePhotoPanelTriggerStart(point.PanelX, point.PanelY);
                }
                else
                {
                    HandlePhotoPanelTriggerDrag(point.PanelX, point.PanelY);
                }
            }
            catch
            {
                // ignore; best-effort
            }
        }

        private void HandlePhotoPanelTriggerStart(float x, float y)
        {
            var contentRect = GetPhotoPanelContentRect();
            var navigation = GetPhotoPanelNavigationLayout(contentRect);

            if (navigation.LeftButton.Contains(x, y) || navigation.RightButton.Contains(x, y))
            {
                lock (_lock)
                {
                    _photoPanelTextDragging = false;
                    _photoPanelTextDragPageIndex = -1;
                }
                HandlePhotoPanelClick(x, y);
                return;
            }

            lock (_lock)
            {
                if (_disposed || _panelLocked)
                {
                    return;
                }

                if (!navigation.PageRect.Contains(x, y) || _photoPanelTranscripts.Count <= 0)
                {
                    _photoPanelTextDragging = false;
                    _photoPanelTextDragPageIndex = -1;
                    return;
                }

                _photoPanelPageIndex = Math.Clamp(_photoPanelPageIndex, 0, _photoPanelTranscripts.Count - 1);
                _photoPanelTextDragging = true;
                _photoPanelTextDragLastY = y;
                _photoPanelTextDragPageIndex = _photoPanelPageIndex;
            }
        }

        private void HandlePhotoPanelTriggerDrag(float x, float y)
        {
            float deltaY;
            lock (_lock)
            {
                if (_disposed || _panelLocked || !_photoPanelTextDragging)
                {
                    return;
                }

                if (_photoPanelTextDragPageIndex != _photoPanelPageIndex)
                {
                    ResetPhotoPanelTextScrollLocked();
                    return;
                }

                deltaY = y - _photoPanelTextDragLastY;
                _photoPanelTextDragLastY = y;
            }

            if (Math.Abs(deltaY) < 0.5f)
            {
                return;
            }

            ApplyPhotoPanelTextScrollDelta(deltaY);
        }

        private void ApplyPhotoPanelTextScrollDelta(float deltaY)
        {
            var scrollDelta = -(int)Math.Round(deltaY);
            if (scrollDelta == 0)
            {
                return;
            }

            SteamVrOverlayTranscript? item;
            float fontScale;
            int pageIndex;
            int requestedOffset;
            lock (_lock)
            {
                if (_disposed || _panelLocked || !_photoPanelTextDragging || _photoPanelTranscripts.Count <= 0)
                {
                    return;
                }

                _photoPanelPageIndex = Math.Clamp(_photoPanelPageIndex, 0, _photoPanelTranscripts.Count - 1);
                pageIndex = _photoPanelPageIndex;
                item = _photoPanelTranscripts[pageIndex];
                fontScale = _panelState.FontScale;
                requestedOffset = _photoPanelTextScrollOffset + scrollDelta;
            }

            var maxScroll = item == null
                ? 0
                : ComputePhotoPanelTextMaxScroll(item, fontScale);

            lock (_lock)
            {
                if (_disposed || _panelLocked || pageIndex != _photoPanelPageIndex)
                {
                    ResetPhotoPanelTextScrollLocked();
                    return;
                }

                var nextOffset = Math.Clamp(requestedOffset, 0, maxScroll);
                if (nextOffset != _photoPanelTextScrollOffset)
                {
                    _photoPanelTextScrollOffset = nextOffset;
                    _photoPanelDirty = true;
                }
            }
        }

        private void HandlePhotoPanelClick(float x, float y)
        {
            var contentRect = GetPhotoPanelContentRect();
            var navigation = GetPhotoPanelNavigationLayout(contentRect);

            lock (_lock)
            {
                if (_disposed || _panelLocked)
                {
                    return;
                }

                var count = _photoPanelTranscripts?.Count ?? 0;
                if (count <= 0)
                {
                    _photoPanelPageIndex = 0;
                    ResetPhotoPanelTextScrollLocked();
                    return;
                }

                _photoPanelPageIndex = Math.Clamp(_photoPanelPageIndex, 0, count - 1);
                if (navigation.LeftButton.Contains(x, y) && count > 1)
                {
                    _photoPanelPageIndex = (_photoPanelPageIndex + 1) % count;
                    ResetPhotoPanelTextScrollLocked();
                    MarkPhotoPanelDirtyForInteractionLocked();
                }
                else if (navigation.RightButton.Contains(x, y) && count > 1)
                {
                    _photoPanelPageIndex = (_photoPanelPageIndex + count - 1) % count;
                    ResetPhotoPanelTextScrollLocked();
                    MarkPhotoPanelDirtyForInteractionLocked();
                }
            }
        }

        private static bool IsControllerTriggerPressed(in VRControllerState_t state)
        {
            var triggerMask = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
            if ((state.ulButtonPressed & triggerMask) != 0)
            {
                return true;
            }

            // Some runtimes expose the trigger only as an analog axis in legacy OpenVR state.
            return state.rAxis1.x >= 0.55f;
        }

        private void HandleOverlayEvent(VREvent_t evt)
        {
            try
            {
                var type = (EVREventType)evt.eventType;
                switch (type)
                {
                    case EVREventType.VREvent_MouseMove:
                    {
                        lock (_lock)
                        {
                            if (_cursorOverlayUsesNativeCursor)
                            {
                                break;
                            }
                        }

                        var point = OverlayCoordinatesToPanelPixels(evt.data.mouse.x, evt.data.mouse.y);
                        lock (_lock)
                        {
                            _cursorU = point.U;
                            _cursorV = point.V;
                            _cursorLocalX = (point.U - 0.5f) * (_panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size);
                            _cursorLocalY = (0.5f - point.V) * ((_panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size) * (PanelHeight / (float)PanelWidth));
                            _cursorLocalZ = 0f;
                            _cursorValid = true;
                            _lastCursorUpdateUtc = DateTime.UtcNow;
                            _cursorDirty = true;
                            MarkCursorPanelDirtyIfNeededLocked();
                        }
                        break;
                    }
                    case EVREventType.VREvent_MouseButtonDown:
                    {
                        lock (_lock)
                        {
                            if (_cursorOverlayUsesNativeCursor)
                            {
                                break;
                            }
                        }

                        var point = OverlayCoordinatesToPanelPixels(evt.data.mouse.x, evt.data.mouse.y);
                        HandlePanelClick(point.PanelX, point.PanelY);
                        break;
                    }
                    case EVREventType.VREvent_TouchPadMove:
                    {
                        // Trackpad movement can be used as a "cursor dot" controller without relying on a laser pointer.
                        var x = evt.data.touchPadMove.fValueXRaw;
                        var y = evt.data.touchPadMove.fValueYRaw;
                        var (u, v) = NormalizeTrackpadUv(x, y);
                        lock (_lock)
                        {
                            _cursorU = u;
                            _cursorV = v;
                            _cursorValid = true;
                            _lastCursorUpdateUtc = DateTime.UtcNow;
                            _cursorDirty = true;
                            MarkCursorPanelDirtyIfNeededLocked();
                        }
                        break;
                    }
                    case EVREventType.VREvent_ButtonPress:
                    {
                        // Trigger clicks are handled by controller-state polling so we can require
                        // an actual panel hit and avoid duplicate/stale-cursor clicks when SteamVR UI opens.
                        break;
                    }
                    case EVREventType.VREvent_ScrollDiscrete:
                    case EVREventType.VREvent_ScrollSmooth:
                    {
                        var delta = evt.data.scroll.ydelta;
                        if (Math.Abs(delta) > 0.0001f)
                        {
                            lock (_lock)
                            {
                                if (_panelLocked)
                                {
                                    break;
                                }

                                if (_dropdownOpen)
                                {
                                    var count = _panelState.Transcripts?.Count ?? 0;
                                    const int visible = 10;
                                    var max = Math.Max(0, count - visible);
                                    _scrollIndex = Math.Clamp(_scrollIndex + (delta > 0 ? -1 : 1), 0, max);
                                }
                                else
                                {
                                    // Scroll the transcript list (one row). Clamp happens during render based on visible rows.
                                    _scrollIndex = Math.Max(0, _scrollIndex + (delta > 0 ? -1 : 1));
                                }
                                _panelDirty = true;
                            }
                        }
                        break;
                    }
                    default:
                        break;
                }
            }
            catch
            {
                // ignore
            }
        }

        private (float PanelX, float PanelY, float U, float V) OverlayCoordinatesToPanelPixels(float x, float y)
        {
            bool flipU, flipV;
            lock (_lock)
            {
                flipU = _config.SteamVrCursorFlipU;
                flipV = _config.SteamVrCursorFlipV;
            }

            return OverlayCoordinatesToPanelPixels(x, y, flipU, flipV, preferPixels: false);
        }

        private static (float PanelX, float PanelY, float U, float V) OverlayCoordinatesToPanelPixels(float x, float y, bool flipU, bool flipV, bool preferPixels = true)
        {
            return OverlayCoordinatesToPixels(x, y, PanelWidth, PanelHeight, flipU, flipV, preferPixels);
        }

        private static (float PanelX, float PanelY, float U, float V) OverlayCoordinatesToPhotoPanelPixels(float x, float y, bool flipU, bool flipV, bool preferPixels = true)
        {
            return OverlayCoordinatesToPixels(x, y, PhotoPanelWidth, PhotoPanelHeight, flipU, flipV, preferPixels);
        }

        private static (float PanelX, float PanelY, float U, float V) OverlayCoordinatesToPixels(float x, float y, int width, int height, bool flipU, bool flipV, bool preferPixels = true)
        {
            // OpenVR overlay coordinates use GL space: origin at lower-left. With mouse scale set,
            // they are panel pixels; older/default paths may still return normalized coordinates.
            float overlayX;
            float overlayYFromBottom;

            if (!preferPixels && x >= -1f && x <= 1f && y >= -1f && y <= 1f)
            {
                var normalizedX = x < 0f ? (x + 1f) * 0.5f : x;
                var normalizedY = y < 0f ? (y + 1f) * 0.5f : y;
                overlayX = Math.Clamp(normalizedX, 0f, 1f) * width;
                overlayYFromBottom = Math.Clamp(normalizedY, 0f, 1f) * height;
            }
            else
            {
                overlayX = Math.Clamp(x, 0f, width);
                overlayYFromBottom = Math.Clamp(y, 0f, height);
            }

            if (flipU) overlayX = width - overlayX;
            if (flipV) overlayYFromBottom = height - overlayYFromBottom;

            var panelX = overlayX;
            var panelY = height - overlayYFromBottom;
            var u = Math.Clamp(panelX / width, 0f, 1f);
            var v = Math.Clamp(panelY / height, 0f, 1f);
            return (panelX, panelY, u, v);
        }

        private (float u, float v) NormalizeTrackpadUv(float x, float y)
        {
            var u = x < 0f || x > 1f ? (x + 1f) * 0.5f : x;
            var v = y < 0f || y > 1f ? (y + 1f) * 0.5f : y;
            return ApplyUvTransform(Math.Clamp(u, 0f, 1f), Math.Clamp(v, 0f, 1f));
        }

        private (float u, float v) ApplyUvTransform(float u, float v)
        {
            // Allow user calibration. Different devices / poses can appear mirrored depending on how the quad is oriented.
            if (_config.SteamVrCursorFlipU) u = 1f - u;
            if (_config.SteamVrCursorFlipV) v = 1f - v;
            return (u, v);
        }

        private static HmdMatrix34_t Multiply(in HmdMatrix34_t a, in HmdMatrix34_t b)
        {
            // 3x4 affine matrix multiply (OpenVR style):
            // [R_a t_a] * [R_b t_b] = [R_a*R_b, R_a*t_b + t_a]
            var c = new HmdMatrix34_t
            {
                m0 = a.m0 * b.m0 + a.m1 * b.m4 + a.m2 * b.m8,
                m1 = a.m0 * b.m1 + a.m1 * b.m5 + a.m2 * b.m9,
                m2 = a.m0 * b.m2 + a.m1 * b.m6 + a.m2 * b.m10,

                m4 = a.m4 * b.m0 + a.m5 * b.m4 + a.m6 * b.m8,
                m5 = a.m4 * b.m1 + a.m5 * b.m5 + a.m6 * b.m9,
                m6 = a.m4 * b.m2 + a.m5 * b.m6 + a.m6 * b.m10,

                m8 = a.m8 * b.m0 + a.m9 * b.m4 + a.m10 * b.m8,
                m9 = a.m8 * b.m1 + a.m9 * b.m5 + a.m10 * b.m9,
                m10 = a.m8 * b.m2 + a.m9 * b.m6 + a.m10 * b.m10,
            };

            c.m3 = a.m0 * b.m3 + a.m1 * b.m7 + a.m2 * b.m11 + a.m3;
            c.m7 = a.m4 * b.m3 + a.m5 * b.m7 + a.m6 * b.m11 + a.m7;
            c.m11 = a.m8 * b.m3 + a.m9 * b.m7 + a.m10 * b.m11 + a.m11;

            return c;
        }

        private void HandlePanelClick(float x, float y)
        {
            if (ShouldSuppressPanelClick(x, y))
            {
                return;
            }

            var header = BuildPanelHeaderLayout();
            var headerY = header.Mic.Y;
            var headerH = header.Mic.Height;
            var micRect = header.Mic;
            var deskRect = header.Desktop;
            var photoRect = header.Photo;
            var dropRect = header.History;
            var lockRect = header.LockButton;
            var gearRect = header.Settings;

            var listY = headerY + headerH + 18f;
            var listH = PanelHeight - listY - 16f;
            var listRect = new RectangleF(16, listY, PanelWidth - 32, listH);

            SteamVrOverlayActionType? actionToRaise = null;
            SteamVrOverlayTranscript? transcriptToCopy = null;
            string? languageCodeToSelect = null;
            var actionResolvedInSettings = false;

            lock (_lock)
            {
                if (_disposed) return;

                var now = DateTime.UtcNow;
                var unlockConfirmPending = IsPanelUnlockConfirmPendingLocked(now);
                if (lockRect.Contains(x, y))
                {
                    if (!_panelLocked)
                    {
                        _panelLocked = true;
                        _panelUnlockConfirmUntilUtc = DateTime.MinValue;
                        _dropdownOpen = false;
                        CloseLanguagePickerLocked();
                    }
                    else if (unlockConfirmPending)
                    {
                        _panelLocked = false;
                        _panelUnlockConfirmUntilUtc = DateTime.MinValue;
                    }
                    else
                    {
                        _panelUnlockConfirmUntilUtc = now + PanelUnlockConfirmInterval;
                    }

                    _panelDirty = true;
                    return;
                }

                if (_panelLocked)
                {
                    return;
                }

                if (gearRect.Contains(x, y))
                {
                    _settingsOpen = !_settingsOpen;
                    _dropdownOpen = false;
                    CloseLanguagePickerLocked();
                    _panelDirty = true;
                    return;
                }
                else if (micRect.Contains(x, y))
                {
                    actionToRaise = SteamVrOverlayActionType.ToggleMic;
                    _panelDirty = true;
                }
                else if (deskRect.Contains(x, y))
                {
                    actionToRaise = SteamVrOverlayActionType.ToggleDesktop;
                    _panelDirty = true;
                }
                else if (photoRect.Contains(x, y))
                {
                    if (!_photoPanelDismissed && (_photoPanelTranscripts?.Count ?? 0) > 0)
                    {
                        _photoPanelDismissed = true;
                        actionToRaise = SteamVrOverlayActionType.ClosePhotoTranslation;
                        _photoPanelDirty = true;
                        _panelDirty = true;
                    }
                    else if (_panelState.IsPhotoTranslationProcessing)
                    {
                        if (!_photoPanelDismissed)
                        {
                            _photoPanelDismissed = true;
                            _photoPanelDirty = true;
                        }
                        actionToRaise = SteamVrOverlayActionType.ClosePhotoTranslation;
                        _panelDirty = true;
                    }
                    else if (_panelState.IsPhotoTranslationArmed)
                    {
                        actionToRaise = SteamVrOverlayActionType.CancelPhotoTranslation;
                        _panelDirty = true;
                    }
                    else
                    {
                        actionToRaise = SteamVrOverlayActionType.RequestPhotoTranslation;
                        _panelDirty = true;
                    }
                }
                else if (dropRect.Contains(x, y) && !_settingsOpen)
                {
                    _dropdownOpen = !_dropdownOpen;
                    _panelDirty = true;
                    return;
                }
                if (_settingsOpen)
                {
                    var settingsRect = new RectangleF(16, listY, PanelWidth - 48, PanelHeight - listY - 16);

                    if (TryHandleLanguagePickerClickLocked(settingsRect, x, y, out var pickerAction, out var pickerLanguageCode))
                    {
                        actionToRaise = pickerAction;
                        languageCodeToSelect = pickerLanguageCode;
                        actionResolvedInSettings = true;
                    }
                    else
                    {
                        var layout = BuildSettingsPanelLayout(settingsRect);

                        if (layout.AnchorPalm.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetAnchorPalm;
                            _panelDirty = true;
                        }
                        else if (layout.AnchorBack.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetAnchorBackOfHand;
                            _panelDirty = true;
                        }
                        else if (layout.AnchorHead.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetAnchorHead;
                            _panelDirty = true;
                        }
                        else if (layout.HandRight.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetHandRight;
                            _panelDirty = true;
                        }
                        else if (layout.HandLeft.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetHandLeft;
                            _panelDirty = true;
                        }
                        else if (layout.TrackingAuto.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetTrackingDeviceAuto;
                            _panelDirty = true;
                        }
                        else if (layout.TrackingController.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetTrackingDeviceController;
                            _panelDirty = true;
                        }
                        else if (layout.TrackingHand.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.SetTrackingDeviceHandTracking;
                            _panelDirty = true;
                        }
                        else if (layout.Startup.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleSteamVrAutoLaunch;
                            _panelDirty = true;
                        }
                        else if (layout.SizeMinus.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.DecreaseOverlaySize;
                            _panelDirty = true;
                        }
                        else if (layout.SizePlus.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.IncreaseOverlaySize;
                            _panelDirty = true;
                        }
                        else if (layout.FontMinus.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.DecreasePanelFontSize;
                            _panelDirty = true;
                        }
                        else if (layout.FontPlus.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.IncreasePanelFontSize;
                            _panelDirty = true;
                        }
                        else if (layout.Reset.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ResetOverlayOffset;
                            _panelDirty = true;
                        }
                        else if (layout.TtsEnabled.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleTts;
                            _panelDirty = true;
                        }
                        else if (layout.TtsPassthrough.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleTtsMicPassthrough;
                            _panelDirty = true;
                        }
                        else if (layout.TtsPassVolumeMinus.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.DecreaseTtsMicPassthroughVolume;
                            _panelDirty = true;
                        }
                        else if (layout.TtsPassVolumePlus.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.IncreaseTtsMicPassthroughVolume;
                            _panelDirty = true;
                        }
                        else if (layout.MicTtsOriginal.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleMicTtsOriginal;
                            _panelDirty = true;
                        }
                        else if (layout.MicTtsTranslation1.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleMicTtsTranslation1;
                            _panelDirty = true;
                        }
                        else if (layout.MicTtsTranslation2.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleMicTtsTranslation2;
                            _panelDirty = true;
                        }
                        else if (layout.MicTtsTranslation3.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleMicTtsTranslation3;
                            _panelDirty = true;
                        }
                        else if (layout.DesktopTtsOriginal.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleDesktopTtsOriginal;
                            _panelDirty = true;
                        }
                        else if (layout.DesktopTtsTranslation1.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleDesktopTtsTranslation1;
                            _panelDirty = true;
                        }
                        else if (layout.DesktopTtsTranslation2.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleDesktopTtsTranslation2;
                            _panelDirty = true;
                        }
                        else if (layout.DesktopTtsTranslation3.Contains(x, y))
                        {
                            actionToRaise = SteamVrOverlayActionType.ToggleDesktopTtsTranslation3;
                            _panelDirty = true;
                        }
                        else if (layout.MicSource.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.MicSource);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.MicTarget1.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.MicTarget1);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.MicTarget2.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.MicTarget2);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.MicTarget3.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.MicTarget3);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.DesktopSource.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.DesktopSource);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.DesktopTarget1.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.DesktopTarget1);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.DesktopTarget2.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.DesktopTarget2);
                            actionResolvedInSettings = true;
                        }
                        else if (layout.DesktopTarget3.Contains(x, y))
                        {
                            OpenLanguagePickerLocked(SteamVrLanguagePickerTarget.DesktopTarget3);
                            actionResolvedInSettings = true;
                        }

                        if (actionToRaise.HasValue)
                        {
                            actionResolvedInSettings = true;
                        }
                        if (!actionResolvedInSettings && !actionToRaise.HasValue)
                        {
                            return;
                        }
                    }
                }

                // Dropdown list (history)
                if (_dropdownOpen && !actionResolvedInSettings)
                {
                    var items = _panelState.Transcripts ?? Array.Empty<SteamVrOverlayTranscript>();
                    const int maxDropItems = 10;
                    var visible = Math.Min(maxDropItems, items.Count);
                    var start = Math.Clamp(_scrollIndex, 0, Math.Max(0, items.Count - visible));
                    var dropListRect = new RectangleF(dropRect.X, dropRect.Bottom + 6, dropRect.Width, visible * 34 + 8);
                    if (dropListRect.Contains(x, y))
                    {
                        var localY = y - (dropRect.Bottom + 6 + 4);
                        var idxInDrop = (int)(localY / 34f);
                        if (idxInDrop >= 0 && idxInDrop < visible)
                        {
                            _selectedIndex = start + idxInDrop;
                            _dropdownOpen = false;
                            _panelDirty = true;
                        }
                        return;
                    }
                }

                // Page buttons + list selection.
                var items2 = _panelState.Transcripts ?? Array.Empty<SteamVrOverlayTranscript>();
                if (!actionResolvedInSettings && items2.Count > 0)
                {
                    const float btnW = 36f;
                    const float btnH = 36f;
                    var btnX = listRect.Right - btnW - 10;
                    var btnTop = new RectangleF(btnX, listRect.Y + 12, btnW, btnH);
                    var btnUp = new RectangleF(btnX, btnTop.Bottom + 10, btnW, btnH);
                    var btnDown = new RectangleF(btnX, btnUp.Bottom + 10, btnW, btnH);

                    var contentRect = new RectangleF(listRect.X + 10, listRect.Y + 10, listRect.Width - 20 - (btnW + 12), listRect.Height - 20);
                    var languageMask = _panelState.LanguageMask == TranscriptLanguageMask.None
                        ? (TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1)
                        : _panelState.LanguageMask;

                    using var hitTestBmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
                    using var hitTestGraphics = Graphics.FromImage(hitTestBmp);
                    hitTestGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    using var transcriptFont = new Font(
                        "Microsoft YaHei UI",
                        BaseTranscriptFontSize * Math.Clamp(_panelState.FontScale, 0.70f, 1.40f),
                        FontStyle.Bold,
                        GraphicsUnit.Pixel);

                    var maxScroll = Math.Max(0, items2.Count - 1);
                    _scrollIndex = Math.Clamp(_scrollIndex, 0, maxScroll);
                    var micTextColor = ParsePanelTextColor(_panelState.MicTextColor, AppConfig.DefaultSteamVrMicTextColor);
                    var desktopTextColor = ParsePanelTextColor(_panelState.DesktopTextColor, AppConfig.DefaultSteamVrDesktopTextColor);
                    var rows = BuildVisibleTranscriptRows(hitTestGraphics, items2, _scrollIndex, contentRect, languageMask, transcriptFont, 4f, micTextColor, desktopTextColor);
                    var visibleCount = Math.Max(1, rows.Count);

                    var atTop = _scrollIndex <= 0;
                    var atBottom = rows.Count == 0 || rows[rows.Count - 1].Index >= items2.Count - 1;

                    // "Top" only applies when not at top.
                    if (!atTop && btnTop.Contains(x, y))
                    {
                        _scrollIndex = 0;
                        _panelDirty = true;
                        return;
                    }
                    if (btnUp.Contains(x, y))
                    {
                        if (!atTop)
                        {
                            _scrollIndex = Math.Max(0, _scrollIndex - visibleCount);
                            _panelDirty = true;
                        }
                        return;
                    }
                    if (btnDown.Contains(x, y))
                    {
                        if (!atBottom)
                        {
                            var nextIndex = rows.Count == 0 ? _scrollIndex + visibleCount : rows[rows.Count - 1].Index + 1;
                            _scrollIndex = Math.Min(maxScroll, nextIndex);
                            _panelDirty = true;
                        }
                        return;
                    }

                    var scrollbarRect = new RectangleF(contentRect.Right + 2, contentRect.Y, 8, contentRect.Height);
                    if (scrollbarRect.Contains(x, y))
                    {
                        if (maxScroll > 0)
                        {
                            var thumbH = Math.Max(24f, scrollbarRect.Height * visibleCount / items2.Count);
                            var travel = scrollbarRect.Height - thumbH;
                            var target = Math.Clamp(y - scrollbarRect.Y - thumbH * 0.5f, 0f, travel);
                            var ratio = travel <= 0 ? 0f : (target / travel);
                            _scrollIndex = Math.Clamp((int)Math.Round(ratio * maxScroll), 0, maxScroll);
                            _panelDirty = true;
                        }
                        return;
                    }

                    // Select item by clicking its row.
                    if (contentRect.Contains(x, y))
                    {
                        for (var i = 0; i < rows.Count; i++)
                        {
                            var row = rows[i];
                            if (!row.RowRect.Contains(x, y))
                            {
                                continue;
                            }

                            var idx = row.Index;
                            if (idx >= 0 && idx < items2.Count)
                            {
                                _selectedIndex = idx;
                                transcriptToCopy = items2[idx];
                                actionToRaise = SteamVrOverlayActionType.CopyTranscript;
                                _panelDirty = true;
                            }
                            break;
                        }
                    }
                }
            }

            // Important: invoke callbacks outside the lock to avoid deadlocks with UI thread -> UpdatePanel().
            if (actionToRaise.HasValue)
            {
                try
                {
                    ActionRequested?.Invoke(this, new SteamVrOverlayAction
                    {
                        Type = actionToRaise.Value,
                        Transcript = transcriptToCopy,
                        LanguageCode = languageCodeToSelect ?? string.Empty
                    });
                }
                catch { }
            }
        }

        private void OpenLanguagePickerLocked(SteamVrLanguagePickerTarget target)
        {
            _languagePickerTarget = target;
            _languagePickerPageIndex = 0;
            _panelDirty = true;
        }

        private void CloseLanguagePickerLocked()
        {
            _languagePickerTarget = SteamVrLanguagePickerTarget.None;
            _languagePickerPageIndex = 0;
        }

        private bool TryHandleLanguagePickerClickLocked(
            RectangleF settingsRect,
            float x,
            float y,
            out SteamVrOverlayActionType? actionToRaise,
            out string? languageCode)
        {
            actionToRaise = null;
            languageCode = null;

            if (_languagePickerTarget == SteamVrLanguagePickerTarget.None)
            {
                return false;
            }

            var target = _languagePickerTarget;
            var text = SteamVrPanelText.Create(FirstNonEmpty(_config.UiLanguage, _panelState.UiLanguage));
            var options = BuildLanguagePickerOptions(_panelState, text, target);
            var layout = BuildLanguagePickerLayout(settingsRect, options.Count, _languagePickerPageIndex);

            if (layout.Close.Contains(x, y))
            {
                CloseLanguagePickerLocked();
                _panelDirty = true;
                return true;
            }

            if (layout.Prev.Contains(x, y))
            {
                _languagePickerPageIndex = Math.Max(0, _languagePickerPageIndex - 1);
                _panelDirty = true;
                return true;
            }

            if (layout.Next.Contains(x, y))
            {
                _languagePickerPageIndex = Math.Min(Math.Max(0, layout.PageCount - 1), _languagePickerPageIndex + 1);
                _panelDirty = true;
                return true;
            }

            for (var i = 0; i < layout.OptionRects.Count; i++)
            {
                if (!layout.OptionRects[i].Contains(x, y))
                {
                    continue;
                }

                var optionIndex = layout.StartIndex + i;
                if (optionIndex >= 0 && optionIndex < options.Count)
                {
                    actionToRaise = GetSelectLanguageAction(target);
                    languageCode = options[optionIndex].Code;
                    CloseLanguagePickerLocked();
                    _panelDirty = true;
                }

                return true;
            }

            if (!layout.Popup.Contains(x, y))
            {
                CloseLanguagePickerLocked();
                _panelDirty = true;
            }

            return true;
        }

        private bool ShouldSuppressPanelClick(float x, float y)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return true;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastPanelClickUtc) < PanelClickDebounceInterval)
                {
                    var dx = x - _lastPanelClickX;
                    var dy = y - _lastPanelClickY;
                    if ((dx * dx) + (dy * dy) <= 64f)
                    {
                        return true;
                    }
                }

                _lastPanelClickUtc = now;
                _lastPanelClickX = x;
                _lastPanelClickY = y;
                return false;
            }
        }

        private void ApplyCursorOverlayIfNeeded(ulong panelOverlayHandle, CVROverlay overlay)
        {
            ulong cursorHandle;
            float u, v;
            float localX, localY;
            bool valid;
            bool shouldBeVisible;
            bool dirty;
            float appliedU, appliedV;
            bool appliedVisible;
            uint rayDeviceIndex;
            uint appliedRayDeviceIndex;
            uint panelDeviceIndex;
            HmdMatrix34_t panelTransform;
            float rayDistance;
            float appliedRayDistance;
            DateTime lastFailureUtc;

            lock (_lock)
            {
                if (_disposed) return;
                if (_cursorOverlayHandle == 0) return;

                cursorHandle = _cursorOverlayHandle;
                u = _cursorU;
                v = _cursorV;
                localX = _cursorLocalX;
                localY = _cursorLocalY;
                valid = _cursorValid;
                dirty = _cursorDirty;
                appliedU = _cursorAppliedU;
                appliedV = _cursorAppliedV;
                appliedVisible = _cursorAppliedVisible;
                rayDeviceIndex = _cursorRayDeviceIndex;
                appliedRayDeviceIndex = _cursorAppliedRayDeviceIndex;
                panelDeviceIndex = _panelTrackedDeviceIndex;
                panelTransform = _panelTransformInDeviceSpace;
                rayDistance = _cursorRayDistanceMeters;
                appliedRayDistance = _cursorAppliedRayDistanceMeters;
                lastFailureUtc = _lastCursorOverlayFailureUtc;

                shouldBeVisible = _desiredVisible
                    && IsConnected
                    && panelOverlayHandle == _overlayHandle
                    && valid
                    && panelDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
            }

            try
            {
                if (!shouldBeVisible)
                {
                    if (appliedVisible)
                    {
                        overlay.HideOverlay(cursorHandle);
                        lock (_lock)
                        {
                            _cursorAppliedVisible = false;
                            _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                            _cursorAppliedRayDistanceMeters = float.NaN;
                        }
                    }
                    return;
                }

                if ((DateTime.UtcNow - lastFailureUtc) < CursorOverlayFailureRetryInterval)
                {
                    return;
                }

                // Don't spam transforms if nothing changed.
                rayDistance = Math.Clamp(rayDistance, CursorLaserMinLengthMeters, CursorLaserMaxLengthMeters);
                var moved = float.IsNaN(appliedU)
                    || Math.Abs(u - appliedU) > 0.002f
                    || Math.Abs(v - appliedV) > 0.002f
                    || panelDeviceIndex != appliedRayDeviceIndex
                    || float.IsNaN(appliedRayDistance)
                    || Math.Abs(rayDistance - appliedRayDistance) > 0.01f;
                if (!dirty && appliedVisible && !moved) return;

                var widthErr = overlay.SetOverlayWidthInMeters(cursorHandle, CursorDotOverlayWidthMeters);
                if (widthErr != EVROverlayError.None)
                {
                    MarkCursorOverlayFailure(
                        $"panel-dot width failed: widthErr={widthErr}",
                        u,
                        v,
                        CursorDotOverlayWidthMeters,
                        rayDistance);
                    return;
                }

                var bounds = new VRTextureBounds_t
                {
                    uMin = 0f,
                    vMin = 0f,
                    uMax = 1f,
                    vMax = 1f
                };
                var boundsErr = overlay.SetOverlayTextureBounds(cursorHandle, ref bounds);
                if (boundsErr != EVROverlayError.None)
                {
                    MarkCursorOverlayFailure(
                        $"panel-dot bounds failed: boundsErr={boundsErr}",
                        u,
                        v,
                        bounds.uMax,
                        bounds.vMax);
                    return;
                }

                // Render the cursor as a small overlay at the panel-space hit point, slightly in
                // front of the panel to avoid depth fighting with the panel overlay itself.
                var cursorLocalT = CreateTranslation(localX, localY, CursorPanelDepthOffsetMeters);
                var cursorT = Multiply(panelTransform, cursorLocalT);
                var transformErr = overlay.SetOverlayTransformTrackedDeviceRelative(cursorHandle, panelDeviceIndex, ref cursorT);
                if (transformErr != EVROverlayError.None)
                {
                    MarkCursorOverlayFailure(
                        $"panel-dot transform failed: transformErr={transformErr}, panelDevice={panelDeviceIndex}, rayDevice={rayDeviceIndex}",
                        u,
                        v,
                        CursorDotOverlayWidthMeters,
                        rayDistance);
                    return;
                }

                var showErr = EVROverlayError.None;
                if (!appliedVisible)
                {
                    showErr = overlay.ShowOverlay(cursorHandle);
                }

                if (showErr != EVROverlayError.None)
                {
                    MarkCursorOverlayFailure(
                        $"show failed: showErr={showErr}",
                        u,
                        v,
                        CursorDotOverlayWidthMeters,
                        rayDistance);
                    return;
                }

                lock (_lock)
                {
                    var shouldLogApplied = MarkCursorOverlayAppliedLocked(u, v, panelDeviceIndex, rayDistance);
                    if (shouldLogApplied)
                    {
                        _logger.Debug(
                            "SteamVR cursor panel-dot overlay visible. panelDeviceIndex={PanelDeviceIndex}, rayDeviceIndex={RayDeviceIndex}, u={U:0.000}, v={V:0.000}, localX={LocalX:0.0000}, localY={LocalY:0.0000}, width={Width:0.0000}",
                            panelDeviceIndex,
                            rayDeviceIndex,
                            u,
                            v,
                            localX,
                            localY,
                            CursorDotOverlayWidthMeters);
                    }
                }
            }
            catch (Exception ex)
            {
                MarkCursorOverlayFailure($"exception while applying cursor overlay: {ex.GetType().Name}", u, v, float.NaN, float.NaN);
            }
        }

        private void MarkCursorOverlayFailure(string reason, float u, float v, float overlayX, float overlayY)
        {
            var shouldLog = false;
            lock (_lock)
            {
                _lastCursorOverlayFailureUtc = DateTime.UtcNow;
                _cursorDirty = false;
                _cursorAppliedVisible = false;
                _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorAppliedRayDistanceMeters = float.NaN;

                if ((_lastCursorOverlayFailureUtc - _lastCursorOverlayFailureLogUtc) > TimeSpan.FromSeconds(2))
                {
                    _lastCursorOverlayFailureLogUtc = _lastCursorOverlayFailureUtc;
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                _logger.Debug(
                    "SteamVR cursor overlay update failed. reason={Reason}, u={U:0.000}, v={V:0.000}, overlayX={OverlayX:0.0}, overlayY={OverlayY:0.0}",
                    reason,
                    u,
                    v,
                    overlayX,
                    overlayY);
            }
        }

        private bool MarkCursorOverlayAppliedLocked(float u, float v, uint transformDeviceIndex, float rayDistance)
        {
            var shouldLogApplied = !_cursorOverlayFirstVisibleLogEmitted;
            _cursorDirty = false;
            _cursorAppliedU = u;
            _cursorAppliedV = v;
            _cursorAppliedRayDeviceIndex = transformDeviceIndex;
            _cursorAppliedRayDistanceMeters = rayDistance;
            _cursorAppliedVisible = true;
            _lastCursorOverlayFailureUtc = DateTime.MinValue;
            _cursorOverlayFirstVisibleLogEmitted = true;

            return shouldLogApplied;
        }

        private static void InverseTransformPointRigid(in HmdMatrix34_t m, float x, float y, float z, out float ox, out float oy, out float oz)
        {
            // Inverse of a rigid 3x4 transform:
            // p_local = R^T * (p_world - t)
            var dx = x - m.m3;
            var dy = y - m.m7;
            var dz = z - m.m11;

            // R = [m0 m1 m2; m4 m5 m6; m8 m9 m10]
            // R^T * v => [dot(col0,v), dot(col1,v), dot(col2,v)]
            ox = m.m0 * dx + m.m4 * dy + m.m8 * dz;
            oy = m.m1 * dx + m.m5 * dy + m.m9 * dz;
            oz = m.m2 * dx + m.m6 * dy + m.m10 * dz;
        }

        private bool ShouldFaceCursorToBackSide(in HmdMatrix34_t overlayPointTransform)
        {
            try
            {
                var system = Valve.VR.OpenVR.System;
                if (system == null)
                {
                    return false;
                }

                var poses = _trackedDevicePoses;
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

                var hmdIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd;
                if (hmdIndex >= poses.Length || !poses[hmdIndex].bPoseIsValid)
                {
                    return false;
                }

                var hmd = poses[hmdIndex].mDeviceToAbsoluteTracking;
                var toHmdX = hmd.m3 - overlayPointTransform.m3;
                var toHmdY = hmd.m7 - overlayPointTransform.m7;
                var toHmdZ = hmd.m11 - overlayPointTransform.m11;
                var distanceSq = (toHmdX * toHmdX) + (toHmdY * toHmdY) + (toHmdZ * toHmdZ);
                if (distanceSq < 0.000001f)
                {
                    return false;
                }

                // OpenVR overlay coordinates define local -Z as the outward/front side of the overlay.
                var outwardX = -overlayPointTransform.m2;
                var outwardY = -overlayPointTransform.m6;
                var outwardZ = -overlayPointTransform.m10;
                var dot = (outwardX * toHmdX) + (outwardY * toHmdY) + (outwardZ * toHmdZ);
                return dot < 0f;
            }
            catch
            {
                return false;
            }
        }

        private readonly struct TranscriptRowLayout
        {
            public TranscriptRowLayout(int index, RectangleF rowRect, (string Text, Color Color)[] blocks)
            {
                Index = index;
                RowRect = rowRect;
                Blocks = blocks;
            }

            public int Index { get; }
            public RectangleF RowRect { get; }
            public (string Text, Color Color)[] Blocks { get; }
        }

        private readonly struct PanelHeaderLayout
        {
            public PanelHeaderLayout(
                RectangleF mic,
                RectangleF desktop,
                RectangleF photo,
                RectangleF history,
                RectangleF lockButton,
                RectangleF settings)
            {
                Mic = mic;
                Desktop = desktop;
                Photo = photo;
                History = history;
                LockButton = lockButton;
                Settings = settings;
            }

            public RectangleF Mic { get; }
            public RectangleF Desktop { get; }
            public RectangleF Photo { get; }
            public RectangleF History { get; }
            public RectangleF LockButton { get; }
            public RectangleF Settings { get; }
        }

        private enum SteamVrLanguagePickerTarget
        {
            None,
            MicSource,
            MicTarget1,
            MicTarget2,
            MicTarget3,
            DesktopSource,
            DesktopTarget1,
            DesktopTarget2,
            DesktopTarget3
        }

        private sealed class SteamVrLanguagePickerOption
        {
            public string Code { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public bool IsSelected { get; init; }
        }

        private sealed class SteamVrLanguagePickerLayout
        {
            public RectangleF Popup { get; init; }
            public RectangleF Close { get; init; }
            public RectangleF Prev { get; init; }
            public RectangleF Next { get; init; }
            public RectangleF Page { get; init; }
            public int PageIndex { get; init; }
            public int StartIndex { get; init; }
            public int PageCount { get; init; }
            public List<RectangleF> OptionRects { get; } = new();
        }

        private sealed class SettingsPanelLayout
        {
            public RectangleF AnchorPalm { get; init; }
            public RectangleF AnchorBack { get; init; }
            public RectangleF AnchorHead { get; init; }
            public RectangleF HandRight { get; init; }
            public RectangleF HandLeft { get; init; }
            public RectangleF TrackingAuto { get; init; }
            public RectangleF TrackingController { get; init; }
            public RectangleF TrackingHand { get; init; }
            public RectangleF Startup { get; init; }
            public RectangleF SizeMinus { get; init; }
            public RectangleF SizeValue { get; init; }
            public RectangleF SizePlus { get; init; }
            public RectangleF FontMinus { get; init; }
            public RectangleF FontValue { get; init; }
            public RectangleF FontPlus { get; init; }
            public RectangleF Reset { get; init; }
            public RectangleF MicSource { get; init; }
            public RectangleF MicTarget1 { get; init; }
            public RectangleF MicTarget2 { get; init; }
            public RectangleF MicTarget3 { get; init; }
            public RectangleF DesktopSource { get; init; }
            public RectangleF DesktopTarget1 { get; init; }
            public RectangleF DesktopTarget2 { get; init; }
            public RectangleF DesktopTarget3 { get; init; }
            public RectangleF TtsEnabled { get; init; }
            public RectangleF TtsPassthrough { get; init; }
            public RectangleF TtsPassVolumeMinus { get; init; }
            public RectangleF TtsPassVolumeValue { get; init; }
            public RectangleF TtsPassVolumePlus { get; init; }
            public RectangleF MicTtsOriginal { get; init; }
            public RectangleF MicTtsTranslation1 { get; init; }
            public RectangleF MicTtsTranslation2 { get; init; }
            public RectangleF MicTtsTranslation3 { get; init; }
            public RectangleF DesktopTtsOriginal { get; init; }
            public RectangleF DesktopTtsTranslation1 { get; init; }
            public RectangleF DesktopTtsTranslation2 { get; init; }
            public RectangleF DesktopTtsTranslation3 { get; init; }
        }

        private sealed class SteamVrPanelText
        {
            private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SettingsTitle"] = "VRCLS Settings",
                ["Back"] = "BACK",
                ["Set"] = "SET",
                ["HistoryEmpty"] = "History (empty)",
                ["HistoryPrefix"] = "History",
                ["NoTranscriptYet"] = "No transcript yet",
                ["PanelLockedHint"] = "Panel locked",
                ["PanelUnlockConfirmHint"] = "Tap again to unlock",
                ["PanelPlacementTitle"] = "Panel placement",
                ["PanelPlacementHint"] = "Choose the anchor here. Fine-tune offsets from the desktop settings panel.",
                ["Palm"] = "Palm",
                ["BackOfHand"] = "Back",
                ["Head"] = "Head",
                ["RightHand"] = "Right hand",
                ["LeftHand"] = "Left hand",
                ["Auto"] = "Auto",
                ["Controller"] = "Controller",
                ["HandTracking"] = "Hand tracking",
                ["StartWithSteamVR"] = "Start with SteamVR",
                ["TextScale"] = "Text",
                ["ResetPosition"] = "Reset position",
                ["SettingsDragHint"] = "The in-VR drag handle has been removed to avoid accidental movement while browsing results.",
                ["LanguageRoutesTitle"] = "Recognition & translation",
                ["LanguageRoutesHint"] = "Open a language chip to choose the route language.",
                ["TtsSectionTitle"] = "Speech synthesis",
                ["TtsSectionHint"] = "Choose spoken content and mic passthrough.",
                ["TtsEnabledShort"] = "TTS",
                ["PassthroughShort"] = "Pass",
                ["PassthroughVolumeShort"] = "Pass volume",
                ["MicTtsContent"] = "Mic speech",
                ["DesktopTtsContent"] = "Monitor speech",
                ["SelectLanguageTitle"] = "Select language",
                ["Close"] = "Close",
                ["PreviousPage"] = "Prev",
                ["NextPage"] = "Next",
                ["NoLanguageOptions"] = "No language options",
                ["Mic"] = "Mic",
                ["Desktop"] = "Monitor",
                ["RecognitionShort"] = "Rec",
                ["Translation1Short"] = "T1",
                ["Translation2Short"] = "T2",
                ["Translation3Short"] = "T3",
                ["None"] = "None",
                ["PhotoTranslationTitle"] = "Photo Translation",
                ["Processing"] = "Processing",
                ["Ready"] = "Ready",
                ["Result"] = "Result",
                ["Photo"] = "Photo",
                ["PhotoProcessingCaptured"] = "Processing captured photo",
                ["PhotoWaitingCapture"] = "Waiting for capture",
                ["PhotoNoResult"] = "No photo result",
                ["PhotoProcessing"] = "Processing photo translation...",
                ["PhotoWaiting"] = "Waiting for photo translation...",
                ["PhotoNone"] = "No photo translation yet",
                ["Original"] = "Original",
                ["Translation"] = "Translation",
                ["Latest"] = "Latest"
            };

            private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>(English, StringComparer.Ordinal)
            {
                ["SettingsTitle"] = "VRCLS 设置",
                ["Back"] = "返回",
                ["Set"] = "设置",
                ["HistoryEmpty"] = "历史为空",
                ["HistoryPrefix"] = "历史",
                ["NoTranscriptYet"] = "暂无识别结果",
                ["PanelLockedHint"] = "面板已锁定",
                ["PanelUnlockConfirmHint"] = "再点一次解锁",
                ["PanelPlacementTitle"] = "面板位置",
                ["PanelPlacementHint"] = "在 VR 内选择锚点，细调偏移请到桌面设置页。",
                ["Palm"] = "掌心",
                ["BackOfHand"] = "手背",
                ["Head"] = "头部",
                ["RightHand"] = "右手",
                ["LeftHand"] = "左手",
                ["Auto"] = "自动",
                ["Controller"] = "手柄",
                ["HandTracking"] = "手势追踪",
                ["StartWithSteamVR"] = "随 SteamVR 启动",
                ["TextScale"] = "文本",
                ["ResetPosition"] = "重置位置",
                ["SettingsDragHint"] = "VR 内拖拽柄已移除，避免浏览结果时误移动面板。",
                ["LanguageRoutesTitle"] = "识别与翻译语言",
                ["LanguageRoutesHint"] = "点击语言按钮，打开列表选择该通道语言。",
                ["TtsSectionTitle"] = "语音合成",
                ["TtsSectionHint"] = "选择播报内容、麦克风透传和透传音量。",
                ["TtsEnabledShort"] = "TTS",
                ["PassthroughShort"] = "透传",
                ["PassthroughVolumeShort"] = "透传音量",
                ["MicTtsContent"] = "麦克风播报",
                ["DesktopTtsContent"] = "监听音频播报",
                ["SelectLanguageTitle"] = "选择语言",
                ["Close"] = "关闭",
                ["PreviousPage"] = "上页",
                ["NextPage"] = "下页",
                ["NoLanguageOptions"] = "暂无语言选项",
                ["Mic"] = "麦克风",
                ["Desktop"] = "监听音频",
                ["RecognitionShort"] = "识别",
                ["Translation1Short"] = "译1",
                ["Translation2Short"] = "译2",
                ["Translation3Short"] = "译3",
                ["None"] = "不翻译",
                ["PhotoTranslationTitle"] = "拍照翻译",
                ["Processing"] = "处理中",
                ["Ready"] = "等待中",
                ["Result"] = "结果",
                ["Photo"] = "拍照",
                ["PhotoProcessingCaptured"] = "正在处理已捕获照片",
                ["PhotoWaitingCapture"] = "等待截图",
                ["PhotoNoResult"] = "暂无拍照结果",
                ["PhotoProcessing"] = "正在处理拍照翻译...",
                ["PhotoWaiting"] = "等待拍照翻译...",
                ["PhotoNone"] = "暂无拍照翻译",
                ["Original"] = "原文",
                ["Translation"] = "译文",
                ["Latest"] = "最新"
            };

            private static readonly IReadOnlyDictionary<string, string> Japanese = new Dictionary<string, string>(English, StringComparer.Ordinal)
            {
                ["SettingsTitle"] = "VRCLS 設定",
                ["Back"] = "戻る",
                ["Set"] = "設定",
                ["HistoryEmpty"] = "履歴なし",
                ["HistoryPrefix"] = "履歴",
                ["NoTranscriptYet"] = "まだ認識結果がありません",
                ["PanelLockedHint"] = "パネルはロック中",
                ["PanelUnlockConfirmHint"] = "もう一度押して解除",
                ["PanelPlacementTitle"] = "パネル位置",
                ["PanelPlacementHint"] = "VR 内でアンカーを選択します。細かなオフセットはデスクトップ設定で調整します。",
                ["Palm"] = "手のひら",
                ["BackOfHand"] = "手の甲",
                ["Head"] = "頭部",
                ["RightHand"] = "右手",
                ["LeftHand"] = "左手",
                ["Auto"] = "自動",
                ["Controller"] = "コントローラー",
                ["HandTracking"] = "ハンド追跡",
                ["StartWithSteamVR"] = "SteamVR と起動",
                ["TextScale"] = "文字",
                ["ResetPosition"] = "位置をリセット",
                ["SettingsDragHint"] = "結果閲覧中の誤移動を避けるため、VR 内ドラッグハンドルは削除されています。",
                ["LanguageRoutesTitle"] = "認識と翻訳言語",
                ["LanguageRoutesHint"] = "言語チップを開いて、このルートの言語を選択します。",
                ["TtsSectionTitle"] = "音声合成",
                ["TtsSectionHint"] = "読み上げ内容とマイクパススルーを選択します。",
                ["TtsEnabledShort"] = "TTS",
                ["PassthroughShort"] = "パス",
                ["PassthroughVolumeShort"] = "パス音量",
                ["MicTtsContent"] = "マイク読み上げ",
                ["DesktopTtsContent"] = "監視音声読み上げ",
                ["SelectLanguageTitle"] = "言語を選択",
                ["Close"] = "閉じる",
                ["PreviousPage"] = "前へ",
                ["NextPage"] = "次へ",
                ["NoLanguageOptions"] = "言語候補なし",
                ["Mic"] = "マイク",
                ["Desktop"] = "監視音声",
                ["RecognitionShort"] = "認識",
                ["Translation1Short"] = "訳1",
                ["Translation2Short"] = "訳2",
                ["Translation3Short"] = "訳3",
                ["None"] = "翻訳なし",
                ["PhotoTranslationTitle"] = "写真翻訳",
                ["Processing"] = "処理中",
                ["Ready"] = "待機中",
                ["Result"] = "結果",
                ["Photo"] = "写真",
                ["PhotoProcessingCaptured"] = "取得した写真を処理中",
                ["PhotoWaitingCapture"] = "撮影待ち",
                ["PhotoNoResult"] = "写真結果なし",
                ["PhotoProcessing"] = "写真翻訳を処理中...",
                ["PhotoWaiting"] = "写真翻訳を待機中...",
                ["PhotoNone"] = "写真翻訳はまだありません",
                ["Original"] = "原文",
                ["Translation"] = "翻訳",
                ["Latest"] = "最新"
            };

            private static readonly IReadOnlyDictionary<string, string> French = new Dictionary<string, string>(English, StringComparer.Ordinal)
            {
                ["SettingsTitle"] = "Paramètres VRCLS",
                ["Back"] = "RETOUR",
                ["Set"] = "SET",
                ["HistoryEmpty"] = "Historique vide",
                ["HistoryPrefix"] = "Historique",
                ["NoTranscriptYet"] = "Aucune transcription",
                ["PanelLockedHint"] = "Panneau verrouillé",
                ["PanelUnlockConfirmHint"] = "Appuyez encore pour déverrouiller",
                ["PanelPlacementTitle"] = "Position du panneau",
                ["PanelPlacementHint"] = "Choisissez l'ancrage ici. Réglez les décalages dans les paramètres bureau.",
                ["Palm"] = "Paume",
                ["BackOfHand"] = "Dos",
                ["Head"] = "Tête",
                ["RightHand"] = "Main droite",
                ["LeftHand"] = "Main gauche",
                ["Auto"] = "Auto",
                ["Controller"] = "Contrôleur",
                ["HandTracking"] = "Suivi main",
                ["StartWithSteamVR"] = "Avec SteamVR",
                ["TextScale"] = "Texte",
                ["ResetPosition"] = "Réinitialiser",
                ["SettingsDragHint"] = "La poignée de déplacement VR a été retirée pour éviter les déplacements accidentels.",
                ["LanguageRoutesTitle"] = "Reconnaissance et traduction",
                ["LanguageRoutesHint"] = "Ouvrez un bouton de langue pour choisir la langue du trajet.",
                ["TtsSectionTitle"] = "Synthèse vocale",
                ["TtsSectionHint"] = "Choisissez le contenu lu et le passthrough micro.",
                ["TtsEnabledShort"] = "TTS",
                ["PassthroughShort"] = "Pass",
                ["PassthroughVolumeShort"] = "Volume pass",
                ["MicTtsContent"] = "Lecture micro",
                ["DesktopTtsContent"] = "Lecture PC",
                ["SelectLanguageTitle"] = "Choisir la langue",
                ["Close"] = "Fermer",
                ["PreviousPage"] = "Préc.",
                ["NextPage"] = "Suiv.",
                ["NoLanguageOptions"] = "Aucune langue",
                ["Mic"] = "Micro",
                ["Desktop"] = "Audio PC",
                ["RecognitionShort"] = "Reco",
                ["Translation1Short"] = "T1",
                ["Translation2Short"] = "T2",
                ["Translation3Short"] = "T3",
                ["None"] = "Aucune",
                ["PhotoTranslationTitle"] = "Traduction photo",
                ["Processing"] = "Traitement",
                ["Ready"] = "Prêt",
                ["Result"] = "Résultat",
                ["Photo"] = "Photo",
                ["PhotoProcessingCaptured"] = "Traitement de la photo capturée",
                ["PhotoWaitingCapture"] = "En attente de capture",
                ["PhotoNoResult"] = "Aucun résultat photo",
                ["PhotoProcessing"] = "Traduction photo en cours...",
                ["PhotoWaiting"] = "En attente de traduction photo...",
                ["PhotoNone"] = "Aucune traduction photo",
                ["Original"] = "Original",
                ["Translation"] = "Traduction",
                ["Latest"] = "Récent"
            };

            private static readonly IReadOnlyDictionary<string, string> Russian = new Dictionary<string, string>(English, StringComparer.Ordinal)
            {
                ["SettingsTitle"] = "Настройки VRCLS",
                ["Back"] = "НАЗАД",
                ["Set"] = "НАСТР",
                ["HistoryEmpty"] = "История пуста",
                ["HistoryPrefix"] = "История",
                ["NoTranscriptYet"] = "Распознаваний пока нет",
                ["PanelLockedHint"] = "Панель заблокирована",
                ["PanelUnlockConfirmHint"] = "Нажмите ещё раз",
                ["PanelPlacementTitle"] = "Положение панели",
                ["PanelPlacementHint"] = "Выберите привязку здесь. Смещение настраивается в окне настроек.",
                ["Palm"] = "Ладонь",
                ["BackOfHand"] = "Тыльная",
                ["Head"] = "Голова",
                ["RightHand"] = "Правая",
                ["LeftHand"] = "Левая",
                ["Auto"] = "Авто",
                ["Controller"] = "Контроллер",
                ["HandTracking"] = "Трекинг рук",
                ["StartWithSteamVR"] = "Старт со SteamVR",
                ["TextScale"] = "Текст",
                ["ResetPosition"] = "Сбросить",
                ["SettingsDragHint"] = "VR-ручка перемещения удалена, чтобы панель случайно не сдвигалась при просмотре.",
                ["LanguageRoutesTitle"] = "Распознавание и перевод",
                ["LanguageRoutesHint"] = "Откройте язык, чтобы выбрать язык маршрута.",
                ["TtsSectionTitle"] = "Синтез речи",
                ["TtsSectionHint"] = "Выберите речь и сквозной микрофон.",
                ["TtsEnabledShort"] = "TTS",
                ["PassthroughShort"] = "Сквозн.",
                ["PassthroughVolumeShort"] = "Громк. сквозн.",
                ["MicTtsContent"] = "Речь микр.",
                ["DesktopTtsContent"] = "Речь ПК",
                ["SelectLanguageTitle"] = "Выбор языка",
                ["Close"] = "Закрыть",
                ["PreviousPage"] = "Назад",
                ["NextPage"] = "Далее",
                ["NoLanguageOptions"] = "Нет языков",
                ["Mic"] = "Микрофон",
                ["Desktop"] = "Аудио ПК",
                ["RecognitionShort"] = "Расп.",
                ["Translation1Short"] = "П1",
                ["Translation2Short"] = "П2",
                ["Translation3Short"] = "П3",
                ["None"] = "Нет",
                ["PhotoTranslationTitle"] = "Фото-перевод",
                ["Processing"] = "Обработка",
                ["Ready"] = "Готово",
                ["Result"] = "Результат",
                ["Photo"] = "Фото",
                ["PhotoProcessingCaptured"] = "Обработка снимка",
                ["PhotoWaitingCapture"] = "Ожидание снимка",
                ["PhotoNoResult"] = "Нет фото-результата",
                ["PhotoProcessing"] = "Обработка фото-перевода...",
                ["PhotoWaiting"] = "Ожидание фото-перевода...",
                ["PhotoNone"] = "Фото-переводов пока нет",
                ["Original"] = "Оригинал",
                ["Translation"] = "Перевод",
                ["Latest"] = "Последний"
            };

            private SteamVrPanelText(string language, IReadOnlyDictionary<string, string> values)
            {
                Language = language;
                _values = values;
            }

            private readonly IReadOnlyDictionary<string, string> _values;

            public string Language { get; }

            public string Get(string key)
            {
                return _values.TryGetValue(key, out var value)
                    ? value
                    : English.TryGetValue(key, out var fallback) ? fallback : key;
            }

            public static SteamVrPanelText Create(string? uiLanguage)
            {
                var normalized = NormalizeUiLanguage(uiLanguage);
                return normalized switch
                {
                    "zh-CN" => new SteamVrPanelText(normalized, Chinese),
                    "ja-JP" => new SteamVrPanelText(normalized, Japanese),
                    "fr-FR" => new SteamVrPanelText(normalized, French),
                    "ru-RU" => new SteamVrPanelText(normalized, Russian),
                    _ => new SteamVrPanelText("en-US", English)
                };
            }
        }

        private sealed class SteamVrLanguageRouteSnapshot
        {
            public string MicSource { get; init; } = string.Empty;
            public string MicTarget1 { get; init; } = string.Empty;
            public string MicTarget2 { get; init; } = string.Empty;
            public string MicTarget3 { get; init; } = string.Empty;
            public string DesktopSource { get; init; } = string.Empty;
            public string DesktopTarget1 { get; init; } = string.Empty;
            public string DesktopTarget2 { get; init; } = string.Empty;
            public string DesktopTarget3 { get; init; } = string.Empty;
        }

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> CommonLanguageNames =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["en-US"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh"] = "Chinese", ["en"] = "English", ["ja"] = "Japanese", ["ko"] = "Korean",
                    ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["ru"] = "Russian",
                    ["it"] = "Italian", ["pt"] = "Portuguese", ["ar"] = "Arabic", ["hi"] = "Hindi",
                    ["th"] = "Thai", ["vi"] = "Vietnamese", ["id"] = "Indonesian", ["tr"] = "Turkish",
                    ["pl"] = "Polish", ["nl"] = "Dutch", ["sv"] = "Swedish", ["uk"] = "Ukrainian"
                },
                ["zh-CN"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh"] = "中文", ["en"] = "英语", ["ja"] = "日语", ["ko"] = "韩语",
                    ["de"] = "德语", ["fr"] = "法语", ["es"] = "西班牙语", ["ru"] = "俄语",
                    ["it"] = "意大利语", ["pt"] = "葡萄牙语", ["ar"] = "阿拉伯语", ["hi"] = "印地语",
                    ["th"] = "泰语", ["vi"] = "越南语", ["id"] = "印尼语", ["tr"] = "土耳其语",
                    ["pl"] = "波兰语", ["nl"] = "荷兰语", ["sv"] = "瑞典语", ["uk"] = "乌克兰语"
                },
                ["ja-JP"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh"] = "中国語", ["en"] = "英語", ["ja"] = "日本語", ["ko"] = "韓国語",
                    ["de"] = "ドイツ語", ["fr"] = "フランス語", ["es"] = "スペイン語", ["ru"] = "ロシア語",
                    ["it"] = "イタリア語", ["pt"] = "ポルトガル語", ["ar"] = "アラビア語", ["hi"] = "ヒンディー語",
                    ["th"] = "タイ語", ["vi"] = "ベトナム語", ["id"] = "インドネシア語", ["tr"] = "トルコ語",
                    ["pl"] = "ポーランド語", ["nl"] = "オランダ語", ["sv"] = "スウェーデン語", ["uk"] = "ウクライナ語"
                },
                ["fr-FR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh"] = "chinois", ["en"] = "anglais", ["ja"] = "japonais", ["ko"] = "coréen",
                    ["de"] = "allemand", ["fr"] = "français", ["es"] = "espagnol", ["ru"] = "russe",
                    ["it"] = "italien", ["pt"] = "portugais", ["ar"] = "arabe", ["hi"] = "hindi",
                    ["th"] = "thaï", ["vi"] = "vietnamien", ["id"] = "indonésien", ["tr"] = "turc",
                    ["pl"] = "polonais", ["nl"] = "néerlandais", ["sv"] = "suédois", ["uk"] = "ukrainien"
                },
                ["ru-RU"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh"] = "китайский", ["en"] = "английский", ["ja"] = "японский", ["ko"] = "корейский",
                    ["de"] = "немецкий", ["fr"] = "французский", ["es"] = "испанский", ["ru"] = "русский",
                    ["it"] = "итальянский", ["pt"] = "португальский", ["ar"] = "арабский", ["hi"] = "хинди",
                    ["th"] = "тайский", ["vi"] = "вьетнамский", ["id"] = "индонезийский", ["tr"] = "турецкий",
                    ["pl"] = "польский", ["nl"] = "нидерландский", ["sv"] = "шведский", ["uk"] = "украинский"
                }
            };

        private static readonly string[] CommonRecognitionLanguageCodes =
        {
            "zh", "en", "ja", "ko", "de", "fr", "es", "ru"
        };

        private static readonly string[] LocalRecognitionLanguageCodes =
        {
            "zh", "en"
        };

        private static readonly string[] CommonTranslationLanguageCodes =
        {
            "en", "zh", "ja", "ko", "de", "fr", "es", "ru"
        };

        private SteamVrLanguageRouteSnapshot BuildLanguageRouteSnapshot(
            SteamVrOverlayPanelState state,
            SteamVrPanelText text)
        {
            return new SteamVrLanguageRouteSnapshot
            {
                MicSource = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolveSourceLanguage("Mic"), state.MicSourceLanguage),
                    _config.SupportedRecognitionLanguages,
                    text,
                    includeNone: false),
                MicTarget1 = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolvePrimaryTargetLanguage("Mic"), state.MicTargetLanguage),
                    _config.SupportedTranslationLanguages,
                    text,
                    includeNone: true),
                MicTarget2 = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolveSecondaryTargetLanguage("Mic"), state.MicTargetLanguage2),
                    _config.SupportedTranslationLanguages,
                    text,
                    includeNone: true),
                MicTarget3 = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolveTertiaryTargetLanguage("Mic"), state.MicTargetLanguage3),
                    _config.SupportedTranslationLanguages,
                    text,
                    includeNone: true),
                DesktopSource = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolveSourceLanguage("Desktop"), state.DesktopSourceLanguage),
                    _config.SupportedRecognitionLanguages,
                    text,
                    includeNone: false),
                DesktopTarget1 = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolvePrimaryTargetLanguage("Desktop"), state.DesktopTargetLanguage),
                    _config.SupportedTranslationLanguages,
                    text,
                    includeNone: true),
                DesktopTarget2 = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolveSecondaryTargetLanguage("Desktop"), state.DesktopTargetLanguage2),
                    _config.SupportedTranslationLanguages,
                    text,
                    includeNone: true),
                DesktopTarget3 = GetLanguageDisplayName(
                    FirstNonEmpty(_config.ResolveTertiaryTargetLanguage("Desktop"), state.DesktopTargetLanguage3),
                    _config.SupportedTranslationLanguages,
                    text,
                    includeNone: true)
            };
        }

        private IReadOnlyList<SteamVrLanguagePickerOption> BuildLanguagePickerOptions(
            SteamVrOverlayPanelState state,
            SteamVrPanelText text,
            SteamVrLanguagePickerTarget target)
        {
            if (target == SteamVrLanguagePickerTarget.None)
            {
                return Array.Empty<SteamVrLanguagePickerOption>();
            }

            var recognition = IsRecognitionLanguagePickerTarget(target);
            var catalog = recognition ? _config.SupportedRecognitionLanguages : _config.SupportedTranslationLanguages;
            var selectedCode = NormalizeLanguageCode(GetLanguageCodeForPickerTarget(state, target));
            var localMicRecognition = IsLocalMicRecognitionPickerTarget(target);
            if (localMicRecognition)
            {
                selectedCode = AppConfig.CoerceLocalRecognitionSourceLanguage(selectedCode);
            }
            if (!recognition && string.IsNullOrWhiteSpace(selectedCode))
            {
                selectedCode = "none";
            }

            var options = new List<SteamVrLanguagePickerOption>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddOption(string? code, string? displayName = null)
            {
                var normalized = NormalizeLanguageCode(code);
                if (string.IsNullOrWhiteSpace(normalized)
                    || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
                    || (recognition && string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
                    || !seen.Add(normalized))
                {
                    return;
                }

                options.Add(new SteamVrLanguagePickerOption
                {
                    Code = normalized,
                    DisplayName = GetLanguageDisplayName(normalized, catalog, text, includeNone: !recognition, fallbackDisplayName: displayName),
                    IsSelected = string.Equals(normalized, selectedCode, StringComparison.OrdinalIgnoreCase)
                });
            }

            if (!recognition)
            {
                AddOption("none");
            }

            if (localMicRecognition)
            {
                foreach (var code in LocalRecognitionLanguageCodes)
                {
                    AddOption(code);
                }
            }
            else if (catalog != null)
            {
                foreach (var item in catalog)
                {
                    AddOption(item?.Code, item?.DisplayName);
                }
            }

            if (options.Count == (recognition ? 0 : 1))
            {
                foreach (var code in recognition ? CommonRecognitionLanguageCodes : CommonTranslationLanguageCodes)
                {
                    AddOption(code);
                }
            }

            AddOption(selectedCode);

            return OrderLanguagePickerOptions(options, recognition);
        }

        private bool IsLocalMicRecognitionPickerTarget(SteamVrLanguagePickerTarget target)
        {
            return target == SteamVrLanguagePickerTarget.MicSource
                && _config.RecognitionBackendModeValue == RecognitionBackendMode.Local;
        }

        private string GetLanguageCodeForPickerTarget(
            SteamVrOverlayPanelState state,
            SteamVrLanguagePickerTarget target)
        {
            return target switch
            {
                SteamVrLanguagePickerTarget.MicSource => FirstNonEmpty(state.MicSourceLanguage, _config.ResolveSourceLanguage("Mic")),
                SteamVrLanguagePickerTarget.MicTarget1 => FirstNonEmpty(state.MicTargetLanguage, _config.ResolvePrimaryTargetLanguage("Mic")),
                SteamVrLanguagePickerTarget.MicTarget2 => FirstNonEmpty(state.MicTargetLanguage2, _config.ResolveSecondaryTargetLanguage("Mic")),
                SteamVrLanguagePickerTarget.MicTarget3 => FirstNonEmpty(state.MicTargetLanguage3, _config.ResolveTertiaryTargetLanguage("Mic")),
                SteamVrLanguagePickerTarget.DesktopSource => FirstNonEmpty(state.DesktopSourceLanguage, _config.ResolveSourceLanguage("Desktop")),
                SteamVrLanguagePickerTarget.DesktopTarget1 => FirstNonEmpty(state.DesktopTargetLanguage, _config.ResolvePrimaryTargetLanguage("Desktop")),
                SteamVrLanguagePickerTarget.DesktopTarget2 => FirstNonEmpty(state.DesktopTargetLanguage2, _config.ResolveSecondaryTargetLanguage("Desktop")),
                SteamVrLanguagePickerTarget.DesktopTarget3 => FirstNonEmpty(state.DesktopTargetLanguage3, _config.ResolveTertiaryTargetLanguage("Desktop")),
                _ => string.Empty
            };
        }

        private static bool IsRecognitionLanguagePickerTarget(SteamVrLanguagePickerTarget target)
        {
            return target is SteamVrLanguagePickerTarget.MicSource
                or SteamVrLanguagePickerTarget.DesktopSource;
        }

        private static SteamVrOverlayActionType? GetSelectLanguageAction(SteamVrLanguagePickerTarget target)
        {
            return target switch
            {
                SteamVrLanguagePickerTarget.MicSource => SteamVrOverlayActionType.SelectMicSourceLanguage,
                SteamVrLanguagePickerTarget.MicTarget1 => SteamVrOverlayActionType.SelectMicTargetLanguage,
                SteamVrLanguagePickerTarget.MicTarget2 => SteamVrOverlayActionType.SelectMicTargetLanguage2,
                SteamVrLanguagePickerTarget.MicTarget3 => SteamVrOverlayActionType.SelectMicTargetLanguage3,
                SteamVrLanguagePickerTarget.DesktopSource => SteamVrOverlayActionType.SelectDesktopSourceLanguage,
                SteamVrLanguagePickerTarget.DesktopTarget1 => SteamVrOverlayActionType.SelectDesktopTargetLanguage,
                SteamVrLanguagePickerTarget.DesktopTarget2 => SteamVrOverlayActionType.SelectDesktopTargetLanguage2,
                SteamVrLanguagePickerTarget.DesktopTarget3 => SteamVrOverlayActionType.SelectDesktopTargetLanguage3,
                _ => null
            };
        }

        private static string GetLanguagePickerTargetLabel(SteamVrPanelText text, SteamVrLanguagePickerTarget target)
        {
            return target switch
            {
                SteamVrLanguagePickerTarget.MicSource => $"{text.Get("Mic")} · {text.Get("RecognitionShort")}",
                SteamVrLanguagePickerTarget.MicTarget1 => $"{text.Get("Mic")} · {text.Get("Translation1Short")}",
                SteamVrLanguagePickerTarget.MicTarget2 => $"{text.Get("Mic")} · {text.Get("Translation2Short")}",
                SteamVrLanguagePickerTarget.MicTarget3 => $"{text.Get("Mic")} · {text.Get("Translation3Short")}",
                SteamVrLanguagePickerTarget.DesktopSource => $"{text.Get("Desktop")} · {text.Get("RecognitionShort")}",
                SteamVrLanguagePickerTarget.DesktopTarget1 => $"{text.Get("Desktop")} · {text.Get("Translation1Short")}",
                SteamVrLanguagePickerTarget.DesktopTarget2 => $"{text.Get("Desktop")} · {text.Get("Translation2Short")}",
                SteamVrLanguagePickerTarget.DesktopTarget3 => $"{text.Get("Desktop")} · {text.Get("Translation3Short")}",
                _ => string.Empty
            };
        }

        private static string FirstNonEmpty(string? preferred, string? fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? (fallback ?? string.Empty) : preferred;
        }

        private static IReadOnlyList<SteamVrLanguagePickerOption> OrderLanguagePickerOptions(
            IReadOnlyList<SteamVrLanguagePickerOption> options,
            bool recognition)
        {
            var result = new List<SteamVrLanguagePickerOption>(options.Count);
            var addedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commonOrder = recognition ? CommonRecognitionLanguageCodes : CommonTranslationLanguageCodes;

            if (!recognition)
            {
                AddByGroupKey("none");
            }

            foreach (var code in commonOrder)
            {
                AddByGroupKey(code);
            }

            foreach (var option in options)
            {
                if (addedCodes.Add(option.Code))
                {
                    result.Add(option);
                }
            }

            return result;

            void AddByGroupKey(string code)
            {
                var groupKey = GetLanguageGroupKey(code);
                if (string.IsNullOrWhiteSpace(groupKey))
                {
                    return;
                }

                foreach (var option in options)
                {
                    if (string.Equals(GetLanguageGroupKey(option.Code), groupKey, StringComparison.OrdinalIgnoreCase)
                        && addedCodes.Add(option.Code))
                    {
                        result.Add(option);
                    }
                }
            }
        }

        private static string GetLanguageDisplayName(
            string? code,
            IEnumerable<LanguageCatalogItem>? catalog,
            SteamVrPanelText text,
            bool includeNone,
            string? fallbackDisplayName = null)
        {
            var normalized = NormalizeLanguageCode(code);
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
            {
                return includeNone ? text.Get("None") : normalized;
            }

            var groupKey = GetLanguageGroupKey(normalized);
            if (CommonLanguageNames.TryGetValue(text.Language, out var names)
                && names.TryGetValue(groupKey, out var commonName))
            {
                return commonName;
            }

            var catalogName = catalog?
                .FirstOrDefault(item => string.Equals(NormalizeLanguageCode(item?.Code), normalized, StringComparison.OrdinalIgnoreCase))
                ?.DisplayName;
            var preferredDisplayName = ChooseCatalogLanguageDisplayName(
                normalized,
                text.Language,
                FirstNonEmpty(catalogName, fallbackDisplayName));
            if (!string.IsNullOrWhiteSpace(preferredDisplayName))
            {
                return CompactLanguageName(preferredDisplayName.Trim(), normalized);
            }

            var cultureName = TryGetLocalizedCultureLanguageName(normalized, text.Language);
            if (!string.IsNullOrWhiteSpace(cultureName))
            {
                return CompactLanguageName(cultureName.Trim(), normalized);
            }

            if (!string.IsNullOrWhiteSpace(catalogName)
                && !string.Equals(catalogName.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return CompactLanguageName(catalogName.Trim(), normalized);
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(normalized);
                var display = text.Language == "en-US" ? culture.EnglishName : culture.NativeName;
                return CompactLanguageName(display, normalized);
            }
            catch (CultureNotFoundException)
            {
                return normalized.ToUpperInvariant();
            }
        }

        private static string? ChooseCatalogLanguageDisplayName(
            string code,
            string uiLanguage,
            string? fallbackDisplayName)
        {
            var trimmed = fallbackDisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)
                || string.Equals(trimmed, code, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var normalizedUiLanguage = NormalizeUiLanguage(uiLanguage);
            if (normalizedUiLanguage == "en-US")
            {
                return LooksLikeLatinDisplayName(trimmed) ? trimmed : null;
            }

            if (normalizedUiLanguage == "zh-CN")
            {
                return ContainsCjkCharacters(trimmed) ? trimmed : null;
            }

            return null;
        }

        private static string? TryGetLocalizedCultureLanguageName(string code, string uiLanguage)
        {
            CultureInfo? previousUiCulture = null;
            try
            {
                previousUiCulture = CultureInfo.CurrentUICulture;
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(NormalizeUiLanguage(uiLanguage));

                var culture = CultureInfo.GetCultureInfo(code);
                var display = NormalizeUiLanguage(uiLanguage) == "en-US"
                    ? culture.EnglishName
                    : culture.DisplayName;
                return display.StartsWith("Unknown Language", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : display;
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
            finally
            {
                if (previousUiCulture != null)
                {
                    CultureInfo.CurrentUICulture = previousUiCulture;
                }
            }
        }

        private static bool LooksLikeLatinDisplayName(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && !ContainsCjkCharacters(value)
                && value.Any(static ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
        }

        private static bool ContainsCjkCharacters(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Any(static ch => ch >= '\u4E00' && ch <= '\u9FFF');
        }

        private static string CompactLanguageName(string displayName, string code)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return code.ToUpperInvariant();
            }

            var paren = displayName.IndexOfAny(new[] { '(', '（' });
            if (paren > 0)
            {
                displayName = displayName.Substring(0, paren).Trim();
            }

            return displayName.Length <= 18 ? displayName : code.ToUpperInvariant();
        }

        private static string NormalizeLanguageCode(string? value)
        {
            return (value ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        }

        private static string GetLanguageGroupKey(string? code)
        {
            var normalized = NormalizeLanguageCode(code);
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            var separator = normalized.IndexOf('-');
            return separator > 0 ? normalized.Substring(0, separator) : normalized;
        }

        private static string NormalizeUiLanguage(string? value)
        {
            var normalized = (value ?? string.Empty).Trim().Replace('_', '-');
            if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja-JP";
            if (normalized.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "fr-FR";
            if (normalized.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "ru-RU";
            return "en-US";
        }

        private static PanelHeaderLayout BuildPanelHeaderLayout()
        {
            const float headerY = 10f;
            const float headerH = 54f;
            return new PanelHeaderLayout(
                new RectangleF(140, headerY, 110, headerH),
                new RectangleF(258, headerY, 110, headerH),
                new RectangleF(376, headerY, 110, headerH),
                new RectangleF(494, headerY, 190, headerH),
                new RectangleF(692, headerY, 40, headerH),
                new RectangleF(740, headerY, 60, headerH));
        }

        private static SettingsPanelLayout BuildSettingsPanelLayout(RectangleF settingsRect)
        {
            var leftX = settingsRect.X + 24;
            var rightX = settingsRect.X + 370;
            var anchorY = settingsRect.Y + 76f;
            var anchorPalmRect = new RectangleF(leftX, anchorY, 96, 42);
            var anchorBackRect = new RectangleF(anchorPalmRect.Right + 10, anchorY, 116, 42);
            var anchorHeadRect = new RectangleF(anchorBackRect.Right + 10, anchorY, 96, 42);

            var handY = anchorPalmRect.Bottom + 16;
            var handRightRect = new RectangleF(leftX, handY, 132, 42);
            var handLeftRect = new RectangleF(handRightRect.Right + 10, handY, 132, 42);

            var trackingY = handRightRect.Bottom + 14;
            var trackingAutoRect = new RectangleF(leftX, trackingY, 88, 42);
            var trackingControllerRect = new RectangleF(trackingAutoRect.Right + 10, trackingY, 126, 42);
            var trackingHandRect = new RectangleF(trackingControllerRect.Right + 10, trackingY, 136, 42);

            var startupRect = new RectangleF(leftX, trackingAutoRect.Bottom + 16, 214, 42);

            var sizeY = startupRect.Bottom + 16;
            var sizeMinusRect = new RectangleF(leftX, sizeY, 42, 42);
            var sizeValueRect = new RectangleF(sizeMinusRect.Right + 10, sizeY, 126, 42);
            var sizePlusRect = new RectangleF(sizeValueRect.Right + 10, sizeY, 42, 42);

            var fontY = sizeMinusRect.Bottom + 12;
            var fontMinusRect = new RectangleF(leftX, fontY, 42, 42);
            var fontValueRect = new RectangleF(fontMinusRect.Right + 10, fontY, 126, 42);
            var fontPlusRect = new RectangleF(fontValueRect.Right + 10, fontY, 42, 42);

            var resetRect = new RectangleF(leftX, fontMinusRect.Bottom + 16, 170, 42);

            const float languageButtonHeight = 38f;
            const float languageButtonGap = 10f;
            var languageButtonWidth = (settingsRect.Right - 24 - rightX - languageButtonGap) * 0.5f;
            languageButtonWidth = Math.Max(136f, languageButtonWidth);
            var micButtonY = settingsRect.Y + 102f;
            var desktopButtonY = settingsRect.Y + 258f;
            var ttsY = settingsRect.Y + 344f;
            var ttsControlY = ttsY + 50f;
            var ttsVolumeY = ttsControlY + 40f;
            var ttsContentChipY = ttsVolumeY + 54f;
            const float ttsButtonHeight = 36f;
            const float ttsContentChipHeight = 34f;
            const float ttsContentChipGap = 8f;
            var ttsAreaWidth = settingsRect.Right - 24 - rightX;
            var ttsContentChipWidth = (ttsAreaWidth - (ttsContentChipGap * 3)) / 4f;
            ttsContentChipWidth = Math.Max(70f, ttsContentChipWidth);

            return new SettingsPanelLayout
            {
                AnchorPalm = anchorPalmRect,
                AnchorBack = anchorBackRect,
                AnchorHead = anchorHeadRect,
                HandRight = handRightRect,
                HandLeft = handLeftRect,
                TrackingAuto = trackingAutoRect,
                TrackingController = trackingControllerRect,
                TrackingHand = trackingHandRect,
                Startup = startupRect,
                SizeMinus = sizeMinusRect,
                SizeValue = sizeValueRect,
                SizePlus = sizePlusRect,
                FontMinus = fontMinusRect,
                FontValue = fontValueRect,
                FontPlus = fontPlusRect,
                Reset = resetRect,
                MicSource = new RectangleF(rightX, micButtonY, languageButtonWidth, languageButtonHeight),
                MicTarget1 = new RectangleF(rightX + languageButtonWidth + languageButtonGap, micButtonY, languageButtonWidth, languageButtonHeight),
                MicTarget2 = new RectangleF(rightX, micButtonY + languageButtonHeight + 8, languageButtonWidth, languageButtonHeight),
                MicTarget3 = new RectangleF(rightX + languageButtonWidth + languageButtonGap, micButtonY + languageButtonHeight + 8, languageButtonWidth, languageButtonHeight),
                DesktopSource = new RectangleF(rightX, desktopButtonY, languageButtonWidth, languageButtonHeight),
                DesktopTarget1 = new RectangleF(rightX + languageButtonWidth + languageButtonGap, desktopButtonY, languageButtonWidth, languageButtonHeight),
                DesktopTarget2 = new RectangleF(rightX, desktopButtonY + languageButtonHeight + 8, languageButtonWidth, languageButtonHeight),
                DesktopTarget3 = new RectangleF(rightX + languageButtonWidth + languageButtonGap, desktopButtonY + languageButtonHeight + 8, languageButtonWidth, languageButtonHeight),
                TtsEnabled = new RectangleF(rightX, ttsControlY, 82, ttsButtonHeight),
                TtsPassthrough = new RectangleF(rightX + 92, ttsControlY, 136, ttsButtonHeight),
                TtsPassVolumeMinus = new RectangleF(rightX, ttsVolumeY, 42, ttsButtonHeight),
                TtsPassVolumeValue = new RectangleF(rightX + 52, ttsVolumeY, 142, ttsButtonHeight),
                TtsPassVolumePlus = new RectangleF(rightX + 204, ttsVolumeY, 42, ttsButtonHeight),
                MicTtsOriginal = new RectangleF(rightX, ttsContentChipY, ttsContentChipWidth, ttsContentChipHeight),
                MicTtsTranslation1 = new RectangleF(rightX + (ttsContentChipWidth + ttsContentChipGap), ttsContentChipY, ttsContentChipWidth, ttsContentChipHeight),
                MicTtsTranslation2 = new RectangleF(rightX + (ttsContentChipWidth + ttsContentChipGap) * 2, ttsContentChipY, ttsContentChipWidth, ttsContentChipHeight),
                MicTtsTranslation3 = new RectangleF(rightX + (ttsContentChipWidth + ttsContentChipGap) * 3, ttsContentChipY, ttsContentChipWidth, ttsContentChipHeight),
                DesktopTtsOriginal = new RectangleF(rightX, ttsContentChipY + ttsContentChipHeight + 38, ttsContentChipWidth, ttsContentChipHeight),
                DesktopTtsTranslation1 = new RectangleF(rightX + (ttsContentChipWidth + ttsContentChipGap), ttsContentChipY + ttsContentChipHeight + 38, ttsContentChipWidth, ttsContentChipHeight),
                DesktopTtsTranslation2 = new RectangleF(rightX + (ttsContentChipWidth + ttsContentChipGap) * 2, ttsContentChipY + ttsContentChipHeight + 38, ttsContentChipWidth, ttsContentChipHeight),
                DesktopTtsTranslation3 = new RectangleF(rightX + (ttsContentChipWidth + ttsContentChipGap) * 3, ttsContentChipY + ttsContentChipHeight + 38, ttsContentChipWidth, ttsContentChipHeight)
            };
        }

        private static SteamVrLanguagePickerLayout BuildLanguagePickerLayout(
            RectangleF settingsRect,
            int optionCount,
            int requestedPageIndex)
        {
            const int columns = 2;
            const float optionHeight = 38f;
            const float columnGap = 12f;
            const float rowGap = 8f;

            var popup = new RectangleF(settingsRect.X + 64, settingsRect.Y + 58, settingsRect.Width - 128, settingsRect.Height - 116);
            var close = new RectangleF(popup.Right - 72, popup.Y + 14, 50, 34);
            var footerY = popup.Bottom - 48;
            var prev = new RectangleF(popup.X + 22, footerY, 86, 34);
            var next = new RectangleF(popup.Right - 108, footerY, 86, 34);
            var page = new RectangleF(prev.Right + 10, footerY, next.X - prev.Right - 20, 34);
            var body = new RectangleF(popup.X + 22, popup.Y + 64, popup.Width - 44, footerY - popup.Y - 76);
            var rows = Math.Max(1, (int)Math.Floor((body.Height + rowGap) / (optionHeight + rowGap)));
            var perPage = Math.Max(1, rows * columns);
            var pageCount = Math.Max(1, (int)Math.Ceiling(Math.Max(0, optionCount) / (double)perPage));
            var pageIndex = Math.Clamp(requestedPageIndex, 0, pageCount - 1);
            var start = pageIndex * perPage;
            var visible = Math.Min(perPage, Math.Max(0, optionCount - start));
            var optionWidth = (body.Width - columnGap) / columns;

            var layout = new SteamVrLanguagePickerLayout
            {
                Popup = popup,
                Close = close,
                Prev = prev,
                Next = next,
                Page = page,
                PageIndex = pageIndex,
                StartIndex = start,
                PageCount = pageCount
            };

            for (var i = 0; i < visible; i++)
            {
                var row = i / columns;
                var column = i % columns;
                layout.OptionRects.Add(new RectangleF(
                    body.X + column * (optionWidth + columnGap),
                    body.Y + row * (optionHeight + rowGap),
                    optionWidth,
                    optionHeight));
            }

            return layout;
        }

        private static EVROverlayError RenderAndUploadPanel(
            ulong overlayHandle,
            SteamVrOverlayPanelState state,
            bool dropdownOpen,
            int scrollIndex,
            int selectedIndex,
            bool drawCursorDotInPanel,
            float cursorU,
            float cursorV,
            bool cursorValid,
            string lastText,
            bool settingsOpen,
            SteamVrOverlayAnchorMode anchorMode,
            SteamVrTrackingDeviceMode trackingDeviceMode,
            int hand,
            float panelSize,
            bool startupAutoLaunch,
            bool photoPanelOpen,
            bool panelLocked,
            bool unlockConfirmPending,
            SteamVrPanelText text,
            SteamVrLanguageRouteSnapshot languageRoutes,
            SteamVrLanguagePickerTarget languagePickerTarget,
            IReadOnlyList<SteamVrLanguagePickerOption> languagePickerOptions,
            int languagePickerPageIndex)
        {
            using var bmp = new Bitmap(PanelWidth, PanelHeight, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var nonTextOpacity = Math.Clamp(state.NonTextOpacity, 0f, 1f);
            var fontScale = Math.Clamp(state.FontScale, 0.70f, 1.40f);
            var micTextColor = ParsePanelTextColor(state.MicTextColor, AppConfig.DefaultSteamVrMicTextColor);
            var desktopTextColor = ParsePanelTextColor(state.DesktopTextColor, AppConfig.DefaultSteamVrDesktopTextColor);

            // Background
            using (var bg = new SolidBrush(WithNonTextOpacity(110, 0, 0, 0, nonTextOpacity)))
            {
                FillRoundedRect(g, bg, new RectangleF(0, 0, PanelWidth, PanelHeight), 18);
            }

            using var titleFont = new Font("Microsoft YaHei UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelFont = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
            using var smallFont = new Font("Microsoft YaHei UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var boldFont = new Font("Microsoft YaHei UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var transcriptFont = new Font("Microsoft YaHei UI", BaseTranscriptFontSize * fontScale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var chipFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);

            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(200, 220, 220, 220));

            // Header
            g.DrawString(settingsOpen ? text.Get("SettingsTitle") : "VRCLS", titleFont, white, new PointF(20, 18));
            if (!string.IsNullOrWhiteSpace(AppVersionLabel))
            {
                g.DrawString(AppVersionLabel, smallFont, dim, new PointF(24, 50));
            }

            var header = BuildPanelHeaderLayout();
            var micRect = header.Mic;
            var deskRect = header.Desktop;
            var photoRect = header.Photo;
            var dropRect = header.History;
            var lockRect = header.LockButton;
            var gearRect = header.Settings;

            var items = state.Transcripts ?? Array.Empty<SteamVrOverlayTranscript>();
            var selected = (selectedIndex >= 0 && selectedIndex < items.Count) ? items[selectedIndex] : null;
            var dropText = selected == null ? text.Get("HistoryEmpty") : $"{text.Get("HistoryPrefix")}: {selected.Timestamp:HH:mm:ss}";

            var languageMask = state.LanguageMask == TranscriptLanguageMask.None
                ? (TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1)
                : state.LanguageMask;

            if (cursorValid)
            {
                var hoverX = cursorU * PanelWidth;
                var hoverY = cursorV * PanelHeight;
                if (!panelLocked)
                {
                    DrawHoverHighlight(g, micRect, hoverX, hoverY, nonTextOpacity);
                    DrawHoverHighlight(g, deskRect, hoverX, hoverY, nonTextOpacity);
                    DrawHoverHighlight(g, photoRect, hoverX, hoverY, nonTextOpacity);
                    DrawHoverHighlight(g, gearRect, hoverX, hoverY, nonTextOpacity);
                }
                DrawHoverHighlight(g, lockRect, hoverX, hoverY, nonTextOpacity);
            }

            // Toggle buttons: icon + dot. Source colors match the main window converter.
            DrawToggleIconDot(g, micRect, state.IsMicListening, ToggleIcon.Mic, SourceMicColor, ToggleOffColor, nonTextOpacity);
            DrawToggleIconDot(g, deskRect, state.IsDesktopListening, ToggleIcon.Desktop, SourceDesktopColor, ToggleOffColor, nonTextOpacity);
            var photoDotColor = photoPanelOpen
                ? ToggleOpenColor
                : (state.IsPhotoTranslationArmed || state.IsPhotoTranslationProcessing)
                    ? SourcePhotoColor
                    : ToggleOffColor;
            DrawToggleIconDot(g, photoRect, ToggleIcon.Photo, photoDotColor, nonTextOpacity);

            // Dropdown header (shows selected record time)
            DrawButton(g, dropRect, dropText, labelFont, nonTextOpacity);
            DrawLockButton(g, lockRect, panelLocked, unlockConfirmPending, nonTextOpacity);
            DrawButton(g, gearRect, settingsOpen ? text.Get("Back") : text.Get("Set"), labelFont, nonTextOpacity);

            // Transcript list (paged/scrollable) + dropdown history popup.
            var listY = 82f;
            var listRect = new RectangleF(16, listY, PanelWidth - 32, PanelHeight - listY - 16);
            using (var listBg = new SolidBrush(WithNonTextOpacity(60, 255, 255, 255, nonTextOpacity)))
            {
                FillRoundedRect(g, listBg, listRect, 14);
            }

            // Page buttons (Top/Up/Down). "Top" only shows when not at top.
            const float btnW = 36f;
            const float btnH = 36f;
            var btnX = listRect.Right - btnW - 10;
            var btnTop = new RectangleF(btnX, listRect.Y + 12, btnW, btnH);
            var btnUp = new RectangleF(btnX, btnTop.Bottom + 10, btnW, btnH);
            var btnDown = new RectangleF(btnX, btnUp.Bottom + 10, btnW, btnH);

            // Reserve space for buttons on the right.
            var contentRect = new RectangleF(listRect.X + 10, listRect.Y + 10, listRect.Width - 20 - (btnW + 12), listRect.Height - 20);

            if (items.Count == 0)
            {
                var txt = string.IsNullOrWhiteSpace(lastText) ? text.Get("NoTranscriptYet") : lastText;
                DrawAutoFitText(g, txt, contentRect, transcriptFont, Color.White, minFontSize: 8f);
            }
            else
            {
                scrollIndex = Math.Clamp(scrollIndex, 0, Math.Max(0, items.Count - 1));
                var rows = BuildVisibleTranscriptRows(g, items, scrollIndex, contentRect, languageMask, transcriptFont, 4f, micTextColor, desktopTextColor);
                var visibleCount = Math.Max(1, rows.Count);
                var maxScroll = Math.Max(0, items.Count - 1);
                scrollIndex = Math.Clamp(scrollIndex, 0, maxScroll);
                var atBottom = rows.Count == 0 || rows[rows.Count - 1].Index >= items.Count - 1;

                var scrollbarRect = new RectangleF(contentRect.Right + 2, contentRect.Y, 8, contentRect.Height);
                DrawScrollbar(g, scrollbarRect, scrollIndex, visibleCount, items.Count, maxScroll, atBottom, nonTextOpacity);

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var idx = row.Index;
                    var item = items[idx];
                    var rowRect = row.RowRect;

                    var isSelected = idx == selectedIndex;
                    if (isSelected)
                    {
                        using var sel = new SolidBrush(WithNonTextOpacity(90, 255, 255, 255, nonTextOpacity));
                        FillRoundedRect(g, sel, rowRect, 10);
                    }

                    // Source color bar: keep in sync with the main window SourceToColorConverter.
                    var barColor = GetTranscriptSourceColor(item.Source);
                    using (var bar = new SolidBrush(WithNonTextOpacity(barColor, nonTextOpacity)))
                    {
                        FillRoundedRect(g, bar, new RectangleF(rowRect.X + 2, rowRect.Y + 8, 4, rowRect.Height - 16), 2);
                    }

                    var textX = rowRect.X + 14;
                    var blocks = row.Blocks;
                    var textRect = new RectangleF(textX, rowRect.Y + 6, rowRect.Width - 18, rowRect.Height - 12);
                    DrawTranscriptBlocks(g, blocks, textRect, transcriptFont, minFontSize: 8f, lineSpacing: 4f);
                }

                var atTop = scrollIndex <= 0;

                if (!atTop)
                {
                    DrawIconButton(g, btnTop, IconButtonKind.Top, enabled: true, nonTextOpacity);
                }
                DrawIconButton(g, btnUp, IconButtonKind.PageUp, enabled: !atTop, nonTextOpacity);
                DrawIconButton(g, btnDown, IconButtonKind.PageDown, enabled: !atBottom, nonTextOpacity);
            }

            // Dropdown list (history) overlayed on top of the list.
            if (dropdownOpen && items.Count > 0)
            {
                const int maxDropItems = 10;
                var visible = Math.Min(maxDropItems, items.Count);
                var start = Math.Clamp(scrollIndex, 0, Math.Max(0, items.Count - visible));
                var dropListRect = new RectangleF(dropRect.X, dropRect.Bottom + 6, dropRect.Width, visible * 34 + 8);
                using var dropBg = new SolidBrush(WithNonTextOpacity(210, 20, 20, 20, nonTextOpacity));
                FillRoundedRect(g, dropBg, dropListRect, 12);

                for (var i = 0; i < visible; i++)
                {
                    var idx = start + i;
                    var r = new RectangleF(dropListRect.X + 8, dropListRect.Y + 4 + i * 34, dropListRect.Width - 16, 30);
                    if (idx == selectedIndex)
                    {
                        using var sel = new SolidBrush(WithNonTextOpacity(90, 255, 255, 255, nonTextOpacity));
                        FillRoundedRect(g, sel, r, 8);
                    }

                    var it = items[idx];
                    var preview = BuildCombinedText(it, languageMask);
                    var previewText = $"{it.Timestamp:HH:mm:ss}  {preview}";
                    var previewRect = new RectangleF(r.X + 8, r.Y + 6, r.Width - 16, r.Height - 8);
                    DrawAutoFitText(g, previewText, previewRect, smallFont, GetTranscriptTextColor(it.Source, micTextColor, desktopTextColor), minFontSize: 9f);
                }
            }

            if (settingsOpen)
            {
                var settingsRect = new RectangleF(16, listY, PanelWidth - 48, PanelHeight - listY - 16);
                using var settingsBg = new SolidBrush(WithNonTextOpacity(225, 18, 18, 18, nonTextOpacity));
                FillRoundedRect(g, settingsBg, settingsRect, 16);

                var layout = BuildSettingsPanelLayout(settingsRect);

                g.DrawString(text.Get("PanelPlacementTitle"), boldFont, white, new PointF(settingsRect.X + 24, settingsRect.Y + 18));
                g.DrawString(text.Get("PanelPlacementHint"), smallFont, dim, new RectangleF(settingsRect.X + 24, settingsRect.Y + 46, 330, 40));

                DrawFilterChip(g, layout.AnchorPalm, text.Get("Palm"), anchorMode == SteamVrOverlayAnchorMode.Palm, chipFont, nonTextOpacity);
                DrawFilterChip(g, layout.AnchorBack, text.Get("BackOfHand"), anchorMode == SteamVrOverlayAnchorMode.BackOfHand, chipFont, nonTextOpacity);
                DrawFilterChip(g, layout.AnchorHead, text.Get("Head"), anchorMode == SteamVrOverlayAnchorMode.Head, chipFont, nonTextOpacity);

                DrawFilterChip(g, layout.HandRight, text.Get("RightHand"), hand == 0, chipFont, nonTextOpacity);
                DrawFilterChip(g, layout.HandLeft, text.Get("LeftHand"), hand == 1, chipFont, nonTextOpacity);

                DrawFilterChip(g, layout.TrackingAuto, text.Get("Auto"), trackingDeviceMode == SteamVrTrackingDeviceMode.Auto, chipFont, nonTextOpacity);
                DrawFilterChip(g, layout.TrackingController, text.Get("Controller"), trackingDeviceMode == SteamVrTrackingDeviceMode.Controller, chipFont, nonTextOpacity);
                DrawFilterChip(g, layout.TrackingHand, text.Get("HandTracking"), trackingDeviceMode == SteamVrTrackingDeviceMode.HandTracking, chipFont, nonTextOpacity);

                DrawFilterChip(g, layout.Startup, text.Get("StartWithSteamVR"), startupAutoLaunch, chipFont, nonTextOpacity);

                DrawButton(g, layout.SizeMinus, "-", boldFont, nonTextOpacity);
                DrawButton(g, layout.SizeValue, $"{panelSize:0.00}m", labelFont, nonTextOpacity);
                DrawButton(g, layout.SizePlus, "+", boldFont, nonTextOpacity);

                DrawButton(g, layout.FontMinus, "-", boldFont, nonTextOpacity);
                DrawButton(g, layout.FontValue, $"{text.Get("TextScale")} {fontScale:0.00}x", labelFont, nonTextOpacity);
                DrawButton(g, layout.FontPlus, "+", boldFont, nonTextOpacity);

                DrawButton(g, layout.Reset, text.Get("ResetPosition"), labelFont, nonTextOpacity);

                var rightX = settingsRect.X + 370;
                g.DrawString(text.Get("LanguageRoutesTitle"), boldFont, white, new PointF(rightX, settingsRect.Y + 18));
                g.DrawString(text.Get("LanguageRoutesHint"), smallFont, dim, new RectangleF(rightX, settingsRect.Y + 46, settingsRect.Right - rightX - 24, 40));

                g.DrawString(text.Get("Mic"), chipFont, white, new PointF(rightX, settingsRect.Y + 78));
                DrawButton(g, layout.MicSource, BuildLanguageChipText(text.Get("RecognitionShort"), languageRoutes.MicSource), smallFont, nonTextOpacity);
                DrawButton(g, layout.MicTarget1, BuildLanguageChipText(text.Get("Translation1Short"), languageRoutes.MicTarget1), smallFont, nonTextOpacity);
                DrawButton(g, layout.MicTarget2, BuildLanguageChipText(text.Get("Translation2Short"), languageRoutes.MicTarget2), smallFont, nonTextOpacity);
                DrawButton(g, layout.MicTarget3, BuildLanguageChipText(text.Get("Translation3Short"), languageRoutes.MicTarget3), smallFont, nonTextOpacity);

                g.DrawString(text.Get("Desktop"), chipFont, white, new PointF(rightX, settingsRect.Y + 234));
                DrawButton(g, layout.DesktopSource, BuildLanguageChipText(text.Get("RecognitionShort"), languageRoutes.DesktopSource), smallFont, nonTextOpacity);
                DrawButton(g, layout.DesktopTarget1, BuildLanguageChipText(text.Get("Translation1Short"), languageRoutes.DesktopTarget1), smallFont, nonTextOpacity);
                DrawButton(g, layout.DesktopTarget2, BuildLanguageChipText(text.Get("Translation2Short"), languageRoutes.DesktopTarget2), smallFont, nonTextOpacity);
                DrawButton(g, layout.DesktopTarget3, BuildLanguageChipText(text.Get("Translation3Short"), languageRoutes.DesktopTarget3), smallFont, nonTextOpacity);

                var ttsTitleY = settingsRect.Y + 344f;
                g.DrawString(text.Get("TtsSectionTitle"), boldFont, white, new PointF(rightX, ttsTitleY));
                g.DrawString(text.Get("TtsSectionHint"), smallFont, dim, new RectangleF(rightX, ttsTitleY + 28, settingsRect.Right - rightX - 24, 30));
                DrawFilterChip(g, layout.TtsEnabled, text.Get("TtsEnabledShort"), state.IsTtsEnabled, chipFont, nonTextOpacity);
                DrawFilterChip(g, layout.TtsPassthrough, text.Get("PassthroughShort"), state.IsTtsMicPassthroughEnabled, chipFont, nonTextOpacity);
                DrawButton(g, layout.TtsPassVolumeMinus, "-", boldFont, nonTextOpacity);
                DrawButton(g, layout.TtsPassVolumeValue, $"{text.Get("PassthroughVolumeShort")} {Math.Clamp(state.TtsMicPassthroughVolume, 0f, 4f):0.00}x", smallFont, nonTextOpacity);
                DrawButton(g, layout.TtsPassVolumePlus, "+", boldFont, nonTextOpacity);

                g.DrawString(text.Get("MicTtsContent"), smallFont, dim, new PointF(rightX, layout.MicTtsOriginal.Y - 24));
                DrawFilterChip(g, layout.MicTtsOriginal, text.Get("Original"), (state.MicTtsSpeakMask & 1) != 0, smallFont, nonTextOpacity);
                DrawFilterChip(g, layout.MicTtsTranslation1, text.Get("Translation1Short"), (state.MicTtsSpeakMask & 2) != 0, smallFont, nonTextOpacity);
                DrawFilterChip(g, layout.MicTtsTranslation2, text.Get("Translation2Short"), (state.MicTtsSpeakMask & 4) != 0, smallFont, nonTextOpacity);
                DrawFilterChip(g, layout.MicTtsTranslation3, text.Get("Translation3Short"), (state.MicTtsSpeakMask & 8) != 0, smallFont, nonTextOpacity);

                g.DrawString(text.Get("DesktopTtsContent"), smallFont, dim, new PointF(rightX, layout.DesktopTtsOriginal.Y - 24));
                DrawFilterChip(g, layout.DesktopTtsOriginal, text.Get("Original"), (state.DesktopTtsSpeakMask & 1) != 0, smallFont, nonTextOpacity);
                DrawFilterChip(g, layout.DesktopTtsTranslation1, text.Get("Translation1Short"), (state.DesktopTtsSpeakMask & 2) != 0, smallFont, nonTextOpacity);
                DrawFilterChip(g, layout.DesktopTtsTranslation2, text.Get("Translation2Short"), (state.DesktopTtsSpeakMask & 4) != 0, smallFont, nonTextOpacity);
                DrawFilterChip(g, layout.DesktopTtsTranslation3, text.Get("Translation3Short"), (state.DesktopTtsSpeakMask & 8) != 0, smallFont, nonTextOpacity);

                using var hintBg = new SolidBrush(WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity));
                var hintRect = new RectangleF(settingsRect.X + 24, layout.Reset.Bottom + 18, 306, 72);
                FillRoundedRect(g, hintBg, hintRect, 12);
                g.DrawString(text.Get("SettingsDragHint"), smallFont, white, new RectangleF(hintRect.X + 14, hintRect.Y + 12, hintRect.Width - 28, hintRect.Height - 24));

                if (languagePickerTarget != SteamVrLanguagePickerTarget.None)
                {
                    DrawLanguagePickerPopup(
                        g,
                        settingsRect,
                        text,
                        languagePickerTarget,
                        languagePickerOptions,
                        languagePickerPageIndex,
                        labelFont,
                        smallFont,
                        chipFont,
                        nonTextOpacity);
                }
            }

            if (panelLocked)
            {
                DrawPanelLockOverlay(g, listRect, unlockConfirmPending, text, smallFont, boldFont, nonTextOpacity);
            }

            return UploadBitmap(overlayHandle, bmp);
        }

        private static EVROverlayError RenderAndUploadPhotoPanel(
            ulong overlayHandle,
            SteamVrOverlayPanelState state)
        {
            using var bmp = new Bitmap(PhotoPanelWidth * PhotoPanelTextureScale, PhotoPanelHeight * PhotoPanelTextureScale, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.ScaleTransform(PhotoPanelTextureScale, PhotoPanelTextureScale);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var nonTextOpacity = Math.Clamp(state.NonTextOpacity, 0f, 1f);
            var fontScale = Math.Clamp(state.FontScale, 0.70f, 1.40f);
            var text = SteamVrPanelText.Create(state.UiLanguage);
            var items = state.Transcripts ?? Array.Empty<SteamVrOverlayTranscript>();
            var currentIndex = items.Count == 0
                ? -1
                : Math.Clamp(state.PhotoPanelIndex, 0, items.Count - 1);
            var currentItem = currentIndex >= 0 ? items[currentIndex] : null;

            using (var bg = new SolidBrush(WithNonTextOpacity(112, 0, 0, 0, nonTextOpacity)))
            {
                FillRoundedRect(g, bg, new RectangleF(0, 0, PhotoPanelWidth, PhotoPanelHeight), 18);
            }

            using var titleFont = new Font("Microsoft YaHei UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelFont = new Font("Microsoft YaHei UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
            using var smallFont = new Font("Microsoft YaHei UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
            using var transcriptFont = new Font("Microsoft YaHei UI", BaseTranscriptFontSize * fontScale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var chipFont = new Font("Microsoft YaHei UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);

            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(205, 220, 220, 220));

            g.DrawString(text.Get("PhotoTranslationTitle"), titleFont, white, new PointF(20, 16));
            var statusRect = new RectangleF(PhotoPanelWidth - 158, 14, 136, 38);
            var statusText = state.IsPhotoTranslationProcessing
                ? text.Get("Processing")
                : state.IsPhotoTranslationArmed
                    ? text.Get("Ready")
                    : items.Count > 0
                        ? text.Get("Result")
                        : text.Get("Photo");
            DrawFilterChip(g, statusRect, statusText, state.IsPhotoTranslationProcessing || state.IsPhotoTranslationArmed || items.Count > 0, chipFont, nonTextOpacity);

            var subtitle = items.Count > 0
                ? $"{currentIndex + 1}/{items.Count}  {currentItem!.Timestamp:HH:mm:ss}"
                : state.IsPhotoTranslationProcessing
                    ? text.Get("PhotoProcessingCaptured")
                    : state.IsPhotoTranslationArmed ? text.Get("PhotoWaitingCapture") : text.Get("PhotoNoResult");
            g.DrawString(subtitle, smallFont, dim, new PointF(22, 46));

            var contentRect = GetPhotoPanelContentRect();
            using (var contentBg = new SolidBrush(WithNonTextOpacity(62, 255, 255, 255, nonTextOpacity)))
            {
                FillRoundedRect(g, contentBg, contentRect, 14);
            }

            if (items.Count == 0)
            {
                var txt = state.IsPhotoTranslationProcessing
                    ? text.Get("PhotoProcessing")
                    : state.IsPhotoTranslationArmed ? text.Get("PhotoWaiting") : text.Get("PhotoNone");
                DrawAutoFitText(g, txt, new RectangleF(contentRect.X + 16, contentRect.Y + 16, contentRect.Width - 32, contentRect.Height - 32), transcriptFont, Color.White, minFontSize: 9f);
                return UploadBitmap(overlayHandle, bmp);
            }

            var navigation = GetPhotoPanelNavigationLayout(contentRect);
            DrawIconButton(g, navigation.LeftButton, IconButtonKind.PageLeft, items.Count > 1, nonTextOpacity);
            DrawIconButton(g, navigation.RightButton, IconButtonKind.PageRight, items.Count > 1, nonTextOpacity);

            var imageLabel = currentIndex == 0
                ? $"{text.Get("Latest")} {currentItem!.Timestamp:HH:mm:ss}"
                : currentItem!.Timestamp.ToString("HH:mm:ss");
            if (!TryDrawPhotoImage(g, currentItem, navigation.PageRect, nonTextOpacity, smallFont, dim, imageLabel))
            {
                DrawPhotoTextPage(g, currentItem, navigation.PageRect, nonTextOpacity, labelFont, transcriptFont, state.PhotoPanelTextScrollOffset, text);
            }

            return UploadBitmap(overlayHandle, bmp);
        }

        private static RectangleF GetPhotoPanelContentRect()
        {
            return new RectangleF(18, 76, PhotoPanelWidth - 36, PhotoPanelHeight - 94);
        }

        private static (RectangleF LeftButton, RectangleF RightButton, RectangleF PageRect) GetPhotoPanelNavigationLayout(RectangleF contentRect)
        {
            const float arrowWidth = 46f;
            const float arrowHeight = 68f;
            const float arrowInset = 10f;
            const float pageInsetX = 68f;
            const float pageInsetY = 14f;

            var leftButton = new RectangleF(
                contentRect.X + arrowInset,
                contentRect.Y + (contentRect.Height - arrowHeight) * 0.5f,
                arrowWidth,
                arrowHeight);
            var rightButton = new RectangleF(
                contentRect.Right - arrowInset - arrowWidth,
                contentRect.Y + (contentRect.Height - arrowHeight) * 0.5f,
                arrowWidth,
                arrowHeight);
            var pageRect = new RectangleF(
                contentRect.X + pageInsetX,
                contentRect.Y + pageInsetY,
                contentRect.Width - pageInsetX * 2,
                contentRect.Height - pageInsetY * 2);
            return (leftButton, rightButton, pageRect);
        }

        private static void DrawPhotoTextPage(
            Graphics g,
            SteamVrOverlayTranscript item,
            RectangleF pageRect,
            float nonTextOpacity,
            Font labelFont,
            Font transcriptFont,
            int scrollOffset,
            SteamVrPanelText text)
        {
            const float lineSpacing = 5f;
            var (originalRect, translationRect) = GetPhotoTextColumnRects(pageRect);
            var originalBlocks = string.IsNullOrWhiteSpace(item.Original)
                ? Array.Empty<(string Text, Color Color)>()
                : new[] { (item.Original, Color.FromArgb(235, 230, 230, 230)) };
            var translationBlocks = BuildPhotoTranslationBlocks(item);
            var originalMaxScroll = ComputePhotoTextBlocksMaxScroll(g, originalBlocks, GetPhotoTextColumnTextRect(originalRect), transcriptFont, lineSpacing);
            var translationMaxScroll = ComputePhotoTextBlocksMaxScroll(g, translationBlocks, GetPhotoTextColumnTextRect(translationRect), transcriptFont, lineSpacing);
            var maxScroll = Math.Max(originalMaxScroll, translationMaxScroll);
            scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

            DrawPhotoTextColumn(
                g,
                originalRect,
                text.Get("Original"),
                originalBlocks,
                nonTextOpacity,
                labelFont,
                transcriptFont,
                lineSpacing,
                scrollOffset);
            DrawPhotoTextColumn(
                g,
                translationRect,
                text.Get("Translation"),
                translationBlocks,
                nonTextOpacity,
                labelFont,
                transcriptFont,
                lineSpacing,
                scrollOffset);
        }

        private static void DrawPhotoTextColumn(
            Graphics g,
            RectangleF rect,
            string label,
            (string Text, Color Color)[] blocks,
            float nonTextOpacity,
            Font labelFont,
            Font transcriptFont,
            float lineSpacing,
            int scrollOffset)
        {
            using (var bg = new SolidBrush(WithNonTextOpacity(54, 255, 255, 255, nonTextOpacity)))
            {
                FillRoundedRect(g, bg, rect, 10);
            }

            using (var bar = new SolidBrush(WithNonTextOpacity(GetTranscriptSourceColor("Photo"), nonTextOpacity)))
            {
                FillRoundedRect(g, bar, new RectangleF(rect.X + 5, rect.Y + 10, 4, rect.Height - 20), 2);
            }

            using var dim = new SolidBrush(Color.FromArgb(190, 220, 220, 220));
            g.DrawString(label, labelFont, dim, new RectangleF(rect.X + 16, rect.Y + 10, rect.Width - 28, 24));

            if (blocks.Length == 0)
            {
                return;
            }

            var textRect = GetPhotoTextColumnTextRect(rect);
            DrawScrollableTranscriptBlocks(g, blocks, textRect, transcriptFont, lineSpacing, scrollOffset, nonTextOpacity);
        }

        private static (RectangleF Original, RectangleF Translation) GetPhotoTextColumnRects(RectangleF pageRect)
        {
            const float columnGap = 12f;
            var columnWidth = (pageRect.Width - columnGap) * 0.5f;
            var originalRect = new RectangleF(pageRect.X, pageRect.Y, columnWidth, pageRect.Height);
            var translationRect = new RectangleF(originalRect.Right + columnGap, pageRect.Y, columnWidth, pageRect.Height);
            return (originalRect, translationRect);
        }

        private static RectangleF GetPhotoTextColumnTextRect(RectangleF columnRect)
        {
            return new RectangleF(columnRect.X + 16, columnRect.Y + 42, columnRect.Width - 28, Math.Max(16f, columnRect.Height - 52));
        }

        private static int ComputePhotoTextBlocksMaxScroll(
            Graphics g,
            (string Text, Color Color)[] blocks,
            RectangleF textRect,
            Font font,
            float lineSpacing)
        {
            if (blocks.Length == 0)
            {
                return 0;
            }

            var textWidth = Math.Max(1f, textRect.Width - 10f);
            var contentHeight = MeasureTranscriptBlocksHeight(g, blocks, font, textWidth, lineSpacing);
            return Math.Max(0, (int)Math.Ceiling(contentHeight - textRect.Height));
        }

        private static int ComputePhotoPanelTextMaxScroll(SteamVrOverlayTranscript item, float fontScale)
        {
            if (IsPhotoImageResultDrawable(item))
            {
                return 0;
            }

            const float lineSpacing = 5f;
            var contentRect = GetPhotoPanelContentRect();
            var pageRect = GetPhotoPanelNavigationLayout(contentRect).PageRect;
            var (originalRect, translationRect) = GetPhotoTextColumnRects(pageRect);
            var originalBlocks = string.IsNullOrWhiteSpace(item.Original)
                ? Array.Empty<(string Text, Color Color)>()
                : new[] { (item.Original, Color.FromArgb(235, 230, 230, 230)) };
            var translationBlocks = BuildPhotoTranslationBlocks(item);

            using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var transcriptFont = new Font("Microsoft YaHei UI", BaseTranscriptFontSize * Math.Clamp(fontScale, 0.70f, 1.40f), FontStyle.Bold, GraphicsUnit.Pixel);

            var originalMax = ComputePhotoTextBlocksMaxScroll(g, originalBlocks, GetPhotoTextColumnTextRect(originalRect), transcriptFont, lineSpacing);
            var translationMax = ComputePhotoTextBlocksMaxScroll(g, translationBlocks, GetPhotoTextColumnTextRect(translationRect), transcriptFont, lineSpacing);
            return Math.Max(originalMax, translationMax);
        }

        private static bool IsPhotoImageResultDrawable(SteamVrOverlayTranscript item)
        {
            return string.Equals(item.PhotoTranslationMode, AppConfig.PhotoTranslationModeImage, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.PhotoImagePath)
                && File.Exists(item.PhotoImagePath);
        }

        private static void DrawScrollableTranscriptBlocks(
            Graphics g,
            (string Text, Color Color)[] blocks,
            RectangleF textRect,
            Font font,
            float lineSpacing,
            int scrollOffset,
            float nonTextOpacity)
        {
            if (blocks.Length == 0)
            {
                return;
            }

            var textWidth = Math.Max(1f, textRect.Width - 10f);
            var contentHeight = MeasureTranscriptBlocksHeight(g, blocks, font, textWidth, lineSpacing);
            var maxScroll = Math.Max(0, (int)Math.Ceiling(contentHeight - textRect.Height));
            var effectiveOffset = Math.Clamp(scrollOffset, 0, maxScroll);
            var state = g.Save();
            g.SetClip(textRect);

            var y = textRect.Y - effectiveOffset;
            for (var i = 0; i < blocks.Length; i++)
            {
                var block = blocks[i];
                var h = MeasureTextHeight(g, block.Text, font, textWidth);
                var drawRect = new RectangleF(textRect.X, y, textWidth, h);
                if (drawRect.Bottom >= textRect.Y && drawRect.Y <= textRect.Bottom)
                {
                    DrawTextWithShadow(g, block.Text, font, drawRect, block.Color);
                }
                y += h;
                if (i < blocks.Length - 1)
                {
                    y += lineSpacing;
                }
            }

            g.Restore(state);

            if (maxScroll > 0)
            {
                var scrollbarRect = new RectangleF(textRect.Right - 5f, textRect.Y, 5f, textRect.Height);
                DrawPhotoTextScrollIndicator(g, scrollbarRect, effectiveOffset, maxScroll, nonTextOpacity);
            }
        }

        private static void DrawPhotoTextScrollIndicator(
            Graphics g,
            RectangleF trackRect,
            int scrollOffset,
            int maxScroll,
            float nonTextOpacity)
        {
            using var track = new SolidBrush(WithNonTextOpacity(45, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, track, trackRect, 3);

            var thumbHeight = Math.Max(28f, trackRect.Height * trackRect.Height / (trackRect.Height + maxScroll));
            var travel = Math.Max(0f, trackRect.Height - thumbHeight);
            var t = maxScroll <= 0 ? 0f : Math.Clamp(scrollOffset, 0, maxScroll) / (float)maxScroll;
            var thumbRect = new RectangleF(trackRect.X, trackRect.Y + (travel * t), trackRect.Width, thumbHeight);
            using var thumb = new SolidBrush(WithNonTextOpacity(160, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, thumb, thumbRect, 3);
        }

        private static (string Text, Color Color)[] BuildPhotoTranslationBlocks(SteamVrOverlayTranscript item)
        {
            var blocks = new List<(string Text, Color Color)>(3);
            if (!string.IsNullOrWhiteSpace(item.Translation))
            {
                blocks.Add((item.Translation, Color.White));
            }
            if (!string.IsNullOrWhiteSpace(item.Translation2))
            {
                blocks.Add((item.Translation2, Color.FromArgb(235, 240, 240, 240)));
            }
            if (!string.IsNullOrWhiteSpace(item.Translation3))
            {
                blocks.Add((item.Translation3, Color.FromArgb(235, 240, 240, 240)));
            }
            return blocks.ToArray();
        }

        private static bool TryDrawPhotoImage(
            Graphics g,
            SteamVrOverlayTranscript item,
            RectangleF contentRect,
            float nonTextOpacity,
            Font metaFont,
            Brush metaBrush,
            string label)
        {
            if (!string.Equals(item.PhotoTranslationMode, AppConfig.PhotoTranslationModeImage, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.PhotoImagePath)
                || !File.Exists(item.PhotoImagePath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(item.PhotoImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var image = Image.FromStream(stream);
                var frameRect = GetPhotoImageFrameRect(contentRect);
                using (var frameBg = new SolidBrush(WithNonTextOpacity(64, 0, 0, 0, nonTextOpacity)))
                {
                    FillRoundedRect(g, frameBg, frameRect, 12);
                }

                var imageRect = GetPhotoImageRect(contentRect, image.Width, image.Height);
                var oldState = g.Save();
                using (var clipPath = CreateRoundedRectPath(imageRect, 8))
                {
                    g.SetClip(clipPath);
                    g.DrawImage(image, imageRect);
                }
                g.Restore(oldState);

                var metaRect = new RectangleF(frameRect.X + 12, frameRect.Bottom - 28, frameRect.Width - 24, 20);
                g.DrawString(label, metaFont, metaBrush, metaRect);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to render SteamVR photo translation image. path={ImagePath}", item.PhotoImagePath);
                return false;
            }
        }

        private static RectangleF GetPhotoImageFrameRect(RectangleF contentRect)
        {
            return new RectangleF(contentRect.X + 12, contentRect.Y + 12, contentRect.Width - 24, contentRect.Height - 24);
        }

        private static RectangleF GetPhotoImageBoundsRect(RectangleF frameRect)
        {
            return new RectangleF(frameRect.X + 8, frameRect.Y + 8, frameRect.Width - 16, frameRect.Height - 42);
        }

        private static RectangleF GetPhotoImageRect(RectangleF contentRect, float sourceWidth, float sourceHeight)
        {
            return ComputeUniformFitRect(sourceWidth, sourceHeight, GetPhotoImageBoundsRect(GetPhotoImageFrameRect(contentRect)));
        }

        private static RectangleF ComputeUniformFitRect(float sourceWidth, float sourceHeight, RectangleF bounds)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return bounds;
            }

            var scale = Math.Min(bounds.Width / sourceWidth, bounds.Height / sourceHeight);
            var width = sourceWidth * scale;
            var height = sourceHeight * scale;
            return new RectangleF(
                bounds.X + (bounds.Width - width) / 2f,
                bounds.Y + (bounds.Height - height) / 2f,
                width,
                height);
        }

        private static List<TranscriptRowLayout> BuildVisibleTranscriptRows(
            Graphics g,
            IReadOnlyList<SteamVrOverlayTranscript> items,
            int scrollIndex,
            RectangleF contentRect,
            TranscriptLanguageMask languageMask,
            Font transcriptFont,
            float lineSpacing,
            Color micTextColor,
            Color desktopTextColor)
        {
            var rows = new List<TranscriptRowLayout>();
            if (items.Count == 0 || contentRect.Width <= 1f || contentRect.Height <= 1f)
            {
                return rows;
            }

            const float rowGap = 8f;
            scrollIndex = Math.Clamp(scrollIndex, 0, items.Count - 1);
            var y = contentRect.Y;
            var textWidth = Math.Max(1f, contentRect.Width - 18f);

            for (var idx = scrollIndex; idx < items.Count; idx++)
            {
                var sourceTextColor = GetTranscriptTextColor(items[idx].Source, micTextColor, desktopTextColor);
                var blocks = BuildTranscriptBlocks(items[idx], languageMask, sourceTextColor);
                if (blocks.Length == 0)
                {
                    blocks = BuildTranscriptBlocks(items[idx], TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1, sourceTextColor);
                }
                if (blocks.Length == 0)
                {
                    continue;
                }

                var rowHeight = ComputeTranscriptRowHeight(g, blocks, transcriptFont, textWidth, contentRect.Height, lineSpacing);
                if (rows.Count > 0 && y + rowHeight > contentRect.Bottom + 0.5f)
                {
                    break;
                }

                rowHeight = rows.Count == 0
                    ? Math.Min(rowHeight, contentRect.Height)
                    : Math.Min(rowHeight, Math.Max(0f, contentRect.Bottom - y));
                if (rowHeight < 1f)
                {
                    break;
                }

                rows.Add(new TranscriptRowLayout(idx, new RectangleF(contentRect.X, y, contentRect.Width, rowHeight), blocks));
                y += rowHeight + rowGap;
                if (y >= contentRect.Bottom - 1f)
                {
                    break;
                }
            }

            return rows;
        }

        private static float ComputeTranscriptRowHeight(
            Graphics g,
            (string Text, Color Color)[] blocks,
            Font font,
            float textWidth,
            float contentHeight,
            float lineSpacing)
        {
            const float verticalPadding = 12f;
            const float minRowHeight = 86f;

            var measured = MeasureTranscriptBlocksHeight(g, blocks, font, textWidth, lineSpacing);
            var desired = measured + verticalPadding;
            var min = Math.Min(minRowHeight, contentHeight);
            return Math.Clamp(desired, min, contentHeight);
        }

        private static float MeasureTranscriptBlocksHeight(
            Graphics g,
            (string Text, Color Color)[] blocks,
            Font font,
            float textWidth,
            float lineSpacing)
        {
            var total = 0f;
            for (var i = 0; i < blocks.Length; i++)
            {
                total += MeasureTextHeight(g, blocks[i].Text, font, textWidth);
                if (i < blocks.Length - 1)
                {
                    total += lineSpacing;
                }
            }

            return total;
        }

        private static Color GetTranscriptSourceColor(string source)
        {
            if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return SourceDesktopColor;
            }
            if (string.Equals(source, "Photo", StringComparison.OrdinalIgnoreCase))
            {
                return SourcePhotoColor;
            }
            return SourceMicColor;
        }

        private static Color GetTranscriptTextColor(string source, Color micTextColor, Color desktopTextColor)
        {
            if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return desktopTextColor;
            }
            if (string.Equals(source, "Photo", StringComparison.OrdinalIgnoreCase))
            {
                return Color.White;
            }
            return micTextColor;
        }

        private static Color ParsePanelTextColor(string value, string fallback)
        {
            var normalized = AppConfig.NormalizeHexColor(value, fallback);
            var hex = normalized.StartsWith("#", StringComparison.Ordinal)
                ? normalized.Substring(1)
                : normalized;
            var alpha = 255;
            var offset = 0;
            if (hex.Length == 8)
            {
                alpha = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                offset = 2;
            }

            var red = int.Parse(hex.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var green = int.Parse(hex.Substring(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var blue = int.Parse(hex.Substring(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(alpha, red, green, blue);
        }

        private static (string Text, Color Color)[] BuildTranscriptBlocks(
            SteamVrOverlayTranscript item,
            TranscriptLanguageMask languageMask,
            Color textColor)
        {
            var blocks = new System.Collections.Generic.List<(string Text, Color Color)>(4);

            if ((languageMask & TranscriptLanguageMask.Translation1) != 0 && !string.IsNullOrWhiteSpace(item.Translation))
            {
                blocks.Add((item.Translation, textColor));
            }
            if ((languageMask & TranscriptLanguageMask.Translation2) != 0 && !string.IsNullOrWhiteSpace(item.Translation2))
            {
                blocks.Add((item.Translation2, textColor));
            }
            if ((languageMask & TranscriptLanguageMask.Translation3) != 0 && !string.IsNullOrWhiteSpace(item.Translation3))
            {
                blocks.Add((item.Translation3, textColor));
            }
            if ((languageMask & TranscriptLanguageMask.Original) != 0 && !string.IsNullOrWhiteSpace(item.Original))
            {
                blocks.Add((item.Original, textColor));
            }

            return blocks.ToArray();
        }

        private static TranscriptLanguageMask BuildPhotoTranslationLanguageMask(TranscriptLanguageMask languageMask)
        {
            var translationsOnly = languageMask & (TranscriptLanguageMask.Translation1 | TranscriptLanguageMask.Translation2 | TranscriptLanguageMask.Translation3);
            return translationsOnly == TranscriptLanguageMask.None
                ? (TranscriptLanguageMask.Translation1 | TranscriptLanguageMask.Translation2 | TranscriptLanguageMask.Translation3)
                : translationsOnly;
        }

        private static string BuildCombinedText(SteamVrOverlayTranscript item, TranscriptLanguageMask languageMask)
        {
            var parts = new List<string>(4);
            if ((languageMask & TranscriptLanguageMask.Translation1) != 0 && !string.IsNullOrWhiteSpace(item.Translation))
            {
                parts.Add(item.Translation);
            }
            if ((languageMask & TranscriptLanguageMask.Translation2) != 0 && !string.IsNullOrWhiteSpace(item.Translation2))
            {
                parts.Add(item.Translation2);
            }
            if ((languageMask & TranscriptLanguageMask.Translation3) != 0 && !string.IsNullOrWhiteSpace(item.Translation3))
            {
                parts.Add(item.Translation3);
            }
            if ((languageMask & TranscriptLanguageMask.Original) != 0 && !string.IsNullOrWhiteSpace(item.Original))
            {
                parts.Add(item.Original);
            }

            return parts.Count == 0 ? string.Empty : string.Join(" | ", parts);
        }

        private static string BuildLanguageChipText(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return label;
            }

            return $"{label}: {value}";
        }

        private static void DrawTranscriptBlocks(
            Graphics g,
            (string Text, Color Color)[] blocks,
            RectangleF rect,
            Font baseFont,
            float minFontSize,
            float lineSpacing)
        {
            if (blocks.Length == 0) return;

            var fontSize = FindBestFontSize(g, blocks.Select(x => x.Text).ToArray(), rect, baseFont, minFontSize, lineSpacing);
            using var font = new Font(baseFont.FontFamily, fontSize, baseFont.Style, GraphicsUnit.Pixel);

            var y = rect.Y;
            for (var i = 0; i < blocks.Length; i++)
            {
                var block = blocks[i];
                var h = MeasureTextHeight(g, block.Text, font, rect.Width);
                var drawRect = new RectangleF(rect.X, y, rect.Width, h);
                DrawTextWithShadow(g, block.Text, font, drawRect, block.Color);
                y += h;
                if (i < blocks.Length - 1)
                {
                    y += lineSpacing;
                }
            }
        }

        private static void DrawAutoFitText(
            Graphics g,
            string text,
            RectangleF rect,
            Font baseFont,
            Color color,
            float minFontSize)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var fontSize = FindBestFontSize(g, new[] { text }, rect, baseFont, minFontSize, lineSpacing: 0f);
            using var font = new Font(baseFont.FontFamily, fontSize, baseFont.Style, GraphicsUnit.Pixel);
            DrawTextWithShadow(g, text, font, rect, color);
        }

        private static float FindBestFontSize(
            Graphics g,
            string[] lines,
            RectangleF rect,
            Font baseFont,
            float minFontSize,
            float lineSpacing)
        {
            var maxSize = baseFont.Size;
            for (var size = maxSize; size >= minFontSize; size -= 1f)
            {
                using var probe = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                var totalHeight = 0f;
                for (var i = 0; i < lines.Length; i++)
                {
                    totalHeight += MeasureTextHeight(g, lines[i], probe, rect.Width);
                    if (i < lines.Length - 1)
                    {
                        totalHeight += lineSpacing;
                    }
                }

                if (totalHeight <= rect.Height)
                {
                    return size;
                }
            }

            using (var probe = new Font(baseFont.FontFamily, minFontSize, baseFont.Style, GraphicsUnit.Pixel))
            {
                var totalHeight = 0f;
                for (var i = 0; i < lines.Length; i++)
                {
                    totalHeight += MeasureTextHeight(g, lines[i], probe, rect.Width);
                    if (i < lines.Length - 1)
                    {
                        totalHeight += lineSpacing;
                    }
                }

                if (totalHeight > rect.Height && totalHeight > 0.1f)
                {
                    var scale = rect.Height / totalHeight;
                    return Math.Max(6f, minFontSize * scale);
                }
            }

            return minFontSize;
        }

        private static float MeasureTextHeight(Graphics g, string text, Font font, float width)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0f;
            using var sf = new StringFormat
            {
                Trimming = StringTrimming.None,
                FormatFlags = 0
            };
            var measured = g.MeasureString(text, font, new SizeF(Math.Max(1f, width), 10000f), sf);
            return (float)Math.Ceiling(measured.Height);
        }

        private static int ScaleNonTextAlpha(int alpha, float opacity)
        {
            return (int)Math.Round(Math.Clamp(alpha, 0, 255) * Math.Clamp(opacity, 0f, 1f));
        }

        private static Color WithNonTextOpacity(int alpha, int red, int green, int blue, float opacity)
        {
            return Color.FromArgb(ScaleNonTextAlpha(alpha, opacity), red, green, blue);
        }

        private static Color WithNonTextOpacity(Color color, float opacity)
        {
            return Color.FromArgb(ScaleNonTextAlpha(color.A, opacity), color.R, color.G, color.B);
        }

        private static void DrawLanguagePickerPopup(
            Graphics g,
            RectangleF settingsRect,
            SteamVrPanelText text,
            SteamVrLanguagePickerTarget target,
            IReadOnlyList<SteamVrLanguagePickerOption> options,
            int pageIndex,
            Font labelFont,
            Font smallFont,
            Font chipFont,
            float nonTextOpacity)
        {
            var layout = BuildLanguagePickerLayout(settingsRect, options.Count, pageIndex);

            using (var scrim = new SolidBrush(WithNonTextOpacity(135, 0, 0, 0, nonTextOpacity)))
            {
                FillRoundedRect(g, scrim, settingsRect, 16);
            }

            using (var bg = new SolidBrush(WithNonTextOpacity(238, 20, 20, 20, nonTextOpacity)))
            {
                FillRoundedRect(g, bg, layout.Popup, 14);
            }

            using (var border = new Pen(WithNonTextOpacity(145, 255, 255, 255, nonTextOpacity), 1.5f))
            {
                DrawRoundedRectOutline(g, border, layout.Popup, 14);
            }

            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(205, 220, 220, 220));
            var title = $"{text.Get("SelectLanguageTitle")} - {GetLanguagePickerTargetLabel(text, target)}";
            g.DrawString(title, labelFont, white, new RectangleF(layout.Popup.X + 22, layout.Popup.Y + 17, layout.Close.X - layout.Popup.X - 30, 32));
            DrawButton(g, layout.Close, text.Get("Close"), smallFont, nonTextOpacity);

            if (options.Count == 0)
            {
                var emptyRect = new RectangleF(layout.Popup.X + 24, layout.Popup.Y + 92, layout.Popup.Width - 48, 60);
                DrawAutoFitText(g, text.Get("NoLanguageOptions"), emptyRect, labelFont, Color.White, minFontSize: 9f);
            }
            else
            {
                for (var i = 0; i < layout.OptionRects.Count; i++)
                {
                    var optionIndex = layout.StartIndex + i;
                    if (optionIndex < 0 || optionIndex >= options.Count)
                    {
                        continue;
                    }

                    var option = options[optionIndex];
                    var display = string.IsNullOrWhiteSpace(option.DisplayName) ? option.Code.ToUpperInvariant() : option.DisplayName;
                    if (option.IsSelected)
                    {
                        DrawFilterChip(g, layout.OptionRects[i], display, true, chipFont, nonTextOpacity);
                    }
                    else
                    {
                        DrawButton(g, layout.OptionRects[i], display, chipFont, nonTextOpacity);
                    }
                }
            }

            DrawPagerButton(g, layout.Prev, text.Get("PreviousPage"), layout.PageIndex > 0, smallFont, nonTextOpacity);
            DrawPagerButton(g, layout.Next, text.Get("NextPage"), layout.PageIndex < layout.PageCount - 1, smallFont, nonTextOpacity);

            var pageText = $"{layout.PageIndex + 1}/{layout.PageCount}";
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None
            };
            g.DrawString(pageText, smallFont, dim, layout.Page, sf);
        }

        private static void DrawPagerButton(Graphics g, RectangleF rect, string text, bool enabled, Font font, float nonTextOpacity)
        {
            var bgAlpha = enabled ? 70 : 34;
            var fgAlpha = enabled ? 255 : 120;
            using var bg = new SolidBrush(WithNonTextOpacity(bgAlpha, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, bg, rect, 10);
            var textRect = new RectangleF(rect.X + 8, rect.Y + 4, rect.Width - 16, rect.Height - 8);
            var fontSize = FindBestFontSize(g, new[] { text }, textRect, font, minFontSize: 7f, lineSpacing: 0f);
            using var fitFont = new Font(font.FontFamily, fontSize, font.Style, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(fgAlpha, 255, 255, 255));
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None
            };
            g.DrawString(text, fitFont, brush, textRect, sf);
        }

        private static void DrawFilterChip(Graphics g, RectangleF rect, string text, bool enabled, Font font, float nonTextOpacity)
        {
            var bgColor = enabled
                ? WithNonTextOpacity(145, 86, 165, 255, nonTextOpacity)
                : WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity);
            using var bg = new SolidBrush(bgColor);
            FillRoundedRect(g, bg, rect, 10);

            var borderColor = enabled
                ? WithNonTextOpacity(220, 255, 255, 255, nonTextOpacity)
                : WithNonTextOpacity(120, 255, 255, 255, nonTextOpacity);
            using var border = new Pen(borderColor, 1.5f);
            DrawRoundedRectOutline(g, border, rect, 10);

            var fg = enabled ? Color.White : Color.FromArgb(220, 220, 220, 220);
            var textRect = new RectangleF(rect.X + 8, rect.Y + 4, rect.Width - 16, rect.Height - 8);
            var fontSize = FindBestFontSize(g, new[] { text }, textRect, font, minFontSize: 7f, lineSpacing: 0f);
            using var fitFont = new Font(font.FontFamily, fontSize, font.Style, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(fg);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None
            };
            g.DrawString(text, fitFont, brush, textRect, sf);
        }

        private static EVROverlayError UploadCursorTexture(ulong overlayHandle)
        {
            const int w = CursorDotTextureSize;
            const int h = CursorDotTextureSize;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            using var outer = new Pen(Color.FromArgb(230, 0, 0, 0), 4f);
            using var inner = new Pen(Color.FromArgb(245, 255, 255, 255), 3f);
            using var fill = new SolidBrush(Color.FromArgb(245, 95, 167, 191));

            g.FillEllipse(shadow, 13, 13, 38, 38);
            g.FillEllipse(fill, 18, 18, 28, 28);
            g.DrawEllipse(outer, 17, 17, 30, 30);
            g.DrawEllipse(inner, 21, 21, 22, 22);

            try
            {
                return UploadBitmap(overlayHandle, bmp);
            }
            catch
            {
                return EVROverlayError.UnknownOverlay;
            }
        }

        private static void FillRoundedRect(Graphics g, Brush brush, RectangleF rect, float radius)
        {
            using var path = CreateRoundedRectPath(rect, radius);
            g.FillPath(brush, path);
        }

        private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0.1f)
            {
                path.AddRectangle(rect);
                path.CloseFigure();
                return path;
            }

            var d = radius * 2f;
            var arc = new RectangleF(rect.X, rect.Y, d, d);

            // top-left
            path.AddArc(arc, 180, 90);
            // top-right
            arc.X = rect.Right - d;
            path.AddArc(arc, 270, 90);
            // bottom-right
            arc.Y = rect.Bottom - d;
            path.AddArc(arc, 0, 90);
            // bottom-left
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);

            path.CloseFigure();
            return path;
        }

        private static void DrawTextWithShadow(Graphics g, string text, Font font, RectangleF rect, Color? textColor = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            using var shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0));
            using var brush = new SolidBrush(textColor ?? Color.White);
            using var sf = new StringFormat
            {
                Trimming = StringTrimming.None,
                FormatFlags = 0
            };

            // Simple shadow improves readability in VR without expensive outline rendering.
            var shadowRect = new RectangleF(rect.X + 2, rect.Y + 2, rect.Width, rect.Height);
            g.DrawString(text, font, shadow, shadowRect, sf);
            g.DrawString(text, font, brush, rect, sf);
        }

        private static void DrawTextBlockWithShadow(Graphics g, string text, Font font, RectangleF rect, Color textColor)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            using var shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0));
            using var brush = new SolidBrush(textColor);
            using var sf = new StringFormat { Trimming = StringTrimming.None };

            var shadowRect = new RectangleF(rect.X + 2, rect.Y + 2, rect.Width, rect.Height);
            g.DrawString(text, font, shadow, shadowRect, sf);
            g.DrawString(text, font, brush, rect, sf);
        }

        private static void DrawPanelLockOverlay(
            Graphics g,
            RectangleF contentRect,
            bool unlockConfirmPending,
            SteamVrPanelText text,
            Font smallFont,
            Font boldFont,
            float nonTextOpacity)
        {
            using (var scrim = new SolidBrush(WithNonTextOpacity(58, 0, 0, 0, nonTextOpacity)))
            {
                FillRoundedRect(g, scrim, contentRect, 14);
            }

            var hintText = unlockConfirmPending
                ? text.Get("PanelUnlockConfirmHint")
                : text.Get("PanelLockedHint");
            var hintRect = new RectangleF(
                contentRect.X + Math.Max(28f, (contentRect.Width - 330f) * 0.5f),
                contentRect.Y + 18,
                Math.Min(330f, contentRect.Width - 56f),
                44);

            using (var bg = new SolidBrush(WithNonTextOpacity(unlockConfirmPending ? 155 : 115, unlockConfirmPending ? 255 : 33, unlockConfirmPending ? 193 : 150, unlockConfirmPending ? 7 : 243, nonTextOpacity)))
            {
                FillRoundedRect(g, bg, hintRect, 12);
            }

            using var border = new Pen(WithNonTextOpacity(165, 255, 255, 255, nonTextOpacity), 1.5f);
            DrawRoundedRectOutline(g, border, hintRect, 12);

            var iconRect = new RectangleF(hintRect.X + 13, hintRect.Y + 9, 26, 26);
            using var iconPen = new Pen(Color.FromArgb(232, 255, 255, 255), 2f);
            DrawLockGlyph(g, iconRect, true, iconPen);

            var textRect = new RectangleF(hintRect.X + 47, hintRect.Y + 5, hintRect.Width - 58, hintRect.Height - 10);
            DrawAutoFitText(g, hintText, textRect, unlockConfirmPending ? boldFont : smallFont, Color.White, minFontSize: 8f);
        }

        private static void DrawLockButton(Graphics g, RectangleF rect, bool locked, bool unlockConfirmPending, float nonTextOpacity)
        {
            var bg = unlockConfirmPending
                ? WithNonTextOpacity(126, 255, 193, 7, nonTextOpacity)
                : locked
                    ? WithNonTextOpacity(112, 33, 150, 243, nonTextOpacity)
                    : WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity);
            using (var bgBrush = new SolidBrush(bg))
            {
                FillRoundedRect(g, bgBrush, rect, 12);
            }

            using (var border = new Pen(WithNonTextOpacity(locked ? 175 : 90, 255, 255, 255, nonTextOpacity), 1.5f))
            {
                DrawRoundedRectOutline(g, border, rect, 12);
            }

            var iconRect = new RectangleF(rect.X + 9, rect.Y + 13, rect.Width - 18, rect.Height - 24);
            using var iconPen = new Pen(Color.FromArgb(228, 255, 255, 255), 2f);
            DrawLockGlyph(g, iconRect, locked, iconPen);

            if (unlockConfirmPending)
            {
                using var checkPen = new Pen(Color.FromArgb(245, 255, 255, 255), 2f);
                var checkRect = new RectangleF(rect.Right - 15, rect.Y + 7, 10, 10);
                g.DrawLines(checkPen, new[]
                {
                    new PointF(checkRect.X, checkRect.Y + checkRect.Height * 0.52f),
                    new PointF(checkRect.X + checkRect.Width * 0.38f, checkRect.Bottom),
                    new PointF(checkRect.Right, checkRect.Y)
                });
            }
        }

        private static void DrawLockGlyph(Graphics g, RectangleF rect, bool locked, Pen pen)
        {
            var body = new RectangleF(rect.X + 3, rect.Y + rect.Height * 0.46f, rect.Width - 6, rect.Height * 0.42f);
            DrawRoundedRectOutline(g, pen, body, 3);

            var shackle = new RectangleF(rect.X + rect.Width * 0.24f, rect.Y + 2, rect.Width * 0.52f, rect.Height * 0.56f);
            if (locked)
            {
                g.DrawArc(pen, shackle, 190, 160);
                g.DrawLine(pen, shackle.Left, shackle.Y + shackle.Height * 0.55f, shackle.Left, body.Top + 1);
                g.DrawLine(pen, shackle.Right, shackle.Y + shackle.Height * 0.55f, shackle.Right, body.Top + 1);
            }
            else
            {
                shackle.Offset(rect.Width * 0.17f, -1f);
                g.DrawArc(pen, shackle, 205, 135);
                g.DrawLine(pen, shackle.Left, shackle.Y + shackle.Height * 0.52f, shackle.Left, body.Top + 1);
            }
        }

        private static void DrawToggleDot(Graphics g, RectangleF rect, bool isOn, Color onColor, Color offColor, float nonTextOpacity)
        {
            var c = isOn ? onColor : offColor;
            using var bg = new SolidBrush(WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, bg, rect, 12);
            using var dot = new SolidBrush(c);
            var d = 16f;
            var dotRect = new RectangleF(rect.X + (rect.Width - d) * 0.5f, rect.Y + (rect.Height - d) * 0.5f, d, d);
            g.FillEllipse(dot, dotRect);
        }

        private static void DrawHoverHighlight(Graphics g, RectangleF rect, float cursorX, float cursorY, float nonTextOpacity)
        {
            if (!rect.Contains(cursorX, cursorY))
            {
                return;
            }

            using var hover = new SolidBrush(WithNonTextOpacity(55, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, hover, rect, 12);

            using var border = new Pen(WithNonTextOpacity(150, 255, 255, 255, nonTextOpacity), 2f);
            using var path = CreateRoundedRectPath(rect, 12);
            g.DrawPath(border, path);
        }

        private enum ToggleIcon
        {
            Mic,
            Desktop,
            Photo
        }

        private enum IconButtonKind
        {
            Top,
            PageUp,
            PageDown,
            PageLeft,
            PageRight
        }

        private static void DrawToggleIconDot(Graphics g, RectangleF rect, bool isOn, ToggleIcon icon, Color onColor, Color offColor, float nonTextOpacity)
        {
            var dotColor = isOn ? onColor : offColor;
            DrawToggleIconDot(g, rect, icon, dotColor, nonTextOpacity);
        }

        private static void DrawToggleIconDot(Graphics g, RectangleF rect, ToggleIcon icon, Color dotColor, float nonTextOpacity)
        {
            using var bg = new SolidBrush(WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, bg, rect, 12);

            using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 2);
            using var iconPen = new Pen(Color.FromArgb(210, 255, 255, 255), 2);

            // Icon region (left) and status dot (right).
            var iconRect = new RectangleF(rect.X + 12, rect.Y + 12, 22, rect.Height - 24);
            var dotD = 16f;
            var dotRect = new RectangleF(rect.Right - 12 - dotD, rect.Y + (rect.Height - dotD) * 0.5f, dotD, dotD);

            switch (icon)
            {
                case ToggleIcon.Mic:
                    DrawMicIcon(g, iconRect, iconPen);
                    break;
                case ToggleIcon.Desktop:
                    DrawDesktopIcon(g, iconRect, iconPen);
                    break;
                case ToggleIcon.Photo:
                    DrawCameraIcon(g, iconRect, iconPen);
                    break;
            }

            using var dotBrush = new SolidBrush(dotColor);
            g.FillEllipse(dotBrush, dotRect);
            g.DrawEllipse(pen, dotRect);
        }

        private static void DrawMicIcon(Graphics g, RectangleF rect, Pen pen)
        {
            // Simple microphone glyph.
            var w = rect.Width;
            var h = rect.Height;
            var cx = rect.X + w * 0.5f;
            var top = rect.Y + 1;
            var bodyW = w * 0.55f;
            var bodyH = h * 0.65f;
            var body = new RectangleF(cx - bodyW * 0.5f, top, bodyW, bodyH);
            DrawRoundedRectOutline(g, pen, body, bodyW * 0.45f);
            var stemTop = body.Bottom + 1;
            g.DrawLine(pen, cx, stemTop, cx, rect.Bottom - 3);
            g.DrawArc(pen, rect.X + 2, body.Bottom - 2, w - 4, h * 0.55f, 20, 140);
        }

        private static void DrawDesktopIcon(Graphics g, RectangleF rect, Pen pen)
        {
            // Simple monitor glyph.
            var screen = new RectangleF(rect.X + 1, rect.Y + 2, rect.Width - 2, rect.Height * 0.6f);
            g.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
            var cx = rect.X + rect.Width * 0.5f;
            var standY = screen.Bottom + 2;
            g.DrawLine(pen, cx, standY, cx, rect.Bottom - 2);
            g.DrawLine(pen, cx - 6, rect.Bottom - 2, cx + 6, rect.Bottom - 2);
        }

        private static void DrawCameraIcon(Graphics g, RectangleF rect, Pen pen)
        {
            var body = new RectangleF(rect.X + 1, rect.Y + rect.Height * 0.25f, rect.Width - 2, rect.Height * 0.58f);
            DrawRoundedRectOutline(g, pen, body, 3);
            var bump = new RectangleF(rect.X + rect.Width * 0.18f, rect.Y + rect.Height * 0.12f, rect.Width * 0.28f, rect.Height * 0.18f);
            g.DrawRectangle(pen, bump.X, bump.Y, bump.Width, bump.Height);
            var lensD = Math.Min(rect.Width, rect.Height) * 0.32f;
            var lens = new RectangleF(
                rect.X + (rect.Width - lensD) * 0.5f,
                body.Y + (body.Height - lensD) * 0.5f,
                lensD,
                lensD);
            g.DrawEllipse(pen, lens);
        }

        private static void DrawIconButton(Graphics g, RectangleF rect, IconButtonKind kind, bool enabled, float nonTextOpacity)
        {
            var bgA = enabled ? 90 : 40;
            using var bg = new SolidBrush(WithNonTextOpacity(bgA, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, bg, rect, 10);

            var fg = enabled ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255);
            using var pen = new Pen(fg, 2);

            var cx = rect.X + rect.Width * 0.5f;
            var cy = rect.Y + rect.Height * 0.5f;

            switch (kind)
            {
                case IconButtonKind.PageUp:
                {
                    var p1 = new PointF(cx, rect.Y + 10);
                    var p2 = new PointF(rect.X + 10, rect.Bottom - 12);
                    var p3 = new PointF(rect.Right - 10, rect.Bottom - 12);
                    g.DrawPolygon(pen, new[] { p1, p2, p3 });
                    break;
                }
                case IconButtonKind.PageDown:
                {
                    var p1 = new PointF(cx, rect.Bottom - 10);
                    var p2 = new PointF(rect.X + 10, rect.Y + 12);
                    var p3 = new PointF(rect.Right - 10, rect.Y + 12);
                    g.DrawPolygon(pen, new[] { p1, p2, p3 });
                    break;
                }
                case IconButtonKind.PageLeft:
                {
                    var p1 = new PointF(rect.X + 12, cy);
                    var p2 = new PointF(rect.Right - 10, rect.Y + 10);
                    var p3 = new PointF(rect.Right - 10, rect.Bottom - 10);
                    g.DrawPolygon(pen, new[] { p1, p2, p3 });
                    break;
                }
                case IconButtonKind.PageRight:
                {
                    var p1 = new PointF(rect.Right - 12, cy);
                    var p2 = new PointF(rect.X + 10, rect.Y + 10);
                    var p3 = new PointF(rect.X + 10, rect.Bottom - 10);
                    g.DrawPolygon(pen, new[] { p1, p2, p3 });
                    break;
                }
                case IconButtonKind.Top:
                {
                    // Up arrow + top bar.
                    g.DrawLine(pen, rect.X + 10, rect.Y + 10, rect.Right - 10, rect.Y + 10);
                    var p1 = new PointF(cx, rect.Y + 14);
                    var p2 = new PointF(rect.X + 10, rect.Bottom - 12);
                    var p3 = new PointF(rect.Right - 10, rect.Bottom - 12);
                    g.DrawPolygon(pen, new[] { p1, p2, p3 });
                    break;
                }
            }
        }

        private static void DrawScrollbar(
            Graphics g,
            RectangleF trackRect,
            int scrollIndex,
            int visibleCount,
            int totalCount,
            int maxScroll,
            bool atBottom,
            float nonTextOpacity)
        {
            using var track = new SolidBrush(WithNonTextOpacity(60, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, track, trackRect, 4);

            if (totalCount <= 0) return;

            maxScroll = Math.Max(0, maxScroll);
            var inset = 1f;
            if (maxScroll <= 0)
            {
                using var thumbAll = new SolidBrush(WithNonTextOpacity(90, 255, 255, 255, nonTextOpacity));
                var full = new RectangleF(trackRect.X + inset, trackRect.Y + inset, trackRect.Width - inset * 2, trackRect.Height - inset * 2);
                FillRoundedRect(g, thumbAll, full, 4);
                return;
            }

            var thumbH = Math.Max(24f, trackRect.Height * visibleCount / totalCount);
            var travel = trackRect.Height - thumbH;
            var t = atBottom ? 1f : Math.Clamp(scrollIndex, 0, maxScroll) / (float)maxScroll;
            var thumbY = trackRect.Y + travel * t;
            var thumbRect = new RectangleF(trackRect.X + inset, thumbY, trackRect.Width - inset * 2, thumbH);
            using var thumb = new SolidBrush(WithNonTextOpacity(170, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, thumb, thumbRect, 4);
        }

        // System.Drawing lacks a built-in rounded-rect draw helper; keep a tiny one here.
        private static void DrawRoundedRectOutline(Graphics g, Pen pen, RectangleF rect, float radius)
        {
            using var path = CreateRoundedRectPath(rect, radius);
            g.DrawPath(pen, path);
        }

        private static void DrawButton(Graphics g, RectangleF rect, string text, Font font, float nonTextOpacity)
        {
            using var bg = new SolidBrush(WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, bg, rect, 12);
            var textRect = new RectangleF(rect.X + 8, rect.Y + 4, rect.Width - 16, rect.Height - 8);
            var fontSize = FindBestFontSize(g, new[] { text }, textRect, font, minFontSize: 7f, lineSpacing: 0f);
            using var fitFont = new Font(font.FontFamily, fontSize, font.Style, GraphicsUnit.Pixel);
            using var shadowBrush = new SolidBrush(Color.FromArgb(230, 0, 0, 0));
            using var textBrush = new SolidBrush(Color.White);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None
            };
            var shadowRect = new RectangleF(textRect.X + 1.5f, textRect.Y + 1.5f, textRect.Width, textRect.Height);
            g.DrawString(text, fitFont, shadowBrush, shadowRect, sf);
            g.DrawString(text, fitFont, textBrush, textRect, sf);
        }

        private static EVROverlayError UploadBitmap(ulong overlayHandle, Bitmap bmp)
        {
            var overlay = Valve.VR.OpenVR.Overlay;
            if (overlay == null) return EVROverlayError.InvalidHandle;

            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                return overlay.SetOverlayRaw(overlayHandle, data.Scan0, (uint)bmp.Width, (uint)bmp.Height, 4);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private void StartAttachRetryLoop()
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_attachRetryCts != null) return;
                _attachRetryCts = new CancellationTokenSource();
            }

            var token = _attachRetryCts.Token;
            _ = Task.Run(async () =>
            {
                // Controllers may appear a bit later; keep trying to attach periodically.
                while (!token.IsCancellationRequested)
                {
                    var retryNeeded = false;
                    try
                    {
                        lock (_lock)
                        {
                            retryNeeded = IsConnected
                                && _overlayHandle != 0
                                && _desiredVisible
                                && _config.SteamVrAnchorModeValue != SteamVrOverlayAnchorMode.Head
                                && (_panelTrackedDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid
                                    || _panelTrackedDeviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd);
                        }

                        if (retryNeeded)
                        {
                            ApplyPlacementInternal();
                        }
                    }
                    catch
                    {
                        // keep retrying; overlay should be best-effort
                    }

                    try
                    {
                        await Task.Delay(retryNeeded ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2), token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, token);
        }

        public void Dispose()
        {
            var stopwatch = Stopwatch.StartNew();
            ulong overlayHandle;
            ulong cursorOverlayHandle;
            ulong photoOverlayHandle;
            ulong photoImageOverlayHandle;
            bool shouldShutdown;

            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                overlayHandle = _overlayHandle;
                cursorOverlayHandle = _cursorOverlayHandle;
                photoOverlayHandle = _photoOverlayHandle;
                photoImageOverlayHandle = _photoImageOverlayHandle;
                shouldShutdown = IsConnected || overlayHandle != 0 || cursorOverlayHandle != 0 || photoOverlayHandle != 0 || photoImageOverlayHandle != 0;
                _overlayHandle = 0;
                _cursorOverlayHandle = 0;
                _photoOverlayHandle = 0;
                _photoImageOverlayHandle = 0;
                _cursorOverlayIsSubview = false;
                _cursorOverlayUsesNativeCursor = false;
                _cursorOverlayFirstVisibleLogEmitted = false;
                _photoOverlayFirstVisibleLogEmitted = false;
                _photoPanelAppliedVisible = false;
                ResetPhotoImageOverlayStateLocked(resetContent: true);
                _photoPanelDismissed = false;
                _photoPanelAppliedWidthMeters = 0f;
                _photoPanelLatestTranscriptHash = 0;
                _photoPanelPageIndex = 0;
                ResetPhotoPanelTextScrollLocked();
                ResetPanelLockStateLocked();
                _cursorAppliedRayDeviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
                _cursorAppliedRayDistanceMeters = float.NaN;
                _cursorRayDistanceMeters = CursorLaserDefaultLengthMeters;
                InvalidatePointerDeviceCandidateCacheLocked();
                IsConnected = false;
            }

            LogDiagnosticInformation(
                "SteamVR service dispose started. overlayHandle={OverlayHandle}, cursorOverlayHandle={CursorOverlayHandle}, photoOverlayHandle={PhotoOverlayHandle}, photoImageOverlayHandle={PhotoImageOverlayHandle}, shouldShutdown={ShouldShutdown}",
                overlayHandle,
                cursorOverlayHandle,
                photoOverlayHandle,
                photoImageOverlayHandle,
                shouldShutdown);

            try
            {
                try
                {
                    _attachRetryCts?.Cancel();
                    _attachRetryCts?.Dispose();
                    _attachRetryCts = null;
                }
                catch { }

                try
                {
                    _eventLoopCts?.Cancel();
                    _eventLoopCts?.Dispose();
                    _eventLoopCts = null;
                }
                catch { }

                if (overlayHandle != 0 || cursorOverlayHandle != 0 || photoOverlayHandle != 0 || photoImageOverlayHandle != 0)
                {
                    LogDiagnosticDebug(
                        "Destroying SteamVR overlay handles. overlayHandle={OverlayHandle}, cursorOverlayHandle={CursorOverlayHandle}, photoOverlayHandle={PhotoOverlayHandle}, photoImageOverlayHandle={PhotoImageOverlayHandle}",
                        overlayHandle,
                        cursorOverlayHandle,
                        photoOverlayHandle,
                        photoImageOverlayHandle);
                    var overlay = Valve.VR.OpenVR.Overlay;
                    if (overlay != null && overlayHandle != 0)
                    {
                        try { overlay.HideOverlay(overlayHandle); } catch { }
                        try { overlay.DestroyOverlay(overlayHandle); } catch { }
                    }

                    if (overlay != null && cursorOverlayHandle != 0)
                    {
                        try { overlay.HideOverlay(cursorOverlayHandle); } catch { }
                        try { overlay.DestroyOverlay(cursorOverlayHandle); } catch { }
                    }

                    if (overlay != null && photoOverlayHandle != 0)
                    {
                        try { overlay.HideOverlay(photoOverlayHandle); } catch { }
                        try { overlay.DestroyOverlay(photoOverlayHandle); } catch { }
                    }

                    if (overlay != null && photoImageOverlayHandle != 0)
                    {
                        try { overlay.HideOverlay(photoImageOverlayHandle); } catch { }
                        try { overlay.DestroyOverlay(photoImageOverlayHandle); } catch { }
                    }
                }
            }
            catch { }

            if (shouldShutdown)
            {
                var shutdownWatch = Stopwatch.StartNew();
                LogDiagnosticDebug("OpenVR.Shutdown start during SteamVR service dispose.");
                try { Valve.VR.OpenVR.Shutdown(); }
                catch (Exception ex)
                {
                    if (IsDiagnosticLoggingEnabled)
                    {
                        _logger.Debug(ex, "OpenVR.Shutdown failed during SteamVR service dispose.");
                    }
                }
                finally
                {
                    shutdownWatch.Stop();
                    LogDiagnosticInformation("OpenVR.Shutdown finished during SteamVR service dispose. elapsedMs={ElapsedMs}", shutdownWatch.ElapsedMilliseconds);
                }
            }
            else
            {
                LogDiagnosticInformation("OpenVR.Shutdown skipped during SteamVR service dispose because no overlay context was created.");
            }

            stopwatch.Stop();
            LogDiagnosticInformation("SteamVR service dispose finished. elapsedMs={ElapsedMs}", stopwatch.ElapsedMilliseconds);
        }
    }
}


