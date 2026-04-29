using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private const string SteamVrAppKey = "vrcls.overlay";

        private bool _initInProgress;
        private bool _restartInProgress;
        private bool _disposed;
        private bool _desiredVisible;
        private bool _appliedVisible;
        private ulong _overlayHandle;
        private ulong _cursorOverlayHandle;
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

        // Simple interactive "dashboard-like" panel rendering.
        // Match "wrist/palm" usage: less horizontal, bigger readable text.
        private const int PanelWidth = 820;
        private const int PanelHeight = 720;
        private SteamVrOverlayPanelState _panelState = new();
        private bool _panelDirty = true;
        private int _panelStateHash;
        private DateTime _lastPanelRenderUtc = DateTime.MinValue;
        private static readonly TimeSpan PanelRenderMinInterval = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan PanelClickDebounceInterval = TimeSpan.FromMilliseconds(250);
        private bool _dropdownOpen;
        private bool _settingsOpen;
        private int _scrollIndex;
        private int _selectedIndex;
        private DateTime _lastPanelClickUtc = DateTime.MinValue;
        private float _lastPanelClickX = float.NaN;
        private float _lastPanelClickY = float.NaN;
        private float _cursorU = 0.5f; // 0..1
        private float _cursorV = 0.2f; // 0..1
        private bool _cursorValid;
        private DateTime _lastCursorUpdateUtc = DateTime.MinValue;
        private bool _cursorDirty;
        private float _cursorAppliedU = float.NaN;
        private float _cursorAppliedV = float.NaN;
        private bool _cursorAppliedVisible;
        private CancellationTokenSource? _eventLoopCts;
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
                if (_disposed) return Task.CompletedTask;

                // InitializeAsync is called when users want the overlay enabled.
                _desiredVisible = true;

                if (IsConnected || _initInProgress || _restartInProgress) return Task.CompletedTask;
                _initInProgress = true;
            }

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

                        if (!IsSteamVrRunning())
                        {
                            SetStatus(attempt <= fastAttempts
                                ? $"Waiting for SteamVR... ({attempt}/{fastAttempts})"
                                : "Waiting for SteamVR... (auto retry)");
                            await Task.Delay(attempt <= fastAttempts ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            continue;
                        }

                        if (!SafeIsHmdPresent())
                        {
                            SetStatus(attempt <= fastAttempts
                                ? $"SteamVR running, waiting for HMD... ({attempt}/{fastAttempts})"
                                : "SteamVR running, waiting for HMD... (auto retry)");
                            await Task.Delay(attempt <= fastAttempts ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            continue;
                        }

                        if (TryInitializeOnce())
                        {
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
            var canToggleNow = false;
            lock (_lock)
            {
                if (_disposed) return;
                _desiredVisible = visible;
                if (visible) _panelDirty = true;
                canToggleNow = IsConnected && _overlayHandle != 0;
                if (canToggleNow) handle = _overlayHandle;
                if (canToggleNow && _appliedVisible == visible) return;
            }

            if (!visible)
            {
                SetStatus("SteamVR overlay disabled");
            }

            if (!canToggleNow) return;

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
                }

                lock (_lock) _appliedVisible = visible;
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
            var hash = ComputePanelStateHash(state, frozen);

            lock (_lock)
            {
                if (_disposed) return;

                if (hash == _panelStateHash)
                {
                    return;
                }

                _panelStateHash = hash;
                _panelState = new SteamVrOverlayPanelState
                {
                    IsMicListening = state.IsMicListening,
                    IsDesktopListening = state.IsDesktopListening,
                    MicVolume = state.MicVolume,
                    DesktopVolume = state.DesktopVolume,
                    NonTextOpacity = Math.Clamp(state.NonTextOpacity, 0f, 1f),
                    SourceMask = state.SourceMask,
                    LanguageMask = state.LanguageMask,
                    Transcripts = frozen
                };

                if (_selectedIndex >= frozen.Length)
                {
                    _selectedIndex = frozen.Length == 0 ? 0 : frozen.Length - 1;
                }

                _panelDirty = true;
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
            hash.Add(QuantizeVolume(state.MicVolume));
            hash.Add(QuantizeVolume(state.DesktopVolume));
            hash.Add((int)Math.Round(Math.Clamp(state.NonTextOpacity, 0f, 1f) * 100f));
            hash.Add((int)state.SourceMask);
            hash.Add((int)state.LanguageMask);
            hash.Add(transcripts.Length);
            for (var i = 0; i < transcripts.Length; i++)
            {
                var t = transcripts[i];
                hash.Add(t.Source ?? string.Empty);
                hash.Add(t.Original ?? string.Empty);
                hash.Add(t.Translation ?? string.Empty);
                hash.Add(t.Translation2 ?? string.Empty);
                hash.Add(t.Translation3 ?? string.Empty);
                hash.Add(t.Timestamp.Ticks);
            }
            return hash.ToHashCode();
        }

        private bool TryConsumePanelDirtyLocked(DateTime now)
        {
            if (!_panelDirty) return false;
            if (now - _lastPanelRenderUtc < PanelRenderMinInterval) return false;
            _panelDirty = false;
            return true;
        }

        private uint GetPreferredPointerDeviceIndex(CVRSystem system)
        {
            if (system == null) return Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;

            int panelHand;
            lock (_lock)
            {
                panelHand = _hand;
            }

            // Palm panel is easiest to point at with the opposite controller.
            var preferredRole = panelHand == 1
                ? ETrackedControllerRole.RightHand
                : ETrackedControllerRole.LeftHand;
            var fallbackRole = preferredRole == ETrackedControllerRole.LeftHand
                ? ETrackedControllerRole.RightHand
                : ETrackedControllerRole.LeftHand;

            var preferredDevice = system.GetTrackedDeviceIndexForControllerRole(preferredRole);
            if (preferredDevice != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                return preferredDevice;
            }

            return system.GetTrackedDeviceIndexForControllerRole(fallbackRole);
        }

        public void SetHand(int hand)
        {
            var normalized = Math.Clamp(hand, 0, 1);
            lock (_lock)
            {
                _hand = normalized;
                _config.SteamVrHand = normalized;
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
                    _cursorDirty = true;
                }

                _panelDirty = true;
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

            EnsureSteamVrApplicationRegistration(enabled);
        }

        public void RefreshStartupRegistrationStatus()
        {
            EnsureSteamVrApplicationRegistration(_config.SteamVrAutoLaunch, refreshOnly: true);
        }

        public void IdentifyCurrentProcess()
        {
            TryIdentifyCurrentProcessInternal();
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
                // Keep retrying; meanwhile attach to HMD so users can still see something.
                var now = DateTime.UtcNow;
                if ((now - _lastControllerMissingLogUtc) > TimeSpan.FromSeconds(5))
                {
                    _lastControllerMissingLogUtc = now;
                    _logger.Debug("未找到手柄设备 {Role}", role);
                    SetStatus("SteamVR overlay connected (waiting for controller)");
                }
                deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd;
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

        private void ApplyPlacementInternal()
        {
            var system = Valve.VR.OpenVR.System;
            var overlay = Valve.VR.OpenVR.Overlay;
            if (system == null || overlay == null) return;

            var placement = BuildPlacementTransform(system);
            var transform = placement.Transform;
            var err = overlay.SetOverlayTransformTrackedDeviceRelative(_overlayHandle, placement.DeviceIndex, ref transform);
            if (err != EVROverlayError.None)
            {
                _logger.Debug("SetOverlayTransformTrackedDeviceRelative failed: {Error}", err);
                if (err == EVROverlayError.InvalidHandle || err == EVROverlayError.UnknownOverlay)
                {
                    RequestRestart($"SetOverlayTransformTrackedDeviceRelative failed: {err}");
                }
            }

            lock (_lock)
            {
                _panelTrackedDeviceIndex = placement.DeviceIndex;
                _panelTransformInDeviceSpace = transform;
                _cursorDirty = true;
            }
        }

        private (uint DeviceIndex, HmdMatrix34_t Transform) BuildPlacementTransform(CVRSystem system)
        {
            uint deviceIndex;
            HmdMatrix34_t baseTransform;
            var anchorMode = _config.SteamVrAnchorModeValue;
            var hand = _hand;
            var offset = _config.GetSteamVrAnchorOffset(anchorMode);
            var rotation = _config.GetSteamVrAnchorRotation(anchorMode);

            if (anchorMode == SteamVrOverlayAnchorMode.Head)
            {
                deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd;
                baseTransform = BuildTransform(0.00f, -0.18f, -0.75f, 0.0, 0.0, 0.0);
            }
            else
            {
                var role = hand == 1 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
                deviceIndex = system.GetTrackedDeviceIndexForControllerRole(role);
                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastControllerMissingLogUtc) > TimeSpan.FromSeconds(5))
                    {
                        _lastControllerMissingLogUtc = now;
                        _logger.Debug("Controller not found for role {Role}", role);
                        SetStatus("SteamVR overlay connected (waiting for controller)");
                    }

                    deviceIndex = Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd;
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
                    baseTransform = BuildTransform(
                        hand == 1 ? 0.010f : -0.010f,
                        0.056f,
                        -0.018f,
                        90.0,
                        hand == 1 ? -12.0 : 12.0,
                        hand == 1 ? -90.0 : 90.0);
                }
                else
                {
                    baseTransform = BuildTransform(
                        hand == 1 ? 0.03f : -0.03f,
                        -0.04f,
                        0.06f,
                        -90.0,
                        hand == 1 ? 30.0 : -30.0,
                        hand == 1 ? -90.0 : 90.0);
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
            return (deviceIndex, Multiply(Multiply(baseTransform, rotationTransform), offsetTransform));
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

            // Cursor size is proportional to panel width so it stays usable for different overlay sizes.
            // 64px out of the panel width feels like a good starting point in VR.
            var cursorWidthMeters = panelWidthMeters * (64f / PanelWidth);
            cursorWidthMeters = Math.Clamp(cursorWidthMeters, 0.006f, 0.02f);
            overlay.SetOverlayWidthInMeters(_cursorOverlayHandle, cursorWidthMeters);
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

                _cursorAppliedVisible = false;
                _cursorAppliedU = float.NaN;
                _cursorAppliedV = float.NaN;
                _cursorDirty = false;
                _panelStateHash = 0;
                _lastPanelRenderUtc = DateTime.MinValue;
            }
            catch { }

            try { Valve.VR.OpenVR.Shutdown(); } catch { }
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

        private void EnsureSteamVrApplicationRegistration(bool enabled, bool refreshOnly = false)
        {
            try
            {
                var manifestPath = EnsureSteamVrManifestWritten();
                var hasOpenVrContext = IsConnected || Valve.VR.OpenVR.System != null;
                if (!hasOpenVrContext && !IsSteamVrRunning())
                {
                    SetStartupRegistrationStatus(enabled
                        ? "SteamVR auto-launch pending. Start SteamVR once to finish registration."
                        : "SteamVR auto-launch disabled");
                    return;
                }

                var shouldShutdown = false;
                if (!hasOpenVrContext)
                {
                    var initError = EVRInitError.None;
                    Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
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
                    if (addErr != EVRApplicationError.None && addErr != EVRApplicationError.AppKeyAlreadyExists)
                    {
                        SetStartupRegistrationStatus($"SteamVR manifest registration failed: {FormatApplicationError(applications, addErr)}");
                        return;
                    }

                    if (!refreshOnly || enabled)
                    {
                        var autoErr = applications.SetApplicationAutoLaunch(SteamVrAppKey, enabled);
                        if (autoErr != EVRApplicationError.None)
                        {
                            SetStartupRegistrationStatus($"SteamVR auto-launch update failed: {FormatApplicationError(applications, autoErr)}");
                            return;
                        }
                    }

                    var autoLaunch = applications.GetApplicationAutoLaunch(SteamVrAppKey);
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
                _logger.Debug(ex, "SteamVR app registration failed");
            }
        }

        private void TryIdentifyCurrentProcessInternal()
        {
            try
            {
                var hasOpenVrContext = IsConnected || Valve.VR.OpenVR.System != null;
                if (!hasOpenVrContext && !IsSteamVrRunning())
                {
                    return;
                }

                var shouldShutdown = false;
                if (!hasOpenVrContext)
                {
                    var initError = EVRInitError.None;
                    Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
                    if (initError != EVRInitError.None)
                    {
                        return;
                    }

                    shouldShutdown = true;
                }

                try
                {
                    var applications = Valve.VR.OpenVR.Applications;
                    if (applications == null)
                    {
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
                _logger.Debug(ex, "Identify SteamVR application failed");
            }
        }

        private static void TryIdentifyCurrentProcessInternal(CVRApplications applications)
        {
            try
            {
                applications.IdentifyApplication((uint)Environment.ProcessId, SteamVrAppKey);
            }
            catch
            {
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
            lock (_lock)
            {
                _startupRegistrationStatus = status;
            }
        }

        private bool TryInitializeOnce()
        {
            try
            {
                SetStatus("Initializing SteamVR overlay...");

                var error = EVRInitError.None;
                Valve.VR.OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
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
                    _logger.Warning("OpenVR.Overlay unavailable");
                    SetStatus("OpenVR.Overlay unavailable");
                    SetDisconnected();
                    CleanupFailedInit();
                    return false;
                }

                var overlayError = overlay.CreateOverlay("vrcls.subtitle", "VRCLS Subtitle", ref _overlayHandle);
                if (overlayError != EVROverlayError.None)
                {
                    _logger.Warning("创建 Overlay 失败: {Error}", overlayError);
                    SetStatus($"CreateOverlay failed: {overlayError}");
                    SetDisconnected();
                    CleanupFailedInit();
                    return false;
                }

                // Cursor overlay (separate from panel) to avoid re-uploading the big texture on every cursor move.
                var cursorHandle = 0UL;
                try
                {
                    var key = $"vrcls.cursor.{DateTime.UtcNow.Ticks}";
                    var name = $"VRCLS Cursor {DateTime.UtcNow.Ticks}";
                    var cursorErr = overlay.CreateOverlay(key, name, ref cursorHandle);
                    if (cursorErr == EVROverlayError.None)
                    {
                        _cursorOverlayHandle = cursorHandle;
                        overlay.SetOverlayAlpha(_cursorOverlayHandle, 1.0f);
                        overlay.SetOverlayFlag(_cursorOverlayHandle, VROverlayFlags.SortWithNonSceneOverlays, true);
                        // Keep cursor overlay non-interactive as well (avoid stealing app input).
                        overlay.SetOverlayInputMethod(_cursorOverlayHandle, VROverlayInputMethod.None);
                        overlay.SetOverlayFlag(_cursorOverlayHandle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
                        overlay.HideOverlay(_cursorOverlayHandle);
                        UploadCursorTexture(_cursorOverlayHandle);
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

                // Enable interactive events so we can implement a "dashboard-like" panel with a cursor dot.
                try
                {
                    // IMPORTANT: keep the overlay non-interactive so it doesn't steal VRChat input/movement.
                    // We'll compute a cursor by ray-intersection and handle clicks by polling controller state.
                    overlay.SetOverlayInputMethod(_overlayHandle, VROverlayInputMethod.None);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.SendVRTouchpadEvents, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.SendVRDiscreteScrollEvents, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.SendVRSmoothScrollEvents, false);
                    overlay.SetOverlayFlag(_overlayHandle, VROverlayFlags.ShowTouchPadScrollWheel, false);
                }
                catch { }

                ApplySizeInternal(_size);
                ApplyCursorSizeInternal(_panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size);
                ApplyPlacementInternal();
                EnsureSteamVrApplicationRegistration(_config.SteamVrAutoLaunch, refreshOnly: true);
                TryIdentifyCurrentProcessInternal();
                StartAttachRetryLoop();
                StartEventLoop();

                overlay.SetOverlayAlpha(_overlayHandle, 1.0f);
                if (_desiredVisible)
                {
                    overlay.ShowOverlay(_overlayHandle);
                }
                else
                {
                    overlay.HideOverlay(_overlayHandle);
                }
                _appliedVisible = _desiredVisible;

                IsConnected = true;
                SetStatus("SteamVR overlay connected");
                _logger.Information("SteamVR Overlay 初始化完成(hand={Hand}, size={Size})", _hand, _size);
                Connected?.Invoke(this, EventArgs.Empty);

                // Push a visible initial frame (render loop will upload).
                lock (_lock)
                {
                    _panelDirty = true;
                    _lastPanelRenderUtc = DateTime.MinValue;
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
                            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                            continue;
                        }

                        if (TryInitializeOnce())
                        {
                            return;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
                    }

                    SetStatus("SteamVR overlay repair failed (manual retry needed)");
                }
                finally
                {
                    lock (_lock) _restartInProgress = false;
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
                    var now = DateTime.UtcNow;
                    SteamVrOverlayPanelState state;
                    var dropdownOpen = false;
                    var scrollIndex = 0;
                    var selectedIndex = 0;
                    var cursorU = 0.5f;
                    var cursorV = 0.5f;
                    var cursorValid = false;
                    var lastText = string.Empty;
                    var shouldRender = false;
                    var drawCursorDotInPanel = false;
                    var settingsOpen = false;
                    var anchorMode = SteamVrOverlayAnchorMode.Palm;
                    var steamVrHand = 0;
                    var panelSize = 0.15f;
                    var startupAutoLaunch = false;

                    lock (_lock)
                    {
                        if (_disposed || !IsConnected || _overlayHandle == 0 || !_desiredVisible)
                        {
                            shouldRender = false;
                            handle = 0;
                            state = _panelState;
                        }
                        else
                        {
                            handle = _overlayHandle;
                            state = _panelState;
                            dropdownOpen = _dropdownOpen;
                            scrollIndex = _scrollIndex;
                            selectedIndex = _selectedIndex;
                            cursorU = _cursorU;
                            cursorV = _cursorV;
                            cursorValid = _cursorValid;
                            lastText = _lastText;
                            drawCursorDotInPanel = _cursorOverlayHandle == 0;
                            settingsOpen = _settingsOpen;
                            anchorMode = _config.SteamVrAnchorModeValue;
                            steamVrHand = _hand;
                            panelSize = _size;
                            startupAutoLaunch = _config.SteamVrAutoLaunch;

                            shouldRender = TryConsumePanelDirtyLocked(now);
                        }
                    }

                    try
                    {
                        if (handle != 0)
                        {
                            var overlay = Valve.VR.OpenVR.Overlay;
                            if (overlay != null)
                            {
                                // Drain all pending events for this overlay handle.
                                while (overlay.PollNextOverlayEvent(handle, ref evt, evtSize))
                                {
                                    HandleOverlayEvent(evt);
                                }

                                // If SteamVR isn't sending mouse events (common for palm-attached overlays),
                                // compute intersection ourselves so a cursor dot still appears when the user points.
                                TryUpdateCursorFromControllerRay(handle, overlay);

                                // If the cursor is hitting the panel, treat trigger as click without making the overlay interactive.
                                TryHandleControllerClick(handle);

                                // Apply cursor overlay transform (cheap) without re-uploading the big panel texture.
                                ApplyCursorOverlayIfNeeded(handle, overlay);
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
                                dropdownOpen = _dropdownOpen;
                                scrollIndex = _scrollIndex;
                                selectedIndex = _selectedIndex;
                                cursorU = _cursorU;
                                cursorV = _cursorV;
                                cursorValid = _cursorValid;
                                lastText = _lastText;
                                drawCursorDotInPanel = _cursorOverlayHandle == 0;
                                settingsOpen = _settingsOpen;
                                anchorMode = _config.SteamVrAnchorModeValue;
                                steamVrHand = _hand;
                                panelSize = _size;
                                startupAutoLaunch = _config.SteamVrAutoLaunch;
                            }
                        }
                    }

                    if (handle != 0 && shouldRender)
                    {
                        try
                        {
                                var err = RenderAndUploadPanel(handle, state, dropdownOpen, scrollIndex, selectedIndex, drawCursorDotInPanel, cursorU, cursorV, cursorValid, lastText, settingsOpen, anchorMode, steamVrHand, panelSize, startupAutoLaunch);
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

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(33), token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, token);
        }

        private void TryUpdateCursorFromControllerRay(ulong overlayHandle, CVROverlay overlay)
        {
            try
            {
                var system = Valve.VR.OpenVR.System;
                if (system == null) return;

                uint panelDeviceIndex;
                HmdMatrix34_t panelDeviceRelative;
                float panelWidthMeters;
                bool flipU, flipV;
                lock (_lock)
                {
                    panelDeviceIndex = _panelTrackedDeviceIndex;
                    panelDeviceRelative = _panelTransformInDeviceSpace;
                    panelWidthMeters = _panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size;
                    flipU = _config.SteamVrCursorFlipU;
                    flipV = _config.SteamVrCursorFlipV;
                }
                var panelHeightMeters = panelWidthMeters * (PanelHeight / (float)PanelWidth);

                var deviceIndex = GetPreferredPointerDeviceIndex(system);
                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return;

                // Get latest poses.
                var poses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
                system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

                if (deviceIndex < 0 || deviceIndex >= poses.Length) return;
                var pose = poses[deviceIndex];
                if (!pose.bPoseIsValid) return;

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
                var hit = overlay.ComputeOverlayIntersection(overlayHandle, ref p, ref r);

                lock (_lock)
                {
                    if (_disposed) return;

                    if (!hit)
                    {
                        // If we haven't had any cursor updates in a while, hide the dot.
                        if (_cursorValid && (DateTime.UtcNow - _lastCursorUpdateUtc) > TimeSpan.FromSeconds(1))
                        {
                            _cursorValid = false;
                            _cursorDirty = true;
                            if (_cursorOverlayHandle == 0) _panelDirty = true; // fallback: remove cursor from panel
                        }
                        return;
                    }

                    _cursorRayDeviceIndex = deviceIndex;

                    // Store the exact hit point/normal in tracking space; used to position the cursor overlay
                    // precisely (avoids size/aspect mismatch near edges).
                    _cursorPointX = r.vPoint.v0;
                    _cursorPointY = r.vPoint.v1;
                    _cursorPointZ = r.vPoint.v2;
                    _cursorNormalX = r.vNormal.v0;
                    _cursorNormalY = r.vNormal.v1;
                    _cursorNormalZ = r.vNormal.v2;

                    // Derive panel-local meters from the hit point so that cursor placement and click mapping
                    // are guaranteed to line up (independent of SteamVR-reported UV quirks).
                    var localX = 0f;
                    var localY = 0f;
                    var localZ = 0f;
                    var computedLocal = false;
                    try
                    {
                        if (panelDeviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid &&
                            panelDeviceIndex < poses.Length &&
                            poses[panelDeviceIndex].bPoseIsValid)
                        {
                            var panelAbs = Multiply(poses[panelDeviceIndex].mDeviceToAbsoluteTracking, panelDeviceRelative);
                            InverseTransformPointRigid(panelAbs, _cursorPointX, _cursorPointY, _cursorPointZ, out localX, out localY, out localZ);
                            computedLocal = true;
                        }
                    }
                    catch
                    {
                        computedLocal = false;
                    }

                    if (!computedLocal)
                    {
                        // Fallback: compute local from UVs if we can't get a valid panel pose.
                        var u0 = Math.Clamp(r.vUVs.v0, 0f, 1f);
                        var v0 = Math.Clamp(r.vUVs.v1, 0f, 1f);
                        localX = (u0 - 0.5f) * panelWidthMeters;
                        localY = (0.5f - v0) * panelHeightMeters;
                        localZ = 0f;
                    }

                    // Apply user calibration consistently for both cursor and click.
                    if (flipU) localX = -localX;
                    if (flipV) localY = -localY;

                    _cursorLocalX = localX;
                    _cursorLocalY = localY;
                    _cursorLocalZ = localZ;

                    // UV for move detection (also used by fallbacks).
                    var u = (localX / panelWidthMeters) + 0.5f;
                    var v = 0.5f - (localY / panelHeightMeters);
                    u = Math.Clamp(u, 0f, 1f);
                    v = Math.Clamp(v, 0f, 1f);

                    // Only re-render if cursor actually moved a bit (avoid 30fps uploads).
                    var changed = !_cursorValid || Math.Abs(u - _cursorU) > 0.003f || Math.Abs(v - _cursorV) > 0.003f;
                    if (changed)
                    {
                        _cursorU = u;
                        _cursorV = v;
                        _cursorValid = true;
                        _lastCursorUpdateUtc = DateTime.UtcNow;
                        _cursorDirty = true;
                        if (_cursorOverlayHandle == 0) _panelDirty = true; // fallback: draw cursor inside panel
                    }
                }
            }
            catch
            {
                // ignore; best-effort cursor
            }
        }

        private void TryHandleControllerClick(ulong overlayHandle)
        {
            try
            {
                var system = Valve.VR.OpenVR.System;
                if (system == null) return;

                // Only react when we're actually pointing at the panel.
                float localX, localY;
                float panelWidthMeters;
                bool valid;
                lock (_lock)
                {
                    if (_disposed || !IsConnected || !_desiredVisible || _overlayHandle != overlayHandle) return;
                    valid = _cursorValid;
                    localX = _cursorLocalX;
                    localY = _cursorLocalY;
                    panelWidthMeters = _panelAppliedWidthMeters > 0 ? _panelAppliedWidthMeters : _size;
                }
                if (!valid) return;

                // Use the same controller we used for ray intersection, if known.
                uint deviceIndex;
                lock (_lock) deviceIndex = _cursorRayDeviceIndex;
                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                {
                    deviceIndex = GetPreferredPointerDeviceIndex(system);
                }
                if (deviceIndex == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return;

                var state = new VRControllerState_t();
                var ok = system.GetControllerState(deviceIndex, ref state, (uint)Marshal.SizeOf<VRControllerState_t>());
                if (!ok) return;

                // Trigger pressed?
                // OpenVR button mask is 1<<buttonId.
                var triggerMask = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
                var pressed = (state.ulButtonPressed & triggerMask) != 0;

                var doClick = false;
                var panelHeightMeters = panelWidthMeters * (PanelHeight / (float)PanelWidth);
                var px = ((localX / panelWidthMeters) + 0.5f) * PanelWidth;
                var py = (0.5f - (localY / panelHeightMeters)) * PanelHeight;
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
                    HandlePanelClick(px, py);
                }
            }
            catch
            {
                // ignore; best-effort
            }
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
                        var (u, v) = NormalizeUv(evt.data.mouse.x, evt.data.mouse.y);
                        lock (_lock)
                        {
                            _cursorU = u;
                            _cursorV = v;
                            _cursorValid = true;
                            _lastCursorUpdateUtc = DateTime.UtcNow;
                            _cursorDirty = true;
                            if (_cursorOverlayHandle == 0) _panelDirty = true; // fallback: draw cursor inside panel
                        }
                        break;
                    }
                    case EVREventType.VREvent_MouseButtonDown:
                    {
                        var (u, v) = NormalizeUv(evt.data.mouse.x, evt.data.mouse.y);
                        HandleClick(u, v);
                        break;
                    }
                    case EVREventType.VREvent_TouchPadMove:
                    {
                        // Trackpad movement can be used as a "cursor dot" controller without relying on a laser pointer.
                        var x = evt.data.touchPadMove.fValueXRaw;
                        var y = evt.data.touchPadMove.fValueYRaw;
                        var (u, v) = NormalizeUv(x, y);
                        lock (_lock)
                        {
                            _cursorU = u;
                            _cursorV = v;
                            _cursorValid = true;
                            _lastCursorUpdateUtc = DateTime.UtcNow;
                            _cursorDirty = true;
                            if (_cursorOverlayHandle == 0) _panelDirty = true; // fallback: draw cursor inside panel
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

        private (float u, float v) NormalizeUv(float x, float y)
        {
            // OpenVR mouse/touchpad coordinates are typically 0..1, but some paths can produce -1..1.
            // Normalize both cases and clamp.
            var u = x;
            var v = y;

            if (u < 0f || u > 1f)
            {
                u = (u + 1f) * 0.5f;
            }
            if (v < 0f || v > 1f)
            {
                v = (v + 1f) * 0.5f;
            }

            u = Math.Clamp(u, 0f, 1f);
            v = Math.Clamp(v, 0f, 1f);

            return ApplyUvTransform(u, v);
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

        private void HandleClick(float u, float v)
        {
            var px = u * PanelWidth;
            var py = v * PanelHeight;
            HandlePanelClick(px, py);
        }

        private void HandlePanelClick(float x, float y)
        {
            if (ShouldSuppressPanelClick(x, y))
            {
                return;
            }

            // Layout constants (pixels)
            var headerY = 10f;
            var headerH = 54f;

            var micRect = new RectangleF(150, headerY, 140, headerH);
            var deskRect = new RectangleF(300, headerY, 140, headerH);
            var dropRect = new RectangleF(450, headerY, 280, headerH);
            var gearRect = new RectangleF(740, headerY, 60, headerH);

            var listY = headerY + headerH + 18f;
            var listH = PanelHeight - listY - 16f;
            var listRect = new RectangleF(16, listY, PanelWidth - 32, listH);

            SteamVrOverlayActionType? actionToRaise = null;
            SteamVrOverlayTranscript? transcriptToCopy = null;
            var actionResolvedInSettings = false;

            lock (_lock)
            {
                if (_disposed) return;

                if (gearRect.Contains(x, y))
                {
                    _settingsOpen = !_settingsOpen;
                    _dropdownOpen = false;
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
                else if (dropRect.Contains(x, y) && !_settingsOpen)
                {
                    _dropdownOpen = !_dropdownOpen;
                    _panelDirty = true;
                    return;
                }
                if (_settingsOpen)
                {
                    var settingsRect = new RectangleF(16, listY, PanelWidth - 48, PanelHeight - listY - 16);
                    var anchorY = settingsRect.Y + 54f;
                    var anchorPalmRect = new RectangleF(settingsRect.X + 24, anchorY, 136, 42);
                    var anchorBackRect = new RectangleF(anchorPalmRect.Right + 12, anchorY, 156, 42);
                    var anchorHeadRect = new RectangleF(anchorBackRect.Right + 12, anchorY, 136, 42);

                    var handY = anchorPalmRect.Bottom + 20;
                    var handRightRect = new RectangleF(settingsRect.X + 24, handY, 136, 42);
                    var handLeftRect = new RectangleF(handRightRect.Right + 12, handY, 136, 42);
                    var startupRect = new RectangleF(settingsRect.X + 24, handRightRect.Bottom + 20, 240, 42);
                    var sizeMinusRect = new RectangleF(settingsRect.X + 24, startupRect.Bottom + 20, 42, 42);
                    var sizePlusRect = new RectangleF(sizeMinusRect.Right + 210, sizeMinusRect.Y, 42, 42);
                    var resetRect = new RectangleF(settingsRect.X + 24, sizeMinusRect.Bottom + 20, 180, 42);

                    if (anchorPalmRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.SetAnchorPalm;
                        _panelDirty = true;
                    }
                    else if (anchorBackRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.SetAnchorBackOfHand;
                        _panelDirty = true;
                    }
                    else if (anchorHeadRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.SetAnchorHead;
                        _panelDirty = true;
                    }
                    else if (handRightRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.SetHandRight;
                        _panelDirty = true;
                    }
                    else if (handLeftRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.SetHandLeft;
                        _panelDirty = true;
                    }
                    else if (startupRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.ToggleSteamVrAutoLaunch;
                        _panelDirty = true;
                    }
                    else if (sizeMinusRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.DecreaseOverlaySize;
                        _panelDirty = true;
                    }
                    else if (sizePlusRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.IncreaseOverlaySize;
                        _panelDirty = true;
                    }
                    else if (resetRect.Contains(x, y))
                    {
                        actionToRaise = SteamVrOverlayActionType.ResetOverlayOffset;
                        _panelDirty = true;
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
                    var itemH = ComputePanelItemHeight(contentRect.Height, languageMask);
                    var visibleCount = Math.Max(1, (int)Math.Floor(contentRect.Height / itemH));
                    var maxScroll = Math.Max(0, items2.Count - visibleCount);
                    _scrollIndex = Math.Clamp(_scrollIndex, 0, maxScroll);

                    var atTop = _scrollIndex <= 0;
                    var atBottom = _scrollIndex >= maxScroll;

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
                            _scrollIndex = Math.Min(maxScroll, _scrollIndex + visibleCount);
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
                        var idxInList = (int)((y - contentRect.Y) / itemH);
                        var idx = _scrollIndex + idxInList;
                        if (idx >= 0 && idx < items2.Count)
                        {
                            _selectedIndex = idx;
                            transcriptToCopy = items2[idx];
                            actionToRaise = SteamVrOverlayActionType.CopyTranscript;
                            _panelDirty = true;
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
                        Transcript = transcriptToCopy
                    });
                }
                catch { }
            }
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
            bool valid;
            bool shouldBeVisible;
            bool dirty;
            float appliedU, appliedV;
            bool appliedVisible;
            uint panelDeviceIndex;
            HmdMatrix34_t panelTransform;
            float localX, localY, localZ;

            lock (_lock)
            {
                if (_disposed) return;
                if (_cursorOverlayHandle == 0) return;

                cursorHandle = _cursorOverlayHandle;
                u = _cursorU;
                v = _cursorV;
                valid = _cursorValid;
                dirty = _cursorDirty;
                appliedU = _cursorAppliedU;
                appliedV = _cursorAppliedV;
                appliedVisible = _cursorAppliedVisible;
                panelDeviceIndex = _panelTrackedDeviceIndex;
                panelTransform = _panelTransformInDeviceSpace;
                localX = _cursorLocalX;
                localY = _cursorLocalY;
                localZ = _cursorLocalZ;

                shouldBeVisible = _desiredVisible && IsConnected && panelOverlayHandle == _overlayHandle && valid;
            }

            try
            {
                if (!shouldBeVisible)
                {
                    if (appliedVisible)
                    {
                        overlay.HideOverlay(cursorHandle);
                        lock (_lock) _cursorAppliedVisible = false;
                    }
                    return;
                }

                // Don't spam transforms if nothing changed.
                var moved = float.IsNaN(appliedU) || Math.Abs(u - appliedU) > 0.002f || Math.Abs(v - appliedV) > 0.002f;
                if (!dirty && appliedVisible && !moved) return;

                // A tiny +Z offset avoids z-fighting between the cursor quad and the panel quad.
                var local = new HmdMatrix34_t { m0 = 1, m5 = 1, m10 = 1, m3 = localX, m7 = localY, m11 = localZ + 0.001f };
                var cursorT = Multiply(panelTransform, local);
                overlay.SetOverlayTransformTrackedDeviceRelative(cursorHandle, panelDeviceIndex, ref cursorT);
                if (!appliedVisible) overlay.ShowOverlay(cursorHandle);

                lock (_lock)
                {
                    _cursorDirty = false;
                    _cursorAppliedU = u;
                    _cursorAppliedV = v;
                    _cursorAppliedVisible = true;
                }
            }
            catch
            {
                // ignore; cursor is best-effort
            }
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
            int hand,
            float panelSize,
            bool startupAutoLaunch)
        {
            using var bmp = new Bitmap(PanelWidth, PanelHeight, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var nonTextOpacity = Math.Clamp(state.NonTextOpacity, 0f, 1f);

            // Background
            using (var bg = new SolidBrush(WithNonTextOpacity(110, 0, 0, 0, nonTextOpacity)))
            {
                FillRoundedRect(g, bg, new RectangleF(0, 0, PanelWidth, PanelHeight), 18);
            }

            using var titleFont = new Font("Microsoft YaHei UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelFont = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
            using var smallFont = new Font("Microsoft YaHei UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var boldFont = new Font("Microsoft YaHei UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var chipFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);

            using var white = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(200, 220, 220, 220));

            // Header
            g.DrawString(settingsOpen ? "VRCLS Settings" : "VRCLS", titleFont, white, new PointF(20, 18));
            if (!string.IsNullOrWhiteSpace(AppVersionLabel))
            {
                g.DrawString(AppVersionLabel, smallFont, dim, new PointF(24, 50));
            }

            var micRect = new RectangleF(150, 10, 140, 54);
            var deskRect = new RectangleF(300, 10, 140, 54);
            var dropRect = new RectangleF(450, 10, 280, 54);
            var gearRect = new RectangleF(740, 10, 60, 54);

            var items = state.Transcripts ?? Array.Empty<SteamVrOverlayTranscript>();
            var selected = (selectedIndex >= 0 && selectedIndex < items.Count) ? items[selectedIndex] : null;
            var dropText = selected == null ? "History (empty)" : $"History: {selected.Timestamp:HH:mm:ss}";

            var languageMask = TranscriptLanguageMask.All;

            if (cursorValid)
            {
                var hoverX = cursorU * PanelWidth;
                var hoverY = cursorV * PanelHeight;
                DrawHoverHighlight(g, micRect, hoverX, hoverY, nonTextOpacity);
                DrawHoverHighlight(g, deskRect, hoverX, hoverY, nonTextOpacity);
                DrawHoverHighlight(g, gearRect, hoverX, hoverY, nonTextOpacity);
            }

            // Toggle buttons: icon + dot. Colors: Mic=green, Desktop=orange. Off=gray.
            DrawToggleIconDot(g, micRect, state.IsMicListening, ToggleIcon.Mic, onColor: Color.FromArgb(230, 76, 175, 80), offColor: Color.FromArgb(200, 140, 140, 140), nonTextOpacity);
            DrawToggleIconDot(g, deskRect, state.IsDesktopListening, ToggleIcon.Desktop, onColor: Color.FromArgb(235, 255, 152, 0), offColor: Color.FromArgb(200, 140, 140, 140), nonTextOpacity);

            // Dropdown header (shows selected record time)
            DrawButton(g, dropRect, dropText, labelFont, nonTextOpacity);
            DrawButton(g, gearRect, settingsOpen ? "BACK" : "SET", labelFont, nonTextOpacity);

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
                var txt = string.IsNullOrWhiteSpace(lastText) ? "No transcript yet" : lastText;
                DrawAutoFitText(g, txt, contentRect, boldFont, Color.White, minFontSize: 10f);
            }
            else
            {
                var itemH = ComputePanelItemHeight(contentRect.Height, languageMask);
                var visibleCount = Math.Max(1, (int)Math.Floor(contentRect.Height / itemH));
                var maxScroll = Math.Max(0, items.Count - visibleCount);
                scrollIndex = Math.Clamp(scrollIndex, 0, maxScroll);

                var scrollbarRect = new RectangleF(contentRect.Right + 2, contentRect.Y, 8, contentRect.Height);
                DrawScrollbar(g, scrollbarRect, scrollIndex, visibleCount, items.Count, nonTextOpacity);

                for (var i = 0; i < visibleCount; i++)
                {
                    var idx = scrollIndex + i;
                    if (idx >= items.Count) break;

                    var item = items[idx];
                    var y = contentRect.Y + i * itemH;
                    var rowRect = new RectangleF(contentRect.X, y, contentRect.Width, itemH - 8);

                    var isSelected = idx == selectedIndex;
                    if (isSelected)
                    {
                        using var sel = new SolidBrush(WithNonTextOpacity(90, 255, 255, 255, nonTextOpacity));
                        FillRoundedRect(g, sel, rowRect, 10);
                    }

                    // Source color bar (Mic=green, Desktop=orange).
                    var barColor = item.Source == "Desktop"
                        ? Color.FromArgb(235, 255, 152, 0)
                        : Color.FromArgb(230, 76, 175, 80);
                    using (var bar = new SolidBrush(WithNonTextOpacity(barColor, nonTextOpacity)))
                    {
                        FillRoundedRect(g, bar, new RectangleF(rowRect.X + 2, rowRect.Y + 8, 4, rowRect.Height - 16), 2);
                    }

                    var textX = rowRect.X + 14;
                    var blocks = BuildTranscriptBlocks(item, languageMask);
                    if (blocks.Length == 0)
                    {
                        blocks = BuildTranscriptBlocks(item, TranscriptLanguageMask.Original | TranscriptLanguageMask.Translation1);
                    }

                    var textRect = new RectangleF(textX, rowRect.Y + 6, rowRect.Width - 18, rowRect.Height - 12);
                    DrawTranscriptBlocks(g, blocks, textRect, boldFont, minFontSize: 10f, lineSpacing: 4f);
                }

                var atTop = scrollIndex <= 0;
                var atBottom = scrollIndex >= maxScroll;

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
                    DrawAutoFitText(g, previewText, previewRect, smallFont, Color.White, minFontSize: 9f);
                }
            }

            if (settingsOpen)
            {
                var settingsRect = new RectangleF(16, listY, PanelWidth - 48, PanelHeight - listY - 16);
                using var settingsBg = new SolidBrush(WithNonTextOpacity(225, 18, 18, 18, nonTextOpacity));
                FillRoundedRect(g, settingsBg, settingsRect, 16);

                g.DrawString("Panel placement", boldFont, white, new PointF(settingsRect.X + 24, settingsRect.Y + 18));
                g.DrawString("Choose the anchor here. Fine-tune offsets from the desktop settings panel.", smallFont, dim, new PointF(settingsRect.X + 24, settingsRect.Y + 46));

                var anchorY = settingsRect.Y + 54f;
                var anchorPalmRect = new RectangleF(settingsRect.X + 24, anchorY, 136, 42);
                var anchorBackRect = new RectangleF(anchorPalmRect.Right + 12, anchorY, 156, 42);
                var anchorHeadRect = new RectangleF(anchorBackRect.Right + 12, anchorY, 136, 42);
                DrawFilterChip(g, anchorPalmRect, "Palm", anchorMode == SteamVrOverlayAnchorMode.Palm, chipFont, nonTextOpacity);
                DrawFilterChip(g, anchorBackRect, "Back", anchorMode == SteamVrOverlayAnchorMode.BackOfHand, chipFont, nonTextOpacity);
                DrawFilterChip(g, anchorHeadRect, "Head", anchorMode == SteamVrOverlayAnchorMode.Head, chipFont, nonTextOpacity);

                var handY = anchorPalmRect.Bottom + 20;
                var handRightRect = new RectangleF(settingsRect.X + 24, handY, 136, 42);
                var handLeftRect = new RectangleF(handRightRect.Right + 12, handY, 136, 42);
                DrawFilterChip(g, handRightRect, "Right hand", hand == 0, chipFont, nonTextOpacity);
                DrawFilterChip(g, handLeftRect, "Left hand", hand == 1, chipFont, nonTextOpacity);

                var startupRect = new RectangleF(settingsRect.X + 24, handRightRect.Bottom + 20, 240, 42);
                DrawFilterChip(g, startupRect, "Start with SteamVR", startupAutoLaunch, chipFont, nonTextOpacity);

                var sizeY = startupRect.Bottom + 20;
                var sizeMinusRect = new RectangleF(settingsRect.X + 24, sizeY, 42, 42);
                var sizeValueRect = new RectangleF(sizeMinusRect.Right + 12, sizeY, 144, 42);
                var sizePlusRect = new RectangleF(sizeValueRect.Right + 12, sizeY, 42, 42);
                DrawButton(g, sizeMinusRect, "-", boldFont, nonTextOpacity);
                DrawButton(g, sizeValueRect, $"{panelSize:0.00}m", labelFont, nonTextOpacity);
                DrawButton(g, sizePlusRect, "+", boldFont, nonTextOpacity);

                var resetRect = new RectangleF(settingsRect.X + 24, sizeMinusRect.Bottom + 20, 180, 42);
                DrawButton(g, resetRect, "Reset position", labelFont, nonTextOpacity);

                using var hintBg = new SolidBrush(WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity));
                var hintRect = new RectangleF(settingsRect.X + 24, resetRect.Bottom + 20, settingsRect.Width - 48, 78);
                FillRoundedRect(g, hintBg, hintRect, 12);
                g.DrawString("The in-VR drag handle has been removed to avoid accidental movement while browsing results.", smallFont, white, new RectangleF(hintRect.X + 14, hintRect.Y + 12, hintRect.Width - 28, hintRect.Height - 24));
            }

            // Fallback: if cursor overlay couldn't be created, draw a dot into the panel texture.
            // This is more expensive (requires re-uploading the big texture), but better than no cursor at all.
            if (drawCursorDotInPanel && cursorValid)
            {
                var cx = cursorU * PanelWidth;
                var cy = cursorV * PanelHeight;
                using var dot = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
                using var shadow = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillEllipse(shadow, cx - 8, cy - 8, 16, 16);
                g.FillEllipse(dot, cx - 5, cy - 5, 10, 10);
            }

            return UploadBitmap(overlayHandle, bmp);
        }

        private static float ComputePanelItemHeight(float contentHeight, TranscriptLanguageMask languageMask)
        {
            var lineCount = 0;
            if ((languageMask & TranscriptLanguageMask.Translation1) != 0) lineCount++;
            if ((languageMask & TranscriptLanguageMask.Translation2) != 0) lineCount++;
            if ((languageMask & TranscriptLanguageMask.Translation3) != 0) lineCount++;
            if ((languageMask & TranscriptLanguageMask.Original) != 0) lineCount++;
            if (lineCount <= 0) lineCount = 1;

            var desired = 90f + Math.Max(0, lineCount - 2) * 32f;
            var itemH = Math.Max(120f, desired);
            return Math.Min(itemH, contentHeight);
        }

        private static (string Text, Color Color)[] BuildTranscriptBlocks(
            SteamVrOverlayTranscript item,
            TranscriptLanguageMask languageMask)
        {
            var blocks = new System.Collections.Generic.List<(string Text, Color Color)>(4);

            if ((languageMask & TranscriptLanguageMask.Translation1) != 0 && !string.IsNullOrWhiteSpace(item.Translation))
            {
                blocks.Add((item.Translation, Color.White));
            }
            if ((languageMask & TranscriptLanguageMask.Translation2) != 0 && !string.IsNullOrWhiteSpace(item.Translation2))
            {
                blocks.Add((item.Translation2, Color.FromArgb(235, 240, 240, 240)));
            }
            if ((languageMask & TranscriptLanguageMask.Translation3) != 0 && !string.IsNullOrWhiteSpace(item.Translation3))
            {
                blocks.Add((item.Translation3, Color.FromArgb(235, 240, 240, 240)));
            }
            if ((languageMask & TranscriptLanguageMask.Original) != 0 && !string.IsNullOrWhiteSpace(item.Original))
            {
                blocks.Add((item.Original, Color.FromArgb(235, 220, 220, 220)));
            }

            return blocks.ToArray();
        }

        private static string BuildCombinedText(SteamVrOverlayTranscript item, TranscriptLanguageMask languageMask)
        {
            var blocks = BuildTranscriptBlocks(item, languageMask);
            if (blocks.Length == 0) return string.Empty;
            return string.Join(" | ", blocks.Select(x => x.Text));
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
            using var brush = new SolidBrush(fg);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None
            };
            g.DrawString(text, font, brush, rect, sf);
        }

        private static void UploadCursorTexture(ulong overlayHandle)
        {
            const int w = 64;
            const int h = 64;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            // Outer soft shadow
            using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillEllipse(shadow, 12, 12, 40, 40);
            }

            // White dot with black outline
            using (var dot = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0), 2))
            {
                g.FillEllipse(dot, 20, 20, 24, 24);
                g.DrawEllipse(pen, 20, 20, 24, 24);
            }

            try { UploadBitmap(overlayHandle, bmp); } catch { }
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
            Desktop
        }

        private enum IconButtonKind
        {
            Top,
            PageUp,
            PageDown
        }

        private static void DrawToggleIconDot(Graphics g, RectangleF rect, bool isOn, ToggleIcon icon, Color onColor, Color offColor, float nonTextOpacity)
        {
            using var bg = new SolidBrush(WithNonTextOpacity(70, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, bg, rect, 12);

            var c = isOn ? onColor : offColor;
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
            }

            using var dotBrush = new SolidBrush(c);
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

        private static void DrawScrollbar(Graphics g, RectangleF trackRect, int scrollIndex, int visibleCount, int totalCount, float nonTextOpacity)
        {
            using var track = new SolidBrush(WithNonTextOpacity(60, 255, 255, 255, nonTextOpacity));
            FillRoundedRect(g, track, trackRect, 4);

            if (totalCount <= 0) return;

            var maxScroll = Math.Max(0, totalCount - visibleCount);
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
            var t = Math.Clamp(scrollIndex, 0, maxScroll) / (float)maxScroll;
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
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(text, font, textBrush, new RectangleF(rect.X + 12, rect.Y + 14, rect.Width - 24, rect.Height - 14));
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
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

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

                var overlay = Valve.VR.OpenVR.Overlay;
                if (overlay != null && _overlayHandle != 0)
                {
                    try { overlay.HideOverlay(_overlayHandle); } catch { }
                    try { overlay.DestroyOverlay(_overlayHandle); } catch { }
                }

                if (overlay != null && _cursorOverlayHandle != 0)
                {
                    try { overlay.HideOverlay(_cursorOverlayHandle); } catch { }
                    try { overlay.DestroyOverlay(_cursorOverlayHandle); } catch { }
                }
            }
            catch { }

            try { Valve.VR.OpenVR.Shutdown(); } catch { }
        }
    }
}


