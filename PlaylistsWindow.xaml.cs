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

        public PlaylistsWindow(SpotifyClient spotify, string? accountName)
        {
            _spotify = spotify;
            InitializeComponent();

            AccountText.Text = string.IsNullOrEmpty(accountName) ? "" : $"Signed in as {accountName}";
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
                    "Spotify only shows apps the songs in playlists you made or collaborate on. You can still play it.");
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
            try
            {
                await _spotify.PlayAsync(_selected.Uri, t.Uri);
                ShowToast($"Playing \"{t.Title}\"");
            }
            catch (SpotifyException ex) { ShowToast(ex.Message); }
        }

        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            try
            {
                await _spotify.PlayAsync(_selected.Uri, null);
                ShowToast($"Playing \"{_selected.Name}\"");
            }
            catch (SpotifyException ex) { ShowToast(ex.Message); }
        }

        private async void AddCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            AddCurrentButton.IsEnabled = false;
            try
            {
                var current = await _spotify.GetCurrentTrackAsync();
                if (current == null) { ShowToast("Nothing is playing in Spotify (or it's a podcast or ad)."); return; }
                if (_tracks.Any(t => t.Uri == current.Value.Uri))
                {
                    ShowToast($"\"{current.Value.Name}\" is already in {_selected.Name}.");
                    return;
                }
                await _spotify.AddToPlaylistAsync(_selected.Id, current.Value.Uri);
                ShowToast($"Added \"{current.Value.Name}\" to {_selected.Name}.");
                await LoadTracksAsync(_selected);
            }
            catch (SpotifyException ex) { ShowToast(ex.Message); }
            finally { AddCurrentButton.IsEnabled = true; }
        }

        // ----- Bits of UI -----

        private void ShowHeader(PlaylistInfo? p)
        {
            HeaderName.Text = p?.Name ?? "Playlists";
            HeaderSub.Text = p == null ? "Choose one on the left" : p.Subtitle + (p.Editable ? "" : "  ·  followed");
            PlayButton.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
            AddCurrentButton.Visibility = p?.Editable == true ? Visibility.Visible : Visibility.Collapsed;

            HeaderCover.Background = (Brush)FindResource("ControlBrush");
            if (p?.ImageUrl != null)
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
