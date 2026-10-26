using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kairos
{
    /// <summary>
    /// A miniature of the real lyrics window, at its real size and shape (scaled to fit), playing a short
    /// sample song so every setting, including the moving effects, can be seen as it will look.
    /// </summary>
    public sealed class LivePreview : Grid
    {
        // An original sample song: each line and when it starts (seconds). Loops.
        private static readonly (double At, string Text)[] Song =
        {
            (0.0,  "Streetlights hum a quiet tune"),
            (2.6,  "Every moment finds its place"),
            (5.2,  "I can hear the morning coming"),
            (7.8,  "Counting down the falling rain"),
            (10.4, "Hold the line and let it echo"),
            (13.0, "All the words we never said"),
            (15.6, "Right on time, the sky is turning"),
            (18.2, "Gold and blue above our heads"),
        };
        private const double SongLength = 20.8;

        private readonly Settings _s;
        private readonly Func<BitmapSource?> _albumArt;
        private readonly Func<BitmapSource?> _profilePicture;

        // Layout: the same parts as the real window, arranged by the same code.
        private readonly Grid _content = new();
        private readonly Border _rowProfile = new() { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _freeArt = new() { IsHitTestVisible = false };
        private readonly Border _freeProfile = new() { IsHitTestVisible = false };
        private readonly Border _lyricsPanel = new() { IsHitTestVisible = false };
        private readonly LayoutEngine.Parts _parts;

        /// <summary>Raised after the parts have been arranged (the Layout editor redraws its outlines then).</summary>
        public event Action? LayoutApplied;

        /// <summary>Off in the Layout editor, where clicks pick things up instead of pausing the sample.</summary>
        public bool ClickToPause { get; set; } = true;

        private readonly Grid _window = new() { ClipToBounds = true };
        private readonly BackgroundLayer _bg = new();
        private readonly Border _frame = new();
        private readonly Grid _trackRow = new() { Margin = new Thickness(0, 0, 0, 8) };
        private readonly Border _thumb = new() { Width = 42, Height = 42, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _trackText = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title = new() { Text = "Right on Time", FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _artist = new() { Text = "The Sample Band", FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        private readonly Grid _lyricsArea = new() { ClipToBounds = true };
        private readonly StackPanel _lines = new();
        private readonly TranslateTransform _scroll = new();
        private readonly List<TextBlock> _blocks = new();
        private readonly TextBlock _paused = new() { Text = "Paused  ·  click to play", FontSize = 11, Foreground = Brushes.White, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 6), IsHitTestVisible = false };

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick, _time, _beat;
        private int _current = -1;
        private bool _isPaused;
        private GradientStop[]? _karaokeStops;
        private readonly ScaleTransform _pulse = new(1, 1);

        public LivePreview(Settings settings, Func<BitmapSource?> albumArt, Func<BitmapSource?>? profilePicture = null)
        {
            _s = settings;
            _albumArt = albumArt;
            _profilePicture = profilePicture ?? (() => null);

            // Title row, laid out like the real window.
            var titleStack = _trackText;
            titleStack.Children.Add(_title);
            titleStack.Children.Add(_artist);
            _trackRow.Children.Add(_thumb);
            _trackRow.Children.Add(_rowProfile);
            _trackRow.Children.Add(titleStack);

            // Lyrics, faded at the top and bottom edges like the real window.
            _lines.RenderTransform = _scroll;
            _lyricsArea.Children.Add(_lines);
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.14));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.86));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _lyricsArea.OpacityMask = mask;
            foreach (var (_, text) in Song)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.White),
                    LayoutTransform = new ScaleTransform(1, 1),
                };
                _blocks.Add(tb);
                _lines.Children.Add(tb);
            }

            var content = _content;
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            SetRow(_lyricsPanel, 1);
            content.Children.Add(_lyricsPanel);
            content.Children.Add(_trackRow);
            SetRow(_lyricsArea, 1);
            content.Children.Add(_lyricsArea);
            content.Children.Add(_freeArt);
            content.Children.Add(_freeProfile);
            _frame.Child = content;
            _parts = new LayoutEngine.Parts
            {
                Content = content,
                TitleRow = _trackRow,
                TitleText = _trackText,
                TitleBlocks = new[] { _title, _artist },
                RowArt = _thumb,
                RowProfile = _rowProfile,
                FreeArt = _freeArt,
                FreeProfile = _freeProfile,
                LyricsPanel = _lyricsPanel,
                Lyrics = new FrameworkElement[] { _lyricsArea },
            };
            content.SizeChanged += (_, _) => RefreshLayout();

            _window.Children.Add(_bg);
            _window.Children.Add(_frame);
            _window.Children.Add(_paused);

            Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = _window });

            Cursor = Cursors.Hand;
            ToolTip = "A sample song, styled with your settings. Click to pause or play.";
            MouseLeftButtonUp += (_, _) =>
            {
                if (!ClickToPause) return;
                _isPaused = !_isPaused;
                _paused.BeginAnimation(OpacityProperty, new DoubleAnimation(_isPaused ? 0.8 : 0, TimeSpan.FromMilliseconds(150)));
            };
            _timer.Tick += (_, _) => Tick();
            Loaded += (_, _) => { Refresh(); _timer.Start(); };
            Unloaded += (_, _) => _timer.Stop();
            _bg.ImageLoaded += ApplyFrameBackground;
        }

        // =================================================================
        //  Applying settings
        // =================================================================

        public void Refresh()
        {
            _window.Width = Math.Max(200, double.IsNaN(_s.Width) ? 380 : _s.Width);
            _window.Height = Math.Max(110, double.IsNaN(_s.Height) ? 260 : _s.Height);

            double radius = Math.Max(0, _s.CornerRadius);
            _window.Clip = new RectangleGeometry(new Rect(0, 0, _window.Width, _window.Height), radius, radius);
            _bg.CornerRadius = radius;
            var art = _albumArt();
            _bg.Update(_s, art);
            ApplyFrameBackground();
            _frame.CornerRadius = new CornerRadius(radius);
            _frame.Padding = new Thickness(Math.Max(0, _s.Padding));

            var font = new FontFamily((_s.FontFamily ?? "Segoe UI") + ", Segoe UI");
            var current = MainWindow.ParseColor(_s.CurrentLineColor, Colors.White);
            var other = MainWindow.ParseColor(_s.OtherLinesColor, Colors.White);
            var align = (_s.TextAlignment ?? "").ToLowerInvariant() switch
            {
                "left" => TextAlignment.Left,
                "right" => TextAlignment.Right,
                _ => TextAlignment.Center,
            };

            // Title row, cover and profile picture, placed as on the real window.
            _title.FontFamily = _artist.FontFamily = font;
            _title.Foreground = new SolidColorBrush(current);
            _artist.Foreground = new SolidColorBrush(other);
            double titleScale = Math.Clamp(_s.Layout?.TitleScale ?? 1, 0.6, 4);
            _title.FontSize = 13 * titleScale;
            _artist.FontSize = 11.5 * titleScale;
            RefreshLayout();

            foreach (var tb in _blocks)
            {
                tb.FontFamily = font;
                tb.TextAlignment = align;
                tb.Margin = new Thickness(0, 0, 0, Math.Max(0, _s.LineSpacing));
            }
            _lines.Effect = _s.TextShadow
                ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1.2, Opacity = 0.9 }
                : null;

            Restyle(instant: true);
        }

        /// <summary>Arranges the parts (standard or custom layout). Quick, so the Layout editor calls it while dragging.</summary>
        public void RefreshLayout()
        {
            if (_parts == null) return;
            var art = _albumArt();
            Brush? artBrush = !_s.ShowAlbumArt ? null
                : art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill }
                : new LinearGradientBrush(Color.FromRgb(0xE0, 0x7A, 0x5F), Color.FromRgb(0x3D, 0x40, 0x5B), 45);
            var profile = _profilePicture();
            Brush? profileBrush = _s.ProfilePicture == "Off" ? null
                : profile != null ? new ImageBrush(profile) { Stretch = Stretch.UniformToFill }
                : new LinearGradientBrush(Color.FromRgb(0x6A, 0x8C, 0xC8), Color.FromRgb(0x2E, 0x3A, 0x5C), 45);
            LayoutEngine.Apply(_parts, _s, artBrush, profileBrush);
            Dispatcher.BeginInvoke(new Action(() => LayoutApplied?.Invoke()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>The grid the parts are arranged in (the window inside its padding). The Layout editor draws over it.</summary>
        public Grid ContentArea => _content;

        /// <summary>Where the parts are right now, as layout boxes (see <see cref="LayoutEngine.Measure"/>).</summary>
        public LayoutData MeasureLayout() => LayoutEngine.Measure(_parts, _s.Layout ?? new LayoutData());

        /// <summary>The window colour: the background on its own, a dimming layer over album art, off over an image.</summary>
        private void ApplyFrameBackground()
        {
            var bg = MainWindow.ParseColor(_s.BackgroundColor, Colors.Black);
            double opacity = _bg.ActiveMode switch
            {
                "AlbumArt" => Math.Clamp(_s.AlbumArtDim, 0, 1),
                "Image" => 0,
                // On a plain colour background, the colour dance shows through the window colour, which darkens it.
                _ => _bg.DanceActive ? Math.Clamp(_s.DanceDim, 0, 1) : _s.BackgroundOpacity,
            };
            bg.A = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
            _frame.Background = new SolidColorBrush(bg);
        }

        /// <summary>The same styling rules as the real window: current line, distance fade, glow, blur, karaoke, pulse.</summary>
        private void Restyle(bool instant)
        {
            var currentColor = MainWindow.ParseColor(_s.CurrentLineColor, Colors.White);
            var otherColor = MainWindow.ParseColor(_s.OtherLinesColor, Colors.White);
            var glowColor = _s.GlowMatchLine ? currentColor : MainWindow.ParseColor(_s.GlowColor, currentColor);
            double baseSize = Math.Clamp(_s.FontSize, 6, 120);
            double currentScale = Math.Clamp(_s.CurrentLineScale, 0.5, 3);
            var duration = instant || !_s.SmoothScrolling ? TimeSpan.Zero : TimeSpan.FromMilliseconds(260);
            _karaokeStops = null;

            for (int i = 0; i < _blocks.Count; i++)
            {
                var tb = _blocks[i];
                bool isCurrent = i == _current;
                double opacity = 1;
                if (!isCurrent)
                {
                    opacity = Math.Clamp(_s.OtherLinesOpacity, 0.05, 1);
                    if (_s.FadeDistantLines && _current >= 0)
                        opacity *= Math.Max(0.3, 1 - 0.17 * (Math.Abs(i - _current) - 1));
                }

                tb.FontSize = baseSize;
                tb.FontWeight = isCurrent
                    ? (_s.CurrentLineBold ? FontWeights.Bold : FontWeights.Normal)
                    : (_s.OtherLinesBold ? FontWeights.SemiBold : FontWeights.Normal);

                if (isCurrent && _s.CurrentLineGlow)
                    tb.Effect = new DropShadowEffect { Color = glowColor, BlurRadius = Math.Clamp(_s.GlowSize, 2, 60), ShadowDepth = 0, Opacity = Math.Clamp(_s.GlowStrength, 0.05, 1) };
                else if (!isCurrent && _s.FocusBlur)
                    tb.Effect = new BlurEffect { Radius = Math.Clamp(_s.FocusBlurAmount, 0.5, 12) };
                else tb.Effect = null;

                // Size, brightness and colour, gliding like the real window (with the bounce if Pop is on).
                var st = (ScaleTransform)tb.LayoutTransform;
                double scale = isCurrent ? currentScale : 1;
                var brush = new SolidColorBrush(isCurrent ? currentColor : otherColor);
                tb.Foreground = brush;
                if (duration == TimeSpan.Zero)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    tb.BeginAnimation(OpacityProperty, null);
                    st.ScaleX = st.ScaleY = scale;
                    tb.Opacity = opacity;
                }
                else
                {
                    bool pop = isCurrent && _s.PopEffect;
                    IEasingFunction ease = pop ? new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
                    var d = pop ? TimeSpan.FromMilliseconds(420) : duration;
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, d) { EasingFunction = ease });
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, d) { EasingFunction = ease });
                    tb.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }

                if (isCurrent && _s.KaraokeFill)
                {
                    var unsung = otherColor;
                    unsung.A = (byte)Math.Round(Math.Clamp(_s.OtherLinesOpacity, 0.15, 1) * 255);
                    _karaokeStops = new[]
                    {
                        new GradientStop(currentColor, 0), new GradientStop(currentColor, 0),
                        new GradientStop(unsung, 0.03), new GradientStop(unsung, 1),
                    };
                    var fill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                    foreach (var stop in _karaokeStops) fill.GradientStops.Add(stop);
                    tb.Foreground = fill;
                }

                if (isCurrent && _s.BeatPulse)
                {
                    tb.RenderTransform = _pulse;
                    tb.RenderTransformOrigin = new Point(tb.TextAlignment == TextAlignment.Left ? 0 : tb.TextAlignment == TextAlignment.Right ? 1 : 0.5, 0.5);
                }
                else tb.RenderTransform = Transform.Identity;
            }
        }

        // =================================================================
        //  Playing the sample
        // =================================================================

        private void Tick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - _lastTick, 0, 0.1);
            _lastTick = now;
            if (!_isPaused) _time = (_time + dt) % SongLength;

            int index = 0;
            for (int i = 0; i < Song.Length; i++) if (Song[i].At <= _time) index = i;
            if (index != _current)
            {
                _current = index;
                Restyle(instant: false);
            }

            // Karaoke fill along the current line.
            if (_karaokeStops != null)
            {
                double start = Song[_current].At;
                double end = _current + 1 < Song.Length ? Song[_current + 1].At : SongLength;
                double p = Math.Clamp((_time - start) / Math.Max(0.4, (end - start) * 0.9), 0, 1);
                _karaokeStops[1].Offset = p;
                _karaokeStops[2].Offset = Math.Min(1, p + 0.03);
            }

            // A pretend beat (about 120 bpm) for the pulse.
            if (_s.BeatPulse)
            {
                if (!_isPaused) _beat += dt;
                double kick = Math.Pow(Math.Max(0, Math.Cos(_beat * Math.PI * 2 * 2)), 8);
                double s = 1 + kick * 0.05 * Math.Clamp(_s.BeatPulseStrength, 0.2, 3);
                _pulse.ScaleX = _pulse.ScaleY = s;
            }

            // Keep the current line centred, easing like the real window.
            if (_current >= 0 && _current < _blocks.Count && _blocks[_current].ActualHeight > 0 && _lyricsArea.ActualHeight > 0)
            {
                double y;
                try { y = _blocks[_current].TransformToAncestor(_lines).Transform(new Point(0, 0)).Y; }
                catch (InvalidOperationException) { return; }
                double target = _lyricsArea.ActualHeight / 2 - (y + _blocks[_current].ActualHeight / 2);
                if (!_s.SmoothScrolling) _scroll.Y = target;
                else _scroll.Y += (target - _scroll.Y) * (1 - Math.Exp(-dt * 9));
            }
        }
    }
}
