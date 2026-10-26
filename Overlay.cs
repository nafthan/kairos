using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kairos
{
    /// <summary>
    /// Windows helpers for floating over other apps: letting clicks pass through a window, and noticing a full-screen
    /// app (a game, a video, a presentation) on the same screen.
    /// </summary>
    public static class Overlay
    {
        // ----- Click-through -----

        /// <summary>Makes the window ignore the mouse (clicks go to whatever is underneath), or take it back.</summary>
        public static void SetClickThrough(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            long wanted = on ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED : style & ~WS_EX_TRANSPARENT;
            if (wanted != style) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(wanted));
        }

        public static bool IsCtrlHeld() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        public static bool IsMouseButtonDown() =>
            (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        /// <summary>Whether the mouse pointer is over the window (works even while clicks pass through it).</summary>
        public static bool IsCursorOver(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetCursorPos(out var p) || !GetWindowRect(hwnd, out var r)) return false;
            return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        }

        // ----- Full-screen apps -----

        /// <summary>
        /// True when the app in front covers the whole of the screen <paramref name="hwnd"/> is on: a game, a video
        /// in full screen, a presentation. Full-screen apps on other screens don't count, and nor do ordinary
        /// maximised windows (they keep their title bar).
        /// </summary>
        public static bool FullScreenAppOnSameScreen(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd || !IsWindowVisible(fg) || IsIconic(fg)) return false;

            // Kairos's own windows, and the desktop and taskbar, aren't apps in full screen.
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            switch (cls.ToString())
            {
                case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":
                case "Windows.UI.Core.CoreWindow": case "XamlExplorerHostIslandWindow": // Start, search, task view
                    return false;
            }

            var screen = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != screen) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(screen, ref info) || !GetWindowRect(fg, out var r)) return false;

            bool coversScreen = r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top &&
                                r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
            if (!coversScreen) return false;

            // A maximised window with a title bar can cover the screen too (with an auto-hiding taskbar): not full screen.
            long style = GetWindowLongPtr(fg, GWL_STYLE).ToInt64();
            return (style & WS_CAPTION) != WS_CAPTION;
        }

        // ----- Win32 -----

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_CAPTION = 0x00C00000;
        private const int VK_CONTROL = 0x11, VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    }
}
