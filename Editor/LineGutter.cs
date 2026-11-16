using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace pxdNotepad.Editor;

/// <summary>
/// Line numbers beside the editor. A "line" is a paragraph or a Shift+Enter line break (wrapped text keeps its
/// number). Each number is drawn at the size of the text that starts its line and sits on that text's baseline, so
/// a 32 pt heading gets a 32 pt number and an 11 pt line an 11 pt one.
/// </summary>
public sealed class LineGutter : FrameworkElement
{
    private readonly RichTextBox _rtb;
    private readonly List<TextPointer> _starts = [];
    private bool _dirty = true;
    private bool _queued;
    private double _dpi = 1;
    private static readonly Typeface Face = new(new FontFamily("Consolas, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public LineGutter(RichTextBox rtb)
    {
        _rtb = rtb;
        ClipToBounds = true;
        Width = 28;
        IsHitTestVisible = false;
        rtb.TextChanged += (_, _) => { _dirty = true; Queue(); };
        rtb.SelectionChanged += (_, _) => Queue();
        rtb.SizeChanged += (_, _) => Queue();
        rtb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Queue()));
        Loaded += (_, _) => { _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; Queue(); };
    }

    public void Reset()
    {
        _dirty = true;
        Queue();
    }

    /// <summary>Redraw once the editor has laid out its text (positions aren't known before then).</summary>
    private void Queue()
    {
        if (_queued || Visibility != Visibility.Visible) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _queued = false;
            InvalidateVisual();
        });
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == VisibilityProperty && Visibility == Visibility.Visible) Reset();
    }

    private void Rebuild()
    {
        _starts.Clear();
        Collect(_rtb.Document.Blocks);
        _dirty = false;
    }

    private void Collect(BlockCollection blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    _starts.Add(p.ContentStart);
                    CollectBreaks(p.Inlines);
                    break;
                case List l:
                    foreach (var li in l.ListItems) Collect(li.Blocks);
                    break;
                case Section s:
                    Collect(s.Blocks);
                    break;
                case Table t:
                    foreach (var g in t.RowGroups)
                        foreach (var r in g.Rows)
                            foreach (var c in r.Cells) Collect(c.Blocks);
                    break;
                case BlockUIContainer { Child: DateDivider }:
                    break;
                case BlockUIContainer u:
                    _starts.Add(u.ContentStart);
                    break;
            }
        }
    }

    private void CollectBreaks(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is LineBreak lb) _starts.Add(lb.ElementEnd);
            else if (i is Span s) CollectBreaks(s.Inlines);
        }
    }

    public readonly record struct Line(int Number, double Baseline, double Size, bool Current);

    /// <summary>The lines whose start is on screen: number, baseline (editor coordinates) and text size.</summary>
    public List<Line> VisibleLines()
    {
        var lines = new List<Line>();
        if (_dirty) Rebuild();
        if (_starts.Count == 0) return lines;
        var height = _rtb.ActualHeight;

        // First line whose start is at or below the top: binary search on the (increasing) start positions.
        int lo = 0, hi = _starts.Count - 1, first = _starts.Count;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var r = Rect(_starts[mid]);
            if (r.IsEmpty) return lines;   // not laid out yet
            if (r.Bottom >= 0) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        var caret = _rtb.CaretPosition;
        for (var i = first; i < _starts.Count; i++)
        {
            var p = _starts[i];
            var r = Rect(p);
            if (r.IsEmpty || r.Top > height) break;
            var (family, size) = FontAt(p);
            // The text's baseline: the caret box's top plus the font's ascent at that size.
            // The text's baseline: the bottom of the caret box less the font's descent at that size (on a picture
            // line the box is the picture's height, and the picture sits on the baseline).
            var baseline = r.Bottom - Math.Max(0, family.LineSpacing - family.Baseline) * size;
            var next = i + 1 < _starts.Count ? _starts[i + 1] : null;
            var current = caret.CompareTo(p) >= 0 && (next is null || caret.CompareTo(next) < 0);
            lines.Add(new Line(i + 1, baseline, size, current));
        }
        return lines;
    }

    private static Rect Rect(TextPointer p)
    {
        var ip = p.GetInsertionPosition(LogicalDirection.Forward);
        return ip.GetCharacterRect(LogicalDirection.Forward);
    }

    /// <summary>The font of the text right after a line start (or the paragraph's, on an empty line).</summary>
    private static (FontFamily, double) FontAt(TextPointer p)
    {
        var ip = p.GetInsertionPosition(LogicalDirection.Forward);
        if (ip.Parent is TextElement te && te is not Paragraph) return (te.FontFamily, te.FontSize);
        if (ip.GetAdjacentElement(LogicalDirection.Forward) is TextElement next) return (next.FontFamily, next.FontSize);
        if (ip.Paragraph is { } para) return (para.FontFamily, para.FontSize);
        return (_rtbDefault.Family, _rtbDefault.Size);
    }

    private static (FontFamily Family, double Size) _rtbDefault = (new FontFamily("Segoe UI"), 15);

    protected override void OnRender(DrawingContext dc)
    {
        if (Visibility != Visibility.Visible) return;
        // Its own shade with an edge line, so the numbers read as apart from the page.
        dc.DrawRectangle((Brush)FindResource("GutterBg"), null, new Rect(RenderSize));
        dc.DrawRectangle((Brush)FindResource("GutterLine"), null, new Rect(Math.Max(0, ActualWidth - 1), 0, 1, ActualHeight));
        _rtbDefault = (_rtb.Document.FontFamily, _rtb.Document.FontSize);
        var lines = VisibleLines();
        var dim = (Brush)FindResource("GutterText");
        var cur = (Brush)FindResource("Text");
        var need = 26.0;
        var texts = new List<(FormattedText Text, Line Line)>(lines.Count);
        foreach (var l in lines)
        {
            var ft = new FormattedText(l.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, l.Size, l.Current ? cur : dim, _dpi);
            texts.Add((ft, l));
            need = Math.Max(need, ft.WidthIncludingTrailingWhitespace + 18);
        }
        need = Math.Ceiling(need);
        // Grows at once; shrinks only when clearly too wide, so it doesn't twitch while scrolling.
        if (need > Width || need < Width - 16)
        {
            Width = need;
            return;   // re-rendered at the new width
        }
        foreach (var (ft, l) in texts)
            dc.DrawText(ft, new Point(Width - 10 - ft.WidthIncludingTrailingWhitespace, l.Baseline - ft.Baseline));
    }
}
