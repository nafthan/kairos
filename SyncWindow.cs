using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Kairos
{
    /// <summary>
    /// Turns plain lyrics into time-synced ones: play the song in Spotify and tap Space as each line starts.
    /// Mistakes are easy to fix: Backspace undoes the last tap, and clicking a line re-times from there.
    /// </summary>
    public sealed class SyncWindow : Window
    {
        private sealed class SyncLine
        {
            public string Text = "";
            public TimeSpan? Time;
            public Border Row = null!;
            public TextBlock TimeText = null!;
            public TextBlock LineText = null!;
        }

        private readonly MediaWatcher _media;
        private readonly SpotifyClient? _spotify;
        private readonly string _artist;
        private readonly string _title;
        private readonly Action<string> _onSave;
        private readonly List<SyncLine> _lines = new();
        private int _cursor;          // the next line to tap
        private bool _saved;
        private bool _confirmDiscard;
        private double _reactionSeconds = 0.15;
        private bool? _shownPlaying;

        private readonly StackPanel _list = new();
        private readonly ScrollViewer _scroller;
        private readonly TextBlock _position = new();
        private readonly TextBlock _progress = new();
        private readonly TextBlock _warning = new();
        private readonly TextBlock _hint = new();
        private readonly Button _saveButton;
        private readonly Button _playPause;
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(80) };

        private Brush B(string key) => (Brush)FindResource(key);

        public SyncWindow(MediaWatcher media, SpotifyClient? spotify, string artist, string title, IEnumerable<string> lines,
                          Action<string> onSave, IReadOnlyList<TimeSpan?>? existingTimes = null)
        {
            _media = media;
            _spotify = spotify;
            _artist = artist;
            _title = title;
            _onSave = onSave;

            Title = "Kairos – Sync lyrics";
            Width = 640;
            Height = 700;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Kairos.ico")); } catch { }

            // ---- Header ----
            var close = new Button { Style = (Style)FindResource("CloseButton"), Content = "", ToolTip = "Close", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            close.Click += (_, _) => Close();
            var headText = new StackPanel();
            headText.Children.Add(new TextBlock { Text = "Sync lyrics", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush") });
            headText.Children.Add(new TextBlock { Text = $"{title}  ·  {artist}", FontSize = 12.5, Foreground = B("MutedBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var header = new Grid { Margin = new Thickness(24, 20, 10, 6), Background = Brushes.Transparent };
            header.Children.Add(headText);
            header.Children.Add(close);
            header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            // ---- How-to ----
            _hint.Text = "Play the song in Spotify and press Space (or Tap) the moment each highlighted line starts. " +
                         "Backspace undoes a tap. Click any line to re-time from there.";
            _hint.TextWrapping = TextWrapping.Wrap;
            _hint.Foreground = B("MutedBrush");
            _hint.FontSize = 12.5;
            _hint.LineHeight = 19;
            _hint.Margin = new Thickness(24, 4, 24, 8);

            _warning.TextWrapping = TextWrapping.Wrap;
            _warning.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6B));
            _warning.FontSize = 12.5;
            _warning.Margin = new Thickness(24, 0, 24, 8);
            _warning.Visibility = Visibility.Collapsed;

            // ---- Transport ----
            var restart = IconGhost("\uE892", "Start of song", "Jump back to the beginning");
            restart.Click += async (_, _) => await SeekAsync(TimeSpan.Zero);
            var back = IconGhost("\uE746", "5 s", "Back 5 seconds");
            back.Click += async (_, _) => await SeekAsync(_media.GetPosition() - TimeSpan.FromSeconds(5));
            _playPause = IconGhost("\uE768", "Play", "Play / pause");
            _playPause.Click += async (_, _) => await _media.TogglePlayPauseAsync();



            _position.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            _position.FontSize = 14;
            _position.Foreground = B("TextBrush");
            _position.VerticalAlignment = VerticalAlignment.Center;
            _position.Margin = new Thickness(12, 0, 0, 0);

            var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 4, 24, 10) };
            transport.Children.Add(restart);
            transport.Children.Add(back);
            transport.Children.Add(_playPause);
            transport.Children.Add(_position);

            // ---- Lines ----
            foreach (var text in lines) AddLine(text);
            if (existingTimes != null)
                for (int i = 0; i < _lines.Count && i < existingTimes.Count; i++) _lines[i].Time = existingTimes[i];
            _cursor = _lines.FindIndex(l => l.Time == null);
            if (_cursor < 0) _cursor = _lines.Count;

            _scroller = new ScrollViewer
            {
                Content = _list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16, 0, 12, 0),
            };
            var listCard = new Border
            {
                Background = B("CardBrush"),
                BorderBrush = B("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(24, 0, 24, 0),
                Padding = new Thickness(0, 8, 0, 8),
                Child = _scroller,
            };

            // ---- Bottom bar ----
            var tap = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Tap  (Space)", Padding = new Thickness(28, 10, 28, 10), FontSize = 14 };
            tap.Click += (_, _) => Tap();
            var undo = new Button { Style = (Style)FindResource("TextButton"), Content = "Undo  (Backspace)" };
            undo.Click += (_, _) => Undo();

            var earlier = Ghost("All 0.1 s earlier", "Move every timing a little earlier");
            earlier.Click += (_, _) => ShiftAll(-0.1);
            var later = Ghost("All 0.1 s later", "Move every timing a little later");
            later.Click += (_, _) => ShiftAll(+0.1);

            _saveButton = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Save", Margin = new Thickness(8, 0, 0, 0) };
            _saveButton.Click += (_, _) => Save();

            _progress.Foreground = B("MutedBrush");
            _progress.FontSize = 12.5;
            _progress.VerticalAlignment = VerticalAlignment.Center;

            var tapRow = new DockPanel { Margin = new Thickness(24, 14, 24, 6), LastChildFill = false };
            DockPanel.SetDock(tap, Dock.Left);
            DockPanel.SetDock(undo, Dock.Left);
            undo.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(_progress, Dock.Right);
            tapRow.Children.Add(tap);
            tapRow.Children.Add(undo);
            tapRow.Children.Add(_progress);

            var reaction = new Slider { Style = (Style)FindResource("KSlider"), Minimum = 0, Maximum = 0.5, Value = _reactionSeconds, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 120 };
            var reactionLabel = new TextBlock { Foreground = B("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Width = 50 };
            reactionLabel.Text = $"{_reactionSeconds:0.00} s";
            reaction.ValueChanged += (_, e) => { _reactionSeconds = e.NewValue; reactionLabel.Text = $"{e.NewValue:0.00} s"; };
            var reactionPanel = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = "Taps tend to land a moment late; this much is taken off each one." };
            reactionPanel.Children.Add(new TextBlock { Text = "Reaction time", Foreground = B("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            reactionPanel.Children.Add(reaction);
            reactionPanel.Children.Add(reactionLabel);

            var bottom = new DockPanel { Margin = new Thickness(24, 6, 24, 18), LastChildFill = false };
            DockPanel.SetDock(reactionPanel, Dock.Left);
            DockPanel.SetDock(_saveButton, Dock.Right);
            DockPanel.SetDock(later, Dock.Right);
            DockPanel.SetDock(earlier, Dock.Right);
            later.Margin = new Thickness(6, 0, 0, 0);
            bottom.Children.Add(reactionPanel);
            bottom.Children.Add(_saveButton);
            bottom.Children.Add(later);
            bottom.Children.Add(earlier);

            // ---- Layout ----
            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_hint, Dock.Top);
            DockPanel.SetDock(_warning, Dock.Top);
            DockPanel.SetDock(transport, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            DockPanel.SetDock(tapRow, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(_hint);
            root.Children.Add(_warning);
            root.Children.Add(transport);
            root.Children.Add(bottom);
            root.Children.Add(tapRow);
            root.Children.Add(listCard);

            var shadow = new Border { CornerRadius = new CornerRadius(14), Background = B("BgBrush"), Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.55 } };
            var frame = new Border { CornerRadius = new CornerRadius(14), Background = B("BgBrush"), BorderBrush = B("CardBorderBrush"), BorderThickness = new Thickness(1), Child = root };
            var outer = new Grid { Margin = new Thickness(16) };
            outer.Children.Add(shadow);
            outer.Children.Add(frame);
            Content = outer;

            PreviewKeyDown += OnKey;
            _tick.Tick += (_, _) => OnTick();
            Loaded += (_, _) => { _tick.Start(); Refresh(); Focus(); };
            Closing += (_, e) =>
            {
                if (!_saved && _lines.Any(l => l.Time != null) && !_confirmDiscard)
                {
                    e.Cancel = true;
                    _confirmDiscard = true;
                    ShowWarning("You have timings that aren't saved. Close again to discard them, or press Save.");
                }
            };
            Closed += (_, _) => _tick.Stop();
        }

        // ----- Lines -----

        private void AddLine(string text)
        {
            var line = new SyncLine { Text = text };
            line.TimeText = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Width = 78, VerticalAlignment = VerticalAlignment.Center };
            line.LineText = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(line.TimeText);
            Grid.SetColumn(line.LineText, 1);
            grid.Children.Add(line.LineText);
            line.Row = new Border
            {
                Child = grid,
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Re-time from this line",
            };
            int index = _lines.Count;
            line.Row.MouseLeftButtonUp += async (_, _) => await RetimeFrom(index);
            _lines.Add(line);
            _list.Children.Add(line.Row);
        }

        private void Tap()
        {
            if (_cursor >= _lines.Count) return;
            var t = _media.GetPosition() - TimeSpan.FromSeconds(_reactionSeconds);
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;

            // Keep the order sensible even after a hasty double tap.
            if (_cursor > 0 && _lines[_cursor - 1].Time is TimeSpan prev && t <= prev)
                t = prev + TimeSpan.FromMilliseconds(100);

            _lines[_cursor].Time = t;
            _cursor++;
            _saved = false;
            _confirmDiscard = false;
            Refresh(scrollToCursor: true);
        }

        private void Undo()
        {
            if (_cursor <= 0) return;
            _cursor--;
            _lines[_cursor].Time = null;
            _saved = false;
            Refresh(scrollToCursor: true);
        }

        /// <summary>Makes this line the next to tap and jumps the song to just before it, to hear the lead-in.</summary>
        private async Task RetimeFrom(int index)
        {
            _cursor = index;
            Refresh(scrollToCursor: false);

            TimeSpan? anchor = _lines[index].Time ?? (index > 0 ? _lines[index - 1].Time : null);
            if (anchor is TimeSpan a) await SeekAsync(a - TimeSpan.FromSeconds(3));
        }

        private void ShiftAll(double seconds)
        {
            foreach (var l in _lines)
                if (l.Time is TimeSpan t)
                {
                    var moved = t + TimeSpan.FromSeconds(seconds);
                    l.Time = moved < TimeSpan.Zero ? TimeSpan.Zero : moved;
                }
            _saved = false;
            Refresh(scrollToCursor: false);
        }

        private void Save()
        {
            if (_lines.Count == 0) return;
            int missing = _lines.Count(l => l.Time == null);
            if (missing > 0)
            {
                ShowWarning(missing == 1 ? "One line still needs a timing." : $"{missing} lines still need timings.");
                return;
            }
            var lrc = LocalLyrics.BuildLrc(_artist, _title, _lines.Select(l => (l.Time!.Value, l.Text)));
            _saved = true;
            _onSave(lrc);
            Close();
        }

        // ----- Display -----

        private void Refresh(bool scrollToCursor = false)
        {
            var pos = _media.GetPosition();
            int playing = -1;
            for (int i = 0; i < _lines.Count; i++)
                if (_lines[i].Time is TimeSpan t && t <= pos) playing = i;

            for (int i = 0; i < _lines.Count; i++)
            {
                var l = _lines[i];
                bool next = i == _cursor;
                l.TimeText.Text = l.Time is TimeSpan t ? LocalLyrics.FormatTag(t) : "--:--.--";
                l.TimeText.Foreground = l.Time == null ? B("FaintBrush") : B("MutedBrush");
                l.Row.Background = next ? new SolidColorBrush(Color.FromArgb(0x26, 0x1E, 0xD7, 0x60)) : Brushes.Transparent;
                l.Row.BorderBrush = next ? B("AccentBrush") : Brushes.Transparent;
                l.LineText.Foreground = next || i == playing ? B("TextBrush") : B("MutedBrush");
                l.LineText.FontWeight = i == playing ? FontWeights.SemiBold : FontWeights.Normal;
            }

            int done = _lines.Count(l => l.Time != null);
            _progress.Text = done == _lines.Count
                ? "All lines timed. Save, or click a line to adjust it."
                : $"{done} of {_lines.Count} lines timed";
            _saveButton.Opacity = done == _lines.Count && _lines.Count > 0 ? 1 : 0.5;

            if (scrollToCursor && _cursor < _lines.Count)
            {
                var row = _lines[_cursor].Row;
                row.UpdateLayout();
                try
                {
                    var y = row.TransformToAncestor(_list).Transform(new Point(0, 0)).Y;
                    _scroller.ScrollToVerticalOffset(Math.Max(0, y - _scroller.ViewportHeight / 2 + row.ActualHeight / 2));
                }
                catch (InvalidOperationException) { }
            }
        }

        private void OnTick()
        {
            var pos = _media.GetPosition();
            _position.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:00}.{pos.Milliseconds / 100}";
            bool playingNow = _media.IsPlaying;
            if (_shownPlaying != playingNow)
            {
                _shownPlaying = playingNow;
                _playPause.Content = IconLabel(playingNow ? "\uE769" : "\uE768", playingNow ? "Pause" : "Play");
            }

            // Warn if Spotify is playing something other than the song being synced.
            var current = _media.CurrentTrack;
            bool wrongSong = current != null &&
                             !LocalLyrics.Normalize(current.Title).Contains(LocalLyrics.Normalize(LyricsService.CleanTitle(_title))) &&
                             !LocalLyrics.Normalize(_title).Contains(LocalLyrics.Normalize(LyricsService.CleanTitle(current.Title)));
            if (wrongSong && !_confirmDiscard)
                ShowWarning($"Spotify is playing \"{current!.Title}\", not \"{_title}\". Play the right song before tapping.");
            else if (!_confirmDiscard)
                _warning.Visibility = Visibility.Collapsed;

            Refresh();
        }

        private void ShowWarning(string text)
        {
            _warning.Text = text;
            _warning.Visibility = Visibility.Visible;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Space:
                case Key.Enter:
                    Tap(); e.Handled = true; break;
                case Key.Back:
                    Undo(); e.Handled = true; break;
                case Key.Escape:
                    Close(); e.Handled = true; break;
            }
        }

        private async Task SeekAsync(TimeSpan to)
        {
            if (to < TimeSpan.Zero) to = TimeSpan.Zero;
            if (_media.CanSeek && await _media.SeekAsync(to)) return;
            if (_spotify?.IsConnected == true)
            {
                try { await _spotify.SeekAsync(to); return; }
                catch (SpotifyException) { }
            }
            if (to == TimeSpan.Zero) { await _media.PreviousAsync(); return; } // "previous" restarts the current song
            ShowWarning("Spotify isn't accepting jumps from Kairos right now. Use Spotify's own seek bar instead.");
        }

        /// <summary>Icon and words as separate pieces, each in its own font (an icon font can't draw letters).</summary>
        private StackPanel IconLabel(string glyph, string text)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 7, 0),
            });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private Button IconGhost(string glyph, string text, string tip)
        {
            var b = Ghost(text, tip);
            b.Content = IconLabel(glyph, text);
            return b;
        }

        private Button Ghost(string text, string tip) => new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = text,
            ToolTip = tip,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(12, 6, 12, 6),
        };
    }
}
