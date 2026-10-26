using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kairos
{
    /// <summary>
    /// Writes looping animated GIFs from BGRA frames. WPF's own GIF encoder can't do animation (no frame timing or
    /// looping), so this is a small complete one: a shared 255-colour palette chosen by median cut, a gentle ordered
    /// dither so gradients don't band, only the changed part of each frame stored, and standard LZW compression.
    /// </summary>
    public static class GifEncoder
    {
        private const int Transparent = 255; // palette slot kept free for "unchanged since the last frame"

        /// <summary>
        /// Writes a GIF one frame at a time, so long animations never need every frame in memory at once. The palette
        /// is chosen up front (from a sample of the frames, see <see cref="BuildPalette"/>).
        /// </summary>
        public sealed class GifWriter : IDisposable
        {
            private readonly BinaryWriter _w;
            private readonly int _width, _height;
            private readonly NearestColour _lookup;
            private readonly byte[] _shown, _indexed;
            private bool _first = true, _finished;

            public GifWriter(Stream output, int width, int height, int[] palette)
            {
                if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentException("Bad size");
                _w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
                _width = width;
                _height = height;
                _lookup = new NearestColour(palette);
                _shown = new byte[width * height]; // what's on screen after the previous frame
                _indexed = new byte[width * height];

                // Header and screen
                _w.Write("GIF89a".ToCharArray());
                _w.Write((ushort)width);
                _w.Write((ushort)height);
                _w.Write((byte)0xF7); // global colour table, 8 bits per colour, 256 entries
                _w.Write((byte)0);    // background colour index
                _w.Write((byte)0);    // pixel aspect ratio
                for (int i = 0; i < 256; i++)
                {
                    var c = i < palette.Length ? palette[i] : 0;
                    _w.Write((byte)(c >> 16)); _w.Write((byte)(c >> 8)); _w.Write((byte)c);
                }

                // Loop forever
                _w.Write(new byte[] { 0x21, 0xFF, 0x0B });
                _w.Write("NETSCAPE2.0".ToCharArray());
                _w.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
            }

            /// <summary>Adds a frame (BGRA bytes) shown for <paramref name="delayCs"/> hundredths of a second.</summary>
            public void Add(byte[] bgra, int delayCs)
            {
                if (_finished) throw new InvalidOperationException("Already finished");
                int width = _width, height = _height;
                Quantize(bgra, width, height, _lookup, _indexed);

                // Only store the rectangle that changed, with unchanged pixels inside it see-through.
                int left = 0, top = 0, right = width - 1, bottom = height - 1;
                bool first = _first;
                _first = false;
                if (!first && !ChangedArea(_indexed, _shown, width, height, out left, out top, out right, out bottom))
                {
                    left = top = right = bottom = 0; // nothing changed: a single see-through pixel keeps the timing
                }
                int fw = right - left + 1, fh = bottom - top + 1;
                var pixels = new byte[fw * fh];
                bool usesTransparency = false;
                for (int y = 0; y < fh; y++)
                {
                    int src = (top + y) * width + left, dst = y * fw;
                    for (int x = 0; x < fw; x++, src++, dst++)
                    {
                        byte v = _indexed[src];
                        if (!first && v == _shown[src]) { pixels[dst] = Transparent; usesTransparency = true; }
                        else { pixels[dst] = v; _shown[src] = v; }
                    }
                }

                // Graphic control: keep the previous frame underneath, this frame's delay, transparency
                _w.Write(new byte[] { 0x21, 0xF9, 0x04 });
                _w.Write((byte)((1 << 2) | (usesTransparency ? 1 : 0)));
                _w.Write((ushort)Math.Clamp(delayCs, 2, 65535));
                _w.Write((byte)Transparent);
                _w.Write((byte)0);

                // Image descriptor
                _w.Write((byte)0x2C);
                _w.Write((ushort)left); _w.Write((ushort)top);
                _w.Write((ushort)fw); _w.Write((ushort)fh);
                _w.Write((byte)0);

                _w.Write((byte)8); // LZW minimum code size
                Lzw.Write(_w, pixels);
            }

            /// <summary>Ends the file. (Disposing does this too.)</summary>
            public void Finish()
            {
                if (_finished) return;
                _finished = true;
                _w.Write((byte)0x3B);
                _w.Flush();
            }

            public void Dispose()
            {
                Finish();
                _w.Dispose();
            }
        }

        private static bool ChangedArea(byte[] now, byte[] before, int width, int height, out int left, out int top, out int right, out int bottom)
        {
            left = width; top = height; right = -1; bottom = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (now[row + x] == before[row + x]) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    bottom = y;
                }
            }
            return right >= 0;
        }

        // ----- Colours -----

        // A 4×4 ordered dither: small, fixed offsets that turn bands into a fine, steady texture (no shimmer between frames).
        private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        private static void Quantize(byte[] bgra, int width, int height, NearestColour lookup, byte[] result)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x, p = i * 4;
                    int d = (Bayer[(y & 3) * 4 + (x & 3)] - 7) * 3 / 4; // about −5 … +6
                    int b = Math.Clamp(bgra[p] + d, 0, 255);
                    int g = Math.Clamp(bgra[p + 1] + d, 0, 255);
                    int r = Math.Clamp(bgra[p + 2] + d, 0, 255);
                    result[i] = lookup.Find(r, g, b);
                }
            }
        }

        /// <summary>Picks 255 colours that suit every frame (median cut on a sample of pixels from all of them).</summary>
        public static int[] BuildPalette(IReadOnlyList<byte[]> frames, int width, int height)
        {
            long total = (long)width * height * frames.Count;
            int step = (int)Math.Max(1, total / 250_000);
            var samples = new List<int>();
            long n = 0;
            foreach (var frame in frames)
            {
                for (int i = 0; i < width * height; i++, n++)
                {
                    if (n % step != 0) continue;
                    int p = i * 4;
                    samples.Add((frame[p + 2] << 16) | (frame[p + 1] << 8) | frame[p]);
                }
            }

            var boxes = new List<List<int>> { samples };
            while (boxes.Count < 255)
            {
                // Split the box with the widest spread of colour (weighted by how many pixels it holds).
                int pick = -1; long best = 0; int channel = 0;
                for (int b = 0; b < boxes.Count; b++)
                {
                    var box = boxes[b];
                    if (box.Count < 2) continue;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int shift = 16 - ch * 8, lo = 255, hi = 0;
                        foreach (var c in box) { int v = (c >> shift) & 255; if (v < lo) lo = v; if (v > hi) hi = v; }
                        long score = (long)(hi - lo) * (long)Math.Sqrt(box.Count);
                        if (hi > lo && score > best) { best = score; pick = b; channel = ch; }
                    }
                }
                if (pick < 0) break; // every box is a single colour already
                int sh = 16 - channel * 8;
                var sorted = boxes[pick].OrderBy(c => (c >> sh) & 255).ToList();
                int mid = sorted.Count / 2;
                boxes[pick] = sorted.GetRange(0, mid);
                boxes.Add(sorted.GetRange(mid, sorted.Count - mid));
            }

            return boxes.Where(b => b.Count > 0).Select(b =>
            {
                long r = 0, g = 0, bl = 0;
                foreach (var c in b) { r += (c >> 16) & 255; g += (c >> 8) & 255; bl += c & 255; }
                return (int)((r / b.Count) << 16 | (g / b.Count) << 8 | (bl / b.Count));
            }).Distinct().Take(255).ToArray();
        }

        /// <summary>Nearest palette colour, remembered for each (slightly rounded) colour so it's quick.</summary>
        private sealed class NearestColour
        {
            private readonly int[] _palette;
            private readonly short[] _cache = new short[1 << 18]; // 6 bits per channel
            public NearestColour(int[] palette) { _palette = palette; Array.Fill(_cache, (short)-1); }

            public byte Find(int r, int g, int b)
            {
                int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
                short cached = _cache[key];
                if (cached >= 0) return (byte)cached;
                int rr = (r & ~3) | 2, gg = (g & ~3) | 2, bb = (b & ~3) | 2;
                int best = 0, bestDist = int.MaxValue;
                for (int i = 0; i < _palette.Length; i++)
                {
                    int c = _palette[i];
                    int dr = ((c >> 16) & 255) - rr, dg = ((c >> 8) & 255) - gg, db = (c & 255) - bb;
                    int dist = 2 * dr * dr + 4 * dg * dg + 3 * db * db; // eyes are most sensitive to green
                    if (dist < bestDist) { bestDist = dist; best = i; }
                }
                _cache[key] = (short)best;
                return (byte)best;
            }
        }

        // ----- LZW compression, as GIF expects it (follows giflib's encoder) -----

        private static class Lzw
        {
            private const int ClearCode = 256, EndCode = 257, MaxCode = 4095;

            public static void Write(BinaryWriter w, byte[] pixels)
            {
                var bits = new BitPacker(w);
                var table = new Dictionary<int, int>(5000);
                int next = EndCode + 1, codeBits = 9, limit = 1 << codeBits;

                void Output(int code)
                {
                    bits.Add(code, codeBits);
                    if (next >= limit && code <= MaxCode && codeBits < 12) limit = 1 << ++codeBits;
                }

                Output(ClearCode);
                int current = -1;
                foreach (var px in pixels)
                {
                    if (current < 0) { current = px; continue; }
                    int key = (current << 8) | px;
                    if (table.TryGetValue(key, out int found)) { current = found; continue; }
                    Output(current);
                    current = px;
                    if (next >= MaxCode)
                    {
                        Output(ClearCode);
                        next = EndCode + 1; codeBits = 9; limit = 1 << codeBits;
                        table.Clear();
                    }
                    else table[key] = next++;
                }
                if (current >= 0) Output(current);
                Output(EndCode);
                bits.Finish();
            }
        }

        /// <summary>Packs codes least-significant bit first into GIF's blocks of up to 255 bytes.</summary>
        private sealed class BitPacker
        {
            private readonly BinaryWriter _w;
            private readonly byte[] _block = new byte[255];
            private int _blockLength, _bitCount;
            private uint _buffer;
            public BitPacker(BinaryWriter w) => _w = w;

            public void Add(int code, int bits)
            {
                _buffer |= (uint)code << _bitCount;
                _bitCount += bits;
                while (_bitCount >= 8) { Byte((byte)_buffer); _buffer >>= 8; _bitCount -= 8; }
            }

            private void Byte(byte b)
            {
                _block[_blockLength++] = b;
                if (_blockLength == 255) FlushBlock();
            }

            private void FlushBlock()
            {
                if (_blockLength == 0) return;
                _w.Write((byte)_blockLength);
                _w.Write(_block, 0, _blockLength);
                _blockLength = 0;
            }

            public void Finish()
            {
                if (_bitCount > 0) { Byte((byte)_buffer); _buffer = 0; _bitCount = 0; }
                FlushBlock();
                _w.Write((byte)0); // end of image data
            }
        }
    }
}
