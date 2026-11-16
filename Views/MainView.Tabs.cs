using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using pxdNotepad.Editor;

namespace pxdNotepad.Views;

/// <summary>Tabs inside a note: each its own page of text, saved and synced with the note.</summary>
public partial class MainView
{
    public sealed class NoteTab(string name, FlowDocument doc)
    {
        public string Name { get; set; } = name;
        public FlowDocument Doc { get; } = doc;
        public double Scroll { get; set; }
    }

    private readonly List<NoteTab> _tabs = [];
    private int _tab;

    public IReadOnlyList<NoteTab> Tabs => _tabs;
    public int ActiveTab => _tab;

    /// <summary>Builds the open note's tabs from its saved JSON (or one tab for a note without tabs).</summary>
    private void LoadTabs(string json, string body)
    {
        _tabs.Clear();
        var parsed = DocSerializer.ReadTabs(json);
        if (parsed.Count <= 1)
        {
            var doc = NewDocument();
            DocSerializer.Read(doc, json, body);
            _tabs.Add(new NoteTab(parsed.Count == 1 ? parsed[0].Name : "Tab 1", doc));
        }
        else
        {
            foreach (var (name, blocks) in parsed)
            {
                var doc = NewDocument();
                foreach (var b in blocks) doc.Blocks.Add(b);
                if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
                _tabs.Add(new NoteTab(name, doc));
            }
        }
        _tab = 0;
    }

    private string WriteTabs(out string plain) =>
        DocSerializer.WriteTabs(_tabs.Select(t => (t.Name, t.Doc)).ToList(), _images, out plain, _daily);

    /// <summary>Shows tab <paramref name="i"/> in the editor (keeping each tab's scroll position).</summary>
    public void SwitchTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        if (i == _tab && _tab >= 0 && Editor.Document == _tabs[i].Doc) { BuildTabStrip(); return; }
        if (_tab >= 0 && _tab < _tabs.Count && Editor.Document == _tabs[_tab].Doc) _tabs[_tab].Scroll = Editor.VerticalOffset;
        _tab = i;
        HideSecrets();
        var was = _loading;
        _loading = true;
        Editor.Document = _tabs[i].Doc;
        _loading = was;
        var offset = _tabs[i].Scroll;
        Gutter.Reset();
        Links.Queue();
        Blur.Queue();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Editor.ScrollToVerticalOffset(offset));
        BuildTabStrip();
        UpdateToolbar();
    }

    public void AddTab()
    {
        if (_openId is null || Editor.IsReadOnly) return;
        var n = _tabs.Count + 1;
        while (_tabs.Any(t => t.Name == $"Tab {n}")) n++;
        var doc = NewDocument();
        doc.Blocks.Add(new Paragraph());
        _tabs.Insert(_tab + 1, new NoteTab($"Tab {n}", doc));
        SwitchTab(_tab + 1);
        MarkDirty();
        Editor.Focus();
    }

    public void RenameTab(int i)
    {
        if (i < 0 || i >= _tabs.Count || Editor.IsReadOnly) return;
        var name = Dialogs.Prompt(Owner, "Rename tab", "Tab name:", _tabs[i].Name, "Rename");
        if (name is null || name == _tabs[i].Name) return;
        _tabs[i].Name = name;
        MarkDirty();
        BuildTabStrip();
    }

    public void DeleteTab(int i, bool ask = true)
    {
        if (_tabs.Count < 2 || i < 0 || i >= _tabs.Count || Editor.IsReadOnly) return;
        var text = new TextRange(_tabs[i].Doc.ContentStart, _tabs[i].Doc.ContentEnd).Text.Trim();
        if (ask && text.Length > 0 && !Dialogs.Confirm(Owner, "Delete tab", $"Delete the tab \"{_tabs[i].Name}\" and everything on it?", "Delete tab", danger: true))
            return;
        _tabs.RemoveAt(i);
        var next = Math.Min(i, _tabs.Count - 1);
        _tab = -1;
        SwitchTab(next);
        MarkDirty();
    }

    public void MoveTab(int i, int by)
    {
        var j = i + by;
        if (i < 0 || j < 0 || i >= _tabs.Count || j >= _tabs.Count) return;
        (_tabs[i], _tabs[j]) = (_tabs[j], _tabs[i]);
        if (_tab == i) _tab = j;
        else if (_tab == j) _tab = i;
        MarkDirty();
        BuildTabStrip();
    }

    /// <summary>The strip under the title: a small "+ Tab" when the note has one tab, the tabs when it has more.</summary>
    private void BuildTabStrip()
    {
        TabStrip.Children.Clear();
        if (_openId is null) { TabStrip.Visibility = Visibility.Collapsed; return; }
        var many = _tabs.Count > 1;
        TabStrip.Visibility = !many && (_settings.Focus || Editor.IsReadOnly) ? Visibility.Collapsed : Visibility.Visible;
        if (many)
        {
            for (var i = 0; i < _tabs.Count; i++)
            {
                var index = i;
                var label = new TextBlock { Text = _tabs[i].Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
                var b = new ToggleButton { Content = label, IsChecked = i == _tab, Padding = new Thickness(12, 0, 12, 0), Height = 28, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Double-click to rename · right-click for more" };
                b.SetResourceReference(StyleProperty, "ToolToggle");
                b.Click += (_, _) => SwitchTab(index);
                b.MouseDoubleClick += (_, e) => { RenameTab(index); e.Handled = true; };
                var menu = new ContextMenu();
                void Item(string text, Action a, bool enabled = true)
                {
                    var it = new MenuItem { Header = text, IsEnabled = enabled };
                    it.Click += (_, _) => a();
                    menu.Items.Add(it);
                }
                Item("Rename…", () => RenameTab(index));
                Item("Move left", () => MoveTab(index, -1), index > 0);
                Item("Move right", () => MoveTab(index, 1), index < _tabs.Count - 1);
                menu.Items.Add(new Separator());
                Item("Delete tab…", () => DeleteTab(index));
                b.ContextMenu = menu;
                TabStrip.Children.Add(b);
            }
        }
        if (Editor.IsReadOnly) return;
        var add = new Button { Content = many ? "+" : "+ Tab", ToolTip = "New tab in this note (Ctrl+T)", Height = 28, Padding = new Thickness(many ? 9 : 10, 0, many ? 9 : 10, 0), FontSize = many ? 15 : 12 };
        add.SetResourceReference(StyleProperty, "ToolButton");
        add.SetResourceReference(ForegroundProperty, "Dim");
        add.Click += (_, _) => AddTab();
        TabStrip.Children.Add(add);
    }

    /// <summary>Opened from a search: the first tab that has a match comes up (its words get selected after).</summary>
    private void ShowTabWith(string[] terms)
    {
        if (_tabs.Count < 2) return;
        foreach (var t in terms.OrderByDescending(t => t.Length))
            for (var i = 0; i < _tabs.Count; i++)
                if (new TextRange(_tabs[i].Doc.ContentStart, _tabs[i].Doc.ContentEnd).Text.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || _tabs[i].Name.Contains(t, StringComparison.OrdinalIgnoreCase))
                {
                    SwitchTab(i);
                    return;
                }
    }
}
