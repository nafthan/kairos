using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Kairos
{
    /// <summary>What goes on a lyric card.</summary>
    public sealed class CardContent
    {
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public BitmapSource? Art { get; init; }
        /// <summary>Your background picture, already adjusted (or null).</summary>
        public BitmapSource? Picture { get; init; }
        /// <summary>Your background GIF's frames (or null).</summary>
        public ImageProcessor.Animation? Animation { get; init; }
        public bool Dance { get; init; }
        /// <summary>
        /// When each of <see cref="Lines"/> starts and ends in the song (seconds); null if not synced. When playing
        /// through, <see cref="Lines"/> is the whole song, and the card scrolls through it like the lyrics window.
        /// </summary>
        public IReadOnlyList<(double Start, double End)>? Timing { get; init; }
        /// <summary>Play the song's lines through with their real timings, from <see cref="ClipStart"/>.</summary>
        public bool PlayThrough { get; init; }
        /// <summary>Where in the song the animation starts (seconds), when playing through.</summary>
        public double ClipStart { get; init; }
        /// <summary>Your profile picture, for the corner of the card (or null).</summary>
        public BitmapSource? Profile { get; init; }
    }

    /// <summary>
    /// A lyric card: the chosen lines in your Kairos style, over your background, the album art or a colour, with the
    /// song underneath. Laid out at a fixed size (1080 wide) and rendered at any size. <see cref="SetTime"/> moves its
    /// animations (karaoke fill, glow, colour dance, GIF background, album art drift) to a moment in a seamless loop.
    /// </summary>
    public sealed class CardView : Grid
    {
        public double CardWidth { get; }
        public double CardHeight { get; }

        private readonly Settings _s;
        private readonly CardContent _c;
        private readonly double _unit; // 1% of the shorter side

        private readonly Image? _gif;
        private readonly ScaleTransform _artZoom = new(1.12, 1.12);
        private readonly TranslateTransform _artDrift = new();
        private readonly bool _artDrifts;
        private readonly Ellipse[] _blobs = Array.Empty<Ellipse>();
        private readonly List<GradientStop[]> _karaoke = new();
        private readonly List<int> _lengths = new();
        private readonly DropShadowEffect? _glow;
        private readonly double _glowOpacity;
        private readonly ScaleTransform _pulse = new(1, 1);
        private readonly List<TextBlock> _blocks = new();
        private readonly bool _play;
        private int _playCurrent = int.MinValue;
        // Playing through: the lines scroll inside this area, keeping the one being sung in the middle.
        private readonly Grid? _area;
        private readonly StackPanel? _stack;
        private readonly TranslateTransform _scroll = new();
        private double[]? _centres;

        public static (double W, double H) SizeFor(string shape) => shape switch
        {
            "Story" => (1080, 1920),
            "Wide" => (1600, 900),
            _ => (1080, 1080),
        };

        public CardView(Settings s, CardContent c)
        {
            _s = s;
            _c = c;
            (CardWidth, CardHeight) = SizeFor(s.CardShape);
            Width = CardWidth;
            Height = CardHeight;
            ClipToBounds = true;
            _unit = Math.Min(CardWidth, CardHeight) / 100;
            UseLayoutRounding = false;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            var bgColour = MainWindow.ParseColor(s.BackgroundColor, Color.FromRgb(18, 18, 18));
            bgColour.A = 255;
            Children.Add(new Rectangle { Fill = new SolidColorBrush(bgColour) });

            // ---- Background ----
            string mode = s.CardBackground == "Mine" ? s.BackgroundMode : s.CardBackground;
            bool picture = mode == "Image" && (c.Picture != null || c.Animation != null);
            bool artBg = mode == "AlbumArt" && c.Art != null;
            bool danceFront = s.CardBackground == "Mine" && s.DanceLayer == "Front";
            // How much blur in the window becomes on the card (the card is much bigger than the window).
            double blurScale = Math.Min(CardWidth, CardHeight) / Math.Max(200, Math.Min(s.Width, s.Height));

            if (c.Dance && !(picture && danceFront)) _blobs = AddDance(c, bgColour, colourOnly: !picture && !artBg);

            if (picture)
            {
                var img = new Image
                {
                    Source = c.Animation?.Frames.FirstOrDefault() ?? c.Picture,
                    Stretch = Stretch.UniformToFill,
                    Opacity = Math.Clamp(s.ImageOpacity, 0, 1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(s.ImageFlip ? -1 : 1, 1),
                };
                if (s.ImageBlur > 0.5) img.Effect = new BlurEffect { Radius = Math.Min(200, s.ImageBlur * blurScale), KernelType = KernelType.Gaussian };
                if (c.Animation != null && c.Animation.Frames.Length > 1) _gif = img;
                Children.Add(img);
                var tint = MainWindow.ParseColor(s.ImageTintColor, Colors.Black);
                tint.A = (byte)Math.Round(Math.Clamp(s.ImageTintStrength, 0, 1) * 255);
                if (tint.A > 0) Children.Add(new Rectangle { Fill = new SolidColorBrush(tint), Opacity = Math.Clamp(s.ImageOpacity, 0, 1) });
            }
            else if (artBg)
            {
                bool mine = s.CardBackground == "Mine";
                var group = new TransformGroup();
                group.Children.Add(_artZoom);
                group.Children.Add(_artDrift);
                var img = new Image
                {
                    Source = c.Art,
                    Stretch = Stretch.UniformToFill,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = group,
                    Effect = new BlurEffect { Radius = Math.Min(200, (mine ? s.AlbumArtBlur : 40) * blurScale), KernelType = KernelType.Gaussian },
                };
                _artDrifts = !mine || s.AnimateAlbumArt;
                Children.Add(img);
                var dim = bgColour;
                dim.A = (byte)Math.Round(Math.Clamp(mine ? s.AlbumArtDim : 0.5, 0, 0.95) * 255);
                Children.Add(new Rectangle { Fill = new SolidColorBrush(dim) });
            }

            if (c.Dance && picture && danceFront) _blobs = AddDance(c, bgColour, colourOnly: false);

            // ---- Lyrics and the song ----
            var current = MainWindow.ParseColor(s.CurrentLineColor, Colors.White);
            var other = MainWindow.ParseColor(s.OtherLinesColor, Colors.White);
            other.A = (byte)Math.Round(Math.Clamp(s.OtherLinesOpacity, 0.15, 1) * 255);
            var font = new FontFamily((string.IsNullOrWhiteSpace(s.FontFamily) ? "Segoe UI" : s.FontFamily) + ", Segoe UI");
            _play = c.PlayThrough && c.Timing != null && c.Timing.Count == c.Lines.Count && c.Lines.Count > 0;
            var align = s.TextAlignment?.ToLowerInvariant() switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, _ => TextAlignment.Center };

            double margin = _unit * 8;
            var content = new Grid { Margin = new Thickness(margin) };
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(content);

            double textWidth = CardWidth - margin * 2;
            var lines = new StackPanel { Width = textWidth, RenderTransform = _pulse, RenderTransformOrigin = new Point(0.5, 0.5) };
            int count = Math.Max(1, c.Lines.Count);
            double fontSize = _unit * (_play ? 6.2 : count switch { 1 => 7.4, 2 => 6.6, 3 => 6.0, 4 => 5.4, 5 or 6 => 4.8, _ => 4.3 }) * (s.CardShape == "Wide" ? 0.95 : 1);
            foreach (var text in c.Lines)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontFamily = font,
                    FontSize = fontSize,
                    FontWeight = s.CurrentLineBold ? FontWeights.Bold : FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = align,
                    LineHeight = fontSize * 1.18,
                    Margin = new Thickness(0, 0, 0, fontSize * (_play ? 0.6 : 0.42)),
                };
                if (s.CardKaraoke || _play)
                {
                    var stops = new[] { new GradientStop(current, 0), new GradientStop(current, 0), new GradientStop(other, 0.04), new GradientStop(other, 1) };
                    var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                    foreach (var stop in stops) brush.GradientStops.Add(stop);
                    tb.Foreground = brush;
                    _karaoke.Add(stops);
                }
                else tb.Foreground = new SolidColorBrush(current);
                _lengths.Add(Math.Max(4, text.Length));
                _blocks.Add(tb);
                lines.Children.Add(tb);
            }
            if (lines.Children.Count > 0 && lines.Children[^1] is TextBlock last) last.Margin = new Thickness(0);

            if (s.CurrentLineGlow)
            {
                var glowColour = s.GlowMatchLine ? current : MainWindow.ParseColor(s.GlowColor, current);
                _glowOpacity = Math.Clamp(s.GlowStrength, 0.05, 1);
                _glow = new DropShadowEffect { Color = glowColour, BlurRadius = Math.Clamp(s.GlowSize, 2, 60) * _unit * 0.22, ShadowDepth = 0, Opacity = _glowOpacity };
                // Playing through, only the line being sung glows (moved from line to line in SetTime).
                if (!_play) lines.Effect = _glow;
            }
            if ((_play || !s.CurrentLineGlow) && s.TextShadow)
                lines.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = _unit * 1.2, ShadowDepth = _unit * 0.25, Direction = 270, Opacity = 0.75 };

            if (_play)
            {
                // The song scrolls past, like the lyrics window: lines fade out at the top and bottom edges.
                lines.RenderTransform = _scroll;
                lines.VerticalAlignment = VerticalAlignment.Top;
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                fade.GradientStops.Add(new GradientStop(Colors.Black, 0.16));
                fade.GradientStops.Add(new GradientStop(Colors.Black, 0.84));
                fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                _area = new Grid { ClipToBounds = true, OpacityMask = fade };
                _area.Children.Add(lines);
                _stack = lines;
                content.Children.Add(_area);
            }
            else
            {
                // Long lyrics shrink to fit rather than running off the card.
                var fit = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = lines, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(fit);
            }

            var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, _unit * 5, 0, 0) };
            Grid.SetRow(footer, 1);
            if (s.CardShowMark)
            {
                var mark = Mark(_unit * 3.4, other);
                DockPanel.SetDock(mark, Dock.Right);
                footer.Children.Add(mark);
            }
            if (s.CardShowProfile && c.Profile != null)
            {
                double size = _unit * 9;
                var profilePicture = new Border
                {
                    Width = size, Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Background = new ImageBrush(c.Profile) { Stretch = Stretch.UniformToFill },
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(_unit * 0.3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(_unit * 2.5, 0, s.CardShowMark ? _unit * 2.5 : 0, 0),
                    Effect = new DropShadowEffect { BlurRadius = _unit * 2, ShadowDepth = _unit * 0.4, Direction = 270, Opacity = 0.45 },
                };
                DockPanel.SetDock(profilePicture, Dock.Right);
                footer.Children.Add(profilePicture);
            }
            if (s.CardShowSong && (c.Title.Length > 0 || c.Artist.Length > 0))
            {
                var song = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (c.Art != null)
                {
                    double size = _unit * 11;
                    song.Children.Add(new Border
                    {
                        Width = size, Height = size,
                        CornerRadius = new CornerRadius(_unit * 1.4),
                        Background = new ImageBrush(c.Art) { Stretch = Stretch.UniformToFill },
                        Margin = new Thickness(0, 0, _unit * 2.6, 0),
                        Effect = new DropShadowEffect { BlurRadius = _unit * 2, ShadowDepth = _unit * 0.4, Direction = 270, Opacity = 0.45 },
                    });
                }
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = textWidth * 0.62 };
                words.Children.Add(new TextBlock { Text = c.Title, FontFamily = font, FontSize = _unit * 3.6, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(current.R, current.G, current.B)), TextTrimming = TextTrimming.CharacterEllipsis });
                words.Children.Add(new TextBlock { Text = c.Artist, FontFamily = font, FontSize = _unit * 3.0, Foreground = new SolidColorBrush(other), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, _unit * 0.4, 0, 0) });
                song.Children.Add(words);
                footer.Children.Add(song);
            }
            else footer.Children.Add(new Border());
            content.Children.Add(footer);

            SetTime(0, 4);
        }

        /// <summary>The Kairos logo (drawn, so it's sharp at any size) and name, for the corner of the card.</summary>
        private UIElement Mark(double size, Color colour)
        {
            var canvas = new Canvas { Width = 100, Height = 100 };
            canvas.Children.Add(new Path { Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60)), Data = Geometry.Parse("M26,58 A24,24 0 0 1 74,58 Z") });
            var bar = new Rectangle { Width = 68, Height = 11, RadiusX = 5.5, RadiusY = 5.5, Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)) };
            Canvas.SetLeft(bar, 16); Canvas.SetTop(bar, 56.5);
            canvas.Children.Add(bar);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.8 };
            panel.Children.Add(new Viewbox { Width = size * 1.5, Height = size * 1.5, Child = canvas, Margin = new Thickness(0, 0, size * 0.25, 0) });
            panel.Children.Add(new TextBlock { Text = "Kairos", FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = size * 0.85, Foreground = new SolidColorBrush(colour), VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private Ellipse[] AddDance(CardContent c, Color bgColour, bool colourOnly)
        {
            var palette = c.Art != null ? BackgroundLayer.PaletteOf(c.Art) : Palette.Default;
            var layer = new Grid { Opacity = Math.Clamp(_s.CardBackground == "Mine" ? _s.DanceOpacity : 1, 0.05, 1) };
            var main = palette[0];
            layer.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromRgb((byte)(main.R * 0.12), (byte)(main.G * 0.12), (byte)(main.B * 0.12))) });
            var canvas = new Canvas();
            layer.Children.Add(canvas);
            var blobs = new Ellipse[5];
            for (int i = 0; i < blobs.Length; i++)
            {
                var p = palette[i % palette.Length];
                var colour = Color.FromArgb(235, p.R, p.G, p.B);
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(new GradientStop(colour, 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(colour.A * 0.45), p.R, p.G, p.B), 0.45));
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                blobs[i] = new Ellipse { Fill = brush };
                canvas.Children.Add(blobs[i]);
            }
            Children.Add(layer);
            // Darkened with the window colour so the lyrics stay readable (as in the lyrics window).
            if (colourOnly)
            {
                var dim = bgColour;
                dim.A = (byte)Math.Round(Math.Clamp(_s.DanceDim, 0, 0.9) * 255);
                Children.Add(new Rectangle { Fill = new SolidColorBrush(dim) });
            }
            return blobs;
        }

        /// <summary>
        /// Moves every animation to <paramref name="t"/> seconds into a loop of <paramref name="loop"/> seconds.
        /// Everything repeats exactly at the end of the loop, so a GIF plays round without a jump.
        /// </summary>
        public void SetTime(double t, double loop)
        {
            loop = Math.Max(0.5, loop);
            t = ((t % loop) + loop) % loop;
            double phase = t / loop;
            double turn = phase * Math.PI * 2;
            // A beat at about 120 bpm that still fits the loop exactly.
            int beats = Math.Max(1, (int)Math.Round(loop * 2));
            double beat = Math.Pow(Math.Max(0, Math.Cos(turn * beats)), 6);

            // Playing through: the line being sung at this point of the song is lit (and fills in as it's sung).
            if (_play && _c.Timing is { } timing)
            {
                double pos = _c.ClipStart + t;
                int now = -1;
                for (int i = 0; i < timing.Count; i++) if (pos >= timing[i].Start) now = i;
                for (int i = 0; i < _karaoke.Count && i < timing.Count; i++)
                {
                    var stops = _karaoke[i];
                    double p = 0;
                    if (i == now)
                    {
                        var (start, end) = timing[i];
                        p = _s.CardKaraoke ? Math.Clamp((pos - start) / Math.Max(0.4, (end - start) * 0.9), 0, 1) : 1;
                    }
                    stops[1].Offset = p;
                    stops[2].Offset = i == now ? Math.Min(1, p + 0.04) : 0;
                }
                if (now != _playCurrent)
                {
                    for (int i = 0; i < _blocks.Count; i++)
                    {
                        if (_glow != null) _blocks[i].Effect = i == now ? _glow : null;
                        // Lines further from the one being sung get fainter, as in the lyrics window.
                        int distance = Math.Abs(i - Math.Max(now, 0));
                        _blocks[i].Opacity = i == now || !_s.FadeDistantLines ? 1 : Math.Max(0.3, 1 - 0.17 * (distance - 1));
                    }
                }
                _playCurrent = now;

                // Keep the line being sung in the middle, gliding to each new line.
                if (MeasureLines() is { } centres && _area != null)
                {
                    int target = Math.Clamp(now, 0, centres.Length - 1);
                    double y = centres[target];
                    if (now > 0)
                    {
                        double k = Math.Clamp((pos - timing[now].Start) / 0.4, 0, 1);
                        k = 1 - Math.Pow(1 - k, 3); // ease out
                        y = centres[now - 1] + (centres[now] - centres[now - 1]) * k;
                    }
                    _scroll.Y = _area.ActualHeight / 2 - y;
                }
            }
            // Karaoke: the lines fill one after another over the first three quarters, then hold.
            else if (_karaoke.Count > 0)
            {
                double fill = Math.Clamp(phase / 0.75, 0, 1);
                double total = _lengths.Sum(), done = 0;
                for (int i = 0; i < _karaoke.Count; i++)
                {
                    double share = _lengths[i] / total;
                    double p = Math.Clamp((fill - done) / share, 0, 1);
                    done += share;
                    _karaoke[i][1].Offset = p;
                    _karaoke[i][2].Offset = Math.Min(1, p + 0.04);
                }
            }

            if (_glow != null) _glow.Opacity = _glowOpacity * (0.72 + 0.28 * beat);
            if (_s.BeatPulse) { double k = 1 + 0.018 * beat * Math.Clamp(_s.BeatPulseStrength, 0.3, 2); _pulse.ScaleX = _pulse.ScaleY = k; }

            // Colour dance: each colour on its own looping path, swelling on the beat.
            double w = CardWidth, h = CardHeight, size = Math.Max(w, h) * Math.Clamp(_s.DanceSize, 0.4, 1.8);
            double energy = Math.Clamp(_s.DanceEnergy, 0, 2);
            for (int i = 0; i < _blobs.Length; i++)
            {
                double p = i * 1.37;
                int a = 1 + i % 2, b = 2 + i % 3;
                double cx = w * (0.5 + 0.36 * Math.Sin(turn * a + p) + 0.08 * Math.Sin(turn * b + p * 2));
                double cy = h * (0.5 + 0.34 * Math.Cos(turn * (a == 1 ? 1 : 2) + p * 1.6) + 0.07 * Math.Sin(turn * 3 + p));
                double r = size * (0.42 + 0.1 * Math.Sin(turn + p)) * (1 + beat * energy * 0.25);
                _blobs[i].Width = _blobs[i].Height = r * 2;
                Canvas.SetLeft(_blobs[i], cx - r);
                Canvas.SetTop(_blobs[i], cy - r);
            }

            // Album art drifts slowly round in a small circle.
            if (_artDrifts)
            {
                _artDrift.X = Math.Sin(turn) * _unit * 2.2;
                _artDrift.Y = Math.Cos(turn) * _unit * 1.6;
            }

            // Your GIF background plays at its own speed.
            if (_gif != null && _c.Animation is { } anim)
            {
                double gifLength = anim.Delays.Sum(d => Math.Max(0.02, d.TotalSeconds));
                double at = gifLength > 0 ? t % gifLength : 0;
                int frame = 0;
                for (; frame < anim.Frames.Length - 1; frame++)
                {
                    at -= Math.Max(0.02, anim.Delays[frame].TotalSeconds);
                    if (at < 0) break;
                }
                _gif.Source = anim.Frames[frame];
            }
        }

        /// <summary>Where the middle of each line is in the scrolling list (worked out once, after layout).</summary>
        private double[]? MeasureLines()
        {
            if (_centres != null) return _centres;
            if (_stack == null || _area == null) return null;
            // Not on screen (being exported)? Lay it out now.
            if (VisualTreeHelper.GetParent(this) == null && !IsArrangeValid)
            {
                Measure(new Size(CardWidth, CardHeight));
                Arrange(new Rect(0, 0, CardWidth, CardHeight));
            }
            if (_area.ActualHeight <= 0) return null;
            var centres = new double[_blocks.Count];
            for (int i = 0; i < _blocks.Count; i++)
            {
                var b = _blocks[i];
                if (b.ActualHeight <= 0) return null;
                centres[i] = b.TranslatePoint(new Point(0, 0), _stack).Y + b.ActualHeight / 2;
            }
            return _centres = centres;
        }

        /// <summary>Renders the card at <paramref name="pixelWidth"/> pixels wide (the height follows its shape).</summary>
        public BitmapSource Render(int pixelWidth)
        {
            double scale = pixelWidth / CardWidth;
            int pw = Math.Max(1, (int)Math.Round(CardWidth * scale)), ph = Math.Max(1, (int)Math.Round(CardHeight * scale));
            Measure(new Size(CardWidth, CardHeight));
            Arrange(new Rect(0, 0, CardWidth, CardHeight));
            UpdateLayout();
            var bitmap = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(this);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The card's pixels as BGRA bytes (opaque, so premultiplied and straight are the same).</summary>
        public static byte[] Pixels(BitmapSource bitmap)
        {
            var px = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(px, bitmap.PixelWidth * 4, 0);
            return px;
        }
    }
}
