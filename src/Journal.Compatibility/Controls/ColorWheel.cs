using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Jornal.Desktop.Controls;

// Кольцо оттенка и треугольник HSV. Растры кэшируются: S/V перемещает только указатель.
public sealed class ColorWheel : Control
{
    public static readonly StyledProperty<HsvColor> HsvColorProperty = AvaloniaProperty.Register<ColorWheel, HsvColor>(
        nameof(HsvColor), new HsvColor(1, 0, 0, 1), defaultBindingMode: BindingMode.TwoWay);
    private WriteableBitmap? _ring, _triangle;
    private int _pixels;
    private double _triangleHue = double.NaN;
    private DragPart _drag;
    private enum DragPart { None, Hue, Triangle }
    static ColorWheel()
    {
        FocusableProperty.OverrideDefaultValue<ColorWheel>(true);
        AffectsRender<ColorWheel>(HsvColorProperty, IsFocusedProperty);
    }
    public HsvColor HsvColor { get => GetValue(HsvColorProperty); set => SetValue(HsvColorProperty, value); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side < 32) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var pixels = Math.Clamp((int)Math.Ceiling(side * scaling), 32, 1024);
        if (_pixels != pixels)
        { DisposeImages(); _pixels = pixels; _ring = CreateBitmap(pixels, null); }
        if (_triangle is null || _triangleHue != HsvColor.H)
        { _triangle?.Dispose(); _triangle = CreateBitmap(pixels, HsvColor.H); _triangleHue = HsvColor.H; }
        using var transform = context.PushTransform(Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2));
        context.DrawImage(_ring!, new Rect(new Size(side, side))); context.DrawImage(_triangle, new Rect(new Size(side, side)));
        var geometry = Geometry.For(side, HsvColor.H);
        var weights = new Weights(HsvColor.V * HsvColor.S, HsvColor.V * (1 - HsvColor.S), 1 - HsvColor.V);
        Marker(context, geometry.Point(weights), 4.5);
        Marker(context, Polar(side / 2, geometry.OuterRadius - geometry.RingWidth / 2, HsvColor.H), 4.5);
        if (IsFocused && this.TryFindResource("JournalAccent", ActualThemeVariant, out var accent) && accent is IBrush brush)
            context.DrawEllipse(null, new Pen(brush, 1.5), new Point(side / 2, side / 2), side / 2 - 1, side / 2 - 1);
    }
    private static void Marker(DrawingContext context, Point at, double radius)
    {
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), at, radius, radius);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.5), at, radius, radius);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = LocalPoint(e.GetPosition(this), out var geometry);
        var distance = Length(at - geometry.Center);
        _drag = distance >= geometry.OuterRadius - geometry.RingWidth - 2 && distance <= geometry.OuterRadius + 3 ? DragPart.Hue :
            geometry.Weights(at).Inside ? DragPart.Triangle : DragPart.None;
        if (_drag == DragPart.None) return;
        Focus(); e.Pointer.Capture(this); UpdatePointer(at, geometry); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragPart.None || !IsEffectivelyEnabled) return;
        var at = LocalPoint(e.GetPosition(this), out var geometry); UpdatePointer(at, geometry); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragPart.None) return;
        _drag = DragPart.None; e.Pointer.Capture(null); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { _drag = DragPart.None; base.OnPointerCaptureLost(e); }
    private Point LocalPoint(Point at, out Geometry geometry)
    {
        var side = Math.Max(32, Math.Min(Bounds.Width, Bounds.Height)); geometry = Geometry.For(side, HsvColor.H);
        return at - new Vector((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
    }
    private void UpdatePointer(Point at, Geometry geometry)
    {
        if (_drag == DragPart.Hue)
        {
            var delta = at - geometry.Center;
            if (Length(delta) > 1) SelectColor(new(1, Normalize(Math.Atan2(-delta.Y, delta.X) * 180 / Math.PI), HsvColor.S, HsvColor.V));
        }
        else
        {
            var weights = geometry.Weights(geometry.ClosestPoint(at));
            var value = Math.Clamp(weights.Hue + weights.White, 0, 1);
            SelectColor(new(1, HsvColor.H, value < 1e-9 ? HsvColor.S : Math.Clamp(weights.Hue / value, 0, 1), value));
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        var hsv = HsvColor; var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left: case Key.Right:
                var step = e.Key == Key.Right ? 1 : -1;
                SelectColor(shift ? new(1, hsv.H, Math.Clamp(hsv.S + step * .01, 0, 1), hsv.V) : new(1, Normalize(hsv.H + step), hsv.S, hsv.V)); break;
            case Key.Up: case Key.Down:
                SelectColor(new(1, hsv.H, hsv.S, Math.Clamp(hsv.V + (e.Key == Key.Up ? .01 : -.01), 0, 1))); break;
            default: return;
        }
        e.Handled = true;
    }
    private static double Normalize(double hue) => (hue % 360 + 360) % 360;
    private void SelectColor(HsvColor color) => SetCurrentValue(HsvColorProperty, color);
    private static double Length(Point delta) => Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
    private static double DistanceSquared(Point first, Point second) => Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2);
    private static Point Polar(double center, double radius, double angle) => new(center + radius * Math.Cos(angle * Math.PI / 180), center - radius * Math.Sin(angle * Math.PI / 180));
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _drag = DragPart.None; DisposeImages(); base.OnDetachedFromVisualTree(e); }
    private void DisposeImages() { _ring?.Dispose(); _triangle?.Dispose(); _ring = _triangle = null; _pixels = 0; }
    private static WriteableBitmap CreateBitmap(int side, double? hue)
    {
        var pixels = new int[side * side]; var geometry = Geometry.For(side, hue ?? 0);
        var pure = new HsvColor(1, hue ?? 0, 1, 1).ToRgb();
        for (var y = 0; y < side; y++) for (var x = 0; x < side; x++)
        {
            var point = new Point(x + .5, y + .5); double coverage, r, g, b;
            if (hue is null)
            {
                var delta = point - geometry.Center; var distance = Length(delta);
                coverage = Math.Clamp(Math.Min(geometry.OuterRadius - distance, distance - geometry.OuterRadius + geometry.RingWidth) + .5, 0, 1);
                if (coverage == 0) continue;
                var color = new HsvColor(1, Normalize(Math.Atan2(-delta.Y, delta.X) * 180 / Math.PI), 1, 1).ToRgb();
                r = color.R; g = color.G; b = color.B;
            }
            else
            {
                var weights = geometry.Weights(point);
                coverage = Math.Clamp(Math.Min(weights.Hue, Math.Min(weights.White, weights.Black)) * geometry.Altitude + .5, 0, 1);
                if (coverage == 0) continue;
                var h = Math.Max(0, weights.Hue); var w = Math.Max(0, weights.White); var black = Math.Max(0, weights.Black); var sum = h + w + black;
                r = (h * pure.R + w * 255) / sum; g = (h * pure.G + w * 255) / sum; b = (h * pure.B + w * 255) / sum;
            }
            pixels[y * side + x] = unchecked((int)(((uint)Math.Round(coverage * 255) << 24) | ((uint)Math.Round(r * coverage) << 16) |
                ((uint)Math.Round(g * coverage) << 8) | (uint)Math.Round(b * coverage)));
        }
        var bitmap = new WriteableBitmap(new PixelSize(side, side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        for (var y = 0; y < side; y++) Marshal.Copy(pixels, y * side, buffer.Address + y * buffer.RowBytes, side);
        return bitmap;
    }
    private readonly record struct Weights(double Hue, double White, double Black) { public bool Inside => Hue >= -1e-9 && White >= -1e-9 && Black >= -1e-9; }
    private readonly record struct Geometry(Point Center, double OuterRadius, double RingWidth, Point Hue, Point White, Point Black)
    {
        public double Altitude => Length(Hue - Center) * 1.5;
        public static Geometry For(double side, double hue)
        {
            var center = side / 2; var outer = center - side * .02; var ring = side * .08; var radius = outer - ring - side * .025;
            return new(new Point(center, center), outer, ring, Polar(center, radius, hue), Polar(center, radius, hue + 120), Polar(center, radius, hue - 120));
        }
        public Point Point(Weights weights) => new(Hue.X * weights.Hue + White.X * weights.White + Black.X * weights.Black,
            Hue.Y * weights.Hue + White.Y * weights.White + Black.Y * weights.Black);
        public Weights Weights(Point point)
        {
            var denominator = (White.Y - Black.Y) * (Hue.X - Black.X) + (Black.X - White.X) * (Hue.Y - Black.Y);
            var h = ((White.Y - Black.Y) * (point.X - Black.X) + (Black.X - White.X) * (point.Y - Black.Y)) / denominator;
            var w = ((Black.Y - Hue.Y) * (point.X - Black.X) + (Hue.X - Black.X) * (point.Y - Black.Y)) / denominator;
            return new(h, w, 1 - h - w);
        }
        public Point ClosestPoint(Point point)
        {
            if (Weights(point).Inside) return point;
            return new[] { OnSegment(Hue, White, point), OnSegment(White, Black, point), OnSegment(Black, Hue, point) }.MinBy(candidate => DistanceSquared(candidate, point));
        }
        private static Point OnSegment(Point a, Point b, Point point)
        {
            var edge = new Vector(b.X - a.X, b.Y - a.Y);
            var t = Math.Clamp(((point.X - a.X) * edge.X + (point.Y - a.Y) * edge.Y) / edge.SquaredLength, 0, 1);
            return a + edge * t;
        }
    }
}
