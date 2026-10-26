using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kairos
{
    /// <summary>
    /// Arranges the lyrics window's parts, in the standard layout or a custom one from the Layout editor. Shared by
    /// the real window and the live previews, so they always match.
    /// </summary>
    public static class LayoutEngine
    {
        /// <summary>The pieces being arranged. All live in a two-row grid (title row, then lyrics).</summary>
        public sealed class Parts
        {
            public Grid Content = null!;
            /// <summary>The title row: holds the small cover and profile picture (standard layout) and the text.</summary>
            public FrameworkElement TitleRow = null!;
            public FrameworkElement TitleText = null!;
            public TextBlock[] TitleBlocks = Array.Empty<TextBlock>();
            public Border RowArt = null!;
            public Border RowProfile = null!;
            /// <summary>Cover and profile picture placed freely (custom layout).</summary>
            public Border FreeArt = null!;
            public Border FreeProfile = null!;
            /// <summary>The optional panel behind the lyrics.</summary>
            public Border LyricsPanel = null!;
            /// <summary>Everything that shows in the lyrics area (the lines, status messages, the no-lyrics visual).</summary>
            public FrameworkElement[] Lyrics = Array.Empty<FrameworkElement>();
        }

        public const double ThumbSize = 42;

        /// <param name="art">The cover (null = none to show).</param>
        /// <param name="profile">The profile picture (null = none to show).</param>
        public static void Apply(Parts p, Settings s, Brush? art, Brush? profile)
        {
            var layout = s.Layout ?? new LayoutData();
            bool custom = layout.Custom;
            double width = p.Content.ActualWidth, height = p.Content.ActualHeight;
            var lyricsAlign = Align(s.TextAlignment);
            bool showTitle = s.ShowTrackInfo;

            // ---- The panel behind the lyrics ----
            var panel = p.LyricsPanel;
            double pad = 0;
            if (layout.LyricsPanel)
            {
                var fill = MainWindow.ParseColor(layout.LyricsPanelColor, Colors.Black);
                fill.A = (byte)Math.Round(Math.Clamp(layout.LyricsPanelOpacity, 0, 1) * 255);
                var line = MainWindow.ParseColor(layout.LyricsBorderColor, Colors.White);
                line.A = (byte)Math.Round(Math.Clamp(layout.LyricsBorderOpacity, 0, 1) * 255);
                panel.Background = new SolidColorBrush(fill);
                panel.BorderBrush = new SolidColorBrush(line);
                panel.BorderThickness = new Thickness(Math.Clamp(layout.LyricsBorderWidth, 0, 12));
                panel.CornerRadius = new CornerRadius(Math.Clamp(layout.LyricsPanelCorner, 0, 60));
                panel.Visibility = Visibility.Visible;
                pad = Math.Clamp(layout.LyricsPanelPadding, 0, 60) + panel.BorderThickness.Left;
            }
            else panel.Visibility = Visibility.Collapsed;

            foreach (var b in p.TitleBlocks) b.TextAlignment = custom ? Align(layout.TitleAlign) : lyricsAlign;

            if (!custom || width <= 0 || height <= 0)
            {
                // ---- Standard: the title row on top (cover left, profile right), lyrics below ----
                Place(p.TitleRow, 0, 1, new Thickness(0, 0, 0, 8));
                p.TitleRow.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
                Picture(p.RowArt, art, ThumbSize * 0.14);
                Picture(p.RowProfile, profile, ThumbSize / 2);
                double left = art != null ? ThumbSize + 10 : 0, right = profile != null ? ThumbSize + 10 : 0;
                // Centred titles get equal room on both sides, so they line up with centred lyrics.
                p.TitleText.Margin = lyricsAlign == TextAlignment.Center
                    ? new Thickness(Math.Max(left, right), 0, Math.Max(left, right), 0)
                    : new Thickness(left, 0, right, 0);
                p.TitleRow.MinHeight = art != null || profile != null ? ThumbSize : 0;
                p.FreeArt.Visibility = Visibility.Collapsed;
                p.FreeProfile.Visibility = Visibility.Collapsed;

                Place(panel, 1, 1, new Thickness(0));
                foreach (var e in p.Lyrics) Place(e, 1, 1, new Thickness(pad));
                return;
            }

            // ---- Custom: everything where you put it ----
            Thickness Margin(LayoutBox b, double inset = 0)
            {
                var c = b.Clamped(0.02);
                return new Thickness(c.X * width + inset, c.Y * height + inset,
                                     Math.Max(0, (1 - c.Right) * width) + inset, Math.Max(0, (1 - c.Bottom) * height) + inset);
            }

            Place(p.TitleRow, 0, 2, Margin(layout.Title));
            p.TitleRow.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
            p.TitleRow.MinHeight = 0;
            p.TitleText.Margin = new Thickness(0);
            p.RowArt.Visibility = Visibility.Collapsed;
            p.RowProfile.Visibility = Visibility.Collapsed;

            PlaceFree(p.FreeArt, art, layout.Art, layout.SquareArt, width, height, round: false);
            PlaceFree(p.FreeProfile, profile, layout.Profile, square: true, width, height, round: true);

            Place(panel, 0, 2, Margin(layout.Lyrics));
            foreach (var e in p.Lyrics) Place(e, 0, 2, Margin(layout.Lyrics, pad));
        }

        private static void Place(FrameworkElement e, int row, int span, Thickness margin)
        {
            Grid.SetRow(e, row);
            Grid.SetRowSpan(e, span);
            e.Margin = margin;
        }

        private static void Picture(Border b, Brush? image, double corner)
        {
            b.Visibility = image != null ? Visibility.Visible : Visibility.Collapsed;
            b.Background = image;
            b.CornerRadius = new CornerRadius(corner);
        }

        /// <summary>A picture in its own box: square (centred in the box) or filling it, and round for the profile.</summary>
        private static void PlaceFree(Border b, Brush? image, LayoutBox box, bool square, double width, double height, bool round)
        {
            if (image == null) { b.Visibility = Visibility.Collapsed; return; }
            var c = box.Clamped(0.02);
            double x = c.X * width, y = c.Y * height, w = c.W * width, h = c.H * height;
            if (square)
            {
                double side = Math.Min(w, h);
                x += (w - side) / 2; y += (h - side) / 2; w = h = side;
            }
            Grid.SetRow(b, 0);
            Grid.SetRowSpan(b, 2);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.Margin = new Thickness(x, y, 0, 0);
            b.Width = Math.Max(1, w);
            b.Height = Math.Max(1, h);
            b.Background = image;
            b.CornerRadius = new CornerRadius(round ? Math.Min(w, h) / 2 : Math.Min(w, h) * 0.12);
            b.Visibility = Visibility.Visible;
        }

        public static TextAlignment Align(string? s) => s?.ToLowerInvariant() switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center,
        };

        /// <summary>
        /// Where the parts are right now, as layout boxes: used to start a custom layout from the standard one
        /// exactly as it looks, and to outline the parts in the Layout editor.
        /// </summary>
        public static LayoutData Measure(Parts p, LayoutData from)
        {
            var result = from.Copy();
            double width = p.Content.ActualWidth, height = p.Content.ActualHeight;
            if (width <= 0 || height <= 0) return result;

            LayoutBox? BoxOf(FrameworkElement e)
            {
                if (e.Visibility != Visibility.Visible || e.ActualWidth <= 0 || e.ActualHeight <= 0) return null;
                try
                {
                    var r = e.TransformToAncestor(p.Content).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                    return new LayoutBox(r.X / width, r.Y / height, r.Width / width, r.Height / height).Clamped(0.02);
                }
                catch (InvalidOperationException) { return null; }
            }

            // The lyrics area includes the panel's padding.
            var lyrics = p.LyricsPanel.Visibility == Visibility.Visible ? BoxOf(p.LyricsPanel) : BoxOf(p.Lyrics[0]);
            if (lyrics != null) result.Lyrics = lyrics;
            if (BoxOf(p.TitleRow) is { } row)
            {
                // The text part of the row (between the pictures), so the cover and profile get boxes of their own.
                result.Title = BoxOf(p.TitleText) is { } text && !from.Custom
                    ? new LayoutBox(text.X, row.Y, text.W, row.H)
                    : row;
            }
            if ((from.Custom ? BoxOf(p.FreeArt) : BoxOf(p.RowArt)) is { } art) result.Art = art;
            if ((from.Custom ? BoxOf(p.FreeProfile) : BoxOf(p.RowProfile)) is { } profile) result.Profile = profile;
            return result;
        }
    }
}
