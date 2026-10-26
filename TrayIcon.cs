using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Kairos
{
    /// <summary>
    /// Kairos's icon in the notification area (by the clock), built straight on Windows' Shell_NotifyIcon, so no
    /// extra libraries are needed. Left-click shows or hides Kairos; right-click opens a menu in the Kairos style.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private const int CallbackMessage = 0x8000 + 0x4B; // WM_APP + 'K'
        private readonly HwndSource _window;
        private readonly int _taskbarCreated;
        private IntPtr _icon;
        private string _tip = "Kairos";
        private bool _added, _disposed;

        /// <summary>Left-click on the icon.</summary>
        public event Action? Clicked;
        /// <summary>Right-click: return the menu to show (it's rebuilt each time, so ticks are up to date).</summary>
        public Func<ContextMenu>? MenuBuilder { get; set; }

        public TrayIcon()
        {
            // A hidden window to receive the icon's mouse messages. (Not a message-only window: those miss the
            // "taskbar restarted" broadcast, and the icon would vanish after Explorer restarts.)
            _window = new HwndSource(new HwndSourceParameters("KairosTray") { Width = 0, Height = 0, WindowStyle = 0 });
            _window.AddHook(WndProc);
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _icon = LoadIcon();
            Add();
        }

        /// <summary>The text shown when you hover over the icon (Windows allows up to 127 characters).</summary>
        public void SetTip(string text)
        {
            _tip = text.Length > 127 ? text[..126] + "…" : text;
            if (!_added) return;
            var data = Data(NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        private void Add()
        {
            var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
        }

        private NOTIFYICONDATA Data(int flags) => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
        };

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == CallbackMessage)
            {
                int mouse = lParam.ToInt32() & 0xFFFF;
                if (mouse == WM_LBUTTONUP) Clicked?.Invoke();
                else if (mouse == WM_RBUTTONUP) ShowMenu();
                handled = true;
            }
            else if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                Add(); // Explorer restarted: put the icon back
            }
            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            var menu = MenuBuilder?.Invoke();
            if (menu == null || !GetCursorPos(out var p)) return;

            // So the menu closes when you click somewhere else.
            SetForegroundWindow(_window.Handle);

            var point = new Point(p.X, p.Y);
            var toDip = _window.CompositionTarget?.TransformFromDevice;
            if (toDip != null) point = toDip.Value.Transform(point);
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = point.X;
            menu.VerticalOffset = point.Y;
            menu.IsOpen = true;
        }

        /// <summary>The small icon, taken from Kairos.ico at the size Windows uses in the notification area.</summary>
        private static IntPtr LoadIcon()
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/Kairos.ico"));
                if (info == null) return IntPtr.Zero;
                byte[] ico;
                using (var ms = new MemoryStream()) { info.Stream.CopyTo(ms); ico = ms.ToArray(); }

                int want = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
                // The icon file lists each size: pick the smallest that's at least as big as wanted.
                int count = BitConverter.ToUInt16(ico, 4);
                int best = -1, bestSize = int.MaxValue, largest = -1, largestSize = 0;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + i * 16;
                    int size = ico[e] == 0 ? 256 : ico[e];
                    if (size >= want && size < bestSize) { best = i; bestSize = size; }
                    if (size > largestSize) { largest = i; largestSize = size; }
                }
                if (best < 0) best = largest;
                if (best < 0) return IntPtr.Zero;

                int entry = 6 + best * 16;
                int length = BitConverter.ToInt32(ico, entry + 8);
                int offset = BitConverter.ToInt32(ico, entry + 12);
                var image = new byte[length];
                Array.Copy(ico, offset, image, 0, length);
                return CreateIconFromResourceEx(image, length, true, 0x00030000, want, want, 0);
            }
            catch { return IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_added)
            {
                var data = Data(0);
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }
            if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
            _window.RemoveHook(WndProc);
            _window.Dispose();
        }

        // ----- Win32 -----

        private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
        private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
        private const int SM_CXSMICON = 49;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] data, int size, bool icon, int version, int cx, int cy, int flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
