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

        // =================================================================
        //  Animated GIFs
        // =================================================================

        /// <summary>The frames of an animated GIF (already adjusted) and how long each one shows.</summary>
        public sealed class Animation
        {
            public BitmapSource[] Frames { get; init; } = Array.Empty<BitmapSource>();
            public TimeSpan[] Delays { get; init; } = Array.Empty<TimeSpan>();
        }

        private const int GifMaxFrames = 400;
        private const long GifMaxBytes = 260L * 1024 * 1024; // keeps big GIFs from using too much memory
        private const int GifMaxSide = 1000;

        private static string? _animKey;
        private static Animation? _anim;
        private static readonly Dictionary<string, Task<Animation?>> AnimInFlight = new();

        public static bool IsGif(string path) => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

        public static Animation? TryGetCachedAnimation(Settings s)
        {
            lock (Lock) return _animKey == KeyFor(s) ? _anim : null;
        }

        public static Task<Animation?> GetAnimationAsync(Settings s)
        {
            string key = KeyFor(s);
            string path = s.BackgroundImagePath;
            double brightness = s.ImageBrightness, contrast = s.ImageContrast, saturation = s.ImageSaturation;

            Task<Animation?> task;
            lock (Lock)
            {
                if (_animKey == key) return Task.FromResult(_anim);
                if (AnimInFlight.TryGetValue(key, out var running)) return running;
                task = Task.Run(() =>
                {
                    try
                    {
                        var anim = MakeAnimation(path, brightness, contrast, saturation);
                        lock (Lock) { _animKey = key; _anim = anim; }
                        return anim;
                    }
                    catch { return null; }
                    finally { lock (Lock) AnimInFlight.Remove(key); }
                });
                AnimInFlight[key] = task;
            }
            return task;
        }

        /// <summary>
        /// Builds every full frame of a GIF. GIF frames often only redraw part of the picture and say what to do with
        /// it afterwards (keep, clear, or restore), so each one is drawn onto a running canvas.
        /// </summary>
        internal static Animation? MakeAnimation(string path, double brightness, double contrast, double saturation)
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int count = Math.Min(decoder.Frames.Count, GifMaxFrames);
            if (count == 0) return null;

            // The whole picture's size ("logical screen"), falling back to the frames' extent.
            int width = 0, height = 0;
            try
            {
                if (decoder.Metadata is BitmapMetadata m)
                {
                    width = Num(m, "/logscrdesc/Width");
                    height = Num(m, "/logscrdesc/Height");
                }
            }
            catch { }
            if (width <= 0 || height <= 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var f = decoder.Frames[i];
                    var fm = f.Metadata as BitmapMetadata;
                    width = Math.Max(width, Num(fm, "/imgdesc/Left") + f.PixelWidth);
                    height = Math.Max(height, Num(fm, "/imgdesc/Top") + f.PixelHeight);
                }
            }
            if (width <= 0 || height <= 0) return null;

            // Scale big GIFs down so all the frames fit comfortably in memory.
            double scale = Math.Min(1.0, (double)GifMaxSide / Math.Max(width, height));
            double bytes = (double)count * width * height * 4 * scale * scale;
            if (bytes > GifMaxBytes) scale *= Math.Sqrt(GifMaxBytes / bytes);
            int outW = Math.Max(1, (int)Math.Round(width * scale));
            int outH = Math.Max(1, (int)Math.Round(height * scale));

            var canvas = new byte[width * height * 4];
            var frames = new BitmapSource[count];
            var delays = new TimeSpan[count];

            for (int i = 0; i < count; i++)
            {
                var frame = decoder.Frames[i];
                var meta = frame.Metadata as BitmapMetadata;
                int left = Num(meta, "/imgdesc/Left"), top = Num(meta, "/imgdesc/Top");
                int disposal = Num(meta, "/grctlext/Disposal");
                int delay = Num(meta, "/grctlext/Delay") * 10; // stored in hundredths of a second
                delays[i] = TimeSpan.FromMilliseconds(delay < 20 ? 100 : delay); // browsers treat tiny delays as 0.1 s

                byte[]? before = disposal == 3 ? (byte[])canvas.Clone() : null;

                // Draw this frame's pixels onto the canvas (GIF transparency is all-or-nothing).
                var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
                var px = new byte[fw * fh * 4];
                bgra.CopyPixels(px, fw * 4, 0);
                for (int y = 0; y < fh; y++)
                {
                    int cy = top + y;
                    if (cy < 0 || cy >= height) continue;
                    for (int x = 0; x < fw; x++)
                    {
                        int cx = left + x;
                        if (cx < 0 || cx >= width) continue;
                        int si = (y * fw + x) * 4;
                        if (px[si + 3] == 0) continue;
                        int di = (cy * width + cx) * 4;
                        canvas[di] = px[si]; canvas[di + 1] = px[si + 1]; canvas[di + 2] = px[si + 2]; canvas[di + 3] = px[si + 3];
                    }
                }

                // This frame as shown: scaled, then adjusted.
                var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
                byte[] shown;
                if (outW != width || outH != height)
                {
                    var scaled = new TransformedBitmap(full, new ScaleTransform((double)outW / width, (double)outH / height));
                    var sw = scaled.PixelWidth; var sh = scaled.PixelHeight;
                    shown = new byte[sw * sh * 4];
                    new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0).CopyPixels(shown, sw * 4, 0);
                    frames[i] = Adjust(new Base(sw, sh, shown), brightness, contrast, saturation);
                }
                else
                {
                    frames[i] = Adjust(new Base(width, height, (byte[])canvas.Clone()), brightness, contrast, saturation);
                }

                // What happens before the next frame is drawn.
                if (disposal == 2)
                {
                    // Clear this frame's area back to transparent.
                    int x0 = Math.Max(0, left), x1 = Math.Min(width, left + fw);
                    if (x1 > x0)
                        for (int y = Math.Max(0, top); y < Math.Min(height, top + fh); y++)
                            Array.Clear(canvas, (y * width + x0) * 4, (x1 - x0) * 4);
                }
                else if (disposal == 3 && before != null)
                {
                    canvas = before; // put back what was there before this frame
                }
            }

            return new Animation { Frames = frames, Delays = delays };
        }

        private static int Num(BitmapMetadata? meta, string query)
        {
            try
            {
                if (meta == null || !meta.ContainsQuery(query)) return 0;
                var v = meta.GetQuery(query);
                return v == null ? 0 : Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        /// <summary>Forget everything (after the image file is replaced or removed).</summary>
        public static void Invalidate()
        {
            lock (Lock)
            {
                _basePath = null; _base = null; _resultKey = null; _result = null;
                InFlight.Clear();
                _animKey = null; _anim = null;
                AnimInFlight.Clear();
            }
        }
    }
}
