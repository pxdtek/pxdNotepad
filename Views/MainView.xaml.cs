using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using pxdNotepad.Data;
using pxdNotepad.Editor;
using pxdNotepad.Images;
using pxdNotepad.Sync;

namespace pxdNotepad.Views;

/// <summary>The whole window's content: categories, the note list (or search results) and the editor.</summary>
public partial class MainView : UserControl
{
    private readonly NoteStore _store;
    private readonly ImageStore _images;
    private readonly SearchEngine _search;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<CategoryNode> _roots = [];
    private readonly Dictionary<string, CategoryNode> _nodes = [];
    private readonly ObservableCollection<NoteItem> _items = [];
    private CategoryNode? _cat;
    private int _searchSeq;
    private string[] _terms = [];
    private Point _dragStart;

    public SyncService? Sync { get; set; }

    public MainView(NoteStore store, AppSettings settings, string? dbPath = null)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _images = new ImageStore(store.Connection);
        ImageBox.Store = _images;
        _search = new SearchEngine(dbPath);
        _images.Changed += () => Sync?.Kick(TimeSpan.FromSeconds(4));
        _store.Changed += () => Sync?.Kick(TimeSpan.FromSeconds(4));

        TreeCol.Width = new GridLength(Math.Clamp(settings.TreeWidth, 140, 600));
        ListCol.Width = new GridLength(Math.Clamp(settings.ListWidth, 200, 700));
        AddCategoryButton.Content = Images.MarkupWindow.Glyph("M8,3 L8,13 M3,8 L13,8");
        NewNoteButton.Content = Images.MarkupWindow.Glyph("M3,13 L3,10.5 L10.5,3 L13,5.5 L5.5,13 Z M9,4.5 L11.5,7");
        ClearSearch.Content = Images.MarkupWindow.Glyph("M4,4 L12,12 M12,4 L4,12");

        Tree.ItemsSource = _roots;
        NoteList.ItemsSource = _items;
        Tree.SelectedItemChanged += (_, _) =>
        {
            if (Tree.SelectedItem is CategoryNode n && n != _cat)
            {
                _cat = n;
                _settings.LastCategory = n.Id;
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else ShowList();
            }
        };
        Tree.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemUnder<TreeViewItem>(e.OriginalSource) is { DataContext: CategoryNode n }) n.IsSelected = true;
        };
        Tree.ContextMenuOpening += (_, e) => { Tree.ContextMenu = TreeMenu(Tree.SelectedItem as CategoryNode); };
        Tree.ContextMenu = new ContextMenu();
        Tree.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        Tree.PreviewMouseMove += TreeDragStart;
        Tree.DragOver += TreeDragOver;
        Tree.Drop += TreeDrop;
        Tree.KeyDown += (_, e) =>
        {
            if (Tree.SelectedItem is not CategoryNode { IsSpecial: false } n) return;
            if (e.Key == Key.F2) { Rename(n); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteCategory(n); e.Handled = true; }
        };

        NoteList.SelectionChanged += (_, _) =>
        {
            if (NoteList.SelectedItem is NoteItem it && it.Id != _openId) OpenNote(it.Id, _terms);
        };
        NoteList.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        NoteList.PreviewMouseMove += NoteDragStart;
        NoteList.ContextMenuOpening += (_, e) =>
        {
            if (ItemUnder<ListBoxItem>(e.OriginalSource) is not { DataContext: NoteItem it }) { e.Handled = true; return; }
            NoteList.SelectedItem = it;
            NoteList.ContextMenu = NoteMenu(it);
        };
        NoteList.ContextMenu = new ContextMenu();
        NoteList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && NoteList.SelectedItem is NoteItem it) { TrashOrPurge(it); e.Handled = true; }
            else if (e.Key == Key.Enter && NoteList.SelectedItem is NoteItem) { Editor.Focus(); e.Handled = true; }
        };

        SearchBox.TextChanged += (_, _) => RunSearch();
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { SearchBox.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Down && _items.Count > 0)
            {
                NoteList.SelectedIndex = Math.Max(0, NoteList.SelectedIndex);
                (NoteList.ItemContainerGenerator.ContainerFromIndex(NoteList.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _items.Count > 0)
            {
                if (NoteList.SelectedItem is null) NoteList.SelectedIndex = 0;
                Editor.Focus();
                e.Handled = true;
            }
        };
        ClearSearch.Click += (_, _) => { SearchBox.Text = ""; SearchBox.Focus(); };
        AddCategoryButton.Click += (_, _) => NewCategory(_cat is { IsSpecial: false } ? null : null);
        NewNoteButton.Click += (_, _) => NewNote();

        SetupEditor();
        BuildTree();
        var start = (settings.LastCategory is { } lc && _nodes.TryGetValue(lc, out var ln)) ? ln : _roots[0];
        start.IsSelected = true;
        _cat = start;
        ShowList();
        if (settings.LastNote is { } last && _store.Load(last) is { Trashed: false })
            OpenNote(last, []);

        PreviewKeyDown += OnKeys;
    }

    public void SaveLayout()
    {
        SaveNow();
        _settings.TreeWidth = TreeCol.ActualWidth;
        _settings.ListWidth = ListCol.ActualWidth;
        _settings.Collapsed = _nodes.Values.Where(n => !n.IsExpanded && n.Children.Count > 0).Select(n => n.Id).ToList();
        _settings.Save();
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.F && !shift) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.N) { NewCategory(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { NewNote(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveNow(); e.Handled = true; }
        else if (ctrl && e.Key == Key.L && shift) { ToggleLineNumbers(); e.Handled = true; }
    }

    private Window? Owner => Window.GetWindow(this);

    // ── categories ──

    public void BuildTree()
    {
        var selected = _cat?.Id;
        _roots.Clear();
        _nodes.Clear();
        var counts = _store.Counts(out var all, out var unfiled, out var trash);
        var allNode = new CategoryNode(CategoryNode.All, "All notes", null) { Count = all };
        _roots.Add(allNode);
        _nodes[allNode.Id] = allNode;

        var cats = _store.Categories();
        var byParent = cats.GroupBy(c => c.Parent ?? "").ToDictionary(g => g.Key, g => g.ToList());
        var known = cats.Select(c => c.Id).ToHashSet();
        void Add(Category c, CategoryNode? parent, ICollection<CategoryNode> into, int depth)
        {
            if (depth > 30) return;
            var n = new CategoryNode(c.Id, c.Name, parent) { IsExpanded = !_settings.Collapsed.Contains(c.Id) };
            _nodes[c.Id] = n;
            into.Add(n);
            if (byParent.TryGetValue(c.Id, out var kids))
                foreach (var k in kids) Add(k, n, n.Children, depth + 1);
        }
        // Top level: no parent, or a parent that's gone (deleted on another PC).
        foreach (var c in cats.Where(c => c.Parent is null || !known.Contains(c.Parent)))
            Add(c, null, _roots, 0);
        foreach (var n in _nodes.Values.Where(n => !n.IsSpecial))
            n.Count = n.SelfAndDescendants().Sum(id => counts.GetValueOrDefault(id));

        var un = new CategoryNode(CategoryNode.Unfiled, "Unfiled", null) { Count = unfiled };
        var tr = new CategoryNode(CategoryNode.TrashId, "Trash", null) { Count = trash };
        _roots.Add(un);
        _roots.Add(tr);
        _nodes[un.Id] = un;
        _nodes[tr.Id] = tr;
        if (selected is not null && _nodes.TryGetValue(selected, out var again))
        {
            _cat = again;
            again.IsSelected = true;
        }
        else if (selected is not null)
        {
            _cat = allNode;
            allNode.IsSelected = true;
        }
    }

    private void RefreshCounts()
    {
        var counts = _store.Counts(out var all, out var unfiled, out var trash);
        foreach (var n in _nodes.Values)
            n.Count = n.Id switch
            {
                CategoryNode.All => all,
                CategoryNode.Unfiled => unfiled,
                CategoryNode.TrashId => trash,
                _ => n.SelfAndDescendants().Sum(id => counts.GetValueOrDefault(id)),
            };
    }

    private ContextMenu TreeMenu(CategoryNode? n)
    {
        var m = new ContextMenu();
        MenuItem Item(string text, Action a, string? keys = null)
        {
            var i = new MenuItem { Header = text, InputGestureText = keys ?? "" };
            i.Click += (_, _) => a();
            m.Items.Add(i);
            return i;
        }
        if (n is null || n.Id == CategoryNode.TrashId)
        {
            if (n is not null) Item("Empty trash…", EmptyTrash);
            return m;
        }
        Item("New note here", () => { n.IsSelected = true; NewNote(); }, "Ctrl+N");
        if (n.IsSpecial)
        {
            Item("New category", () => NewCategory(null), "Ctrl+Shift+N");
            return m;
        }
        Item("New sub-category", () => NewCategory(n.Id));
        m.Items.Add(new Separator());
        Item("Rename…", () => Rename(n), "F2");
        if (n.Parent is not null) Item("Move to top level", () => { _store.MoveCategory(n.Id, null); BuildTree(); });
        Item("Delete category…", () => DeleteCategory(n), "Del");
        return m;
    }

    private void NewCategory(string? parent)
    {
        var where = parent is not null && _nodes.TryGetValue(parent, out var p) ? $" inside {p.Name}" : "";
        var name = Dialogs.Prompt(Owner, parent is null ? "New category" : "New sub-category", $"Name the new category{where}:", "", "Create");
        if (name is null) return;
        var c = _store.AddCategory(name, parent);
        if (parent is not null && _nodes.TryGetValue(parent, out var pn)) pn.IsExpanded = true;
        _cat = null;
        BuildTree();
        if (_nodes.TryGetValue(c.Id, out var node)) node.IsSelected = true;
    }

    private void Rename(CategoryNode n)
    {
        var name = Dialogs.Prompt(Owner, "Rename category", "New name:", n.Name, "Rename");
        if (name is null || name == n.Name) return;
        _store.RenameCategory(n.Id, name);
        n.Name = name;
        ShowList();
    }

    private void DeleteCategory(CategoryNode n)
    {
        var up = n.Parent is null || n.Parent.IsSpecial ? "Unfiled (or the top level, for sub-categories)" : n.Parent.Name;
        if (!Dialogs.Confirm(Owner, "Delete category", $"Delete \"{n.Name}\"? Its notes and sub-categories aren't deleted: they move up to {up}.", "Delete", danger: true))
            return;
        _store.DeleteCategory(n.Id);
        _cat = null;
        BuildTree();
        (_nodes.GetValueOrDefault(n.Parent?.Id ?? "") ?? _roots[0]).IsSelected = true;
    }

    private void EmptyTrash()
    {
        var count = _nodes[CategoryNode.TrashId].Count;
        if (count == 0) return;
        if (!Dialogs.Confirm(Owner, "Empty trash", $"Delete the {count} note{(count == 1 ? "" : "s")} in Trash for good? This can't be undone (on any PC that syncs).", "Delete for good", danger: true))
            return;
        _store.EmptyTrash();
        if (_openId is not null && _store.Load(_openId) is null) CloseNote();
        RefreshCounts();
        ShowList();
    }

    // ── note list ──

    private string CategoryPath(string? cat) => cat is not null && _nodes.TryGetValue(cat, out var n) ? n.Path : "Unfiled";

    public static string When(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var now = DateTime.Now;
        if (t.Date == now.Date) return t.ToString("h:mm tt");
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday";
        if ((now - t).TotalDays < 7) return t.ToString("dddd");
        return t.Year == now.Year ? t.ToString("MMM d") : t.ToString("MMM d, yyyy");
    }

    public void ShowList()
    {
        if (SearchBox.Text.Trim().Length > 0) { RunSearch(); return; }
        _terms = [];
        var n = _cat ?? _roots[0];
        var rows = n.Id switch
        {
            CategoryNode.All => _store.Notes(null),
            CategoryNode.Unfiled => _store.Notes(null, unfiled: true),
            CategoryNode.TrashId => _store.Notes(null, trash: true),
            _ => _store.Notes(n.SelfAndDescendants().ToList()),
        };
        ListTitle.Text = n.Name;
        ListInfo.Text = rows.Count == 1 ? "1 note" : $"{rows.Count} notes";
        Fill(rows.Select(r => new NoteItem
        {
            Id = r.Id,
            Cat = r.Cat,
            Title = r.Title,
            Updated = r.Updated,
            Meta = n.IsSpecial || r.Cat != n.Id ? $"{When(r.Updated)} · {CategoryPath(r.Cat)}" : When(r.Updated),
            Snippet = r.Preview,
        }));
    }

    private void Fill(IEnumerable<NoteItem> items)
    {
        _items.Clear();
        foreach (var i in items) _items.Add(i);
        var open = _items.FirstOrDefault(i => i.Id == _openId);
        NoteList.SelectedItem = open;
    }

    private void RunSearch()
    {
        var q = SearchBox.Text;
        var has = q.Trim().Length > 0;
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            _terms = [];
            ShowList();
            return;
        }
        var seq = ++_searchSeq;
        var terms = SearchEngine.Terms(q);
        Task.Run(() =>
        {
            if (seq != _searchSeq) return;
            List<SearchHit> hits;
            double ms;
            try { hits = _search.Search(q, 300, out ms); }
            catch { hits = []; ms = 0; }
            if (seq != _searchSeq) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (seq != _searchSeq) return;
                _terms = terms;
                ListTitle.Text = "Search";
                ListInfo.Text = (hits.Count == 0 ? "Nothing found" : hits.Count == 1 ? "1 note" : hits.Count >= 300 ? "300+ notes" : $"{hits.Count} notes") + $" · {ms:0.0} ms";
                Fill(hits.Select(h => new NoteItem
                {
                    Id = h.Id,
                    Cat = h.Cat,
                    Title = h.Title,
                    Updated = h.Updated,
                    Meta = $"{When(h.Updated)} · {CategoryPath(h.Cat)}",
                    Snippet = h.Snippet,
                    Terms = terms,
                }));
            });
        });
    }

    private ContextMenu NoteMenu(NoteItem it)
    {
        var m = new ContextMenu();
        var trashed = _store.Load(it.Id)?.Trashed == true;
        MenuItem Item(ItemsControl into, string text, Action a, string? keys = null)
        {
            var i = new MenuItem { Header = text, InputGestureText = keys ?? "" };
            i.Click += (_, _) => a();
            into.Items.Add(i);
            return i;
        }
        if (trashed)
        {
            Item(m, "Restore", () => { _store.Trash(it.Id, false); RefreshCounts(); ShowList(); });
            Item(m, "Delete for good…", () => TrashOrPurge(it), "Del");
            return m;
        }
        var move = new MenuItem { Header = "Move to" };
        Item(move, "Unfiled", () => Move(it.Id, null));
        foreach (var n in _roots.Where(r => !r.IsSpecial).SelectMany(r => r.Walk()))
            Item(move, new string(' ', n.Depth * 3) + n.Name, () => Move(it.Id, n.Id));
        m.Items.Add(move);
        Item(m, "Duplicate", () =>
        {
            SaveNow();
            if (_store.Load(it.Id) is not { } f) return;
            var id = _store.NewNote(f.Cat, (f.Title.Length > 0 ? f.Title : "Untitled") + " (copy)", f.Doc, f.Body);
            RefreshCounts();
            ShowList();
            OpenNote(id, []);
        });
        m.Items.Add(new Separator());
        Item(m, "Move to Trash", () => TrashOrPurge(it), "Del");
        return m;
    }

    private void Move(string id, string? cat)
    {
        SaveNow();
        _store.MoveNote(id, cat);
        RefreshCounts();
        ShowList();
        if (id == _openId) UpdateMeta();
    }

    private void TrashOrPurge(NoteItem it)
    {
        var note = _store.Load(it.Id);
        if (note is null) return;
        if (note.Trashed)
        {
            if (!Dialogs.Confirm(Owner, "Delete note", $"Delete \"{it.Title}\" for good? This can't be undone.", "Delete for good", danger: true)) return;
            _store.Purge(it.Id);
        }
        else
        {
            if (it.Id == _openId) SaveNow();
            _store.Trash(it.Id, true);
        }
        var idx = _items.IndexOf(it);
        if (it.Id == _openId) CloseNote();
        RefreshCounts();
        ShowList();
        if (_items.Count > 0) NoteList.SelectedIndex = Math.Min(idx, _items.Count - 1);
    }

    private void NewNote()
    {
        SaveNow();
        var cat = _cat is { IsSpecial: false } c ? c.Id : null;
        if (_cat?.Id == CategoryNode.TrashId) { _nodes[CategoryNode.All].IsSelected = true; }
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        var id = _store.NewNote(cat);
        RefreshCounts();
        ShowList();
        OpenNote(id, []);
        TitleBox.Focus();
    }

    // ── dragging notes and categories onto the tree ──

    private static T? ItemUnder<T>(object source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private bool Moved(MouseEventArgs e)
    {
        var p = e.GetPosition(null);
        return e.LeftButton == MouseButtonState.Pressed
            && (Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance);
    }

    private void NoteDragStart(object sender, MouseEventArgs e)
    {
        if (!Moved(e) || ItemUnder<ListBoxItem>(e.OriginalSource) is not { DataContext: NoteItem it }) return;
        SaveNow();
        DragDrop.DoDragDrop(NoteList, new DataObject("pxdNotepad.Note", it.Id), DragDropEffects.Move);
    }

    private void TreeDragStart(object sender, MouseEventArgs e)
    {
        if (!Moved(e) || ItemUnder<TreeViewItem>(e.OriginalSource) is not { DataContext: CategoryNode { IsSpecial: false } n }) return;
        DragDrop.DoDragDrop(Tree, new DataObject("pxdNotepad.Category", n.Id), DragDropEffects.Move);
    }

    private CategoryNode? DropTarget(DragEventArgs e) => ItemUnder<TreeViewItem>(e.OriginalSource)?.DataContext as CategoryNode;

    private void TreeDragOver(object sender, DragEventArgs e)
    {
        var t = DropTarget(e);
        var ok = t is not null && t.Id != CategoryNode.TrashId
            && (e.Data.GetDataPresent("pxdNotepad.Note") || (e.Data.GetData("pxdNotepad.Category") is string cid && cid != t.Id && t.Id != CategoryNode.Unfiled));
        if (t?.Id == CategoryNode.TrashId && e.Data.GetDataPresent("pxdNotepad.Note")) ok = true;
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TreeDrop(object sender, DragEventArgs e)
    {
        var t = DropTarget(e);
        if (t is null) return;
        if (e.Data.GetData("pxdNotepad.Note") is string nid)
        {
            if (t.Id == CategoryNode.TrashId)
            {
                if (nid == _openId) CloseNote();
                _store.Trash(nid, true);
            }
            else
            {
                if (_store.Load(nid)?.Trashed == true) _store.Trash(nid, false);
                _store.MoveNote(nid, t.IsSpecial ? null : t.Id);
            }
            RefreshCounts();
            ShowList();
            if (nid == _openId) UpdateMeta();
        }
        else if (e.Data.GetData("pxdNotepad.Category") is string cid && cid != t.Id)
        {
            if (!_store.MoveCategory(cid, t.IsSpecial ? null : t.Id))
                return;   // into itself
            t.IsExpanded = true;
            BuildTree();
            ShowList();
        }
    }

    // ── sync ──

    /// <summary>Another PC's changes arrived (called on the UI thread).</summary>
    public void OnRemoteChanges(SyncChanges ch)
    {
        foreach (var id in ch.Images) _images.Forget(id);
        if (ch.Categories) BuildTree();
        else RefreshCounts();
        if (_openId is not null && ch.Notes.Contains(_openId) && !_dirty)
        {
            var full = _store.Load(_openId);
            if (full is null || full.Trashed) CloseNote();
            else if (full.Ver != _openVer) OpenNote(_openId, [], keepScroll: true);
        }
        if (SearchBox.Text.Trim().Length > 0) RunSearch();
        else ShowList();
    }

    public NoteStore Store => _store;
    public ImageStore ImagesStore => _images;
    public SearchEngine SearchEngine => _search;

    [Conditional("DEBUG")]
    private static void Log(string s) => Debug.WriteLine(s);
}
