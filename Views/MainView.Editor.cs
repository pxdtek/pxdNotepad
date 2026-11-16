using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using pxdNotepad.Editor;
using pxdNotepad.Images;

namespace pxdNotepad.Views;

public partial class MainView
{
    public RichTextBox Editor { get; private set; } = null!;
    public LineGutter Gutter { get; private set; } = null!;
    public LinkLayer Links { get; private set; } = null!;
    public BlurLayer Blur { get; private set; } = null!;
    private string? _openId;
    private long _openVer;
    private bool _dirty, _loading, _toolbarSync;
    /// <summary>The open note has "Date each day's entries" on.</summary>
    private bool _daily;
    private ToggleButton _dailyButton = null!;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private ComboBox _font = null!, _size = null!;
    private ToggleButton _bold = null!, _italic = null!, _underline = null!, _strike = null!, _bullets = null!, _numbers = null!, _lines = null!;
    private ToggleButton _left = null!, _center = null!, _right = null!;
    private Border _colorBar = null!, _highlightBar = null!;
    private Brush _lastColor = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
    private Brush? _lastHighlight = DocSerializer.Brush("#80FFE600");
    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic"];

    public static readonly string[] TextColors =
    [
        "#000000", "#434343", "#666666", "#999999", "#CCCCCC", "#FFFFFF",
        "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#39C5CF",
        "#0A84FF", "#5856D6", "#AF52DE", "#FF2D55", "#A2845E", "#8E8E93",
        "#B71C1C", "#E65100", "#F9A825", "#1B5E20", "#006064", "#0D47A1",
    ];
    public static readonly string[] HighlightColors = ["#80FFE600", "#80FFB300", "#80FF7A00", "#80FF3B30", "#80FF2D95", "#80AF52DE", "#800A84FF", "#8000C7BE", "#8034C759", "#80A2D600", "#80A0A0A0", "#80606875"];

    private void SetupEditor()
    {
        Editor = new RichTextBox
        {
            AcceptsTab = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(18, 4, 24, 60),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsInactiveSelectionHighlightEnabled = true,
            SelectionOpacity = 0.35,
            FocusVisualStyle = null,
        };
        Editor.SetResourceReference(TextBoxBase.CaretBrushProperty, "Text");
        Editor.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Accent");
        Editor.SetResourceReference(ForegroundProperty, "Text");
        SpellCheck.SetIsEnabled(Editor, _settings.SpellCheck);
        Editor.Document = NewDocument();
        Gutter = new LineGutter(Editor) { Visibility = _settings.LineNumbers ? Visibility.Visible : Visibility.Collapsed, MatchTextSize = _settings.LineNumbersMatchSize };
        Grid.SetColumn(Editor, 1);
        EditorGrid.Children.Add(Gutter);
        EditorGrid.Children.Add(Editor);
        // Phone numbers, emails and web links: underlined, click to open (calls ask first), Ctrl+click to edit.
        Links = new LinkLayer(Editor);
        Grid.SetColumn(Links, 1);
        EditorGrid.Children.Add(Links);
        // Blurred text (passwords for a moment): drawn smeared, copies as the real text.
        Blur = new BlurLayer(Editor) { Outline = _settings.BlurOutline };
        Grid.SetColumn(Blur, 1);
        EditorGrid.Children.Add(Blur);
        Editor.PreviewMouseMove += (_, e) =>
        {
            var hit = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? null : Links.At(e.GetPosition(Editor));
            Editor.Cursor = hit is null ? null : Cursors.Hand;
            var tip = hit is null ? null : LinkTip(hit.Link);
            if (!Equals(Editor.ToolTip, tip)) Editor.ToolTip = tip;
        };
        Editor.MouseLeave += (_, _) => { Editor.Cursor = null; Editor.ToolTip = null; };
        Editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            if (Links.At(e.GetPosition(Editor)) is not { } hit) return;
            e.Handled = true;
            OpenLink(hit.Link);
        };
        // The editor's own menu (WPF builds one whenever ContextMenu is empty, before anyone else sees the right-click)
        // is replaced: with a ContextMenu set, ours is the one that opens, rebuilt for what's under the mouse each time.
        Editor.ContextMenu = new ContextMenu();
        Editor.ContextMenuOpening += (_, _) =>
        {
            var menu = Links.At(Mouse.GetPosition(Editor)) is { } hit ? LinkMenu(hit.Link) : EditMenu();
            menu.PlacementTarget = Editor;
            Editor.ContextMenu = menu;
        };

        Editor.TextChanged += (_, _) => { if (!_loading) MarkDirty(); };
        Editor.SelectionChanged += (_, _) => UpdateToolbar();
        // A new day's first words in a dated note get the day's line above them.
        Editor.PreviewTextInput += (_, e) => { if (e.Text.Length > 0 && !char.IsControl(e.Text[0])) EnsureTodayDivider(); };
        Editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && EnsureTodayDivider(startNewLine: true)) e.Handled = true;
        };
        TitleBox.TextChanged += (_, _) =>
        {
            TitleHint.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_loading) MarkDirty();
        };
        TitleBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Down) { Editor.Focus(); Editor.CaretPosition = Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward); e.Handled = true; }
        };
        _saveTimer.Tick += (_, _) => SaveNow();

        CommandManager.AddPreviewExecutedHandler(Editor, OnEditorCommand);
        DataObject.AddCopyingHandler(Editor, (_, e) =>
        {
            // Dragging a selection that holds a picture would go through WPF's XAML copy, which can't carry it.
            if (e.IsDragDrop && HasEmbedded(Editor.Selection)) e.CancelCommand();
        });
        Editor.AllowDrop = true;
        Editor.PreviewDragOver += (_, e) =>
        {
            if (ImageFiles(e.Data).Count > 0) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        };
        Editor.PreviewDrop += (_, e) =>
        {
            var files = ImageFiles(e.Data);
            if (files.Count == 0) return;
            e.Handled = true;
            if (Editor.GetPositionFromPoint(e.GetPosition(Editor), true) is { } at) Editor.CaretPosition = at;
            foreach (var f in files) InsertImageFile(f);
        };

        ImageBox.EditRequested += box => { if (IsInEditor(box)) MarkUp(box); };
        ImageBox.Changed += box => { if (IsInEditor(box) || _openId is not null) MarkDirty(); };
        BuildToolbar();
    }

    private bool IsInEditor(DependencyObject d)
    {
        for (var p = d; p is not null; p = LogicalTreeHelper.GetParent(p) ?? (p is Visual ? VisualTreeHelper.GetParent(p) : null))
            if (p == Editor.Document || p == Editor) return true;
        return false;
    }

    private FlowDocument NewDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = FontCache.Get(_settings.FontFamily),
            FontSize = _settings.FontSize,
            TextAlignment = TextAlignment.Left,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Text");
        var para = new Style(typeof(Paragraph));
        para.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
        doc.Resources.Add(typeof(Paragraph), para);
        var list = new Style(typeof(List));
        list.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 2, 0, 2)));
        list.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(26, 0, 0, 0)));
        doc.Resources.Add(typeof(List), list);
        return doc;
    }

    // ── open / save ──

    public void OpenNote(string id, string[] terms, bool keepScroll = false)
    {
        if (id != _openId) SaveNow();
        var full = _store.Load(id);
        if (full is null) { CloseNote(); return; }
        var offset = keepScroll && id == _openId ? Editor.VerticalOffset : 0;
        _loading = true;
        try
        {
            var doc = NewDocument();
            DocSerializer.Read(doc, full.Doc, full.Body);
            _daily = DocSerializer.ReadDaily(full.Doc);
            Editor.Document = doc;
            TitleBox.Text = full.Title;
            _openId = id;
            _openVer = full.Ver;
            _dirty = false;
            Editor.IsReadOnly = full.Trashed;
            TitleBox.IsReadOnly = full.Trashed;
        }
        finally
        {
            _loading = false;
        }
        EditorPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        _settings.LastNote = full.Trashed ? null : id;
        SaveState.Text = full.Trashed ? "In Trash (read-only): right-click it in the list to restore" : "";
        UpdateMeta();
        WordCount(full.Body);
        _dailyButton.IsChecked = _daily;
        Gutter.Reset();
        SelectNote(id);
        if (keepScroll) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Editor.ScrollToVerticalOffset(offset));
        else if (terms.Length > 0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ShowMatch(terms));
        else Editor.ScrollToHome();
        UpdateToolbar();
    }

    /// <summary>Opens the current note again (after the default font changes).</summary>
    public void ReopenCurrent()
    {
        if (_openId is not { } id) return;
        SaveNow();
        OpenNote(id, [], keepScroll: true);
    }

    public void CloseNote()
    {
        SaveNow();
        _openId = null;
        _dirty = false;
        _loading = true;
        Editor.Document = NewDocument();
        TitleBox.Text = "";
        _loading = false;
        EditorPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        SaveState.Text = "";
        StatusText.Text = "";
    }

    private void MarkDirty()
    {
        if (_openId is null || Editor.IsReadOnly) return;
        _dirty = true;
        SaveState.Text = "Editing…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the open note now if it has changes (also on switching notes and closing).</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty || _openId is null) return;
        _dirty = false;
        var json = DocSerializer.Write(Editor.Document, _images, out var plain, daily: _daily);
        var title = TitleBox.Text.Trim();
        _openVer = _store.Save(_openId, title, json, plain);
        SaveState.Text = "Saved";
        WordCount(plain);
        UpdateNoteRow(_openId, title, plain);
    }

    private void WordCount(string plain)
    {
        var words = 0;
        var inWord = false;
        foreach (var ch in plain)
        {
            var w = !char.IsWhiteSpace(ch);
            if (w && !inWord) words++;
            inWord = w;
        }
        StatusText.Text = $"{words:N0} word{(words == 1 ? "" : "s")} · {plain.Length:N0} characters";
    }

    private void UpdateMeta()
    {
        if (_openId is null) return;
        var full = _store.Load(_openId);
        if (full is null) return;
        NoteMeta.Text = $"{FolderPath(full.Cat)}  ·  edited {When(full.Updated)}";
    }

    /// <summary>Opened from a search: select the first match and scroll it into view.</summary>
    private void ShowMatch(string[] terms)
    {
        foreach (var t in terms.OrderByDescending(t => t.Length))
        {
            for (var p = Editor.Document.ContentStart; p is not null && p.CompareTo(Editor.Document.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
                var run = p.GetTextInRun(LogicalDirection.Forward);
                var at = run.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var start = p.GetPositionAtOffset(at);
                var end = start?.GetPositionAtOffset(t.Length);
                if (start is null || end is null) continue;
                Editor.Selection.Select(start, end);
                var r = start.GetCharacterRect(LogicalDirection.Forward);
                if (!r.IsEmpty) Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + r.Top - Editor.ActualHeight / 3));
                return;
            }
        }
    }

    // ── toolbar ──

    private void BuildToolbar()
    {
        _font = new ComboBox { Width = 170, IsEditable = true, MaxDropDownHeight = 420, ToolTip = "Font" };
        _font.Items.Add(_settings.FontFamily);
        _font.DropDownOpened += (_, _) => FillFonts();
        _font.SelectionChanged += (_, _) =>
        {
            if (!_toolbarSync && _font.SelectedItem is string f && _font.IsDropDownOpen) Apply(TextElement.FontFamilyProperty, FontCache.Get(f));
        };
        _font.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var name = _font.Text.Trim();
            if (name.Length > 0) Apply(TextElement.FontFamilyProperty, FontCache.Get(name));
            e.Handled = true;
        };
        _font.ItemTemplate = FontItemTemplate();

        _size = new ComboBox { Width = 62, IsEditable = true, Margin = new Thickness(6, 0, 4, 0), ToolTip = "Size (Ctrl+] bigger, Ctrl+[ smaller)" };
        foreach (var s in new[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48, 56, 64, 72, 96 })
            _size.Items.Add(s.ToString());
        _size.SelectionChanged += (_, _) => { if (_size.IsDropDownOpen && _size.SelectedItem is string s) { _size.Text = s; ApplySize(); } };
        _size.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplySize(); e.Handled = true; } };

        _bold = Toggle("Bold (Ctrl+B)", "M4.5,2.5 L9,2.5 C11.5,2.5 12,4.5 12,5.3 C12,6.5 11,7.6 9.3,7.8 C11.5,8 12.6,9.3 12.6,10.8 C12.6,12.5 11.3,13.5 9.3,13.5 L4.5,13.5 Z M6.6,4.4 L6.6,7 L8.8,7 C9.6,7 10,6.4 10,5.7 C10,5 9.6,4.4 8.8,4.4 Z M6.6,8.8 L6.6,11.6 L9.1,11.6 C10,11.6 10.5,11 10.5,10.2 C10.5,9.4 10,8.8 9.1,8.8 Z", () => EditingCommands.ToggleBold.Execute(null, Editor), fill: true);
        _italic = Toggle("Italic (Ctrl+I)", "M7,2.5 L12,2.5 M4,13.5 L9,13.5 M9.6,2.5 L6.4,13.5", () => EditingCommands.ToggleItalic.Execute(null, Editor));
        _underline = Toggle("Underline (Ctrl+U)", "M4.5,2.5 L4.5,8 C4.5,10.2 6,11.5 8,11.5 C10,11.5 11.5,10.2 11.5,8 L11.5,2.5 M3.5,14 L12.5,14", () => EditingCommands.ToggleUnderline.Execute(null, Editor));
        _strike = Toggle("Strikethrough", "M11.5,4.5 C11,3.3 9.8,2.5 8,2.5 C6,2.5 4.7,3.5 4.7,5 C4.7,8.2 11.5,7.2 11.5,10.8 C11.5,12.4 10,13.5 8,13.5 C6,13.5 4.6,12.6 4.2,11.2 M2.5,8 L13.5,8", ToggleStrike);

        var color = ColorButton("Text color", out _colorBar, false);
        var highlight = ColorButton("Highlight", out _highlightBar, true);

        _bullets = Toggle("Bulleted list", "M6,4 L14,4 M6,8 L14,8 M6,12 L14,12 M2.5,4 L3,4 M2.5,8 L3,8 M2.5,12 L3,12", () => EditingCommands.ToggleBullets.Execute(null, Editor));
        _numbers = Toggle("Numbered list", "M6.5,4 L14,4 M6.5,8 L14,8 M6.5,12 L14,12 M2,2.8 L3,2.3 L3,5.5 M2,7 C2.3,6.4 3.8,6.3 3.8,7.3 C3.8,8 2,8.8 2,9.8 L4,9.8 M2,11.2 L3.8,11.2 L2.8,12.3 C3.6,12.3 4,12.7 4,13.2 C4,14 2.6,14.2 2,13.6", () => EditingCommands.ToggleNumbering.Execute(null, Editor));
        _left = Toggle("Align left", "M2,3.5 L14,3.5 M2,6.5 L10,6.5 M2,9.5 L14,9.5 M2,12.5 L10,12.5", () => EditingCommands.AlignLeft.Execute(null, Editor));
        _center = Toggle("Center", "M2,3.5 L14,3.5 M4,6.5 L12,6.5 M2,9.5 L14,9.5 M4,12.5 L12,12.5", () => EditingCommands.AlignCenter.Execute(null, Editor));
        _right = Toggle("Align right", "M2,3.5 L14,3.5 M6,6.5 L14,6.5 M2,9.5 L14,9.5 M6,12.5 L14,12.5", () => EditingCommands.AlignRight.Execute(null, Editor));
        var image = ToolButton("Insert image (or paste / drop one)", "M2,3 L14,3 L14,13 L2,13 Z M2,11 L6,7 L9,10 L11,8 L14,11 M10.5,5.8 A1,1 0 1 1 10.49,5.8", PickImage);
        _lines = Toggle("Line numbers (Ctrl+Shift+L)", "M2,4 L3,3.4 L3,6.5 M2,9 C2.2,8.4 4,8.3 4,9.3 C4,10 2,10.8 2,11.8 L4.2,11.8 M7,4.5 L14,4.5 M7,10.5 L14,10.5", ToggleLineNumbers);
        _lines.IsChecked = _settings.LineNumbers;
        _dailyButton = Toggle("Date each day's entries (this note): a line with the date goes above the first thing you write each day",
            "M1.5,3 L14.5,3 L14.5,15 L1.5,15 Z M1.5,6.5 L14.5,6.5 M4.5,1 L4.5,4.5 M11.5,1 L11.5,4.5 M4,10.5 L12,10.5", ToggleDaily);
        var clear = ToolButton("Clear formatting", "M3,3 L12,3 M7.5,3 L5.5,13 M9.5,9 L14,13.5 M14,9 L9.5,13.5", () =>
        {
            Editor.Selection.ClearAllProperties();
            MarkDirty();
        });

        Toolbar.Children.Add(_font);
        Toolbar.Children.Add(_size);
        Toolbar.Children.Add(Sep());
        foreach (var b in new UIElement[] { _bold, _italic, _underline, _strike, color, highlight, Sep(), _bullets, _numbers, Sep(), _left, _center, _right, Sep(), image, _lines, _dailyButton, clear })
            Toolbar.Children.Add(b);
    }

    private static DataTemplate FontItemTemplate()
    {
        var t = new DataTemplate();
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        f.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding { Converter = new FontNameConverter() });
        f.SetValue(TextBlock.FontSizeProperty, 14.0);
        t.VisualTree = f;
        return t;
    }

    private sealed class FontNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is string s ? FontCache.Get(s) : SystemFonts.MessageFontFamily;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private static List<string>? _fontNames;

    private void FillFonts()
    {
        if (_fontNames is null)
        {
            _fontNames = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.StartsWith('@')).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (_font.Items.Count > 1) return;
        _toolbarSync = true;
        var cur = _font.Text;
        _font.Items.Clear();
        foreach (var n in _fontNames) _font.Items.Add(n);
        _font.Text = cur;
        _toolbarSync = false;
    }

    private ToggleButton Toggle(string tip, string glyph, Action a, bool fill = false)
    {
        var b = new ToggleButton { ToolTip = tip, Content = Glyph(glyph, fill) };
        b.SetResourceReference(StyleProperty, "ToolToggle");
        b.Click += (_, _) => { a(); UpdateToolbar(); Editor.Focus(); };
        return b;
    }

    private Button ToolButton(string tip, string glyph, Action a)
    {
        var b = new Button { ToolTip = tip, Content = Glyph(glyph) };
        b.SetResourceReference(StyleProperty, "ToolButton");
        b.Click += (_, _) => { a(); Editor.Focus(); };
        return b;
    }

    private static FrameworkElement Glyph(string data, bool fill = false)
    {
        var p = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = fill ? 0 : 1.5, Stretch = Stretch.None, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        p.SetResourceReference(fill ? System.Windows.Shapes.Shape.FillProperty : System.Windows.Shapes.Shape.StrokeProperty, "Text");
        return p;
    }

    private static FrameworkElement Sep()
    {
        var r = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Border.BackgroundProperty, "Line");
        return r;
    }

    /// <summary>"A" (or a marker) over a bar in the last colour used: click applies that colour, the arrow picks another.</summary>
    private FrameworkElement ColorButton(string tip, out Border bar, bool highlight)
    {
        var letter = highlight
            ? Glyph("M4,10.5 L9.5,5 L12,7.5 L6.5,13 L4,13 Z M9.5,5 L11,3.5 L13.5,6 L12,7.5")
            : new TextBlock { Text = "A", FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
        bar = new Border { Height = 3.5, Width = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 1, 0, 0), Background = highlight ? _lastHighlight : _lastColor };
        var stack = new StackPanel { Width = 18 };
        stack.Children.Add(letter);
        stack.Children.Add(bar);
        var main = new Button { ToolTip = tip, Content = stack, Margin = new Thickness(1, 0, 0, 0) };
        main.SetResourceReference(StyleProperty, "ToolButton");
        var arrow = new Button { ToolTip = tip + ": pick a colour", Content = Glyph("M5,7 L8,10 L11,7"), MinWidth = 16, Padding = new Thickness(0), Margin = new Thickness(0, 0, 1, 0) };
        arrow.SetResourceReference(StyleProperty, "ToolButton");
        var thisBar = bar;
        main.Click += (_, _) =>
        {
            if (highlight) Apply(TextElement.BackgroundProperty, _lastHighlight);
            else Apply(TextElement.ForegroundProperty, _lastColor);
            Editor.Focus();
        };
        var popup = new Popup { PlacementTarget = main, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.None };
        arrow.Click += (_, _) =>
        {
            popup.Child = Palette(highlight, b =>
            {
                popup.IsOpen = false;
                if (highlight) { _lastHighlight = b; thisBar.Background = b ?? Brushes.Transparent; Apply(TextElement.BackgroundProperty, b); }
                else
                {
                    var brush = b ?? (Brush)FindResource("Text");
                    if (b is not null) { _lastColor = b; thisBar.Background = b; }
                    Apply(TextElement.ForegroundProperty, b is null ? Editor.Document.Foreground : brush);
                }
                Editor.Focus();
            });
            popup.IsOpen = true;
        };
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(main);
        p.Children.Add(arrow);
        p.Children.Add(popup);
        return p;
    }

    private FrameworkElement Palette(bool highlight, Action<Brush?> pick)
    {
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(6) };
        foreach (var hex in highlight ? HighlightColors : TextColors)
        {
            var brush = DocSerializer.Brush(hex)!;
            var sw = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = brush, BorderThickness = new Thickness(1), Margin = new Thickness(3) };
            sw.SetResourceReference(Border.BorderBrushProperty, "Line");
            var b = new Button { Content = sw, ToolTip = hex, Padding = new Thickness(0), MinWidth = 0, Height = 26 };
            b.SetResourceReference(StyleProperty, "ToolButton");
            b.Click += (_, _) => pick(brush);
            grid.Children.Add(b);
        }
        var none = new Button { Content = highlight ? "No highlight" : "Automatic (theme colour)", Margin = new Thickness(8, 6, 8, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        none.Click += (_, _) => pick(null);
        var stack = new StackPanel();
        stack.Children.Add(none);
        stack.Children.Add(grid);
        var border = new Border { Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "Panel");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    private void Apply(DependencyProperty prop, object? value)
    {
        if (Editor.IsReadOnly) return;
        Editor.Selection.ApplyPropertyValue(prop, value);
        MarkDirty();
        UpdateToolbar();
    }

    private void ApplySize()
    {
        if (_toolbarSync) return;
        if (double.TryParse(_size.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var s) && s >= 1 && s <= 400)
        {
            Apply(TextElement.FontSizeProperty, s);
            Editor.Focus();
        }
    }

    private void ToggleStrike()
    {
        var cur = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = cur?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        var next = new TextDecorationCollection();
        if (cur is not null) foreach (var d in cur) if (d.Location != TextDecorationLocation.Strikethrough) next.Add(d);
        if (!has) foreach (var d in TextDecorations.Strikethrough) next.Add(d);
        Apply(Inline.TextDecorationsProperty, next);
    }

    /// <summary>Turns "Date each day's entries" on or off for the open note (it's saved with the note, so it syncs).</summary>
    public void ToggleDaily()
    {
        if (_openId is null || Editor.IsReadOnly) { _dailyButton.IsChecked = _daily; return; }
        _daily = !_daily;
        _dailyButton.IsChecked = _daily;
        _dirty = true;
        SaveNow();
        SaveState.Text = _daily ? "Each day's entries get a date line" : "Date lines off (existing ones stay)";
    }

    public bool DailyOn => _daily;

    /// <summary>
    /// In a dated note, puts today's date line in before the first thing written today: above the line being typed on
    /// if it's empty, or after it (with a fresh line) when typing carries on at its end. Editing older text never adds
    /// one. True when a line was added.
    /// </summary>
    public bool EnsureTodayDivider(bool startNewLine = false)
    {
        if (!_daily || _openId is null || Editor.IsReadOnly) return false;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var doc = Editor.Document;
        if (doc.Blocks.Any(b => b is BlockUIContainer { Child: DateDivider d } && d.Date == today)) return false;
        if (!Editor.Selection.IsEmpty) return false;
        var caret = Editor.CaretPosition;
        // The top-level block the caret is in (a paragraph, or the list it's inside).
        Block? block = caret.Paragraph;
        while (block is not null && block.Parent is not FlowDocument) block = (block.Parent as TextElement) as Block ?? (block.Parent as ListItem)?.List;
        if (block is null || caret.Paragraph is not { } para) return false;
        var empty = doc.Blocks.Count == 1 && block is Paragraph && new TextRange(para.ContentStart, para.ContentEnd).IsEmpty
                    || block is Paragraph && new TextRange(para.ContentStart, para.ContentEnd).Text.Length == 0 && !HasEmbedded(new TextRange(para.ContentStart, para.ContentEnd));
        var atEnd = block == doc.Blocks.LastBlock && caret.CompareTo(para.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)) >= 0;
        if (!empty && !atEnd) return false;
        Editor.BeginChange();
        try
        {
            var line = new BlockUIContainer(new DateDivider(today));
            if (empty)
            {
                doc.Blocks.InsertBefore(block, line);
                Editor.CaretPosition = para.ContentStart;
            }
            else
            {
                doc.Blocks.InsertAfter(block, line);
                var fresh = new Paragraph();
                doc.Blocks.InsertAfter(line, fresh);
                Editor.CaretPosition = fresh.ContentStart;
            }
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        return !empty || startNewLine;
    }

    public void ToggleLineNumbers()
    {
        _settings.LineNumbers = !_settings.LineNumbers;
        Gutter.Visibility = _settings.LineNumbers ? Visibility.Visible : Visibility.Collapsed;
        _lines.IsChecked = _settings.LineNumbers;
        _settings.Save();
    }

    private void UpdateToolbar()
    {
        if (_font is null) return;
        _toolbarSync = true;
        try
        {
            var sel = Editor.Selection;
            var ff = sel.GetPropertyValue(TextElement.FontFamilyProperty);
            _font.Text = ff is FontFamily f ? f.Source : "";
            var fs = sel.GetPropertyValue(TextElement.FontSizeProperty);
            _size.Text = fs is double d ? Math.Round(d, 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
            _bold.IsChecked = sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;
            _italic.IsChecked = sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle st && st != FontStyles.Normal;
            var decos = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            _underline.IsChecked = decos?.Any(x => x.Location == TextDecorationLocation.Underline) == true;
            _strike.IsChecked = decos?.Any(x => x.Location == TextDecorationLocation.Strikethrough) == true;
            var list = sel.Start.Paragraph?.Parent is ListItem li ? li.List : null;
            _bullets.IsChecked = list is not null && list.MarkerStyle != TextMarkerStyle.Decimal;
            _numbers.IsChecked = list?.MarkerStyle == TextMarkerStyle.Decimal;
            var align = sel.GetPropertyValue(Paragraph.TextAlignmentProperty);
            _left.IsChecked = align is TextAlignment.Left;
            _center.IsChecked = align is TextAlignment.Center;
            _right.IsChecked = align is TextAlignment.Right;
        }
        finally
        {
            _toolbarSync = false;
        }
    }

    // ── images ──

    private void PickImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff;*.ico;*.heic|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Owner) != true) return;
        foreach (var f in dlg.FileNames) InsertImageFile(f);
    }

    private static List<string> ImageFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList()
            : [];

    private void InsertImageFile(string path)
    {
        try
        {
            InsertImage(_images.Import(File.ReadAllBytes(path)));
        }
        catch (Exception e)
        {
            Dialogs.Info(Owner, "Insert image", $"Couldn't read {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>Puts an image at the caret, sized to fit the page (it can be resized after).</summary>
    public InlineUIContainer? InsertImage(string id, double width = 0)
    {
        if (_openId is null || Editor.IsReadOnly) return null;
        if (width <= 0)
        {
            var natural = _images.Info(id)?.Width ?? 400;
            var room = Editor.ActualWidth - Editor.Padding.Left - Editor.Padding.Right - 30;
            width = Math.Min(natural, room > 100 ? room : 640);
            width = Math.Min(width, 760);
        }
        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty) Editor.Selection.Text = "";
            var pos = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            var c = new InlineUIContainer(new ImageBox(id, width), pos) { BaselineAlignment = BaselineAlignment.Bottom };
            Editor.CaretPosition = c.ElementEnd;
            return c;
        }
        finally
        {
            Editor.EndChange();
            MarkDirty();
        }
    }

    private void MarkUp(ImageBox box)
    {
        if (Editor.IsReadOnly) return;
        try
        {
            var w = new MarkupWindow(_images, box.ImageId, BrandIcon.Image) { Owner = Owner };
            w.ShowDialog();
            if (w.Saved) { box.Refresh(); MarkDirty(); }
        }
        catch (Exception e)
        {
            Dialogs.Info(Owner, "Mark up image", e.Message);
        }
    }

    private static bool HasEmbedded(TextRange r)
    {
        for (var p = r.Start; p is not null && p.CompareTo(r.End) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement) return true;
        return false;
    }

    // ── right-click menu, blur ──

    /// <summary>Spelling suggestions (when on a misspelt word), cut/copy/paste, and blur/unblur.</summary>
    internal ContextMenu EditMenu()
    {
        var m = new ContextMenu();
        if (SpellCheck.GetIsEnabled(Editor) && Editor.GetSpellingError(Editor.CaretPosition) is { } err)
        {
            var any = false;
            foreach (var s in err.Suggestions.Take(6))
            {
                m.Items.Add(new MenuItem { Header = s, FontWeight = FontWeights.SemiBold, Command = EditingCommands.CorrectSpellingError, CommandParameter = s, CommandTarget = Editor });
                any = true;
            }
            if (!any) m.Items.Add(new MenuItem { Header = "No spelling suggestions", IsEnabled = false });
            m.Items.Add(new MenuItem { Header = "Ignore this word", Command = EditingCommands.IgnoreSpellingError, CommandTarget = Editor });
            m.Items.Add(new Separator());
        }
        foreach (var cmd in new[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste })
            m.Items.Add(new MenuItem { Header = cmd.Text, Command = cmd, CommandTarget = Editor, InputGestureText = cmd == ApplicationCommands.Cut ? "Ctrl+X" : cmd == ApplicationCommands.Copy ? "Ctrl+C" : "Ctrl+V" });
        var (canBlur, canUnblur) = BlurState();
        if (canBlur || canUnblur) m.Items.Add(new Separator());
        if (canBlur)
        {
            var b = new MenuItem { Header = "Blur", InputGestureText = "Ctrl+Shift+B" };
            b.Click += (_, _) => SetBlur(true);
            m.Items.Add(b);
        }
        if (canUnblur)
        {
            var u = new MenuItem { Header = "Unblur", InputGestureText = canBlur ? "" : "Ctrl+Shift+B" };
            u.Click += (_, _) => SetBlur(false);
            m.Items.Add(u);
        }
        return m;
    }

    /// <summary>The text blur/unblur works on: the selection, or with nothing selected the blurred run at the caret.</summary>
    private TextRange? BlurTarget()
    {
        if (!Editor.Selection.IsEmpty) return new TextRange(Editor.Selection.Start, Editor.Selection.End);
        var p = Editor.CaretPosition;
        foreach (var dir in new[] { LogicalDirection.Forward, LogicalDirection.Backward })
            if (p.GetAdjacentElement(dir) is Run r && DocSerializer.IsBlurred(r)) return new TextRange(r.ContentStart, r.ContentEnd);
        if (p.Parent is Run pr && DocSerializer.IsBlurred(pr)) return new TextRange(pr.ContentStart, pr.ContentEnd);
        return null;
    }

    private (bool CanBlur, bool CanUnblur) BlurState()
    {
        if (Editor.IsReadOnly || BlurTarget() is not { } range) return (false, false);
        bool anyBlurred = false, anyClear = false;
        for (var p = range.Start; p is not null && p.CompareTo(range.End) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text || p.Parent is not Run r) continue;
            if (DocSerializer.IsBlurred(r)) anyBlurred = true;
            else anyClear = true;
        }
        return (anyClear && !range.IsEmpty, anyBlurred);
    }

    /// <summary>Blurs (or unblurs) the selected text. It stays real text: copying it gives what's underneath.</summary>
    public void SetBlur(bool on)
    {
        if (Editor.IsReadOnly || BlurTarget() is not { } range || range.IsEmpty) return;
        range.ApplyPropertyValue(TextElement.BackgroundProperty, on ? DocSerializer.BlurBrush : null);
        MarkDirty();
        Blur.Queue();
        Links.Queue();
        SaveState.Text = on ? "Blurred (copying it still gives the real text)" : "Unblurred";
    }

    /// <summary>Ctrl+Shift+B: blur the selection, or unblur it if it's all blurred already.</summary>
    public void ToggleBlur()
    {
        var (canBlur, canUnblur) = BlurState();
        if (canBlur) SetBlur(true);
        else if (canUnblur) SetBlur(false);
    }

    // ── phone numbers, emails, web links ──

    private static string LinkTip(FoundLink l) => l.Kind switch
    {
        LinkKind.Phone => $"Click to call {l.Text} (asks first) · Ctrl+click to edit",
        LinkKind.Email => $"Click to email {l.Text} · Ctrl+click to edit",
        _ => $"Click to open {l.Text} · Ctrl+click to edit",
    };

    /// <summary>Email and web links open straight away; a phone number asks before anything dials.</summary>
    public void OpenLink(FoundLink l)
    {
        if (l.Kind == LinkKind.Phone && !Dialogs.Call(Owner, l.Text))
            return;
        Launch(l);
    }

    private void Launch(FoundLink l)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(l.Target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            Dialogs.Info(Owner, l.Kind == LinkKind.Phone ? "Call" : "Open link", l.Kind switch
            {
                LinkKind.Phone => "Windows has no app set for phone links. Open Settings → Apps → Default apps, search for TEL, and choose RingCentral (or your calling app).",
                LinkKind.Email => "Windows has no email app set. Open Settings → Apps → Default apps, search for MAILTO, and choose your email app.",
                _ => "Couldn't open that link: " + e.Message,
            });
        }
    }

    internal ContextMenu LinkMenu(FoundLink l)
    {
        var m = new ContextMenu();
        void Item(string text, Action a)
        {
            var i = new MenuItem { Header = text };
            i.Click += (_, _) => a();
            m.Items.Add(i);
        }
        Item(l.Kind switch { LinkKind.Phone => $"Call {l.Text}…", LinkKind.Email => $"Email {l.Text}", _ => "Open link" }, () => OpenLink(l));
        Item(l.Kind switch { LinkKind.Phone => "Copy number", LinkKind.Email => "Copy address", _ => "Copy link" }, () => { try { Clipboard.SetText(l.Text); } catch { /* busy */ } });
        m.Items.Add(new Separator());
        foreach (var cmd in new[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste })
            m.Items.Add(new MenuItem { Header = cmd.Text, Command = cmd, CommandTarget = Editor });
        return m;
    }

    // ── copy / cut / paste ──

    private void OnEditorCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
        {
            if (Editor.Selection.IsEmpty) return;
            e.Handled = true;
            CopySelection();
            if (e.Command == ApplicationCommands.Cut && !Editor.IsReadOnly)
            {
                Editor.Selection.Text = "";
                MarkDirty();
            }
        }
        else if (e.Command == ApplicationCommands.Paste)
        {
            if (Editor.IsReadOnly) { e.Handled = true; return; }
            if (PasteSpecial()) { e.Handled = true; MarkDirty(); }
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, ConvertPastedImages);
        }
    }

    /// <summary>Text, rich text for other programs, and pxd Notepad's own copy (keeps images and every format).</summary>
    public void CopySelection()
    {
        var sel = Editor.Selection;
        var data = new DataObject();
        data.SetText(sel.Text);
        data.SetData(DocSerializer.ClipboardFormat, DocSerializer.Write(Editor.Document, null, out _, sel.Start, sel.End));
        try
        {
            using var ms = new MemoryStream();
            sel.Save(ms, DataFormats.Rtf);
            data.SetData(DataFormats.Rtf, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch
        {
            /* plain text and our own copy still go */
        }
        // Just one picture selected: other programs get the picture too.
        var start = sel.Start.GetNextContextPosition(LogicalDirection.Forward);
        if (sel.Start.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Child: ImageBox box } && sel.Text.Trim().Length == 0 && _images.Bitmap(box.ImageId) is { } bmp)
            data.SetImage(bmp);
        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch
        {
            /* clipboard busy */
        }
    }

    /// <summary>Pastes the things WPF can't: our own copies, pictures and picture files. False: leave it to WPF.</summary>
    private bool PasteSpecial()
    {
        IDataObject data;
        try { data = Clipboard.GetDataObject(); }
        catch { return false; }
        if (data.GetData(DocSerializer.ClipboardFormat) is string json)
        {
            InsertFragment(json);
            return true;
        }
        var files = ImageFiles(data);
        if (files.Count > 0)
        {
            foreach (var f in files) InsertImageFile(f);
            return true;
        }
        if (data.GetDataPresent(DataFormats.Text) || data.GetDataPresent(DataFormats.Rtf)) return false;
        // A screenshot or copied picture: PNG keeps transparency, the bitmap form is the fallback.
        if (data.GetData("PNG") is MemoryStream png)
        {
            InsertImage(_images.Import(png.ToArray()));
            return true;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } img)
        {
            InsertImage(_images.Import(img));
            return true;
        }
        return false;
    }

    /// <summary>Inserts a fragment copied from a note at the caret (pictures are copied so each note can mark its own up).</summary>
    public void InsertFragment(string json)
    {
        using var d = JsonDocument.Parse(json);
        var paras = DocSerializer.FragmentParagraphs(d);
        if (paras.Count == 0) return;
        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty) Editor.Selection.Text = "";
            var pos = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            for (var i = 0; i < paras.Count; i++)
            {
                if (i > 0)
                {
                    pos = pos.InsertParagraphBreak();
                    if (pos.Paragraph is { } np && paras[i].TryGetProperty("a", out var a))
                        np.TextAlignment = a.GetString() switch { "c" => TextAlignment.Center, "r" => TextAlignment.Right, "j" => TextAlignment.Justify, _ => TextAlignment.Left };
                }
                foreach (var el in paras[i].GetProperty("p").EnumerateArray())
                {
                    if (el.TryGetProperty("t", out var t))
                    {
                        var r = new Run(t.GetString() ?? "", pos);
                        DocSerializer.ApplyRunProps(r, el);
                        pos = r.ElementEnd;
                    }
                    else if (el.TryGetProperty("br", out _))
                        pos = new LineBreak(pos).ElementEnd;
                    else if (el.TryGetProperty("img", out var img) && img.GetString() is { Length: > 0 } id)
                    {
                        var copy = _images.Duplicate(id) ?? id;
                        var w = el.TryGetProperty("w", out var ww) ? ww.GetDouble() : 0;
                        pos = new InlineUIContainer(new ImageBox(copy, w), pos) { BaselineAlignment = BaselineAlignment.Bottom }.ElementEnd;
                    }
                }
            }
            Editor.CaretPosition = pos;
        }
        finally
        {
            Editor.EndChange();
        }
    }

    /// <summary>Pictures pasted from Word or a web page arrive as plain WPF images: make them note images.</summary>
    public void ConvertPastedImages()
    {
        var found = new List<(TextElement Container, BitmapSource Bmp, double Width)>();
        void Walk(BlockCollection blocks)
        {
            foreach (var b in blocks)
            {
                if (b is Paragraph p) WalkInlines(p.Inlines);
                else if (b is List l) foreach (var li in l.ListItems) Walk(li.Blocks);
                else if (b is Section s) Walk(s.Blocks);
                else if (b is Table t) foreach (var g in t.RowGroups) foreach (var r in g.Rows) foreach (var c in r.Cells) Walk(c.Blocks);
                else if (b is BlockUIContainer { Child: Image { Source: BitmapSource bmp } im } u) found.Add((u, bmp, im.Width));
            }
        }
        void WalkInlines(InlineCollection inl)
        {
            foreach (var i in inl)
            {
                if (i is InlineUIContainer { Child: Image { Source: BitmapSource bmp } im } c) found.Add((c, bmp, im.Width));
                else if (i is Span s) WalkInlines(s.Inlines);
            }
        }
        Walk(Editor.Document.Blocks);
        if (found.Count == 0) return;
        foreach (var (container, bmp, w) in found)
        {
            var id = _images.Import(bmp);
            var width = double.IsNaN(w) || w <= 0 ? Math.Min(bmp.PixelWidth, 640) : w;
            if (container is InlineUIContainer ic) ic.Child = new ImageBox(id, width);
            else if (container is BlockUIContainer bc)
            {
                var para = new Paragraph(new InlineUIContainer(new ImageBox(id, width)) { BaselineAlignment = BaselineAlignment.Bottom });
                if (bc.Parent is FlowDocument fd) fd.Blocks.InsertAfter(bc, para);
                else if (bc.Parent is Section sec) sec.Blocks.InsertAfter(bc, para);
                else if (bc.Parent is ListItem li) li.Blocks.InsertAfter(bc, para);
                else if (bc.Parent is TableCell tc) tc.Blocks.InsertAfter(bc, para);
                bc.SiblingBlocks?.Remove(bc);
            }
        }
        MarkDirty();
    }
}
