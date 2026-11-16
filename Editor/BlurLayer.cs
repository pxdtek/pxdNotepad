using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace pxdNotepad.Editor;

/// <summary>
/// Draws blurred text: over each blurred run on screen it paints the page colour, then that same piece of the editor,
/// and the whole layer is Gaussian-blurred, so the words show as a soft smear. The text underneath is untouched, so
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
        Effect = new BlurEffect { Radius = 12, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
        rtb.TextChanged += (_, _) => Queue();
        rtb.SizeChanged += (_, _) => Queue();
        rtb.SelectionChanged += (_, _) => Queue();
        rtb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Queue()));
        Loaded += (_, _) => Queue();
    }

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

    protected override void OnRender(DrawingContext dc)
    {
        _pieces.Clear();
        Refresh();
        if (_pieces.Count == 0) return;
        var page = (Brush)FindResource("EditorBg");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (box, run, from, to) in _pieces)
        {
            // Cover the real letters with the page, then draw the same letters there; the layer's blur smears them.
            var cover = box;
            cover.Inflate(6, 4);
            dc.DrawRoundedRectangle(page, null, cover, 3, 3);
            if (to <= from) continue;
            var face = new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch);
            var ft = new FormattedText(run.Text[from..to], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, run.FontSize, run.Foreground ?? Brushes.Gray, dpi);
            dc.DrawText(ft, new Point(box.Left, box.Top + Math.Max(0, (box.Height - ft.Height) / 2)));
        }
    }
}
