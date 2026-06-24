using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VRCLS.UI.Services
{
    internal static class NonTopmostPopupHelper
    {
        public static void Attach(Window popup, Window? owner)
        {
            if (popup == null)
            {
                throw new ArgumentNullException(nameof(popup));
            }

            popup.Topmost = false;
            if (owner == null)
            {
                return;
            }

            var scope = new OwnerTopmostScope(owner);
            popup.Owner = owner;
            popup.Closed += (_, __) => scope.Dispose();
        }

        public static void AttachIndependent(Window popup, Window? owner)
        {
            if (popup == null)
            {
                throw new ArgumentNullException(nameof(popup));
            }

            popup.Topmost = false;
            // Keep the call shape aligned with Attach, but do not assign Owner or
            // alter owner z-order. Settings must stay Alt+Tab-independent without
            // breaking main-window topmost.
        }

        public static bool? ShowDialog(Window popup, Window? owner)
        {
            if (popup == null)
            {
                throw new ArgumentNullException(nameof(popup));
            }

            Attach(popup, owner);
            return popup.ShowDialog();
        }

        private sealed class OwnerTopmostScope : IDisposable
        {
            private readonly Window _owner;
            private readonly DependencyPropertyDescriptor? _topmostDescriptor;
            private readonly EventHandler _topmostChangedHandler;
            private bool _isDisposed;
            private bool _isApplying;
            private bool _desiredTopmost;

            public OwnerTopmostScope(Window owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _desiredTopmost = owner.Topmost;
                _topmostChangedHandler = (_, __) => OnTopmostChanged();
                _topmostDescriptor = DependencyPropertyDescriptor.FromProperty(Window.TopmostProperty, typeof(Window));
                _topmostDescriptor?.AddValueChanged(_owner, _topmostChangedHandler);
                ApplyNativeTopmost(false);
            }

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _topmostDescriptor?.RemoveValueChanged(_owner, _topmostChangedHandler);
                ApplyNativeTopmost(_owner.Topmost);
            }

            private void OnTopmostChanged()
            {
                if (_isApplying || _isDisposed)
                {
                    return;
                }

                _desiredTopmost = _owner.Topmost;
                if (_desiredTopmost)
                {
                    ApplyNativeTopmost(false);
                }
            }

            private void ApplyNativeTopmost(bool value)
            {
                if (_owner.Dispatcher.CheckAccess())
                {
                    SetTopmostCore(value);
                    return;
                }

                _owner.Dispatcher.Invoke(() => SetTopmostCore(value));
            }

            private void SetTopmostCore(bool value)
            {
                try
                {
                    _isApplying = true;
                    var handle = new WindowInteropHelper(_owner).Handle;
                    if (handle == IntPtr.Zero)
                    {
                        return;
                    }

                    var insertAfter = value ? HWND_TOPMOST : HWND_NOTOPMOST;
                    _ = SetWindowPos(handle, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    _isApplying = false;
                }
            }
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
    }
}
