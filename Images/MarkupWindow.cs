using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pxdStandard;

namespace pxdNotepad.Images;

/// <summary>
/// Draw arrows and boxes on a note's image, blur or pixelate parts of it, or cut a band out. Saving keeps the
/// original, so every edit can be changed later.
/// </summary>
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
        _img.Source = _original;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        picture.Children.Add(_img);
        picture.Children.Add(_layer);
        // Zoom: the picture scaled in a scrolling area (fit to the window at first; zoom bar, Ctrl+wheel, Ctrl +/-/0).
        picture.LayoutTransform = _scale;
        _picture = picture;
        var holder = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Edge) };
        holder.Children.Add(picture);
        _scroll = new ScrollViewer { Content = holder, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _scroll.SetResourceReference(StyleProperty, "QuietScrollViewer");
        _scroll.Loaded += (_, _) => Fit();
        _scroll.SizeChanged += (_, _) => { if (_fitted) Fit(); };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            ZoomAt(_scale.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(_picture), e.GetPosition(_scroll));
            e.Handled = true;
        };
        // A press in the space around the picture starts there too (taken as the picture's nearest edge): boxes are
        // easier to draw right up to the edge from outside. Not on the scroll bars.
        _scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject d || Within<MarkLayer>(d) || Within<ScrollBar>(d)) return;
            _layer.StartAt(e.GetPosition(_layer));
            e.Handled = true;
        };
        _scroll.Loaded += (_, _) => AroundCursor();
        var stage = new Border { Child = _scroll };
        stage.SetResourceReference(Border.BackgroundProperty, "EditorBg");

        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 8) };
        bar.Children.Add(Tool("arrow", "Arrow (A)", "M2,14 L12,4 M6,4 L12,4 L12,10"));
        bar.Children.Add(Tool("box", "Box (B) · Shift for a square", "M2,3 L14,3 L14,13 L2,13 Z"));
        bar.Children.Add(Tool("blur", "Blur an area (U) · what's under it can't be read back", "M2,4 L14,4 M2,8 L14,8 M2,12 L14,12 M4,2 L4,14 M8,2 L8,14 M12,2 L12,14"));
        bar.Children.Add(Tool("pixel", "Pixelate an area (P)", "M2,2 L7,2 L7,7 L2,7 Z M9,9 L14,9 L14,14 L9,14 Z M9,2 L14,2 L14,7 L9,7 Z M2,9 L7,9 L7,14 L2,14 Z"));
        bar.Children.Add(Tool("cut", "Cut out (X) · drag over the part to remove: a wide box takes out those rows, a tall one those columns (Shift: the other way); the picture closes up", "M4.5,5.5 A2,2 0 1 1 4.49,5.5 Z M4.5,14.5 A2,2 0 1 1 4.49,14.5 Z M6.2,6.6 L14,13 M6.2,13.4 L14,7"));
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
            ShowPhoto();
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
        var hint = _hint = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        // The zoom bar: out, slider, in, the percentage, Fit.
        _zoomSlider = new Slider { Minimum = MinZoom, Maximum = MaxZoom, Width = 150, VerticalAlignment = VerticalAlignment.Center, Value = 1, ToolTip = "Zoom (Ctrl + mouse wheel)" };
        _zoomSlider.ValueChanged += (_, e) => { if (!_zoomSync) { _fitted = false; SetZoom(e.NewValue); } };
        _zoomText = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var fit = new Button { Content = "Fit", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Fit the picture to the window (Ctrl+0)" };
        fit.Click += (_, _) => Fit();
        var zoomBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        zoomBar.Children.Add(Act("Zoom out (Ctrl+-)", "M3,8 L13,8", () => ZoomStep(1 / 1.25)));
        zoomBar.Children.Add(_zoomSlider);
        zoomBar.Children.Add(Act("Zoom in (Ctrl+=)", "M3,8 L13,8 M8,3 L8,13", () => ZoomStep(1.25)));
        zoomBar.Children.Add(_zoomText);
        zoomBar.Children.Add(fit);

        var foot = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(zoomBar, Dock.Left);
        foot.Children.Add(buttons);
        foot.Children.Add(zoomBar);
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
        _layer.AfterChange += () => { _redo.Clear(); ShowPhoto(); };
        ShowPhoto();
        SetTool("arrow");
        SetColor(Palette[0]);
        PreviewKeyDown += OnKey;
    }

    private const double MinZoom = 0.05, MaxZoom = 8, Edge = 16;
    private readonly Image _img = new() { Stretch = Stretch.Fill };
    private TextBlock _hint = null!;
    private BitmapSource _photo = null!;
    private string? _photoKey;

    /// <summary>The picture as edited so far (blurs, pixelations, cut-outs), redone only when those change.</summary>
    private void ShowPhoto()
    {
        var key = System.Text.Json.JsonSerializer.Serialize(_layer.Marks.Where(PhotoEdits.IsPhoto).ToList());
        if (key == _photoKey) return;
        _photoKey = key;
        _photo = PhotoEdits.Apply(_original, _layer.Marks);
        _img.Source = _photo;
        _picture.Width = _layer.Width = _photo.PixelWidth;
        _picture.Height = _layer.Height = _photo.PixelHeight;
        _layer.InvalidateVisual();
        if (_fitted && IsLoaded) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Fit);
    }

    /// <summary>The picture's size now (after any cut-outs).</summary>
    public (int Width, int Height) PictureSize => (_photo.PixelWidth, _photo.PixelHeight);

    /// <summary>The edits as they stand (for tests).</summary>
    public MarkLayer Layer => _layer;
    private readonly ScaleTransform _scale = new(1, 1);
    private ScrollViewer _scroll = null!;
    private FrameworkElement _picture = null!;
    private Slider _zoomSlider = null!;
    private TextBlock _zoomText = null!;
    private bool _fitted = true, _zoomSync;

    /// <summary>How big the picture is shown (1 = actual pixels).</summary>
    public double Zoom => _scale.ScaleX;

    public void SetZoom(double z)
    {
        z = Math.Clamp(z, MinZoom, MaxZoom);
        _scale.ScaleX = _scale.ScaleY = z;
        _zoomSync = true;
        _zoomSlider.Value = z;
        _zoomSync = false;
        _zoomText.Text = $"{z * 100:0}%";
        // Sharp pixels when zoomed in a lot, smooth when shown smaller.
        RenderOptions.SetBitmapScalingMode(_picture, z >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    /// <summary>The whole picture in the window (never bigger than its actual size).</summary>
    public void Fit()
    {
        var w = (_scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth) - Edge * 2;
        var h = (_scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight) - Edge * 2;
        if (w <= 0 || h <= 0) return;
        SetZoom(Math.Min(1, Math.Min(w / _photo.PixelWidth, h / _photo.PixelHeight)));
        _fitted = true;
    }

    private void ZoomStep(double factor)
    {
        var mid = new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);
        ZoomAt(_scale.ScaleX * factor, _scroll.TranslatePoint(mid, _picture), mid);
    }

    /// <summary>Zooms keeping the picture point under the mouse (or the middle) where it is.</summary>
    private void ZoomAt(double z, Point onPicture, Point inView)
    {
        _fitted = false;
        SetZoom(z);
        _scroll.UpdateLayout();
        var now = _picture.TranslatePoint(onPicture, _scroll);
        _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + now.X - inView.X);
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + now.Y - inView.Y);
    }

    /// <summary>A line width that reads well at this picture's size.</summary>
    private double DefaultWidth() => Math.Clamp(Math.Round(Math.Max(_original.PixelWidth, _original.PixelHeight) / 220.0), 3, 18);

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveAndClose(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { ZoomStep(1.25); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { ZoomStep(1 / 1.25); e.Handled = true; }
        else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { Fit(); e.Handled = true; }
        else if (!ctrl && e.Key == Key.A) { SetTool("arrow"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.B) { SetTool("box"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.V) { SetTool("select"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.U) { SetTool("blur"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.P) { SetTool("pixel"); e.Handled = true; }
        else if (!ctrl && e.Key == Key.X) { SetTool("cut"); e.Handled = true; }
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
        ShowPhoto();
        _layer.InvalidateVisual();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_layer.Marks.Select(m => m.Clone()).ToList());
        _layer.Marks = _redo.Pop();
        _layer.Selected = null;
        ShowPhoto();
        _layer.InvalidateVisual();
    }

    public void SaveAndClose()
    {
        var marks = _layer.Marks;
        byte[]? flat = null;
        BitmapSource shown = _original;
        if (marks.Count > 0)
        {
            shown = MarkDrawing.Flatten(_original, marks);
            flat = ImageStore.Encode(shown, _mime == "image/jpeg" ? "image/jpeg" : "image/png");
        }
        // the note shows the picture at its new shape after a cut-out
        _store.SaveEdit(_id, Mark.ToJson(marks), flat, shown.PixelWidth, shown.PixelHeight);
        Saved = true;
        Close();
    }

    private static bool Within<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T) return true;
        return false;
    }

    /// <summary>The space around the picture shows the same cursor as the picture (a cross while drawing).</summary>
    private void AroundCursor()
    {
        if (FindPresenter(_scroll) is { } area) area.Cursor = _layer.Cursor;
    }

    private static ScrollContentPresenter? FindPresenter(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollContentPresenter p) return p;
            if (FindPresenter(c) is { } found) return found;
        }
        return null;
    }

    public void SetTool(string tool)
    {
        _layer.Tool = tool;
        _hint.Text = tool switch
        {
            "blur" => "Drag over what to hide. What was under the blur is gone from the saved picture.",
            "pixel" => "Drag over what to hide. The saved picture keeps only the squares.",
            "cut" => "Drag over the part to remove: a wide box takes out rows, a tall one columns (Shift flips).",
            "select" => "Click an arrow, box, blur or pixelation to move it, or press Delete to remove it.",
            _ => "Drag on the picture to draw. Saving keeps the original, so every edit can be changed later.",
        };
        _layer.Cursor = tool == "select" ? Cursors.Hand : Cursors.Cross;
        AroundCursor();
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
