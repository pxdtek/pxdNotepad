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

/// <summary>A row in the category tree. Ids starting with * are the built-in ones (All notes, Unfiled, Trash).</summary>
public sealed class CategoryNode : Observable
{
    public const string All = "*all", Unfiled = "*unfiled", TrashId = "*trash";
    private const string FolderIcon = "M1.5,4 L1.5,13 L14.5,13 L14.5,5.5 L7.5,5.5 L6,3.5 L1.5,3.5 Z";
    private const string AllIcon = "M3,2.5 L13,2.5 L13,13.5 L3,13.5 Z M5.5,6 L10.5,6 M5.5,8.5 L10.5,8.5 M5.5,11 L8.5,11";
    private const string UnfiledIcon = "M3,2.5 L10,2.5 L13,5.5 L13,13.5 L3,13.5 Z M10,2.5 L10,5.5 L13,5.5";
    private const string TrashIcon = "M3,4 L13,4 M6,4 L6,2.5 L10,2.5 L10,4 M4.5,4 L5.3,13.5 L10.7,13.5 L11.5,4";

    private string _name;
    private int _count;
    private bool _expanded = true, _selected;

    public CategoryNode(string id, string name, CategoryNode? parent)
    {
        Id = id;
        _name = name;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public string Id { get; }
    public CategoryNode? Parent { get; }
    public int Depth { get; }
    public bool IsSpecial => Id.StartsWith('*');
    public ObservableCollection<CategoryNode> Children { get; } = [];
    public string Name { get => _name; set { Set(ref _name, value); Raise(nameof(Tip)); } }
    public int Count { get => _count; set { Set(ref _count, value); Raise(nameof(CountText)); } }
    public string CountText => _count > 0 ? _count.ToString() : "";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Tip => IsSpecial ? Name : Path;
    public string Path => Parent is null || Parent.IsSpecial ? Name : Parent.Path + " › " + Name;

    public Geometry Icon => Geometry.Parse(Id switch { All => AllIcon, Unfiled => UnfiledIcon, TrashId => TrashIcon, _ => FolderIcon });
    public Brush IconBrush => (Brush)Application.Current.FindResource(IsSpecial ? "Dim" : "Accent");

    /// <summary>This category's id and every sub-category's below it.</summary>
    public IEnumerable<string> SelfAndDescendants()
    {
        yield return Id;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    public IEnumerable<CategoryNode> Walk()
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
    public string Snippet { get => _snippet; set => Set(ref _snippet, value); }
    public IReadOnlyList<string> Terms { get; init; } = [];
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
