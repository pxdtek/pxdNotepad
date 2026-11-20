using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pxdNotepad.Images;

/// <summary>
/// One edit to an image, in the image's own pixels: an arrow or box drawn on top, or a blur, pixelation or cut-out
/// of the picture itself (see PhotoEdits).
/// </summary>
public sealed class Mark
{
    public string Kind { get; set; } = "arrow";   // "arrow" | "box" | "blur" | "pixel" | "cut"
    /// <summary>A cut's direction: "rows" (takes out the rows its box spans) or "cols".</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Dir { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string Color { get; set; } = "#FF3B30";
    public double Width { get; set; } = 6;

    public Mark Clone() => (Mark)MemberwiseClone();

    public static List<Mark> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Mark>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string ToJson(List<Mark> marks) => marks.Count == 0 ? "" : JsonSerializer.Serialize(marks);
}

public static class MarkDrawing
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    private static SolidColorBrush Brush(string hex)
    {
        if (!Cache.TryGetValue(hex, out var b))
        {
            try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { b = new SolidColorBrush(Colors.Red); }
            b.Freeze();
            Cache[hex] = b;
        }
        return b;
    }

    /// <summary>Draws a mark, with a faint dark edge so it shows up on light and dark pictures alike.</summary>
    public static void Draw(DrawingContext dc, Mark m)
    {
        if (PhotoEdits.IsPhoto(m)) return;   // part of the picture, not drawn on top
        var brush = Brush(m.Color);
        var edge = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        edge.Freeze();
        if (m.Kind == "box")
        {
            var r = new Rect(new Point(m.X1, m.Y1), new Point(m.X2, m.Y2));
            var radius = Math.Min(m.Width * 1.2, Math.Min(r.Width, r.Height) / 4);
            dc.DrawRoundedRectangle(null, new Pen(edge, m.Width + 2) { LineJoin = PenLineJoin.Round }, r, radius, radius);
            dc.DrawRoundedRectangle(null, new Pen(brush, m.Width) { LineJoin = PenLineJoin.Round }, r, radius, radius);
            return;
        }
        var geo = Arrow(m);
        dc.DrawGeometry(null, new Pen(edge, 2) { LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(brush, null, geo);
    }

    /// <summary>An arrow as one filled outline: a shaft of the mark's width and a head at (X2, Y2).</summary>
    public static Geometry Arrow(Mark m)
    {
        var a = new Point(m.X1, m.Y1);
        var b = new Point(m.X2, m.Y2);
        var v = b - a;
        var len = v.Length;
        if (len < 0.5) return Geometry.Empty;
        v /= len;
        var n = new Vector(-v.Y, v.X);
        var head = Math.Min(len * 0.6, Math.Max(14, m.Width * 4.2));
        var headHalf = Math.Max(8, m.Width * 2.6) * Math.Min(1, len * 0.6 / head + 0.2);
        var half = m.Width / 2;
        var neck = b - v * head;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a + n * half, true, true);
            ctx.LineTo(neck + n * half, true, false);
            ctx.LineTo(neck + n * headHalf, true, false);
            ctx.LineTo(b, true, false);
            ctx.LineTo(neck - n * headHalf, true, false);
            ctx.LineTo(neck - n * half, true, false);
            ctx.LineTo(a - n * half, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The picture with its marks drawn in, at full size.</summary>
    public static BitmapSource Flatten(BitmapSource original, List<Mark> marks)
    {
        var photo = PhotoEdits.Apply(original, marks);   // blurs, pixelations, cut-outs
        int w = photo.PixelWidth, h = photo.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(photo, new Rect(0, 0, w, h));
            foreach (var m in marks) Draw(dc, m);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Distance from a point to a mark's line or outline (for picking one to move or delete).</summary>
    public static double Distance(Mark m, Point p)
    {
        if (m.Kind is "blur" or "pixel")
        {
            var r = new Rect(new Point(m.X1, m.Y1), new Point(m.X2, m.Y2));
            var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
            var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
            return Math.Sqrt(dx * dx + dy * dy);   // anywhere inside picks it
        }
        if (m.Kind == "box")
        {
            var r = new Rect(new Point(m.X1, m.Y1), new Point(m.X2, m.Y2));
            var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
            var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
            if (dx > 0 || dy > 0) return Math.Sqrt(dx * dx + dy * dy);
            return Math.Min(Math.Min(p.X - r.Left, r.Right - p.X), Math.Min(p.Y - r.Top, r.Bottom - p.Y));
        }
        var a = new Point(m.X1, m.Y1);
        var b = new Point(m.X2, m.Y2);
        var ab = b - a;
        var t = ab.LengthSquared < 1e-6 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

/// <summary>The drawing surface over the picture, sized in the picture's pixels (a Viewbox fits it to the window).</summary>
public sealed class MarkLayer : FrameworkElement
{
    public List<Mark> Marks { get; set; } = [];
    public Mark? Selected { get; set; }
    public string Tool { get; set; } = "arrow";   // arrow | box | blur | pixel | cut | select
    public string Color { get; set; } = "#FF3B30";
    public double StrokeWidth { get; set; } = 6;
    public event Action? BeforeChange;
    public event Action? AfterChange;

    private Mark? _drawing;
    private Point _last;
    private bool _moving;

    public MarkLayer()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    private static readonly Brush AreaFill = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 0x39, 0xC5, 0xCF)));
    private static readonly Brush CutFill = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 0xFF, 0x3B, 0x30)));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Blur, pixelate and cut-out marks that can be picked and moved: those made after the last cut (earlier ones are in the picture's size from before it).</summary>
    public bool Pickable(Mark m)
    {
        if (!PhotoEdits.IsPhoto(m)) return true;
        if (m.Kind == "cut") return false;
        return Marks.IndexOf(m) > Marks.FindLastIndex(x => x.Kind == "cut");
    }

    /// <summary>Where a blur or pixelate box is going (while drawn or moved), and the band a cut will take out.</summary>
    private void Preview(DrawingContext dc, Mark m)
    {
        var pen = new Pen(Brushes.White, 1.5 / Scale()) { DashStyle = new DashStyle([4, 3], 0) };
        if (m.Kind == "cut")
        {
            var (from, to, rows) = PhotoEdits.Band(m, (int)Math.Round(ActualWidth), (int)Math.Round(ActualHeight));
            var band = rows ? new Rect(0, from, ActualWidth, Math.Max(0, to - from)) : new Rect(from, 0, Math.Max(0, to - from), ActualHeight);
            dc.DrawRectangle(CutFill, pen, band);
            return;
        }
        dc.DrawRectangle(AreaFill, pen, new Rect(new Point(m.X1, m.Y1), new Point(m.X2, m.Y2)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        foreach (var m in Marks) MarkDrawing.Draw(dc, m);
        if (_moving && Selected is { } moving && PhotoEdits.IsPhoto(moving)) Preview(dc, moving);
        if (_drawing is not null)
        {
            if (PhotoEdits.IsPhoto(_drawing)) Preview(dc, _drawing);
            else MarkDrawing.Draw(dc, _drawing);
        }
        if (Selected is not null)
        {
            var r = new Rect(new Point(Selected.X1, Selected.Y1), new Point(Selected.X2, Selected.Y2));
            r.Inflate(Selected.Width + 6, Selected.Width + 6);
            var scale = Scale();
            var pen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0xC5, 0xCF)), 1.5 / scale) { DashStyle = new DashStyle([4, 3], 0) };
            dc.DrawRectangle(null, pen, r);
        }
    }

    /// <summary>How much the Viewbox shrinks the picture (so the selection outline stays thin).</summary>
    private double Scale()
    {
        try
        {
            var w = PresentationSource.FromVisual(this) is null ? 1 : PointToScreen(new Point(100, 0)).X - PointToScreen(new Point(0, 0)).X;
            return Math.Max(0.05, w / 100);
        }
        catch
        {
            return 1;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        StartAt(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>
    /// A press at a point in the picture's pixels; outside the picture (the space around it) counts as its nearest
    /// edge, so a box can be started from beyond the picture and still reach right to its edge.
    /// </summary>
    public void StartAt(Point at)
    {
        Focus();
        var p = Clamp(at);
        _last = p;
        if (Tool == "select")
        {
            var tol = Math.Max(10 / Scale(), 4);
            Selected = Marks.LastOrDefault(m => Pickable(m) && MarkDrawing.Distance(m, p) <= (PhotoEdits.IsPhoto(m) ? 0 : m.Width / 2 + tol));
            if (Selected is not null)
            {
                BeforeChange?.Invoke();
                _moving = true;
                CaptureMouse();
            }
            InvalidateVisual();
            return;
        }
        Selected = null;
        _drawing = new Mark { Kind = Tool, X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Color = Color, Width = StrokeWidth };
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e) => DragTo(e.GetPosition(this));

    /// <summary>The mouse moved to a point in the picture's pixels (kept inside the picture).</summary>
    public void DragTo(Point at)
    {
        var p = Clamp(at);
        if (_drawing is not null)
        {
            _drawing.X2 = p.X;
            _drawing.Y2 = p.Y;
            if (_drawing.Kind == "cut")
            {
                // by the box's shape (wide: rows, tall: columns); Shift takes the other way
                var wide = Math.Abs(p.X - _drawing.X1) >= Math.Abs(p.Y - _drawing.Y1);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) wide = !wide;
                _drawing.Dir = wide ? "rows" : "cols";
            }
            if (_drawing.Kind == "box" && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var s = Math.Max(Math.Abs(p.X - _drawing.X1), Math.Abs(p.Y - _drawing.Y1));
                _drawing.X2 = _drawing.X1 + Math.Sign(p.X - _drawing.X1) * s;
                _drawing.Y2 = _drawing.Y1 + Math.Sign(p.Y - _drawing.Y1) * s;
            }
            InvalidateVisual();
        }
        else if (_moving && Selected is not null)
        {
            var d = p - _last;
            _last = p;
            Selected.X1 += d.X; Selected.X2 += d.X;
            Selected.Y1 += d.Y; Selected.Y2 += d.Y;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Finish();

    /// <summary>The button went up: what was drawn or moved is kept.</summary>
    public void Finish()
    {
        ReleaseMouseCapture();
        if (_drawing is not null)
        {
            var m = _drawing;
            _drawing = null;
            if (m.Kind == "cut") AddCut(m);
            else if (Math.Abs(m.X2 - m.X1) + Math.Abs(m.Y2 - m.Y1) > 6
                     && (!PhotoEdits.IsPhoto(m) || (Math.Abs(m.X2 - m.X1) >= 2 && Math.Abs(m.Y2 - m.Y1) >= 2)))
                Add(m);
            InvalidateVisual();
        }
        else if (_moving)
        {
            _moving = false;
            AfterChange?.Invoke();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.Key == Key.Delete || e.Key == Key.Back) && Selected is not null)
        {
            BeforeChange?.Invoke();
            Marks.Remove(Selected);
            Selected = null;
            AfterChange?.Invoke();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>Adds an edit (undoable).</summary>
    public void Add(Mark m)
    {
        BeforeChange?.Invoke();
        Marks.Add(m);
        AfterChange?.Invoke();
        InvalidateVisual();
    }

    /// <summary>A cut: the band goes, the picture closes up, and arrows and boxes below or right of it move up or left with it.</summary>
    public bool AddCut(Mark cut)
    {
        int w = (int)Math.Round(Width), h = (int)Math.Round(Height);
        if (!PhotoEdits.CutLeavesEnough(cut, w, h)) return false;
        cut.Dir ??= PhotoEdits.CutDir(cut);
        BeforeChange?.Invoke();
        var (from, to, rows) = PhotoEdits.Band(cut, w, h);
        foreach (var m in Marks.Where(m => !PhotoEdits.IsPhoto(m))) PhotoEdits.Follow(m, from, to, rows);
        Marks.Add(cut);
        Selected = null;
        AfterChange?.Invoke();
        return true;
    }

    private Point Clamp(Point p)
    {
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        return new(Math.Clamp(p.X, 0, double.IsNaN(w) ? 0 : w), Math.Clamp(p.Y, 0, double.IsNaN(h) ? 0 : h));
    }
}
