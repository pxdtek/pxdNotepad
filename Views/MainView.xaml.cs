using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pxdNotepad.Data;
using pxdNotepad.Editor;
using pxdNotepad.Images;
using pxdNotepad.Sync;

namespace pxdNotepad.Views;

/// <summary>The window's content: the folder/note tree on the left, the editor on the right. Search lives in the
/// title bar (<see cref="SearchBar"/>, placed there by MainWindow).</summary>
public partial class MainView : UserControl
{
    private readonly NoteStore _store;
    private readonly ImageStore _images;
    private readonly TagStore _tags;
    private Dictionary<string, Tag> _tagById = [];
    private readonly SearchEngine _search;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<SideNode> _roots = [];
    private readonly Dictionary<string, SideNode> _folders = [];
    private readonly Dictionary<string, SideNode> _notes = [];
    private SideNode? _trash;
    private Point _dragStart;

    public SyncService? Sync { get; set; }
    /// <summary>Raised when the sidebar or focus mode changes (MainWindow resizes its title bar).</summary>
    public event Action? LayoutModeChanged;

    public MainView(NoteStore store, AppSettings settings, string? dbPath = null)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _images = new ImageStore(store.Connection);
        _tags = new TagStore(store.Connection);
        _tags.Changed += () => Sync?.Kick(TimeSpan.FromSeconds(4));
        ImageBox.Store = _images;
        _search = new SearchEngine(dbPath);
        _images.Changed += () => Sync?.Kick(TimeSpan.FromSeconds(4));
        _store.Changed += () => Sync?.Kick(TimeSpan.FromSeconds(4));

        SideCol.Width = new GridLength(Math.Clamp(settings.SideWidth, 170, 700));
        NewNoteButton.Content = MarkupWindow.Glyph("M4,2 L9.5,2 L12.5,5 L12.5,14 L4,14 Z M8.25,7 L8.25,12 M5.75,9.5 L10.75,9.5");
        NewFolderButton.Content = MarkupWindow.Glyph("M1.5,4 L1.5,13 L14.5,13 L14.5,5.5 L7.5,5.5 L6,3.5 L1.5,3.5 Z M8,7.3 L8,11.3 M6,9.3 L10,9.3");
        TagsButton.Content = MarkupWindow.Glyph("M1.5,2.5 L1.5,7.8 L8.7,15 L15,8.7 L7.8,1.5 L2.5,1.5 Z M5,4.3 A0.8,0.8 0 1 1 4.99,4.3 Z");
        TagsButton.Click += (_, _) => OpenTags();
        CollapseAllButton.Content = MarkupWindow.Glyph("M4,6.5 L8,3 L12,6.5 M4,13 L8,9.5 L12,13");

        Tree.ItemsSource = _roots;
        Tree.SelectedItemChanged += (_, _) =>
        {
            if (Tree.SelectedItem is SideNode { IsNote: true } n && n.Id != _openId) OpenNote(n.Id, []);
        };
        Tree.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler((_, e) => (e.OriginalSource as TreeViewItem)?.BringIntoView()));
        Tree.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemUnder<TreeViewItem>(e.OriginalSource) is { DataContext: SideNode n }) n.IsSelected = true;
        };
        Tree.ContextMenuOpening += (_, e) =>
        {
            var n = ItemUnder<TreeViewItem>(e.OriginalSource)?.DataContext as SideNode;
            Tree.ContextMenu = TreeMenu(n);
        };
        Tree.ContextMenu = new ContextMenu();
        Tree.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(null);
            // A double-click would toggle a folder twice (once per click below, once by TreeView): one per click is enough.
            if (e.ClickCount == 2 && ItemUnder<TreeViewItem>(e.OriginalSource) is { DataContext: SideNode { IsNote: false } }) e.Handled = true;
        };
        // One click on a folder's row opens or closes it (not just the arrow).
        Tree.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemUnder<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource) is not null) return;
            if (ItemUnder<TreeViewItem>(e.OriginalSource) is not { DataContext: SideNode { IsNote: false } n } || n.Children.Count == 0) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _dragStart.X) > 4 || Math.Abs(p.Y - _dragStart.Y) > 4) return;
            n.IsExpanded = !n.IsExpanded;
        };
        Tree.PreviewMouseMove += TreeDragStart;
        Tree.DragOver += TreeDragOver;
        Tree.Drop += TreeDrop;
        Tree.KeyDown += TreeKeys;
        // A click on empty space below the rows clears the selection, so a new note goes to the top level.
        Tree.MouseLeftButtonDown += (_, e) =>
        {
            if (ItemUnder<TreeViewItem>(e.OriginalSource) is null && Tree.SelectedItem is SideNode s) s.IsSelected = false;
        };

        NewNoteButton.Click += (_, _) => NewNote();
        NewFolderButton.Click += (_, _) => NewFolder(SelectedFolder());
        CollapseAllButton.Click += (_, _) => { foreach (var n in _roots.SelectMany(r => r.Walk())) if (n.Kind != NodeKind.Note) n.IsExpanded = false; };

        SetupEditor();
        BuildSearch();
        BuildTree();
        if (settings.LastNote is { } last && _store.Load(last) is { Trashed: false })
            OpenNote(last, []);
        ApplyLayoutMode();
        PreviewKeyDown += OnKeys;
    }

    public void SaveLayout()
    {
        SaveNow();
        if (_settings.SidebarOpen && !_settings.Focus && SideCol.ActualWidth > 50) _settings.SideWidth = SideCol.ActualWidth;
        _settings.Collapsed = _roots.SelectMany(r => r.Walk()).Where(n => n.Kind != NodeKind.Note && !n.IsExpanded).Select(n => n.Id).ToList();
        _settings.Save();
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.F && !shift) { FocusSearch(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.N) { NewFolder(SelectedFolder()); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { NewNote(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveNow(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.L) { ToggleLineNumbers(); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemPipe) { ToggleSidebar(); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFocus(); e.Handled = true; }
    }

    private Window? Owner => Window.GetWindow(this);

    // ── sidebar / focus mode ──

    public bool FocusMode => _settings.Focus;

    public void ToggleSidebar()
    {
        if (_settings.Focus) { _settings.Focus = false; _settings.SidebarOpen = true; }
        else
        {
            if (_settings.SidebarOpen && SideCol.ActualWidth > 50) _settings.SideWidth = SideCol.ActualWidth;
            _settings.SidebarOpen = !_settings.SidebarOpen;
        }
        ApplyLayoutMode();
        _settings.Save();
    }

    /// <summary>Writing only: no sidebar, toolbar or status line, a slim title bar (F11 again to come back).</summary>
    public void ToggleFocus()
    {
        if (!_settings.Focus && _settings.SidebarOpen && SideCol.ActualWidth > 50) _settings.SideWidth = SideCol.ActualWidth;
        _settings.Focus = !_settings.Focus;
        ApplyLayoutMode();
        _settings.Save();
        if (_openId is not null) Editor.Focus();
    }

    private void ApplyLayoutMode()
    {
        var side = _settings.SidebarOpen && !_settings.Focus;
        Sidebar.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = Sidebar.Visibility;
        SideCol.MinWidth = side ? 170 : 0;
        SideCol.Width = side ? new GridLength(Math.Clamp(_settings.SideWidth, 170, 700)) : new GridLength(0);
        var chrome = _settings.Focus ? Visibility.Collapsed : Visibility.Visible;
        ToolbarBar.Visibility = chrome;
        StatusBar.Visibility = chrome;
        NoteMeta.Visibility = chrome;
        LayoutModeChanged?.Invoke();
    }

    // ── the tree ──

    private SideNode? Selected => Tree.SelectedItem as SideNode;
    private string? SelectedFolder() => Selected?.FolderId;

    /// <summary>Rebuilds the tree from the database, keeping what's open, selected and expanded.</summary>
    public void BuildTree()
    {
        var expandedNow = _roots.Count == 0 ? null : _roots.SelectMany(r => r.Walk()).Where(n => n.Kind != NodeKind.Note).ToDictionary(n => n.Id, n => n.IsExpanded);
        var selectedId = Selected?.Id;
        _roots.Clear();
        _folders.Clear();
        _notes.Clear();

        bool Expanded(string id) => expandedNow?.TryGetValue(id, out var e) == true ? e : !_settings.Collapsed.Contains(id);

        _tagById = _tags.All().ToDictionary(t => t.Id);
        var cats = _store.Categories();
        var byParent = cats.GroupBy(c => c.Parent ?? "").ToDictionary(g => g.Key, g => g.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        var known = cats.Select(c => c.Id).ToHashSet();
        void Add(Category c, SideNode? parent, ICollection<SideNode> into, int depth)
        {
            if (depth > 40) return;
            var n = new SideNode(NodeKind.Folder, c.Id, c.Name, parent);
            _folders[c.Id] = n;
            into.Add(n);
            if (byParent.TryGetValue(c.Id, out var kids))
                foreach (var k in kids) Add(k, n, n.Children, depth + 1);
        }
        // Top level: no parent, or a parent that's gone (deleted on another PC).
        foreach (var c in cats.Where(c => c.Parent is null || !known.Contains(c.Parent)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Add(c, null, _roots, 0);

        // Notes go after their folder's sub-folders, A to Z (so they don't jump around while you type).
        foreach (var r in _store.Notes(null).OrderBy(r => Title(r.Title), StringComparer.CurrentCultureIgnoreCase))
        {
            var parent = r.Cat is not null && _folders.TryGetValue(r.Cat, out var f) ? f : null;
            var n = new SideNode(NodeKind.Note, r.Id, Title(r.Title), parent) { Updated = r.Updated, Preview = r.Preview, TagIcons = Chips(r.Tags) };
            (parent?.Children ?? (ICollection<SideNode>)_roots).Add(n);
            _notes[r.Id] = n;
        }

        _trash = new SideNode(NodeKind.Trash, SideNode.TrashId, "Trash", null);
        foreach (var r in _store.Notes(null, trash: true))
        {
            var n = new SideNode(NodeKind.Note, r.Id, Title(r.Title), _trash) { Updated = r.Updated, Preview = r.Preview, TagIcons = Chips(r.Tags) };
            _trash.Children.Add(n);
            _notes[r.Id] = n;
        }
        _roots.Add(_trash);

        foreach (var n in _roots.SelectMany(r => r.Walk()).Where(n => n.Kind != NodeKind.Note))
        {
            n.IsExpanded = n.Kind == NodeKind.Trash ? expandedNow?.GetValueOrDefault(n.Id) == true : Expanded(n.Id);
            n.Count = n.Walk().Count(x => x.IsNote);
        }

        var pick = (_openId is not null && _notes.TryGetValue(_openId, out var open) ? open : null)
                   ?? (selectedId is not null ? (_folders.GetValueOrDefault(selectedId) ?? _notes.GetValueOrDefault(selectedId)) : null);
        if (pick is not null) Reveal(pick);
    }

    /// <summary>A note's tags (ids, space-separated) as icons, in the order the tags are listed.</summary>
    private IReadOnlyList<TagChip> Chips(string tags)
    {
        var ids = TagStore.Split(tags);
        if (ids.Length == 0) return [];
        return _tagById.Values.Where(t => ids.Contains(t.Id)).Select(t => new TagChip(t.Id, t.Name, TagIcons.For(t))).ToList();
    }

    public TagStore Tags => _tags;

    /// <summary>The tag manager; afterwards the tree shows any changed icons.</summary>
    public void OpenTags()
    {
        var w = new TagsWindow(_tags) { Owner = Owner };
        w.ShowDialog();
        if (w.Changed) BuildTree();
        if (w.SearchFor is { Length: > 0 } s) { SearchBox.Text = s; FocusSearch(); }
    }

    private MenuItem TagsMenu(SideNode n)
    {
        var menu = new MenuItem { Header = "Tags" };
        var on = _tags.TagsOf(n.Id);
        foreach (var t in _tagById.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var i = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = on.Contains(t.Id), Icon = new Image { Source = TagIcons.For(t), Width = 16, Height = 16 }, StaysOpenOnClick = true };
            i.Click += (_, _) =>
            {
                i.IsChecked = _tags.Toggle(n.Id, t.Id);
                n.TagIcons = Chips(string.Join(' ', _tags.TagsOf(n.Id)));
                if (SearchText.Length > 0) RunSearch();
            };
            menu.Items.Add(i);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = _tagById.Count == 0 ? "Make a tag…" : "New or edit tags…" };
        manage.Click += (_, _) => OpenTags();
        menu.Items.Add(manage);
        return menu;
    }

    private static string Title(string t) => string.IsNullOrWhiteSpace(t) ? "Untitled" : t.Trim();

    /// <summary>Expands a row's folders and selects it.</summary>
    private void Reveal(SideNode n)
    {
        for (var p = n.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        n.IsSelected = true;
    }

    private void SelectNote(string id)
    {
        if (_notes.TryGetValue(id, out var n) && !n.IsSelected) Reveal(n);
    }

    /// <summary>A note's title changed: rename and re-sort its row without rebuilding.</summary>
    private void UpdateNoteRow(string id, string title, string plain)
    {
        if (!_notes.TryGetValue(id, out var n)) return;
        n.Updated = Db.Now();
        n.Preview = NoteStore.Preview(plain);
        var name = Title(title);
        if (n.Name == name) return;
        n.Name = name;
        var list = n.Parent?.Children ?? _roots;
        var from = list.IndexOf(n);
        var notes = list.Where(x => x.IsNote && x != n).ToList();
        var after = notes.LastOrDefault(x => string.Compare(x.Name, name, StringComparison.CurrentCultureIgnoreCase) <= 0);
        var to = after is null ? list.Count(x => !x.IsNote && x.Kind != NodeKind.Trash) : list.IndexOf(after) + (list.IndexOf(after) < from ? 1 : 0);
        if (n.Parent is null && to >= list.Count) to = list.Count - 1;   // Trash stays last
        to = Math.Clamp(to, 0, list.Count - 1);
        if (to != from) list.Move(from, to);
        n.IsSelected = true;
    }

    private void RefreshCounts()
    {
        foreach (var n in _roots.SelectMany(r => r.Walk()).Where(n => n.Kind != NodeKind.Note))
            n.Count = n.Walk().Count(x => x.IsNote);
    }

    private void TreeKeys(object sender, KeyEventArgs e)
    {
        if (Selected is not { } n) return;
        if (e.Key == Key.F2)
        {
            if (n.IsFolder) Rename(n);
            else if (n.IsNote && !n.InTrash) { TitleBox.Focus(); TitleBox.SelectAll(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (n.IsFolder) DeleteFolder(n);
            else if (n.IsNote) TrashOrPurge(n);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && n.IsNote)
        {
            Editor.Focus();
            e.Handled = true;
        }
    }

    private ContextMenu TreeMenu(SideNode? n)
    {
        var m = new ContextMenu();
        MenuItem Item(ItemsControl into, string text, Action a, string? keys = null)
        {
            var i = new MenuItem { Header = text, InputGestureText = keys ?? "" };
            i.Click += (_, _) => a();
            into.Items.Add(i);
            return i;
        }
        if (n is null)
        {
            Item(m, "New note", () => NewNote(null), "Ctrl+N");
            Item(m, "New folder", () => NewFolder(null), "Ctrl+Shift+N");
            Item(m, "Tags…", OpenTags);
            return m;
        }
        if (n.Kind == NodeKind.Trash)
        {
            Item(m, "Empty trash…", EmptyTrash);
            return m;
        }
        if (n.IsNote && n.InTrash)
        {
            Item(m, "Restore", () => { _store.Trash(n.Id, false); BuildTree(); });
            Item(m, "Delete for good…", () => TrashOrPurge(n), "Del");
            return m;
        }
        if (n.IsFolder)
        {
            Item(m, "New note here", () => NewNote(n.Id), "Ctrl+N");
            Item(m, "New sub-folder", () => NewFolder(n.Id), "Ctrl+Shift+N");
            m.Items.Add(new Separator());
            Item(m, "Rename…", () => Rename(n), "F2");
            m.Items.Add(MoveMenu(n));
            Item(m, "Delete folder…", () => DeleteFolder(n), "Del");
            return m;
        }
        Item(m, "Rename", () => { if (n.Id != _openId) OpenNote(n.Id, []); TitleBox.Focus(); TitleBox.SelectAll(); }, "F2");
        m.Items.Add(TagsMenu(n));
        m.Items.Add(MoveMenu(n));
        Item(m, "Duplicate", () =>
        {
            SaveNow();
            if (_store.Load(n.Id) is not { } f) return;
            var id = _store.NewNote(f.Cat, Title(f.Title) + " (copy)", f.Doc, f.Body);
            if (_tags.TagsOf(n.Id) is { Length: > 0 } copyTags) _tags.SetTags(id, copyTags);
            BuildTree();
            OpenNote(id, []);
        });
        m.Items.Add(new Separator());
        Item(m, "Move to Trash", () => TrashOrPurge(n), "Del");
        return m;
    }

    /// <summary>"Move to ▸" with every folder (and the top level).</summary>
    private MenuItem MoveMenu(SideNode n)
    {
        var move = new MenuItem { Header = "Move to" };
        void Add(string label, string? target)
        {
            var i = new MenuItem { Header = label };
            i.Click += (_, _) => MoveTo(n, target);
            move.Items.Add(i);
        }
        Add("Top level", null);
        foreach (var f in _roots.Where(r => r.IsFolder).SelectMany(r => r.Walk()).Where(x => x.IsFolder))
        {
            if (n.IsFolder && f.Walk().Contains(n)) continue;   // not into itself
            Add(new string(' ', f.Depth * 3) + f.Name, f.Id);
        }
        return move;
    }

    private void MoveTo(SideNode n, string? folder)
    {
        SaveNow();
        if (n.IsNote)
        {
            if (_store.Load(n.Id)?.Trashed == true) _store.Trash(n.Id, false);
            _store.MoveNote(n.Id, folder);
        }
        else if (n.IsFolder && !_store.MoveCategory(n.Id, folder)) return;
        if (folder is not null && _folders.TryGetValue(folder, out var f)) f.IsExpanded = true;
        BuildTree();
        if (n.IsNote && n.Id == _openId) UpdateMeta();
    }

    private void NewFolder(string? parent)
    {
        var where = parent is not null && _folders.TryGetValue(parent, out var p) ? $" inside {p.Name}" : "";
        var name = Dialogs.Prompt(Owner, parent is null ? "New folder" : "New sub-folder", $"Name the new folder{where}:", "", "Create");
        if (name is null) return;
        var c = _store.AddCategory(name, parent);
        if (parent is not null && _folders.TryGetValue(parent, out var pn)) pn.IsExpanded = true;
        BuildTree();
        if (_folders.TryGetValue(c.Id, out var node)) Reveal(node);
    }

    private void Rename(SideNode n)
    {
        var name = Dialogs.Prompt(Owner, "Rename folder", "New name:", n.Name, "Rename");
        if (name is null || name == n.Name) return;
        _store.RenameCategory(n.Id, name);
        BuildTree();
        UpdateMeta();
    }

    private void DeleteFolder(SideNode n)
    {
        var up = n.Parent is { IsFolder: true } ? n.Parent.Name : "the top level";
        if (!Dialogs.Confirm(Owner, "Delete folder", $"Delete the folder \"{n.Name}\"? Its notes and sub-folders aren't deleted: they move up to {up}.", "Delete folder", danger: true))
            return;
        _store.DeleteCategory(n.Id);
        BuildTree();
        UpdateMeta();
    }

    private void EmptyTrash()
    {
        var count = _trash?.Count ?? 0;
        if (count == 0) return;
        if (!Dialogs.Confirm(Owner, "Empty trash", $"Delete the {count} note{(count == 1 ? "" : "s")} in Trash for good? This can't be undone (on any PC that syncs).", "Delete for good", danger: true))
            return;
        _store.EmptyTrash();
        if (_openId is not null && _store.Load(_openId) is null) CloseNote();
        BuildTree();
    }

    public static string When(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var now = DateTime.Now;
        if (t.Date == now.Date) return t.ToString("h:mm tt");
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday";
        if ((now - t).TotalDays < 7) return t.ToString("dddd");
        return t.Year == now.Year ? t.ToString("MMM d") : t.ToString("MMM d, yyyy");
    }

    private string FolderPath(string? cat) => cat is not null && _folders.TryGetValue(cat, out var n) ? n.Path : "Top level";

    private void TrashOrPurge(SideNode n)
    {
        var note = _store.Load(n.Id);
        if (note is null) return;
        if (note.Trashed)
        {
            if (!Dialogs.Confirm(Owner, "Delete note", $"Delete \"{n.Name}\" for good? This can't be undone.", "Delete for good", danger: true)) return;
            _store.Purge(n.Id);
        }
        else
        {
            if (n.Id == _openId) SaveNow();
            _store.Trash(n.Id, true);
        }
        if (n.Id == _openId) CloseNote();
        BuildTree();
    }

    public void NewNote() => NewNote(SelectedFolder());

    private void NewNote(string? folder)
    {
        SaveNow();
        if (Selected?.InTrash == true) folder = null;
        var id = _store.NewNote(folder);
        if (folder is not null && _folders.TryGetValue(folder, out var f)) f.IsExpanded = true;
        BuildTree();
        OpenNote(id, []);
        TitleBox.Focus();
    }

    // ── dragging notes and folders around the tree ──

    private static T? ItemUnder<T>(object source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void TreeDragStart(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemUnder<TreeViewItem>(e.OriginalSource) is not { DataContext: SideNode n } || n.Kind == NodeKind.Trash) return;
        if (ItemUnder<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource) is not null) return;
        SaveNow();
        DragDrop.DoDragDrop(Tree, new DataObject("pxdNotepad.Node", n.Id), DragDropEffects.Move);
    }

    private SideNode? Dragged(DragEventArgs e) =>
        e.Data.GetData("pxdNotepad.Node") is string id ? _folders.GetValueOrDefault(id) ?? _notes.GetValueOrDefault(id) : null;

    /// <summary>Where a drop lands: a folder (or the folder of the note it's on), Trash, or the top level (empty space).</summary>
    private (bool Ok, bool Trash, string? Folder) Target(DragEventArgs e, SideNode dragged)
    {
        var under = ItemUnder<TreeViewItem>(e.OriginalSource)?.DataContext as SideNode;
        if (under?.Kind == NodeKind.Trash || under?.InTrash == true) return (dragged.IsNote, true, null);
        var folder = under?.FolderId;
        if (dragged.IsFolder && folder is not null && _folders.TryGetValue(folder, out var f) && f.Walk().Contains(dragged) && f != dragged.Parent)
            return (false, false, null);   // into itself
        if (dragged.IsFolder && folder == dragged.Id) return (false, false, null);
        return (true, false, folder);
    }

    private void TreeDragOver(object sender, DragEventArgs e)
    {
        var d = Dragged(e);
        e.Effects = d is not null && Target(e, d).Ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TreeDrop(object sender, DragEventArgs e)
    {
        if (Dragged(e) is not { } d) return;
        var (ok, trash, folder) = Target(e, d);
        if (!ok) return;
        if (trash)
        {
            if (d.Id == _openId) CloseNote();
            _store.Trash(d.Id, true);
            BuildTree();
            return;
        }
        MoveTo(d, folder);
    }

    // ── sync ──

    /// <summary>Another PC's changes arrived (called on the UI thread).</summary>
    public void OnRemoteChanges(SyncChanges ch)
    {
        foreach (var id in ch.Images) _images.Forget(id);
        if (ch.Tags) TagIcons.Forget();
        if (_openId is not null && ch.Notes.Contains(_openId) && !_dirty)
        {
            var full = _store.Load(_openId);
            if (full is null || full.Trashed) CloseNote();
            else if (full.Ver != _openVer) OpenNote(_openId, [], keepScroll: true);
        }
        if (ch.Categories || ch.Tags || ch.Notes.Count > 0) BuildTree();
        if (SearchText.Length > 0) RunSearch();
    }

    public NoteStore Store => _store;
    public ImageStore ImagesStore => _images;
}
