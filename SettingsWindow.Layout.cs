using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kairos
{
    /// <summary>Settings → Layout: a big live preview where you drag the window's parts around, and their options.</summary>
    public partial class SettingsWindow
    {
        private LayoutEditor? _layoutEditor;

        private void BuildLayoutPage()
        {
            var layout = _s.Layout;

            // ---- The editor: a live preview with draggable outlines ----
            var preview = new LivePreview(_s, _albumArt, _profilePicture)
            {
                ClickToPause = false,
                ToolTip = null,
                Cursor = null,
                Height = 340,
            };
            _extraPreviews.Add(preview);
            _layoutEditor = new LayoutEditor(_s, preview,
                liveUpdate: _apply,
                done: Changed,
                modeChanged: () => Dispatcher.BeginInvoke(new Action(Refresh)));

            PageHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 6, 0, 0),
                ClipToBounds = false,
                // the same stand-in desktop as the small preview
                Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3A, 0x67), Color.FromRgb(0xA0, 0x52, 0x6D), 45),
                Child = preview,
            });
            PageHost.Children.Add(new TextBlock
            {
                Text = "Drag a part to move it, or drag its edges and corners to resize. Edges and centres snap to the window " +
                       "and to each other (hold Alt to place freely). Arrow keys nudge the selected part; Ctrl + arrows resize it. " +
                       "Positions are kept in proportion when the window changes size.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(4, 10, 4, 0),
                LineHeight = 18,
            });

            // ---- Standard or custom, and ready-made arrangements ----
            var arrange = Card("Arrangement");
            Row(arrange, "Layout", layout.Custom ? "Your own arrangement" : "Title on top, lyrics below. Drag anything above to start your own.",
                Segmented(new[] { ("Standard", "Standard"), ("Custom", "Custom") }, layout.Custom ? "Custom" : "Standard", v =>
                {
                    if (v == "Custom" && !_s.Layout.HasCustom)
                    {
                        // Start from exactly what the standard layout looks like now.
                        var measured = preview.MeasureLayout();
                        measured.HasCustom = true;
                        _s.Layout = measured;
                    }
                    _s.Layout.Custom = v == "Custom";
                    Changed();
                    Dispatcher.BeginInvoke(new Action(Refresh));
                }));

            var presets = new WrapPanel();
            foreach (var (key, name) in LayoutData.Presets)
            {
                var b = new Button { Style = (Style)FindResource("GhostButton"), Content = name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5, 10, 5) };
                b.Click += (_, _) => { _s.Layout.ApplyPreset(key); Changed(); Dispatcher.BeginInvoke(new Action(Refresh)); };
                presets.Children.Add(b);
            }
            var presetRow = new StackPanel { Margin = new Thickness(16, 4, 16, 10) };
            presetRow.Children.Add(new TextBlock { Text = "Start from", FontSize = 13.5, Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
            presetRow.Children.Add(presets);
            arrange.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush"), Margin = new Thickness(16, 0, 16, 10) });
            arrange.Children.Add(presetRow);

            // ---- Window size (layouts come into their own at different shapes) ----
            var size = Card("Window size");
            var sizes = new WrapPanel();
            foreach (var (name, w, h) in new[] { ("Compact", 380.0, 260.0), ("Tall", 360.0, 560.0), ("Wide", 760.0, 240.0), ("Large", 720.0, 480.0) })
            {
                var b = new Button { Style = (Style)FindResource("GhostButton"), Content = name, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 5, 10, 5) };
                b.Click += (_, _) => { ResizeLyricsWindow(w, h); Dispatcher.BeginInvoke(new Action(Refresh)); };
                sizes.Children.Add(b);
            }
            Row(size, "Quick sizes", null, sizes);
            SliderRow(size, "Width", null, 200, 1600, 10, _s.Width, v => ResizeLyricsWindow(v, _s.Height), v => $"{v:0} px");
            SliderRow(size, "Height", null, 110, 1200, 10, _s.Height, v => ResizeLyricsWindow(_s.Width, v), v => $"{v:0} px");

            // ---- The window's own shape ----
            var shape = Card("Window shape");
            SliderRow(shape, "Corner roundness", null, 0, 30, 1, _s.CornerRadius, v => _s.CornerRadius = v, v => $"{v:0} px");
            SliderRow(shape, "Space around the edge", "Between the window's edge and everything in it", 0, 40, 1, _s.Padding, v => _s.Padding = v, v => $"{v:0} px");

            // ---- Title, cover ----
            var parts = Card("Song and cover");
            SwitchRow(parts, "Song title and artist", null, _s.ShowTrackInfo, v => { _s.ShowTrackInfo = v; Dispatcher.BeginInvoke(new Action(() => _layoutEditor?.Redraw())); });
            SliderRow(parts, "Title size", null, 0.6, 3, 0.05, layout.TitleScale, v => _s.Layout.TitleScale = v, v => $"{v * 100:0}%");
            if (layout.Custom)
                Row(parts, "Title alignment", null, Segmented(new[] { ("Left", "Left"), ("Center", "Centre"), ("Right", "Right") },
                    layout.TitleAlign, v => { _s.Layout.TitleAlign = v; Changed(); }));
            SwitchRow(parts, "Album cover", null, _s.ShowAlbumArt, v => { _s.ShowAlbumArt = v; Dispatcher.BeginInvoke(new Action(() => _layoutEditor?.Redraw())); });
            SwitchRow(parts, "Click the cover to open the album", "In Spotify. Without the Spotify connection, it searches Spotify for the album.",
                _s.AlbumLink, v => _s.AlbumLink = v);
            if (layout.Custom)
                SwitchRow(parts, "Keep the cover square", "Off: the cover fills its box, cropped to fit", layout.SquareArt, v => _s.Layout.SquareArt = v);

            // ---- Profile picture ----
            var profile = Card("Profile picture");
            string profileHint = _s.ProfilePicture switch
            {
                "Spotify" when !_spotify.IsConnected => "Connect Spotify first (settings → Spotify), or choose a picture of your own.",
                "Spotify" => "Your Spotify account's picture. If you haven't set one on Spotify, there's nothing to show.",
                "Custom" => "Any picture; it's shown as a circle.",
                _ => "Show your Spotify picture, or one of your own, like the album cover. Place it here in Layout.",
            };
            Row(profile, "Show", profileHint, Segmented(new[] { ("Off", "Off"), ("Spotify", "Spotify"), ("Custom", "Your picture") }, _s.ProfilePicture, v =>
            {
                _s.ProfilePicture = v;
                if (v == "Custom" && string.IsNullOrEmpty(_s.ProfilePicturePath)) ChooseProfilePicture();
                Changed();
                Dispatcher.BeginInvoke(new Action(Refresh));
            }));
            if (_s.ProfilePicture == "Spotify" && !_spotify.IsConnected)
            {
                var go = new Button { Style = (Style)FindResource("GhostButton"), Content = "Connect Spotify…" };
                go.Click += (_, _) => GoTo("spotify");
                Row(profile, "Spotify", null, go);
            }
            if (_s.ProfilePicture == "Custom")
            {
                var choose = new Button { Style = (Style)FindResource("GhostButton"), Content = string.IsNullOrEmpty(_s.ProfilePicturePath) ? "Choose…" : "Change…" };
                choose.Click += (_, _) => { if (ChooseProfilePicture()) { Changed(); Dispatcher.BeginInvoke(new Action(Refresh)); } };
                Row(profile, "Picture", string.IsNullOrEmpty(_s.ProfilePicturePath) ? "None chosen yet" : null, choose);
            }

            if (_s.ProfilePicture != "Off")
            {
                Row(profile, "When clicked", _s.ProfileLink switch
                    {
                        "Spotify" => "Opens your profile in Spotify (needs the Spotify connection)",
                        "Custom" => "Opens your link, e.g. a playlist or your socials",
                        _ => null,
                    },
                    Segmented(new[] { ("Off", "Nothing"), ("Spotify", "Spotify profile"), ("Custom", "A link") }, _s.ProfileLink, v =>
                    {
                        _s.ProfileLink = v;
                        Changed();
                        Dispatcher.BeginInvoke(new Action(Refresh));
                    }));
                if (_s.ProfileLink == "Custom")
                {
                    var link = new TextBox { Style = (Style)FindResource("DarkText"), Text = _s.ProfileLinkUrl, Width = 280, FontFamily = FontFamily, FontSize = 13 };
                    link.TextChanged += (_, _) => { _s.ProfileLinkUrl = link.Text.Trim(); Changed(); };
                    Row(profile, "Link", "A web address (https://…) or a Spotify link", link);
                }
            }

            // ---- A panel and border behind the lyrics ----
            var panel = Card("Lyrics area");
            SwitchRow(panel, "Panel behind the lyrics", "A box, with an optional border, that sets the lyrics apart",
                layout.LyricsPanel, v => { _s.Layout.LyricsPanel = v; Dispatcher.BeginInvoke(new Action(Refresh)); });
            if (layout.LyricsPanel)
            {
                ColorRow(panel, "Panel colour", () => _s.Layout.LyricsPanelColor, v => _s.Layout.LyricsPanelColor = v);
                SliderRow(panel, "Panel opacity", null, 0, 1, 0.05, layout.LyricsPanelOpacity, v => _s.Layout.LyricsPanelOpacity = v, v => $"{v * 100:0}%");
                ColorRow(panel, "Border colour", () => _s.Layout.LyricsBorderColor, v => _s.Layout.LyricsBorderColor = v);
                SliderRow(panel, "Border opacity", null, 0, 1, 0.05, layout.LyricsBorderOpacity, v => _s.Layout.LyricsBorderOpacity = v, v => $"{v * 100:0}%");
                SliderRow(panel, "Border width", null, 0, 6, 0.5, layout.LyricsBorderWidth, v => _s.Layout.LyricsBorderWidth = v, v => v < 0.25 ? "None" : $"{v:0.#} px");
                SliderRow(panel, "Panel corners", null, 0, 40, 1, layout.LyricsPanelCorner, v => _s.Layout.LyricsPanelCorner = v, v => $"{v:0} px");
                SliderRow(panel, "Space inside", null, 0, 40, 1, layout.LyricsPanelPadding, v => _s.Layout.LyricsPanelPadding = v, v => $"{v:0} px");
            }
        }

        /// <summary>Deletes copies of profile pictures that are no longer used (never your originals).</summary>
        private void CleanUpProfilePictures()
        {
            try
            {
                if (!Directory.Exists(Settings.AppDataFolder)) return;
                foreach (var f in Directory.GetFiles(Settings.AppDataFolder, "profile-*"))
                    if (!string.Equals(f, _s.ProfilePicturePath, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        /// <summary>Resizes the lyrics window from settings (and the previews with it).</summary>
        private void ResizeLyricsWindow(double width, double height)
        {
            _s.Width = Math.Clamp(width, 200, 4000);
            _s.Height = Math.Clamp(height, 110, 3000);
            _resizeWindow?.Invoke(_s.Width, _s.Height);
            RefreshPreview();
        }

        /// <summary>Picks a profile picture and keeps a copy in Kairos's folder. Returns false if cancelled.</summary>
        private bool ChooseProfilePicture()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a profile picture",
                Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
            };
            if (dlg.ShowDialog(this) != true) return false;
            try
            {
                Directory.CreateDirectory(Settings.AppDataFolder);
                string copy = System.IO.Path.Combine(Settings.AppDataFolder, "profile-" + Guid.NewGuid().ToString("N")[..8] + System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant());
                File.Copy(dlg.FileName, copy, overwrite: true);

                // (Older copies are tidied away when settings close, so Undo can still bring one back.)
                _s.ProfilePicturePath = copy;
                _s.ProfilePicture = "Custom";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't use that picture: " + ex.Message, "Kairos", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
    }
}
