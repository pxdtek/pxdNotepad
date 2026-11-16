using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace pxdNotepad.Views;

/// <summary>
/// Any colour: a saturation/brightness square, a hue bar, a hex box and a preview. Opens as a small drop-down under
/// whatever asked for it (<see cref="Open"/>), or can be placed directly (the self-test draws it).
/// </summary>
public sealed class ColorPicker : Border
{
    private double _h, _s = 1, _v = 1;
    private readonly Border _square = new() { Width = 220, Height = 150, CornerRadius = new CornerRadius(6), ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Canvas _dotLayer = new() { IsHitTestVisible = false };
    private readonly Border _hue = new() { Width = 220, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand };
    private readonly Canvas _hueLayer = new() { IsHitTestVisible = false };
    private readonly Border _hueKnob = new() { Width = 6, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false };
    private readonly TextBox _hex = new() { Width = 96, FontFamily = new FontFamily("Consolas"), FontSize = 13 };
    private readonly Border _preview = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1) };
    private bool _typing;

    public event Action<Color>? Picked;
    public event Action? Cancelled;
    public Color Value => Hsv(_h, _s, _v);

    public ColorPicker(Color start)
    {
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(BorderBrushProperty, "Line");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12);
        (_h, _s, _v) = ToHsv(start);

        var squareGrid = new Grid();
        squareGrid.Children.Add(_square);
        var shade = new Border { Width = 220, Height = 150, CornerRadius = new CornerRadius(6), IsHitTestVisible = false, Background = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) };
        squareGrid.Children.Add(shade);
        squareGrid.Children.Add(_dotLayer);
        _dotLayer.Children.Add(_dot);
        _square.MouseLeftButtonDown += (_, e) => { _square.CaptureMouse(); PickSquare(e.GetPosition(_square)); };
        _square.MouseMove += (_, e) => { if (_square.IsMouseCaptured) PickSquare(e.GetPosition(_square)); };
        _square.MouseLeftButtonUp += (_, _) => _square.ReleaseMouseCapture();

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(Hsv(i * 60 % 360, 1, 1), i / 6.0));
        rainbow.GradientStops[^1] = new GradientStop(Hsv(0, 1, 1), 1);
        _hue.Background = rainbow;
        var hueGrid = new Grid();
        hueGrid.Children.Add(_hue);
        hueGrid.Children.Add(_hueLayer);
        _hueLayer.Children.Add(_hueKnob);
        _hue.MouseLeftButtonDown += (_, e) => { _hue.CaptureMouse(); PickHue(e.GetPosition(_hue)); };
        _hue.MouseMove += (_, e) => { if (_hue.IsMouseCaptured) PickHue(e.GetPosition(_hue)); };
        _hue.MouseLeftButtonUp += (_, _) => _hue.ReleaseMouseCapture();

        _preview.SetResourceReference(Border.BorderBrushProperty, "Line");
        _hex.TextChanged += (_, _) =>
        {
            if (_typing) return;
            if (TryHex(_hex.Text, out var c))
            {
                _typing = true;
                (_h, _s, _v) = ToHsv(c);
                Show(updateHex: false);
                _typing = false;
            }
        };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Picked?.Invoke(Value); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; }
        };
        var use = new Button { Content = "Use", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
        use.SetResourceReference(StyleProperty, "AccentButton");
        use.Click += (_, _) => Picked?.Invoke(Value);
        var hexLabel = new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        hexLabel.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        row.Children.Add(_preview);
        row.Children.Add(hexLabel);
        row.Children.Add(_hex);
        DockPanel.SetDock(use, Dock.Right);
        row.Children.Add(use);

        var stack = new StackPanel();
        stack.Children.Add(squareGrid);
        stack.Children.Add(hueGrid);
        stack.Children.Add(row);
        Child = stack;
        Show(updateHex: true);
    }

    private void PickSquare(Point p)
    {
        _s = Math.Clamp(p.X / _square.Width, 0, 1);
        _v = 1 - Math.Clamp(p.Y / _square.Height, 0, 1);
        Show(updateHex: true);
    }

    private void PickHue(Point p)
    {
        _h = Math.Clamp(p.X / _hue.Width, 0, 1) * 359.9;
        Show(updateHex: true);
    }

    private void Show(bool updateHex)
    {
        _square.Background = new LinearGradientBrush(Colors.White, Hsv(_h, 1, 1), 0);
        Canvas.SetLeft(_dot, _s * _square.Width - 6);
        Canvas.SetTop(_dot, (1 - _v) * _square.Height - 6);
        Canvas.SetLeft(_hueKnob, _h / 360 * _hue.Width - 3);
        Canvas.SetTop(_hueKnob, -2);
        var c = Value;
        _preview.Background = new SolidColorBrush(c);
        if (updateHex)
        {
            _typing = true;
            _hex.Text = Editor.DocSerializer.Hex(c);
            _typing = false;
        }
    }

    public static bool TryHex(string text, out Color c)
    {
        c = default;
        var t = text.Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
        c = Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
        return true;
    }

    public static Color Hsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * ((g - b) / d % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    /// <summary>Opens a picker under <paramref name="anchor"/>; <paramref name="picked"/> runs with the chosen colour.</summary>
    public static void Open(UIElement anchor, Color start, Action<Color> picked)
    {
        var picker = new ColorPicker(start);
        var popup = new Popup { Child = picker, PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.None, VerticalOffset = 4 };
        picker.Picked += c => { popup.IsOpen = false; picked(c); };
        picker.Cancelled += () => popup.IsOpen = false;
        popup.IsOpen = true;
        picker._hex.Focus();
        picker._hex.SelectAll();
    }
}
