using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kairos
{
    /// <summary>
    /// Pick a colour from anywhere on screen. A see-through layer covers every monitor to catch the click,
    /// and a loupe follows the cursor showing the pixels underneath, magnified, with the colour's hex code.
    /// </summary>
    public sealed class Eyedropper : Window
    {
        private const int Grab = 11;          // pixels captured around the cursor (odd, so there's a centre)
        private const double Zoom = 11;       // how much the loupe magnifies them

        private readonly Action<Color> _onPicked;
        private readonly Window _loupe;
        private readonly Image _zoomed = new() { Width = Grab * Zoom, Height = Grab * Zoom, Stretch = Stretch.Fill };
        private readonly Border _swatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) };
        private readonly TextBlock _hex = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private Color _current = Colors.Black;
        private bool _done;

        public static void Pick(Action<Color> onPicked) => new Eyedropper(onPicked).Show();

        private Eyedropper(Action<Color> onPicked)
        {
            _onPicked = onPicked;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // invisible, but catches the mouse
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Cursor = Cursors.Cross;
            Left = 0; Top = 0; Width = 1; Height = 1;

            // ---- Loupe ----
            RenderOptions.SetBitmapScalingMode(_zoomed, BitmapScalingMode.NearestNeighbor);
            var centre = new Border
            {
                Width = Zoom + 2, Height = Zoom + 2,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Opacity = 1 },
            };
            var picture = new Grid { ClipToBounds = true };
            picture.Children.Add(_zoomed);
            picture.Children.Add(centre);

            var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            info.Children.Add(_swatch);
            info.Children.Add(_hex);

            var body = new StackPanel();
            body.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = picture });
            body.Children.Add(info);
            body.Children.Add(new TextBlock
            {
                Text = "Click to pick  ·  Esc to cancel",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });

            _loupe = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                IsHitTestVisible = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Content = new Border
                {
                    Margin = new Thickness(4, 4, 12, 12),
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(8),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.5 },
                    Child = body,
                },
            };

            SourceInitialized += (_, _) =>
            {
                // Cover every monitor, in real screen pixels (works with any scaling).
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, HWND_TOPMOST,
                    GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                    GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN), SWP_SHOWWINDOW);
            };
            Loaded += (_, _) =>
            {
                _loupe.Show();
                Activate();
                Focus();
                UpdateUnderCursor();
            };
            MouseMove += (_, _) => UpdateUnderCursor();
            MouseLeftButtonDown += (_, e) => { e.Handled = true; UpdateUnderCursor(); Finish(picked: true); };
            MouseRightButtonDown += (_, e) => { e.Handled = true; Finish(picked: false); };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(picked: false); };
            Deactivated += (_, _) => Finish(picked: false); // switched away (Alt+Tab): cancel
        }

        private void Finish(bool picked)
        {
            if (_done) return;
            _done = true;
            _loupe.Close();
            Close();
            if (picked) _onPicked(_current);
        }

        /// <summary>Grabs the pixels around the cursor, shows them magnified, and reads the centre one.</summary>
        private void UpdateUnderCursor()
        {
            if (_done || !GetCursorPos(out var p)) return;

            var shot = Capture(p.X - Grab / 2, p.Y - Grab / 2, Grab, Grab);
            if (shot != null)
            {
                _zoomed.Source = shot;
                var px = new byte[4];
                try
                {
                    new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0)
                        .CopyPixels(new Int32Rect(Grab / 2, Grab / 2, 1, 1), px, 4, 0);
                    _current = Color.FromRgb(px[2], px[1], px[0]);
                }
                catch { }
            }
            _swatch.Background = new SolidColorBrush(_current);
            _hex.Text = $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}";

            // Keep the loupe beside the cursor, flipping to the other side near the screen's edge.
            var lh = new WindowInteropHelper(_loupe).Handle;
            if (lh == IntPtr.Zero) return;
            var dpi = VisualTreeHelper.GetDpi(_loupe);
            int w = (int)Math.Ceiling(_loupe.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(_loupe.ActualHeight * dpi.DpiScaleY);
            int right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int x = p.X + 22, y = p.Y + 22;
            if (x + w > right) x = p.X - 22 - w;
            if (y + h > bottom) y = p.Y - 22 - h;
            SetWindowPos(lh, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private static BitmapSource? Capture(int x, int y, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
            IntPtr old = SelectObject(mem, bmp);
            try
            {
                BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
                SelectObject(mem, old);
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                DeleteObject(bmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ----- Win32 -----

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
