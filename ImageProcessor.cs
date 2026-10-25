using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kairos
{
    /// <summary>
    /// Loads background images and applies brightness, contrast and saturation with plain pixel maths
    /// (WPF has no built-in effects for these). Work happens off the UI thread, and results are cached
    /// so the lyrics window and the settings previews share them.
    /// </summary>
    public static class ImageProcessor
    {
        private const int MaxSide = 2200; // big enough for a sharp window, small enough to adjust quickly

        private sealed record Base(int Width, int Height, byte[] Pixels);

        private static readonly object Lock = new();
        private static string? _basePath;
        private static Base? _base;
        private static string? _resultKey;
        private static BitmapSource? _result;
        private static readonly Dictionary<string, Task<BitmapSource?>> InFlight = new();

        public static string KeyFor(Settings s) =>
            $"{s.BackgroundImagePath}|{s.ImageBrightness:0}|{s.ImageContrast:0}|{s.ImageSaturation:0}";

        /// <summary>Returns the adjusted image straight away if it's already been made, otherwise null.</summary>
        public static BitmapSource? TryGetCached(Settings s)
        {
            lock (Lock) return _resultKey == KeyFor(s) ? _result : null;
        }

        /// <summary>Makes (or fetches) the adjusted image. Returns null if the file can't be read.</summary>
        public static Task<BitmapSource?> GetAsync(Settings s)
        {
            string key = KeyFor(s);
            string path = s.BackgroundImagePath;
            double brightness = s.ImageBrightness, contrast = s.ImageContrast, saturation = s.ImageSaturation;

            Task<BitmapSource?> task;
            lock (Lock)
            {
                if (_resultKey == key) return Task.FromResult(_result);
                // The lyrics window and the settings preview often ask at the same moment: share the work.
                if (InFlight.TryGetValue(key, out var running)) return running;
                task = Task.Run(() => Make(path, key, brightness, contrast, saturation));
                InFlight[key] = task;
            }
            return task;
        }

        private static BitmapSource? Make(string path, string key, double brightness, double contrast, double saturation)
        {
            {
                try
                {
                    var b = LoadBase(path);
                    if (b == null) return null;
                    var result = Adjust(b, brightness, contrast, saturation);
                    lock (Lock)
                    {
                        _resultKey = key;
                        _result = result;
                    }
                    return result;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    lock (Lock) InFlight.Remove(key);
                }
            }
        }

        /// <summary>Checks a file is an image WPF can open, before it's adopted as the background.</summary>
        public static bool CanOpen(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
                return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0;
            }
            catch { return false; }
        }

        private static Base? LoadBase(string path)
        {
            lock (Lock)
                if (_basePath == path && _base != null) return _base;

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            BitmapSource frame;
            using (var fs = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = decoder.Frames[0];
            }

            // Scale very large photos down; the window never needs more than this.
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource src = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            var b = new Base(w, h, pixels);
            lock (Lock)
            {
                _basePath = path;
                _base = b;
            }
            return b;
        }

        /// <param name="brightness">-100 … 100 (0 = unchanged)</param>
        /// <param name="contrast">-100 … 100 (0 = unchanged)</param>
        /// <param name="saturation">0 … 200 percent (100 = unchanged, 0 = black and white)</param>
        private static BitmapSource Adjust(Base b, double brightness, double contrast, double saturation)
        {
            var output = new byte[b.Pixels.Length];

            // Brightness and contrast as a lookup table (fast); saturation per pixel.
            double c = Math.Clamp(contrast, -100, 100) * 2.55;
            double factor = 259 * (c + 255) / (255 * (259 - c));
            double add = Math.Clamp(brightness, -100, 100) * 1.6;
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
                lut[i] = ClampByte(factor * (i - 128) + 128 + add);

            double sat = Math.Clamp(saturation, 0, 200) / 100.0;
            bool doSat = Math.Abs(sat - 1) > 0.001;

            var src = b.Pixels;
            for (int i = 0; i < src.Length; i += 4)
            {
                double bl = src[i], g = src[i + 1], r = src[i + 2];
                if (doSat)
                {
                    double lum = 0.0722 * bl + 0.7152 * g + 0.2126 * r;
                    bl = lum + (bl - lum) * sat;
                    g = lum + (g - lum) * sat;
                    r = lum + (r - lum) * sat;
                }
                output[i] = lut[ClampByte(bl)];
                output[i + 1] = lut[ClampByte(g)];
                output[i + 2] = lut[ClampByte(r)];
                output[i + 3] = src[i + 3];
            }

            var bmp = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgra32, null, output, b.Width * 4);
            bmp.Freeze();
            return bmp;
        }

        private static byte ClampByte(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        /// <summary>Forget everything (after the image file is replaced or removed).</summary>
        public static void Invalidate()
        {
            lock (Lock)
            {
                _basePath = null; _base = null; _resultKey = null; _result = null;
                InFlight.Clear();
            }
        }
    }
}
