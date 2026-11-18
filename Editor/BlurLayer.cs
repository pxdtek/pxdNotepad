using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace pxdNotepad.Editor;

/// <summary>
/// Draws blurred text: over each blurred run on screen it paints the page colour, then a soft smudge in the text's
/// colour. The smudge is made of blurry blobs placed at random (seeded per piece, so it doesn't shimmer), never from
/// the letters themselves, so there's nothing to read at any size or zoom. The text underneath is untouched, so
/// selecting and copying it gives the real text. Clicks pass through to the editor.
/// </summary>
public sealed class BlurLayer : FrameworkElement
{
    private readonly RichTextBox _rtb;
    private readonly List<Rect> _boxes = [];
    private readonly DocIndex _index;
    private bool _queued;

    public BlurLayer(RichTextBox rtb, DocIndex index)
    {
        _rtb = rtb;
        _index = index;
        IsHitTestVisible = false;
        ClipToBounds = true;
        rtb.TextChanged += (_, _) => Queue();
        rtb.SizeChanged += (_, _) => Queue();
        rtb.SelectionChanged += (_, _) => Queue();
        rtb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Queue()));
        Loaded += (_, _) => Queue();
    }

    /// <summary>Draw a thin outline around each blurred piece (Settings → Text).</summary>
    public bool Outline { get; set; } = true;

    /// <summary>Where blurred text is on screen (editor coordinates), after the last draw.</summary>
    public IReadOnlyList<Rect> Boxes => _boxes;

    public void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _queued = false;
            InvalidateVisual();
        });
    }

    private void Refresh()
    {
        _boxes.Clear();
        // Most notes have nothing blurred: that's known from the text alone, far cheaper than asking the layout
        // where each paragraph is.
        if (!_index.AnyBlur) return;
        var paras = _index.Paragraphs;
        var height = _rtb.ActualHeight;

        // First paragraph whose end is at or below the top of the view (binary search, as the links do).
        int lo = 0, hi = paras.Count - 1, first = paras.Count;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var r = paras[mid].ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (r.IsEmpty) return;   // not laid out yet
            if (r.Bottom >= 0) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        for (var i = first; i < paras.Count; i++)
        {
            var p = paras[i];
            if (!HasBlur(p.Inlines)) continue;
            var top = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (top.IsEmpty || top.Top > height) break;
            Walk(p.Inlines);
        }
    }

    private static bool HasBlur(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is Span s) { if (HasBlur(s.Inlines)) return true; }
            else if (i is Run r && r.Text.Length > 0 && DocSerializer.IsBlurred(r)) return true;
        }
        return false;
    }

    private void Walk(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is Span s) Walk(s.Inlines);
            else if (i is Run r && r.Text.Length > 0 && DocSerializer.IsBlurred(r)) AddBoxes(r);
        }
    }

    private readonly List<(Rect Box, Run Run, int From, int To)> _pieces = [];

    /// <summary>One box per line the run covers, with the part of its text on that line.</summary>
    private void AddBoxes(Run r)
    {
        var a = r.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var last = a;
        var from = 0;
        for (var k = 1; k <= r.Text.Length; k++)
        {
            var p = r.ContentStart.GetPositionAtOffset(k);
            if (p is null) break;
            var b = p.GetCharacterRect(LogicalDirection.Backward);
            if (Math.Abs(b.Bottom - a.Bottom) > 2)
            {
                // wrapped: close the line at the previous character, start again on the new one
                Add(new Rect(new Point(a.Left, a.Top), new Point(Math.Max(a.Left + 2, last.Left), a.Bottom)), r, from, k - 1);
                a = r.ContentStart.GetPositionAtOffset(k - 1)!.GetCharacterRect(LogicalDirection.Forward);
                from = k - 1;
            }
            last = b;
        }
        Add(new Rect(new Point(a.Left, a.Top), new Point(Math.Max(a.Left + 2, last.Left), Math.Max(a.Bottom, last.Bottom))), r, from, r.Text.Length);
    }

    private void Add(Rect box, Run r, int from, int to)
    {
        _boxes.Add(box);
        _pieces.Add((box, r, from, to));
    }

    /// <summary>Blurred pieces shown for the moment (a click on them). Never saved: the text stays blurred in the note.</summary>
    private readonly HashSet<Run> _revealed = [];

    /// <summary>The blurred run under a point (editor coordinates), shown or not.</summary>
    public Run? At(Point p)
    {
        foreach (var (box, run, _, _) in _pieces)
        {
            var hit = box;
            hit.Inflate(4, 2);
            if (hit.Contains(p)) return run;
        }
        return null;
    }

    public bool IsRevealed(Run r) => _revealed.Contains(r);

    /// <summary>Shows a blurred piece, or blurs it again. True when it's showing now.</summary>
    public bool Toggle(Run r)
    {
        var on = _revealed.Add(r);
        if (!on) _revealed.Remove(r);
        InvalidateVisual();
        return on;
    }

    /// <summary>Everything blurred again (after a while, or on another note or tab).</summary>
    public void HideAll()
    {
        if (_revealed.Count == 0) return;
        _revealed.Clear();
        InvalidateVisual();
    }

    public bool AnyRevealed => _revealed.Count > 0;

    private Run? _hover;

    /// <summary>The blurred piece under the mouse: it gets a "Reveal" (or "Hide") label with an eye.</summary>
    public Run? Hover
    {
        get => _hover;
        set
        {
            if (_hover == value) return;
            _hover = value;
            InvalidateVisual();
        }
    }

    private static readonly Geometry Eye = Freeze(Geometry.Parse("M1.5,8 C3.5,4.5 5.7,3.2 8,3.2 C10.3,3.2 12.5,4.5 14.5,8 C12.5,11.5 10.3,12.8 8,12.8 C5.7,12.8 3.5,11.5 1.5,8 Z M8,5.8 A2.2,2.2 0 1 1 7.99,5.8 Z"));
    private static readonly Geometry EyeOff = Freeze(Geometry.Parse("M1.5,8 C3.5,4.5 5.7,3.2 8,3.2 C10.3,3.2 12.5,4.5 14.5,8 C12.5,11.5 10.3,12.8 8,12.8 C5.7,12.8 3.5,11.5 1.5,8 Z M8,5.8 A2.2,2.2 0 1 1 7.99,5.8 Z M2.5,13.5 L13.5,2.5"));

    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

    /// <summary>A small pill in the middle of the piece: eye + "Reveal", or crossed-out eye + "Hide" when it's showing.</summary>
    private void Label(DrawingContext dc, Rect box, bool showing)
    {
        var text = new FormattedText(showing ? "Hide" : "Reveal", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, (Brush)FindResource("Text"),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        const double icon = 16, gap = 5, padX = 9, h = 24;
        var w = padX + icon + gap + text.Width + padX;
        var pill = new Rect(box.Left + box.Width / 2 - w / 2, box.Top + box.Height / 2 - h / 2, w, h);
        var accent = (Brush)FindResource("Accent");
        dc.DrawRoundedRectangle((Brush)FindResource("Panel2"), new Pen(accent, 1), pill, h / 2, h / 2);
        dc.PushTransform(new TranslateTransform(pill.Left + padX, pill.Top + (h - icon) / 2));
        dc.DrawGeometry(null, new Pen(accent, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, showing ? EyeOff : Eye);
        dc.Pop();
        dc.DrawText(text, new Point(pill.Left + padX + icon + gap, pill.Top + (h - text.Height) / 2));
    }

    /// <summary>How many times it has drawn (the self-test checks it settles).</summary>
    public int Renders { get; private set; }

    protected override void OnRender(DrawingContext dc)
    {
        Renders++;
        _pieces.Clear();
        Refresh();
        if (_pieces.Count == 0) return;
        var page = (Brush)FindResource("EditorBg");
        Pen? edge = null;
        if (Outline)
        {
            var accent = ((SolidColorBrush)FindResource("Accent")).Color;
            edge = new Pen(new SolidColorBrush(Color.FromArgb(140, accent.R, accent.G, accent.B)), 1) { DashStyle = DashStyles.Solid };
            edge.Freeze();
        }
        Pen? shown = null;
        var hovered = new List<(Rect, Run)>();
        foreach (var (box, run, from, to) in _pieces)
        {
            if (_revealed.Contains(run))
            {
                // Shown for the moment: the real text, with a dashed outline so it's clear it's a secret.
                if (shown is null)
                {
                    var accent = ((SolidColorBrush)FindResource("Accent")).Color;
                    shown = new Pen(new SolidColorBrush(Color.FromArgb(200, accent.R, accent.G, accent.B)), 1.2) { DashStyle = new DashStyle([3, 2], 0) };
                    shown.Freeze();
                }
                var around = box;
                around.Inflate(Math.Max(3, box.Height * 0.12), Math.Max(2, box.Height * 0.06));
                dc.DrawRoundedRectangle(null, shown, around, Math.Min(around.Height / 2, 10), Math.Min(around.Height / 2, 10));
                if (run == _hover) Label(dc, box, showing: true);
                continue;
            }
            // The page over the real letters (a little bigger than them, with round ends)…
            var cover = box;
            cover.Inflate(Math.Max(3, box.Height * 0.12), Math.Max(2, box.Height * 0.06));
            var round = Math.Min(cover.Height / 2, 10);
            dc.DrawRoundedRectangle(page, null, cover, round, round);
            // …then the smudge: a faint band and soft blobs where letters would be, in the text's own colour.
            var ink = (run.Foreground as SolidColorBrush)?.Color ?? Colors.Gray;
            Smudge(dc, box, ink, from * 7919 + (to - from) * 104729 + (int)box.Height);
            hovered.Add((box, run));
            if (edge is not null)
            {
                var ring = cover;
                ring.Inflate(-0.5, -0.5);
                dc.DrawRoundedRectangle(null, edge, ring, round, round);
            }
        }
        foreach (var (box, run) in hovered)
            if (run == _hover) Label(dc, box, showing: false);
    }

    /// <summary>Soft blobs across the box: shaped like a line of smeared words, made from random numbers, not letters.</summary>
    private static void Smudge(DrawingContext dc, Rect box, Color ink, int seed)
    {
        var rnd = new Random(seed);
        var h = box.Height;
        var mid = box.Top + h * 0.56;
        var band = new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 0), new GradientStop(Color.FromArgb(46, ink.R, ink.G, ink.B), 0.5), new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 1)],
            new Point(0, 0), new Point(0, 1));
        band.Freeze();
        dc.DrawRoundedRectangle(band, null, new Rect(box.Left, mid - h * 0.32, box.Width, h * 0.64), h * 0.3, h * 0.3);
        var step = Math.Max(4, h * 0.34);
        for (var x = box.Left + step * 0.4; x < box.Right - step * 0.2; x += step * (0.75 + rnd.NextDouble() * 0.5))
        {
            if (rnd.NextDouble() < 0.12) continue;   // a gap now and then, like between words
            var rx = h * (0.22 + rnd.NextDouble() * 0.2);
            var ry = h * (0.16 + rnd.NextDouble() * 0.14);
            var cy = mid + (rnd.NextDouble() - 0.5) * h * 0.18;
            var alpha = (byte)(70 + rnd.Next(60));
            var blob = new RadialGradientBrush(Color.FromArgb(alpha, ink.R, ink.G, ink.B), Color.FromArgb(0, ink.R, ink.G, ink.B));
            blob.Freeze();
            dc.DrawEllipse(blob, null, new Point(Math.Min(x, box.Right - rx * 0.5), cy), rx, ry);
        }
    }
}
