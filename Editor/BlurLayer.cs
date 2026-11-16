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
    private bool _queued;

    public BlurLayer(RichTextBox rtb)
    {
        _rtb = rtb;
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
        var paras = new List<Paragraph>();
        LinkLayer.Paragraphs(_rtb.Document.Blocks, paras);
        var height = _rtb.ActualHeight;
        foreach (var p in paras)
        {
            var end = p.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (end.IsEmpty) return;
            if (end.Bottom < 0) continue;
            var top = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (top.Top > height) break;
            Walk(p.Inlines);
        }
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
            if (edge is not null)
            {
                var ring = cover;
                ring.Inflate(-0.5, -0.5);
                dc.DrawRoundedRectangle(null, edge, ring, round, round);
            }
        }
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
