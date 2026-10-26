using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Kairos
{
    /// <summary>
    /// Drawn over a live preview in settings → Layout: an outline for each part of the window (lyrics, title, cover,
    /// profile picture) that you drag to move, or drag by its edges and corners to resize. Edges and centres snap to
    /// the window and to each other, with guide lines; hold Alt to place freely. Arrow keys nudge the selected part.
    /// </summary>
    public sealed class LayoutEditor : Canvas
    {
        private enum Part { Lyrics, Title, Art, Profile }

        private sealed class Item
        {
            public Part Part;
            public Grid Root = null!;
            public Rectangle Outline = null!;
            public Border Label = null!;
            public TextBlock LabelText = null!;
            public readonly List<(Rectangle Handle, LayoutMaths.Grip Grip)> Handles = new();
        }

        private static readonly Color Accent = Color.FromRgb(0x1E, 0xD7, 0x60);

        private readonly Settings _s;
        private readonly LivePreview _preview;
        private readonly Action _liveUpdate;   // the real lyrics window, while dragging
        private readonly Action _done;         // after a drag: save, refresh everything
        private readonly Action _modeChanged;  // a drag turned the standard layout into a custom one
        private readonly Dictionary<Part, Item> _items = new();
        private readonly Line _guideX = Guide(), _guideY = Guide();
        private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

        private Part? _selected;
        private Part? _dragging;
        private LayoutMaths.Grip _grip;
        private Point _start;
        private LayoutBox _startBox = new();
        private bool _moved, _convertOnMove, _converted;
        private double _scale = 1; // how much the preview is zoomed, so handles stay a comfortable size

        public LayoutEditor(Settings settings, LivePreview preview, Action liveUpdate, Action done, Action modeChanged)
        {
            _s = settings;
            _preview = preview;
            _liveUpdate = liveUpdate;
            _done = done;
            _modeChanged = modeChanged;

            Background = Brushes.Transparent; // catches clicks on empty space (to deselect)
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = false;

            foreach (Part p in Enum.GetValues(typeof(Part))) _items[p] = MakeItem(p);
            Children.Add(_guideX);
            Children.Add(_guideY);

            Grid.SetRowSpan(this, 2);
            preview.ContentArea.Children.Add(this);
            preview.LayoutApplied += Redraw;
            SizeChanged += (_, _) => Redraw();

            MouseLeftButtonDown += (_, e) => { Select(null); Focus(); e.Handled = true; };
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            LostMouseCapture += (_, _) => EndDrag();
            KeyDown += OnKeyDown;
            _liveTimer.Tick += (_, _) => { _liveTimer.Stop(); _liveUpdate(); };
        }

        private static Line Guide() => new()
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0x4F, 0xA3)),
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        private static string NameOf(Part p) => p switch
        {
            Part.Lyrics => "Lyrics",
            Part.Title => "Title and artist",
            Part.Art => "Album cover",
            _ => "Profile picture",
        };

        // ----- Building the outlines -----

        private Item MakeItem(Part part)
        {
            var item = new Item { Part = part };
            item.Outline = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF)), // nearly clear, but clickable
                Cursor = Cursors.SizeAll,
            };
            item.LabelText = new TextBlock { Text = NameOf(part), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold };
            item.Label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = item.LabelText,
                Cursor = Cursors.SizeAll,
            };
            item.Root = new Grid();
            item.Root.Children.Add(item.Outline);
            item.Root.Children.Add(item.Label);

            void AddHandle(LayoutMaths.Grip grip, HorizontalAlignment h, VerticalAlignment v, Cursor cursor)
            {
                var handle = new Rectangle
                {
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Accent),
                    HorizontalAlignment = h,
                    VerticalAlignment = v,
                    Cursor = cursor,
                    Visibility = Visibility.Collapsed,
                };
                handle.MouseLeftButtonDown += (_, e) => { StartDrag(part, grip, e); e.Handled = true; };
                item.Handles.Add((handle, grip));
                item.Root.Children.Add(handle);
            }
            var G = LayoutMaths.Grip.Left; var T = LayoutMaths.Grip.Top; var R = LayoutMaths.Grip.Right; var B = LayoutMaths.Grip.Bottom;
            AddHandle(G | T, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE);
            AddHandle(R | B, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
            AddHandle(R | T, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW);
            AddHandle(G | B, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW);
            AddHandle(G, HorizontalAlignment.Left, VerticalAlignment.Center, Cursors.SizeWE);
            AddHandle(R, HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE);
            AddHandle(T, HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.SizeNS);
            AddHandle(B, HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS);

            item.Outline.MouseLeftButtonDown += (_, e) => { StartDrag(part, LayoutMaths.Grip.Move, e); e.Handled = true; };
            item.Label.MouseLeftButtonDown += (_, e) => { StartDrag(part, LayoutMaths.Grip.Move, e); e.Handled = true; };
            item.Root.ToolTip = $"{NameOf(part)}: drag to move, drag the edges or corners to resize";
            Children.Add(item.Root);
            return item;
        }

        /// <summary>Which parts are showing (hidden ones can't be placed).</summary>
        private bool Present(Part p) => p switch
        {
            Part.Title => _s.ShowTrackInfo,
            Part.Art => _s.ShowAlbumArt,
            Part.Profile => _s.ProfilePicture != "Off",
            _ => true,
        };

        private static LayoutBox BoxOf(LayoutData l, Part p) => p switch
        {
            Part.Lyrics => l.Lyrics,
            Part.Title => l.Title,
            Part.Art => l.Art,
            _ => l.Profile,
        };

        private static void SetBox(LayoutData l, Part p, LayoutBox b)
        {
            switch (p)
            {
                case Part.Lyrics: l.Lyrics = b; break;
                case Part.Title: l.Title = b; break;
                case Part.Art: l.Art = b; break;
                default: l.Profile = b; break;
            }
        }

        /// <summary>The boxes as they are on screen: the custom layout, or the standard one measured from the preview.</summary>
        private LayoutData Current() => _s.Layout.Custom ? _s.Layout : _preview.MeasureLayout();

        /// <summary>Moves the outlines to match the preview.</summary>
        public void Redraw()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            try
            {
                var root = PresentationSource.FromVisual(this)?.RootVisual as Visual;
                if (root != null)
                {
                    // How big 1 unit here is on screen: map a unit-long line and measure it.
                    var t = TransformToAncestor(root);
                    var a = t.Transform(new Point(0, 0));
                    var b = t.Transform(new Point(1, 0));
                    _scale = Math.Max(0.2, (b - a).Length);
                }
            }
            catch (InvalidOperationException) { }

            var layout = Current();
            foreach (var (part, item) in _items)
            {
                bool show = Present(part);
                item.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) continue;
                var b = BoxOf(layout, part).Clamped(0.02);
                SetLeft(item.Root, b.X * w);
                SetTop(item.Root, b.Y * h);
                item.Root.Width = Math.Max(4, b.W * w);
                item.Root.Height = Math.Max(4, b.H * h);

                // Constant on-screen sizes however much the preview is zoomed.
                double px = 1 / _scale;
                bool selected = _selected == part;
                item.Outline.StrokeThickness = (selected ? 2 : 1.2) * px;
                item.Outline.StrokeDashArray = selected ? null : new DoubleCollection { 4, 3 };
                item.Outline.Stroke = new SolidColorBrush(selected ? Accent : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                item.Outline.Fill = new SolidColorBrush(selected ? Color.FromArgb(0x24, Accent.R, Accent.G, Accent.B) : Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));
                item.LabelText.FontSize = 10.5 * px;
                item.Label.Padding = new Thickness(5 * px, 1.5 * px, 5 * px, 2 * px);
                item.Label.CornerRadius = new CornerRadius(3 * px);
                item.Label.Background = new SolidColorBrush(selected ? Color.FromArgb(0xE6, 0x13, 0x8A, 0x40) : Color.FromArgb(0xC8, 0x10, 0x10, 0x10));
                foreach (var (handle, _) in item.Handles)
                {
                    handle.Width = handle.Height = 8 * px;
                    handle.StrokeThickness = 1.2 * px;
                    handle.Margin = new Thickness(-4 * px);
                    handle.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            // The selected part on top, so its handles can always be reached.
            if (_selected is Part sel && _items[sel].Root.Visibility == Visibility.Visible)
            {
                Children.Remove(_items[sel].Root);
                Children.Insert(Children.Count - 2, _items[sel].Root); // below the guide lines
            }
            _guideX.StrokeThickness = _guideY.StrokeThickness = 1.2 / _scale;
        }

        private void Select(Part? part)
        {
            _selected = part;
            Redraw();
        }

        // ----- Dragging -----

        private void StartDrag(Part part, LayoutMaths.Grip grip, MouseButtonEventArgs e)
        {
            Focus();
            _selected = part;
            _dragging = part;
            _grip = grip;
            _start = e.GetPosition(this);
            _moved = false;
            _converted = false;
            _convertOnMove = !_s.Layout.Custom;
            _startBox = BoxOf(Current(), part).Copy();
            CaptureMouse();
            Redraw();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging is not Part part || !IsMouseCaptured) return;
            var p = e.GetPosition(this);
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (!_moved && Math.Abs(p.X - _start.X) < 3 && Math.Abs(p.Y - _start.Y) < 3) return;
            _moved = true;

            // The first real drag on the standard layout starts a custom one, exactly as it looked.
            if (_convertOnMove)
            {
                var measured = _preview.MeasureLayout();
                measured.Custom = true;
                measured.HasCustom = true;
                _s.Layout = measured;
                _startBox = BoxOf(measured, part).Copy();
                _convertOnMove = false;
                _converted = true;
            }

            var others = _items.Keys.Where(o => o != part && Present(o)).Select(o => BoxOf(_s.Layout, o));
            var (gx, gy) = LayoutMaths.Guides(others);
            bool free = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            double snapX = free ? 0 : 7 / _scale / w, snapY = free ? 0 : 7 / _scale / h;
            double min = part == Part.Lyrics ? 0.12 : 0.05;
            var box = LayoutMaths.Drag(_startBox, _grip, (p.X - _start.X) / w, (p.Y - _start.Y) / h, gx, gy, snapX, snapY, min,
                                       out double sx, out double sy);
            SetBox(_s.Layout, part, box);
            ShowGuides(sx, sy);
            _preview.RefreshLayout();
            if (!_liveTimer.IsEnabled) _liveTimer.Start();
        }

        private void ShowGuides(double x, double y)
        {
            double w = ActualWidth, h = ActualHeight, extra = 6 / _scale;
            if (double.IsNaN(x)) _guideX.Visibility = Visibility.Collapsed;
            else
            {
                _guideX.X1 = _guideX.X2 = x * w;
                _guideX.Y1 = -extra; _guideX.Y2 = h + extra;
                _guideX.Visibility = Visibility.Visible;
            }
            if (double.IsNaN(y)) _guideY.Visibility = Visibility.Collapsed;
            else
            {
                _guideY.Y1 = _guideY.Y2 = y * h;
                _guideY.X1 = -extra; _guideY.X2 = w + extra;
                _guideY.Visibility = Visibility.Visible;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging == null) return;
            e.Handled = true;
            ReleaseMouseCapture(); // → EndDrag
        }

        private void EndDrag()
        {
            if (_dragging == null) return;
            _dragging = null;
            _guideX.Visibility = _guideY.Visibility = Visibility.Collapsed;
            _liveTimer.Stop();
            if (!_moved) return;
            _done();
            if (_converted) _modeChanged();
        }

        // ----- Keyboard -----

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Select(null); e.Handled = true; return; }
            if (_selected is not Part part) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.02 : 0.005;
            double dx = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            double dy = e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 };
            if (dx == 0 && dy == 0) return;
            e.Handled = true;

            bool converted = false;
            if (!_s.Layout.Custom)
            {
                var measured = _preview.MeasureLayout();
                measured.Custom = true;
                measured.HasCustom = true;
                _s.Layout = measured;
                converted = true;
            }
            var b = BoxOf(_s.Layout, part);
            // Ctrl + arrows resize instead of moving.
            var moved = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? new LayoutBox(b.X, b.Y, b.W + dx, b.H + dy)
                : new LayoutBox(b.X + dx, b.Y + dy, b.W, b.H);
            SetBox(_s.Layout, part, moved.Clamped(part == Part.Lyrics ? 0.12 : 0.05));
            _preview.RefreshLayout();
            _done();
            if (converted) _modeChanged();
        }
    }
}
