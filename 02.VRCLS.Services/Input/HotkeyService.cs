using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VRCLS.Core.Interfaces;

namespace VRCLS.Services.Input
{
    /// <summary>
    /// 热键服务实现
    /// 使用 Windows 低级键盘钩子 (WH_KEYBOARD_LL) 捕获键盘输入，并通过 GetAsyncKeyState 轮询兜底。
    /// </summary>
    public class HotkeyService : IHotkeyService
    {
        #region Windows API

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int KeyPollingIntervalMs = 15;
        private const int HookHealthProbeIntervalMs = 50;

        // 虚拟键码
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // Alt
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        #endregion

        #region Fields

        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        private bool _isRunning;
        private bool _disposed;
        private readonly object _stateLock = new();
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        private volatile bool _pollingFallbackEnabled;

        // 热键注册表: hotkeyType -> (modifiers, keyCode)
        private readonly Dictionary<string, (Keys modifiers, Keys keyCode, string hotkeyString)> _registeredHotkeys = new();

        // 按住说话注册表: keyType -> (keyCode, keyName)
        private readonly Dictionary<string, (Keys keyCode, string keyName)> _registeredPressKeys = new();

        // 按键状态跟踪
        private readonly Dictionary<string, bool> _pressKeyStates = new();

        // 热键组合状态跟踪，避免按住组合键时由按键重复或轮询重复触发。
        private readonly Dictionary<string, bool> _hotkeyActiveStates = new();

        // Release delay (ms) per press-key type.
        private readonly Dictionary<string, int> _pressKeyReleaseDelayMs = new();

        // 当前按下的修饰键
        private bool _isShiftPressed;
        private bool _isCtrlPressed;
        private bool _isAltPressed;
        private readonly object _lock = new();

        #endregion

        #region Events

        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
        public event EventHandler<KeyStateEventArgs>? KeyStateChanged;

        #endregion

        #region Properties

        public bool IsRunning => _isRunning;

        #endregion

        #region Public Methods

        public HotkeyService()
        {
            _proc = HookCallback;
        }

        public void RegisterHotkey(string hotkeyString, string hotkeyType)
        {
            if (string.IsNullOrWhiteSpace(hotkeyString) || string.IsNullOrWhiteSpace(hotkeyType))
                return;

            var (modifiers, keyCode) = ParseHotkeyString(hotkeyString);

            lock (_lock)
            {
                _registeredHotkeys[hotkeyType] = (modifiers, keyCode, hotkeyString);
                _hotkeyActiveStates[hotkeyType] = false;
            }

            Debug.WriteLine($"[HotkeyService] 注册热键: {hotkeyType} = {hotkeyString} (Modifiers: {modifiers}, Key: {keyCode})");
        }

        public void RegisterPressKey(string keyName, string keyType, int releaseDelayMs = 0)
        {
            if (string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(keyType))
                return;

            var keyCode = ParseKeyName(keyName);

            lock (_lock)
            {
                _registeredPressKeys[keyType] = (keyCode, keyName.ToLower());
                _pressKeyStates[keyType] = false;
                _pressKeyReleaseDelayMs[keyType] = Math.Max(0, releaseDelayMs);
            }

            Debug.WriteLine($"[HotkeyService] 注册按住说话: {keyType} = {keyName} (KeyCode: {keyCode})");
        }

        public void UnregisterHotkey(string hotkeyType)
        {
            lock (_lock)
            {
                _registeredHotkeys.Remove(hotkeyType);
                _hotkeyActiveStates.Remove(hotkeyType);
            }
        }

        public void UnregisterPressKey(string keyType)
        {
            lock (_lock)
            {
                _registeredPressKeys.Remove(keyType);
                _pressKeyStates.Remove(keyType);
                _pressKeyReleaseDelayMs.Remove(keyType);
            }
        }

        public void Start()
        {
            lock (_stateLock)
            {
                if (_isRunning)
                    return;

                var hookInstalled = TryInstallKeyboardHook();
                if (hookInstalled)
                {
                    StartKeyboardStateMonitor(fallbackEnabled: false);
                }
                else
                {
                    StartKeyboardStateMonitor(fallbackEnabled: true);
                }

                _isRunning = true;
                Debug.WriteLine("[HotkeyService] Keyboard listener started");
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_isRunning)
                    return;

                if (_hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }

                StopKeyboardStateMonitor();
                _isRunning = false;
                Debug.WriteLine("[HotkeyService] Keyboard listener stopped");
            }
        }
        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }

        #endregion

        #region Private Methods

        private bool TryInstallKeyboardHook()
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;

            if (curModule == null)
            {
                Debug.WriteLine("[HotkeyService] Current module is unavailable; keyboard polling fallback will be used.");
                return false;
            }

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
            if (_hookId == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[HotkeyService] Failed to install keyboard hook: {error}; keyboard polling fallback will be used.");
                return false;
            }

            Debug.WriteLine("[HotkeyService] Keyboard hook started");
            return true;
        }

        private void StartKeyboardStateMonitor(bool fallbackEnabled)
        {
            _pollingFallbackEnabled = fallbackEnabled;
            _pollingCts = new CancellationTokenSource();
            _pollingTask = Task.Run(() => PollKeyboardStateAsync(_pollingCts.Token));
            Debug.WriteLine(fallbackEnabled
                ? "[HotkeyService] Keyboard polling fallback started"
                : "[HotkeyService] Keyboard hook health probe started");
        }

        private void StopKeyboardStateMonitor()
        {
            var pollingCts = _pollingCts;
            var pollingTask = _pollingTask;
            _pollingCts = null;
            _pollingTask = null;
            _pollingFallbackEnabled = false;

            if (pollingCts == null)
            {
                return;
            }

            pollingCts.Cancel();

            try
            {
                pollingTask?.Wait(250);
            }
            catch (AggregateException ex) when (IsOnlyTaskCanceled(ex))
            {
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                pollingCts.Dispose();
            }
        }

        private async Task PollKeyboardStateAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_pollingFallbackEnabled)
                    {
                        PollRegisteredHotkeys();
                        PollRegisteredPressKeys();
                        await Task.Delay(KeyPollingIntervalMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (ShouldEnablePollingFallback())
                    {
                        _pollingFallbackEnabled = true;
                        Debug.WriteLine("[HotkeyService] Keyboard hook missed registered input; polling fallback enabled");
                        PollRegisteredHotkeys();
                        PollRegisteredPressKeys();
                        await Task.Delay(KeyPollingIntervalMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(HookHealthProbeIntervalMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HotkeyService] Keyboard polling error: {ex}");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private bool ShouldEnablePollingFallback()
        {
            lock (_lock)
            {
                foreach (var kvp in _registeredPressKeys)
                {
                    var (keyCode, _) = kvp.Value;
                    var actualPressed = keyCode != Keys.None && IsKeyPressed(keyCode);
                    var observedPressed = _pressKeyStates.GetValueOrDefault(kvp.Key, false);

                    if (actualPressed != observedPressed)
                    {
                        return true;
                    }
                }

                foreach (var kvp in _registeredHotkeys)
                {
                    var (modifiers, keyCode, _) = kvp.Value;
                    var actualActive = keyCode != Keys.None
                        && IsKeyPressed(keyCode)
                        && GetCurrentPressedModifiers() == modifiers;
                    var observedActive = _hotkeyActiveStates.GetValueOrDefault(kvp.Key, false);

                    if (actualActive != observedActive)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vkCode = (int)hookStruct.vkCode;
                var key = (Keys)vkCode;

                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                // 更新修饰键状态
                UpdateModifierState(vkCode, isKeyDown, isKeyUp);

                if (isKeyDown)
                {
                    HandleKeyDown(key);
                }
                else if (isKeyUp)
                {
                    HandleKeyUp(key);
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void UpdateModifierState(int vkCode, bool isKeyDown, bool isKeyUp)
        {
            switch (vkCode)
            {
                case VK_SHIFT:
                case 0xA0: // VK_LSHIFT
                case 0xA1: // VK_RSHIFT
                    if (isKeyDown) _isShiftPressed = true;
                    else if (isKeyUp) _isShiftPressed = false;
                    break;

                case VK_CONTROL:
                case 0xA2: // VK_LCONTROL
                case 0xA3: // VK_RCONTROL
                    if (isKeyDown) _isCtrlPressed = true;
                    else if (isKeyUp) _isCtrlPressed = false;
                    break;

                case VK_MENU:
                case 0xA4: // VK_LMENU
                case 0xA5: // VK_RMENU
                    if (isKeyDown) _isAltPressed = true;
                    else if (isKeyUp) _isAltPressed = false;
                    break;
            }
        }

        private void HandleKeyDown(Keys key)
        {
            // 检查热键组合
            CheckHotkeys(key);

            // 检查按住说话
            CheckPressKeyDown(key);
        }

        private void HandleKeyUp(Keys key)
        {
            // 检查热键组合释放
            CheckHotkeyRelease(key);

            // 检查按住说话释放
            CheckPressKeyUp(key);
        }

        private void CheckHotkeys(Keys key)
        {
            // 构建当前修饰键状态
            Keys currentModifiers = Keys.None;
            if (_isShiftPressed) currentModifiers |= Keys.Shift;
            if (_isCtrlPressed) currentModifiers |= Keys.Control;
            if (_isAltPressed) currentModifiers |= Keys.Alt;

            lock (_lock)
            {
                foreach (var kvp in _registeredHotkeys)
                {
                    var (modifiers, keyCode, hotkeyString) = kvp.Value;

                    // 检查修饰键和主键是否匹配
                    if (currentModifiers == modifiers && key == keyCode)
                    {
                        if (_hotkeyActiveStates.GetValueOrDefault(kvp.Key, false))
                        {
                            continue;
                        }

                        _hotkeyActiveStates[kvp.Key] = true;
                        RaiseHotkeyPressed(hotkeyString, kvp.Key);
                    }
                }
            }
        }

        private void CheckHotkeyRelease(Keys key)
        {
            lock (_lock)
            {
                foreach (var kvp in _registeredHotkeys)
                {
                    var (modifiers, keyCode, _) = kvp.Value;

                    if (key == keyCode || IsModifierReleaseForHotkey(key, modifiers))
                    {
                        _hotkeyActiveStates[kvp.Key] = false;
                    }
                }
            }
        }

        private void CheckPressKeyDown(Keys key)
        {
            lock (_lock)
            {
                foreach (var kvp in _registeredPressKeys)
                {
                    var (keyCode, keyName) = kvp.Value;
                    var keyType = kvp.Key;

                    if (key == keyCode)
                    {
                        // 如果之前未按下，触发按下事件
                        if (!_pressKeyStates.GetValueOrDefault(keyType, false))
                        {
                            _pressKeyStates[keyType] = true;

                            RaiseKeyStateChanged(keyName, keyType, true);
                        }
                    }
                }
            }
        }

        private void CheckPressKeyUp(Keys key)
        {
            lock (_lock)
            {
                foreach (var kvp in _registeredPressKeys)
                {
                    var (keyCode, keyName) = kvp.Value;
                    var keyType = kvp.Key;

                    if (key == keyCode && _pressKeyStates.GetValueOrDefault(keyType, false))
                    {
                        _pressKeyStates[keyType] = false;

                        RaiseKeyStateChanged(keyName, keyType, false);
                    }
                }
            }
        }

        private void PollRegisteredHotkeys()
        {
            lock (_lock)
            {
                foreach (var kvp in _registeredHotkeys)
                {
                    var hotkeyType = kvp.Key;
                    var (modifiers, keyCode, hotkeyString) = kvp.Value;
                    var isActive = keyCode != Keys.None
                        && IsKeyPressed(keyCode)
                        && GetCurrentPressedModifiers() == modifiers;
                    var wasActive = _hotkeyActiveStates.GetValueOrDefault(hotkeyType, false);

                    if (isActive == wasActive)
                    {
                        continue;
                    }

                    _hotkeyActiveStates[hotkeyType] = isActive;

                    if (isActive)
                    {
                        RaiseHotkeyPressed(hotkeyString, hotkeyType);
                    }
                }
            }
        }

        private void PollRegisteredPressKeys()
        {
            lock (_lock)
            {
                foreach (var kvp in _registeredPressKeys)
                {
                    var keyType = kvp.Key;
                    var (keyCode, keyName) = kvp.Value;
                    var isPressed = keyCode != Keys.None && IsKeyPressed(keyCode);
                    var wasPressed = _pressKeyStates.GetValueOrDefault(keyType, false);

                    if (isPressed == wasPressed)
                    {
                        continue;
                    }

                    _pressKeyStates[keyType] = isPressed;
                    RaiseKeyStateChanged(keyName, keyType, isPressed);
                }
            }
        }

        private void RaiseHotkeyPressed(string hotkeyString, string hotkeyType)
        {
            Debug.WriteLine($"[HotkeyService] 热键触发: {hotkeyType} ({hotkeyString})");

            // 在新线程中触发事件，避免阻塞钩子
            Task.Run(() =>
            {
                HotkeyPressed?.Invoke(this, new HotkeyEventArgs
                {
                    HotkeyId = hotkeyString,
                    HotkeyType = hotkeyType
                });
            });
        }

        private void RaiseKeyStateChanged(string keyName, string keyType, bool isPressed)
        {
            Debug.WriteLine($"[HotkeyService] 按键{(isPressed ? "按下" : "释放")}: {keyType} ({keyName})");

            if (isPressed)
            {
                Task.Run(() =>
                {
                    KeyStateChanged?.Invoke(this, new KeyStateEventArgs
                    {
                        KeyName = keyName,
                        IsPressed = true,
                        KeyType = keyType
                    });
                });
                return;
            }

            var releaseDelayMs = _pressKeyReleaseDelayMs.GetValueOrDefault(keyType, 0);
            Task.Run(async () =>
            {
                if (releaseDelayMs > 0)
                {
                    await Task.Delay(releaseDelayMs);
                    lock (_lock)
                    {
                        // Pressed again during delay window: skip this release event.
                        if (_pressKeyStates.GetValueOrDefault(keyType, false))
                        {
                            return;
                        }
                    }
                }

                KeyStateChanged?.Invoke(this, new KeyStateEventArgs
                {
                    KeyName = keyName,
                    IsPressed = false,
                    KeyType = keyType
                });
            });
        }

        private static bool IsKeyPressed(Keys key)
        {
            return (GetAsyncKeyState((int)key) & unchecked((short)0x8000)) != 0;
        }

        private static Keys GetCurrentPressedModifiers()
        {
            Keys currentModifiers = Keys.None;

            if (IsVirtualKeyPressed(VK_SHIFT) || IsVirtualKeyPressed(0xA0) || IsVirtualKeyPressed(0xA1))
            {
                currentModifiers |= Keys.Shift;
            }

            if (IsVirtualKeyPressed(VK_CONTROL) || IsVirtualKeyPressed(0xA2) || IsVirtualKeyPressed(0xA3))
            {
                currentModifiers |= Keys.Control;
            }

            if (IsVirtualKeyPressed(VK_MENU) || IsVirtualKeyPressed(0xA4) || IsVirtualKeyPressed(0xA5))
            {
                currentModifiers |= Keys.Alt;
            }

            return currentModifiers;
        }

        private static bool IsVirtualKeyPressed(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
        }

        private static bool IsModifierReleaseForHotkey(Keys key, Keys modifiers)
        {
            return (modifiers.HasFlag(Keys.Shift) && (key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey))
                || (modifiers.HasFlag(Keys.Control) && (key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey))
                || (modifiers.HasFlag(Keys.Alt) && (key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu));
        }

        private static bool IsOnlyTaskCanceled(AggregateException exception)
        {
            foreach (var innerException in exception.InnerExceptions)
            {
                if (innerException is not OperationCanceledException)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 解析热键字符串，如 "&lt;alt&gt;+1", "&lt;ctrl&gt;+&lt;shift&gt;+m"
        /// </summary>
        private (Keys modifiers, Keys keyCode) ParseHotkeyString(string hotkeyString)
        {
            Keys modifiers = Keys.None;
            Keys keyCode = Keys.None;

            var parts = hotkeyString.ToLower().Split('+');

            foreach (var part in parts)
            {
                var trimmed = part.Trim();

                switch (trimmed)
                {
                    case "<alt>":
                    case "alt":
                        modifiers |= Keys.Alt;
                        break;

                    case "<ctrl>":
                    case "ctrl":
                    case "<control>":
                    case "control":
                        modifiers |= Keys.Control;
                        break;

                    case "<shift>":
                    case "shift":
                        modifiers |= Keys.Shift;
                        break;

                    default:
                        // 解析主键
                        keyCode = ParseKeyName(trimmed);
                        break;
                }
            }

            return (modifiers, keyCode);
        }

        /// <summary>
        /// 解析按键名称为 Keys 枚举
        /// </summary>
        private Keys ParseKeyName(string keyName)
        {
            var name = keyName.Trim().ToLower();

            // 数字键
            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                return (Keys)((int)Keys.D0 + (name[0] - '0'));
            }

            // 字母键
            if (name.Length == 1 && char.IsLetter(name[0]))
            {
                return (Keys)((int)Keys.A + (char.ToUpper(name[0]) - 'A'));
            }

            // 特殊键
            return name switch
            {
                "space" => Keys.Space,
                "enter" => Keys.Enter,
                "return" => Keys.Return,
                "tab" => Keys.Tab,
                "escape" => Keys.Escape,
                "esc" => Keys.Escape,
                "backspace" => Keys.Back,
                "delete" => Keys.Delete,
                "del" => Keys.Delete,
                "insert" => Keys.Insert,
                "ins" => Keys.Insert,
                "home" => Keys.Home,
                "end" => Keys.End,
                "pageup" => Keys.PageUp,
                "pagedown" => Keys.PageDown,
                "up" => Keys.Up,
                "down" => Keys.Down,
                "left" => Keys.Left,
                "right" => Keys.Right,
                "f1" => Keys.F1,
                "f2" => Keys.F2,
                "f3" => Keys.F3,
                "f4" => Keys.F4,
                "f5" => Keys.F5,
                "f6" => Keys.F6,
                "f7" => Keys.F7,
                "f8" => Keys.F8,
                "f9" => Keys.F9,
                "f10" => Keys.F10,
                "f11" => Keys.F11,
                "f12" => Keys.F12,
                "numpad0" => Keys.NumPad0,
                "numpad1" => Keys.NumPad1,
                "numpad2" => Keys.NumPad2,
                "numpad3" => Keys.NumPad3,
                "numpad4" => Keys.NumPad4,
                "numpad5" => Keys.NumPad5,
                "numpad6" => Keys.NumPad6,
                "numpad7" => Keys.NumPad7,
                "numpad8" => Keys.NumPad8,
                "numpad9" => Keys.NumPad9,
                "multiply" => Keys.Multiply,
                "add" => Keys.Add,
                "subtract" => Keys.Subtract,
                "decimal" => Keys.Decimal,
                "divide" => Keys.Divide,
                "capslock" => Keys.CapsLock,
                "numlock" => Keys.NumLock,
                "scrolllock" => Keys.Scroll,
                "printscreen" => Keys.PrintScreen,
                "pause" => Keys.Pause,
                _ => Keys.None
            };
        }

        #endregion
    }
}
