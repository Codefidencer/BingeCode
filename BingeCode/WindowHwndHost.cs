using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BingeCode
{
    /// <summary>
    /// Embeds a native Windows window (HWND) inside a WPF layout using HwndHost.
    /// On creation it reparents the target window and strips its title bar / borders.
    /// On disposal it returns the window to the desktop and restores its original style.
    /// </summary>
    public class WindowHwndHost : HwndHost
    {
        private readonly IntPtr _targetHwnd;
        private IntPtr _hostHwnd;
        private int _originalStyle;

        public WindowHwndHost(IntPtr targetHwnd)
        {
            _targetHwnd = targetHwnd;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            // Create a plain child window that acts as the container
            _hostHwnd = NativeMethods.CreateWindowEx(
                0, "static", "",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
                0, 0, 100, 100,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_targetHwnd != IntPtr.Zero)
            {
                // Save the original style so we can restore it later
                _originalStyle = NativeMethods.GetWindowLong(_targetHwnd, NativeMethods.GWL_STYLE);

                // Strip decorations and make it a child window
                int newStyle = (_originalStyle
                    & ~(NativeMethods.WS_CAPTION
                      | NativeMethods.WS_THICKFRAME
                      | NativeMethods.WS_SYSMENU
                      | NativeMethods.WS_MINIMIZEBOX
                      | NativeMethods.WS_MAXIMIZEBOX))
                    | NativeMethods.WS_CHILD;

                NativeMethods.SetWindowLong(_targetHwnd, NativeMethods.GWL_STYLE, newStyle);
                // Force the non-client area to recalculate so stripped borders don't render as black space
                NativeMethods.SetWindowPos(_targetHwnd, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE);
                NativeMethods.SetParent(_targetHwnd, _hostHwnd);
                NativeMethods.ShowWindow(_targetHwnd, NativeMethods.SW_SHOW);
            }

            return new HandleRef(this, _hostHwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_targetHwnd != IntPtr.Zero)
            {
                // Return the window to the desktop and restore its style
                NativeMethods.SetParent(_targetHwnd, NativeMethods.GetDesktopWindow());
                NativeMethods.SetWindowLong(_targetHwnd, NativeMethods.GWL_STYLE, _originalStyle);
                NativeMethods.ShowWindow(_targetHwnd, NativeMethods.SW_SHOW);
            }

            if (_hostHwnd != IntPtr.Zero)
                NativeMethods.DestroyWindow(_hostHwnd);
        }

        /// <summary>
        /// Call this whenever the containing WPF element changes size
        /// so the embedded window fills the available space.
        /// </summary>
        public void Resize(int width, int height)
        {
            if (_hostHwnd != IntPtr.Zero)
                NativeMethods.MoveWindow(_hostHwnd, 0, 0, width, height, true);

            if (_targetHwnd != IntPtr.Zero)
                NativeMethods.MoveWindow(_targetHwnd, 0, 0, width, height, true);
        }
    }
}
