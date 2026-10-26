using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Kairos
{
    /// <summary>
    /// Draws the window background when it's an image or album art: fit, zoom, position, flip, blur,
    /// tint and opacity (brightness/contrast/saturation are baked in by <see cref="ImageProcessor"/>).
    /// Used by the lyrics window and by both previews in settings, so they always match.
    /// </summary>
    public sealed class BackgroundLayer : Grid
    {
        private readonly Image _image;
        private readonly BlurEffect _imageBlur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _shift = new();

        private readonly Rectangle _tile;
        private readonly BlurEffect _tileBlur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        private readonly ImageBrush _tileBrush = new() { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };

        private readonly Border _tint;
        /// <summary>The window colour behind the picture, for the space around it when zoomed out.</summary>
        private readonly Border _backdrop = new() { Visibility = Visibility.Collapsed };
        /// <summary>The picture and its tint, faded together by the image opacity setting.</summary>
        private readonly Grid _pictureGroup = new();

        private Settings? _s;
        private BitmapSource? _albumArt;
        private BitmapSource? _picture;   // the adjusted background image
        private string? _pictureKey;
        private bool _loading;
        private double _scaleFactor = 1;
        private double _cornerRadius;
        private bool _drifting;

        // Animated GIF
        private ImageProcessor.Animation? _anim;
        private int _frame;
        private readonly System.Windows.Threading.DispatcherTimer _gifTimer = new();

        /// <summary>What is actually showing: "Colour" (nothing; the window colour shows), "Image" or "AlbumArt".</summary>
        public string ActiveMode { get; private set; } = "Colour";

        /// <summary>Raised when a background image finishes loading (or fails), so the host can refresh around it.</summary>
        public event Action? ImageLoaded;

        public BackgroundLayer()
        {
            ClipToBounds = true;
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;

            _image = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = _imageBlur,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            var transforms = new TransformGroup();
            transforms.Children.Add(_scale);
            transforms.Children.Add(_shift);
            _image.RenderTransform = transforms;

            _tile = new Rectangle { Fill = _tileBrush, Effect = _tileBlur, Visibility = Visibility.Collapsed };
            _tint = new Border { Visibility = Visibility.Collapsed };

            _pictureGroup.Children.Add(_image);
            _pictureGroup.Children.Add(_tile);
            _pictureGroup.Children.Add(_tint);
            Children.Add(_backdrop);
            Children.Add(_pictureGroup);

            SizeChanged += (_, _) => { UpdateClip(); ApplyImageLayout(); };

            _gifTimer.Tick += (_, _) => NextGifFrame();
            // Previews in settings come and go: don't leave their animations running in the background.
            Unloaded += (_, _) => _gifTimer.Stop();
            Loaded += (_, _) => { if (ActiveMode == "Image" && _anim != null && _anim.Frames.Length > 1) StartGif(); };
        }

        public double CornerRadius
        {
            get => _cornerRadius;
            set { _cornerRadius = Math.Max(0, value); UpdateClip(); }
        }

        /// <param name="scaleFactor">For previews drawn smaller than the real window: shrinks blur and tile size to match.</param>
        public void Update(Settings s, BitmapSource? albumArt, double scaleFactor = 1)
        {
            _s = s;
            _albumArt = albumArt;
            _scaleFactor = Math.Clamp(scaleFactor, 0.05, 4);

            // The background itself…
            if (s.BackgroundMode == "AlbumArt" && albumArt != null) ShowAlbumArt();
            else if (s.BackgroundMode == "Image" && !string.IsNullOrEmpty(s.BackgroundImagePath)) ShowImage();
            else ShowNothing();

            // …and the colour dance, its own layer behind or in front of it.
            if (s.DanceOn) ShowDance(); else StopDance();
        }

        /// <summary>Whether the colour dance is showing.</summary>
        public bool DanceActive => _danceOn;

        // =================================================================
        //  Colour dance: glowing colours from the album art, moving with the music
        // =================================================================

        private const int Blobs = 5;
        private readonly Grid _dance = new() { Visibility = Visibility.Collapsed, ClipToBounds = true };
        private readonly Border _danceBase = new();
        private readonly Canvas _danceCanvas = new();
        private readonly System.Windows.Shapes.Ellipse[] _blobs = new System.Windows.Shapes.Ellipse[Blobs];
        private readonly GradientStop[] _blobInner = new GradientStop[Blobs];
        private readonly GradientStop[] _blobMid = new GradientStop[Blobs];
        private readonly Color[] _blobTarget = new Color[Blobs];
        // The colours as they blend, kept as fractions so they arrive exactly (bytes would stall short of the target).
        private readonly double[,] _blobNow = new double[Blobs, 4];
        private readonly double[] _baseNow = { 10, 10, 14 };
        private Color _baseTarget = Color.FromRgb(10, 10, 14);
        private BitmapSource? _paletteFrom;
        private bool _danceBuilt, _danceRunning, _danceOn;
        private readonly System.Diagnostics.Stopwatch _danceClock = System.Diagnostics.Stopwatch.StartNew();
        private double _danceLast, _danceTime, _fakeBeat, _level;

        /// <summary>How loud the music is right now (0 … 1). Without one, a gentle made-up beat is used.</summary>
        public Func<double>? LevelSource { get; set; }

        private void ShowDance()
        {
            var settings = _s!;
            _danceOn = true;
            Visibility = Visibility.Visible;
            BuildDance();

            // Behind the picture (the default) or in front of it.
            bool front = settings.DanceLayer == "Front";
            int index = Children.IndexOf(_dance);
            if (front && index != Children.Count - 1) { Children.Remove(_dance); Children.Add(_dance); }
            else if (!front && index != 0) { Children.Remove(_dance); Children.Insert(0, _dance); }

            _dance.Visibility = Visibility.Visible;
            _dance.Opacity = Math.Clamp(settings.DanceOpacity, 0.05, 1);
            // Its own deep base colour fills the window, except when it's laid over a picture.
            _danceBase.Visibility = front && ActiveMode != "Colour" ? Visibility.Collapsed : Visibility.Visible;

            // New cover: pick its colours (the blobs blend over to them).
            if (!ReferenceEquals(_paletteFrom, _albumArt) || _blobTarget[0].A == 0)
            {
                _paletteFrom = _albumArt;
                var colours = _albumArt != null ? PaletteOf(_albumArt) : Palette.Default;
                for (int i = 0; i < Blobs; i++)
                {
                    var c = colours[i % colours.Length];
                    _blobTarget[i] = Color.FromRgb(c.R, c.G, c.B);
                }
                var main = colours[0];
                _baseTarget = Color.FromRgb((byte)(main.R * 0.12), (byte)(main.G * 0.12), (byte)(main.B * 0.12));
            }
            StartDance();
        }

        private void BuildDance()
        {
            if (_danceBuilt) return;
            _danceBuilt = true;
            _danceBase.Background = new SolidColorBrush(_baseTarget);
            _dance.Children.Add(_danceBase);
            _dance.Children.Add(_danceCanvas);
            for (int i = 0; i < Blobs; i++)
            {
                _blobInner[i] = new GradientStop(Colors.Transparent, 0);
                _blobMid[i] = new GradientStop(Colors.Transparent, 0.45);
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(_blobInner[i]);
                brush.GradientStops.Add(_blobMid[i]);
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                _blobs[i] = new System.Windows.Shapes.Ellipse { Fill = brush, IsHitTestVisible = false };
                _danceCanvas.Children.Add(_blobs[i]);
            }
            Children.Insert(0, _dance);
            // Pause the animation while off screen (e.g. a settings page that's been closed), and pick it back up after.
            Unloaded += (_, _) => PauseDance();
            Loaded += (_, _) => { if (_danceOn) StartDance(); };
        }

        private void StartDance()
        {
            if (_danceRunning) return;
            _danceRunning = true;
            _danceLast = _danceClock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += DanceFrame;
        }

        private void PauseDance()
        {
            if (_danceRunning) CompositionTarget.Rendering -= DanceFrame;
            _danceRunning = false;
        }

        private void StopDance()
        {
            _danceOn = false;
            PauseDance();
            _dance.Visibility = Visibility.Collapsed;
        }

        private void DanceFrame(object? sender, EventArgs e)
        {
            try { DanceStep(); }
            catch { /* never let one bad frame stop the animation */ }
        }

        private void DanceStep()
        {
            if (_s == null || !_danceOn) return;
            double now = _danceClock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - _danceLast, 0, 0.1);
            _danceLast = now;

            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // How loud it is: from the music, or a made-up beat (about 118 bpm) in previews.
            double level;
            if (LevelSource != null) level = Math.Clamp(LevelSource(), 0, 1);
            else
            {
                _fakeBeat += dt;
                level = 0.35 + 0.65 * Math.Pow(Math.Max(0, Math.Cos(_fakeBeat * Math.PI * 2 * 1.97)), 6);
            }
            _level += (level - _level) * (level > _level ? 0.6 : Math.Min(1, dt * 5));

            double energy = Math.Clamp(_s.DanceEnergy, 0, 2);
            double speed = Math.Clamp(_s.DanceSpeed, 0.1, 3);
            // Louder music makes them move faster, as if dancing to it.
            _danceTime += dt * speed * (0.55 + _level * energy * 0.9);
            double size = Math.Max(w, h) * Math.Clamp(_s.DanceSize, 0.4, 1.8);

            // Blend towards the current song's colours over about a second and a half.
            double k = Math.Min(1, dt * 1.8);
            for (int i = 0; i < Blobs; i++)
            {
                var target = _blobTarget[i];
                _blobNow[i, 0] += (235 - _blobNow[i, 0]) * k;
                _blobNow[i, 1] += (target.R - _blobNow[i, 1]) * k;
                _blobNow[i, 2] += (target.G - _blobNow[i, 2]) * k;
                _blobNow[i, 3] += (target.B - _blobNow[i, 3]) * k;
                var c = Color.FromArgb(Byte(_blobNow[i, 0]), Byte(_blobNow[i, 1]), Byte(_blobNow[i, 2]), Byte(_blobNow[i, 3]));
                _blobInner[i].Color = c;
                _blobMid[i].Color = Color.FromArgb((byte)(c.A * 0.45), c.R, c.G, c.B);

                // Each colour sways along its own looping path (different speeds, so the pattern never quite repeats)…
                double t = _danceTime, p = i * 1.37;
                double cx = w * (0.5 + 0.36 * Math.Sin(t * (0.53 + i * 0.11) + p) + 0.08 * Math.Sin(t * 1.9 + p * 2));
                double cy = h * (0.5 + 0.34 * Math.Cos(t * (0.47 + i * 0.09) + p * 1.6) + 0.07 * Math.Sin(t * 2.3 + p));
                // …and swells on the beat.
                double r = size * (0.42 + 0.1 * Math.Sin(t * 0.8 + p)) * (1 + _level * energy * 0.32);
                var blob = _blobs[i];
                blob.Width = blob.Height = r * 2;
                Canvas.SetLeft(blob, cx - r);
                Canvas.SetTop(blob, cy - r);
            }
            _baseNow[0] += (_baseTarget.R - _baseNow[0]) * k;
            _baseNow[1] += (_baseTarget.G - _baseNow[1]) * k;
            _baseNow[2] += (_baseTarget.B - _baseNow[2]) * k;
            if (_danceBase.Background is SolidColorBrush baseBrush)
                baseBrush.Color = Color.FromRgb(Byte(_baseNow[0]), Byte(_baseNow[1]), Byte(_baseNow[2]));
        }

        private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);


        /// <summary>The cover's main colours, from a small copy of it.</summary>
        public static Palette.Rgb[] PaletteOf(BitmapSource art)
        {
            try
            {
                const int size = 48;
                var small = new TransformedBitmap(art, new ScaleTransform((double)size / art.PixelWidth, (double)size / art.PixelHeight));
                var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                var px = new byte[w * h * 4];
                bgra.CopyPixels(px, w * 4, 0);
                return Palette.Extract(px, w, h);
            }
            catch { return Palette.Default; }
        }

        // ----- Modes -----

        private void ShowNothing()
        {
            ActiveMode = "Colour";
            StopGif();
            StopDrift();
            _image.Source = null;
            _tileBrush.ImageSource = null;
            Visibility = Visibility.Collapsed;
        }

        private void ShowAlbumArt()
        {
            var s = _s!;
            ActiveMode = "AlbumArt";
            StopGif();
            Visibility = Visibility.Visible;
            Opacity = 1;
            _pictureGroup.Opacity = 1;
            _backdrop.Visibility = Visibility.Collapsed;
            _tint.Visibility = Visibility.Collapsed;
            _tile.Visibility = Visibility.Collapsed;
            _image.Visibility = Visibility.Visible;
            _image.Source = _albumArt;
            _image.Stretch = Stretch.UniformToFill;
            _imageBlur.Radius = Math.Clamp(s.AlbumArtBlur, 0, 100) * _scaleFactor;

            if (s.AnimateAlbumArt) StartDrift();
            else
            {
                StopDrift();
                _scale.ScaleX = _scale.ScaleY = 1.15;
                _shift.X = _shift.Y = 0;
            }
        }

        private void ShowImage()
        {
            var s = _s!;
            ActiveMode = "Image";
            StopDrift();
            Visibility = Visibility.Visible;
            Opacity = 1;
            _pictureGroup.Opacity = Math.Clamp(s.ImageOpacity, 0, 1);
            if (s.ImageSurround == "Colour")
            {
                var colour = MainWindow.ParseColor(s.BackgroundColor, Colors.Black);
                colour.A = (byte)Math.Clamp(Math.Round(s.BackgroundOpacity * 255), 0, 255);
                _backdrop.Background = new SolidColorBrush(colour);
                _backdrop.Visibility = Visibility.Visible;
            }
            else _backdrop.Visibility = Visibility.Collapsed;

            var tint = MainWindow.ParseColor(s.ImageTintColor, Colors.Black);
            tint.A = (byte)Math.Round(Math.Clamp(s.ImageTintStrength, 0, 1) * 255);
            _tint.Background = new SolidColorBrush(tint);
            _tint.Visibility = tint.A > 0 ? Visibility.Visible : Visibility.Collapsed;

            double blur = Math.Clamp(s.ImageBlur, 0, 100) * _scaleFactor;
            _imageBlur.Radius = blur;
            _tileBlur.Radius = blur;

            bool gif = ImageProcessor.IsGif(s.BackgroundImagePath);
            if (!gif) { StopGif(); _anim = null; }

            string key = ImageProcessor.KeyFor(s);
            if (key != _pictureKey)
            {
                var cachedAnim = gif ? ImageProcessor.TryGetCachedAnimation(s) : null;
                var cached = gif ? null : ImageProcessor.TryGetCached(s);
                if (cachedAnim != null && cachedAnim.Frames.Length > 0)
                {
                    UseAnimation(cachedAnim);
                    _pictureKey = key;
                }
                else if (cached != null)
                {
                    _picture = cached;
                    _pictureKey = key;
                }
                else if (!_loading)
                {
                    _ = LoadLatestAsync();
                }
                // While loading, the previous version stays up, so sliders don't flicker.
            }
            ApplyImageLayout();
        }

        /// <summary>
        /// Makes the adjusted picture for the latest settings. If the settings change again while it's
        /// working (dragging a slider), it goes round again for the newest values instead of queueing every step.
        /// </summary>
        private async System.Threading.Tasks.Task LoadLatestAsync()
        {
            _loading = true;
            try
            {
                while (_s != null && ActiveMode == "Image")
                {
                    string key = ImageProcessor.KeyFor(_s);
                    if (key == _pictureKey) break;

                    BitmapSource? picture;
                    if (ImageProcessor.IsGif(_s.BackgroundImagePath))
                    {
                        var anim = await ImageProcessor.GetAnimationAsync(_s);
                        if (ActiveMode != "Image") break;
                        if (anim != null && anim.Frames.Length > 0) UseAnimation(anim);
                        picture = anim != null && anim.Frames.Length > 0 ? anim.Frames[_frame % anim.Frames.Length] : null;
                    }
                    else
                    {
                        picture = await ImageProcessor.GetAsync(_s);
                        if (ActiveMode != "Image") break;
                    }

                    _picture = picture;
                    _pictureKey = key;
                    if (picture == null)
                    {
                        // File missing or unreadable: fall back to the background colour.
                        ShowNothing();
                        ImageLoaded?.Invoke();
                        break;
                    }
                    ApplyImageLayout();
                    ImageLoaded?.Invoke();
                }
            }
            finally
            {
                _loading = false;
            }
        }

        // ----- Animated GIF playback -----

        private void UseAnimation(ImageProcessor.Animation anim)
        {
            _anim = anim;
            if (_frame >= anim.Frames.Length) _frame = 0;
            _picture = anim.Frames[_frame];
            if (anim.Frames.Length > 1) StartGif(); else StopGif();
        }

        private void StartGif()
        {
            if (_anim == null || _anim.Frames.Length < 2) return;
            _gifTimer.Interval = _anim.Delays[_frame];
            _gifTimer.Start();
        }

        private void StopGif() => _gifTimer.Stop();

        private void NextGifFrame()
        {
            if (_anim == null || _anim.Frames.Length < 2 || ActiveMode != "Image") { StopGif(); return; }
            _frame = (_frame + 1) % _anim.Frames.Length;
            _picture = _anim.Frames[_frame];
            // Just swap the picture; position, blur and tint stay as they are.
            if (_tile.Visibility == Visibility.Visible) _tileBrush.ImageSource = _picture;
            else _image.Source = _picture;
            _gifTimer.Interval = _anim.Delays[_frame];
        }

        /// <summary>Fit, zoom, position and flip for the background image.</summary>
        private void ApplyImageLayout()
        {
            if (ActiveMode != "Image" || _s == null) return;
            var s = _s;
            double zoom = Math.Clamp(s.ImageZoom, 0.25, 3);
            double offX = Math.Clamp(s.ImageOffsetX, -100, 100) / 100 * ActualWidth / 2;
            double offY = Math.Clamp(s.ImageOffsetY, -100, 100) / 100 * ActualHeight / 2;

            if (string.Equals(s.ImageFit, "Tile", StringComparison.OrdinalIgnoreCase))
            {
                _image.Visibility = Visibility.Collapsed;
                _image.Source = null;
                _tile.Visibility = Visibility.Visible;
                _tileBrush.ImageSource = _picture;
                if (_picture != null)
                {
                    // Tiles at the picture's natural size (scaled for previews), times zoom.
                    double w = Math.Max(8, _picture.PixelWidth * zoom * _scaleFactor * 0.5);
                    double h = Math.Max(8, _picture.PixelHeight * zoom * _scaleFactor * 0.5);
                    _tileBrush.Viewport = new Rect(offX, offY, w, h);
                }
                _tileBrush.RelativeTransform = s.ImageFlip ? new ScaleTransform(-1, 1, 0.5, 0.5) : Transform.Identity;
                return;
            }

            _tile.Visibility = Visibility.Collapsed;
            _tileBrush.ImageSource = null;
            _image.Visibility = Visibility.Visible;
            _image.Source = _picture;
            _image.Stretch = s.ImageFit?.ToLowerInvariant() switch
            {
                "fit" => Stretch.Uniform,
                "stretch" => Stretch.Fill,
                _ => Stretch.UniformToFill,
            };
            _scale.ScaleX = zoom * (s.ImageFlip ? -1 : 1);
            _scale.ScaleY = zoom;
            _shift.X = offX;
            _shift.Y = offY;
        }

        // ----- Slow drift (album art) -----

        private void StartDrift()
        {
            if (_drifting) return;
            _drifting = true;

            DoubleAnimation Drift(double from, double to, double seconds) => new(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Drift(1.15, 1.35, 24));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Drift(1.15, 1.35, 24));
            _shift.BeginAnimation(TranslateTransform.XProperty, Drift(-18 * _scaleFactor, 18 * _scaleFactor, 31));
            _shift.BeginAnimation(TranslateTransform.YProperty, Drift(-10 * _scaleFactor, 10 * _scaleFactor, 19));
        }

        private void StopDrift()
        {
            if (!_drifting) return;
            _drifting = false;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.BeginAnimation(TranslateTransform.YProperty, null);
            _scale.ScaleX = _scale.ScaleY = 1;
            _shift.X = _shift.Y = 0;
        }

        private void UpdateClip()
        {
            Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)), _cornerRadius, _cornerRadius);
        }
    }
}
