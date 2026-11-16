using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pxdStandard;

namespace pxdNotepad.Images;

/// <summary>Draw arrows and boxes on a note's image. Saving keeps the original, so marks can be changed later.</summary>
public sealed class MarkupWindow : Window
{
    private readonly ImageStore _store;
    private readonly string _id;
    private readonly BitmapSource _original;
    private readonly MarkLayer _layer = new();
    private readonly Stack<List<Mark>> _undo = new();
    private readonly Stack<List<Mark>> _redo = new();
    private readonly Dictionary<string, ToggleButton> _tools = [];
    private readonly List<ToggleButton> _swatches = [];
    private readonly string _mime;
    public bool Saved { get; private set; }

    public static readonly string[] Palette = ["#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#39C5CF", "#0A84FF", "#AF52DE", "#FFFFFF", "#000000"];

    public MarkupWindow(ImageStore store, string imageId, ImageSource? icon)
    {
        _store = store;
        _id = imageId;
        var info = store.Info(imageId) ?? throw new InvalidOperationException("That image isn't here.");
        _mime = info.Mime;
        _original = ImageStore.Decode(store.Original(imageId) ?? throw new InvalidOperationException("That image hasn't downloaded yet."), int.MaxValue);
        _layer.Marks = Mark.Parse(info.Marks);
        _layer.Width = _original.PixelWidth;
        _layer.Height = _original.PixelHeight;
        Width = 1000;
        Height = 720;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var picture = new Grid { Width = _original.PixelWidth, Height = _original.PixelHeight };
        var img = new Image { Source = _original, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        picture.Children.Add(img);
        picture.Children.Add(_layer);
        var view = new Viewbox { Child = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16) };
        var stage = new Border { Child = view };
        stage.SetResourceReference(Border.BackgroundProperty, "EditorBg");

        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 8) };
        bar.Children.Add(Tool("arrow", "Arrow (A)", "M2,14 L12,4 M6,4 L12,4 L12,10"));
        bar.Children.Add(Tool("box", "Box (B) · Shift for a square", "M2,3 L14,3 L14,13 L2,13 Z"));
        bar.Children.Add(Tool("select", "Move or delete (V) · Delete removes the picked one", "M3,2 L3,14 L6.5,10.5 L9,15 L11,14 L8.6,9.6 L13,9.6 Z"));
        bar.Children.Add(Gap());
        foreach (var c in Palette) bar.Children.Add(Swatch(c));
        bar.Children.Add(Gap());
        var thickLabel = new TextBlock { Text = "Line", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        thickLabel.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        bar.Children.Add(thickLabel);
        var thick = new Slider { Minimum = 2, Maximum = 30, Value = DefaultWidth(), Width = 110, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
        thick.ValueChanged += (_, e) =>
        {
            _layer.StrokeWidth = e.NewValue;
            if (_layer.Selected is { } s) { Push(); s.Width = e.NewValue; _layer.InvalidateVisual(); }
        };
        _layer.StrokeWidth = thick.Value;
        bar.Children.Add(thick);
        bar.Children.Add(Gap());
        bar.Children.Add(Act("Undo (Ctrl+Z)", "M5,4 L2,7 L5,10 M2,7 L10,7 C13,7 14,9 14,11 C14,13 13,14 11,14", Undo));
        bar.Children.Add(Act("Redo (Ctrl+Y)", "M11,4 L14,7 L11,10 M14,7 L6,7 C3,7 2,9 2,11 C2,13 3,14 5,14", Redo));
        bar.Children.Add(Act("Remove all marks", "M3,4 L13,4 M6,4 L6,2.5 L10,2.5 L10,4 M4.5,4 L5.3,14 L10.7,14 L11.5,4", () =>
        {
            if (_layer.Marks.Count == 0) return;
            Push();
            _layer.Marks.Clear();
            _layer.Selected = null;
            _layer.InvalidateVisual();
        }));

        var save = new Button { Content = "Save", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        save.SetResourceReference(StyleProperty, "AccentButton");
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 12) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var hint = new TextBlock { Text = "Drag on the picture to draw. Saving keeps the original, so you can change the marks later.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var foot = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(hint);

        var top = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        top.SetResourceReference(Border.BorderBrushProperty, "Line");
        top.SetResourceReference(Border.BackgroundProperty, "Panel");
        var bottom = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        bottom.SetResourceReference(Border.BorderBrushProperty, "Line");
        bottom.SetResourceReference(Border.BackgroundProperty, "Panel");
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(stage);

        Chrome.Apply(this, "Mark up image", root, icon);
        Chrome.RememberPlacement(this, "markup");
        _layer.BeforeChange += Push;
        _layer.AfterChange += () => _redo.Clear();
        SetTool("arrow");
        SetColor(Palette[0]);
        PreviewKeyDown += OnKey;
    }

    /// <summary>A line width that reads well at this picture's size.</summary>
    private double DefaultWidth() => Math.Clamp(Math.Round(Math.Max(_original.PixelWidth, _original.PixelHeight) / 220.0), 3, 18);

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveAndClose(); e.Handled = true; }
        else if (!ctrl && e.Key == Key.A) { SetTool("arrow"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.B) { SetTool("box"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.V) { SetTool("select"); e.Handled = true; }
    }

    private void Push()
    {
        _undo.Push(_layer.Marks.Select(m => m.Clone()).ToList());
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_layer.Marks.Select(m => m.Clone()).ToList());
        _layer.Marks = _undo.Pop();
        _layer.Selected = null;
        _layer.InvalidateVisual();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_layer.Marks.Select(m => m.Clone()).ToList());
        _layer.Marks = _redo.Pop();
        _layer.Selected = null;
        _layer.InvalidateVisual();
    }

    private void SaveAndClose()
    {
        var marks = _layer.Marks;
        byte[]? flat = null;
        if (marks.Count > 0)
            flat = ImageStore.Encode(MarkDrawing.Flatten(_original, marks), _mime == "image/jpeg" ? "image/jpeg" : "image/png");
        _store.SaveEdit(_id, Mark.ToJson(marks), flat);
        Saved = true;
        Close();
    }

    private void SetTool(string tool)
    {
        _layer.Tool = tool;
        _layer.Cursor = tool == "select" ? Cursors.Hand : Cursors.Cross;
        if (tool != "select") { _layer.Selected = null; _layer.InvalidateVisual(); }
        foreach (var (k, b) in _tools) b.IsChecked = k == tool;
    }

    private void SetColor(string c)
    {
        _layer.Color = c;
        foreach (var s in _swatches) s.IsChecked = (string)s.Tag == c;
        if (_layer.Selected is { } m) { Push(); m.Color = c; _layer.InvalidateVisual(); }
    }

    private ToggleButton Tool(string key, string tip, string glyph)
    {
        var b = new ToggleButton { ToolTip = tip, Content = Glyph(glyph) };
        b.SetResourceReference(StyleProperty, "ToolToggle");
        b.Click += (_, _) => SetTool(key);
        _tools[key] = b;
        return b;
    }

    private Button Act(string tip, string glyph, Action a)
    {
        var b = new Button { ToolTip = tip, Content = Glyph(glyph) };
        b.SetResourceReference(StyleProperty, "ToolButton");
        b.Click += (_, _) => a();
        return b;
    }

    private ToggleButton Swatch(string c)
    {
        var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = (Brush)new BrushConverter().ConvertFromString(c)!, BorderThickness = new Thickness(1) };
        dot.SetResourceReference(Border.BorderBrushProperty, "Line");
        var b = new ToggleButton { Content = dot, Tag = c, ToolTip = c };
        b.SetResourceReference(StyleProperty, "ToolToggle");
        b.Click += (_, _) => SetColor(c);
        _swatches.Add(b);
        return b;
    }

    private static FrameworkElement Gap()
    {
        var r = new Border { Width = 1, Height = 20, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Border.BackgroundProperty, "Line");
        return r;
    }

    public static System.Windows.Shapes.Path Glyph(string data)
    {
        var p = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = 1.5, Stretch = Stretch.None, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text");
        return p;
    }
}
