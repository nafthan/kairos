using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Kairos
{
    /// <summary>
    /// Mini mode: a slim strip, like a subtitle bar, showing just the line being sung (and, optionally, the next one).
    /// The main window keeps following the song in the background and feeds this one.
    /// </summary>
    public sealed class MiniWindow : Window
    {
        private readonly Settings _s;
        private readonly Action _backToFull;
        private readonly Action _openSettings;
        private readonly Action _exit;

        private readonly Border _frame = new();
        private readonly Grid _textArea = new();
        private readonly TextBlock _line = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        private readonly TextBlock _next = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TranslateTransform _slide = new();
        private readonly StackPanel _hoverBar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, Margin = new Thickness(0, 4, 6, 0) };
        private readonly Thumb _resize;
        private string _shownText = "";
        private GradientStop[]? _karaoke;
        private readonly DispatcherSaver _saver;
        /// <summary>Extra items for the right-click menu, added by the lyrics window (click-through, lyric card).</summary>
        public Action<ContextMenu>? ExtraMenu { get; set; }

        public MiniWindow(Settings settings, Action backToFull, Action openSettings, Action exit)
        {
            _s = settings;
            _backToFull = backToFull;
            _openSettings = openSettings;
            _exit = exit;
            _saver = new DispatcherSaver(Remember);

            Title = "Kairos (mini)";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            MinWidth = 220;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Kairos.ico")); } catch { }

            // ---- Text ----
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, RenderTransform = _slide };
            stack.Children.Add(_line);
            stack.Children.Add(_next);
            _textArea.Children.Add(stack);

            // ---- Hover buttons ----
            Button IconButton(string glyph, string tip, Action click)
            {
                var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, ToolTip = tip, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Width = 24, Height = 24 };
                b.Click += (_, _) => click();
                return b;
            }
            _hoverBar.Children.Add(IconButton("\uE740", "Full window (double-click also works)", () => _backToFull()));
            _hoverBar.Children.Add(IconButton("\uE713", "Settings", () => _openSettings()));
            var hoverBack = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)), CornerRadius = new CornerRadius(12), Padding = new Thickness(2), Child = _hoverBar };
            hoverBack.HorizontalAlignment = HorizontalAlignment.Right;
            hoverBack.VerticalAlignment = VerticalAlignment.Top;
            hoverBack.Margin = new Thickness(0, 4, 6, 0);
            _hoverBar.Margin = new Thickness(0);
            hoverBack.Opacity = 0;

            // ---- Resize width from the right edge ----
            _resize = new Thumb { Style = (Style)FindResource("ResizeEdge"), Width = 8, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.SizeWE };
            _resize.DragDelta += (_, e) =>
            {
                if (_s.MiniFullWidth) return;
                Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            };
            _resize.DragCompleted += (_, _) => _saver.Soon();

            var root = new Grid();
            _frame.Child = _textArea;
            root.Children.Add(_frame);
            root.Children.Add(hoverBack);
            root.Children.Add(_resize);
            Content = root;

            MouseEnter += (_, _) => hoverBack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            MouseLeave += (_, _) => hoverBack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { _backToFull(); return; }
                if (_s.LockPosition) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            LocationChanged += (_, _) => _saver.Soon();

            ContextMenu = new ContextMenu();
            ContextMenuOpening += (_, _) => BuildMenu();

            Loaded += (_, _) => { if (_s.MiniFullWidth) StretchAcrossScreen(); };
        }

        // ----- Showing lines -----

        /// <summary>Updates the text. A new line slides in gently; the same line just keeps its karaoke fill moving.</summary>
        public void ShowLine(string text, string next, double progress)
        {
            if (text != _shownText)
            {
                _shownText = text;
                _line.Text = text;
                _next.Text = next;
                _next.Visibility = _s.MiniShowNextLine && !string.IsNullOrEmpty(next) ? Visibility.Visible : Visibility.Collapsed;
                StyleLine();
                if (_s.SmoothScrolling)
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    _line.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                    _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                }
            }
            if (_karaoke != null)
            {
                double p = Math.Clamp(progress, 0, 1);
                _karaoke[1].Offset = p;
                _karaoke[2].Offset = Math.Min(1, p + 0.03);
            }
        }

        /// <summary>Applies fonts, colours and the window look from settings.</summary>
        public void ApplySettings()
        {
            Topmost = _s.AlwaysOnTop;
            ShowInTaskbar = _s.ShowInTaskbar;

            var bg = MainWindow.ParseColor(_s.BackgroundColor, Colors.Black);
            bg.A = (byte)Math.Clamp(Math.Round(_s.BackgroundOpacity * 255), 2, 255);
            _frame.Background = new SolidColorBrush(bg);
            _frame.CornerRadius = new CornerRadius(Math.Max(0, Math.Min(_s.CornerRadius, 24)));
            _frame.Padding = new Thickness(18, 10, 18, 10);

            var font = new FontFamily((_s.FontFamily ?? "Segoe UI") + ", Segoe UI");
            _line.FontFamily = _next.FontFamily = font;
            _line.FontSize = Math.Clamp(_s.MiniFontSize, 10, 72);
            _next.FontSize = Math.Max(9, _line.FontSize * 0.62);
            _line.FontWeight = _s.CurrentLineBold ? FontWeights.Bold : FontWeights.SemiBold;
            _next.Foreground = new SolidColorBrush(MainWindow.ParseColor(_s.OtherLinesColor, Colors.White));
            _next.Opacity = Math.Clamp(_s.OtherLinesOpacity, 0.15, 1);
            _next.Visibility = _s.MiniShowNextLine && !string.IsNullOrEmpty(_next.Text) ? Visibility.Visible : Visibility.Collapsed;
            _textArea.Effect = _s.TextShadow ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1.2, Opacity = 0.9 } : null;
            _resize.Visibility = _s.MiniFullWidth || _s.LockSize ? Visibility.Collapsed : Visibility.Visible;
            StyleLine();

            if (IsLoaded && _s.MiniFullWidth) StretchAcrossScreen();
        }

        private void StyleLine()
        {
            var current = MainWindow.ParseColor(_s.CurrentLineColor, Colors.White);
            _karaoke = null;
            if (_s.KaraokeFill)
            {
                var other = MainWindow.ParseColor(_s.OtherLinesColor, Colors.White);
                other.A = (byte)Math.Round(Math.Clamp(_s.OtherLinesOpacity, 0.15, 1) * 255);
                _karaoke = new[] { new GradientStop(current, 0), new GradientStop(current, 0), new GradientStop(other, 0.03), new GradientStop(other, 1) };
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                foreach (var stop in _karaoke) brush.GradientStops.Add(stop);
                _line.Foreground = brush;
            }
            else _line.Foreground = new SolidColorBrush(current);

            var glowColor = _s.GlowMatchLine ? current : MainWindow.ParseColor(_s.GlowColor, current);
            _line.Effect = _s.CurrentLineGlow
                ? new DropShadowEffect { Color = glowColor, BlurRadius = Math.Clamp(_s.GlowSize, 2, 60), ShadowDepth = 0, Opacity = Math.Clamp(_s.GlowStrength, 0.05, 1) }
                : null;
        }

        // ----- Placement -----

        /// <summary>Puts the strip where it was last time, or near the bottom of the screen like subtitles.</summary>
        public void Place()
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, double.IsNaN(_s.MiniWidth) ? 640 : _s.MiniWidth);
            bool saved = !double.IsNaN(_s.MiniLeft) && !double.IsNaN(_s.MiniTop) &&
                         _s.MiniLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
                         _s.MiniTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30 &&
                         _s.MiniLeft + Width > SystemParameters.VirtualScreenLeft + 40;
            if (saved) { Left = _s.MiniLeft; Top = _s.MiniTop; }
            else
            {
                Left = work.Left + (work.Width - Width) / 2;
                Top = work.Bottom - 120;
            }
        }

        public void StretchAcrossScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeWindow.StretchAcrossMonitor(hwnd);
        }

        public void CentreHorizontally()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeWindow.CentreHorizontally(hwnd);
            _saver.Soon();
        }

        private void Remember()
        {
            if (!IsVisible) return;
            _s.MiniLeft = Left;
            _s.MiniTop = Top;
            if (!_s.MiniFullWidth) _s.MiniWidth = Width;
            _s.Save();
        }

        // ----- Right-click menu -----

        private void BuildMenu()
        {
            var m = ContextMenu;
            m.Items.Clear();
            MenuItem Item(string header, Action click, string? gesture = null)
            {
                var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
                mi.Click += (_, _) => click();
                return mi;
            }
            MenuItem Check(string header, bool value, Action<bool> set)
            {
                var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
                mi.Click += (_, _) => { set(mi.IsChecked); _s.Save(); ApplySettings(); };
                return mi;
            }
            m.Items.Add(Item("Full window", _backToFull, "Ctrl+Alt+M"));
            m.Items.Add(new Separator());
            m.Items.Add(Check("Show the next line", _s.MiniShowNextLine, v => _s.MiniShowNextLine = v));
            m.Items.Add(Check("Stretch across the screen", _s.MiniFullWidth, v => _s.MiniFullWidth = v));
            m.Items.Add(Check("Always on top", _s.AlwaysOnTop, v => _s.AlwaysOnTop = v));
            var size = new MenuItem { Header = "Text size" };
            size.Items.Add(Item("Larger", () => { _s.MiniFontSize = Math.Min(72, _s.MiniFontSize + 2); _s.Save(); ApplySettings(); }));
            size.Items.Add(Item("Smaller", () => { _s.MiniFontSize = Math.Max(10, _s.MiniFontSize - 2); _s.Save(); ApplySettings(); }));
            m.Items.Add(size);
            m.Items.Add(Item("Centre horizontally", CentreHorizontally, "Ctrl+Alt+C"));
            ExtraMenu?.Invoke(m);
            m.Items.Add(new Separator());
            m.Items.Add(Item("Settings…", _openSettings));
            m.Items.Add(Item("Exit Kairos", _exit));
        }

        /// <summary>Saves the position shortly after moving stops, rather than on every pixel.</summary>
        private sealed class DispatcherSaver
        {
            private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
            public DispatcherSaver(Action save) => _timer.Tick += (_, _) => { _timer.Stop(); save(); };
            public void Soon() { _timer.Stop(); _timer.Start(); }
        }
    }
}
