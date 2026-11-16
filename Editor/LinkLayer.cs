using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace pxdNotepad.Editor;

/// <summary>
/// Draws a cyan underline under every phone number, email address and web link in view, and knows where they are so
/// a click can open them. Sits over the editor and lets clicks through; the note itself is never changed.
/// </summary>
public sealed class LinkLayer : FrameworkElement
{
    public sealed class Live
    {
        public required FoundLink Link { get; init; }
        public required TextPointer Start { get; init; }
        public required TextPointer End { get; init; }
        public List<Rect> Boxes { get; } = [];
    }

    private readonly RichTextBox _rtb;
    private readonly List<Live> _links = [];
    private bool _queued;

    public LinkLayer(RichTextBox rtb)
    {
        _rtb = rtb;
        IsHitTestVisible = false;
        ClipToBounds = true;
        rtb.TextChanged += (_, _) => Queue();
        rtb.SizeChanged += (_, _) => Queue();
        rtb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Queue()));
        Loaded += (_, _) => Queue();
    }

    public IReadOnlyList<Live> Links => _links;

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

    /// <summary>The link under a point (editor coordinates), if any.</summary>
    public Live? At(Point p)
    {
        foreach (var l in _links)
            foreach (var b in l.Boxes)
                if (b.Contains(p)) return l;
        return null;
    }

    internal static void Paragraphs(BlockCollection blocks, List<Paragraph> into)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p: into.Add(p); break;
                case List l: foreach (var li in l.ListItems) Paragraphs(li.Blocks, into); break;
                case Section s: Paragraphs(s.Blocks, into); break;
                case Table t:
                    foreach (var g in t.RowGroups) foreach (var r in g.Rows) foreach (var c in r.Cells) Paragraphs(c.Blocks, into);
                    break;
            }
        }
    }

    /// <summary>Finds the links in the paragraphs on screen (the rest wait until they're scrolled to).</summary>
    public void Refresh()
    {
        _links.Clear();
        var paras = new List<Paragraph>();
        Paragraphs(_rtb.Document.Blocks, paras);
        if (paras.Count == 0) return;
        var height = _rtb.ActualHeight;

        // First paragraph whose end is at or below the top of the view.
        int lo = 0, hi = paras.Count - 1, first = paras.Count;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var r = paras[mid].ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (r.IsEmpty) return;
            if (r.Bottom >= 0) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        for (var i = first; i < paras.Count; i++)
        {
            var top = paras[i].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (top.IsEmpty || top.Top > height) break;
            Scan(paras[i]);
        }
    }

    private void Scan(Paragraph p)
    {
        var text = new StringBuilder();
        var runs = new List<(Run Run, int At)>();
        void Walk(InlineCollection inlines)
        {
            foreach (var i in inlines)
            {
                switch (i)
                {
                    case Run r when DocSerializer.IsBlurred(r): text.Append('\u2022', r.Text.Length); break;
                    case Run r: runs.Add((r, text.Length)); text.Append(r.Text); break;
                    case LineBreak: text.Append('\n'); break;
                    case Span s: Walk(s.Inlines); break;
                    default: text.Append('￼'); break;
                }
            }
        }
        Walk(p.Inlines);
        if (text.Length < 7) return;
        foreach (var f in LinkDetector.Find(text.ToString()))
        {
            var start = Pointer(runs, f.Start, false);
            var end = Pointer(runs, f.Start + f.Length, true);
            if (start is not null && end is not null)
                _links.Add(new Live { Link = f, Start = start, End = end });
        }
    }

    private static TextPointer? Pointer(List<(Run Run, int At)> runs, int index, bool isEnd)
    {
        foreach (var (run, at) in runs)
        {
            var len = run.Text.Length;
            if (isEnd ? index > at && index <= at + len : index >= at && index < at + len)
                return run.ContentStart.GetPositionAtOffset(index - at);
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        Refresh();
        if (_links.Count == 0) return;
        var line = (Brush)FindResource("Accent");
        var soft = (Brush)FindResource("AccentSoft");
        foreach (var l in _links)
        {
            // Walk the link a line at a time (a long address can wrap).
            var segStart = l.Start;
            var a = segStart.GetCharacterRect(LogicalDirection.Forward);
            for (var p = l.Start; p is not null && p.CompareTo(l.End) < 0;)
            {
                var next = p.GetPositionAtOffset(1, LogicalDirection.Forward);
                if (next is null) break;
                var r = next.GetCharacterRect(LogicalDirection.Backward);
                var atEnd = next.CompareTo(l.End) >= 0;
                var wrapped = Math.Abs(r.Bottom - a.Bottom) > 2;
                if (wrapped || atEnd)
                {
                    var right = wrapped ? p.GetCharacterRect(LogicalDirection.Backward).Right : r.Left;
                    var box = new Rect(new Point(a.Left, a.Top), new Point(Math.Max(a.Left + 2, right), a.Bottom));
                    l.Boxes.Add(box);
                    dc.DrawRectangle(soft, null, box);
                    dc.DrawRectangle(line, null, new Rect(box.Left, box.Bottom - 1.6, box.Width, 1.2));
                    if (wrapped) a = p.GetCharacterRect(LogicalDirection.Forward);
                }
                p = next;
            }
        }
    }
}
