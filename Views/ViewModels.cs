using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace pxdNotepad.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum NodeKind { Folder, Note, Trash }

/// <summary>A row in the sidebar: a folder (category), a note, or Trash. Folders hold sub-folders, then notes.</summary>
public sealed class SideNode : Observable
{
    public const string TrashId = "*trash";
    private const string FolderIcon = "M1.5,4 L1.5,13 L14.5,13 L14.5,5.5 L7.5,5.5 L6,3.5 L1.5,3.5 Z";
    private const string FolderOpenIcon = "M1.5,13 L1.5,3.5 L6,3.5 L7.5,5.5 L13,5.5 L13,7.5 M1.5,13 L3.8,7.5 L15,7.5 L12.8,13 Z";
    private const string NoteIcon = "M4,2 L9.5,2 L12.5,5 L12.5,14 L4,14 Z M9.5,2 L9.5,5 L12.5,5 M6,8.5 L10.5,8.5 M6,11 L9.5,11";
    private const string TrashIcon = "M3,4 L13,4 M6,4 L6,2.5 L10,2.5 L10,4 M4.5,4 L5.3,13.5 L10.7,13.5 L11.5,4";

    private string _name;
    private int _count;
    private bool _expanded, _selected;

    public SideNode(NodeKind kind, string id, string name, SideNode? parent)
    {
        Kind = kind;
        Id = id;
        _name = name;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public NodeKind Kind { get; }
    public string Id { get; }
    public SideNode? Parent { get; }
    public int Depth { get; }
    public bool IsFolder => Kind == NodeKind.Folder;
    public bool IsNote => Kind == NodeKind.Note;
    public bool InTrash => Kind == NodeKind.Trash || Parent?.Kind == NodeKind.Trash;
    public long Updated { get; set; }
    public string Preview { get; set; } = "";
    public ObservableCollection<SideNode> Children { get; } = [];
    private IReadOnlyList<TagChip> _tagIcons = [];
    /// <summary>The note's tag icons, shown at the right end of its row.</summary>
    public IReadOnlyList<TagChip> TagIcons { get => _tagIcons; set => Set(ref _tagIcons, value); }

    public string Name { get => _name; set { Set(ref _name, value); Raise(nameof(Tip)); } }
    public int Count { get => _count; set { Set(ref _count, value); Raise(nameof(CountText)); } }
    public string CountText => Kind != NodeKind.Note && _count > 0 ? _count.ToString() : "";
    public bool IsExpanded { get => _expanded; set { Set(ref _expanded, value); Raise(nameof(Icon)); } }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    /// <summary>The folder a new note goes in when this row is selected (null: the top level).</summary>
    public string? FolderId => Kind switch { NodeKind.Folder => Id, NodeKind.Note when Parent?.IsFolder == true => Parent.Id, _ => null };

    public string Path => Parent is { IsFolder: true } ? Parent.Path + " › " + Name : Name;
    public string Tip => Kind == NodeKind.Note
        ? (Preview.Length > 0 ? Preview + Environment.NewLine + Environment.NewLine : "") + "Edited " + MainView.When(Updated)
        : Path;

    private static readonly Geometry NoteGeo = Frozen(NoteIcon), TrashGeo = Frozen(TrashIcon), FolderGeo = Frozen(FolderIcon), FolderOpenGeo = Frozen(FolderOpenIcon);
    private static Geometry Frozen(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    public Geometry Icon => (Kind switch
    {
        NodeKind.Note => NoteGeo,
        NodeKind.Trash => TrashGeo,
        _ => _expanded && Children.Count > 0 ? FolderOpenGeo : FolderGeo,
    });
    public Brush IconBrush => (Brush)Application.Current.FindResource(Kind == NodeKind.Folder ? "Accent" : "Dim");
    public Brush TextBrush => (Brush)Application.Current.FindResource(InTrash && Kind == NodeKind.Note ? "Dim" : "Text");

    public IEnumerable<SideNode> Walk()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Walk())
                yield return d;
    }
}

public sealed class NoteItem : Observable
{
    private string _title = "", _meta = "", _snippet = "";
    public required string Id { get; init; }
    public string? Cat { get; set; }
    public string Title { get => _title; set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Untitled" : value); }
    public string Meta { get => _meta; set => Set(ref _meta, value); }
    /// <summary>Where it is (folder path), shown bold in the accent colour in search results.</summary>
    public string Place { get; init; } = "";
    /// <summary>The tab the words were found in, or a folder's note count.</summary>
    public string Extra { get; init; } = "";
    public string WhenText { get; init; } = "";
    public Visibility ExtraShown => Extra.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Snippet { get => _snippet; set => Set(ref _snippet, value); }
    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlyList<TagChip> TagIcons { get; init; } = [];
    /// <summary>A folder (found by its name or its tags), not a note.</summary>
    public bool IsFolder { get; init; }
    public Visibility FolderGlyph => IsFolder ? Visibility.Visible : Visibility.Collapsed;
    public long Updated { get; set; }
}

/// <summary>A TextBlock that shows <see cref="Source"/> with every search term in it highlighted.</summary>
public sealed class HighlightText : TextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(HighlightText), new PropertyMetadata("", (d, _) => ((HighlightText)d).Build()));
    public static readonly DependencyProperty TermsProperty = DependencyProperty.Register(nameof(Terms), typeof(IReadOnlyList<string>), typeof(HighlightText), new PropertyMetadata(null, (d, _) => ((HighlightText)d).Build()));

    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public IReadOnlyList<string>? Terms { get => (IReadOnlyList<string>?)GetValue(TermsProperty); set => SetValue(TermsProperty, value); }

    private void Build()
    {
        Inlines.Clear();
        var text = Source ?? "";
        var terms = Terms;
        if (terms is null || terms.Count == 0 || text.Length == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }
        // Mark every character covered by some term, then emit runs.
        var hit = new bool[text.Length];
        foreach (var t in terms)
        {
            if (t.Length == 0) continue;
            for (var at = text.IndexOf(t, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(t, at + t.Length, StringComparison.OrdinalIgnoreCase))
                for (var k = at; k < at + t.Length; k++) hit[k] = true;
        }
        var i = 0;
        while (i < text.Length)
        {
            var j = i;
            while (j < text.Length && hit[j] == hit[i]) j++;
            var run = new Run(text[i..j]);
            if (hit[i])
            {
                run.SetResourceReference(TextElement.BackgroundProperty, "Match");
                run.SetResourceReference(TextElement.ForegroundProperty, "Text");
            }
            Inlines.Add(run);
            i = j;
        }
    }
}
