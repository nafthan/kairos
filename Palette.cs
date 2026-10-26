using System;
using System.Collections.Generic;
using System.Linq;

namespace Kairos
{
    /// <summary>
    /// Picks a handful of strong, distinct colours from a picture (album covers), favouring colourful ones over
    /// greys, so they glow nicely on a dark background.
    /// </summary>
    public static class Palette
    {
        public readonly record struct Rgb(byte R, byte G, byte B);

        /// <summary>A pleasant fallback for when there's no cover.</summary>
        public static readonly Rgb[] Default =
        {
            new(0x1E, 0xD7, 0x60), new(0x38, 0xBD, 0xF8), new(0xC0, 0x84, 0xFC), new(0xFB, 0x71, 0x85), new(0xFF, 0xB8, 0x6B),
        };

        private sealed class Bucket
        {
            public double R, G, B, Weight;
            public int Count;
        }

        /// <param name="bgra">Pixels, 4 bytes each (blue, green, red, alpha), ideally from a small copy (e.g. 48×48).</param>
        public static Rgb[] Extract(byte[] bgra, int width, int height, int count = 5)
        {
            // Group pixels by hue (12 slices of 30°); greys go into three brightness groups instead.
            var buckets = new Dictionary<int, Bucket>();
            int pixels = Math.Min(width * height, bgra.Length / 4);
            for (int i = 0; i < pixels; i++)
            {
                byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
                if (a < 128) continue;
                ToHsv(r, g, b, out double h, out double s, out double v);
                if (v < 0.08) continue; // near black says little about the cover's colours

                int key = s < 0.18 ? 100 + (int)(v * 2.999) : (int)(h / 30) % 12;
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Bucket();
                // Colourful, reasonably bright pixels count for more.
                double weight = (0.25 + s) * (0.35 + v);
                bucket.R += r * weight; bucket.G += g * weight; bucket.B += b * weight;
                bucket.Weight += weight;
                bucket.Count++;
            }

            var ranked = buckets
                .Where(kv => kv.Value.Count >= Math.Max(2, pixels / 400))
                .Select(kv => (Key: kv.Key, Colour: new Rgb(
                    (byte)(kv.Value.R / kv.Value.Weight), (byte)(kv.Value.G / kv.Value.Weight), (byte)(kv.Value.B / kv.Value.Weight)),
                    Score: kv.Value.Weight * (kv.Key >= 100 ? 0.45 : 1.0)))
                .OrderByDescending(x => x.Score)
                .ToList();

            var chosen = new List<Rgb>();
            foreach (var (_, colour, _) in ranked)
            {
                if (chosen.Any(c => Distance(c, colour) < 60)) continue; // too similar to one already picked
                chosen.Add(Liven(colour));
                if (chosen.Count == count) break;
            }

            if (chosen.Count == 0) return Default.Take(count).ToArray();

            // Nearly one-colour covers: add related shades so the dance still has variety.
            int original = chosen.Count;
            for (int i = 0; chosen.Count < Math.Min(count, 4); i++)
            {
                var c = chosen[i % original];
                ToHsv(c.R, c.G, c.B, out double h, out double s, out double v);
                double shift = (i % 2 == 0 ? 28 : -28) * (1 + i / 2);
                chosen.Add(FromHsv(h + shift, Math.Clamp(s * 0.9, 0.25, 1), Math.Clamp(v * (i % 2 == 0 ? 0.8 : 1.15), 0.35, 1)));
            }
            return chosen.ToArray();
        }

        /// <summary>Makes a colour a little more vivid and not too dark, so it glows rather than muddies.</summary>
        private static Rgb Liven(Rgb c)
        {
            ToHsv(c.R, c.G, c.B, out double h, out double s, out double v);
            if (s >= 0.18) s = Math.Min(1, s * 1.15 + 0.05);
            v = Math.Clamp(v * 1.1, 0.45, 1);
            return FromHsv(h, s, v);
        }

        private static double Distance(Rgb a, Rgb b)
        {
            // "Redmean" approximation of how different two colours look.
            double rm = (a.R + b.R) / 2.0;
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt((2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db);
        }

        public static void ToHsv(byte r8, byte g8, byte b8, out double h, out double s, out double v)
        {
            double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        public static Rgb FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            (double r, double g, double b) = h switch
            {
                < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
                < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
            };
            return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
