using System.Windows.Controls;
using System.Windows.Documents;

namespace pxdNotepad.Editor;

/// <summary>
/// One shared map of the open note for the layers over the editor (line numbers, links, blur): its paragraphs in
/// order, where each numbered line starts, and whether anything is blurred. Walking a FlowDocument is slow (thousands
/// of tree steps on a long note), so it's done once per change of shape, not once per layer per key. Typing or
/// deleting a single character can't add or remove a paragraph, line break or run, so the map stays as it is (its
/// TextPointers move with the text by themselves).
/// </summary>
public sealed class DocIndex
{
    private readonly RichTextBox _rtb;
    private FlowDocument? _doc;
    private bool _dirty = true;
    private readonly List<Paragraph> _paras = [];
    private readonly List<TextPointer> _starts = [];
    private bool _anyBlur;

    public DocIndex(RichTextBox rtb)
    {
        _rtb = rtb;
        rtb.TextChanged += (_, e) => { if (!OneCharacter(e)) _dirty = true; };
    }

    /// <summary>How many times it has walked the note (the self-test checks typing doesn't).</summary>
    public int Builds { get; private set; }

    /// <summary>Every change is one character typed or removed (anything that adds or removes an element is at least two symbols).</summary>
    private static bool OneCharacter(TextChangedEventArgs e)
    {
        if (e.Changes.Count == 0) return false;
        foreach (var c in e.Changes)
            if (c.AddedLength + c.RemovedLength != 1) return false;
        return true;
    }

    public void Invalidate() => _dirty = true;

    /// <summary>All paragraphs, in document order (inside lists, sections and tables too).</summary>
    public List<Paragraph> Paragraphs { get { Ensure(); return _paras; } }

    /// <summary>Where each numbered line starts: a paragraph, a Shift+Enter break, or a picture line.</summary>
    public List<TextPointer> LineStarts { get { Ensure(); return _starts; } }

    /// <summary>Some text in the note is blurred.</summary>
    public bool AnyBlur { get { Ensure(); return _anyBlur; } }

    private void Ensure()
    {
        if (!_dirty && ReferenceEquals(_doc, _rtb.Document)) return;
        _doc = _rtb.Document;
        _paras.Clear();
        _starts.Clear();
        _anyBlur = false;
        Collect(_doc.Blocks);
        _dirty = false;
        Builds++;
    }

    private void Collect(BlockCollection blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    _paras.Add(p);
                    _starts.Add(p.ContentStart);
                    Inlines(p.Inlines);
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

    private void Inlines(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case LineBreak lb: _starts.Add(lb.ElementEnd); break;
                case Span s: Inlines(s.Inlines); break;
                case Run r when !_anyBlur && DocSerializer.IsBlurred(r): _anyBlur = true; break;
            }
        }
    }
}
