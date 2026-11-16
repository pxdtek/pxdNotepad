using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using pxdNotepad.Data;
using pxdStandard;

namespace pxdNotepad.Views;

/// <summary>Make and change tags: a name and an icon (a built-in symbol in a colour, or any .ico/.png file).</summary>
public sealed class TagsWindow : Window
{
    private readonly TagStore _tags;
    private readonly ListBox _list = new() { MinHeight = 300 };
    private readonly TextBox _name = new() { FontSize = 14 };
    private readonly WrapPanel _glyphs = new();
    private readonly WrapPanel _colors = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Image _preview = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _previewName = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _fileInfo = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _heading = new() { FontSize = 11, FontWeight = FontWeights.SemiBold };
    private readonly Button _save = new() { Content = "Create tag", MinWidth = 110 };
    private readonly Button _delete = new() { Content = "Delete tag", MinWidth = 100 };
    private readonly Button _show = new() { Content = "Show its notes", Margin = new Thickness(8, 0, 0, 0) };
    private string? _editing;
    private string _glyph = "heart", _color = "#FF3B30";
    private byte[]? _png;

    /// <summary>Set when "Show its notes" was clicked: the tag's name, for the search box.</summary>
    public string? SearchFor { get; private set; }
    public bool Changed { get; private set; }

    public TagsWindow(TagStore tags)
    {
        _tags = tags;
        Width = 760;
        Height = 560;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // left: the tags
        _list.ItemTemplate = RowTemplate();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Row r) Edit(r.Tag); };
        var newTag = new Button { Content = "+ New tag", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(10, 8, 10, 10) };
        newTag.Click += (_, _) => { _list.SelectedItem = null; Edit(null); _name.Focus(); };
        var left = new DockPanel();
        DockPanel.SetDock(newTag, Dock.Bottom);
        left.Children.Add(newTag);
        var listHead = Label("TAGS");
        listHead.Margin = new Thickness(14, 12, 0, 4);
        DockPanel.SetDock(listHead, Dock.Top);
        left.Children.Add(listHead);
        left.Children.Add(_list);
        var leftBorder = new Border { Child = left, Width = 230, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftBorder.SetResourceReference(Border.BackgroundProperty, "Panel");
        leftBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        // right: the editor
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _previewName.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var preview = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 14) };
        preview.Children.Add(_preview);
        preview.Children.Add(_previewName);
        _name.TextChanged += (_, _) => _previewName.Text = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "Name";

        foreach (var (key, label, _) in TagIcons.Glyphs)
        {
            var b = new ToggleButton { Tag = key, ToolTip = label, Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4) };
            b.SetResourceReference(StyleProperty, "ToolToggle");
            b.Click += (_, _) => { _glyph = key; _png = null; Refresh(); };
            _glyphs.Children.Add(b);
        }
        foreach (var c in TagIcons.Colors)
        {
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Editor.DocSerializer.Brush(c) };
            var b = new ToggleButton { Content = dot, Tag = c, ToolTip = c, Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0) };
            b.SetResourceReference(StyleProperty, "ToolToggle");
            b.Click += (_, _) => { _color = c; Refresh(); };
            _colors.Children.Add(b);
        }
        var file = new Button { Content = "Use an icon file…" };
        file.Click += (_, _) => PickFile();
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        fileRow.Children.Add(file);
        fileRow.Children.Add(_fileInfo);
        _fileInfo.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        _save.SetResourceReference(StyleProperty, "AccentButton");
        _save.Click += (_, _) => Save();
        _delete.Click += (_, _) => DeleteTag();
        _show.Click += (_, _) => { if (_editing is not null) { SearchFor = _name.Text.Trim(); Close(); } };
        _delete.SetResourceReference(ForegroundProperty, "Danger");
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(_delete, Dock.Right);
        buttons.Children.Add(_delete);
        var lhs = new StackPanel { Orientation = Orientation.Horizontal };
        lhs.Children.Add(_save);
        lhs.Children.Add(_show);
        buttons.Children.Add(lhs);

        var form = new StackPanel { Margin = new Thickness(22, 14, 22, 18) };
        form.Children.Add(_heading);
        form.Children.Add(preview);
        form.Children.Add(Label("NAME (WHAT YOU SEARCH FOR)"));
        _name.Margin = new Thickness(0, 6, 0, 14);
        form.Children.Add(_name);
        form.Children.Add(Label("ICON"));
        var glyphHolder = new Border { Child = _glyphs, Margin = new Thickness(0, 8, 0, 0) };
        form.Children.Add(glyphHolder);
        form.Children.Add(_colors);
        var or = Note("or a picture of your own (.ico, .png, .jpg, .bmp):");
        or.Margin = new Thickness(0, 14, 0, 0);
        form.Children.Add(or);
        form.Children.Add(fileRow);
        form.Children.Add(buttons);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new DockPanel();
        DockPanel.SetDock(leftBorder, Dock.Left);
        root.Children.Add(leftBorder);
        root.Children.Add(scroll);

        Chrome.Apply(this, "Tags", root, BrandIcon.Image);
        Chrome.RememberPlacement(this, "tags");
        Load(null);
        Edit(null);
    }

    private sealed record Row(Tag Tag, ImageSource Icon, string Name, string Count);

    private static DataTemplate RowTemplate()
    {
        var t = new DataTemplate();
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var count = new FrameworkElementFactory(typeof(TextBlock));
        count.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Count)));
        count.SetValue(DockPanel.DockProperty, Dock.Right);
        count.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        count.SetValue(TextBlock.FontSizeProperty, 11.5);
        count.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Row.Icon)));
        img.SetValue(WidthProperty, 16.0);
        img.SetValue(HeightProperty, 16.0);
        img.SetValue(MarginProperty, new Thickness(0, 0, 9, 0));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        dock.AppendChild(count);
        dock.AppendChild(img);
        dock.AppendChild(name);
        t.VisualTree = dock;
        return t;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    private void Load(string? select)
    {
        var rows = _tags.All().Select(t =>
        {
            var n = _tags.CountNotes(t.Id);
            return new Row(t, TagIcons.For(t), t.Name, n == 0 ? "" : n.ToString());
        }).ToList();
        _list.ItemsSource = rows;
        if (select is not null) _list.SelectedItem = rows.FirstOrDefault(r => r.Tag.Id == select);
    }

    private void Edit(Tag? t)
    {
        _editing = t?.Id;
        _name.Text = t?.Name ?? "";
        _glyph = t is null || t.Glyph.Length == 0 ? "heart" : t.Glyph;
        _color = t is null || t.Color.Length == 0 ? "#FF3B30" : t.Color;
        _png = t?.Png;
        _heading.Text = t is null ? "NEW TAG" : "EDIT TAG";
        _save.Content = t is null ? "Create tag" : "Save changes";
        _delete.Visibility = t is null ? Visibility.Collapsed : Visibility.Visible;
        _show.Visibility = _delete.Visibility;
        _previewName.Text = t?.Name ?? "Name";
        Refresh();
    }

    /// <summary>Redraws the symbol buttons in the chosen colour and the preview.</summary>
    private void Refresh()
    {
        foreach (ToggleButton b in _glyphs.Children)
        {
            var key = (string)b.Tag;
            b.Content = new Image { Source = TagIcons.Glyph(key, _color), Width = 18, Height = 18 };
            b.IsChecked = _png is null && key == _glyph;
        }
        foreach (ToggleButton b in _colors.Children) b.IsChecked = _png is null && (string)b.Tag == _color;
        _preview.Source = _png is not null ? TagIcons.For(new Tag("preview", "", "", "", _png, Environment.TickCount64)) : TagIcons.Glyph(_glyph, _color);
        _fileInfo.Text = _png is not null ? "Using your picture (pick a symbol to go back)" : "";
    }

    private void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick an icon", Filter = "Icons and pictures|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _png = TagIcons.ImportFile(dlg.FileName);
            if (_name.Text.Trim().Length == 0) _name.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            Refresh();
        }
        catch (Exception e)
        {
            Dialogs.Info(this, "Use an icon file", $"Couldn't read that file: {e.Message}");
        }
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { _name.Focus(); return; }
        if (_tags.All().Any(t => t.Id != _editing && string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Dialogs.Info(this, "Tags", $"There's already a tag called \"{name}\".");
            return;
        }
        string id;
        if (_editing is null) id = _tags.Add(name, _png is null ? _glyph : "", _png is null ? _color : "", _png).Id;
        else { _tags.Update(_editing, name, _png is null ? _glyph : "", _png is null ? _color : "", _png); id = _editing; }
        Changed = true;
        TagIcons.Forget();
        Load(id);
    }

    private void DeleteTag()
    {
        if (_editing is null) return;
        var n = _tags.CountNotes(_editing);
        var msg = n == 0 ? $"Delete the tag \"{_name.Text.Trim()}\"?" : $"Delete the tag \"{_name.Text.Trim()}\"? It comes off the {n} note{(n == 1 ? "" : "s")} that have it (the notes stay).";
        if (!Dialogs.Confirm(this, "Delete tag", msg, "Delete tag", danger: true)) return;
        _tags.Delete(_editing);
        Changed = true;
        Load(null);
        Edit(null);
    }
}
