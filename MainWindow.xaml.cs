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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
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
        private readonly List<(double Scale, double Opacity, Color Color, int Effects)> _lineTargets = new();
        private double _lastTimingUpdate;

        // Album art
        private BitmapImage? _art;
        private int _artRequest;
        private string? _artAlbumKey;    // album the current picture belongs to
        private int _artHash;            // to spot Spotify still serving the previous song's picture

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
        private PlaylistsWindow? _playlistsWindow;
        private SearchWindow? _searchWindow;
        /// <summary>Where the current song is playing from (playlist, album…), when Spotify is connected.</summary>
        private (string Type, string Id, string Uri)? _context;
        private int? _apiVolume;
        private bool _noLyricsFromPlaylist;
        private readonly DispatcherTimer _apiVolumeTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly SpotifyClient _spotify;
        private readonly LyricsFiller _filler;
        private string _fillerCaption = "";
        private bool _fillerNotFound;
        private AppAudio? _spotifyAudio;
        private DateTime _spotifyAudioChecked;

        // Together with the Spotify app
        private readonly DispatcherTimer _spotifyAppTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        // Hidden Spotify window
        private IntPtr _hiddenSpotifyWindow;
        private bool _userShowingSpotify;
        private string _lastSpotifyWindowMode = "";
        private bool _spotifyWasRunning;
        private bool _hiddenForSpotify;
        private bool _closingBecauseSpotifyClosed;
        private bool _spotifyCloseActionDone;
        private bool _started;
        /// <summary>Set when Windows is signing out or shutting down: don't hold things up pausing Spotify.</summary>
        public bool WindowsIsShuttingDown { get; set; }

        // Ctrl+Alt+C: centre horizontally; Ctrl+Alt+M: mini mode. Both from any app.
        private const int CentreShortcutId = 0x4B43;
        private const int MiniShortcutId = 0x4B4D;
        private bool _miniShortcutRegistered;
        private MiniWindow? _mini;

        // Listening statistics
        private readonly Stats _stats = Stats.Load();
        private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _statsClock = Stopwatch.StartNew();
        private double _statsLast;
        private bool _centreShortcutRegistered;

        // Click-through, fading out for full-screen apps, the tray icon and lyric cards
        private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private int _overlayTicks;
        private bool _fullScreenHidden;
        private bool _wasClickThrough;
        private bool _interactive;
        private const int ClickThroughShortcutId = 0x4B54;
        private bool _clickThroughShortcutRegistered;
        private TrayIcon? _tray;

        // Layout and the profile picture
        private readonly LayoutEngine.Parts _layoutParts;
        private BitmapSource? _profile;
        private string _profileKey = "";
        private int _profileRequest;
        private LyricCardWindow? _cardWindow;

        public MainWindow(Settings settings)
        {
            _s = settings;
            _spotify = new SpotifyClient(settings);
            InitializeComponent();

            // Fills the lyrics area for songs without lyrics.
            _filler = new LyricsFiller(_media, () => _art);
            Grid.SetRow(_filler, 1);
            ContentGrid.Children.Add(_filler);

            // The parts the Layout editor can move around.
            _layoutParts = new LayoutEngine.Parts
            {
                Content = ContentGrid,
                TitleRow = TrackInfoPanel,
                TitleText = TrackText,
                TitleBlocks = new[] { TrackTitle, TrackArtist, SyncNote },
                RowArt = ArtThumb,
                RowProfile = ProfileThumb,
                FreeArt = FreeArt,
                FreeProfile = FreeProfile,
                LyricsPanel = LyricsPanel,
                Lyrics = new FrameworkElement[] { Scroller, StatusPanel, _filler },
            };
            ContentGrid.SizeChanged += (_, _) => ApplyLayout();

            RestorePlacement();
            ApplySettings();
            ShowStatus("Play a song in Spotify", "Lyrics will appear here");

            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); RememberPlacement(); _s.Save(); };
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0, 300); };
            _pollTimer.Tick += async (_, _) => await _media.PollAsync();
            _statsTimer.Tick += (_, _) => CountListening();
            _statsTimer.Start();

            LocationChanged += (_, _) => _saveTimer.Start();
            SizeChanged += (_, _) => { UpdateSpacers(); _saveTimer.Start(); };
            Bg.ImageLoaded += ApplyFrameBackground;
            Bg.LevelSource = MusicLevel; // the colour dance moves with the music
            Scroller.SizeChanged += (_, _) => UpdateSpacers();

            MouseEnter += (_, _) => { Fade(HoverBar, 1, 150); Fade(ResizeGrip, 1, 150); };
            MouseLeave += (_, _) => { Fade(HoverBar, 0, 250); Fade(ResizeGrip, 0, 250); };

            PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewMouseLeftButtonUp += OnPreviewMouseUp;
            Scroller.PreviewMouseWheel += OnUserScroll;
            PreviewMouseWheel += OnWindowWheel; // Ctrl + scroll = Spotify volume
            KeyDown += OnKeyDown;

            ContextMenu = new ContextMenu();
            ContextMenuOpening += (_, _) => BuildContextMenu();

            _media.TrackChanged += t => Dispatcher.InvokeAsync(() => OnTrackChanged(t));
            _media.PlayStateChanged += p => Dispatcher.InvokeAsync(() => UpdatePlayPauseIcon(p));

            Loaded += (_, _) => StartUp();

            SourceInitialized += (_, _) =>
            {
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
                UpdateCentreShortcut();
            };

            _spotifyAppTimer.Tick += (_, _) => WatchSpotifyApp();
            _overlayTimer.Tick += (_, _) => UpdateOverlay();
            _apiVolumeTimer.Tick += ApiVolumeTimer_Tick;

            Closing += (_, e) =>
            {
                // Pause or close Spotify first, if that's what's set (this cancels the close and finishes it after).
                if (HandleSpotifyOnClose(e)) return;

                // Spotify was hidden and is staying open: give it its window back, so it's never left unreachable.
                if (_s.SpotifyWindowMode == "Hidden" && !_closingBecauseSpotifyClosed && SpotifyApp.IsRunning())
                    SpotifyApp.ShowWindowAgain(_hiddenSpotifyWindow, minimised: true);

                _spotifyAppTimer.Stop();
                _overlayTimer.Stop();
                _tray?.Dispose();
                _tray = null;
                _cardWindow?.Close();
                if (_clickThroughShortcutRegistered) NativeWindow.UnregisterShortcut(new WindowInteropHelper(this).Handle, ClickThroughShortcutId);
                if (_centreShortcutRegistered) NativeWindow.UnregisterShortcut(new WindowInteropHelper(this).Handle, CentreShortcutId);
                if (_miniShortcutRegistered) NativeWindow.UnregisterShortcut(new WindowInteropHelper(this).Handle, MiniShortcutId);
                _mini?.Close();
                CompositionTarget.Rendering -= OnFrame;
                _pollTimer.Stop();
                RememberPlacement();
                _s.Save();
                _stats.Save();
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
            Topmost = _s.AlwaysOnTop && !_fullScreenHidden;
            ShowInTaskbar = _s.ShowInTaskbar;
            UpdateTray();

            ApplyArtwork();
            Frame.CornerRadius = new CornerRadius(Math.Max(0, _s.CornerRadius));
            Frame.Padding = new Thickness(Math.Max(0, _s.Padding));

            var font = SafeFont(_s.FontFamily);
            var current = new SolidColorBrush(ParseColor(_s.CurrentLineColor, Colors.White));
            var other = new SolidColorBrush(ParseColor(_s.OtherLinesColor, Colors.White));
            var align = ParseAlignment(_s.TextAlignment);

            TrackTitle.FontFamily = font; TrackArtist.FontFamily = font;
            TrackTitle.Foreground = current; TrackArtist.Foreground = other;
            double titleScale = Math.Clamp(_s.Layout?.TitleScale ?? 1, 0.6, 4);
            TrackTitle.FontSize = 13 * titleScale;
            TrackArtist.FontSize = 11.5 * titleScale;
            SyncNote.FontSize = 10.5 * Math.Min(titleScale, 1.6);

            _filler.ApplyStyle(font, current.Color, other.Color);
            if (_filler.IsShowing) ShowFiller(_fillerCaption, _fillerNotFound); // the chosen look may have changed

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
            UpdateCentreShortcut();
            _mini?.ApplySettings();
            var spotifyButtons = _spotify.IsConnected ? Visibility.Visible : Visibility.Collapsed;
            AddToPlaylistButton.Visibility = spotifyButtons;
            SearchButton.Visibility = spotifyButtons;
            PlaylistsButton.Visibility = spotifyButtons;
            SyncNote.FontFamily = font;
            SyncNote.Foreground = other;

            UpdateProfilePicture();
            ApplyLayout();
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
            var glowColor = _s.GlowMatchLine ? currentColor : ParseColor(_s.GlowColor, currentColor);
            // Effect settings are part of each line's "target", so changing them restyles every line.
            int effects = HashCode.Combine(_s.FocusBlur, _s.FocusBlurAmount, _s.GlowSize, _s.GlowStrength, glowColor, _s.KaraokeFill, _s.BeatPulse);

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

                var target = (scale, opacity, color, glow ? effects : effects + 1);
                if (!instant && _lineTargets[i] == target) continue;
                _lineTargets[i] = target;

                if (glow)
                    tb.Effect = new DropShadowEffect
                    {
                        Color = glowColor,
                        BlurRadius = Math.Clamp(_s.GlowSize, 2, 60),
                        ShadowDepth = 0,
                        Opacity = Math.Clamp(_s.GlowStrength, 0.05, 1),
                    };
                else if (synced && !isCurrent && _s.FocusBlur)
                    tb.Effect = new BlurEffect { Radius = Math.Clamp(_s.FocusBlurAmount, 0.5, 12), RenderingBias = RenderingBias.Performance };
                else
                    tb.Effect = null;

                bool pop = isCurrent && _s.PopEffect && !instant;
                AnimateLine(tb, scale, opacity, color, duration, pop);

                // Karaoke: the current line gets a two-colour brush that fills in as it's sung (moved each frame).
                if (isCurrent && _s.KaraokeFill)
                {
                    var unsung = otherColor;
                    unsung.A = (byte)Math.Round(Math.Clamp(_s.OtherLinesOpacity, 0.15, 1) * 255);
                    _karaokeStops = new[]
                    {
                        new GradientStop(currentColor, 0), new GradientStop(currentColor, 0),
                        new GradientStop(unsung, 0.02), new GradientStop(unsung, 1),
                    };
                    var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                    foreach (var stop in _karaokeStops) brush.GradientStops.Add(stop);
                    tb.Foreground = brush;
                    _karaokeLine = i;
                }

                // Beat pulse: a scale on top of the size, so it doesn't push the other lines around.
                if (isCurrent && _s.BeatPulse)
                {
                    tb.RenderTransform = _pulseScale;
                    tb.RenderTransformOrigin = new Point(
                        tb.TextAlignment == TextAlignment.Left ? 0 : tb.TextAlignment == TextAlignment.Right ? 1 : 0.5, 0.5);
                }
                else tb.RenderTransform = Transform.Identity;
            }
            if (!_s.KaraokeFill || _currentIndex < 0) _karaokeLine = -1;
        }

        private static void AnimateLine(TextBlock tb, double scale, double opacity, Color color, TimeSpan duration, bool pop = false)
        {
            var st = (ScaleTransform)tb.LayoutTransform;
            // The karaoke effect swaps in a two-colour brush; go back to a plain one for normal colouring.
            if (tb.Foreground is not SolidColorBrush brush || brush.IsFrozen)
            {
                brush = new SolidColorBrush(color);
                tb.Foreground = brush;
            }

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
            // "Pop": a slightly longer grow that overshoots and settles.
            IEasingFunction scaleEase = pop ? new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut } : ease;
            var scaleDuration = pop ? TimeSpan.FromMilliseconds(420) : duration;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, scaleDuration) { EasingFunction = scaleEase });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, scaleDuration) { EasingFunction = scaleEase });
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = ease });
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, duration) { EasingFunction = ease });
        }

        // ----- Per-frame effects -----

        private GradientStop[]? _karaokeStops;
        private int _karaokeLine = -1;
        private readonly ScaleTransform _pulseScale = new(1, 1);
        private AppAudio? _pulseAudio;
        private DateTime _pulseAudioChecked;
        private double _pulseLevel;

        /// <summary>How loud Spotify is right now (0 … 1), for the colour dance. Silent when paused.</summary>
        private double MusicLevel()
        {
            if (!_media.IsPlaying) return 0;
            if (_pulseAudio == null || DateTime.UtcNow - _pulseAudioChecked > TimeSpan.FromSeconds(3))
            {
                _pulseAudioChecked = DateTime.UtcNow;
                try { _pulseAudio = AudioMixer.Spotify(); } catch { _pulseAudio = null; }
            }
            try { return Math.Sqrt(Math.Clamp(_pulseAudio?.Peak ?? AudioMixer.MasterPeak, 0, 1)); }
            catch { return 0; }
        }

        /// <summary>Moves the karaoke fill along the current line and pulses it with the music.</summary>
        private void UpdateLineEffects(TimeSpan pos, double dt)
        {
            if (_karaokeStops != null && _karaokeLine >= 0 && _lyrics != null && _karaokeLine < _lyrics.Lines.Count)
            {
                var start = _lyrics.Lines[_karaokeLine].Time;
                var end = _karaokeLine + 1 < _lyrics.Lines.Count ? _lyrics.Lines[_karaokeLine + 1].Time : start + TimeSpan.FromSeconds(4);
                // Finish filling a little before the next line, roughly when the words end.
                double length = Math.Max(0.4, (end - start).TotalSeconds * 0.9);
                double p = Math.Clamp((pos - start).TotalSeconds / length, 0, 1);
                double edge = 0.03;
                _karaokeStops[1].Offset = p;
                _karaokeStops[2].Offset = Math.Min(1, p + edge);
            }

            if (_s.BeatPulse)
            {
                if (_pulseAudio == null || DateTime.UtcNow - _pulseAudioChecked > TimeSpan.FromSeconds(3))
                {
                    _pulseAudioChecked = DateTime.UtcNow;
                    try { _pulseAudio = AudioMixer.Spotify(); } catch { _pulseAudio = null; }
                }
                double peak = 0;
                try { peak = _media.IsPlaying ? (_pulseAudio?.Peak ?? AudioMixer.MasterPeak) : 0; } catch { }
                double target = Math.Sqrt(Math.Clamp(peak, 0, 1));
                _pulseLevel += (target - _pulseLevel) * (target > _pulseLevel ? 0.5 : Math.Min(1, dt * 4));
                double s = 1 + _pulseLevel * 0.06 * Math.Clamp(_s.BeatPulseStrength, 0.2, 3);
                _pulseScale.ScaleX = _pulseScale.ScaleY = s;
            }
            else if (_pulseScale.ScaleX != 1) _pulseScale.ScaleX = _pulseScale.ScaleY = 1;
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
            if (track?.Key != _track?.Key) _stats.NewListen();
            _fetchCts?.Cancel();
            _track = track;
            UpdateTrayTip();
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
                // Playing from a playlist marked "no lyrics"? Then don't look; show the visual instead.
                _context = null;
                _noLyricsFromPlaylist = false;
                if (_spotify.IsConnected)
                {
                    try
                    {
                        var contextTask = _spotify.GetPlaybackContextAsync(cts.Token);
                        if (await Task.WhenAny(contextTask, Task.Delay(2500, cts.Token)) == contextTask)
                            _context = await contextTask;
                    }
                    catch (SpotifyException) { /* carry on without it */ }
                    if (cts.IsCancellationRequested) return;

                    if (_context is { Type: "playlist" } c && _s.NoLyricsPlaylists.ContainsKey(c.Id))
                    {
                        _lyrics = new Lyrics { IsInstrumental = true, Source = LyricsSource.Local };
                        _noLyricsFromPlaylist = true;
                        RecordLyricsState(_lyrics);
                        ShowFiller("No lyrics in this playlist", notFound: false);
                        return;
                    }
                }

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
            RecordLyricsState(lyrics);

            if (lyrics.IsInstrumental && lyrics.Lines.Count == 0)
            {
                ShowFiller(lyrics.Source == LyricsSource.Local ? "No lyrics for this song" : "Instrumental", notFound: false);
                return;
            }
            if (lyrics.Lines.Count == 0)
            {
                if (_s.FillerWhenNotFound) ShowFiller("No lyrics found", notFound: true);
                else ShowStatus("No lyrics found", "for this song", showAddButton: true);
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
                _lineTargets.Add((double.NaN, double.NaN, Colors.Transparent, 0));
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
            _filler.Hide();
            _lineBlocks.Clear();
            _lineTargets.Clear();
            LinesPanel.Children.Clear();
            SyncNote.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            UpdateSpacers();
        }

        private void ShowStatus(string text, string sub, bool showAddButton = false)
        {
            _filler.Hide();
            NoLyricsButtons.Visibility = showAddButton && _track != null ? Visibility.Visible : Visibility.Collapsed;
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

            _filler.Update(dt);

            if (_s.ShowTimingInfo && now - _lastTimingUpdate > 0.25)
            {
                _lastTimingUpdate = now;
                UpdateTimingInfo(pos);
            }

            UpdateMini(pos);

            if (_lyrics == null || !_lyrics.IsSynced || _lineBlocks.Count == 0) return;

            // Which line are we on?
            int index = FindLine(pos);
            if (index != _currentIndex)
            {
                _currentIndex = index;
                RestyleLines();
            }
            UpdateLineEffects(pos, dt);

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

            // A click (not a drag) on the cover or profile picture opens its link.
            if (e.OriginalSource is DependencyObject clicked)
            {
                if (_s.AlbumLink && (IsWithin(clicked, ArtThumb) || IsWithin(clicked, FreeArt))) { await OpenAlbumAsync(); return; }
                if (_s.ProfileLink != "Off" && (IsWithin(clicked, ProfileThumb) || IsWithin(clicked, FreeProfile))) { await OpenProfileAsync(); return; }
            }

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

        private static bool IsWithin(DependencyObject d, DependencyObject target)
        {
            for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (ReferenceEquals(x, target)) return true;
            return false;
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
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            var key = e.Key == Key.System ? e.SystemKey : e.Key; // with Alt held, WPF reports the real key separately
            if (ctrl && alt && key == Key.C)
            {
                CentreHorizontally();
                e.Handled = true;
                return;
            }
            if (ctrl && alt && key == Key.M)
            {
                ToggleMiniMode();
                e.Handled = true;
                return;
            }
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
                case Key.F when ctrl:
                    OpenSearch(); break;
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

            var background = new MenuItem { Header = "Background" };
            foreach (var (mode, label) in new[] { ("Colour", "Colour"), ("Image", "Your picture"), ("AlbumArt", "Album art") })
            {
                if (mode == "Image" && string.IsNullOrEmpty(_s.BackgroundImagePath)) continue; // nothing chosen yet
                var chosen = mode;
                var item = Item(label, () => { _s.BackgroundMode = chosen; ApplyAndSave(); });
                item.IsChecked = _s.BackgroundMode == mode;
                background.Items.Add(item);
            }
            background.Items.Add(new Separator());
            background.Items.Add(Check("Colour dance", _s.DanceOn, v => _s.DanceOn = v));
            m.Items.Add(background);

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
            if (_track != null && _lyrics != null && _lyrics.Lines.Count > 0)
                m.Items.Add(Item(_lyrics.IsSynced ? "Re-sync timings for this song…" : "Sync timings for this song…", OpenSyncForCurrent));
            if (_noLyricsFromPlaylist) { /* the playlist entry below covers it */ }
            else if (_track != null && _lyrics != null && _lyrics.IsInstrumental && _lyrics.Source == LyricsSource.Local)
                m.Items.Add(Item("This song has lyrics after all", UnmarkNoLyrics));
            else if (_track != null && (_lyrics == null || _lyrics.Lines.Count == 0))
                m.Items.Add(Item("This song has no lyrics", MarkNoLyrics));
            if (_spotify.IsConnected && _context is { Type: "playlist" } playing)
            {
                bool marked = _s.NoLyricsPlaylists.ContainsKey(playing.Id);
                m.Items.Add(Item(marked ? "This playlist has lyrics after all" : "This playlist has no lyrics", () => _ = TogglePlaylistNoLyrics(playing.Id)));
            }
            if (_s.SpotifyWindowMode == "Hidden" && SpotifyApp.IsRunning())
                m.Items.Add(_userShowingSpotify ? Item("Hide Spotify's window", HideSpotifyWindow) : Item("Show Spotify's window", ShowSpotifyWindow));
            m.Items.Add(Item("Spotify volume…", () => ShowVolumeMixer(null)));
            m.Items.Add(Item("Statistics…", () => OpenSettings("stats")));
            m.Items.Add(Item("Centre horizontally\tCtrl+Alt+C", CentreHorizontally));
            m.Items.Add(Item("Mini mode\tCtrl+Alt+M", ToggleMiniMode));
            m.Items.Add(Check("Click-through (hold Ctrl to use)\tCtrl+Alt+T", _s.ClickThrough, v => SetClickThrough(v)));
            if (HasLyricsForCard()) m.Items.Add(Item("Lyric card…", OpenLyricCard));
            m.Items.Add(new Separator());
            if (_spotify.IsConnected)
            {
                m.Items.Add(Item("Search Spotify…\tCtrl+F", () => OpenSearch()));
                m.Items.Add(Item("Add to playlist…", () => ShowPlaylistPicker(null)));
                m.Items.Add(Item("Playlists…", OpenPlaylists));
            }
            else
            {
                m.Items.Add(Item("Connect Spotify for playlists…", () => OpenSettings("spotify")));
            }
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
            _settingsWindow = new SettingsWindow(_s, ApplySettings, page, _spotify, _media, _stats,
                currentTrack: () => _track,
                lyricsChanged: () => { if (_track != null) OnTrackChanged(_track); },
                albumArt: () => _art,
                profilePicture: () => ProfileImage,
                resizeWindow: (w, h) =>
                {
                    if (WindowState != WindowState.Normal) return;
                    Width = Math.Max(MinWidth, w);
                    Height = Math.Max(MinHeight, h);
                    _saveTimer.Start();
                });
            // Not owned by the lyrics window, so it doesn't inherit "always on top".
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
        private void NoLyricsButton_Click(object sender, RoutedEventArgs e) => MarkNoLyrics();

        /// <summary>Time the current song's lyrics by tapping along, then save them to your lyrics folder.</summary>
        private void OpenSyncForCurrent()
        {
            if (_track == null || _lyrics == null || _lyrics.Lines.Count == 0) return;
            var track = _track;
            var real = _lyrics.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
            var lines = real.Select(l => l.Text.Trim()).ToList();
            List<TimeSpan?>? times = _lyrics.IsSynced ? real.Select(l => (TimeSpan?)l.Time).ToList() : null;

            string artist = LyricsService.FirstArtist(track.Artist);
            string title = LyricsService.CleanTitle(track.Title);
            var sync = new SyncWindow(_media, _spotify, artist, title, lines, lrc =>
            {
                try
                {
                    LocalLyrics.Save(LocalLyrics.FolderFor(_s), artist, title, lrc);
                    Toast_Show("Saved to your lyrics folder");
                    OnTrackChanged(track);
                }
                catch (Exception ex) { Toast_Show($"Couldn't save: {ex.Message}"); }
            }, times);
            sync.Show(); // not owned by the lyrics window, so it doesn't stay on top
        }

        // =================================================================
        //  Songs without lyrics
        // =================================================================

        private void ShowFiller(string caption, bool notFound)
        {
            _fillerCaption = caption;
            _fillerNotFound = notFound;
            if (!notFound && !_s.ShowFillerCaption) caption = ""; // "No lyrics for this song" / "Instrumental" turned off

            if (_s.NoLyricsFiller == LyricsFiller.Message)
            {
                ShowStatus(notFound ? "No lyrics found" : "♪", notFound ? "for this song" : caption, showAddButton: notFound);
                return;
            }

            HideStatus();
            if (notFound)
            {
                var add = new Button { Style = (Style)FindResource("PillButton"), Content = "Add lyrics" };
                add.Click += AddLyricsButton_Click;
                var none = new Button { Style = (Style)FindResource("PillGhostButton"), Content = "This song has no lyrics", Margin = new Thickness(8, 0, 0, 0) };
                none.Click += NoLyricsButton_Click;
                _filler.Show(_s.NoLyricsFiller, caption, add, none);
            }
            else _filler.Show(_s.NoLyricsFiller, caption);
        }

        /// <summary>Remembers that this song has no lyrics, so Kairos stops searching and shows the filler.</summary>
        private void MarkNoLyrics()
        {
            if (_track == null) return;
            try
            {
                LocalLyrics.MarkNoLyrics(LocalLyrics.FolderFor(_s), LyricsService.FirstArtist(_track.Artist), LyricsService.CleanTitle(_track.Title));
                OnTrackChanged(_track);
                Toast_Show("Marked as having no lyrics. Right-click to undo.");
            }
            catch (Exception ex) { Toast_Show($"Couldn't save that: {ex.Message}"); }
        }

        private void UnmarkNoLyrics()
        {
            if (_track == null) return;
            var file = LocalLyrics.FindFile(LocalLyrics.FolderFor(_s), _track);
            if (file != null && file.EndsWith(LocalLyrics.NoLyricsExtension, StringComparison.OrdinalIgnoreCase))
                LocalLyrics.UnmarkNoLyrics(file);
            OnTrackChanged(_track, bypassCache: true); // look online again
        }

        // =================================================================
        //  Volume
        // =================================================================

        private void VolumeButton_Click(object sender, RoutedEventArgs e) => ShowVolumeMixer(VolumeButton);

        // =================================================================
        //  Listening statistics
        // =================================================================

        /// <summary>Adds the last second (or so) of listening to the statistics, if music is playing.</summary>
        private void CountListening()
        {
            double now = _statsClock.Elapsed.TotalSeconds;
            double dt = now - _statsLast;
            _statsLast = now;
            if (!_s.TrackStatistics || !_media.IsPlaying || _track == null || string.IsNullOrWhiteSpace(_track.Artist)) return; // adverts have no artist
            _stats.AddListening(_track.Title, _track.Artist, _track.Duration.TotalSeconds, dt, DateTime.Now);
        }

        private void RecordLyricsState(Lyrics lyrics)
        {
            if (!_s.TrackStatistics || _track == null || string.IsNullOrWhiteSpace(_track.Artist)) return;
            string state = lyrics.IsInstrumental && lyrics.Lines.Count == 0 ? "nolyrics"
                : lyrics.Lines.Count == 0 ? "none"
                : lyrics.IsSynced ? "synced" : "plain";
            _stats.SetLyrics(_track.Title, _track.Artist, state, DateTime.Now);
        }
        private void MiniButton_Click(object sender, RoutedEventArgs e) => EnterMiniMode();

        // =================================================================
        //  Mini mode
        // =================================================================

        private void ToggleMiniMode()
        {
            if (_mini?.IsVisible == true) ExitMiniMode();
            else EnterMiniMode();
        }

        private void EnterMiniMode()
        {
            _mini ??= new MiniWindow(_s, ExitMiniMode, () => OpenSettings(), Close)
            {
                ExtraMenu = m =>
                {
                    m.Items.Add(Check("Click-through (hold Ctrl to use)\tCtrl+Alt+T", _s.ClickThrough, v => SetClickThrough(v)));
                    if (HasLyricsForCard()) m.Items.Add(Item("Lyric card…", OpenLyricCard));
                },
            };
            if (IsVisible) RememberPlacement();
            _s.MiniMode = true;
            _s.Save();
            _mini.ApplySettings();
            if (!_mini.IsVisible) _mini.Place();
            _mini.Show();
            _mini.Activate();
            Hide();
        }

        private void ExitMiniMode()
        {
            _s.MiniMode = false;
            _s.Save();
            _mini?.Hide();
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>Starts straight into mini mode (it was on when Kairos last closed).</summary>
        public void StartInMini()
        {
            new WindowInteropHelper(this).EnsureHandle();
            StartUp();
            EnterMiniMode();
        }

        /// <summary>Gives the mini strip the line being sung, the next one, and how far through it the song is.</summary>
        private void UpdateMini(TimeSpan pos)
        {
            if (_mini == null || !_mini.IsVisible) return;

            string text, next = "";
            double progress = 0;
            if (_lyrics != null && _lyrics.IsSynced && _lyrics.Lines.Count > 0 && !_noLyricsFromPlaylist)
            {
                var lines = _lyrics.Lines;
                int i = FindLine(pos);
                if (i < 0)
                {
                    text = "\u266A";
                    next = lines[0].Text;
                }
                else
                {
                    text = string.IsNullOrWhiteSpace(lines[i].Text) ? "\u266A" : lines[i].Text;
                    for (int j = i + 1; j < lines.Count; j++)
                        if (!string.IsNullOrWhiteSpace(lines[j].Text)) { next = lines[j].Text; break; }
                    var start = lines[i].Time;
                    var end = i + 1 < lines.Count ? lines[i + 1].Time : start + TimeSpan.FromSeconds(4);
                    progress = (pos - start).TotalSeconds / Math.Max(0.4, (end - start).TotalSeconds * 0.9);
                }
            }
            else if (_track != null)
            {
                text = string.IsNullOrWhiteSpace(_track.Artist) ? _track.Title : $"{_track.Title}  \u00B7  {_track.Artist}";
                next = _lyrics == null ? "Finding lyrics\u2026"
                    : _lyrics.IsInstrumental || _noLyricsFromPlaylist ? "No lyrics"
                    : _lyrics.Lines.Count > 0 ? "Lyrics aren't time-synced for this song"
                    : "No lyrics found";
            }
            else text = "Play a song in Spotify";

            _mini.ShowLine(text, next, progress);
        }
        private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

        private void OpenSearch()
        {
            if (!_spotify.IsConnected) { OpenSettings("spotify"); return; }
            if (_searchWindow == null)
            {
                _searchWindow = new SearchWindow(_spotify);
                _searchWindow.Closed += (_, _) => _searchWindow = null;
            }
            _searchWindow.Open();
        }

        /// <summary>Marks (or unmarks) the playlist that's playing as having no lyrics.</summary>
        private async Task TogglePlaylistNoLyrics(string playlistId)
        {
            if (_s.NoLyricsPlaylists.Remove(playlistId))
            {
                _s.Save();
                Toast_Show("Lyrics will show for this playlist again");
            }
            else
            {
                var name = await _spotify.GetPlaylistNameAsync(playlistId) ?? "This playlist";
                _s.NoLyricsPlaylists[playlistId] = name;
                _s.Save();
                Toast_Show($"{name}: marked as no lyrics");
            }
            if (_track != null) OnTrackChanged(_track);
        }

        // ----- Spotify's own volume, through the Spotify connection (when Windows can't find Spotify's sound) -----

        private void AddApiVolumeRow(StackPanel body)
        {
            var icon = new TextBlock { Text = "\uE767", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var percent = new TextBlock { Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12.5, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "…" };
            var slider = new Slider { Style = (Style)FindResource("KSlider"), Minimum = 0, Maximum = 100, SmallChange = 2, LargeChange = 10, IsEnabled = false, Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var note = new TextBlock { Text = "Using Spotify's own volume, through your Spotify connection.", Foreground = (Brush)FindResource("FaintBrush"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0) };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(icon);
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            Grid.SetColumn(percent, 2);
            row.Children.Add(percent);
            body.Children.Add(row);
            body.Children.Add(note);

            slider.ValueChanged += (_, e) =>
            {
                if (!slider.IsEnabled) return; // still loading
                percent.Text = $"{e.NewValue:0}%";
                SetApiVolumeSoon((int)Math.Round(e.NewValue));
            };

            _ = LoadAsync();
            async Task LoadAsync()
            {
                try
                {
                    var v = await _spotify.GetVolumeAsync();
                    if (v == null) { note.Text = "Play something in Spotify first."; return; }
                    _apiVolume = v;
                    slider.Value = v.Value;
                    percent.Text = $"{v.Value}%";
                    slider.IsEnabled = true;
                }
                catch (SpotifyException ex) { note.Text = ex.Message; }
            }
        }

        private async void NudgeApiVolume(int delta)
        {
            try
            {
                _apiVolume ??= await _spotify.GetVolumeAsync();
                if (_apiVolume == null) { Toast_Show("Play something in Spotify first"); return; }
                int v = Math.Clamp(_apiVolume.Value + delta, 0, 100);
                SetApiVolumeSoon(v);
                Toast_Show($"Spotify volume {v}%");
            }
            catch (SpotifyException ex) { Toast_Show(ex.Message); }
        }

        /// <summary>Sends the volume once you stop moving the slider for a moment (Spotify limits how often apps can call it).</summary>
        private void SetApiVolumeSoon(int percent)
        {
            _apiVolume = percent;
            _apiVolumeTimer.Stop();
            _apiVolumeTimer.Start();
        }

        private async void ApiVolumeTimer_Tick(object? sender, EventArgs e)
        {
            _apiVolumeTimer.Stop();
            if (_apiVolume is not int v) return;
            try { await _spotify.SetVolumeAsync(v); }
            catch (SpotifyException ex) { Toast_Show(ex.Message); }
        }


        /// <summary>Ctrl + mouse wheel anywhere on the window changes Spotify's volume.</summary>
        private void OnWindowWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;

            if (_spotifyAudio == null || DateTime.UtcNow - _spotifyAudioChecked > TimeSpan.FromSeconds(5))
            {
                _spotifyAudio = AudioMixer.Spotify();
                _spotifyAudioChecked = DateTime.UtcNow;
            }
            if (_spotifyAudio == null)
            {
                if (_spotify.IsConnected) { NudgeApiVolume(e.Delta > 0 ? 5 : -5); return; }
                Toast_Show("Windows isn't reporting Spotify's sound");
                return;
            }

            float v = Math.Clamp(_spotifyAudio.Volume + (e.Delta > 0 ? 0.05f : -0.05f), 0f, 1f);
            _spotifyAudio.Volume = v;
            if (_spotifyAudio.Muted && e.Delta > 0) _spotifyAudio.Muted = false;
            Toast_Show($"Spotify volume {v * 100:0}%");
        }

        /// <summary>A small pop-up with Spotify's volume, mute and a live level meter.</summary>
        private void ShowVolumeMixer(FrameworkElement? anchor)
        {
            var body = new StackPanel { Width = 270 };
            body.Children.Add(new TextBlock { Text = "Spotify volume", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(4, 0, 0, 10) });

            var spotify = AudioMixer.Spotify();
            _spotifyAudio = spotify;
            _spotifyAudioChecked = DateTime.UtcNow;
            DispatcherTimer? meterTimer = null;

            if (spotify == null)
            {
                if (_spotify.IsConnected) AddApiVolumeRow(body);
                else
                    body.Children.Add(new TextBlock
                    {
                        Text = "Windows isn't reporting Spotify's sound, so Kairos can't adjust it here. " +
                               "Connecting Spotify (settings → Spotify) lets Kairos use Spotify's own volume instead.",
                        Foreground = (Brush)FindResource("MutedBrush"),
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 0, 4, 4),
                    });

                // What Windows does report, to help work out why Spotify isn't among it.
                body.Children.Add(new TextBlock
                {
                    Text = AudioMixer.Describe(),
                    Foreground = (Brush)FindResource("FaintBrush"),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 10, 4, 0),
                });
            }
            else
            {
                var muteGlyph = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = (Brush)FindResource("TextBrush") };
                var mute = new Button { Style = (Style)FindResource("BareButton"), Width = 32, Height = 32, ToolTip = "Mute Spotify", Content = muteGlyph, VerticalAlignment = VerticalAlignment.Center };
                void ShowMute()
                {
                    muteGlyph.Text = spotify.Muted ? "\uE74F" : "\uE767";
                    muteGlyph.Opacity = spotify.Muted ? 0.5 : 1;
                }
                ShowMute();
                mute.Click += (_, _) => { spotify.Muted = !spotify.Muted; ShowMute(); };

                var percent = new TextBlock { Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12.5, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Style = (Style)FindResource("KSlider"), Minimum = 0, Maximum = 100, Value = Math.Round(spotify.Volume * 100), SmallChange = 2, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center };
                percent.Text = $"{slider.Value:0}%";
                slider.ValueChanged += (_, e) =>
                {
                    spotify.Volume = (float)(e.NewValue / 100);
                    percent.Text = $"{e.NewValue:0}%";
                    if (spotify.Muted && e.NewValue > 0) { spotify.Muted = false; ShowMute(); }
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(mute);
                Grid.SetColumn(slider, 1);
                slider.Margin = new Thickness(6, 0, 6, 0);
                row.Children.Add(slider);
                Grid.SetColumn(percent, 2);
                row.Children.Add(percent);
                body.Children.Add(row);

                // A thin live level meter, so you can see Spotify is the one making the sound.
                var meterTrack = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) };
                var meterFill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                var meter = new Grid { Margin = new Thickness(44, 6, 46, 4) };
                meter.Children.Add(meterTrack);
                meter.Children.Add(meterFill);
                body.Children.Add(meter);

                body.Children.Add(new TextBlock
                {
                    Text = "Tip: Ctrl + scroll over the lyrics window changes it too.",
                    Foreground = (Brush)FindResource("FaintBrush"),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 8, 4, 0),
                });

                meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                meterTimer.Tick += (_, _) =>
                {
                    double target = Math.Sqrt(Math.Clamp(spotify.Peak, 0f, 1f)) * meterTrack.ActualWidth;
                    meterFill.Width = meterFill.Width + (target - meterFill.Width) * (target > meterFill.Width ? 0.6 : 0.2);
                };
            }

            var popup = new Popup
            {
                PlacementTarget = anchor ?? (UIElement)this,
                Placement = anchor != null ? PlacementMode.Bottom : PlacementMode.MousePoint,
                VerticalOffset = 6,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                    BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12),
                    Child = body,
                },
            };
            // A pop-up takes its text settings from the button it hangs off, which uses the icon font.
            TextElement.SetFontFamily(popup.Child, new FontFamily("Segoe UI Variable Text, Segoe UI"));
            TextElement.SetFontSize(popup.Child, 13);

            if (meterTimer != null)
            {
                popup.Opened += (_, _) => meterTimer.Start();
                popup.Closed += (_, _) => meterTimer.Stop();
            }
            popup.IsOpen = true;
        }

        // =================================================================
        //  Together with the Spotify app
        // =================================================================

        private async void StartUp()
        {
            if (_started) return;
            _started = true;

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

            _spotifyWasRunning = SpotifyApp.IsRunning();
            if (_s.OpenSpotifyOnStart && !_spotifyWasRunning)
            {
                if (SpotifyApp.Launch(minimised: _s.SpotifyWindowMode != "Normal")) ShowStatus("Opening Spotify…", "Press play when it's ready");
                else Toast_Show("Couldn't find Spotify to open it");
            }
            _spotifyAppTimer.Start();
            _overlayTimer.Start();
            UpdateTray();
        }

        /// <summary>Starts in the background, waiting for Spotify to open (nothing on screen yet).</summary>
        public void StartHidden()
        {
            new WindowInteropHelper(this).EnsureHandle();
            _hiddenForSpotify = true;
            StartUp();
        }

        /// <summary>Shows the window again (Spotify opened, or Kairos was launched again while waiting).</summary>
        public void BringBack()
        {
            _hiddenForSpotify = false;
            if (_s.MiniMode) { EnterMiniMode(); return; }
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = _s.AlwaysOnTop;
            Activate();
        }

        /// <summary>Notices Spotify opening and closing, and hides, shows or closes Kairos to match.</summary>
        private void WatchSpotifyApp()
        {
            KeepSpotifyHidden();
            bool running = SpotifyApp.IsRunning();
            if (running == _spotifyWasRunning) return;
            _spotifyWasRunning = running;

            if (running)
            {
                if (_hiddenForSpotify) BringBack();
                return;
            }

            switch (_s.OnSpotifyClose)
            {
                case "Hide":
                    _hiddenForSpotify = true;
                    RememberPlacement();
                    _s.Save();
                    Hide();
                    _mini?.Hide();
                    _settingsWindow?.Close();
                    break;
                case "Close":
                    _closingBecauseSpotifyClosed = true;
                    Close();
                    break;
            }
        }

        /// <summary>
        /// "Hidden" Spotify window: keeps Spotify playing in the background with no window or taskbar button.
        /// If you bring Spotify up yourself (e.g. from its tray icon) it's left alone; minimising it hides it again.
        /// </summary>
        private void KeepSpotifyHidden()
        {
            // Switched away from Hidden: put Spotify's window back (minimised, out of the way).
            if (_lastSpotifyWindowMode == "Hidden" && _s.SpotifyWindowMode != "Hidden")
            {
                SpotifyApp.ShowWindowAgain(_hiddenSpotifyWindow, minimised: true);
                _hiddenSpotifyWindow = IntPtr.Zero;
                _userShowingSpotify = false;
            }
            _lastSpotifyWindowMode = _s.SpotifyWindowMode;
            if (_s.SpotifyWindowMode != "Hidden") return;

            var window = SpotifyApp.VisibleMainWindow();
            if (window == IntPtr.Zero) return;

            if (_userShowingSpotify)
            {
                // You opened it on purpose; once you minimise it, it goes back into hiding.
                if (SpotifyApp.IsMinimised(window)) { SpotifyApp.HideWindow(window); _hiddenSpotifyWindow = window; _userShowingSpotify = false; }
                return;
            }

            // Brought to the front by you (not just starting up)? Leave it be until it's minimised.
            var started = SpotifyApp.StartedAt();
            bool justStarted = started == null || DateTime.Now - started.Value < TimeSpan.FromSeconds(20);
            if (!justStarted && _hiddenSpotifyWindow != IntPtr.Zero && SpotifyApp.IsInFront(window))
            {
                _userShowingSpotify = true;
                return;
            }

            SpotifyApp.HideWindow(window);
            _hiddenSpotifyWindow = window;
        }

        private void ShowSpotifyWindow()
        {
            _userShowingSpotify = true;
            SpotifyApp.ShowWindowAgain(_hiddenSpotifyWindow, minimised: false);
        }

        private void HideSpotifyWindow()
        {
            _userShowingSpotify = false;
            KeepSpotifyHidden();
        }

        /// <summary>
        /// When Kairos closes, pause or close Spotify if set. Returns true if it cancelled this close to do that
        /// first (the window then closes itself once Spotify has been dealt with).
        /// </summary>
        private bool HandleSpotifyOnClose(System.ComponentModel.CancelEventArgs e)
        {
            if (_spotifyCloseActionDone || _closingBecauseSpotifyClosed || WindowsIsShuttingDown) return false;
            if (_s.OnKairosClose != "Pause" && _s.OnKairosClose != "Close") return false;
            if (!SpotifyApp.IsRunning()) return false;

            _spotifyCloseActionDone = true;
            e.Cancel = true;
            Hide(); // feels instant; the rest happens a moment later
            _mini?.Hide();

            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // Pause even when closing Spotify, so the music stops straight away.
                    var pause = _media.PauseAsync();
                    await Task.WhenAny(pause, Task.Delay(1500));
                    if (_s.OnKairosClose == "Close")
                    {
                        var close = SpotifyApp.CloseAsync();
                        await Task.WhenAny(close, Task.Delay(5000));
                    }
                }
                catch { }
                Close();
            });
            return true;
        }

        // =================================================================
        //  Click-through and fading out for full-screen apps
        // =================================================================

        /// <summary>The Kairos window on screen right now: the mini strip or the lyrics window.</summary>
        private Window? ShownWindow() => _mini?.IsVisible == true ? _mini : IsVisible ? this : null;

        /// <summary>
        /// Runs a dozen times a second. With click-through on, the window lets the mouse through unless you hold Ctrl
        /// over it (and stays usable while a button is held or its menu is open). Twice a second it also checks for a
        /// full-screen app on the same screen as Kairos.
        /// </summary>
        private void UpdateOverlay()
        {
            var window = ShownWindow();
            if (window == null) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (++_overlayTicks % 6 == 0)
            {
                bool hide = _s.HideForFullScreen && Overlay.FullScreenAppOnSameScreen(hwnd);
                if (hide != _fullScreenHidden) SetFullScreenHidden(hide);
            }

            bool menuOpen = window.ContextMenu?.IsOpen == true;
            bool over = Overlay.IsCursorOver(hwnd);
            _interactive = menuOpen || (over && (Overlay.IsCtrlHeld() || (_interactive && Overlay.IsMouseButtonDown())));
            bool through = _fullScreenHidden || (_s.ClickThrough && !_interactive);
            Overlay.SetClickThrough(hwnd, through);

            // The window that isn't showing shouldn't keep the setting from before.
            Window? other = window == this ? _mini : this;
            if (other != null)
            {
                var otherHwnd = new WindowInteropHelper(other).Handle;
                if (otherHwnd != IntPtr.Zero) Overlay.SetClickThrough(otherHwnd, through);
            }

            // Windows doesn't tell the window the mouse has gone when it starts letting it through: tidy up by hand.
            if (through && !_wasClickThrough && window == this) { Fade(HoverBar, 0, 250); Fade(ResizeGrip, 0, 250); }
            _wasClickThrough = through;
        }

        private void ToggleClickThrough() => SetClickThrough(!_s.ClickThrough);

        private void SetClickThrough(bool on)
        {
            _s.ClickThrough = on;
            _s.Save();
            _interactive = false;
            UpdateOverlay();
            Toast_Show(on ? "Click-through on: hold Ctrl to use Kairos" : "Click-through off");
        }

        /// <summary>Fades Kairos out (and lets clicks through) while a full-screen app is on its screen, and back after.</summary>
        private void SetFullScreenHidden(bool hide)
        {
            _fullScreenHidden = hide;
            foreach (var w in new Window?[] { this, _mini })
            {
                if (w == null) continue;
                w.BeginAnimation(OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(hide ? 200 : 450)));
                // Not "on top" while hidden, so games keep their full screen to themselves.
                w.Topmost = !hide && _s.AlwaysOnTop;
            }
        }

        // =================================================================
        //  Tray icon
        // =================================================================

        private void UpdateTray()
        {
            if (!_started) return; // made once Kairos has started
            if (_s.ShowTrayIcon && _tray == null)
            {
                try
                {
                    _tray = new TrayIcon { MenuBuilder = BuildTrayMenu };
                    _tray.Clicked += ToggleFromTray;
                    UpdateTrayTip();
                }
                catch { _tray = null; }
            }
            else if (!_s.ShowTrayIcon && _tray != null)
            {
                _tray.Dispose();
                _tray = null;
            }
        }

        private void UpdateTrayTip()
        {
            if (_tray == null) return;
            _tray.SetTip(_track == null ? "Kairos"
                : string.IsNullOrWhiteSpace(_track.Artist) ? $"Kairos\n{_track.Title}"
                : $"Kairos\n{_track.Title}\n{_track.Artist}");
        }

        private bool KairosShowing => IsVisible || _mini?.IsVisible == true;

        /// <summary>Left-click on the tray icon: hide Kairos, or bring it back.</summary>
        private void ToggleFromTray()
        {
            if (KairosShowing)
            {
                if (IsVisible) RememberPlacement();
                _s.Save();
                Hide();
                _mini?.Hide();
            }
            else BringBack();
        }

        private ContextMenu BuildTrayMenu()
        {
            var m = new ContextMenu();
            if (_track != null)
            {
                m.Items.Add(new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(_track.Artist) ? _track.Title : $"{_track.Title}  \u00B7  {_track.Artist}",
                    IsEnabled = false,
                });
                m.Items.Add(new Separator());
            }
            m.Items.Add(Item(KairosShowing ? "Hide Kairos" : "Show Kairos", ToggleFromTray));
            m.Items.Add(new Separator());
            m.Items.Add(Item(_media.IsPlaying ? "Pause" : "Play", () => _ = _media.TogglePlayPauseAsync()));
            m.Items.Add(Item("Next", () => _ = _media.NextAsync()));
            m.Items.Add(Item("Previous", () => _ = _media.PreviousAsync()));
            m.Items.Add(new Separator());
            var mini = Item("Mini mode\tCtrl+Alt+M", ToggleMiniMode);
            mini.IsChecked = _mini?.IsVisible == true;
            m.Items.Add(mini);
            m.Items.Add(Check("Click-through (hold Ctrl to use)\tCtrl+Alt+T", _s.ClickThrough, v => SetClickThrough(v)));
            if (_s.SpotifyWindowMode == "Hidden" && SpotifyApp.IsRunning())
                m.Items.Add(_userShowingSpotify ? Item("Hide Spotify's window", HideSpotifyWindow) : Item("Show Spotify's window", ShowSpotifyWindow));
            if (HasLyricsForCard()) m.Items.Add(Item("Lyric card…", OpenLyricCard));
            m.Items.Add(Item("Settings…", () => OpenSettings()));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Exit", Close));
            return m;
        }

        // =================================================================
        //  Lyric card
        // =================================================================

        private bool HasLyricsForCard() =>
            _track != null && _lyrics != null && !_noLyricsFromPlaylist && _lyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text));

        private void OpenLyricCard()
        {
            if (!HasLyricsForCard() || _track == null || _lyrics == null)
            {
                Toast_Show("Play a song with lyrics to make a card");
                return;
            }
            _cardWindow?.Close();

            // The lines worth putting on a card (no blanks), starting at the one being sung, with when each is sung
            // (in song time, allowing for your lyrics timing setting) for playing them through.
            var lines = new List<CardLine>();
            int start = 0;
            double offset = _s.SyncOffsetMs / 1000.0;
            var all = _lyrics.Lines;
            for (int i = 0; i < all.Count; i++)
            {
                var text = all[i].Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (i <= _currentIndex) start = lines.Count;
                double begin = double.NaN, end = double.NaN;
                if (_lyrics.IsSynced)
                {
                    begin = Math.Max(0, all[i].Time.TotalSeconds - offset);
                    end = i + 1 < all.Count ? all[i + 1].Time.TotalSeconds - offset : begin + 4;
                    if (end <= begin) end = begin + 0.5;
                }
                lines.Add(new CardLine(text.Trim(), begin, end));
            }
            if (_currentIndex < 0) start = 0;

            _cardWindow = new LyricCardWindow(_s, _track, lines, start, _art, ProfileImage, _media);
            _cardWindow.Closed += (_, _) => _cardWindow = null;
            _cardWindow.Show();
            _cardWindow.Activate();
        }

        // =================================================================
        //  Centring and the system-wide shortcut
        // =================================================================

        private void CentreHorizontally()
        {
            if (WindowState != WindowState.Normal) return;
            NativeWindow.CentreHorizontally(new WindowInteropHelper(this).Handle);
            _saveTimer.Start();
        }

        private void UpdateCentreShortcut()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return; // not created yet; done again in SourceInitialized

            if (_s.GlobalCentreShortcut && !_centreShortcutRegistered)
                _centreShortcutRegistered = NativeWindow.RegisterShortcut(hwnd, CentreShortcutId,
                    NativeWindow.MOD_CONTROL | NativeWindow.MOD_ALT, 0x43 /* C */);
            else if (!_s.GlobalCentreShortcut && _centreShortcutRegistered)
            {
                NativeWindow.UnregisterShortcut(hwnd, CentreShortcutId);
                _centreShortcutRegistered = false;
            }

            if (_s.GlobalCentreShortcut && !_clickThroughShortcutRegistered)
                _clickThroughShortcutRegistered = NativeWindow.RegisterShortcut(hwnd, ClickThroughShortcutId,
                    NativeWindow.MOD_CONTROL | NativeWindow.MOD_ALT, 0x54 /* T */);
            else if (!_s.GlobalCentreShortcut && _clickThroughShortcutRegistered)
            {
                NativeWindow.UnregisterShortcut(hwnd, ClickThroughShortcutId);
                _clickThroughShortcutRegistered = false;
            }

            if (_s.GlobalCentreShortcut && !_miniShortcutRegistered)
                _miniShortcutRegistered = NativeWindow.RegisterShortcut(hwnd, MiniShortcutId,
                    NativeWindow.MOD_CONTROL | NativeWindow.MOD_ALT, 0x4D /* M */);
            else if (!_s.GlobalCentreShortcut && _miniShortcutRegistered)
            {
                NativeWindow.UnregisterShortcut(hwnd, MiniShortcutId);
                _miniShortcutRegistered = false;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeWindow.WM_HOTKEY && wParam.ToInt32() == CentreShortcutId)
            {
                if (_mini?.IsVisible == true) _mini.CentreHorizontally();
                else if (IsVisible) CentreHorizontally();
                handled = true;
            }
            else if (msg == NativeWindow.WM_HOTKEY && wParam.ToInt32() == ClickThroughShortcutId)
            {
                ToggleClickThrough();
                handled = true;
            }
            else if (msg == NativeWindow.WM_HOTKEY && wParam.ToInt32() == MiniShortcutId)
            {
                if (!_hiddenForSpotify) ToggleMiniMode();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // =================================================================
        //  Spotify playlists
        // =================================================================

        private void AddToPlaylistButton_Click(object sender, RoutedEventArgs e) => ShowPlaylistPicker(AddToPlaylistButton);
        private void PlaylistsButton_Click(object sender, RoutedEventArgs e) => OpenPlaylists();

        private void OpenPlaylists()
        {
            if (!_spotify.IsConnected) { OpenSettings("spotify"); return; }
            if (_playlistsWindow != null) { _playlistsWindow.Activate(); return; }
            _playlistsWindow = new PlaylistsWindow(_spotify, _s.SpotifyUserName, _s, () => { if (_track != null) OnTrackChanged(_track); });
            _playlistsWindow.Closed += (_, _) => _playlistsWindow = null;
            _playlistsWindow.Show();
        }

        /// <summary>A small pop-up list of your playlists; click one to add the song that's playing.</summary>
        private async void ShowPlaylistPicker(FrameworkElement? anchor)
        {
            if (!_spotify.IsConnected) { OpenSettings("spotify"); return; }

            var list = new StackPanel();
            var search = new TextBox
            {
                Style = (Style)FindResource("DarkText"),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "Filter playlists",
            };
            var status = new TextBlock
            {
                Text = "Loading your playlists…",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12.5,
                Margin = new Thickness(4, 6, 4, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            var scroller = new ScrollViewer
            {
                Content = list,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var body = new StackPanel { Width = 280 };
            body.Children.Add(new TextBlock
            {
                Text = "Add to playlist",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(4, 0, 0, 10),
            });
            body.Children.Add(search);
            body.Children.Add(status);
            body.Children.Add(scroller);

            var uiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
            var popup = new Popup
            {
                PlacementTarget = anchor ?? (UIElement)this,
                Placement = anchor != null ? PlacementMode.Bottom : PlacementMode.MousePoint,
                VerticalOffset = 6,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                    BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10),
                    Child = body,
                },
            };
            // A pop-up inherits text settings from the button it's attached to, which here uses the icon font.
            TextElement.SetFontFamily(popup.Child, uiFont);
            TextElement.SetFontSize(popup.Child, 13);
            popup.IsOpen = true;
            search.Focus();

            List<PlaylistInfo> playlists;
            try
            {
                playlists = (await _spotify.GetPlaylistsAsync()).Where(p => p.Editable).ToList();
            }
            catch (SpotifyException ex)
            {
                status.Text = ex.Message;
                return;
            }

            void Fill()
            {
                list.Children.Clear();
                var f = search.Text.Trim();
                var shown = playlists.Where(p => f.Length == 0 || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                status.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                status.Text = playlists.Count == 0 ? "You don't have any playlists you can add to yet." : "No playlists match.";

                if (!_spotify.HasLibraryAccess && f.Length == 0)
                {
                    // Connected with an older version: Liked Songs needs one more permission.
                    var reconnect = PickerRow(CoverTile(null, liked: true), "Liked Songs", "Reconnect Spotify to enable");
                    reconnect.Opacity = 0.75;
                    reconnect.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; OpenSettings("spotify"); };
                    list.Children.Add(reconnect);
                }

                foreach (var p in shown)
                {
                    var item = PickerRow(CoverTile(p.ImageUrl, p.IsLikedSongs), p.Name, p.Total == 1 ? "1 song" : $"{p.Total} songs");
                    var target = p;
                    item.MouseLeftButtonUp += async (_, _) =>
                    {
                        popup.IsOpen = false;
                        await AddCurrentSongTo(target);
                    };
                    list.Children.Add(item);
                }
            }

            search.TextChanged += (_, _) => Fill();
            search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) popup.IsOpen = false; };
            Fill();
        }

        /// <summary>One row in the playlist picker: cover, name, song count.</summary>
        private Border PickerRow(UIElement cover, string name, string detail)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = name, Foreground = (Brush)FindResource("TextBrush"), FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = detail, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(cover);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            return row;
        }

        /// <summary>A playlist cover (loaded from Spotify), or the purple heart tile for Liked Songs.</summary>
        public static Border CoverTile(string? imageUrl, bool liked, double size = 40)
        {
            var tile = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size * 0.15),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            };
            if (liked)
            {
                tile.Background = new LinearGradientBrush(Color.FromRgb(0x45, 0x0A, 0xF5), Color.FromRgb(0xC4, 0xEF, 0xD9), 45);
                tile.Child = new TextBlock
                {
                    Text = "\uEB52", // filled heart
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = size * 0.4,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else if (!string.IsNullOrEmpty(imageUrl))
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(imageUrl);
                    img.DecodePixelWidth = (int)(size * 2);
                    img.EndInit();
                    tile.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
            return tile;
        }

        private async Task AddCurrentSongTo(PlaylistInfo playlist)
        {
            try
            {
                var current = await _spotify.GetCurrentTrackAsync();
                if (current == null)
                {
                    Toast_Show("Nothing is playing in Spotify (or it's a podcast or ad).");
                    return;
                }
                if (playlist.IsLikedSongs && await _spotify.IsLikedAsync(current.Value.Uri))
                {
                    Toast_Show("Already in your Liked Songs");
                    return;
                }
                await _spotify.AddToPlaylistAsync(playlist.Id, current.Value.Uri);
                Toast_Show(playlist.IsLikedSongs ? "Saved to Liked Songs" : $"Added to {playlist.Name}");
            }
            catch (SpotifyException ex)
            {
                Toast_Show(ex.Message);
                if (!_spotify.IsConnected) ApplySettings(); // signed out: hide the buttons
            }
        }

        // =================================================================
        //  Album art
        // =================================================================

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
            ApplyArtwork();
        }

        /// <summary>Album art beside the title, the background layer, and the window colour that sits over it.</summary>
        private void ApplyArtwork()
        {
            ApplyLayout();

            Bg.CornerRadius = Math.Max(0, _s.CornerRadius);
            Bg.Update(_s, _art);
            ApplyFrameBackground();
        }

        /// <summary>
        /// The window colour: on its own it's the background; over album art it's the dimming layer;
        /// over a background image it's turned off (the image has its own tint).
        /// </summary>
        private void ApplyFrameBackground()
        {
            var bg = ParseColor(_s.BackgroundColor, Colors.Black);
            double opacity = Bg.ActiveMode switch
            {
                "AlbumArt" => Math.Clamp(_s.AlbumArtDim, 0, 1),
                "Image" => 0,
                // On a plain colour background, the colour dance shows through the window colour, which darkens it.
                _ => Bg.DanceActive ? Math.Clamp(_s.DanceDim, 0, 1) : _s.BackgroundOpacity,
            };
            // Keep alpha ≥ 2: a fully transparent window lets clicks fall through to whatever is behind it.
            bg.A = (byte)Math.Clamp(Math.Round(opacity * 255), 2, 255);
            Frame.Background = new SolidColorBrush(bg);
        }

        // =================================================================
        //  Layout and the profile picture
        // =================================================================

        /// <summary>Places the title, cover, profile picture and lyrics: the standard layout or your own.</summary>
        private void ApplyLayout()
        {
            if (_layoutParts == null) return; // still being set up
            Brush? art = _s.ShowAlbumArt && _art != null ? new ImageBrush(_art) { Stretch = Stretch.UniformToFill } : null;
            Brush? profile = _s.ProfilePicture != "Off" && _profile != null ? new ImageBrush(_profile) { Stretch = Stretch.UniformToFill } : null;
            LayoutEngine.Apply(_layoutParts, _s, art, profile);
            UpdatePictureLinks();
        }

        /// <summary>The cover and profile picture become clickable links when those are switched on.</summary>
        private void UpdatePictureLinks()
        {
            bool album = _s.AlbumLink;
            bool profile = _s.ProfileLink == "Spotify" || (_s.ProfileLink == "Custom" && !string.IsNullOrWhiteSpace(_s.ProfileLinkUrl));
            foreach (var b in new[] { ArtThumb, FreeArt })
            {
                b.IsHitTestVisible = album;
                b.Cursor = album ? Cursors.Hand : null;
                b.ToolTip = album ? "Open the album in Spotify" : null;
            }
            foreach (var b in new[] { ProfileThumb, FreeProfile })
            {
                b.IsHitTestVisible = profile;
                b.Cursor = profile ? Cursors.Hand : null;
                b.ToolTip = !profile ? null : _s.ProfileLink == "Spotify" ? "Open your Spotify profile" : _s.ProfileLinkUrl;
            }
        }

        /// <summary>Opens the playing song's album in Spotify (or searches for it, without the Spotify connection).</summary>
        private async Task OpenAlbumAsync()
        {
            if (_track == null) return;
            string? uri = null;
            if (_spotify.IsConnected)
            {
                try { uri = await _spotify.GetCurrentAlbumUriAsync(); } catch { }
            }
            uri ??= "spotify:search:" + Uri.EscapeDataString($"{_track.Album} {_track.Artist}".Trim());
            OpenLink(uri);
        }

        private async Task OpenProfileAsync()
        {
            if (_s.ProfileLink == "Custom")
            {
                OpenLink(_s.ProfileLinkUrl.Trim());
                return;
            }
            if (!_spotify.IsConnected) { Toast_Show("Connect Spotify (settings → Spotify) to open your profile"); return; }
            string? uri = null;
            try { uri = await _spotify.GetMyProfileUriAsync(); } catch { }
            if (uri == null) { Toast_Show("Couldn't find your Spotify profile"); return; }
            OpenLink(uri);
        }

        /// <summary>Opens a web or spotify: link. Spotify links open in the app (its window comes back if it's hidden).</summary>
        private void OpenLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link)) return;
            bool spotifyLink = link.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
            if (!spotifyLink && !link.Contains("://")) link = "https://" + link;
            if (!spotifyLink && !(link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                Toast_Show("Only web and Spotify links can be opened");
                return;
            }
            if (spotifyLink && _s.SpotifyWindowMode == "Hidden" && SpotifyApp.IsRunning()) ShowSpotifyWindow();
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
            catch
            {
                // No Spotify app to take a spotify: link? Try the web player instead.
                if (spotifyLink)
                {
                    var parts = link.Split(':');
                    string web = parts.Length >= 3 && parts[1] != "search"
                        ? $"https://open.spotify.com/{parts[1]}/{parts[2]}"
                        : "https://open.spotify.com/search/" + (parts.Length >= 3 ? parts[2] : "");
                    try { Process.Start(new ProcessStartInfo(web) { UseShellExecute = true }); } catch { Toast_Show("Couldn't open the link"); }
                }
                else Toast_Show("Couldn't open the link");
            }
        }

        /// <summary>The profile picture shown in the window and previews (null if off or not loaded).</summary>
        public BitmapSource? ProfileImage => _s.ProfilePicture != "Off" ? _profile : null;

        /// <summary>Loads the profile picture when its source changes (your own picture, or your Spotify account's).</summary>
        private void UpdateProfilePicture()
        {
            string key = $"{_s.ProfilePicture}|{_s.ProfilePicturePath}|{_spotify.IsConnected}|{_s.SpotifyUserName}";
            if (key == _profileKey) return;
            _profileKey = key;
            _ = LoadProfilePictureAsync();
        }

        private async Task LoadProfilePictureAsync()
        {
            int request = ++_profileRequest;
            string source = _s.ProfilePicture, path = _s.ProfilePicturePath;
            byte[]? bytes = null;
            try
            {
                if (source == "Custom" && !string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    bytes = await File.ReadAllBytesAsync(path);
                }
                else if (source == "Spotify" && _spotify.IsConnected)
                {
                    // Kept for a day, so Kairos doesn't ask Spotify every time it starts.
                    string who = string.Concat((_s.SpotifyUserName ?? "").Where(char.IsLetterOrDigit));
                    string cache = Path.Combine(Settings.AppDataFolder, $"spotify-profile-{(who.Length > 0 ? who : "me")}.img");
                    bool fresh = File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromDays(1);
                    if (fresh) bytes = await File.ReadAllBytesAsync(cache);
                    else
                    {
                        try { bytes = await _spotify.GetProfilePictureAsync(); } catch { bytes = null; }
                        if (bytes != null)
                        {
                            try { Directory.CreateDirectory(Settings.AppDataFolder); await File.WriteAllBytesAsync(cache, bytes); } catch { }
                        }
                        else if (File.Exists(cache)) bytes = await File.ReadAllBytesAsync(cache); // offline: last one we had
                    }
                }
            }
            catch { bytes = null; }

            if (request != _profileRequest) return; // the source changed again meanwhile
            _profile = bytes != null ? DecodeImage(bytes, 400) : null;
            ApplyLayout();
            _settingsWindow?.PicturesChanged();
        }

        private static BitmapImage? DecodeImage(byte[] bytes, int maxWidth)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = maxWidth; // a phone photo would otherwise use a lot of memory for a small circle
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
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
