using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Kairos
{
    /// <summary>
    /// Fills the lyrics area when a song has no lyrics: a pulse that moves with the music, the album art,
    /// a clock, the song's progress, or a rotating quote about time.
    /// </summary>
    public sealed class LyricsFiller : Grid
    {
        public const string Pulse = "Pulse", AlbumArt = "AlbumArt", Clock = "Clock", Progress = "Progress", Quotes = "Quotes", Message = "Message";

        public static readonly (string Key, string Label)[] Modes =
        {
            (Pulse, "Pulse (moves with the music)"),
            (AlbumArt, "Album art"),
            (Clock, "Clock"),
            (Progress, "Song progress"),
            (Quotes, "Quotes about time"),
            (Message, "Just a message"),
        };

        private static readonly (string Text, string Who)[] QuoteList =
        {
            ("No one steps in the same river twice.", "Heraclitus"),
            ("While we wait for life, life passes.", "Seneca"),
            ("Confine yourself to the present.", "Marcus Aurelius"),
            ("Nature does not hurry, yet everything is accomplished.", "Lao Tzu"),
            ("To everything there is a season, and a time to every purpose.", "Ecclesiastes"),
            ("Time is the wisest of all counsellors.", "Pericles"),
            ("Life is long, if you know how to use it.", "Seneca"),
            ("Where words fail, music speaks.", "Hans Christian Andersen"),
            ("Without music, life would be a mistake.", "Friedrich Nietzsche"),
            ("Music is the moonlight in the gloomy night of life.", "Jean Paul"),
            ("Lost time is never found again.", "Benjamin Franklin"),
            ("Kairos: not the time on the clock, but the right moment.", "Greek idea of time"),
        };

        private readonly MediaWatcher _media;
        private readonly Func<BitmapSource?> _albumArt;
        private string _mode = Pulse;

        private readonly Grid _visual = new();
        private readonly TextBlock _caption = new() { TextAlignment = TextAlignment.Center, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) };
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

        private FontFamily _font = new("Segoe UI");
        private Color _current = Colors.White, _other = Colors.White;

        // Pulse
        private const int BarCount = 29;
        private readonly Border[] _bars = new Border[BarCount];
        private readonly double[] _history = new double[BarCount / 2 + 1];
        private Border? _glow;
        private double _level, _sinceSample, _time;
        private AppAudio? _spotifyAudio;
        private DateTime _audioChecked;

        // Clock / progress / quotes
        private TextBlock? _big, _small;
        private Border? _track, _fill;
        private Border? _artTile;
        private ScaleTransform? _artScale;
        private int _quoteIndex;
        private DateTime _quoteChanged;

        public LyricsFiller(MediaWatcher media, Func<BitmapSource?> albumArt)
        {
            _media = media;
            _albumArt = albumArt;
            IsHitTestVisible = true;
            Visibility = Visibility.Collapsed;

            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bottom = new StackPanel();
            bottom.Children.Add(_caption);
            bottom.Children.Add(_buttons);
            SetRow(bottom, 1);
            Children.Add(_visual);
            Children.Add(bottom);

            _quoteIndex = new Random().Next(QuoteList.Length);
            SizeChanged += (_, _) => Layout();
        }

        public bool IsShowing => Visibility == Visibility.Visible;

        /// <param name="buttons">Optional buttons under the caption (e.g. "Add lyrics" when none were found).</param>
        public void Show(string mode, string caption, params UIElement[] buttons)
        {
            _mode = mode;
            _caption.Text = caption;
            _caption.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
            _buttons.Children.Clear();
            foreach (var b in buttons) _buttons.Children.Add(b);
            Build();
            Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            _buttons.Children.Clear();
            _visual.Children.Clear();
        }

        public void ApplyStyle(FontFamily font, Color current, Color other)
        {
            _font = font;
            _current = current;
            _other = other;
            _caption.FontFamily = font;
            _caption.Foreground = new SolidColorBrush(other);
            if (IsShowing) Build();
        }

        // ----- Building each look -----

        private void Build()
        {
            _visual.Children.Clear();
            _big = _small = null;
            _track = _fill = _artTile = _glow = null;
            _artScale = null;

            switch (_mode)
            {
                case AlbumArt: BuildArt(); break;
                case Clock: BuildText(); break;
                case Progress: BuildProgress(); break;
                case Quotes: BuildQuote(); break;
                case Message: break;
                default: BuildPulse(); break;
            }
            Layout();
            Update(0);
        }

        private void BuildPulse()
        {
            _glow = new Border
            {
                Width = 160, Height = 160, CornerRadius = new CornerRadius(80),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Background = new RadialGradientBrush(Color.FromArgb(0x80, _current.R, _current.G, _current.B), Color.FromArgb(0, _current.R, _current.G, _current.B)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            _visual.Children.Add(_glow);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            int mid = BarCount / 2;
            for (int i = 0; i < BarCount; i++)
            {
                int dist = Math.Abs(i - mid);
                _bars[i] = new Border
                {
                    Width = 5, Height = 4, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(_current),
                    Opacity = Math.Max(0.25, 1 - dist * 0.055),
                };
                row.Children.Add(_bars[i]);
            }
            _visual.Children.Add(row);
        }

        private void BuildArt()
        {
            _artScale = new ScaleTransform(1, 1);
            _artTile = new Border
            {
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _artScale,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
            };
            var art = _albumArt();
            _artTile.Background = art != null
                ? new ImageBrush(art) { Stretch = Stretch.UniformToFill }
                : new SolidColorBrush(Color.FromArgb(0x30, _other.R, _other.G, _other.B));
            _visual.Children.Add(_artTile);
        }

        private void BuildText()
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _big = new TextBlock { FontFamily = _font, Foreground = new SolidColorBrush(_current), FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
            _small = new TextBlock { FontFamily = _font, Foreground = new SolidColorBrush(_other), Opacity = 0.7, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(_big);
            stack.Children.Add(_small);
            _visual.Children.Add(stack);
        }

        private void BuildProgress()
        {
            BuildText();
            _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x40, _other.R, _other.G, _other.B)), Margin = new Thickness(0, 12, 0, 0) };
            _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(_current), HorizontalAlignment = HorizontalAlignment.Left };
            var bar = new Grid();
            bar.Children.Add(_track);
            bar.Children.Add(_fill);
            _fill.Margin = _track.Margin;
            if (_visual.Children[0] is StackPanel stack) stack.Children.Add(bar);
        }

        private void BuildQuote()
        {
            BuildText();
            if (_big != null) { _big.TextWrapping = TextWrapping.Wrap; _big.FontStyle = FontStyles.Italic; _big.FontWeight = FontWeights.Normal; }
            ShowQuote(animate: false);
        }

        private void ShowQuote(bool animate)
        {
            if (_big == null || _small == null) return;
            var (text, who) = QuoteList[_quoteIndex % QuoteList.Length];
            _big.Text = $"“{text}”";
            _small.Text = $"— {who}";
            _quoteChanged = DateTime.Now;
            if (animate && _visual.Children.Count > 0)
            {
                var host = _visual.Children[0];
                host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)));
            }
        }

        // ----- Sizing -----

        private void Layout()
        {
            double w = Math.Max(0, _visual.ActualWidth), h = Math.Max(0, _visual.ActualHeight);
            if (w <= 0 || h <= 0) return;

            if (_artTile != null)
            {
                double side = Math.Max(40, Math.Min(w, h) * 0.86);
                _artTile.Width = _artTile.Height = side;
            }
            if (_big != null)
            {
                bool quote = _mode == Quotes;
                _big.FontSize = quote ? Math.Clamp(h * 0.16, 14, 40) : Math.Clamp(h * 0.38, 22, 120);
                _big.MaxWidth = w * 0.92;
                if (_small != null) _small.FontSize = Math.Clamp(h * 0.08, 11, 22);
            }
            if (_track != null) _track.Width = Math.Clamp(w * 0.6, 80, 420);
            if (_glow != null)
            {
                double size = Math.Min(w, h) * 0.9;
                _glow.Width = _glow.Height = size;
                _glow.CornerRadius = new CornerRadius(size / 2);
            }
        }

        // ----- Every frame -----

        public void Update(double dt)
        {
            if (!IsShowing) return;
            _time += dt;

            switch (_mode)
            {
                case Pulse: UpdatePulse(dt); break;
                case AlbumArt:
                    if (_artScale != null)
                    {
                        double level = ReadLevel(dt);
                        _artScale.ScaleX = _artScale.ScaleY = 1 + level * 0.035;
                    }
                    break;
                case Clock:
                    if (_big != null && _small != null)
                    {
                        var now = DateTime.Now;
                        _big.Text = now.ToString("t");
                        _small.Text = now.ToString("dddd d MMMM");
                    }
                    break;
                case Progress:
                    if (_big != null && _small != null)
                    {
                        var pos = _media.GetPosition();
                        var dur = _media.CurrentTrack?.Duration ?? TimeSpan.Zero;
                        _big.Text = Fmt(pos);
                        _small.Text = dur > TimeSpan.Zero ? $"of {Fmt(dur)}  ·  {Fmt(dur - pos)} left" : "";
                        if (_fill != null && _track != null)
                            _fill.Width = dur > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) * _track.Width : 0;
                    }
                    break;
                case Quotes:
                    if ((DateTime.Now - _quoteChanged).TotalSeconds > 18)
                    {
                        _quoteIndex++;
                        ShowQuote(animate: true);
                    }
                    break;
            }
        }

        private void UpdatePulse(double dt)
        {
            double level = ReadLevel(dt);

            // Shift the level history outward from the centre about 30 times a second.
            _sinceSample += dt;
            if (_sinceSample >= 0.033)
            {
                _sinceSample = 0;
                for (int i = _history.Length - 1; i > 0; i--) _history[i] = _history[i - 1];
                _history[0] = level;
            }

            double maxH = Math.Max(20, _visual.ActualHeight * 0.8);
            int mid = BarCount / 2;
            for (int i = 0; i < BarCount; i++)
            {
                var bar = _bars[i];
                if (bar == null) continue;
                int dist = Math.Abs(i - mid);
                double v = _history[Math.Min(dist, _history.Length - 1)];
                double shimmer = 0.8 + 0.2 * Math.Sin(_time * 3.1 + i * 0.7); // keeps it alive between beats
                bar.Height = 4 + v * shimmer * maxH * (1 - dist * 0.02);
            }

            if (_glow?.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = st.ScaleY = 0.55 + level * 0.6;
                _glow.Opacity = 0.25 + level * 0.6;
            }
        }

        /// <summary>Spotify's own loudness right now (0 … 1), smoothed: quick to rise, slow to fall.</summary>
        private double ReadLevel(double dt)
        {
            if (_spotifyAudio == null || DateTime.UtcNow - _audioChecked > TimeSpan.FromSeconds(3))
            {
                _audioChecked = DateTime.UtcNow;
                try { _spotifyAudio = AudioMixer.Spotify(); } catch { _spotifyAudio = null; }
            }

            double peak;
            try { peak = _spotifyAudio?.Peak ?? AudioMixer.MasterPeak; }
            catch { peak = 0; }
            if (!_media.IsPlaying) peak = 0;

            double target = Math.Sqrt(Math.Clamp(peak, 0, 1)); // closer to how loud it feels
            double k = target > _level ? 0.55 : Math.Min(1, dt * 3.5);
            _level += (target - _level) * k;
            return _level;
        }

        private static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
    }
}
