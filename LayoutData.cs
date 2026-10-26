using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Kairos
{
    /// <summary>
    /// A rectangle in the window, as fractions of the space inside the window's padding (0,0 = top left,
    /// 1,1 = bottom right). Fractions keep a layout in proportion when the window is resized.
    /// </summary>
    public sealed class LayoutBox
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; } = 1;
        public double H { get; set; } = 1;

        public LayoutBox() { }
        public LayoutBox(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }

        public LayoutBox Copy() => new(X, Y, W, H);

        /// <summary>Keeps the box inside the window and at least <paramref name="min"/> across.</summary>
        public LayoutBox Clamped(double min = 0.04)
        {
            double w = Math.Clamp(double.IsFinite(W) ? W : 1, min, 1), h = Math.Clamp(double.IsFinite(H) ? H : 1, min, 1);
            double x = Math.Clamp(double.IsFinite(X) ? X : 0, 0, 1 - w), y = Math.Clamp(double.IsFinite(Y) ? Y : 0, 0, 1 - h);
            return new LayoutBox(x, y, w, h);
        }

        public double Right => X + W;
        public double Bottom => Y + H;
        public double CentreX => X + W / 2;
        public double CentreY => Y + H / 2;
    }

    /// <summary>
    /// Where things go in the lyrics window. "Standard" is the classic arrangement (title row on top, lyrics below);
    /// "Custom" places the lyrics, the title, the album cover and the profile picture freely (Layout editor).
    /// </summary>
    public sealed class LayoutData
    {
        public bool Custom { get; set; }
        /// <summary>Set once a custom layout has been made (so switching to Custom doesn't start from scratch again).</summary>
        public bool HasCustom { get; set; }

        public LayoutBox Lyrics { get; set; } = new(0, 0.22, 1, 0.78);
        public LayoutBox Title { get; set; } = new(0.15, 0, 0.7, 0.2);
        public LayoutBox Art { get; set; } = new(0, 0, 0.13, 0.2);
        public LayoutBox Profile { get; set; } = new(0.87, 0, 0.13, 0.2);

        /// <summary>The cover stays square inside its box (otherwise it fills the box, cropped).</summary>
        public bool SquareArt { get; set; } = true;
        /// <summary>"Left", "Center" or "Right" (custom layouts; the standard one follows the lyrics).</summary>
        public string TitleAlign { get; set; } = "Center";
        /// <summary>Song title and artist size (1 = normal).</summary>
        public double TitleScale { get; set; } = 1;

        // ----- A panel behind the lyrics, with an optional border -----
        public bool LyricsPanel { get; set; }
        public string LyricsPanelColor { get; set; } = "#000000";
        public double LyricsPanelOpacity { get; set; } = 0.35;
        public string LyricsBorderColor { get; set; } = "#FFFFFF";
        public double LyricsBorderOpacity { get; set; } = 0.2;
        public double LyricsBorderWidth { get; set; } = 1;
        public double LyricsPanelCorner { get; set; } = 10;
        public double LyricsPanelPadding { get; set; } = 8;

        public LayoutData Copy() => JsonSerializer.Deserialize<LayoutData>(JsonSerializer.Serialize(this))!;

        /// <summary>Puts every box back inside the window (after hand-editing settings.json, say).</summary>
        public void Tidy()
        {
            Lyrics = (Lyrics ?? new LayoutBox(0, 0.22, 1, 0.78)).Clamped(0.1);
            Title = (Title ?? new LayoutBox(0.15, 0, 0.7, 0.2)).Clamped();
            Art = (Art ?? new LayoutBox(0, 0, 0.13, 0.2)).Clamped();
            Profile = (Profile ?? new LayoutBox(0.87, 0, 0.13, 0.2)).Clamped();
            TitleScale = Math.Clamp(double.IsFinite(TitleScale) ? TitleScale : 1, 0.6, 4);
            if (TitleAlign is not ("Left" or "Center" or "Right")) TitleAlign = "Center";
        }

        // ----- Ready-made arrangements -----

        public static readonly (string Key, string Name)[] Presets =
        {
            ("Classic", "Classic"),
            ("CoverLeft", "Cover left"),
            ("CoverRight", "Cover right"),
            ("CoverTop", "Cover on top"),
            ("TitleBelow", "Title below"),
        };

        /// <summary>Sets the boxes to a ready-made arrangement (other settings, like the lyrics panel, are kept).</summary>
        public void ApplyPreset(string key)
        {
            switch (key)
            {
                case "CoverLeft":
                    Art = new(0.02, 0.03, 0.36, 0.6); Title = new(0, 0.66, 0.4, 0.2); Profile = new(0.14, 0.86, 0.12, 0.14);
                    Lyrics = new(0.43, 0, 0.57, 1); TitleAlign = "Center"; SquareArt = true;
                    break;
                case "CoverRight":
                    Art = new(0.62, 0.03, 0.36, 0.6); Title = new(0.6, 0.66, 0.4, 0.2); Profile = new(0.74, 0.86, 0.12, 0.14);
                    Lyrics = new(0, 0, 0.57, 1); TitleAlign = "Center"; SquareArt = true;
                    break;
                case "CoverTop":
                    Art = new(0.36, 0, 0.28, 0.32); Title = new(0.1, 0.34, 0.8, 0.15); Profile = new(0.88, 0, 0.12, 0.16);
                    Lyrics = new(0, 0.51, 1, 0.49); TitleAlign = "Center"; SquareArt = true;
                    break;
                case "TitleBelow":
                    Lyrics = new(0, 0, 1, 0.78); Art = new(0, 0.8, 0.13, 0.2); Title = new(0.15, 0.8, 0.7, 0.2);
                    Profile = new(0.87, 0.8, 0.13, 0.2); TitleAlign = "Center"; SquareArt = true;
                    break;
                default: // Classic
                    Lyrics = new(0, 0.22, 1, 0.78); Art = new(0, 0, 0.13, 0.2); Title = new(0.15, 0, 0.7, 0.2);
                    Profile = new(0.87, 0, 0.13, 0.2); TitleAlign = "Center"; SquareArt = true;
                    break;
            }
            Custom = true;
            HasCustom = true;
        }
    }

    /// <summary>The maths behind dragging boxes in the Layout editor: moving, resizing and snapping to guides.</summary>
    public static class LayoutMaths
    {
        /// <summary>Which part of a box is being dragged.</summary>
        [Flags]
        public enum Grip { Move = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

        /// <summary>
        /// The box after dragging <paramref name="grip"/> by (dx, dy) (fractions of the window). Snaps the moving
        /// edges (or, when moving, the edges and centre) to the nearest guide within <paramref name="snapX"/> /
        /// <paramref name="snapY"/>, and reports the guides it snapped to (or NaN).
        /// </summary>
        public static LayoutBox Drag(LayoutBox start, Grip grip, double dx, double dy, IReadOnlyList<double> guidesX,
                                     IReadOnlyList<double> guidesY, double snapX, double snapY, double min,
                                     out double snappedX, out double snappedY)
        {
            snappedX = snappedY = double.NaN;
            double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;

            if (grip == Grip.Move)
            {
                double w = start.W, h = start.H;
                l = Math.Clamp(l + dx, 0, 1 - w);
                t = Math.Clamp(t + dy, 0, 1 - h);
                // Snap whichever of the left edge, centre or right edge is nearest a guide.
                var (offX, gx) = Nearest(new[] { l, l + w / 2, l + w }, guidesX, snapX);
                var (offY, gy) = Nearest(new[] { t, t + h / 2, t + h }, guidesY, snapY);
                l = Math.Clamp(l + offX, 0, 1 - w); t = Math.Clamp(t + offY, 0, 1 - h);
                snappedX = gx; snappedY = gy;
                return new LayoutBox(l, t, w, h);
            }

            if (grip.HasFlag(Grip.Left))
            {
                l = Math.Clamp(l + dx, 0, r - min);
                var (off, g) = Nearest(new[] { l }, guidesX, snapX);
                if (l + off <= r - min) { l += off; snappedX = g; }
            }
            if (grip.HasFlag(Grip.Right))
            {
                r = Math.Clamp(r + dx, l + min, 1);
                var (off, g) = Nearest(new[] { r }, guidesX, snapX);
                if (r + off >= l + min) { r += off; snappedX = g; }
            }
            if (grip.HasFlag(Grip.Top))
            {
                t = Math.Clamp(t + dy, 0, b - min);
                var (off, g) = Nearest(new[] { t }, guidesY, snapY);
                if (t + off <= b - min) { t += off; snappedY = g; }
            }
            if (grip.HasFlag(Grip.Bottom))
            {
                b = Math.Clamp(b + dy, t + min, 1);
                var (off, g) = Nearest(new[] { b }, guidesY, snapY);
                if (b + off >= t + min) { b += off; snappedY = g; }
            }
            return new LayoutBox(l, t, r - l, b - t);
        }

        /// <summary>The smallest shift (within <paramref name="snap"/>) that puts one of the points on a guide.</summary>
        private static (double Offset, double Guide) Nearest(double[] points, IReadOnlyList<double> guides, double snap)
        {
            double best = double.MaxValue, offset = 0, guide = double.NaN;
            if (snap <= 0) return (0, double.NaN);
            foreach (var p in points)
                foreach (var g in guides)
                {
                    double d = g - p;
                    if (Math.Abs(d) <= snap && Math.Abs(d) < Math.Abs(best)) { best = d; offset = d; guide = g; }
                }
            return (offset, guide);
        }

        /// <summary>Guides to snap to: the window's edges and centre, and the edges and centres of the other boxes.</summary>
        public static (List<double> X, List<double> Y) Guides(IEnumerable<LayoutBox> others)
        {
            var xs = new List<double> { 0, 0.5, 1 };
            var ys = new List<double> { 0, 0.5, 1 };
            foreach (var o in others)
            {
                xs.Add(o.X); xs.Add(o.CentreX); xs.Add(o.Right);
                ys.Add(o.Y); ys.Add(o.CentreY); ys.Add(o.Bottom);
            }
            return (xs, ys);
        }
    }
}
