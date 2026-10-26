using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Kairos
{
    /// <summary>
    /// The Spotify desktop app itself: is it running, open it, keep it minimised, close it.
    /// (Playback is handled separately through Windows' media controls.)
    /// </summary>
    public static class SpotifyApp
    {
        private const string ProcessName = "Spotify";

        public static bool IsRunning()
        {
            var procs = Process.GetProcessesByName(ProcessName);
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }

        /// <summary>
        /// Opens Spotify. The installer version is started directly (minimised if asked);
        /// the Microsoft Store version is opened through its spotify: link.
        /// </summary>
        public static bool Launch(bool minimised)
        {
            try
            {
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo(exe, minimised ? "--minimized" : "")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exe)!,
                    })?.Dispose();
                }
                else
                {
                    Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true })?.Dispose();
                }
            }
            catch
            {
                return false; // Spotify isn't installed
            }

            // The Store version ignores --minimized, and some versions restore themselves once loaded,
            // so minimise the window ourselves once it appears.
            if (minimised) _ = MinimiseWhenReadyAsync();
            return true;
        }

        private static async Task MinimiseWhenReadyAsync()
        {
            var until = DateTime.UtcNow.AddSeconds(25);
            IntPtr window = IntPtr.Zero;
            while (DateTime.UtcNow < until && window == IntPtr.Zero)
            {
                await Task.Delay(300);
                window = FindMainWindow();
            }
            if (window == IntPtr.Zero) return;

            // Keep it minimised for a few seconds while Spotify finishes loading (it sometimes pops back up).
            var settle = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < settle)
            {
                if (!IsIconic(window)) ShowWindow(window, SW_SHOWMINNOACTIVE);
                await Task.Delay(400);
                var latest = FindMainWindow();
                if (latest != IntPtr.Zero) window = latest;
            }
        }

        private static IntPtr FindMainWindow()
        {
            var procs = Process.GetProcessesByName(ProcessName);
            try
            {
                foreach (var p in procs)
                {
                    p.Refresh();
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindowVisible(h)) return h;
                }
            }
            catch { }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
            return IntPtr.Zero;
        }

        // ----- Spotify's main window (including when hidden) -----

        /// <summary>When Spotify was started (its earliest process), or null if it isn't running.</summary>
        public static DateTime? StartedAt()
        {
            var procs = Process.GetProcessesByName(ProcessName);
            try
            {
                DateTime? first = null;
                foreach (var p in procs)
                {
                    try { var t = p.StartTime; if (first == null || t < first) first = t; } catch { }
                }
                return first;
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        /// <summary>
        /// Spotify's main windows, found by walking every top-level window (so hidden ones are found too).
        /// A main window belongs to a Spotify process, has a title, no owner, and isn't a tool window.
        /// </summary>
        public static List<IntPtr> MainWindows(bool includeHidden)
        {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(ProcessName)) { pids.Add((uint)p.Id); p.Dispose(); }
            var found = new List<(IntPtr Handle, long Area)>();
            if (pids.Count == 0) return new List<IntPtr>();

            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid)) return true;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                if (!includeHidden && !IsWindowVisible(h)) return true;
                GetWindowRect(h, out var r);
                found.Add((h, (long)(r.Right - r.Left) * (r.Bottom - r.Top)));
                return true;
            }, IntPtr.Zero);

            return found.OrderByDescending(f => f.Area).Select(f => f.Handle).ToList();
        }

        /// <summary>Spotify's main window if it's on screen (visible, minimised counts), otherwise zero.</summary>
        public static IntPtr VisibleMainWindow() => MainWindows(includeHidden: false).FirstOrDefault();

        /// <summary>Hides a window completely: no window, no taskbar button. The app keeps running.</summary>
        public static void HideWindow(IntPtr h)
        {
            if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
        }

        /// <summary>Brings a hidden Spotify window back, minimised (out of the way) or in front.</summary>
        public static void ShowWindowAgain(IntPtr h, bool minimised)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) h = MainWindows(includeHidden: true).FirstOrDefault();
            if (h == IntPtr.Zero) return;
            if (minimised) ShowWindow(h, SW_SHOWMINNOACTIVE);
            else
            {
                ShowWindow(h, SW_SHOW);
                ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
        }

        public static bool IsMinimised(IntPtr h) => h != IntPtr.Zero && IsIconic(h);
        public static bool IsInFront(IntPtr h) => h != IntPtr.Zero && GetForegroundWindow() == h;

        /// <summary>
        /// Closes Spotify: politely first (like clicking its X), then, if it's still running a few seconds
        /// later (e.g. it's set to minimise to the tray instead of closing), it's ended.
        /// </summary>
        public static async Task CloseAsync()
        {
            // Ask each main window to close, including a hidden one.
            foreach (var h in MainWindows(includeHidden: true))
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(250);
                if (!IsRunning()) return;
            }

            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }

        // ----- Win32 -----

        private const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;
        private const uint GW_OWNER = 4, WM_CLOSE = 0x0010;
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    }

    /// <summary>Small Windows helpers for the lyrics window: centring on its monitor and a system-wide shortcut.</summary>
    public static class NativeWindow
    {
        /// <summary>Moves the window to the horizontal centre of the monitor it's on (height unchanged).</summary>
        public static void CentreHorizontally(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            // All in physical pixels, so it's right on any scaling and any monitor.
            int width = rect.Right - rect.Left;
            int left = info.rcWork.Left + (info.rcWork.Right - info.rcWork.Left - width) / 2;
            SetWindowPos(hwnd, IntPtr.Zero, left, rect.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Makes the window as wide as the monitor it's on (height and vertical position unchanged).</summary>
        public static void StretchAcrossMonitor(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            SetWindowPos(hwnd, IntPtr.Zero, info.rcWork.Left, rect.Top, info.rcWork.Right - info.rcWork.Left, rect.Bottom - rect.Top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

        /// <summary>Registers a shortcut that works in any app. Returns false if another program already uses it.</summary>
        public static bool RegisterShortcut(IntPtr hwnd, int id, uint modifiers, uint key) =>
            hwnd != IntPtr.Zero && RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, key);

        public static void UnregisterShortcut(IntPtr hwnd, int id)
        {
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, id);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
