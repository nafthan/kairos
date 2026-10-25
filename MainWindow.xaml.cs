using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kairos
{
    public partial class MainWindow : Window
    {
        private readonly Settings _s;
        private readonly MediaWatcher _media = new();
        private readonly LyricsService _lyricsService = new();

        private TrackInfo? _track;
        private Lyrics? _lyrics;
        private readonly List<TextBlock> _lineBlocks = new();
        private readonly List<(double Scale, double Opacity, Color Color, bool Glow)> _lineTargets = new();
        private double _lastTimingUpdate;

        // Album art
        private BitmapImage? _art;
        private int _artRequest;
        private string? _artAlbumKey;    // album the current picture belongs to
        private int _artHash;            // to spot Spotify still serving the previous song's picture
        private bool _artAnimating;
        private int _currentIndex = int.MinValue;
        private CancellationTokenSource? _fetchCts;

        // Following the song vs. the user browsing by scrolling.
        private bool _following = true;
        private DateTime _lastUserScroll;

        // Smooth scrolling
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private double _lastFrame;

        // Click (seek) vs. drag (move window)
        private bool _mouseDown;
        private Point _downPoint;
        private int _downLineIndex = -1;

        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        private SettingsWindow? _settingsWindow;

        public MainWindow(Settings settings)
        {
            _s = settings;
            InitializeComponent();

            RestorePlacement();
            ApplySettings();
            ShowStatus("Play a song in Spotify", "Lyrics will appear here");

            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); RememberPlacement(); _s.Save(); };
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0, 300); };
            _pollTimer.Tick += async (_, _) => await _media.PollAsync();

            LocationChanged += (_, _) => _saveTimer.Start();
            SizeChanged += (_, _) => { UpdateSpacers(); UpdateArtClip(); _saveTimer.Start(); };
            Scroller.SizeChanged += (_, _) => UpdateSpacers();

            MouseEnter += (_, _) => { Fade(HoverBar, 1, 150); Fade(ResizeGrip, 1, 150); };
            MouseLeave += (_, _) => { Fade(HoverBar, 0, 250); Fade(ResizeGrip, 0, 250); };

            PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewMouseLeftButtonUp += OnPreviewMouseUp;
            Scroller.PreviewMouseWheel += OnUserScroll;
            KeyDown += OnKeyDown;

            ContextMenu = new ContextMenu();
            ContextMenuOpening += (_, _) => BuildContextMenu();

            _media.TrackChanged += t => Dispatcher.InvokeAsync(() => OnTrackChanged(t));
            _media.PlayStateChanged += p => Dispatcher.InvokeAsync(() => UpdatePlayPauseIcon(p));

            Loaded += async (_, _) =>
            {
                CompositionTarget.Rendering += OnFrame;
                try
                {
                    await _media.StartAsync();
                }
                catch (Exception ex)
                {
                    ShowStatus("Couldn't connect to Windows media controls", ex.Message);
                }
                _pollTimer.Start();
            };

            Closing += (_, _) =>
            {
                CompositionTarget.Rendering -= OnFrame;
                _pollTimer.Stop();
                RememberPlacement();
                _s.Save();
                _media.Dispose();
                _settingsWindow?.Close();
            };
        }

        // =================================================================
        //  Appearance
        // =================================================================

        /// <summary>Applies every setting to the window. Called on startup and whenever a setting changes.</summary>
        public void ApplySettings()
        {
            Topmost = _s.AlwaysOnTop;
            ShowInTaskbar = _s.ShowInTaskbar;

            var bg = ParseColor(_s.BackgroundColor, Colors.Black);
            // With artwork behind, the background colour acts as a dimming layer over it instead.
            double bgOpacity = ArtBackgroundActive ? Math.Clamp(_s.AlbumArtDim, 0, 1) : _s.BackgroundOpacity;
            // Keep alpha ≥ 2: a fully transparent window lets clicks fall through to whatever is behind it.
            bg.A = (byte)Math.Clamp(Math.Round(bgOpacity * 255), 2, 255);
            Frame.Background = new SolidColorBrush(bg);
            ApplyArtwork();
            Frame.CornerRadius = new CornerRadius(Math.Max(0, _s.CornerRadius));
            Frame.Padding = new Thickness(Math.Max(0, _s.Padding));

            var font = SafeFont(_s.FontFamily);
            var current = new SolidColorBrush(ParseColor(_s.CurrentLineColor, Colors.White));
            var other = new SolidColorBrush(ParseColor(_s.OtherLinesColor, Colors.White));
            var align = ParseAlignment(_s.TextAlignment);

            TrackInfoPanel.Visibility = _s.ShowTrackInfo ? Visibility.Visible : Visibility.Collapsed;
            TrackTitle.FontFamily = font; TrackArtist.FontFamily = font;
            TrackTitle.Foreground = current; TrackArtist.Foreground = other;
            TrackTitle.TextAlignment = align; TrackArtist.TextAlignment = align;
            TrackInfoPanel.Margin = new Thickness(0, 0, 0, 8);

            StatusText.FontFamily = font; StatusSubText.FontFamily = font;
            StatusText.Foreground = current; StatusSubText.Foreground = other;

            LinesPanel.Effect = _s.TextShadow
                ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1.2, Opacity = 0.9, RenderingBias = RenderingBias.Performance }
                : null;

            foreach (var tb in _lineBlocks)
            {
                tb.FontFamily = font;
                tb.TextAlignment = align;
                tb.Margin = new Thickness(0, 0, 0, Math.Max(0, _s.LineSpacing));
            }

            TimingPanel.Visibility = _s.ShowTimingInfo ? Visibility.Visible : Visibility.Collapsed;
            ResizeGrip.Visibility = _s.LockSize ? Visibility.Collapsed : Visibility.Visible;
            SyncNote.FontFamily = font;
            SyncNote.Foreground = other;
            SyncNote.TextAlignment = align;

            UpdateSpacers();
            RestyleLines(instant: true);
        }

        /// <summary>
        /// Highlights the current line and dims the rest. Changes glide over a quarter of a second
        /// (size, brightness and colour) unless <paramref name="instant"/> is set.
        /// </summary>
        private void RestyleLines(bool instant = false)
        {
            if (_lineBlocks.Count == 0) return;

            var currentColor = ParseColor(_s.CurrentLineColor, Colors.White);
            var otherColor = ParseColor(_s.OtherLinesColor, Colors.White);
            double baseSize = Math.Clamp(_s.FontSize, 6, 120);
            double currentScale = Math.Clamp(_s.CurrentLineScale, 0.5, 3);
            bool synced = _lyrics?.IsSynced == true;
            var duration = instant || !_s.SmoothScrolling ? TimeSpan.Zero : TimeSpan.FromMilliseconds(260);

            for (int i = 0; i < _lineBlocks.Count; i++)
            {
                var tb = _lineBlocks[i];
                bool isCurrent = synced && i == _currentIndex;

                double scale = isCurrent ? currentScale : 1;
                Color color = !synced || isCurrent ? currentColor : otherColor;
                double opacity;
                if (!synced) opacity = 0.92;
                else if (isCurrent) opacity = 1;
                else
                {
                    opacity = Math.Clamp(_s.OtherLinesOpacity, 0.05, 1);
                    if (_s.FadeDistantLines && _currentIndex >= 0)
                    {
                        int dist = Math.Abs(i - _currentIndex);
                        opacity *= Math.Max(0.3, 1 - 0.17 * (dist - 1));
                    }
                }
                bool glow = isCurrent && _s.CurrentLineGlow;

                tb.FontSize = baseSize;
                tb.FontWeight = isCurrent
                    ? (_s.CurrentLineBold ? FontWeights.Bold : FontWeights.Normal)
                    : (_s.OtherLinesBold ? FontWeights.SemiBold : FontWeights.Normal);

                var target = (scale, opacity, color, glow);
                if (!instant && _lineTargets[i] == target) continue;
                _lineTargets[i] = target;

                tb.Effect = glow
                    ? new DropShadowEffect { Color = color, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 }
                    : null;
                AnimateLine(tb, scale, opacity, color, duration);
            }
        }

        private static void AnimateLine(TextBlock tb, double scale, double opacity, Color color, TimeSpan duration)
        {
            var st = (ScaleTransform)tb.LayoutTransform;
            var brush = (SolidColorBrush)tb.Foreground;

            if (duration == TimeSpan.Zero)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                tb.BeginAnimation(OpacityProperty, null);
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                st.ScaleX = st.ScaleY = scale;
                tb.Opacity = opacity;
                brush.Color = color;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = ease });
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, duration) { EasingFunction = ease });
        }

        /// <summary>Space above/below the lyrics so the first and last lines can sit in the middle.</summary>
        private void UpdateSpacers()
        {
            bool synced = _lyrics?.IsSynced == true;
            double half = Math.Max(0, Scroller.ActualHeight / 2);
            TopSpacer.Height = synced ? half : Scroller.ActualHeight * 0.12;
            BottomSpacer.Height = synced ? half : Scroller.ActualHeight * 0.2;
        }

        // =================================================================
        //  Track / lyrics loading
        // =================================================================

        private async void OnTrackChanged(TrackInfo? track, bool bypassCache = false)
        {
            _fetchCts?.Cancel();
            _track = track;
            _currentIndex = int.MinValue;
            _following = true;
            ClearLines();
            UpdatePlayPauseIcon(_media.IsPlaying);

            if (track == null)
            {
                SetAlbumArt(null, null);
                TrackTitle.Text = ""; TrackArtist.Text = "";
                ShowStatus("Play a song in Spotify", "Lyrics will appear here");
                return;
            }

            TrackTitle.Text = track.Title;
            TrackArtist.Text = track.Artist;
            _ = LoadAlbumArtAsync(track);

            if (string.IsNullOrWhiteSpace(track.Artist))
            {
                // Spotify adverts and some podcasts have no artist; there are no lyrics to find.
                ShowStatus("♪", "Waiting for the next song");
                return;
            }

            ShowStatus("Finding lyrics…", "");
            var cts = _fetchCts = new CancellationTokenSource();

            try
            {
                // Spotify sometimes reports the song's length a moment after the title; give it a beat.
                if (track.Duration <= TimeSpan.Zero) await Task.Delay(700, cts.Token);
                var latest = _media.CurrentTrack;
                var lookup = latest != null && latest.Key == track.Key ? latest : track;

                var lyrics = await _lyricsService.GetAsync(lookup, cts.Token, bypassCache, LocalLyrics.FolderFor(_s));
                if (cts.IsCancellationRequested) return;
                ShowLyrics(lyrics);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer song took over.
            }
            catch (Exception)
            {
                if (cts.IsCancellationRequested) return;
                ShowStatus("Couldn't reach the lyrics service", "Check your internet connection, or add your own lyrics", showAddButton: true);
            }
        }

        private void ShowLyrics(Lyrics lyrics)
        {
            _lyrics = lyrics;
            ClearLines(keepLyrics: true);

            if (lyrics.IsInstrumental && lyrics.Lines.Count == 0)
            {
                ShowStatus("♪  Instrumental  ♪", "");
                return;
            }
            if (lyrics.Lines.Count == 0)
            {
                ShowStatus("No lyrics found", "for this song", showAddButton: true);
                return;
            }

            HideStatus();
            for (int i = 0; i < lyrics.Lines.Count; i++)
            {
                var text = lyrics.Lines[i].Text;
                var tb = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(text) ? "♪" : text,
                    TextWrapping = TextWrapping.Wrap,
                    Tag = i,
                    Cursor = lyrics.IsSynced && _s.ClickToSeek ? Cursors.Hand : null,
                    Background = Brushes.Transparent,            // makes the whole line area clickable
                    Foreground = new SolidColorBrush(Colors.White), // each line gets its own brush so its colour can animate
                    LayoutTransform = new ScaleTransform(1, 1),     // grows the current line smoothly
                };
                _lineBlocks.Add(tb);
                _lineTargets.Add((double.NaN, double.NaN, Colors.Transparent, false));
                LinesPanel.Children.Add(tb);
            }

            bool local = lyrics.Source == LyricsSource.Local;
            SyncNote.Text = local
                ? (lyrics.IsSynced ? "From your lyrics folder" : "From your lyrics folder · not time-synced")
                : "Not time-synced for this song";
            SyncNote.Visibility = local || !lyrics.IsSynced ? Visibility.Visible : Visibility.Collapsed;
            ApplySettings();
            Scroller.ScrollToVerticalOffset(0);
        }

        private void ClearLines(bool keepLyrics = false)
        {
            if (!keepLyrics) _lyrics = null;
            _lineBlocks.Clear();
            _lineTargets.Clear();
            LinesPanel.Children.Clear();
            SyncNote.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            UpdateSpacers();
        }

        private void ShowStatus(string text, string sub, bool showAddButton = false)
        {
            AddLyricsButton.Visibility = showAddButton && _track != null ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = text;
            StatusSubText.Text = sub;
            StatusSubText.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
            StatusPanel.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusPanel.Visibility = Visibility.Collapsed;

        // =================================================================
        //  Per-frame: highlight the current line and keep it centred
        // =================================================================

        private void OnFrame(object? sender, EventArgs e)
        {
            double now = _frameClock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - _lastFrame, 0, 0.1);
            _lastFrame = now;

            var pos = _media.GetPosition() + TimeSpan.FromMilliseconds(_s.SyncOffsetMs);

            if (_s.ShowTimingInfo && now - _lastTimingUpdate > 0.25)
            {
                _lastTimingUpdate = now;
                UpdateTimingInfo(pos);
            }

            if (_lyrics == null || !_lyrics.IsSynced || _lineBlocks.Count == 0) return;

            // Which line are we on?
            int index = FindLine(pos);
            if (index != _currentIndex)
            {
                _currentIndex = index;
                RestyleLines();
            }

            // Resume following after a pause in scrolling, if enabled.
            if (!_following && _s.AutoResumeSeconds > 0 &&
                (DateTime.Now - _lastUserScroll).TotalSeconds >= _s.AutoResumeSeconds && !IsMouseOver)
            {
                SetFollowing(true);
            }

            if (!_following) return;

            // Scroll so the current line (or the first line, before the song starts singing) is centred.
            var target = _lineBlocks[Math.Max(0, _currentIndex)];
            if (!target.IsLoaded || target.ActualHeight <= 0) return;

            double y;
            try { y = target.TransformToAncestor(LinesHost).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { return; }

            double desired = y + target.ActualHeight / 2 - Scroller.ViewportHeight / 2;
            desired = Math.Clamp(desired, 0, Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight));

            double offset = Scroller.VerticalOffset;
            if (Math.Abs(desired - offset) < 0.5) return;

            if (_s.SmoothScrolling)
            {
                // Ease towards the target; feels natural and copes with lines of different heights.
                double k = 1 - Math.Exp(-dt * 9);
                offset += (desired - offset) * k;
                if (Math.Abs(desired - offset) < 0.5) offset = desired;
            }
            else offset = desired;

            Scroller.ScrollToVerticalOffset(offset);
        }

        private void UpdateTimingInfo(TimeSpan pos)
        {
            string lyricsState = _lyrics == null ? "no lyrics"
                : !_lyrics.IsSynced ? "lyrics not synced"
                : $"line {Math.Max(0, _currentIndex + 1)}/{_lyrics.Lines.Count}" +
                  (_currentIndex >= 0 ? $" @ {FmtTime(_lyrics.Lines[_currentIndex].Time)}" : " (before first line)");
            TimingText.Text =
                $"position {FmtTime(pos)}  ·  offset {_s.SyncOffsetMs:+0;-0;0} ms  ·  {lyricsState}\n{_media.DebugInfo}";
        }

        private static string FmtTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

        private int FindLine(TimeSpan pos)
        {
            var lines = _lyrics!.Lines;
            int lo = 0, hi = lines.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid].Time <= pos) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        private void SetFollowing(bool follow)
        {
            _following = follow;
            BackButton.Visibility = !follow && _lyrics?.IsSynced == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnUserScroll(object sender, MouseWheelEventArgs e)
        {
            _lastUserScroll = DateTime.Now;
            if (_lyrics?.IsSynced == true) SetFollowing(false);
            // Let the ScrollViewer do the actual scrolling.
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => SetFollowing(true);

        // =================================================================
        //  Mouse: drag to move, click a line to seek
        // =================================================================

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInside<ButtonBase>(e.OriginalSource) || IsInside<Thumb>(e.OriginalSource)) return;

            _mouseDown = true;
            _downPoint = e.GetPosition(this);
            _downLineIndex = (e.OriginalSource as FrameworkElement)?.Tag is int i ? i : FindLineUnder(e.OriginalSource);
            Focus();
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _downPoint.X) < 5 && Math.Abs(p.Y - _downPoint.Y) < 5) return;

            _mouseDown = false; // it's a drag, not a click
            if (_s.LockPosition) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private async void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_mouseDown) return;
            _mouseDown = false;

            int i = _downLineIndex;
            if (i < 0 || _lyrics == null || !_lyrics.IsSynced || !_s.ClickToSeek || i >= _lyrics.Lines.Count) return;

            if (!_media.CanSeek)
            {
                Toast_Show("Spotify isn't accepting seek requests right now");
                return;
            }

            // Land a hair after the line starts, compensating for the sync offset.
            var to = _lyrics.Lines[i].Time - TimeSpan.FromMilliseconds(_s.SyncOffsetMs) + TimeSpan.FromMilliseconds(30);
            if (to < TimeSpan.Zero) to = TimeSpan.Zero;

            SetFollowing(true);
            if (!await _media.SeekAsync(to)) Toast_Show("Spotify didn't accept the seek");
        }

        private static int FindLineUnder(object source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is TextBlock tb && tb.Tag is int i) return i;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return -1;
        }

        private static bool IsInside<T>(object source) where T : DependencyObject
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is T) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_s.LockSize) return;
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }

        private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) => _saveTimer.Start();

        // =================================================================
        //  Buttons and keyboard
        // =================================================================

        private async void PrevButton_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
        private async void NextButton_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
        private void MoreButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdatePlayPauseIcon(bool playing)
        {
            PlayPauseButton.Content = playing ? "" : "";
        }

        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.OemPlus when ctrl:
                case Key.Add when ctrl:
                    ChangeFontSize(+1); break;
                case Key.OemMinus when ctrl:
                case Key.Subtract when ctrl:
                    ChangeFontSize(-1); break;
                case Key.OemComma when ctrl:
                    OpenSettings(); break;
                case Key.OemOpenBrackets:
                    NudgeOffset(-250); break;
                case Key.OemCloseBrackets:
                    NudgeOffset(+250); break;
                case Key.Space:
                    await _media.TogglePlayPauseAsync(); break;
                case Key.Escape:
                    SetFollowing(true); break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void ChangeFontSize(double delta)
        {
            _s.FontSize = Math.Clamp(_s.FontSize + delta, 8, 72);
            ApplySettings();
            _saveTimer.Start();
            Toast_Show($"Text size {_s.FontSize:0}");
        }

        private void NudgeOffset(int ms)
        {
            _s.SyncOffsetMs += ms;
            _saveTimer.Start();
            double sec = _s.SyncOffsetMs / 1000.0;
            Toast_Show(_s.SyncOffsetMs == 0 ? "Lyrics timing reset"
                : sec > 0 ? $"Lyrics {sec:0.##} s earlier" : $"Lyrics {-sec:0.##} s later");
        }

        // =================================================================
        //  Right-click menu
        // =================================================================

        private void BuildContextMenu()
        {
            var m = ContextMenu;
            m.Items.Clear();

            m.Items.Add(Check("Always on top", _s.AlwaysOnTop, v => _s.AlwaysOnTop = v));
            m.Items.Add(Check("Show song title", _s.ShowTrackInfo, v => _s.ShowTrackInfo = v));
            m.Items.Add(Check("Lock position", _s.LockPosition, v => _s.LockPosition = v));
            m.Items.Add(Check("Lock size", _s.LockSize, v => _s.LockSize = v));
            m.Items.Add(Check("Click a line to seek", _s.ClickToSeek, v => _s.ClickToSeek = v));
            m.Items.Add(Check("Glow on current line", _s.CurrentLineGlow, v => _s.CurrentLineGlow = v));
            m.Items.Add(new Separator());

            var size = new MenuItem { Header = "Text size" };
            size.Items.Add(Item("Larger\tCtrl +", () => ChangeFontSize(+1)));
            size.Items.Add(Item("Smaller\tCtrl −", () => ChangeFontSize(-1)));
            m.Items.Add(size);

            var opacity = new MenuItem { Header = "Background opacity" };
            foreach (var pct in new[] { 100, 90, 75, 50, 25, 0 })
            {
                double v = pct / 100.0;
                var item = Item(pct == 0 ? "Invisible" : $"{pct}%", () => { _s.BackgroundOpacity = Math.Max(0.01, v); ApplyAndSave(); });
                item.IsChecked = Math.Abs(_s.BackgroundOpacity - v) < 0.02;
                opacity.Items.Add(item);
            }
            m.Items.Add(opacity);

            var theme = new MenuItem { Header = "Theme" };
            foreach (var t in Settings.BuiltInThemes)
            {
                var name = t.Name;
                theme.Items.Add(Item(name, () => { _s.ApplyTheme(t); ApplyAndSave(); }));
            }
            if (_s.CustomThemes.Count > 0)
            {
                theme.Items.Add(new Separator());
                foreach (var t in _s.CustomThemes.ToList())
                {
                    var saved = t;
                    theme.Items.Add(Item(saved.Name, () => { _s.ApplyTheme(saved); ApplyAndSave(); }));
                }
            }
            theme.Items.Add(new Separator());
            theme.Items.Add(Item("Save or manage themes…", () => OpenSettings("colours")));
            m.Items.Add(theme);

            var align = new MenuItem { Header = "Alignment" };
            foreach (var a in new[] { "Left", "Center", "Right" })
            {
                var item = Item(a, () => { _s.TextAlignment = a; ApplyAndSave(); });
                item.IsChecked = string.Equals(_s.TextAlignment, a, StringComparison.OrdinalIgnoreCase);
                align.Items.Add(item);
            }
            m.Items.Add(align);

            var timing = new MenuItem { Header = "Lyrics timing" };
            timing.Items.Add(Item("Earlier (0.25 s)\t]", () => NudgeOffset(+250)));
            timing.Items.Add(Item("Later (0.25 s)\t[", () => NudgeOffset(-250)));
            timing.Items.Add(Item("Reset", () => NudgeOffset(-_s.SyncOffsetMs)));
            m.Items.Add(timing);

            m.Items.Add(new Separator());
            m.Items.Add(Item("Reload lyrics", () => { if (_track != null) OnTrackChanged(_track, bypassCache: true); }));
            m.Items.Add(Item("Add or edit lyrics for this song…", () => OpenSettings("lyrics")));
            m.Items.Add(Check("Show timing info", _s.ShowTimingInfo, v => _s.ShowTimingInfo = v));
            m.Items.Add(Item("Settings…\tCtrl ,", () => OpenSettings()));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Exit", Close));
        }

        private MenuItem Item(string header, Action onClick)
        {
            var parts = header.Split('\t');
            var mi = new MenuItem { Header = parts[0] };
            if (parts.Length > 1) mi.InputGestureText = parts[1];
            mi.Click += (_, _) => onClick();
            return mi;
        }

        private MenuItem Check(string header, bool value, Action<bool> set)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
            mi.Click += (_, _) => { set(mi.IsChecked); ApplyAndSave(); };
            return mi;
        }

        private void ApplyAndSave()
        {
            ApplySettings();
            _s.Save();
        }

        private void OpenSettings(string? page = null)
        {
            if (_settingsWindow != null)
            {
                if (page != null) _settingsWindow.GoTo(page);
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_s, ApplySettings, page,
                currentTrack: () => _track,
                lyricsChanged: () => { if (_track != null) OnTrackChanged(_track); })
            { Owner = this };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ApplySettings();
                // Line cursors depend on the click-to-seek setting.
                foreach (var tb in _lineBlocks)
                    tb.Cursor = _lyrics?.IsSynced == true && _s.ClickToSeek ? Cursors.Hand : null;
            };
            _settingsWindow.Show();
        }

        // =================================================================
        //  Window placement
        // =================================================================

        private void RestorePlacement()
        {
            Width = Math.Max(MinWidth, double.IsNaN(_s.Width) ? 380 : _s.Width);
            Height = Math.Max(MinHeight, double.IsNaN(_s.Height) ? 260 : _s.Height);

            var work = SystemParameters.WorkArea;
            bool valid = !double.IsNaN(_s.Left) && !double.IsNaN(_s.Top) &&
                         _s.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
                         _s.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40 &&
                         _s.Left + Width > SystemParameters.VirtualScreenLeft + 40 &&
                         _s.Top > SystemParameters.VirtualScreenTop - 10;

            if (valid)
            {
                Left = _s.Left;
                Top = _s.Top;
            }
            else
            {
                // First run (or the saved monitor is gone): bottom-right of the main screen.
                Left = work.Right - Width - 24;
                Top = work.Bottom - Height - 24;
            }
        }

        private void RememberPlacement()
        {
            if (WindowState != WindowState.Normal) return;
            _s.Left = Left; _s.Top = Top; _s.Width = Width; _s.Height = Height;
        }

        private void AddLyricsButton_Click(object sender, RoutedEventArgs e) => OpenSettings("lyrics");

        // =================================================================
        //  Album art
        // =================================================================

        private bool ArtBackgroundActive => _s.AlbumArtBackground && _art != null;

        private async Task LoadAlbumArtAsync(TrackInfo track)
        {
            int request = ++_artRequest;
            string albumKey = $"{track.Artist}\u0001{track.Album}";
            bool sameAlbum = albumKey == _artAlbumKey && !string.IsNullOrEmpty(track.Album);
            if (!sameAlbum) SetAlbumArt(null, null);

            // Spotify often sends the picture a moment after the title, and may briefly still
            // serve the previous song's picture, so try a few times.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) await Task.Delay(700);
                if (request != _artRequest) return; // a newer song took over

                var bytes = await _media.GetAlbumArtAsync();
                if (request != _artRequest) return;
                if (bytes == null) continue;

                int hash = Hash(bytes);
                if (!sameAlbum && hash == _artHash && attempt < 4) continue; // still the old picture

                var image = DecodeImage(bytes);
                if (image == null) continue;
                _artHash = hash;
                SetAlbumArt(image, albumKey);
                return;
            }
        }

        private void SetAlbumArt(BitmapImage? image, string? albumKey)
        {
            _art = image;
            _artAlbumKey = albumKey;
            if (image == null) _artHash = 0;
            ApplySettingsArtOnly();
        }

        /// <summary>Re-applies just what depends on the artwork (cheaper than a full ApplySettings).</summary>
        private void ApplySettingsArtOnly()
        {
            var bg = ParseColor(_s.BackgroundColor, Colors.Black);
            double bgOpacity = ArtBackgroundActive ? Math.Clamp(_s.AlbumArtDim, 0, 1) : _s.BackgroundOpacity;
            bg.A = (byte)Math.Clamp(Math.Round(bgOpacity * 255), 2, 255);
            Frame.Background = new SolidColorBrush(bg);
            ApplyArtwork();
        }

        private void ApplyArtwork()
        {
            bool thumb = _s.ShowAlbumArt && _art != null;
            ArtThumb.Visibility = thumb ? Visibility.Visible : Visibility.Collapsed;
            ArtThumb.Background = thumb ? new ImageBrush(_art) { Stretch = Stretch.UniformToFill } : null;

            bool background = ArtBackgroundActive;
            ArtHost.Visibility = background ? Visibility.Visible : Visibility.Collapsed;
            ArtBackground.Source = background ? _art : null;
            ArtBlur.Radius = Math.Clamp(_s.AlbumArtBlur, 0, 100);
            UpdateArtClip();
            UpdateArtAnimation(background && _s.AnimateAlbumArt);
        }

        private void UpdateArtClip()
        {
            double w = ArtHost.ActualWidth > 0 ? ArtHost.ActualWidth : ActualWidth;
            double h = ArtHost.ActualHeight > 0 ? ArtHost.ActualHeight : ActualHeight;
            double r = Math.Max(0, _s.CornerRadius);
            ArtHost.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)), r, r);
        }

        /// <summary>A slow, gentle drift and zoom on the background artwork.</summary>
        private void UpdateArtAnimation(bool run)
        {
            if (run == _artAnimating) return;
            _artAnimating = run;

            if (!run)
            {
                ArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ArtShift.BeginAnimation(TranslateTransform.XProperty, null);
                ArtShift.BeginAnimation(TranslateTransform.YProperty, null);
                ArtScale.ScaleX = ArtScale.ScaleY = 1.15;
                ArtShift.X = ArtShift.Y = 0;
                return;
            }

            DoubleAnimation Drift(double from, double to, double seconds) => new(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            ArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, Drift(1.15, 1.35, 24));
            ArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, Drift(1.15, 1.35, 24));
            ArtShift.BeginAnimation(TranslateTransform.XProperty, Drift(-18, 18, 31));
            ArtShift.BeginAnimation(TranslateTransform.YProperty, Drift(-10, 10, 19));
        }

        private static BitmapImage? DecodeImage(byte[] bytes)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static int Hash(byte[] bytes)
        {
            var h = new HashCode();
            h.AddBytes(bytes);
            return h.ToHashCode();
        }

        // =================================================================
        //  Helpers
        // =================================================================

        private void Toast_Show(string text)
        {
            ToastText.Text = text;
            Fade(Toast, 1, 120);
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private static void Fade(UIElement el, double to, int ms)
        {
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new QuadraticEase(),
            });
        }

        public static Color ParseColor(string? s, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(s) && ColorConverter.ConvertFromString(s.Trim()) is Color c) return c;
            }
            catch (Exception) { /* not a valid colour; use the fallback */ }
            return fallback;
        }

        private static FontFamily SafeFont(string name) =>
            string.IsNullOrWhiteSpace(name) ? new FontFamily("Segoe UI") : new FontFamily(name + ", Segoe UI");

        private static TextAlignment ParseAlignment(string s) => s?.ToLowerInvariant() switch
        {
            "left" => System.Windows.TextAlignment.Left,
            "right" => System.Windows.TextAlignment.Right,
            _ => System.Windows.TextAlignment.Center,
        };
    }
}
