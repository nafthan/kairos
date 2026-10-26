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
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kairos
{
    /// <summary>Browse your Spotify playlists, play them, and add the current song to them.</summary>
    public partial class PlaylistsWindow : Window
    {
        private readonly SpotifyClient _spotify;
        private List<PlaylistInfo> _playlists = new();
        private List<PlaylistTrack> _tracks = new();
        private PlaylistInfo? _selected;
        private CancellationTokenSource? _loadCts;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };

        private readonly Settings? _settings;
        private readonly Action? _noLyricsChanged;

        public PlaylistsWindow(SpotifyClient spotify, string? accountName, Settings? settings = null, Action? noLyricsChanged = null)
        {
            _spotify = spotify;
            _settings = settings;
            _noLyricsChanged = noLyricsChanged;
            InitializeComponent();

            AccountText.Text = string.IsNullOrEmpty(accountName) ? "" : $"Signed in as {accountName}";
            LikedHint.Visibility = spotify.HasLibraryAccess ? Visibility.Collapsed : Visibility.Visible;
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += async (_, _) => await LoadPlaylistsAsync(refresh: false);
            Closed += (_, _) => _loadCts?.Cancel();

            ShowHeader(null);
        }

        // ----- Playlists -----

        private async Task LoadPlaylistsAsync(bool refresh)
        {
            ShowMessage("Loading your playlists…", "");
            try
            {
                _playlists = await _spotify.GetPlaylistsAsync(refresh: refresh);
                ApplyPlaylistFilter();
                if (_playlists.Count == 0) ShowMessage("No playlists yet", "Playlists you make or follow in Spotify appear here.");
                else if (_selected == null) ShowMessage("Pick a playlist", "Double-click a song to play it in Spotify.");
                else HideMessage();
            }
            catch (SpotifyException ex)
            {
                ShowMessage("Couldn't load your playlists", ex.Message);
            }
        }

        private void ApplyPlaylistFilter()
        {
            var filter = PlaylistSearch.Text.Trim();
            var shown = string.IsNullOrEmpty(filter)
                ? _playlists
                : _playlists.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            PlaylistList.ItemsSource = shown;
            if (_selected != null)
                PlaylistList.SelectedItem = shown.FirstOrDefault(p => p.Id == _selected.Id);
        }

        private void PlaylistSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyPlaylistFilter();

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadPlaylistsAsync(refresh: true);
            if (_selected != null) await LoadTracksAsync(_selected);
        }

        private async void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlaylistList.SelectedItem is PlaylistInfo p && p.Id != _selected?.Id)
            {
                _selected = p;
                ShowHeader(p);
                await LoadTracksAsync(p);
            }
        }

        // ----- Songs -----

        private async Task LoadTracksAsync(PlaylistInfo p)
        {
            _loadCts?.Cancel();
            var cts = _loadCts = new CancellationTokenSource();

            _tracks = new();
            TrackList.ItemsSource = null;
            TrackSearch.Text = "";
            TrackSearch.Visibility = Visibility.Collapsed;

            if (!p.Editable)
            {
                // Spotify only lets apps read the songs in playlists you own or collaborate on.
                ShowMessage("Songs in this playlist can't be listed",
                    "Spotify only shows apps the songs in playlists you made or collaborate on. You can still play it. " +
                    "If you do collaborate on it, press Refresh to check again.");
                return;
            }

            ShowMessage("Loading songs…", "");
            try
            {
                var tracks = await _spotify.GetPlaylistTracksAsync(p.Id, cts.Token);
                if (cts.IsCancellationRequested) return;
                _tracks = tracks;
                TrackList.ItemsSource = _tracks;
                TrackSearch.Visibility = _tracks.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
                if (_tracks.Count == 0) ShowMessage("This playlist is empty", "");
                else HideMessage();
            }
            catch (OperationCanceledException) { }
            catch (SpotifyException ex)
            {
                if (!cts.IsCancellationRequested) ShowMessage("Couldn't load the songs", ex.Message);
            }
        }

        private void TrackSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var f = TrackSearch.Text.Trim();
            TrackList.ItemsSource = string.IsNullOrEmpty(f)
                ? _tracks
                : _tracks.Where(t => t.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                                  || t.Artists.Contains(f, StringComparison.OrdinalIgnoreCase)
                                  || t.Album.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private async void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_selected == null || TrackList.SelectedItem is not PlaylistTrack t) return;
            if (await PlaySelectedAsync(t)) ShowToast($"Playing \"{t.Title}\"");
        }

        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            if (await PlaySelectedAsync(null)) ShowToast($"Playing {_selected.Name}");
        }

        /// <summary>Plays the selected playlist, optionally from a particular song.</summary>
        private async Task<bool> PlaySelectedAsync(PlaylistTrack? from)
        {
            if (_selected == null) return false;
            try
            {
                await _spotify.PlayAsync(_selected.Uri, from?.Uri);
                return true;
            }
            catch (SpotifyException ex)
            {
                if (_selected.IsLikedSongs && _tracks.Count > 0)
                {
                    // If Spotify won't play Liked Songs as a whole, play the songs themselves from this one on.
                    try
                    {
                        int start = from == null ? 0 : Math.Max(0, _tracks.IndexOf(from));
                        await _spotify.PlayTracksAsync(_tracks.Skip(start).Select(t => t.Uri));
                        return true;
                    }
                    catch (SpotifyException ex2) { ShowToast(ex2.Message); return false; }
                }
                ShowToast(ex.Message);
                return false;
            }
        }

        private async void AddCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            AddCurrentButton.IsEnabled = false;
            try
            {
                var current = await _spotify.GetCurrentTrackAsync();
                if (current == null) { ShowToast("Nothing is playing in Spotify (or it's a podcast or ad)."); return; }
                bool already = _selected.IsLikedSongs
                    ? await _spotify.IsLikedAsync(current.Value.Uri)
                    : _tracks.Any(t => t.Uri == current.Value.Uri);
                if (already)
                {
                    ShowToast($"\"{current.Value.Name}\" is already in {_selected.Name}.");
                    return;
                }
                await _spotify.AddToPlaylistAsync(_selected.Id, current.Value.Uri);
                ShowToast(_selected.IsLikedSongs
                    ? $"Saved \"{current.Value.Name}\" to Liked Songs."
                    : $"Added \"{current.Value.Name}\" to {_selected.Name}.");
                await LoadTracksAsync(_selected);
            }
            catch (SpotifyException ex) { ShowToast(ex.Message); }
            finally { AddCurrentButton.IsEnabled = true; }
        }

        // ----- Shared playlists by link -----

        private void AddSharedToggle_Click(object sender, RoutedEventArgs e)
        {
            bool show = AddSharedPanel.Visibility != Visibility.Visible;
            AddSharedPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AddSharedStatus.Text = "";
            if (show)
            {
                // Handy: if a playlist link is already copied, paste it in.
                try
                {
                    var clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    if (SpotifyClient.ParsePlaylistId(clip) != null) SharedLinkBox.Text = clip.Trim();
                }
                catch { }
                SharedLinkBox.Focus();
                SharedLinkBox.SelectAll();
            }
        }

        private void SharedLinkBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddShared_Click(sender, e); e.Handled = true; }
        }

        private async void AddShared_Click(object sender, RoutedEventArgs e)
        {
            AddSharedStatus.Text = "Adding…";
            try
            {
                var name = await _spotify.AddSharedPlaylistAsync(SharedLinkBox.Text);
                AddSharedStatus.Text = "";
                SharedLinkBox.Text = "";
                AddSharedPanel.Visibility = Visibility.Collapsed;
                await LoadPlaylistsAsync(refresh: false);
                var added = _playlists.FirstOrDefault(p => p.Name == name && p.AddedByLink);
                if (added != null) PlaylistList.SelectedItem = added;
                ShowToast(added?.Editable == true ? $"Added \"{name}\". You can add songs to it." : $"Added \"{name}\".");
            }
            catch (SpotifyException ex) { AddSharedStatus.Text = ex.Message; }
        }

        // ----- Playlists without lyrics -----

        private void UpdateNoLyricsToggle()
        {
            bool show = _settings != null && _selected != null && !_selected.IsLikedSongs;
            NoLyricsToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            bool on = _settings!.NoLyricsPlaylists.ContainsKey(_selected!.Id);
            NoLyricsToggle.Content = on ? "\u2713  No lyrics" : "Mark as no lyrics";
            NoLyricsToggle.Foreground = on ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");
        }

        private void NoLyricsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _selected == null) return;
            if (_settings.NoLyricsPlaylists.Remove(_selected.Id))
                ShowToast($"Lyrics will show for {_selected.Name} again.");
            else
            {
                _settings.NoLyricsPlaylists[_selected.Id] = _selected.Name;
                ShowToast($"{_selected.Name} is marked as having no lyrics.");
            }
            _settings.Save();
            UpdateNoLyricsToggle();
            _noLyricsChanged?.Invoke();
        }

        private async void RemoveLink_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || !_selected.AddedByLink) return;
            _spotify.RemoveSharedPlaylist(_selected.Id);
            _selected = null;
            ShowHeader(null);
            TrackList.ItemsSource = null;
            await LoadPlaylistsAsync(refresh: false);
        }

        // ----- Bits of UI -----

        private void ShowHeader(PlaylistInfo? p)
        {
            HeaderName.Text = p?.Name ?? "Playlists";
            HeaderSub.Text = p == null ? "Choose one on the left" : p.Subtitle + (p.Editable ? "" : "  ·  followed");
            AddCurrentButton.Content = p?.IsLikedSongs == true ? "Save current song" : "Add current song";
            RemoveLinkButton.Visibility = p?.AddedByLink == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateNoLyricsToggle();
            PlayButton.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
            AddCurrentButton.Visibility = p?.Editable == true ? Visibility.Visible : Visibility.Collapsed;

            HeaderCover.Background = (Brush)FindResource("ControlBrush");
            HeaderCover.Child = null;
            if (p?.IsLikedSongs == true)
            {
                var tile = MainWindow.CoverTile(null, liked: true, size: 104);
                var heart = tile.Child;
                tile.Child = null; // an element can only have one parent: detach before moving it
                HeaderCover.Background = tile.Background;
                HeaderCover.Child = heart;
            }
            else if (p?.ImageUrl != null)
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(p.ImageUrl);
                    img.DecodePixelWidth = 208;
                    img.EndInit();
                    HeaderCover.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
        }

        private void ShowMessage(string text, string sub)
        {
            MessageText.Text = text;
            MessageSub.Text = sub;
            MessageSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
            MessagePanel.Visibility = Visibility.Visible;
        }

        private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
