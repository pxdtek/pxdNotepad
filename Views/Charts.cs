using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace pxdNotepad.Views;

/// <summary>
/// A horizontal bar split into parts (what a total is made of), or a meter (one part of a larger total). Thin, with
/// 4 px rounded ends and a 2 px gap of the page colour between parts; tiny parts still show as a sliver.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public IReadOnlyList<(double Value, string BrushKey)> Parts { get; set; } = [];
    /// <summary>The whole the bar stands for; larger than the parts' sum leaves an empty track (a meter).</summary>
    public double Total { get; set; }

    public SegmentBar() { Height = 12; SnapsToDevicePixels = true; }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var total = Math.Max(Total, Parts.Sum(p => p.Value));
        dc.DrawRoundedRectangle((Brush)FindResource("Panel2"), null, new Rect(0, 0, w, h), 4, 4);
        if (total <= 0 || w <= 0) return;
        var page = (Brush)FindResource("WindowBg");
        double x = 0;
        var shown = Parts.Where(p => p.Value > 0).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var pw = Math.Max(3, w * shown[i].Value / total);
            if (x + pw > w) pw = w - x;
            if (pw <= 0) break;
            var r = new Rect(x, 0, pw, h);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 4, 4));
            dc.DrawRectangle((Brush)FindResource(shown[i].BrushKey), null, r);
            dc.Pop();
            x += pw;
            if (i < shown.Count - 1) { dc.DrawRectangle(page, null, new Rect(x, 0, 2, h)); x += 2; }
        }
    }
}

/// <summary>
/// A small line chart over time: one axis, hairline gridlines, 2 px lines, an end dot with its value, a soft area for a
/// single series, and a crosshair + tooltip on hover. The legend (for two or more series) is drawn by whoever hosts it.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public sealed record Series(string Name, string BrushKey, IReadOnlyList<(DateTime X, double Y)> Points);

    public IReadOnlyList<Series> Data { get; set; } = [];
    public Func<double, string> Format { get; set; } = v => v.ToString("N0", CultureInfo.CurrentCulture);
    /// <summary>Shown in the middle when there's nothing to draw yet.</summary>
    public string Empty { get; set; } = "Nothing to show yet";
    /// <summary>Values are bytes: the axis steps are round in KB / MB / GB, not in raw bytes.</summary>
    public bool Bytes { get; set; }

    private Point? _hover;
    private const double Left = 56, Right = 84, Top = 10, Bottom = 22;

    public LineChart()
    {
        Height = 170;
        Cursor = Cursors.Cross;
        MouseMove += (_, e) => { _hover = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
    }

    private FormattedText Text(string s, double size, string brushKey, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, (Brush)FindResource(brushKey), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>A round top for the axis: 1, 2 or 5 times a power of ten.</summary>
    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 5, 10 }) if (m * p >= v) return m * p;
        return 10 * p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hover anywhere on it
        var all = Data.SelectMany(s => s.Points).ToList();
        if (all.Count == 0)
        {
            var t = Text(Empty, 12, "Faint");
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            return;
        }
        var x0 = all.Min(p => p.X);
        var x1 = all.Max(p => p.X);
        if ((x1 - x0).TotalDays < 1) { x0 = x0.AddDays(-1); x1 = x1.AddDays(1); }
        var max = all.Max(p => p.Y) * 1.08;
        double top;
        if (Bytes)
        {
            var unit = 1.0;
            while (max / unit >= 1024) unit *= 1024;
            top = Nice(max / unit) * unit;
        }
        else top = Nice(max);
        var plot = new Rect(Left, Top, Math.Max(10, w - Left - Right), Math.Max(10, h - Top - Bottom));
        double X(DateTime d) => plot.Left + plot.Width * (d - x0).TotalSeconds / (x1 - x0).TotalSeconds;
        double Y(double v) => plot.Bottom - plot.Height * v / top;

        // gridlines + y ticks (0, half, top), x ends
        var grid = new Pen((Brush)FindResource("ChartGrid"), 1);
        grid.Freeze();
        foreach (var v in new[] { 0, top / 2, top })
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(Format(v), 10.5, "Faint");
            dc.DrawText(t, new Point(plot.Left - 8 - t.Width, y - t.Height / 2));
        }
        var a = Text(x0.ToString("MMM d", CultureInfo.CurrentCulture), 10.5, "Faint");
        var b = Text(x1.ToString("MMM d", CultureInfo.CurrentCulture), 10.5, "Faint");
        dc.DrawText(a, new Point(plot.Left, plot.Bottom + 5));
        dc.DrawText(b, new Point(plot.Right - b.Width, plot.Bottom + 5));

        var ring = (Brush)FindResource("WindowBg");
        foreach (var s in Data)
        {
            if (s.Points.Count == 0) continue;
            var brush = (Brush)FindResource(s.BrushKey);
            var pts = s.Points.OrderBy(p => p.X).Select(p => new Point(X(p.X), Y(p.Y))).ToList();
            if (pts.Count > 1)
            {
                if (Data.Count == 1 && brush is SolidColorBrush sc)
                {
                    // a soft wash under a single line
                    var area = new StreamGeometry();
                    using (var g = area.Open())
                    {
                        g.BeginFigure(new Point(pts[0].X, plot.Bottom), true, true);
                        foreach (var p in pts) g.LineTo(p, false, false);
                        g.LineTo(new Point(pts[^1].X, plot.Bottom), false, false);
                    }
                    var wash = new SolidColorBrush(Color.FromArgb(26, sc.Color.R, sc.Color.G, sc.Color.B));
                    dc.DrawGeometry(wash, null, area);
                }
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(pts[0], false, false);
                    foreach (var p in pts.Skip(1)) g.LineTo(p, true, true);
                }
                dc.DrawGeometry(null, new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
            }
            // the end: dot with a ring, and its value
            var end = pts[^1];
            dc.DrawEllipse(brush, new Pen(ring, 2), end, 4.5, 4.5);
            var label = Text(Format(s.Points.OrderBy(p => p.X).Last().Y), 11.5, "Text", bold: true);
            dc.DrawText(label, new Point(end.X + 9, end.Y - label.Height / 2));
        }

        // hover: crosshair at the nearest day that has a value, and a small card with each series' value there
        if (_hover is { } m && plot.Contains(new Point(Math.Clamp(m.X, plot.Left, plot.Right), m.Y)))
        {
            var day = x0 + TimeSpan.FromSeconds((Math.Clamp(m.X, plot.Left, plot.Right) - plot.Left) / plot.Width * (x1 - x0).TotalSeconds);
            var near = all.OrderBy(p => Math.Abs((p.X - day).TotalSeconds)).First().X;
            var cx = Math.Round(X(near)) + 0.5;
            dc.DrawLine(new Pen((Brush)FindResource("Faint"), 1), new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            var lines = new List<(FormattedText T, Brush? Swatch)> { (Text(near.ToString("ddd, MMM d", CultureInfo.CurrentCulture), 11.5, "Dim"), null) };
            foreach (var s in Data)
            {
                var hit = s.Points.Where(p => p.X.Date == near.Date).Select(p => (double?)p.Y).LastOrDefault();
                if (hit is null) continue;
                lines.Add((Text($"{s.Name}  {Format(hit.Value)}", 12, "Text", bold: true), (Brush)FindResource(s.BrushKey)));
                dc.DrawEllipse((Brush)FindResource(s.BrushKey), new Pen(ring, 2), new Point(cx, Y(hit.Value)), 4, 4);
            }
            var cw = lines.Max(l => l.T.Width + (l.Swatch is null ? 0 : 16)) + 20;
            var ch = lines.Sum(l => l.T.Height + 2) + 14;
            var cxCard = cx + 12 + cw > w ? cx - 12 - cw : cx + 12;
            var card = new Rect(cxCard, plot.Top, cw, ch);
            dc.DrawRoundedRectangle((Brush)FindResource("Panel2"), new Pen((Brush)FindResource("Line"), 1), card, 6, 6);
            var ty = card.Top + 7;
            foreach (var (t, sw) in lines)
            {
                var tx = card.Left + 10;
                if (sw is not null) { dc.DrawRoundedRectangle(sw, null, new Rect(tx, ty + t.Height / 2 - 4, 10, 8), 2, 2); tx += 16; }
                dc.DrawText(t, new Point(tx, ty));
                ty += t.Height + 2;
            }
        }
    }
}
