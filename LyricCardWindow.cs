using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kairos
{
    /// <summary>A lyric line for a card: its words, and when it starts and ends in the song (NaN if not synced).</summary>
    public readonly record struct CardLine(string Text, double Start, double End);

    /// <summary>
    /// Makes a lyric card: pick a line (or a few), a shape and a background, then copy it, save it as a PNG, save it
    /// as an animated GIF (a loop, or the lines played through as sung), or save it as an MP4 video with the song.
    /// </summary>
    public sealed class LyricCardWindow : Window
    {
        private const double FrameSeconds = 0.07; // ≈14 frames a second: smooth enough, and keeps GIFs a sensible size
        private const int MaxLines = 8;
        private const double MaxGifSeconds = 20, MaxVideoSeconds = 30;
        private const int VideoFps = 30;

        private readonly Settings _s;
        private readonly TrackInfo _track;
        private readonly IReadOnlyList<CardLine> _lines;
        private readonly bool _synced;
        private readonly BitmapSource? _art;
        private readonly BitmapSource? _profile;
        private readonly MediaWatcher _media;
        private readonly TextBlock _folderText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _lengthInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 2, 0, 6) };
        private readonly Button _cancel;
        private System.Threading.CancellationTokenSource? _cts;
        private BitmapSource? _picture;
        private ImageProcessor.Animation? _animation;
        private bool _dance;
        private int _from, _to; // chosen lines (inclusive)
        private int _anchor;

        private readonly Border _previewHost = new();
        private CardView? _preview;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly StackPanel _lineList = new();
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) };
        private readonly List<Button> _exportButtons = new();
        private bool _busy;

        private Brush B(string key) => (Brush)FindResource(key);

        public LyricCardWindow(Settings settings, TrackInfo track, IReadOnlyList<CardLine> lines, int startIndex, BitmapSource? art,
                               BitmapSource? profile, MediaWatcher media)
        {
            _s = settings;
            _track = track;
            _lines = lines;
            _synced = lines.Count > 0 && lines.All(l => !double.IsNaN(l.Start) && !double.IsNaN(l.End));
            _art = art;
            _profile = profile;
            _media = media;
            _dance = settings.DanceOn;
            _from = _to = _anchor = Math.Clamp(startIndex, 0, Math.Max(0, lines.Count - 1));

            Title = "Kairos – Lyric card";
            Width = 1000;
            Height = 760;
            MinWidth = 760;
            MinHeight = 520;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            try { Icon = new BitmapImage(new Uri("pack://application:,,,/Kairos.ico")); } catch { }

            // ---- Header ----
            var close = new Button { Style = (Style)FindResource("CloseButton"), Content = "", ToolTip = "Close", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            close.Click += (_, _) => Close();
            var headText = new StackPanel();
            headText.Children.Add(new TextBlock { Text = "Lyric card", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush") });
            headText.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Title}  ·  {track.Artist}", FontSize = 12.5, Foreground = B("MutedBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var header = new Grid { Margin = new Thickness(24, 20, 10, 10), Background = Brushes.Transparent };
            header.Children.Add(headText);
            header.Children.Add(close);
            header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            // ---- Preview (left) ----
            _previewHost.Margin = new Thickness(24, 0, 12, 24);
            _previewHost.Background = B("SidebarBrush");
            _previewHost.CornerRadius = new CornerRadius(10);
            _previewHost.Padding = new Thickness(18);

            // ---- Options (right) ----
            var options = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };

            Section(options, "Lines", $"Click a line to use it. Shift-click to add the lines around it (up to {MaxLines}).");
            var lineScroll = new ScrollViewer { Content = _lineList, MaxHeight = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            options.Children.Add(new Border { Background = B("CardBrush"), BorderBrush = B("CardBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = lineScroll });
            BuildLineList();

            Section(options, "Shape", null);
            options.Children.Add(Segmented(new[] { ("Square", "Square"), ("Story", "Tall"), ("Wide", "Wide") }, _s.CardShape, v => { _s.CardShape = v; Changed(); }));

            Section(options, "Background", null);
            options.Children.Add(Segmented(new[] { ("Mine", "My background"), ("AlbumArt", "Album art"), ("Colour", "Colour") }, _s.CardBackground, v => { _s.CardBackground = v; Changed(); }));
            options.Children.Add(Switch("Colour dance", "Colours from the album art, moving in the GIF", _dance, v => { _dance = v; Changed(); }));
            options.Children.Add(Switch("Song and artist", null, _s.CardShowSong, v => { _s.CardShowSong = v; Changed(); }));
            options.Children.Add(Switch("Kairos logo", null, _s.CardShowMark, v => { _s.CardShowMark = v; Changed(); }));
            options.Children.Add(_profile != null
                ? Switch("Your profile picture", null, _s.CardShowProfile, v => { _s.CardShowProfile = v; Changed(); })
                : Switch("Your profile picture", "Turn one on in settings → Layout first", false, _ => { }, enabled: false));
            options.Children.Add(Switch("Karaoke fill", "The lines fill in as they're sung (in the GIF and video)", _s.CardKaraoke, v => { _s.CardKaraoke = v; Changed(); }));

            Section(options, "Animation", "For the GIF and the preview. Videos always play the lines as sung.");
            if (_synced)
                options.Children.Add(Segmented(new[] { ("Loop", "Short loop"), ("Play", "Play the lines") }, _s.CardPlayThrough ? "Play" : "Loop",
                    v => { _s.CardPlayThrough = v == "Play"; Changed(); }));
            else
                options.Children.Add(new TextBlock { Text = "These lyrics aren't time-synced, so the card loops (playing the lines through needs their timings).", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = B("MutedBrush") });
            _lengthInfo.Foreground = B("MutedBrush");
            options.Children.Add(_lengthInfo);
            if (_synced)
            {
                var clipLabel = new TextBlock { Foreground = B("MutedBrush"), Width = 40, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
                var clip = new Slider { Style = (Style)FindResource("KSlider"), Minimum = 5, Maximum = MaxVideoSeconds, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(_s.CardClipSeconds, 5, MaxVideoSeconds), VerticalAlignment = VerticalAlignment.Center };
                clipLabel.Text = $"{clip.Value:0} s";
                clip.ValueChanged += (_, e) => { _s.CardClipSeconds = e.NewValue; clipLabel.Text = $"{e.NewValue:0} s"; Rebuild(); };
                clip.PreviewMouseUp += (_, _) => _s.Save();
                var clipRow = new DockPanel { Margin = new Thickness(0, 2, 0, 6), ToolTip = "How long a played-through GIF or video runs (at least long enough for the lines you chose)" };
                var clipName = new TextBlock { Text = "Clip", Foreground = B("TextBrush"), Width = 60, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(clipName, Dock.Left);
                DockPanel.SetDock(clipLabel, Dock.Right);
                clipRow.Children.Add(clipName);
                clipRow.Children.Add(clipLabel);
                clipRow.Children.Add(clip);
                options.Children.Add(clipRow);
            }
            var lengthLabel = new TextBlock { Foreground = B("MutedBrush"), Width = 40, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            var length = new Slider { Style = (Style)FindResource("KSlider"), Minimum = 2, Maximum = 10, TickFrequency = 0.5, IsSnapToTickEnabled = true, Value = Math.Clamp(_s.CardSeconds, 2, 10), VerticalAlignment = VerticalAlignment.Center };
            lengthLabel.Text = $"{length.Value:0.#} s";
            length.ValueChanged += (_, e) => { _s.CardSeconds = e.NewValue; lengthLabel.Text = $"{e.NewValue:0.#} s"; };
            length.PreviewMouseUp += (_, _) => _s.Save();
            var lengthRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            var lengthName = new TextBlock { Text = "Length", Foreground = B("TextBrush"), Width = 60, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(lengthName, Dock.Left);
            DockPanel.SetDock(lengthLabel, Dock.Right);
            lengthRow.Children.Add(lengthName);
            lengthRow.Children.Add(lengthLabel);
            lengthRow.Children.Add(length);
            options.Children.Add(lengthRow);
            _lengthRow = lengthRow;
            var sizeRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var sizeName = new TextBlock { Text = "GIF size", Foreground = B("TextBrush"), Width = 60, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sizeName, Dock.Left);
            sizeRow.Children.Add(sizeName);
            sizeRow.Children.Add(Segmented(new[] { ("360", "Small"), ("480", "Medium"), ("720", "Large") }, _s.CardGifSize.ToString(), v => { _s.CardGifSize = int.Parse(v); _s.Save(); }));
            options.Children.Add(sizeRow);

            Section(options, "Video (MP4)", _synced
                ? $"Plays the song from your first chosen line, for the clip length above, at {VideoFps} frames a second. With sound on, " +
                  "Spotify plays the lines while Kairos records them, so keep the song playing and leave the volume where it is."
                : "Videos play the lines as sung, so they need time-synced lyrics.");
            if (_synced)
            {
                var qualityRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                var qualityName = new TextBlock { Text = "Quality", Foreground = B("TextBrush"), Width = 60, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(qualityName, Dock.Left);
                qualityRow.Children.Add(qualityName);
                qualityRow.Children.Add(Segmented(new[] { ("720", "720p"), ("1080", "1080p") }, _s.CardVideoSize.ToString(), v => { _s.CardVideoSize = int.Parse(v); _s.Save(); }));
                options.Children.Add(qualityRow);
                options.Children.Add(Switch("Include the song's sound", "Recorded from Spotify as it plays", _s.CardVideoAudio, v => { _s.CardVideoAudio = v; _s.Save(); }));
            }

            var optionScroll = new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) };

            // ---- Export (bottom right) ----
            var copy = ExportButton("Copy image", "Copy the card as a picture, to paste anywhere", primary: true, () => CopyImage());
            var png = ExportButton("Save PNG…", "Save the card as a full-size picture", primary: false, () => SavePng());
            var gif = ExportButton("Save GIF…", "Save it animated", primary: false, () => _ = SaveGifAsync());
            var copyGif = ExportButton("Copy GIF", "Copy the animated GIF file, to paste into a chat or folder", primary: false, () => _ = CopyGifAsync());
            var mp4 = ExportButton("Save MP4…", "Save a video of the lines played through, with the song", primary: false, () => _ = SaveVideoAsync(copy: false));
            var copyMp4 = ExportButton("Copy MP4", "Copy the video file, to paste into a chat or folder", primary: false, () => _ = SaveVideoAsync(copy: true));
            mp4.IsEnabled = copyMp4.IsEnabled = _synced;
            if (!_synced) { _exportButtons.Remove(mp4); _exportButtons.Remove(copyMp4); }
            var row1 = new UniformGrid2(copy, png);
            var row2 = new UniformGrid2(gif, copyGif);
            var row3 = new UniformGrid2(mp4, copyMp4);
            var export = new StackPanel { Margin = new Thickness(0, 12, 14, 0) };
            export.Children.Add(row1);
            export.Children.Add(row2);
            export.Children.Add(row3);
            row2.Margin = new Thickness(0, 8, 0, 0);
            row3.Margin = new Thickness(0, 8, 0, 0);
            var folderRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
            var openFolder = new Button { Style = (Style)FindResource("TextButton"), Content = "Open folder", ToolTip = "Open the folder lyric cards are saved to" };
            openFolder.Click += (_, _) => OpenCardFolder();
            var changeFolder = new Button { Style = (Style)FindResource("TextButton"), Content = "Change…", ToolTip = "Choose where lyric cards are saved" };
            changeFolder.Click += (_, _) => ChangeCardFolder();
            DockPanel.SetDock(changeFolder, Dock.Right);
            DockPanel.SetDock(openFolder, Dock.Right);
            folderRow.Children.Add(changeFolder);
            folderRow.Children.Add(openFolder);
            _folderText.Foreground = B("MutedBrush");
            folderRow.Children.Add(_folderText);
            export.Children.Add(folderRow);
            ShowFolder();
            _status.Foreground = B("MutedBrush");
            export.Children.Add(_status);
            _cancel = new Button { Style = (Style)FindResource("TextButton"), Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, Margin = new Thickness(-4, 4, 0, 0) };
            _cancel.Click += (_, _) => _cts?.Cancel();
            export.Children.Add(_cancel);

            var right = new DockPanel { Margin = new Thickness(12, 0, 10, 24), Width = 330 };
            DockPanel.SetDock(export, Dock.Bottom);
            right.Children.Add(export);
            right.Children.Add(optionScroll);

            var body = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            body.Children.Add(right);
            body.Children.Add(_previewHost);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(body);

            var shadow = new Border { CornerRadius = new CornerRadius(14), Background = B("BgBrush"), Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.55 } };
            var frame = new Border { CornerRadius = new CornerRadius(14), Background = B("BgBrush"), BorderBrush = B("CardBorderBrush"), BorderThickness = new Thickness(1), Child = root };
            var outer = new Grid { Margin = new Thickness(16) };
            outer.Children.Add(shadow);
            outer.Children.Add(frame);
            Content = outer;

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopyImage(); e.Handled = true; }
            };
            Loaded += async (_, _) =>
            {
                Rebuild();
                CompositionTarget.Rendering += Animate;
                await LoadPictureAsync();
            };
            Closed += (_, _) => { CompositionTarget.Rendering -= Animate; _cts?.Cancel(); };
        }

        // ----- Preview -----

        private DockPanel? _lengthRow;

        private List<CardLine> Chosen() => _lines.Skip(_from).Take(_to - _from + 1).ToList();

        /// <param name="playThrough">Play the lines as sung (null: as chosen for the GIF).</param>
        /// <param name="maxSeconds">The longest the played-through clip may be.</param>
        private CardContent MakeContent(bool? playThrough = null)
        {
            var chosen = Chosen();
            bool play = (playThrough ?? _s.CardPlayThrough) && _synced;
            // Playing through, the card has the whole song and scrolls from just before the first chosen line;
            // otherwise it shows just the chosen lines.
            var lines = play ? _lines : chosen;
            var timing = _synced ? lines.Select(l => (l.Start, l.End)).ToList() : null;
            double clipStart = play ? Math.Max(0, chosen[0].Start - 0.6) : 0;
            return new CardContent
            {
                Lines = lines.Select(l => l.Text).ToList(),
                Title = _track.Title,
                Artist = _track.Artist,
                Art = _art,
                Picture = _picture,
                Animation = _animation,
                Dance = _dance,
                Timing = timing,
                PlayThrough = play,
                ClipStart = clipStart,
                Profile = _s.CardShowProfile ? _profile : null,
            };
        }

        /// <summary>
        /// Played through: the clip length you chose, but always long enough for every chosen line, and stopping at the
        /// end of the song. Otherwise the loop length.
        /// </summary>
        private double ClipSeconds(double maxSeconds)
        {
            var chosen = Chosen();
            double start = Math.Max(0, chosen[0].Start - 0.6);
            double untilLast = chosen[^1].End + 0.5 - start;
            double songLeft = _lines[^1].End + 1 - start;
            return Math.Clamp(Math.Min(Math.Max(_s.CardClipSeconds, untilLast), songLeft), 1, maxSeconds);
        }

        private double LoopSeconds(CardContent content, double maxSeconds = MaxGifSeconds)
        {
            if (content.PlayThrough && content.Timing != null) return ClipSeconds(maxSeconds);
            int frames = Math.Max(10, (int)Math.Round(Math.Clamp(_s.CardSeconds, 2, 10) / FrameSeconds));
            return frames * FrameSeconds;
        }

        private CardContent? _previewContent;

        private void Changed()
        {
            _s.Save();
            Rebuild();
        }

        private void Rebuild()
        {
            _previewContent = MakeContent();
            _preview = new CardView(_s, _previewContent);
            _previewHost.Child = new Viewbox { Child = _preview, Stretch = Stretch.Uniform };
            _clock.Restart();

            // What the animation will be.
            bool play = _previewContent.PlayThrough;
            if (_lengthRow != null) _lengthRow.Visibility = play ? Visibility.Collapsed : Visibility.Visible;
            if (_synced)
            {
                double clip = ClipSeconds(MaxVideoSeconds);
                _lengthInfo.Text = $"Starts at your first chosen line and carries on through the song: {clip:0.#} s" +
                                   (clip > MaxGifSeconds ? $" (GIFs stop at {MaxGifSeconds:0} s)." : ".");
                _lengthInfo.Visibility = play ? Visibility.Visible : Visibility.Collapsed;
            }
            else _lengthInfo.Visibility = Visibility.Collapsed;
        }

        private void Animate(object? sender, EventArgs e)
        {
            if (_preview == null || _previewContent == null || !IsVisible) return;
            try { _preview.SetTime(_clock.Elapsed.TotalSeconds, LoopSeconds(_previewContent)); }
            catch { }
        }

        /// <summary>Your background picture or GIF, for "My background".</summary>
        private async Task LoadPictureAsync()
        {
            if (_s.BackgroundMode != "Image" || string.IsNullOrEmpty(_s.BackgroundImagePath)) return;
            try
            {
                if (ImageProcessor.IsGif(_s.BackgroundImagePath))
                    _animation = ImageProcessor.TryGetCachedAnimation(_s) ?? await ImageProcessor.GetAnimationAsync(_s);
                if (_animation == null)
                    _picture = ImageProcessor.TryGetCached(_s) ?? await ImageProcessor.GetAsync(_s);
                if (_animation != null || _picture != null) Rebuild();
            }
            catch { }
        }

        // ----- Choosing lines -----

        private void BuildLineList()
        {
            _lineList.Children.Clear();
            for (int i = 0; i < _lines.Count; i++)
            {
                int index = i;
                bool chosen = i >= _from && i <= _to;
                var row = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = chosen ? new SolidColorBrush(Color.FromArgb(0x38, 0x1E, 0xD7, 0x60)) : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = _lines[i].Text, TextWrapping = TextWrapping.Wrap, Foreground = chosen ? B("TextBrush") : B("MutedBrush") },
                };
                row.MouseEnter += (_, _) => { if (!(index >= _from && index <= _to)) row.Background = B("ControlBrush"); };
                row.MouseLeave += (_, _) => { if (!(index >= _from && index <= _to)) row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (_, _) => PickLine(index, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                _lineList.Children.Add(row);
            }
            // Bring the chosen line into view.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_from < _lineList.Children.Count && _lineList.Children[_from] is FrameworkElement fe) fe.BringIntoView();
            }), DispatcherPriority.Loaded);
        }

        private void PickLine(int index, bool extend)
        {
            if (extend)
            {
                _from = Math.Min(_anchor, index);
                _to = Math.Max(_anchor, index);
                // Up to MaxLines lines, keeping the end you clicked.
                if (_to - _from > MaxLines - 1)
                {
                    if (index > _anchor) _from = _to - (MaxLines - 1); else _to = _from + (MaxLines - 1);
                }
            }
            else _from = _to = _anchor = index;
            BuildLineList();
            Rebuild();
        }

        // ----- Exporting -----

        private string FileName(string extension)
        {
            string name = string.IsNullOrWhiteSpace(_track.Artist) ? _track.Title : $"{_track.Artist} - {_track.Title}";
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            name = name.Trim();
            if (name.Length > 80) name = name[..80];
            return (name.Length == 0 ? "Lyric card" : name) + extension;
        }

        private static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Kairos");

        /// <summary>Where cards are saved: your chosen folder, or Pictures\Kairos.</summary>
        private string CardFolder()
        {
            var folder = string.IsNullOrWhiteSpace(_s.CardFolder) ? DefaultFolder() : _s.CardFolder;
            try { Directory.CreateDirectory(folder); } catch { folder = DefaultFolder(); try { Directory.CreateDirectory(folder); } catch { } }
            return folder;
        }

        private void OpenCardFolder()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CardFolder()}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Say("Couldn't open the folder: " + ex.Message, error: true); }
        }

        private void ChangeCardFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where to save lyric cards", InitialDirectory = CardFolder() };
            if (dialog.ShowDialog(this) != true) return;
            _s.CardFolder = string.Equals(dialog.FolderName, DefaultFolder(), StringComparison.OrdinalIgnoreCase) ? "" : dialog.FolderName;
            _s.Save();
            ShowFolder();
        }

        /// <summary>Shows the save folder under the export buttons, in short form.</summary>
        private void ShowFolder()
        {
            string folder = CardFolder();
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string shown = folder.StartsWith(pictures, StringComparison.OrdinalIgnoreCase) ? "Pictures" + folder[pictures.Length..]
                : folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + folder[home.Length..]
                : folder;
            _folderText.Text = "Saves to " + shown;
            _folderText.ToolTip = folder;
        }

        /// <summary>A still of the card at full size, with the karaoke fill complete.</summary>
        private BitmapSource RenderStill()
        {
            // A still shows every chosen line fully (the loop, near its end, has them all filled in).
            var content = MakeContent(playThrough: false);
            var card = new CardView(_s, content);
            double loop = LoopSeconds(content);
            card.SetTime(loop * 0.85, loop);
            return card.Render((int)card.CardWidth);
        }

        private void CopyImage()
        {
            if (_busy) return;
            try
            {
                Clipboard.SetImage(RenderStill());
                Say("Copied. Paste it anywhere with Ctrl+V.");
            }
            catch (Exception ex) { Say("Couldn't copy: " + ex.Message, error: true); }
        }

        private void SavePng()
        {
            if (_busy) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save lyric card",
                FileName = FileName(".png"),
                Filter = "PNG picture|*.png",
                InitialDirectory = CardFolder(),
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(RenderStill()));
                using (var file = File.Create(dialog.FileName)) encoder.Save(file);
                Say($"Saved {Path.GetFileName(dialog.FileName)}.", path: dialog.FileName);
            }
            catch (Exception ex) { Say("Couldn't save: " + ex.Message, error: true); }
        }

        private async Task SaveGifAsync()
        {
            if (_busy) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save animated lyric card",
                FileName = FileName(".gif"),
                Filter = "Animated GIF|*.gif",
                InitialDirectory = CardFolder(),
            };
            if (dialog.ShowDialog(this) != true) return;
            var bytes = await MakeGifAsync();
            if (bytes == null) return;
            try
            {
                await File.WriteAllBytesAsync(dialog.FileName, bytes);
                Say($"Saved {Path.GetFileName(dialog.FileName)} ({FormatSize(bytes.Length)}).", path: dialog.FileName);
            }
            catch (Exception ex) { Say("Couldn't save: " + ex.Message, error: true); }
        }

        private async Task CopyGifAsync()
        {
            if (_busy) return;
            var bytes = await MakeGifAsync();
            if (bytes == null) return;
            try
            {
                // Copied as a file: chats, email and folders take it as the GIF itself (a picture copy wouldn't move).
                var folder = Path.Combine(Path.GetTempPath(), "Kairos cards");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, FileName(".gif"));
                await File.WriteAllBytesAsync(path, bytes);
                var files = new System.Collections.Specialized.StringCollection { path };
                Clipboard.SetFileDropList(files);
                Say($"GIF copied ({FormatSize(bytes.Length)}). Paste it into a chat, email or folder.");
            }
            catch (Exception ex) { Say("Couldn't copy: " + ex.Message, error: true); }
        }

        /// <summary>
        /// Draws the animation frame by frame and encodes each one as it goes (so long GIFs don't need every frame in
        /// memory). The colours are chosen first from a dozen frames spread through the animation.
        /// </summary>
        private async Task<byte[]?> MakeGifAsync()
        {
            SetBusy(true);
            var ct = _cts!.Token;
            try
            {
                var content = MakeContent();
                var card = new CardView(_s, content);
                double loop = LoopSeconds(content);
                double step = loop > 10 ? 0.1 : FrameSeconds; // long played-through GIFs: 10 frames a second
                int count = Math.Max(1, (int)Math.Round(loop / step));
                int width = (int)Math.Round(Math.Clamp(_s.CardGifSize, 240, 1080) * (_s.CardShape == "Wide" ? 1.4 : 1));

                Say("Choosing colours…");
                var samples = new List<byte[]>();
                int pw = 0, ph = 0;
                for (int k = 0; k < 12; k++)
                {
                    card.SetTime(k * loop / 12, loop);
                    var b = card.Render(width);
                    pw = b.PixelWidth; ph = b.PixelHeight;
                    samples.Add(CardView.Pixels(b));
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
                var palette = await Task.Run(() => GifEncoder.BuildPalette(samples, pw, ph), ct);
                samples.Clear();

                int delay = (int)Math.Round(step * 100);
                using var output = new MemoryStream();
                using (var writer = new GifEncoder.GifWriter(output, pw, ph, palette))
                {
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        card.SetTime(i * step, loop);
                        var pixels = CardView.Pixels(card.Render(width));
                        await Task.Run(() => writer.Add(pixels, delay), ct);
                        Say($"Making the GIF… {(i + 1) * 100 / count}%");
                    }
                }
                return output.ToArray();
            }
            catch (OperationCanceledException) { Say("Cancelled."); return null; }
            catch (Exception ex)
            {
                Say("Couldn't make the GIF: " + ex.Message, error: true);
                return null;
            }
            finally { SetBusy(false); }
        }

        // ----- Video -----


        /// <summary>Records the song (if asked), then draws the lines played through at 30 fps into an MP4.</summary>
        private async Task SaveVideoAsync(bool copy)
        {
            if (_busy || !_synced) return;
            string path;
            if (copy)
            {
                var folder = Path.Combine(Path.GetTempPath(), "Kairos cards");
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, FileName(".mp4"));
            }
            else
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save lyric video",
                    FileName = FileName(".mp4"),
                    Filter = "MP4 video|*.mp4",
                    InitialDirectory = CardFolder(),
                };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }

            SetBusy(true);
            var ct = _cts!.Token;
            Mp4Writer? video = null;
            bool done = false;
            try
            {
                var content = MakeContent(playThrough: true);
                double length = LoopSeconds(content, MaxVideoSeconds);

                short[]? sound = null;
                bool spotifyOnly = false;
                if (_s.CardVideoAudio)
                {
                    var recorded = await RecordAsync(content.ClipStart, length, ct);
                    if (recorded == null) return;
                    (sound, spotifyOnly) = recorded.Value;
                }

                var (cw, chh) = CardView.SizeFor(_s.CardShape);
                int shortSide = _s.CardVideoSize >= 1080 ? 1080 : 720;
                int width = _s.CardShape == "Wide" ? shortSide * 16 / 9 : shortSide;
                width -= width % 2;
                int height = (int)Math.Round(chh * (width / cw));

                if (File.Exists(path)) File.Delete(path);
                video = new Mp4Writer(path, width, height + height % 2, VideoFps, sound != null);
                var card = new CardView(_s, content);
                int frames = (int)Math.Round(length * VideoFps);
                int soundPerFrame = AudioRecorder.Rate / VideoFps;
                for (int i = 0; i < frames; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    card.SetTime(i / (double)VideoFps, length + 1); // no looping in a video
                    var bitmap = card.Render(width);
                    var pixels = CardView.Pixels(bitmap);
                    if (bitmap.PixelHeight != height + height % 2) pixels = FitHeight(pixels, bitmap.PixelWidth, bitmap.PixelHeight, height + height % 2);
                    video.AddFrame(pixels);
                    if (sound != null)
                    {
                        int from = i * soundPerFrame * 2;
                        int count = Math.Max(0, Math.Min(soundPerFrame * 2, sound.Length - from));
                        if (count > 0) video.AddAudio(sound.AsSpan(from, count).ToArray());
                    }
                    if (i % 3 == 0)
                    {
                        Say($"Making the video… {(i + 1) * 100 / frames}%");
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                video.Finish();
                done = true;

                long size = new FileInfo(path).Length;
                string note = sound == null ? "" : spotifyOnly ? "" : " (Windows recorded all sound, not just Spotify)";
                if (copy)
                {
                    Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                    Say($"Video copied ({FormatSize(size)}){note}. Paste it into a chat, email or folder.");
                }
                else Say($"Saved {Path.GetFileName(path)} ({FormatSize(size)}){note}.", path: path);
            }
            catch (OperationCanceledException) { Say("Cancelled."); }
            catch (Exception ex) { Say("Couldn't make the video: " + ex.Message, error: true); }
            finally
            {
                if (!done && video != null)
                {
                    // Don't leave a half-written video behind.
                    video.Dispose();
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                SetBusy(false);
            }
        }

        /// <summary>Pads or trims a frame to the video's height (only ever off by a pixel from rounding).</summary>
        private static byte[] FitHeight(byte[] pixels, int width, int height, int wanted)
        {
            var result = new byte[width * wanted * 4];
            Array.Copy(pixels, result, Math.Min(pixels.Length, result.Length));
            return result;
        }

        private static bool SameSong(TrackInfo a, TrackInfo b) =>
            string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Has Spotify play the clip (from a moment before it, so it's settled) and records it, starting the moment the
        /// song reaches the clip. Returns null (having said why) if it can't.
        /// </summary>
        private async Task<(short[] Sound, bool SpotifyOnly)?> RecordAsync(double start, double length, System.Threading.CancellationToken ct)
        {
            var playing = _media.CurrentTrack;
            if (playing == null || !SameSong(playing, _track))
            {
                Say($"Play \u201C{_track.Title}\u201D in Spotify first, so Kairos can record its sound (or turn sound off for the video).", error: true);
                return null;
            }

            AudioRecorder recorder;
            try { recorder = await AudioRecorder.StartAsync(AudioRecorder.FindSpotifyProcessId()); }
            catch (Exception ex) { Say(ex.Message, error: true); return null; }

            using (recorder)
            {
                const double preroll = 1.5;
                Say("Getting Spotify ready…");
                if (!await _media.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, start - preroll))))
                {
                    Say("Spotify didn't jump to those lines. Try again, or turn sound off for the video.", error: true);
                    return null;
                }
                if (!_media.IsPlaying) await _media.PlayAsync();

                // Wait for the song to reach the clip, then note where in the recording that is.
                long startFrame = -1;
                var giveUp = DateTime.UtcNow.AddSeconds(preroll + 10);
                while (DateTime.UtcNow < giveUp)
                {
                    ct.ThrowIfCancellationRequested();
                    double pos = _media.GetPosition().TotalSeconds;
                    if (_media.IsPlaying && pos >= start && pos < start + 2)
                    {
                        startFrame = Math.Max(0, recorder.FramesRecorded - (long)((pos - start) * AudioRecorder.Rate));
                        break;
                    }
                    await Task.Delay(5, ct);
                }
                if (startFrame < 0)
                {
                    Say("Spotify didn't start playing those lines. Try again, or turn sound off for the video.", error: true);
                    return null;
                }

                long wanted = (long)Math.Ceiling(length * AudioRecorder.Rate);
                double pausedFor = 0;
                while (recorder.FramesRecorded < startFrame + wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    if (recorder.Error != null) throw new InvalidOperationException(recorder.Error);
                    double got = (recorder.FramesRecorded - startFrame) / (double)AudioRecorder.Rate;
                    Say($"Recording the song… {Math.Max(0, got):0} of {length:0} s{(recorder.SpotifyOnly ? "" : "  (all sound is being recorded)")}");
                    await Task.Delay(100, ct);
                    pausedFor = _media.IsPlaying ? 0 : pausedFor + 0.1;
                    if (pausedFor > 1.5)
                    {
                        Say("Spotify was paused, so the recording stopped. Try again.", error: true);
                        return null;
                    }
                    if (_media.CurrentTrack is { } now && !SameSong(now, _track))
                    {
                        Say("The song changed, so the recording stopped. Try again.", error: true);
                        return null;
                    }
                }
                return (recorder.Take(startFrame, wanted), recorder.SpotifyOnly);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            if (busy) { _cts?.Dispose(); _cts = new System.Threading.CancellationTokenSource(); }
            foreach (var b in _exportButtons) b.IsEnabled = !busy;
            _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Cursor = busy ? Cursors.AppStarting : null;
        }

        private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        private void Say(string text, bool error = false, string? path = null)
        {
            _status.Inlines.Clear();
            _status.Inlines.Add(new System.Windows.Documents.Run(text));
            _status.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6B)) : B("MutedBrush");
            if (path != null)
            {
                var show = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Show in folder")) { Foreground = B("AccentBrush") };
                show.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
                };
                _status.Inlines.Add(new System.Windows.Documents.Run("  "));
                _status.Inlines.Add(show);
            }
        }

        // ----- Building blocks -----

        private void Section(StackPanel panel, string title, string? hint)
        {
            panel.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = B("FaintBrush"),
                Margin = new Thickness(2, panel.Children.Count == 0 ? 0 : 18, 0, hint == null ? 8 : 3),
            });
            if (hint != null)
                panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = B("MutedBrush"), Margin = new Thickness(2, 0, 0, 8) });
        }

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
                Background = B("ControlBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = panel,
            };
        }

        private UIElement Switch(string label, string? hint, bool value, Action<bool> set, bool enabled = true)
        {
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value, VerticalAlignment = VerticalAlignment.Center, IsEnabled = enabled };
            sw.Checked += (_, _) => set(true);
            sw.Unchecked += (_, _) => set(false);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            words.Children.Add(new TextBlock { Text = label, Foreground = B("TextBrush") });
            if (hint != null) words.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = B("MutedBrush"), TextWrapping = TextWrapping.Wrap });
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent, Cursor = enabled ? Cursors.Hand : null, Opacity = enabled ? 1 : 0.55 };
            DockPanel.SetDock(sw, Dock.Right);
            row.Children.Add(sw);
            row.Children.Add(words);
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (!enabled) return;
                if (e.OriginalSource is DependencyObject d && IsInside(d, sw)) return;
                sw.IsChecked = sw.IsChecked != true;
            };
            return row;
        }

        private static bool IsInside(DependencyObject d, DependencyObject container)
        {
            for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (x == container) return true;
            return false;
        }

        private Button ExportButton(string text, string tip, bool primary, Action click)
        {
            var b = new Button
            {
                Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"),
                Content = text,
                ToolTip = tip,
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            b.Click += (_, _) => click();
            _exportButtons.Add(b);
            return b;
        }

        /// <summary>Two buttons side by side, sharing the width.</summary>
        private sealed class UniformGrid2 : Grid
        {
            public UniformGrid2(UIElement a, UIElement b)
            {
                ColumnDefinitions.Add(new ColumnDefinition());
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                ColumnDefinitions.Add(new ColumnDefinition());
                Children.Add(a);
                SetColumn(b, 2);
                Children.Add(b);
            }
        }
    }
}
