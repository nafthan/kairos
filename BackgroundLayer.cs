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

        private Settings? _s;
        private BitmapSource? _albumArt;
        private BitmapSource? _picture;   // the adjusted background image
        private string? _pictureKey;
        private bool _loading;
        private double _scaleFactor = 1;
        private double _cornerRadius;
        private bool _drifting;

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

            Children.Add(_image);
            Children.Add(_tile);
            Children.Add(_tint);

            SizeChanged += (_, _) => { UpdateClip(); ApplyImageLayout(); };
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

            if (s.BackgroundMode == "AlbumArt" && albumArt != null) ShowAlbumArt();
            else if (s.BackgroundMode == "Image" && !string.IsNullOrEmpty(s.BackgroundImagePath)) ShowImage();
            else ShowNothing();
        }

        // ----- Modes -----

        private void ShowNothing()
        {
            ActiveMode = "Colour";
            StopDrift();
            _image.Source = null;
            _tileBrush.ImageSource = null;
            Visibility = Visibility.Collapsed;
        }

        private void ShowAlbumArt()
        {
            var s = _s!;
            ActiveMode = "AlbumArt";
            Visibility = Visibility.Visible;
            Opacity = 1;
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
            Opacity = Math.Clamp(s.ImageOpacity, 0, 1);

            var tint = MainWindow.ParseColor(s.ImageTintColor, Colors.Black);
            tint.A = (byte)Math.Round(Math.Clamp(s.ImageTintStrength, 0, 1) * 255);
            _tint.Background = new SolidColorBrush(tint);
            _tint.Visibility = tint.A > 0 ? Visibility.Visible : Visibility.Collapsed;

            double blur = Math.Clamp(s.ImageBlur, 0, 100) * _scaleFactor;
            _imageBlur.Radius = blur;
            _tileBlur.Radius = blur;

            string key = ImageProcessor.KeyFor(s);
            if (key != _pictureKey)
            {
                var cached = ImageProcessor.TryGetCached(s);
                if (cached != null)
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

                    var picture = await ImageProcessor.GetAsync(_s);
                    if (ActiveMode != "Image") break;

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

        /// <summary>Fit, zoom, position and flip for the background image.</summary>
        private void ApplyImageLayout()
        {
            if (ActiveMode != "Image" || _s == null) return;
            var s = _s;
            double zoom = Math.Clamp(s.ImageZoom, 1, 3);
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
