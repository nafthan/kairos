using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Kairos
{
    /// <summary>
    /// The settings panel. Every change previews live, both in the panel and on the lyrics window.
    /// "Done" (or closing) keeps the changes; "Undo changes" restores how things were when it opened.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Settings _s;
        private readonly Settings _snapshot;
        private readonly Action _apply;
        private readonly bool _startWithWindowsAtOpen;
        private readonly Func<TrackInfo?> _currentTrack;
        private readonly Action _lyricsChanged;
        private readonly Func<System.Windows.Media.Imaging.BitmapSource?> _albumArt;
        private readonly Func<System.Windows.Media.Imaging.BitmapSource?> _profilePicture;
        private readonly Action<double, double>? _resizeWindow;
        private readonly SpotifyClient _spotify;
        private readonly MediaWatcher? _media;
        private readonly Stats _stats;
        private System.Threading.CancellationTokenSource? _connectCts;
        private static string _lastPage = "text";
        private string _page = "text";

        private static readonly (string Key, string Icon, string Title, string Subtitle)[] Pages =
        {
            ("text",      "", "Text",      "Font, size and how the current line stands out"),
            ("effects",   "\uE734", "Effects",   "Glow, karaoke fill, focus blur and movement"),
            ("colours",   "", "Colours",   "Themes, text colours and background"),
            ("background", "\uE91B", "Background", "A colour, a picture or GIF, the album art, or a colour dance"),
            ("layout",    "\uE8A9", "Layout",    "Drag the lyrics, title, cover and profile picture wherever you like"),
            ("window",    "", "Window",    "Shape, placement and how the window behaves"),
            ("behaviour", "", "Behaviour", "Scrolling, seeking and lyrics timing"),
            ("lyrics",    "\uE8F1", "My lyrics", "Your own lyrics, for songs the online library doesn't have"),
            ("spotify",   "\uE8D6", "Spotify",   "Connect your account to add songs to playlists and browse them"),
            ("stats",     "\uE9D9", "Statistics", "What you listen to, when, and how many of your songs have lyrics"),
            ("shortcuts", "", "Shortcuts", "Keys, mouse actions and where things are saved"),
        };

        public SettingsWindow(Settings settings, Action apply, string? startPage = null, SpotifyClient? spotify = null,
                              MediaWatcher? media = null, Stats? stats = null,
                              Func<TrackInfo?>? currentTrack = null, Action? lyricsChanged = null,
                              Func<System.Windows.Media.Imaging.BitmapSource?>? albumArt = null,
                              Func<System.Windows.Media.Imaging.BitmapSource?>? profilePicture = null,
                              Action<double, double>? resizeWindow = null)
        {
            _albumArt = albumArt ?? (() => null);
            _profilePicture = profilePicture ?? (() => null);
            _resizeWindow = resizeWindow;
            _spotify = spotify ?? new SpotifyClient(settings);
            _media = media;
            _stats = stats ?? Stats.Load();
            _s = settings;
            _apply = apply;
            _currentTrack = currentTrack ?? (() => null);
            _lyricsChanged = lyricsChanged ?? (() => { });
            _snapshot = settings.Clone();
            _startWithWindowsAtOpen = settings.StartWithWindows;

            InitializeComponent();

            // Remembered size (kept within the screen).
            Width = Math.Clamp(settings.SettingsWidth, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(settings.SettingsHeight, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));

            VersionButton.Content = $"Version {AppInfo.VersionText}  ·  What's new";

            if (startPage != null) _lastPage = startPage;
            BuildNav();
            ShowPage(_lastPage);
            _preview = new LivePreview(_s, _albumArt, _profilePicture);
            PreviewHost.Content = _preview;
            RefreshPreview();

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Closing += (_, _) => { _connectCts?.Cancel(); Commit(); };
        }

        // =================================================================
        //  Navigation and window chrome
        // =================================================================

        private void BuildNav()
        {
            Nav.Children.Clear();
            foreach (var p in Pages)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock
                {
                    Text = p.Icon,
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 15,
                    Width = 26,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                content.Children.Add(new TextBlock { Text = p.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });

                var rb = new RadioButton
                {
                    Style = (Style)FindResource("NavItem"),
                    Content = content,
                    GroupName = "nav",
                    IsChecked = p.Key == _lastPage,
                    Tag = p.Key,
                };
                rb.Checked += (_, _) => ShowPage((string)rb.Tag);
                Nav.Children.Add(rb);
            }
        }

        /// <summary>Opens a page by key (text, colours, window, behaviour, shortcuts, changelog).</summary>
        public void GoTo(string key)
        {
            if (key == "changelog") { ShowChangelog(); return; }
            foreach (var rb in Nav.Children.OfType<RadioButton>())
                if ((string)rb.Tag == key) { rb.IsChecked = true; return; }
        }

        private void VersionButton_Click(object sender, RoutedEventArgs e) => ShowChangelog();

        private void ShowPage(string key)
        {
            var page = Pages.FirstOrDefault(p => p.Key == key);
            if (page.Key == null) page = Pages[0];
            _page = _lastPage = page.Key;

            PageTitle.Text = page.Title;
            PageSubtitle.Text = page.Subtitle;
            PageHost.Children.Clear();
            _extraPreviews.Clear();
            _layoutEditor = null;
            // The Layout page has its own, bigger preview.
            PreviewPanel.Visibility = page.Key == "layout" ? Visibility.Collapsed : Visibility.Visible;

            switch (page.Key)
            {
                case "text": BuildTextPage(); break;
                case "effects": BuildEffectsPage(); break;
                case "colours": BuildColoursPage(); break;
                case "window": BuildWindowPage(); break;
                case "layout": BuildLayoutPage(); break;
                case "background": BuildBackgroundPage(); break;
                case "lyrics": BuildLyricsPage(); break;
                case "spotify": BuildSpotifyPage(); break;
                case "behaviour": BuildBehaviourPage(); break;
                case "shortcuts": BuildShortcutsPage(); break;
                case "stats": BuildStatsPage(); break;
            }
            PageScroller.ScrollToTop();
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        // ----- Resizing -----

        private void ResizeRight_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);

        private void ResizeBottom_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
            Height = Math.Max(MinHeight, Height + e.VerticalChange);

        private void ResizeCorner_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }

        private void Resize_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _s.SettingsWidth = Width;
            _s.SettingsHeight = Height;
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            // Saved themes, the Spotify connection and the lyrics folder aren't undone, only settings.
            var keep = _s.Clone();
            _s.CopyFrom(_snapshot);
            _s.KeepPersonalDataFrom(keep);
            _resizeWindow?.Invoke(_s.Width, _s.Height);
            Changed();
            Refresh();
        }

        /// <summary>Rebuilds whichever page is showing so its controls reflect the current settings.</summary>
        private void Refresh()
        {
            if (_page == "changelog") ShowChangelog();
            else ShowPage(_page);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            // No confirmation needed: "Undo changes" brings everything back.
            _s.ResetToDefaults();
            Changed();
            Refresh();
        }

        private void Commit()
        {
            if (_s.StartWithWindows != _startWithWindowsAtOpen || _s.StartWithWindows)
                Settings.SetStartWithWindows(_s.StartWithWindows);
            CleanUpBackgrounds();
            CleanUpProfilePictures();
            _s.Save();
        }

        /// <summary>Call after any setting changes: updates the lyrics window and the preview.</summary>
        private void Changed()
        {
            _apply();
            RefreshPreview();
        }

        // =================================================================
        //  Pages
        // =================================================================

        private void BuildTextPage()
        {
            var font = Card("Font");
            Row(font, "Typeface", null, FontPicker());
            SliderRow(font, "Text size", null, 8, 72, 1, _s.FontSize, v => _s.FontSize = v, v => $"{v:0} pt");
            SliderRow(font, "Current line size", "How much larger the line being sung is", 1, 2.5, 0.05,
                _s.CurrentLineScale, v => _s.CurrentLineScale = v, v => $"{v:0.00}×");
            SliderRow(font, "Line spacing", null, 0, 40, 1, _s.LineSpacing, v => _s.LineSpacing = v, v => $"{v:0} px");
            Row(font, "Alignment", null, AlignmentPicker());

            var style = Card("Highlight");
            SwitchRow(style, "Bold current line", null, _s.CurrentLineBold, v => _s.CurrentLineBold = v);
            SwitchRow(style, "Bold other lines", null, _s.OtherLinesBold, v => _s.OtherLinesBold = v);
            SwitchRow(style, "Fade distant lines", "Lines further from the current one get fainter", _s.FadeDistantLines, v => _s.FadeDistantLines = v);
            SwitchRow(style, "Text shadow", "Keeps text readable on light or see-through backgrounds", _s.TextShadow, v => _s.TextShadow = v);
        }

        private void BuildEffectsPage()
        {
            BuildDanceCard();

            var glow = Card("Glow");
            SwitchRow(glow, "Glow on current line", "A soft halo around the line being sung", _s.CurrentLineGlow, v => _s.CurrentLineGlow = v);
            SwitchRow(glow, "Same colour as the line", "Turn off to choose a different glow colour",
                _s.GlowMatchLine, v => { _s.GlowMatchLine = v; Dispatcher.BeginInvoke(new Action(Refresh)); });
            if (!_s.GlowMatchLine)
                ColorRow(glow, "Glow colour", () => _s.GlowColor, v => _s.GlowColor = v);
            SliderRow(glow, "Size", null, 4, 40, 1, _s.GlowSize, v => _s.GlowSize = v, v => $"{v:0}");
            SliderRow(glow, "Strength", null, 0.1, 1, 0.05, _s.GlowStrength, v => _s.GlowStrength = v, v => $"{v * 100:0}%");

            var focus = Card("Focus");
            SwitchRow(focus, "Karaoke fill", "The current line fills in from left to right as it's sung (time-synced lyrics)",
                _s.KaraokeFill, v => _s.KaraokeFill = v);
            SwitchRow(focus, "Blur other lines", "Softly blurs everything except the line being sung",
                _s.FocusBlur, v => _s.FocusBlur = v);
            SliderRow(focus, "Blur amount", null, 0.5, 8, 0.5, _s.FocusBlurAmount, v => _s.FocusBlurAmount = v, v => $"{v:0.0}");

            var motion = Card("Movement");
            SwitchRow(motion, "Pop", "Each new line grows in with a little bounce", _s.PopEffect, v => _s.PopEffect = v);
            SwitchRow(motion, "Beat pulse", "The current line gently pulses with Spotify's sound", _s.BeatPulse, v => _s.BeatPulse = v);
            SliderRow(motion, "Pulse strength", null, 0.3, 2, 0.1, _s.BeatPulseStrength, v => _s.BeatPulseStrength = v, v => $"{v:0.0}×");
            Hint(motion, "Pop and beat pulse show on the lyrics window itself; the preview above shows glow, fill and blur.");
        }

        private void BuildColoursPage()
        {
            var themes = Card("Themes");
            var wrap = new WrapPanel { Margin = new Thickness(14, 14, 4, 6) };
            foreach (var t in Settings.BuiltInThemes)
                wrap.Children.Add(ThemeCard(t, deletable: false));
            themes.Children.Add(wrap);

            BuildCustomThemes();

            var text = Card("Text");
            ColorRow(text, "Current line", () => _s.CurrentLineColor, v => _s.CurrentLineColor = v);
            ColorRow(text, "Other lines", () => _s.OtherLinesColor, v => _s.OtherLinesColor = v);
            SliderRow(text, "Other lines brightness", null, 0.05, 1, 0.05, _s.OtherLinesOpacity, v => _s.OtherLinesOpacity = v, v => $"{v * 100:0}%");
        }

        private void BuildWindowPage()
        {
            var shape = Card("Shape");
            SliderRow(shape, "Corner roundness", null, 0, 30, 1, _s.CornerRadius, v => _s.CornerRadius = v, v => $"{v:0} px");
            SliderRow(shape, "Padding", "Space between the edge and the text", 0, 40, 1, _s.Padding, v => _s.Padding = v, v => $"{v:0} px");

            var win = Card("Window");
            SwitchRow(win, "Always on top", "Stays above other windows while you work", _s.AlwaysOnTop, v => _s.AlwaysOnTop = v);
            SwitchRow(win, "Show song title and artist", null, _s.ShowTrackInfo, v => _s.ShowTrackInfo = v);
            SwitchRow(win, "Show album art beside the title", null, _s.ShowAlbumArt, v => _s.ShowAlbumArt = v);
            SwitchRow(win, "Lock position", "Stops the window being dragged by accident", _s.LockPosition, v => _s.LockPosition = v);
            SwitchRow(win, "Lock size", "Hides the resize handle so the window can't be resized by accident", _s.LockSize, v => _s.LockSize = v);
            SwitchRow(win, "Show in taskbar", null, _s.ShowInTaskbar, v => _s.ShowInTaskbar = v);
            SwitchRow(win, "Start with Windows", "Opens Kairos when you sign in", _s.StartWithWindows, v => _s.StartWithWindows = v);
            SwitchRow(win, "Icon in the notification area", "By the clock: click it to hide or show Kairos, right-click for play, mini mode and more",
                _s.ShowTrayIcon, v => _s.ShowTrayIcon = v);
            SwitchRow(win, "Shortcuts work in any app", "Ctrl+Alt+C centres the window left-to-right, Ctrl+Alt+M switches mini mode, Ctrl+Alt+T switches click-through",
                _s.GlobalCentreShortcut, v => _s.GlobalCentreShortcut = v);

            var over = Card("Over other apps");
            SwitchRow(over, "Click-through", "Clicks go to whatever is underneath, so Kairos never gets in the way. Hold Ctrl over it to use it (drag, buttons, right-click). Ctrl+Alt+T switches it from any app.",
                _s.ClickThrough, v => _s.ClickThrough = v);
            SwitchRow(over, "Hide for full-screen apps", "Fades out while a game, video or presentation fills the screen Kairos is on. Full-screen apps on another screen don't hide it.",
                _s.HideForFullScreen, v => _s.HideForFullScreen = v);

            var mini = Card("Mini mode");
            Hint(mini, "A slim strip, like subtitles, with just the line being sung. Switch with the mini button on the lyrics window, " +
                       "Ctrl+Alt+M, or the right-click menu. Double-click the strip to go back.");
            SwitchRow(mini, "Show the next line", "Smaller and fainter, underneath", _s.MiniShowNextLine, v => _s.MiniShowNextLine = v);
            SwitchRow(mini, "Stretch across the screen", "As wide as the screen it's on, like a subtitle bar", _s.MiniFullWidth, v => _s.MiniFullWidth = v);
            SliderRow(mini, "Text size", null, 10, 60, 1, _s.MiniFontSize, v => _s.MiniFontSize = v, v => $"{v:0} pt");

            var pair = Card("Together with Spotify");
            SwitchRow(pair, "Open Spotify when Kairos opens", "Only if it isn't open already",
                _s.OpenSpotifyOnStart, v => { _s.OpenSpotifyOnStart = v; Dispatcher.BeginInvoke(new Action(Refresh)); });

            Row(pair, "Spotify's window", _s.SpotifyWindowMode switch
                {
                    "Hidden" => "Spotify plays in the background with no window or taskbar button, so only Kairos is in the taskbar. " +
                                "Right-click the lyrics window → Show Spotify's window when you need it; minimising it hides it again.",
                    "Minimised" => "Kept minimised when Kairos opens it",
                    _ => "Left as it is",
                },
                Segmented(new[] { ("Normal", "Normal"), ("Minimised", "Minimised"), ("Hidden", "Hidden") }, _s.SpotifyWindowMode, v =>
                {
                    _s.SpotifyWindowMode = v;
                    // With no window to close it from, closing Kairos should close Spotify too.
                    // Hidden = Kairos is "the" app: it opens Spotify in the background and closes it with it.
                    if (v == "Hidden")
                    {
                        _s.OpenSpotifyOnStart = true;
                        if (_s.OnKairosClose == "Nothing") _s.OnKairosClose = "Close";
                    }
                    Changed();
                    Dispatcher.BeginInvoke(new Action(Refresh));
                }));

            Row(pair, "When you close Kairos", null, Segmented(
                new[] { ("Nothing", "Leave Spotify"), ("Pause", "Pause it"), ("Close", "Close it") },
                _s.OnKairosClose,
                v => { _s.OnKairosClose = v; Changed(); }));

            Row(pair, "When Spotify closes", _s.OnSpotifyClose == "Hide"
                    ? "Kairos waits in the background and comes back when Spotify opens. Start with Windows is on, so it's ready after you sign in."
                    : null,
                Segmented(
                    new[] { ("Keep", "Keep Kairos"), ("Hide", "Hide Kairos"), ("Close", "Close Kairos") },
                    _s.OnSpotifyClose,
                    v =>
                    {
                        _s.OnSpotifyClose = v;
                        // Waiting in the background only helps if Kairos is running when Spotify opens.
                        if (v == "Hide") _s.StartWithWindows = true;
                        Changed();
                        Dispatcher.BeginInvoke(new Action(Refresh)); // rebuild after this click finishes
                    }));
        }

        private void BuildBehaviourPage()
        {
            var follow = Card("Following the song");
            SwitchRow(follow, "Smooth animations", "Glide between lines instead of jumping", _s.SmoothScrolling, v => _s.SmoothScrolling = v);
            SwitchRow(follow, "Click a line to jump to it", null, _s.ClickToSeek, v => _s.ClickToSeek = v);
            SliderRow(follow, "Return to current line", "After you scroll away to browse", 0, 30, 1, _s.AutoResumeSeconds,
                v => _s.AutoResumeSeconds = (int)Math.Round(v), v => v < 0.5 ? "Never" : $"after {v:0} s");

            var timing = Card("Timing");
            Slider? timingSlider = null;
            timingSlider = SliderRow(timing, "Lyrics timing", "If lines change before or after they're sung. The [ and ] keys adjust this too.",
                -3000, 3000, 50, _s.SyncOffsetMs, v => _s.SyncOffsetMs = (int)Math.Round(v),
                v => Math.Abs(v) < 1 ? "In sync" : v > 0 ? $"{v / 1000:0.00} s earlier" : $"{-v / 1000:0.00} s later");
            var resetTiming = new Button { Style = (Style)FindResource("TextButton"), Content = "Reset timing", Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            resetTiming.Click += (_, _) => { if (timingSlider != null) timingSlider.Value = 0; };
            timing.Children.Add(resetTiming);
            SwitchRow(timing, "Show timing info", "Displays the playback position on the lyrics window, handy if sync seems off",
                _s.ShowTimingInfo, v => _s.ShowTimingInfo = v);

            var none = Card("Songs without lyrics");
            var fillerBox = new ComboBox
            {
                Style = (Style)FindResource("DarkCombo"),
                Width = 230,
                FocusVisualStyle = null,
                ItemsSource = LyricsFiller.Modes.Select(m => m.Label).ToList(),
                SelectedIndex = Math.Max(0, Array.FindIndex(LyricsFiller.Modes, m => m.Key == _s.NoLyricsFiller)),
            };
            fillerBox.SelectionChanged += (_, _) =>
            {
                if (fillerBox.SelectedIndex >= 0) { _s.NoLyricsFiller = LyricsFiller.Modes[fillerBox.SelectedIndex].Key; Changed(); }
            };
            Row(none, "Show instead", "For instrumentals and songs you've marked with \"This song has no lyrics\"", fillerBox);
            SwitchRow(none, "Show \"No lyrics for this song\"", "The caption under it (also \"Instrumental\"). Turn off for just the visual",
                _s.ShowFillerCaption, v => _s.ShowFillerCaption = v);
            SwitchRow(none, "Also when lyrics can't be found", "Shows it (with the Add lyrics buttons) instead of the plain message",
                _s.FillerWhenNotFound, v => _s.FillerWhenNotFound = v);

            var lists = Card("Playlists without lyrics");
            foreach (var (id, name) in _s.NoLyricsPlaylists.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var remove = new Button { Style = (Style)FindResource("TextButton"), Content = "Remove" };
                var playlistId = id;
                remove.Click += (_, _) =>
                {
                    _s.NoLyricsPlaylists.Remove(playlistId);
                    _s.Save();
                    _lyricsChanged();
                    Refresh();
                };
                Row(lists, name, "Songs played from it skip the lyrics search", remove);
            }
            Hint(lists, (_s.NoLyricsPlaylists.Count == 0 ? "None yet. " : "") +
                        "To add one, play it and right-click the lyrics window → This playlist has no lyrics, " +
                        "or use Mark as no lyrics in the playlist browser. Needs your Spotify connection.");

            var data = Card("Saved lyrics");
            var clear = new Button { Style = (Style)FindResource("GhostButton"), Content = "Clear" };
            clear.Click += (_, _) =>
            {
                LyricsService.ClearCache();
                clear.Content = "Cleared";
                clear.IsEnabled = false;
            };
            Row(data, "Clear saved lyrics", "Songs are looked up again the next time they play", clear);
        }

        private void BuildBackgroundPage()
        {
            var mode = Card("Behind the lyrics");
            Row(mode, "Background", null, Segmented(
                new[] { ("Colour", "Colour"), ("Image", "Image or GIF"), ("AlbumArt", "Album art") },
                _s.BackgroundMode,
                v => { _s.BackgroundMode = v; Changed(); Refresh(); }));

            Hint(mode, "Backgrounds are saved with themes: set one up here, then use Save current look on the Colours page " +
                       "to switch between looks in one click.");

            switch (_s.BackgroundMode == "Dance" ? "Colour" : _s.BackgroundMode)
            {
                case "Image": BuildImageSection(); break;
                case "AlbumArt": BuildAlbumArtSection(); break;
                default:
                    var colour = Card("Colour");
                    ColorRow(colour, "Colour", () => _s.BackgroundColor, v => _s.BackgroundColor = v);
                    SliderRow(colour, "Opacity", "0% leaves just the text floating on your desktop", 0, 1, 0.05,
                        _s.BackgroundOpacity, v => _s.BackgroundOpacity = v, v => $"{v * 100:0}%");
                    break;
            }

            // The colour dance works with any background, so it's here as well as on the Effects page.
            BuildDanceCard();
        }

        private void BuildImageSection()
        {
            bool hasImage = !string.IsNullOrEmpty(_s.BackgroundImagePath) && File.Exists(_s.BackgroundImagePath);
            if (!hasImage)
            {
                // Until an image is chosen, just the picker: the preview and editing controls appear afterwards.
                var pick = Card("Your image");
                pick.Children.Add(DropZone());
                return;
            }

            var card = Card("Your image");
            var previewHolder = new StackPanel { Margin = new Thickness(16, 16, 16, 8) };
            previewHolder.Children.Add(MakeLargePreview());
            card.Children.Add(previewHolder);

            var change = new Button { Style = (Style)FindResource("GhostButton"), Content = "Change image…", Margin = new Thickness(0, 0, 8, 0) };
            var remove = new Button { Style = (Style)FindResource("TextButton"), Content = "Remove" };
            change.Click += (_, _) => ChooseImageFile();
            remove.Click += (_, _) =>
            {
                _s.BackgroundImagePath = "";
                _s.BackgroundMode = "Colour";
                ImageProcessor.Invalidate();
                Changed();
                Refresh();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14) };
            buttons.Children.Add(change);
            buttons.Children.Add(remove);
            card.Children.Add(buttons);

            static string Signed(double v) => Math.Abs(v) < 0.5 ? "0" : $"{v:+0;-0}";

            var adjust = Card("Adjust");
            SliderRow(adjust, "Brightness", null, -100, 100, 1, _s.ImageBrightness, v => _s.ImageBrightness = v, Signed);
            SliderRow(adjust, "Contrast", null, -100, 100, 1, _s.ImageContrast, v => _s.ImageContrast = v, Signed);
            SliderRow(adjust, "Saturation", "0% is black and white", 0, 200, 1, _s.ImageSaturation, v => _s.ImageSaturation = v, v => $"{v:0}%");
            SliderRow(adjust, "Blur", null, 0, 60, 1, _s.ImageBlur, v => _s.ImageBlur = v, v => v < 0.5 ? "None" : $"{v:0}");
            SliderRow(adjust, "Opacity", "Lower lets your desktop show through", 0, 1, 0.05, _s.ImageOpacity, v => _s.ImageOpacity = v, v => $"{v * 100:0}%");
            ColorRow(adjust, "Tint colour", () => _s.ImageTintColor, v => _s.ImageTintColor = v);
            SliderRow(adjust, "Tint strength", "Darkens or colours the image so lyrics stay readable", 0, 1, 0.05,
                _s.ImageTintStrength, v => _s.ImageTintStrength = v, v => $"{v * 100:0}%");

            var position = Card("Position");
            Row(position, "Fit", "Fill crops to cover the window; Fit shows the whole picture", Segmented(
                new[] { ("Fill", "Fill"), ("Fit", "Fit"), ("Stretch", "Stretch"), ("Tile", "Tile") },
                _s.ImageFit,
                v => { _s.ImageFit = v; Changed(); }));
            SliderRow(position, "Zoom", "Below 100% makes the picture smaller than the window", 0.25, 3, 0.05, _s.ImageZoom,
                v => _s.ImageZoom = v, v => $"{v * 100:0}%");
            Row(position, "Around the picture", "What shows in any space the picture doesn't cover", Segmented(
                new[] { ("Clear", "Desktop"), ("Colour", "Window colour") },
                _s.ImageSurround,
                v => { _s.ImageSurround = v; Changed(); Dispatcher.BeginInvoke(new Action(Refresh)); }));
            if (_s.ImageSurround == "Colour")
            {
                ColorRow(position, "Window colour", () => _s.BackgroundColor, v => _s.BackgroundColor = v);
                SliderRow(position, "Window colour opacity", null, 0, 1, 0.05, _s.BackgroundOpacity, v => _s.BackgroundOpacity = v, v => $"{v * 100:0}%");
            }
            SliderRow(position, "Horizontal position", null, -100, 100, 1, _s.ImageOffsetX, v => _s.ImageOffsetX = v, v => Math.Abs(v) < 0.5 ? "Centre" : $"{v:+0;-0}");
            SliderRow(position, "Vertical position", null, -100, 100, 1, _s.ImageOffsetY, v => _s.ImageOffsetY = v, v => Math.Abs(v) < 0.5 ? "Centre" : $"{v:+0;-0}");
            SwitchRow(position, "Mirror", "Flip the picture left to right", _s.ImageFlip, v => _s.ImageFlip = v);

            var reset = new Button { Style = (Style)FindResource("TextButton"), Content = "Reset image settings", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            reset.Click += (_, _) => { _s.ResetImageAdjustments(); Changed(); Refresh(); };
            PageHost.Children.Add(reset);
        }

        /// <summary>A dashed area to click, or drop an image file on.</summary>
        private UIElement DropZone()
        {
            var dashed = new Rectangle
            {
                RadiusX = 10, RadiusY = 10,
                Stroke = (Brush)FindResource("ControlBorderBrush"),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 4 },
            };
            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "\uE91B", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 24, Foreground = (Brush)FindResource("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            text.Children.Add(new TextBlock { Text = "Choose an image", FontSize = 14, Foreground = (Brush)FindResource("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2) });
            text.Children.Add(new TextBlock { Text = "or drop one here  ·  JPG, PNG, BMP, WebP, or an animated GIF", FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });

            var grid = new Grid { Height = 150 };
            grid.Children.Add(dashed);
            grid.Children.Add(text);
            var zone = new Border { Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, AllowDrop = true, Margin = new Thickness(16) };

            zone.MouseEnter += (_, _) => dashed.Stroke = (Brush)FindResource("AccentBrush");
            zone.MouseLeave += (_, _) => dashed.Stroke = (Brush)FindResource("ControlBorderBrush");
            zone.MouseLeftButtonUp += (_, _) => ChooseImageFile();
            zone.DragEnter += (_, _) => dashed.Stroke = (Brush)FindResource("AccentBrush");
            zone.DragLeave += (_, _) => dashed.Stroke = (Brush)FindResource("ControlBorderBrush");
            zone.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            zone.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) AdoptImage(files[0]);
            };
            return zone;
        }

        private void ChooseImageFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a background image",
                Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == true) AdoptImage(dlg.FileName);
        }

        /// <summary>Copies the picture into Kairos's own folder (so moving the original doesn't break it) and uses it.</summary>
        private void AdoptImage(string file)
        {
            if (!ImageProcessor.CanOpen(file))
            {
                Refresh();
                ShowLyricsBanner("That file isn't an image Kairos can open. Try a JPG or PNG.");
                return;
            }
            try
            {
                Directory.CreateDirectory(Settings.BackgroundsFolder);
                var dest = System.IO.Path.Combine(Settings.BackgroundsFolder,
                    $"background-{DateTime.Now:yyyyMMdd-HHmmss}{System.IO.Path.GetExtension(file).ToLowerInvariant()}");
                File.Copy(file, dest, overwrite: true);

                ImageProcessor.Invalidate();
                _s.BackgroundImagePath = dest;
                _s.BackgroundMode = "Image";
                _s.ImageZoom = 1;
                _s.ImageOffsetX = 0;
                _s.ImageOffsetY = 0;
                Changed();
                Refresh();
            }
            catch (Exception ex)
            {
                Refresh();
                ShowLyricsBanner($"Couldn't use that image: {ex.Message}");
            }
        }

        /// <summary>Removes background copies that are no longer in use (keeps the one still selected).</summary>
        private void CleanUpBackgrounds()
        {
            try
            {
                if (!Directory.Exists(Settings.BackgroundsFolder)) return;
                // Keep the current picture and any picture a saved theme uses.
                var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _s.BackgroundImagePath };
                foreach (var t in _s.CustomThemes)
                    if (t.Image != null) inUse.Add(t.Image.Path);
                foreach (var f in Directory.GetFiles(Settings.BackgroundsFolder))
                    if (!inUse.Contains(f))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        /// <summary>The colour dance: its own layer, behind (or in front of) whatever background you have.</summary>
        private void BuildDanceCard()
        {
            var dance = Card("Colour dance");
            SwitchRow(dance, "Colour dance",
                "Glowing colours from each song's album art drift around, swelling and speeding up with the beat",
                _s.DanceOn, v => { _s.DanceOn = v; Dispatcher.BeginInvoke(new Action(Refresh)); });
            if (!_s.DanceOn) return;

            Row(dance, "Where", _s.DanceLayer == "Front"
                    ? "Over your background. Lower the opacity to let it show through"
                    : "Behind your background picture or GIF, showing through its see-through parts or wherever you lower its opacity",
                Segmented(new[] { ("Behind", "Behind the picture"), ("Front", "In front") }, _s.DanceLayer,
                    v => { _s.DanceLayer = v; Changed(); Dispatcher.BeginInvoke(new Action(Refresh)); }));
            SliderRow(dance, "Opacity", "Lower makes it more see-through", 0.05, 1, 0.05, _s.DanceOpacity, v => _s.DanceOpacity = v, v => $"{v * 100:0}%");
            SliderRow(dance, "Speed", null, 0.2, 2.5, 0.1, _s.DanceSpeed, v => _s.DanceSpeed = v, v => $"{v:0.0}×");
            SliderRow(dance, "Moves with the music", "How much the beat makes it swell and speed up", 0, 2, 0.1,
                _s.DanceEnergy, v => _s.DanceEnergy = v, v => v < 0.05 ? "Not at all" : $"{v * 100:0}%");
            SliderRow(dance, "Size", "How big each colour is", 0.5, 1.6, 0.05, _s.DanceSize, v => _s.DanceSize = v, v => $"{v * 100:0}%");
            if (_s.BackgroundMode == "Colour")
            {
                SliderRow(dance, "Darkening", "Dims it with your window colour so lyrics stay readable", 0, 0.9, 0.05,
                    _s.DanceDim, v => _s.DanceDim = v, v => $"{v * 100:0}%");
                ColorRow(dance, "Darkening colour", () => _s.BackgroundColor, v => _s.BackgroundColor = v);
            }
            else if (_s.BackgroundMode == "Image" && _s.DanceLayer != "Front" && _s.ImageOpacity >= 0.99)
            {
                var lower = new Button { Style = (Style)FindResource("GhostButton"), Content = "Set picture to 60%" };
                lower.Click += (_, _) => { _s.ImageOpacity = 0.6; Changed(); Refresh(); };
                Row(dance, "Your picture is fully solid", "So the dance behind it can't be seen yet. Lower the picture's opacity, or use a GIF/PNG with see-through parts", lower);
            }
            Hint(dance, "The preview uses a made-up beat; on the lyrics window it follows Spotify's sound. With no album art, a default set of colours is used.");
        }

        private void BuildAlbumArtSection()
        {
            var art = Card("Album art");
            SliderRow(art, "Blur", null, 0, 80, 1, _s.AlbumArtBlur, v => _s.AlbumArtBlur = v, v => v < 0.5 ? "None" : $"{v:0}");
            SliderRow(art, "Dimming", "Darkens the artwork with your window colour so lyrics stay readable", 0, 1, 0.05,
                _s.AlbumArtDim, v => _s.AlbumArtDim = v, v => $"{v * 100:0}%");
            ColorRow(art, "Dimming colour", () => _s.BackgroundColor, v => _s.BackgroundColor = v);
            SwitchRow(art, "Slow drift", "A gentle, continuous zoom and pan on the artwork", _s.AnimateAlbumArt, v => _s.AnimateAlbumArt = v);
            Hint(art, "Shows whenever the song has a cover; otherwise your window colour is used.");

            var canvas = Card("About Spotify Canvas");
            Hint(canvas, "Spotify's looping Canvas videos aren't shared with other apps, so Kairos can't show them. " +
                         "The album art background with slow drift is the closest alternative.");
        }

        private void Hint(StackPanel card, string text)
        {
            card.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12.5,
                LineHeight = 19,
                Margin = new Thickness(16, 10, 16, 12),
            });
        }

        /// <summary>A row of word buttons where one is selected, e.g. Colour | Image | Album art.</summary>
        private UIElement Segmented((string Value, string Label)[] options, string current, Action<string> onPick)
        {
            string group = Guid.NewGuid().ToString("N");
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (value, label) in options)
            {
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("TextSegment"),
                    Content = label,
                    GroupName = group,
                    IsChecked = string.Equals(current, value, StringComparison.OrdinalIgnoreCase),
                };
                rb.Checked += (_, _) => onPick(value);
                panel.Children.Add(rb);
            }
            return new Border
            {
                Background = (Brush)FindResource("ControlBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                Child = panel,
            };
        }

        // ----- My lyrics -----

        private string? _editingPath; // the file being edited, if any

        private void BuildLyricsPage()
        {
            string folder = LocalLyrics.FolderFor(_s);
            var track = _currentTrack();

            // ---- Add / edit ----
            var form = Card("Add lyrics");

            var artistBox = new TextBox { Style = (Style)FindResource("DarkText"), FontFamily = FontFamily, FontSize = 13, Width = 300 };
            var titleBox = new TextBox { Style = (Style)FindResource("DarkText"), FontFamily = FontFamily, FontSize = 13, Width = 300 };
            Row(form, "Artist", null, artistBox);
            Row(form, "Song", null, titleBox);

            var textArea = new TextBox { Style = (Style)FindResource("DarkTextArea"), Height = 220 };
            var hint = new TextBlock
            {
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Paste the lyrics below, one line per line, or search for them online. To make them follow the song, " +
                       "click Sync timings and tap along as it plays.",
            };
            var areaPanel = new StackPanel { Margin = new Thickness(16, 4, 16, 4) };
            areaPanel.Children.Add(hint);
            areaPanel.Children.Add(textArea);
            form.Children.Add(areaPanel);

            var status = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
            var useCurrent = new Button { Style = (Style)FindResource("TextButton"), Content = "Use current song" };
            var searchOnline = new Button { Style = (Style)FindResource("TextButton"), Content = "Search online…", Margin = new Thickness(4, 0, 0, 0) };
            var importFile = new Button { Style = (Style)FindResource("TextButton"), Content = "Import files…", Margin = new Thickness(4, 0, 0, 0) };
            var syncButton = new Button { Style = (Style)FindResource("GhostButton"), Content = "Sync timings…", Margin = new Thickness(0, 0, 8, 0),
                                          ToolTip = "Play the song and tap along to time each line" };
            var save = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Save" };

            // Row 1: ways to get lyrics in. Row 2: status, then sync and save.
            var sources = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 16, 0) };
            sources.Children.Add(useCurrent);
            sources.Children.Add(searchOnline);
            sources.Children.Add(importFile);
            form.Children.Add(sources);

            var actions = new DockPanel { Margin = new Thickness(16, 8, 16, 14), LastChildFill = false };
            DockPanel.SetDock(status, Dock.Left);
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(syncButton, Dock.Right);
            actions.Children.Add(status);
            actions.Children.Add(save);
            actions.Children.Add(syncButton);
            form.Children.Add(actions);

            // Online search results appear here.
            var results = new StackPanel { Margin = new Thickness(16, 0, 16, 10), Visibility = Visibility.Collapsed };
            form.Children.Add(results);

            void FillCurrent()
            {
                var t = _currentTrack();
                if (t == null) { status.Text = "Nothing is playing in Spotify."; return; }
                artistBox.Text = LyricsService.FirstArtist(t.Artist);
                titleBox.Text = LyricsService.CleanTitle(t.Title);
                var existing = LocalLyrics.FindFile(folder, t);
                _editingPath = existing;
                textArea.Text = existing != null ? SafeRead(existing) : "";
                status.Text = existing != null ? "Editing your saved lyrics for this song." : "";
            }

            // Start with a song picked on the Statistics page, or the song that's playing.
            if (_lyricsPrefill is { } pre)
            {
                _lyricsPrefill = null;
                artistBox.Text = pre.Artist;
                titleBox.Text = pre.Title;
                var existing = LocalLyrics.FindFile(folder, new TrackInfo(pre.Title, pre.Artist, "", TimeSpan.Zero));
                _editingPath = existing;
                textArea.Text = existing != null ? SafeRead(existing) : "";
                status.Text = "Search online, paste, or import the lyrics, then Sync timings.";
            }
            else if (track != null) FillCurrent();
            useCurrent.Click += (_, _) => FillCurrent();

            // ---- Sync timings: tap along with the song ----
            syncButton.Click += (_, _) =>
            {
                var artist = artistBox.Text.Trim();
                var title = titleBox.Text.Trim();
                if (artist.Length == 0 || title.Length == 0) { status.Text = "Add the artist and song name first."; return; }
                if (_media == null) { status.Text = "Syncing needs the lyrics window running."; return; }

                // Already-synced text keeps its timings, so you can fix just a line or two.
                List<string> lines;
                List<TimeSpan?>? times = null;
                var text = textArea.Text;
                if (LocalLyrics.HasTimestamps(text))
                {
                    var parsed = LyricsService.ParseLrc(text).Where(l => l.Text.Trim().Length > 0).ToList();
                    lines = parsed.Select(l => l.Text.Trim()).ToList();
                    times = parsed.Select(l => (TimeSpan?)l.Time).ToList();
                }
                else lines = LocalLyrics.LinesForSync(text);

                if (lines.Count == 0) { status.Text = "Paste the lyrics first, one line per line."; return; }

                var sync = new SyncWindow(_media, _spotify, artist, title, lines, lrc =>
                {
                    textArea.Text = lrc;
                    status.Text = "Timings added. Save to keep them.";
                }, times) { Owner = this };
                sync.ShowDialog();
            };

            // ---- Search online ----
            searchOnline.Click += async (_, _) =>
            {
                var query = $"{artistBox.Text.Trim()} {titleBox.Text.Trim()}".Trim();
                if (query.Length == 0) { status.Text = "Type the artist and song name first."; return; }
                results.Children.Clear();
                results.Visibility = Visibility.Visible;
                results.Children.Add(new TextBlock { Text = "Searching…", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12.5 });
                List<LyricsService.SearchResult> found;
                try { found = await new LyricsService().SearchManyAsync(query); }
                catch (Exception) { results.Children.Clear(); results.Children.Add(new TextBlock { Text = "Couldn't reach the lyrics library. Check your internet connection.", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12.5 }); return; }

                results.Children.Clear();
                results.Children.Add(new TextBlock
                {
                    Text = found.Count == 0 ? "Nothing found. Try fewer words, or just the song name." : "Pick a match to load it into the box above:",
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 12.5,
                    Margin = new Thickness(0, 0, 0, 6),
                });
                foreach (var r in found)
                {
                    var use = new Button { Style = (Style)FindResource("TextButton"), Content = "Use" };
                    var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
                    titleLine.Children.Add(new TextBlock { Text = r.Title, Foreground = (Brush)FindResource("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });
                    titleLine.Children.Add(new Border
                    {
                        Margin = new Thickness(8, 0, 0, 0),
                        Padding = new Thickness(6, 0, 6, 1),
                        CornerRadius = new CornerRadius(6),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = r.Synced ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("ControlBrush"),
                        Child = new TextBlock
                        {
                            Text = r.Synced ? "synced" : "plain",
                            FontSize = 10.5,
                            Foreground = r.Synced ? new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B)) : (Brush)FindResource("MutedBrush"),
                        },
                    });
                    info.Children.Add(titleLine);
                    string dur = r.DurationSeconds > 0 ? $"  ·  {r.DurationSeconds / 60}:{r.DurationSeconds % 60:00}" : "";
                    info.Children.Add(new TextBlock { Text = $"{r.Artist}{(r.Album.Length > 0 ? "  ·  " + r.Album : "")}{dur}", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                    line.Children.Add(info);
                    Grid.SetColumn(use, 1);
                    line.Children.Add(use);
                    var chosen = r;
                    use.Click += (_, _) =>
                    {
                        textArea.Text = chosen.Text;
                        results.Visibility = Visibility.Collapsed;
                        status.Text = chosen.Synced
                            ? "Loaded synced lyrics. Save to keep them, or Sync timings to adjust."
                            : "Loaded plain lyrics. Use Sync timings to time them to the song.";
                    };
                    results.Children.Add(line);
                }
            };

            save.Click += (_, _) =>
            {
                var artist = artistBox.Text.Trim();
                var title = titleBox.Text.Trim();
                var text = textArea.Text.Trim();
                if (artist.Length == 0 || title.Length == 0) { status.Text = "Add the artist and song name first."; return; }
                if (text.Length == 0) { status.Text = "Paste or import some lyrics first."; return; }
                try
                {
                    var path = LocalLyrics.Save(folder, artist, title, text);
                    if (_editingPath != null && !string.Equals(_editingPath, path, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(_editingPath))
                        LocalLyrics.Delete(_editingPath); // renamed or changed format: remove the old file
                    _editingPath = null;
                    _lyricsChanged();
                    ShowPage("lyrics");
                    ShowLyricsBanner(LocalLyrics.HasTimestamps(text) ? $"Saved \"{title}\" (time-synced)." : $"Saved \"{title}\".");
                }
                catch (Exception ex)
                {
                    status.Text = $"Couldn't save: {ex.Message}";
                }
            };

            importFile.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import lyrics",
                    Filter = "Lyrics files (*.lrc;*.txt)|*.lrc;*.txt|All files (*.*)|*.*",
                    Multiselect = true,
                };
                if (dlg.ShowDialog(this) != true) return;

                if (dlg.FileNames.Length == 1)
                {
                    // One file: load it into the form to check before saving.
                    try
                    {
                        var guess = LocalLyrics.ReadForImport(dlg.FileName);
                        if (!string.IsNullOrWhiteSpace(guess.Artist)) artistBox.Text = guess.Artist;
                        if (!string.IsNullOrWhiteSpace(guess.Title)) titleBox.Text = guess.Title;
                        textArea.Text = guess.Text;
                        _editingPath = null;
                        status.Text = "Check the details, then Save.";
                    }
                    catch (Exception ex) { status.Text = $"Couldn't read that file: {ex.Message}"; }
                    return;
                }

                // Several files: save each one whose artist and title can be worked out.
                int saved = 0;
                var skipped = new List<string>();
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        var g = LocalLyrics.ReadForImport(file);
                        if (string.IsNullOrWhiteSpace(g.Artist) || string.IsNullOrWhiteSpace(g.Title) || g.Text.Trim().Length == 0)
                        {
                            skipped.Add(System.IO.Path.GetFileName(file));
                            continue;
                        }
                        LocalLyrics.Save(folder, g.Artist!, g.Title!, g.Text.Trim());
                        saved++;
                    }
                    catch { skipped.Add(System.IO.Path.GetFileName(file)); }
                }
                _lyricsChanged();
                ShowPage("lyrics");
                ShowLyricsBanner(skipped.Count == 0
                    ? $"Imported {saved} songs."
                    : $"Imported {saved} songs. Skipped {skipped.Count} where the artist or song couldn't be worked out " +
                      "(name them \"Artist - Song.lrc\", or import them one at a time): " +
                      string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? "…" : ""));
            };

            // ---- Saved lyrics ----
            var entries = LocalLyrics.List(folder);
            var list = Card($"Saved lyrics ({entries.Count})");
            if (entries.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "Nothing saved yet. Lyrics you add appear here, and in the folder below.",
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 12, 16, 12),
                });
            }
            foreach (var entry in entries)
            {
                var edit = new Button { Style = (Style)FindResource("TextButton"), Content = "Edit" };
                var del = new Button { Style = (Style)FindResource("TextButton"), Content = "Delete", Margin = new Thickness(4, 0, 0, 0) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(edit);
                buttons.Children.Add(del);

                edit.Click += (_, _) =>
                {
                    artistBox.Text = entry.Artist;
                    titleBox.Text = entry.Title;
                    textArea.Text = SafeRead(entry.Path);
                    _editingPath = entry.Path;
                    status.Text = "Editing. Save when you're done.";
                    PageScroller.ScrollToTop();
                };

                // Two clicks to delete, so a stray click doesn't lose anything.
                bool confirming = false;
                del.Click += (_, _) =>
                {
                    if (!confirming)
                    {
                        if (entry.NoLyrics)
                        {
                            // Undoing a "no lyrics" mark is harmless, so no second click needed.
                            LocalLyrics.Delete(entry.Path);
                            _lyricsChanged();
                            ShowPage("lyrics");
                            return;
                        }
                        confirming = true;
                        del.Content = "Click again to delete";
                        del.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                        return;
                    }
                    LocalLyrics.Delete(entry.Path);
                    _lyricsChanged();
                    ShowPage("lyrics");
                };
                del.MouseLeave += (_, _) =>
                {
                    if (!confirming) return;
                    confirming = false;
                    del.Content = entry.NoLyrics ? "Undo" : "Delete";
                    del.ClearValue(ForegroundProperty);
                };
                if (entry.NoLyrics)
                {
                    // Marked as having no lyrics: nothing to edit; deleting the marker is the undo.
                    edit.Visibility = Visibility.Collapsed;
                    del.Content = "Undo";
                    del.ToolTip = "Look for lyrics for this song again";
                }
                Row(list, entry.Title, entry.Artist + (entry.NoLyrics ? "  ·  no lyrics" : entry.Synced ? "  ·  time-synced" : "  ·  plain"), buttons);
            }

            // ---- Folder ----
            var where = Card("Folder");
            var open = new Button { Style = (Style)FindResource("GhostButton"), Content = "Open", Margin = new Thickness(0, 0, 8, 0) };
            var change = new Button { Style = (Style)FindResource("GhostButton"), Content = "Change…" };
            var folderButtons = new StackPanel { Orientation = Orientation.Horizontal };
            folderButtons.Children.Add(open);
            folderButtons.Children.Add(change);
            Row(where, "Lyrics folder", folder + "\nOne folder per artist, one file per song. You can add or edit files there directly too.", folderButtons);
            open.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                }
                catch { }
            };
            change.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your lyrics folder" };
                if (Directory.Exists(folder)) dlg.InitialDirectory = folder;
                if (dlg.ShowDialog(this) != true) return;
                _s.LyricsFolder = string.Equals(dlg.FolderName, LocalLyrics.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : dlg.FolderName;
                _s.Save();
                _lyricsChanged();
                ShowPage("lyrics");
            };
            if (!string.IsNullOrEmpty(_s.LyricsFolder))
            {
                var reset = new Button { Style = (Style)FindResource("TextButton"), Content = "Use the default folder", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 10, 10) };
                reset.Click += (_, _) => { _s.LyricsFolder = ""; _s.Save(); _lyricsChanged(); ShowPage("lyrics"); };
                where.Children.Add(reset);
            }
        }

        /// <summary>A confirmation banner at the top of the current page.</summary>
        private void ShowLyricsBanner(string message)
        {
            PageHost.Children.Insert(0, new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0x1E, 0xD7, 0x60)),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock { Text = message, Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap },
            });
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return ""; }
        }

        private void BuildSpotifyPage()
        {
            if (_spotify.IsConnected)
            {
                var conn = Card("Connected");
                var disconnect = new Button { Style = (Style)FindResource("GhostButton"), Content = "Disconnect" };
                disconnect.Click += (_, _) =>
                {
                    _spotify.Disconnect();
                    Changed();
                    Refresh();
                };
                Row(conn, $"Signed in as {(_s.SpotifyUserName.Length > 0 ? _s.SpotifyUserName : "your Spotify account")}",
                    "Kairos can add the current song to your playlists, list them, and play them.", disconnect);

                if (!_spotify.HasLibraryAccess)
                {
                    var upgrade = Card("Liked Songs");
                    var reconnect = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Reconnect" };
                    var note = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 12) };
                    Row(upgrade, "Allow access to Liked Songs",
                        "Kairos needs one more permission to list and save your Liked Songs. Reconnect and approve it in your browser.", reconnect);
                    upgrade.Children.Add(note);
                    reconnect.Click += async (_, _) =>
                    {
                        if (_connectCts != null) { _connectCts.Cancel(); return; }
                        _connectCts = new System.Threading.CancellationTokenSource();
                        reconnect.Content = "Cancel";
                        note.Text = "Waiting for you to approve access in your browser…";
                        try
                        {
                            await _spotify.ConnectAsync(_s.SpotifyClientId, _connectCts.Token);
                            Changed();
                            Refresh();
                            ShowLyricsBanner("Liked Songs is ready.");
                            Activate();
                        }
                        catch (SpotifyException ex) { note.Text = ex.Message; }
                        catch (OperationCanceledException) { note.Text = "Cancelled."; }
                        finally
                        {
                            _connectCts?.Dispose();
                            _connectCts = null;
                            reconnect.Content = "Reconnect";
                        }
                    };
                }

                var use = Card("Using it");
                Hint(use, "Hover over the lyrics window: the + button adds the song that's playing to a playlist, and the list button opens your playlists. " +
                          "Both are also in the right-click menu.");

                var about = Card("Your developer app");
                Row(about, "Client ID", MaskId(_s.SpotifyClientId), new Border());
                Hint(about, "Spotify limits each developer app to a few users and needs Premium on the account that owns it. " +
                            "If sign-in ever says your account isn't registered, add your Spotify email under User Management in the dashboard.");
                return;
            }

            var intro = Card(null);
            Hint(intro, "Playlist features use Spotify's Web API, which needs a (free) developer app registered to your own account. " +
                        "It's a one-time setup of about five minutes, and needs Spotify Premium.");

            var steps = Card("Set up");
            Step(steps, 1, "Open the Spotify Developer Dashboard and sign in with your Spotify account. Click Create app.",
                ActionButton("Open dashboard", () =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SpotifyClient.DashboardUrl) { UseShellExecute = true }); } catch { }
                }));

            var uriBox = new TextBox { Style = (Style)FindResource("DarkText"), Text = SpotifyClient.RedirectUri, IsReadOnly = true, Width = 250 };
            var copy = ActionButton("Copy", () => { });
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(SpotifyClient.RedirectUri); copy.Content = "Copied"; } catch { }
            };
            var uriRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            uriRow.Children.Add(uriBox);
            copy.Margin = new Thickness(8, 0, 0, 0);
            uriRow.Children.Add(copy);
            Step(steps, 2, "Name it anything (for example \"Kairos\") and add a short description. Under Redirect URIs, add exactly this address:", uriRow);

            Step(steps, 3, "Under \"Which API/SDKs are you planning to use?\" tick Web API, accept the terms and Save.", null);

            var idBox = new TextBox { Style = (Style)FindResource("DarkText"), Text = _s.SpotifyClientId, Width = 300 };
            var connect = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Connect", Margin = new Thickness(8, 0, 0, 0) };
            var status = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            var idRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            idRow.Children.Add(idBox);
            idRow.Children.Add(connect);
            var idPanel = new StackPanel();
            idPanel.Children.Add(idRow);
            idPanel.Children.Add(status);
            Step(steps, 4, "On your new app's page, open Settings and copy the Client ID. Paste it here and click Connect, then approve access in your browser.", idPanel);

            connect.Click += async (_, _) =>
            {
                if (_connectCts != null)
                {
                    // Second click while waiting = cancel.
                    _connectCts.Cancel();
                    return;
                }
                _connectCts = new System.Threading.CancellationTokenSource();
                connect.Content = "Cancel";
                status.Foreground = (Brush)FindResource("MutedBrush");
                status.Text = "Waiting for you to approve access in your browser…";
                try
                {
                    await _spotify.ConnectAsync(idBox.Text, _connectCts.Token);
                    Changed();
                    Refresh();
                    ShowLyricsBanner($"Connected to Spotify as {_s.SpotifyUserName}.");
                    Activate();
                }
                catch (SpotifyException ex)
                {
                    status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
                    status.Text = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    status.Text = "Cancelled.";
                }
                finally
                {
                    _connectCts?.Dispose();
                    _connectCts = null;
                    connect.Content = "Connect";
                }
            };

            var privacy = Card("Privacy");
            Hint(privacy, "Kairos never sees your Spotify password. Sign-in happens on Spotify's own page, and the access it grants is " +
                          "stored encrypted so only your Windows account can read it. You can remove access any time with Disconnect, " +
                          "or on spotify.com under Manage apps.");
        }

        /// <summary>A numbered setup step with optional controls underneath.</summary>
        private void Step(StackPanel card, int number, string text, UIElement? extra)
        {
            if (card.Children.Count > 0)
                card.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush"), Margin = new Thickness(16, 0, 16, 0) });

            var grid = new Grid { Margin = new Thickness(16, 14, 16, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("ControlBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = number.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            grid.Children.Add(badge);

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextBrush"), LineHeight = 19, Margin = new Thickness(0, 2, 0, 0) });
            if (extra != null) body.Children.Add(extra);
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            card.Children.Add(grid);
        }

        private Button ActionButton(string text, Action onClick)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            b.Click += (_, _) => onClick();
            return b;
        }

        private static string MaskId(string id) =>
            id.Length <= 8 ? id : id[..4] + new string('•', Math.Min(12, id.Length - 8)) + id[^4..];

        // ----- Statistics -----

        private (string Title, string Artist)? _lyricsPrefill;

        // Chart colours: the first three dark-mode steps of the validated reference palette (blue, orange, aqua).
        private static readonly Color ChartBlue = Color.FromRgb(0x39, 0x87, 0xE5);
        private static readonly Color ChartOrange = Color.FromRgb(0xD9, 0x59, 0x26);
        private static readonly Color ChartAqua = Color.FromRgb(0x19, 0x9E, 0x70);

        private void BuildStatsPage()
        {
            var now = DateTime.Now;
            bool any = _stats.TotalSeconds >= 1;

            // ---- Headline numbers ----
            var tiles = new UniformGrid { Columns = 5, Margin = new Thickness(6, 12, 6, 12) };
            void Tile(string value, string label)
            {
                var t = new StackPanel { Margin = new Thickness(10, 4, 10, 4) };
                t.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                t.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap });
                tiles.Children.Add(t);
            }
            Tile(Stats.Duration(_stats.SecondsOn(now)), "today");
            Tile(Stats.Duration(_stats.LastDaysSeconds(now, 7)), "last 7 days");
            Tile(Stats.Duration(_stats.TotalSeconds), "all time");
            Tile(_stats.SongsPlayed.ToString("N0"), _stats.SongsPlayed == 1 ? "song played" : "songs played");
            int streak = _stats.Streak(now);
            Tile(streak.ToString(), streak == 1 ? "day in a row" : "days in a row");
            var overview = Card(any ? $"Since {_stats.D.Since:d MMMM yyyy}" : "Statistics");
            overview.Children.Add(tiles);
            if (!any || !_s.TrackStatistics)
                Hint(overview, !_s.TrackStatistics
                    ? "Statistics are switched off (see the bottom of this page)."
                    : "Play some music and your statistics will start to appear here.");

            if (any)
            {
                BuildHoursChart();
                BuildCoverage();
                BuildTopLists();
            }

            // ---- Settings ----
            var settings = Card("Your statistics");
            SwitchRow(settings, "Keep statistics", "Only ever stored on this PC, in %APPDATA%\\Kairos\\statistics.json", _s.TrackStatistics, v => _s.TrackStatistics = v);
            var reset = new Button { Style = (Style)FindResource("TextButton"), Content = "Reset" };
            bool confirming = false;
            reset.Click += (_, _) =>
            {
                if (!confirming)
                {
                    confirming = true;
                    reset.Content = "Click again to erase everything";
                    reset.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
                    return;
                }
                _stats.Reset();
                Refresh();
            };
            reset.MouseLeave += (_, _) => { if (confirming) { confirming = false; reset.Content = "Reset"; reset.ClearValue(ForegroundProperty); } };
            Row(settings, "Reset statistics", "Starts again from today", reset);
        }

        /// <summary>Listening time in each hour of the day: one series, so no legend; a tooltip on every hour.</summary>
        private void BuildHoursChart()
        {
            var hours = _stats.D.Hours;
            double max = Math.Max(1, hours.Max());
            int peak = Array.IndexOf(hours, hours.Max());

            var card = Card("When you listen");
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            body.Children.Add(new TextBlock
            {
                Text = $"Most often around {peak:00}:00",
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 10),
            });

            const double plotHeight = 110;
            var bars = new Grid { Height = plotHeight };
            for (int h = 0; h < 24; h++)
            {
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                double value = hours[h];
                var bar = new Border
                {
                    Height = value <= 0 ? 0 : Math.Max(3, value / max * plotHeight),
                    MaxWidth = 24,
                    Margin = new Thickness(1, 0, 1, 0), // 2px gap between neighbours
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(4, 4, 0, 0), // rounded data end, square at the baseline
                    Background = new SolidColorBrush(ChartBlue),
                };
                // The hover target is the whole column, bigger than the bar itself.
                var hit = new Grid { Background = Brushes.Transparent, ToolTip = $"{h:00}:00–{(h + 1) % 24:00}:00  ·  {Stats.Duration(value)}" };
                hit.Children.Add(bar);
                hit.MouseEnter += (_, _) => bar.Opacity = 0.75;
                hit.MouseLeave += (_, _) => bar.Opacity = 1;
                Grid.SetColumn(hit, h);
                bars.Children.Add(hit);
            }
            body.Children.Add(bars);
            body.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush") }); // baseline

            var axis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int h = 0; h < 24; h++) axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var h in new[] { 0, 6, 12, 18 })
            {
                var label = new TextBlock { Text = $"{h:00}:00", FontSize = 11, Foreground = (Brush)FindResource("FaintBrush") };
                Grid.SetColumn(label, h);
                Grid.SetColumnSpan(label, 4);
                axis.Children.Add(label);
            }
            body.Children.Add(axis);
            card.Children.Add(body);
        }

        /// <summary>Of the songs you've played, how many had synced, plain or no lyrics: a stacked bar with a legend.</summary>
        private void BuildCoverage()
        {
            var (synced, plain, none, nolyrics) = _stats.Coverage();
            int total = synced + plain + none;
            if (total == 0) return;

            var card = Card("Lyrics for the songs you play");
            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };

            var parts = new[] { ("Time-synced", synced, ChartBlue), ("Plain", plain, ChartOrange), ("Not found", none, ChartAqua) }
                .Where(p => p.Item2 > 0).ToArray();
            var bar = new Grid { Height = 14 };
            for (int i = 0; i < parts.Length; i++)
            {
                var (name, count, colour) = parts[i];
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
                var seg = new Border
                {
                    Background = new SolidColorBrush(colour),
                    // 2px gap between segments; rounded only at the two outer ends.
                    Margin = new Thickness(i == 0 ? 0 : 1, 0, i == parts.Length - 1 ? 0 : 1, 0),
                    CornerRadius = new CornerRadius(i == 0 ? 4 : 0, i == parts.Length - 1 ? 4 : 0, i == parts.Length - 1 ? 4 : 0, i == 0 ? 4 : 0),
                    ToolTip = $"{name}: {count} of {total} songs ({count * 100.0 / total:0}%)",
                };
                Grid.SetColumn(seg, i);
                bar.Children.Add(seg);
            }
            body.Children.Add(bar);

            // Legend, with the numbers, so colour is never the only way to tell them apart.
            var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var (name, count, colour) in parts)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
                item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(colour), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = $"{name}  {count * 100.0 / total:0}%  ({count})", Foreground = (Brush)FindResource("TextBrush"), FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0) });
                legend.Children.Add(item);
            }
            body.Children.Add(legend);
            if (nolyrics > 0)
                body.Children.Add(new TextBlock { Text = $"Not counted: {nolyrics} instrumental or no-lyrics {(nolyrics == 1 ? "song" : "songs")}.", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            card.Children.Add(body);

            // The most-played songs still missing synced lyrics, with a shortcut to add them.
            var missing = _stats.MissingSynced(5);
            if (missing.Count > 0)
            {
                foreach (var m in missing)
                {
                    var add = new Button { Style = (Style)FindResource("TextButton"), Content = m.Lyrics == "plain" ? "Sync" : "Add lyrics" };
                    var song = m;
                    add.Click += (_, _) =>
                    {
                        _lyricsPrefill = (song.Title, song.Artist);
                        GoTo("lyrics");
                    };
                    Row(card, song.Title, $"{song.Artist}  ·  {song.Plays} {(song.Plays == 1 ? "play" : "plays")}  ·  {(song.Lyrics == "plain" ? "not time-synced" : "no lyrics found")}", add);
                }
            }
        }

        /// <summary>Top songs and artists, with thin bars for how often (one colour: it's one measure).</summary>
        private void BuildTopLists()
        {
            void TopList(string title, IEnumerable<(string Name, string Detail, int Plays)> rows)
            {
                var items = rows.ToList();
                if (items.Count == 0) return;
                int max = Math.Max(1, items.Max(r => r.Plays));
                var card = Card(title);
                var body = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };
                int rank = 1;
                foreach (var (name, detail, plays) in items)
                {
                    var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.Children.Add(new TextBlock { Text = rank++.ToString(), Foreground = (Brush)FindResource("FaintBrush"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });

                    var text = new StackPanel();
                    text.Children.Add(new TextBlock { Text = name, Foreground = (Brush)FindResource("TextBrush"), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (!string.IsNullOrEmpty(detail))
                        text.Children.Add(new TextBlock { Text = detail, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
                    var track = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0) };
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(plays, GridUnitType.Star) });
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - plays + 0.0001, GridUnitType.Star) });
                    track.Children.Add(new Border { Background = new SolidColorBrush(ChartBlue), CornerRadius = new CornerRadius(2) });
                    text.Children.Add(track);
                    Grid.SetColumn(text, 1);
                    grid.Children.Add(text);

                    var count = new TextBlock { Text = plays == 1 ? "1 play" : $"{plays:N0} plays", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                    Grid.SetColumn(count, 2);
                    grid.Children.Add(count);
                    body.Children.Add(grid);
                }
                card.Children.Add(body);
            }

            TopList("Top songs", _stats.TopSongs(10).Select(s => (s.Title, $"{s.Artist}  ·  {Stats.Duration(s.Seconds)}", s.Plays)));
            TopList("Top artists", _stats.TopArtists(8).Select(a => (a.Artist, Stats.Duration(a.Seconds), a.Plays)));
        }

        private void BuildShortcutsPage()
        {
            var keys = Card("Keyboard (when the lyrics window is focused)");
            KeyRow(keys, "Larger text", "Ctrl", "+");
            KeyRow(keys, "Smaller text", "Ctrl", "−");
            KeyRow(keys, "Lyrics earlier", "]");
            KeyRow(keys, "Lyrics later", "[");
            KeyRow(keys, "Play / pause", "Space");
            KeyRow(keys, "Back to current line", "Esc");
            KeyRow(keys, "Settings", "Ctrl", ",");
            KeyRow(keys, "Centre left-to-right on screen (works in any app)", "Ctrl", "Alt", "C");
            KeyRow(keys, "Mini mode on / off (works in any app)", "Ctrl", "Alt", "M");
            KeyRow(keys, "Click-through on / off (works in any app)", "Ctrl", "Alt", "T");

            var mouse = Card("Mouse");
            KeyRow(mouse, "Move the window", "Drag");
            KeyRow(mouse, "Resize", "Drag bottom-right corner");
            KeyRow(mouse, "Jump to a line", "Click");
            KeyRow(mouse, "Browse lyrics", "Scroll");
            KeyRow(mouse, "Quick options", "Right-click");
            KeyRow(mouse, "Spotify volume", "Ctrl", "Scroll");
            KeyRow(mouse, "Use Kairos while click-through is on", "Hold Ctrl");

            var about = Card("About");
            var folder = new Button { Style = (Style)FindResource("GhostButton"), Content = "Open folder" };
            folder.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(Settings.AppDataFolder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.AppDataFolder}\"") { UseShellExecute = true });
                }
                catch { }
            };
            Row(about, "Settings and saved lyrics", Settings.AppDataFolder, folder);
            Row(about, "Lyrics", "Provided by LRCLIB (lrclib.net), a free and open lyrics database", new Border());
            var history = new Button { Style = (Style)FindResource("GhostButton"), Content = "What's new" };
            history.Click += (_, _) => ShowChangelog();
            Row(about, $"Kairos {AppInfo.VersionText}", string.IsNullOrEmpty(AppInfo.RepoUrl) ? null : AppInfo.RepoUrl, history);
        }

        // =================================================================
        //  Version history
        // =================================================================

        private void ShowChangelog()
        {
            _page = "changelog";
            foreach (var rb in Nav.Children.OfType<RadioButton>()) rb.IsChecked = false;

            PageTitle.Text = "What's new";
            PageSubtitle.Text = $"You're on version {AppInfo.VersionText}";
            PageHost.Children.Clear();
            _layoutEditor = null;
            PreviewPanel.Visibility = Visibility.Visible;

            var releases = AppInfo.LoadChangelog();
            if (releases.Count == 0)
            {
                var none = Card(null);
                Row(none, "No version history available", null, new Border());
            }

            foreach (var r in releases)
            {
                bool isCurrent = r.Version == AppInfo.VersionText;

                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, PageHost.Children.Count == 0 ? 6 : 22, 0, 8) };
                head.Children.Add(new TextBlock { Text = $"Version {r.Version}", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
                if (!string.IsNullOrEmpty(r.Date))
                    head.Children.Add(new TextBlock { Text = FormatDate(r.Date), FontSize = 12, Foreground = (Brush)FindResource("FaintBrush"), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                if (isCurrent)
                    head.Children.Add(new Border
                    {
                        Background = (Brush)FindResource("AccentBrush"),
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(8, 1, 8, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "Current", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B)) },
                    });
                PageHost.Children.Add(head);

                var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
                foreach (var (heading, items) in r.Sections)
                {
                    if (!string.IsNullOrEmpty(heading))
                        body.Children.Add(new TextBlock
                        {
                            Text = heading.ToUpperInvariant(),
                            FontSize = 10.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = (Brush)FindResource(heading.Equals("Fixed", StringComparison.OrdinalIgnoreCase) ? "MutedBrush" : "AccentBrush"),
                            Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 12, 0, 6),
                        });
                    foreach (var item in items)
                    {
                        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        g.Children.Add(new TextBlock { Text = "•", Foreground = (Brush)FindResource("FaintBrush") });
                        var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextBrush"), LineHeight = 19 };
                        Grid.SetColumn(text, 1);
                        g.Children.Add(text);
                        body.Children.Add(g);
                    }
                }
                PageHost.Children.Add(new Border
                {
                    Background = (Brush)FindResource("CardBrush"),
                    BorderBrush = (Brush)FindResource(isCurrent ? "ControlBorderBrush" : "CardBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = body,
                });
            }

            if (!string.IsNullOrEmpty(AppInfo.RepoUrl))
            {
                var link = new Button { Style = (Style)FindResource("GhostButton"), Content = "View Kairos on GitHub", Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                link.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true }); } catch { }
                };
                PageHost.Children.Add(link);
            }

            PageScroller.ScrollToTop();
        }

        private static string FormatDate(string iso) =>
            DateTime.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.None, out var d)
                ? d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : iso;

        // =================================================================
        //  Building blocks
        // =================================================================

        private StackPanel Card(string? header)
        {
            if (header != null)
            {
                PageHost.Children.Add(new TextBlock
                {
                    Text = header.ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("FaintBrush"),
                    Margin = new Thickness(4, PageHost.Children.Count == 0 ? 6 : 22, 0, 8),
                });
            }
            var stack = new StackPanel();
            PageHost.Children.Add(new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = stack,
            });
            return stack;
        }

        /// <summary>A labelled row inside a card, with the control on the right.</summary>
        private Border Row(StackPanel card, string label, string? hint, UIElement control)
        {
            if (card.Children.Count > 0)
                card.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush"), Margin = new Thickness(16, 0, 16, 0) });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            labels.Children.Add(new TextBlock { Text = label, FontSize = 13.5, Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(hint))
                labels.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(labels);

            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            var row = new Border { Padding = new Thickness(16, 12, 16, 12), Background = Brushes.Transparent, Child = grid };
            card.Children.Add(row);
            return row;
        }

        private void SwitchRow(StackPanel card, string label, string? hint, bool value, Action<bool> set)
        {
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value };
            sw.Checked += (_, _) => { set(true); Changed(); };
            sw.Unchecked += (_, _) => { set(false); Changed(); };

            var row = Row(card, label, hint, sw);
            // The whole row toggles, not just the little switch.
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (!IsInside<CheckBox>(e.OriginalSource)) sw.IsChecked = sw.IsChecked != true;
            };
        }

        private Slider SliderRow(StackPanel card, string label, string? hint, double min, double max, double step,
                                 double value, Action<double> set, Func<double, string> format)
        {
            var slider = new Slider
            {
                Style = (Style)FindResource("KSlider"),
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                SmallChange = step,
                LargeChange = step * 5,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                Width = 190,
            };
            var readout = new TextBlock
            {
                Text = format(slider.Value),
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12.5,
                Width = 92,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += (_, e) =>
            {
                readout.Text = format(e.NewValue);
                set(e.NewValue);
                Changed();
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(slider);
            panel.Children.Add(readout);
            Row(card, label, hint, panel);
            return slider;
        }

        private UIElement FontPicker()
        {
            var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!fonts.Contains(_s.FontFamily, StringComparer.OrdinalIgnoreCase)) fonts.Insert(0, _s.FontFamily);

            // Each font is listed with a sample in that font, and its name in the normal UI font.
            var template = new DataTemplate(typeof(string));
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var sample = new FrameworkElementFactory(typeof(TextBlock));
            sample.SetValue(TextBlock.TextProperty, "Aa");
            sample.SetValue(TextBlock.WidthProperty, 30.0);
            sample.SetValue(TextBlock.FontSizeProperty, 14.0);
            sample.SetBinding(TextBlock.FontFamilyProperty, new Binding("."));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("."));
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(sample);
            row.AppendChild(name);
            template.VisualTree = row;

            var combo = new ComboBox
            {
                Style = (Style)FindResource("DarkCombo"),
                ItemsSource = fonts,
                ItemTemplate = template,
                Width = 250,
                FocusVisualStyle = null,
                SelectedItem = fonts.FirstOrDefault(f => string.Equals(f, _s.FontFamily, StringComparison.OrdinalIgnoreCase)),
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string f) { _s.FontFamily = f; Changed(); }
            };
            return combo;
        }

        private UIElement AlignmentPicker()
        {
            var bar = new Border
            {
                Background = (Brush)FindResource("ControlBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (value, glyph, tip) in new[] { ("Left", "", "Left"), ("Center", "", "Centre"), ("Right", "", "Right") })
            {
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Segment"),
                    Content = glyph,
                    ToolTip = tip,
                    GroupName = "align",
                    IsChecked = string.Equals(_s.TextAlignment, value, StringComparison.OrdinalIgnoreCase)
                                || (value == "Center" && !new[] { "left", "right" }.Contains(_s.TextAlignment?.ToLowerInvariant())),
                };
                rb.Checked += (_, _) => { _s.TextAlignment = value; Changed(); };
                panel.Children.Add(rb);
            }
            bar.Child = panel;
            return bar;
        }

        private void KeyRow(StackPanel card, string action, params string[] keys)
        {
            var caps = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) caps.Children.Add(new TextBlock { Text = "+", Foreground = (Brush)FindResource("FaintBrush"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                caps.Children.Add(new Border
                {
                    Background = (Brush)FindResource("ControlBrush"),
                    BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 3, 9, 3),
                    MinWidth = 28,
                    Child = new TextBlock
                    {
                        Text = keys[i],
                        FontSize = 12,
                        Foreground = (Brush)FindResource("TextBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                });
            }
            Row(card, action, null, caps);
        }

        // ----- Theme cards -----

        private UIElement ThemeCard(ThemeData t, bool deletable)
        {
            var bg = MainWindow.ParseColor(t.BackgroundColor, Colors.Black);
            bg.A = (byte)Math.Clamp(Math.Round(t.BackgroundOpacity * 255), 0, 255);
            var cur = MainWindow.ParseColor(t.CurrentLineColor, Colors.White);
            var oth = MainWindow.ParseColor(t.OtherLinesColor, Colors.White);
            oth.A = (byte)Math.Clamp(t.OtherLinesOpacity * 255, 0, 255);

            Border Bar(double width, Color c, double height = 5) => new Border
            {
                Width = width, Height = height, CornerRadius = new CornerRadius(height / 2),
                Background = new SolidColorBrush(c), Margin = new Thickness(0, 3, 0, 3),
            };

            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            lines.Children.Add(Bar(52, oth));
            lines.Children.Add(Bar(70, cur, 7));
            lines.Children.Add(Bar(46, oth));

            // The theme's background: its picture, the album art, or just the colour.
            UIElement? picture = null;
            string mode = t.BackgroundMode ?? "Colour";
            if (mode == "Image" && t.Image != null && File.Exists(t.Image.Path))
            {
                var thumb = LoadThumbnail(t.Image.Path, 240);
                if (thumb != null)
                {
                    double zoom = Math.Clamp(t.Image.Zoom, 0.25, 3);
                    var pic = new Grid { Opacity = Math.Clamp(t.Image.Opacity, 0, 1) };
                    pic.Children.Add(new Image
                    {
                        Source = thumb,
                        Stretch = t.Image.Fit?.ToLowerInvariant() switch { "fit" => Stretch.Uniform, "stretch" => Stretch.Fill, _ => Stretch.UniformToFill },
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new ScaleTransform(zoom * (t.Image.Flip ? -1 : 1), zoom),
                        Effect = t.Image.Blur > 0.5 ? new BlurEffect { Radius = t.Image.Blur * 0.25 } : null,
                    });
                    var tint = MainWindow.ParseColor(t.Image.TintColor, Colors.Black);
                    tint.A = (byte)Math.Round(Math.Clamp(t.Image.TintStrength, 0, 1) * 255);
                    pic.Children.Add(new Border { Background = new SolidColorBrush(tint) });
                    picture = pic;
                    bg.A = 0; // with a picture, the theme's own tint does the darkening
                }
            }
            else if (mode == "Dance" || (t.DanceOn == true && mode == "Colour"))
            {
                // The colours it would dance with now, side by side.
                var art = _albumArt();
                var colours = art != null ? BackgroundLayer.PaletteOf(art) : Palette.Default;
                var stripes = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                for (int i = 0; i < colours.Length; i++)
                    stripes.GradientStops.Add(new GradientStop(Color.FromRgb(colours[i].R, colours[i].G, colours[i].B), colours.Length == 1 ? 0 : (double)i / (colours.Length - 1)));
                picture = new Border { Background = stripes, Effect = new BlurEffect { Radius = 10 } };
                bg.A = (byte)Math.Round(Math.Clamp(t.DanceDim ?? _s.DanceDim, 0, 1) * 255);
            }
            else if (mode == "AlbumArt")
            {
                var art = _albumArt();
                picture = art != null
                    ? new Image { Source = art, Stretch = Stretch.UniformToFill, Effect = new BlurEffect { Radius = 6 } }
                    : new Border
                    {
                        Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x2A, 0x5C), Color.FromRgb(0x1E, 0x6B, 0x5A), 45),
                        Child = new TextBlock { Text = "\uE8D6", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 4) },
                    };
                bg.A = (byte)Math.Round(Math.Clamp(t.AlbumArtDim ?? _s.AlbumArtDim, 0, 1) * 255);
            }

            var layers = new Grid { Width = 108, Height = 70, Clip = new RectangleGeometry(new Rect(0, 0, 108, 70), 9, 9) };
            if (picture != null) layers.Children.Add(picture);
            layers.Children.Add(new Border { Background = new SolidColorBrush(bg) });
            layers.Children.Add(lines);
            var mini = new Border { Width = 108, Height = 70, CornerRadius = new CornerRadius(9), Child = layers };
            // The same stand-in "desktop" as the preview, so see-through themes read correctly.
            var desk = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3A, 0x67), Color.FromRgb(0xA0, 0x52, 0x6D), 45),
                Child = mini,
            };
            var frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(2),
                Child = desk,
            };

            var top = new Grid();
            top.Children.Add(frame);

            Button? delete = null;
            if (deletable)
            {
                delete = new Button
                {
                    Style = (Style)FindResource("CloseButton"),
                    Content = "\uE74D", // trash can
                    ToolTip = $"Delete \"{t.Name}\"",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 6, 0),
                    Visibility = Visibility.Collapsed,
                };
                // Small, round, readable on any theme colour
                delete.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10));
                delete.Click += (_, _) =>
                {
                    _s.DeleteTheme(t.Name);
                    _s.Save();
                    Refresh();
                };
                top.Children.Add(delete);
            }

            var stack = new StackPanel { Width = 116 };
            stack.Children.Add(top);
            stack.Children.Add(new TextBlock
            {
                Text = t.Name,
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var card = new Border
            {
                Child = stack,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 10),
                ToolTip = $"Apply \"{t.Name}\"",
            };
            card.MouseEnter += (_, _) =>
            {
                frame.BorderBrush = (Brush)FindResource("AccentBrush");
                if (delete != null) delete.Visibility = Visibility.Visible;
            };
            card.MouseLeave += (_, _) =>
            {
                frame.BorderBrush = Brushes.Transparent;
                if (delete != null) delete.Visibility = Visibility.Collapsed;
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (IsInside<Button>(e.OriginalSource)) return; // the delete button
                _s.ApplyTheme(t);
                Changed();
                Refresh(); // update the colour rows below
            };
            return card;
        }

        /// <summary>A small, quick-to-load copy of a picture for theme cards.</summary>
        private static BitmapImage? LoadThumbnail(string path, int width)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(path);
                img.DecodePixelWidth = width;
                img.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>The "Your themes" card: saved themes, plus saving the current look as a new one.</summary>
        private void BuildCustomThemes()
        {
            var card = Card("Your themes");
            var wrap = new WrapPanel { Margin = new Thickness(14, 14, 4, 6) };
            foreach (var t in _s.CustomThemes.ToList())
                wrap.Children.Add(ThemeCard(t, deletable: true));

            // "Save current look" tile
            var dashed = new Rectangle
            {
                RadiusX = 12, RadiusY = 12,
                Stroke = (Brush)FindResource("ControlBorderBrush"),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            var plus = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            plus.Children.Add(new TextBlock { Text = "\uE710", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, Foreground = (Brush)FindResource("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            var tileGrid = new Grid { Width = 116, Height = 78 };
            tileGrid.Children.Add(dashed);
            tileGrid.Children.Add(plus);
            var tileStack = new StackPanel { Width = 116 };
            tileStack.Children.Add(tileGrid);
            tileStack.Children.Add(new TextBlock { Text = "Save current look", FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            var tile = new Border { Child = tileStack, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 10), ToolTip = "Save the current colours as a theme" };
            tile.MouseEnter += (_, _) => dashed.Stroke = (Brush)FindResource("AccentBrush");
            tile.MouseLeave += (_, _) => dashed.Stroke = (Brush)FindResource("ControlBorderBrush");
            wrap.Children.Add(tile);
            card.Children.Add(wrap);

            // Name entry, shown after clicking the tile
            var nameBox = new TextBox { Style = (Style)FindResource("DarkText"), Width = 220, FontFamily = FontFamily, FontSize = 13 };
            var save = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Save", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(10, 0, 0, 0) };
            var cancel = new Button { Style = (Style)FindResource("TextButton"), Content = "Cancel", Margin = new Thickness(4, 0, 0, 0) };
            var note = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            var entryRow = new StackPanel { Orientation = Orientation.Horizontal };
            entryRow.Children.Add(nameBox);
            entryRow.Children.Add(save);
            entryRow.Children.Add(cancel);
            var entry = new StackPanel { Margin = new Thickness(16, 0, 16, 14), Visibility = Visibility.Collapsed };
            entry.Children.Add(new TextBlock { Text = "Name your theme", FontSize = 13.5, Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
            entry.Children.Add(entryRow);
            entry.Children.Add(note);
            card.Children.Add(entry);

            void UpdateNote()
            {
                var n = nameBox.Text.Trim();
                bool builtIn = Settings.BuiltInThemes.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase));
                bool exists = _s.CustomThemes.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
                save.IsEnabled = n.Length > 0 && !builtIn;
                save.Opacity = save.IsEnabled ? 1 : 0.4;
                note.Text = builtIn ? "That name belongs to a built-in theme; pick another."
                    : exists ? $"This will replace your \"{n}\" theme."
                    : _s.BackgroundMode == "Image"
                        ? "Saves the colours, glow, shadow and your background image with its adjustments."
                        : _s.BackgroundMode == "AlbumArt"
                            ? "Saves the colours, glow, shadow and the album art background settings."
                            : "Saves the colours, background, glow and shadow you have now.";
            }

            void DoSave()
            {
                var n = nameBox.Text.Trim();
                if (n.Length == 0 || Settings.BuiltInThemes.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))) return;
                if (n.Length > 40) n = n[..40];
                _s.SaveCurrentAsTheme(n);
                _s.Save(); // saved themes persist straight away
                Refresh();
            }

            tile.MouseLeftButtonUp += (_, _) =>
            {
                int i = 1;
                while (_s.CustomThemes.Any(c => c.Name == $"My theme {i}")) i++;
                nameBox.Text = $"My theme {i}";
                entry.Visibility = Visibility.Visible;
                UpdateNote();
                nameBox.Focus();
                nameBox.SelectAll();
            };
            nameBox.TextChanged += (_, _) => UpdateNote();
            nameBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { DoSave(); e.Handled = true; }
                else if (e.Key == Key.Escape) { entry.Visibility = Visibility.Collapsed; e.Handled = true; }
            };
            save.Click += (_, _) => DoSave();
            cancel.Click += (_, _) => entry.Visibility = Visibility.Collapsed;
        }

        // ----- Colour rows and the picker -----

        private void ColorRow(StackPanel card, string label, Func<string> get, Action<string> set)
        {
            var swatch = new Border
            {
                Width = 30, Height = 30,
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(MainWindow.ParseColor(get(), Colors.White)),
            };
            var swatchButton = new Button { Style = (Style)FindResource("BareButton"), Content = swatch, ToolTip = "Pick a colour", Margin = new Thickness(0, 0, 8, 0) };
            var hex = new TextBox { Style = (Style)FindResource("DarkText"), Text = get().ToUpperInvariant(), Width = 96 };

            bool updatingText = false;
            void Apply(Color c, bool fromText)
            {
                string value = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                set(value);
                swatch.Background = new SolidColorBrush(c);
                if (!fromText)
                {
                    updatingText = true;
                    hex.Text = value;
                    updatingText = false;
                }
                Changed();
            }

            hex.TextChanged += (_, _) =>
            {
                if (updatingText) return;
                var t = hex.Text.Trim();
                if (!t.StartsWith("#")) t = "#" + t;
                if (t.Length != 7 && t.Length != 4) return; // wait for a complete #RGB or #RRGGBB
                var c = MainWindow.ParseColor(t, Color.FromArgb(0, 0, 0, 0));
                if (c.A == 0) return;
                Apply(c, fromText: true);
            };
            swatchButton.Click += (_, _) => OpenColorPicker(swatchButton, MainWindow.ParseColor(get(), Colors.White), c => Apply(c, fromText: false));

            var dropper = EyedropperButton(c => { Apply(c, fromText: false); RememberColour(c); });
            dropper.Margin = new Thickness(6, 0, 0, 0);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatchButton);
            panel.Children.Add(hex);
            panel.Children.Add(dropper);
            Row(card, label, null, panel);
        }

        /// <summary>A small pipette button that starts the screen eyedropper.</summary>
        private Button EyedropperButton(Action<Color> onPicked)
        {
            var icon = new System.Windows.Shapes.Path
            {
                // A pipette, drawn rather than taken from an icon font.
                Data = Geometry.Parse("M 12.6,1.9 C 13.4,1.1 14.8,1.1 15.6,1.9 C 16.4,2.7 16.4,4.1 15.6,4.9 L 13.9,6.6 L 14.7,7.4 L 13.4,8.7 L 12.6,7.9 L 6.9,13.6 L 4.4,14.1 L 4.9,11.6 L 10.6,5.9 L 9.8,5.1 L 11.1,3.8 L 11.9,4.6 Z M 3.6,15.9 L 2.4,15.9 L 2.4,14.7"),
                Stroke = (Brush)FindResource("TextBrush"),
                StrokeThickness = 1.3,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 18, Height = 18,
                Stretch = Stretch.None,
            };
            var face = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("ControlBrush"),
                BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = icon,
            };
            var b = new Button { Style = (Style)FindResource("BareButton"), Content = face, ToolTip = "Pick a colour from anywhere on screen" };
            b.MouseEnter += (_, _) => face.Background = (Brush)FindResource("ControlHoverBrush");
            b.MouseLeave += (_, _) => face.Background = (Brush)FindResource("ControlBrush");
            b.Click += (_, _) => Eyedropper.Pick(c => { onPicked(c); Activate(); });
            return b;
        }

        private void RememberColour(Color c)
        {
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _s.RecentColours.RemoveAll(x => string.Equals(x, hex, StringComparison.OrdinalIgnoreCase));
            _s.RecentColours.Insert(0, hex);
            if (_s.RecentColours.Count > 7) _s.RecentColours.RemoveRange(7, _s.RecentColours.Count - 7);
        }

        private static readonly string[] PresetColours =
        {
            "#FFFFFF", "#D9D9D9", "#8C8C8C", "#121212", "#000000", "#1ED760", "#4ADE80",
            "#38BDF8", "#818CF8", "#C084FC", "#F472B6", "#FB7185", "#FFB86B", "#FDE047",
        };

        /// <summary>A small pop-out colour picker: saturation/brightness square, hue bar and quick swatches.</summary>
        private void OpenColorPicker(FrameworkElement anchor, Color start, Action<Color> onPick)
        {
            RgbToHsv(start, out double hue, out double sat, out double val);
            const double SvW = 236, SvH = 150;

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };

            // Saturation / brightness square
            var hueLayer = new Border { CornerRadius = new CornerRadius(8) };
            var whiteLayer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
            };
            var blackLayer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
            };
            var svMarker = new Ellipse
            {
                Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2,
                Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 },
                IsHitTestVisible = false,
            };
            var svCanvas = new Canvas { IsHitTestVisible = false };
            svCanvas.Children.Add(svMarker);
            var sv = new Grid { Width = SvW, Height = SvH, Cursor = Cursors.Cross, Background = Brushes.Transparent };
            sv.Children.Add(hueLayer);
            sv.Children.Add(whiteLayer);
            sv.Children.Add(blackLayer);
            sv.Children.Add(svCanvas);

            // Hue bar
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(HsvToRgb(i * 60 % 360, 1, 1), i / 6.0));
            var hueMarker = new Border
            {
                Width = 8, Height = 20, CornerRadius = new CornerRadius(4),
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            };
            var hueCanvas = new Canvas { IsHitTestVisible = false };
            hueCanvas.Children.Add(hueMarker);
            var hueBar = new Grid { Width = SvW, Height = 14, Margin = new Thickness(0, 14, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            hueBar.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = rainbow });
            hueBar.Children.Add(hueCanvas);

            // Quick swatches
            var swatches = new WrapPanel { Width = SvW + 4, Margin = new Thickness(0, 8, 0, 0) };

            void Sync(bool notify)
            {
                hueLayer.Background = new SolidColorBrush(HsvToRgb(hue, 1, 1));
                Canvas.SetLeft(svMarker, sat * SvW - 7);
                Canvas.SetTop(svMarker, (1 - val) * SvH - 7);
                Canvas.SetLeft(hueMarker, hue / 360 * SvW - 4);
                Canvas.SetTop(hueMarker, -3);
                if (notify) onPick(HsvToRgb(hue, sat, val));
            }

            void PickSv(Point p)
            {
                sat = Math.Clamp(p.X / SvW, 0, 1);
                val = 1 - Math.Clamp(p.Y / SvH, 0, 1);
                Sync(true);
            }
            void PickHue(Point p)
            {
                hue = Math.Clamp(p.X / SvW, 0, 1) * 359.9;
                Sync(true);
            }

            sv.MouseLeftButtonDown += (_, e) => { PickSv(e.GetPosition(sv)); e.Handled = true; };
            sv.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickSv(e.GetPosition(sv)); };
            hueBar.MouseLeftButtonDown += (_, e) => { PickHue(e.GetPosition(hueBar)); e.Handled = true; };
            hueBar.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickHue(e.GetPosition(hueBar)); };

            foreach (var hexColour in PresetColours)
            {
                var c = MainWindow.ParseColor(hexColour, Colors.White);
                var chip = new Border
                {
                    Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(7),
                    Background = new SolidColorBrush(c),
                    BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = hexColour,
                };
                chip.MouseLeftButtonUp += (_, _) =>
                {
                    RgbToHsv(c, out hue, out sat, out val);
                    Sync(true);
                };
                swatches.Children.Add(chip);
            }

            // Recently used colours.
            var recent = new WrapPanel { Width = SvW + 4 };
            foreach (var hexColour in _s.RecentColours)
            {
                var c = MainWindow.ParseColor(hexColour, Colors.White);
                var chip = new Border
                {
                    Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush(c),
                    BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = hexColour,
                };
                chip.MouseLeftButtonUp += (_, _) => { RgbToHsv(c, out hue, out sat, out val); Sync(true); };
                recent.Children.Add(chip);
            }

            // Eyedropper, from inside the picker too.
            var dropper = EyedropperButton(c => { RgbToHsv(c, out hue, out sat, out val); Sync(true); RememberColour(c); });
            var dropperRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            dropperRow.Children.Add(dropper);
            dropperRow.Children.Add(new TextBlock { Text = "Pick from screen", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            dropper.Click += (_, _) => popup.IsOpen = false; // get the popup out of the way while picking

            var body = new StackPanel();
            body.Children.Add(sv);
            body.Children.Add(hueBar);
            body.Children.Add(dropperRow);
            body.Children.Add(new TextBlock { Text = "SWATCHES", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("FaintBrush"), Margin = new Thickness(0, 12, 0, 0) });
            body.Children.Add(swatches);
            if (_s.RecentColours.Count > 0)
            {
                body.Children.Add(new TextBlock { Text = "RECENT", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("FaintBrush"), Margin = new Thickness(0, 4, 0, 8) });
                body.Children.Add(recent);
            }
            // Remember the colour chosen with the picker when it closes.
            popup.Closed += (_, _) =>
            {
                var chosen = HsvToRgb(hue, sat, val);
                if (chosen != start) RememberColour(chosen);
            };

            popup.Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = (Brush)FindResource("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 14, 14, 6),
                Child = body,
            };

            Sync(false);
            popup.IsOpen = true;
        }

        // =================================================================
        //  Live preview
        // =================================================================

        private LivePreview? _preview;
        private readonly List<LivePreview> _extraPreviews = new();

        private void RefreshPreview()
        {
            _preview?.Refresh();
            foreach (var p in _extraPreviews) p.Refresh();
        }

        /// <summary>The album art or profile picture changed (e.g. finished loading): show it in the previews.</summary>
        public void PicturesChanged() => RefreshPreview();

        /// <summary>A bigger live preview for the image editor, on the same stand-in desktop.</summary>
        private FrameworkElement MakeLargePreview()
        {
            const double width = 460;
            double ratio = Math.Clamp(_s.Height / Math.Max(1, _s.Width), 0.3, 1.2);
            var preview = new LivePreview(_s, _albumArt, _profilePicture) { Width = width, Height = Math.Round(width * ratio) };
            _extraPreviews.Add(preview);
            return new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3A, 0x67), Color.FromRgb(0xA0, 0x52, 0x6D), 45),
                Child = preview,
            };
        }

        // =================================================================
        //  Helpers
        // =================================================================

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

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;
            (double r, double g, double b) = h switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static void RgbToHsv(Color color, out double h, out double s, out double v)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
    }
}
