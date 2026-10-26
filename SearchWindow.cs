using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Kairos
{
    /// <summary>Search Spotify for songs and playlists: play, queue, like, or add to a playlist.</summary>
    public sealed class SearchWindow : Window
    {
        private readonly SpotifyClient _spotify;
        private readonly TextBox _query;
        private readonly StackPanel _results = new();
        private readonly ScrollViewer _scroller;
        private readonly TextBlock _message = new();
        private readonly Border _toast;
        private readonly TextBlock _toastText = new();
        private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
        private CancellationTokenSource? _searchCts;
        private string _filter = "All";
        private string _lastQuery = "";

        // Paging
        private readonly List<SearchTrack> _tracks = new();
        private readonly List<PlaylistInfo> _playlists = new();
        private bool _moreTracks, _morePlaylists;

        private Brush B(string key) => (Brush)FindResource(key);

        public SearchWindow(SpotifyClient spotify)
        {
            _spotify = spotify;

            Title = "Kairos – Search";
            Width = 680;
            Height = 720;
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
            var header = new Grid { Margin = new Thickness(24, 20, 10, 10), Background = Brushes.Transparent };
            header.Children.Add(new TextBlock { Text = "Search Spotify", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush") });
            header.Children.Add(close);
            header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            // ---- Search box ----
            _query = new TextBox { Style = (Style)FindResource("DarkText"), FontFamily = FontFamily, FontSize = 14, Height = 38, Padding = new Thickness(32, 0, 8, 0) };
            var glass = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false };
            var placeholder = new TextBlock { Text = "Songs, artists or playlists", Foreground = B("FaintBrush"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 0), IsHitTestVisible = false };
            var box = new Grid { Margin = new Thickness(24, 0, 24, 10) };
            box.Children.Add(_query);
            box.Children.Add(glass);
            box.Children.Add(placeholder);
            _query.TextChanged += (_, _) =>
            {
                placeholder.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _typingTimer.Stop();
                _typingTimer.Start(); // search shortly after you stop typing
            };
            _query.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter) { _typingTimer.Stop(); await RunSearchAsync(fresh: true); e.Handled = true; }
            };
            _typingTimer.Tick += async (_, _) => { _typingTimer.Stop(); await RunSearchAsync(fresh: true); };

            // ---- Filter ----
            var filters = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var f in new[] { "All", "Songs", "Playlists" })
            {
                var rb = new RadioButton { Style = (Style)FindResource("TextSegment"), Content = f, GroupName = "searchFilter", IsChecked = f == _filter };
                var value = f;
                rb.Checked += async (_, _) => { _filter = value; await RunSearchAsync(fresh: true); };
                filters.Children.Add(rb);
            }
            var filterBar = new Border
            {
                Background = B("ControlBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(24, 0, 24, 10),
                Child = filters,
            };

            // ---- Results ----
            _message.Foreground = B("MutedBrush");
            _message.FontSize = 13;
            _message.TextWrapping = TextWrapping.Wrap;
            _message.TextAlignment = TextAlignment.Center;
            _message.HorizontalAlignment = HorizontalAlignment.Center;
            _message.Margin = new Thickness(30, 40, 30, 0);
            _message.Text = "Type to search. Double-click a song to play it.";

            _scroller = new ScrollViewer
            {
                Content = _results,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16, 0, 12, 14),
            };
            _results.Children.Add(_message);

            // ---- Toast ----
            _toastText.Foreground = B("TextBrush");
            _toastText.TextWrapping = TextWrapping.Wrap;
            _toastText.MaxWidth = 420;
            _toast = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 18),
                Padding = new Thickness(14, 8, 14, 8),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x24, 0x24, 0x24)),
                BorderBrush = B("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                Opacity = 0,
                IsHitTestVisible = false,
                Child = _toastText,
            };
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))); };

            // ---- Layout ----
            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(box, Dock.Top);
            DockPanel.SetDock(filterBar, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(box);
            root.Children.Add(filterBar);
            var resultsArea = new Grid();
            resultsArea.Children.Add(_scroller);
            resultsArea.Children.Add(_toast);
            root.Children.Add(resultsArea);

            var shadow = new Border { CornerRadius = new CornerRadius(14), Background = B("BgBrush"), Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.55 } };
            var frame = new Border { CornerRadius = new CornerRadius(14), Background = B("BgBrush"), BorderBrush = B("CardBorderBrush"), BorderThickness = new Thickness(1), Child = root };
            var outer = new Grid { Margin = new Thickness(16) };
            outer.Children.Add(shadow);
            outer.Children.Add(frame);
            Content = outer;

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (_, _) => _query.Focus();
            Closed += (_, _) => { _searchCts?.Cancel(); _typingTimer.Stop(); };
        }

        /// <summary>Shows the window, optionally with a search already filled in.</summary>
        public void Open(string? query = null)
        {
            if (!string.IsNullOrWhiteSpace(query)) _query.Text = query;
            Show();
            Activate();
            _query.Focus();
            _query.SelectAll();
        }

        // ----- Searching -----

        private async Task RunSearchAsync(bool fresh, bool moreTracks = false, bool morePlaylists = false)
        {
            var q = _query.Text.Trim();
            if (q.Length == 0)
            {
                _tracks.Clear(); _playlists.Clear();
                ShowMessage("Type to search. Double-click a song to play it.");
                return;
            }
            if (fresh && q == _lastQuery && _results.Children.Count > 1 && !moreTracks && !morePlaylists && _filterShown == _filter) return;

            _searchCts?.Cancel();
            var cts = _searchCts = new CancellationTokenSource();
            bool wantTracks = _filter != "Playlists" && (fresh || moreTracks);
            bool wantPlaylists = _filter != "Songs" && (fresh || morePlaylists);

            if (fresh)
            {
                _tracks.Clear(); _playlists.Clear();
                ShowMessage("Searching…");
            }

            try
            {
                var r = await _spotify.SearchAsync(q, _tracks.Count, _playlists.Count, wantTracks, wantPlaylists, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (wantTracks) { _tracks.AddRange(r.Tracks); _moreTracks = r.MoreTracks; }
                if (wantPlaylists) { _playlists.AddRange(r.Playlists); _morePlaylists = r.MorePlaylists; }
                _lastQuery = q;
                _filterShown = _filter;

                double keep = _scroller.VerticalOffset;
                Render();
                if (!fresh) _scroller.ScrollToVerticalOffset(keep);
            }
            catch (OperationCanceledException) { }
            catch (SpotifyException ex)
            {
                if (!cts.IsCancellationRequested) ShowMessage(ex.Message);
            }
        }

        private string _filterShown = "";

        private void ShowMessage(string text)
        {
            _results.Children.Clear();
            _message.Text = text;
            _results.Children.Add(_message);
        }

        private void Render()
        {
            _results.Children.Clear();
            bool showTracks = _filter != "Playlists", showPlaylists = _filter != "Songs";

            if ((!showTracks || _tracks.Count == 0) && (!showPlaylists || _playlists.Count == 0))
            {
                ShowMessage("Nothing found. Try different words.");
                return;
            }

            if (showTracks && _tracks.Count > 0)
            {
                _results.Children.Add(SectionHeading("Songs"));
                foreach (var t in _tracks) _results.Children.Add(TrackRow(t));
                if (_moreTracks) _results.Children.Add(MoreButton("More songs", () => RunSearchAsync(fresh: false, moreTracks: true)));
            }
            if (showPlaylists && _playlists.Count > 0)
            {
                _results.Children.Add(SectionHeading("Playlists"));
                foreach (var p in _playlists) _results.Children.Add(PlaylistRow(p));
                if (_morePlaylists) _results.Children.Add(MoreButton("More playlists", () => RunSearchAsync(fresh: false, morePlaylists: true)));
            }
        }

        // ----- Rows -----

        private UIElement SectionHeading(string text) => new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("FaintBrush"),
            Margin = new Thickness(10, _results.Children.Count == 0 ? 4 : 18, 0, 6),
        };

        private UIElement MoreButton(string text, Func<Task> load)
        {
            var b = new Button { Style = (Style)FindResource("TextButton"), Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 4, 0, 0) };
            b.Click += async (_, _) => { b.IsEnabled = false; b.Content = "Loading…"; await load(); };
            return b;
        }

        private UIElement TrackRow(SearchTrack t)
        {
            var play = Action("", "Play");
            play.Click += async (_, _) => await Do(() => _spotify.PlayTrackAsync(t), $"Playing \"{t.Title}\"");
            var queue = Action("\uECC8", "Add to queue");
            queue.Click += async (_, _) => await Do(() => _spotify.QueueAsync(t.Uri), $"Added \"{t.Title}\" to your queue");
            var like = Action("", "Save to Liked Songs");
            like.Click += async (_, _) => await Do(() => _spotify.SaveToLibraryAsync(t.Uri), $"Saved \"{t.Title}\" to Liked Songs");
            var add = Action("", "Add to playlist");
            add.Click += async (_, _) => await ShowPlaylistMenu(add, t);

            var row = Row(MainWindow.CoverTile(t.ImageUrl, liked: false), t.Title,
                          string.IsNullOrEmpty(t.Album) ? t.Artists : $"{t.Artists}  ·  {t.Album}", t.Duration,
                          play, queue, like, add);
            row.MouseLeftButtonDown += async (_, e) =>
            {
                if (e.ClickCount == 2) await Do(() => _spotify.PlayTrackAsync(t), $"Playing \"{t.Title}\"");
            };
            return row;
        }

        private UIElement PlaylistRow(PlaylistInfo p)
        {
            var play = Action("", "Play");
            play.Click += async (_, _) => await Do(() => _spotify.PlayAsync(p.Uri, null), $"Playing {p.Name}");
            var save = Action("", "Save to your library");
            save.Click += async (_, _) => await Do(() => _spotify.SaveToLibraryAsync(p.Uri), $"Saved {p.Name} to your library");

            var row = Row(MainWindow.CoverTile(p.ImageUrl, liked: false), p.Name, p.Subtitle, "", play, save);
            row.MouseLeftButtonDown += async (_, e) =>
            {
                if (e.ClickCount == 2) await Do(() => _spotify.PlayAsync(p.Uri, null), $"Playing {p.Name}");
            };
            return row;
        }

        /// <summary>A result row: cover, title and detail, with action buttons that appear on hover.</summary>
        private Border Row(UIElement cover, string title, string detail, string right, params Button[] actions)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            text.Children.Add(new TextBlock { Text = title, Foreground = B("TextBrush"), FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = detail, Foreground = B("MutedBrush"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            foreach (var a in actions) buttons.Children.Add(a);
            var duration = new TextBlock { Text = right, Foreground = B("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(cover);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            Grid.SetColumn(duration, 3);
            grid.Children.Add(duration);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 2),
            };
            row.MouseEnter += (_, _) => { row.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)); buttons.Opacity = 1; };
            row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; buttons.Opacity = 0; };
            return row;
        }

        private Button Action(string glyph, string tip)
        {
            var b = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = glyph,
                ToolTip = tip,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Width = 30,
                Height = 30,
            };
            return b;
        }

        /// <summary>A menu of your playlists (Liked Songs first) to add a song to.</summary>
        private async Task ShowPlaylistMenu(FrameworkElement anchor, SearchTrack t)
        {
            List<PlaylistInfo> mine;
            try { mine = (await _spotify.GetPlaylistsAsync()).Where(p => p.Editable).ToList(); }
            catch (SpotifyException ex) { ShowToast(ex.Message); return; }
            if (mine.Count == 0) { ShowToast("You don't have any playlists you can add to yet."); return; }

            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.Items.Add(new MenuItem { Header = "Add to playlist", IsEnabled = false });
            menu.Items.Add(new Separator());
            foreach (var p in mine)
            {
                var item = new MenuItem { Header = p.Name };
                var target = p;
                item.Click += async (_, _) => await Do(() => _spotify.AddToPlaylistAsync(target.Id, t.Uri),
                    target.IsLikedSongs ? $"Saved \"{t.Title}\" to Liked Songs" : $"Added \"{t.Title}\" to {target.Name}");
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private async Task Do(Func<Task> action, string success)
        {
            try
            {
                await action();
                ShowToast(success);
            }
            catch (SpotifyException ex) { ShowToast(ex.Message); }
        }

        private void ShowToast(string text)
        {
            _toastText.Text = text;
            _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }
}
