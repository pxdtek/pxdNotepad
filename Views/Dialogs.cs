using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using pxdStandard;

namespace pxdNotepad.Views;

/// <summary>Small pxd-styled dialogs (own title bar, like every pxd window).</summary>
public static class Dialogs
{
    private static Window Make(Window? owner, string title, UIElement body, double width)
    {
        var w = new Window
        {
            Width = width,
            SizeToContent = SizeToContent.Height,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
        };
        Chrome.Apply(w, title, body, BrandIcon.Image, resizable: false);
        return w;
    }

    private static StackPanel Buttons(params Button[] bs)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in bs) { b.MinWidth = 88; b.Margin = new Thickness(8, 0, 0, 0); p.Children.Add(b); }
        return p;
    }

    public static string? Prompt(Window? owner, string title, string label, string initial = "", string ok = "OK")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0), FontSize = 14 };
        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var okB = new Button { Content = ok, IsDefault = true };
        okB.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        body.Children.Add(text);
        body.Children.Add(box);
        body.Children.Add(Buttons(okB, cancel));
        var w = Make(owner, title, body, 420);
        string? result = null;
        okB.Click += (_, _) => { if (box.Text.Trim().Length > 0) { result = box.Text.Trim(); w.Close(); } };
        cancel.Click += (_, _) => w.Close();
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    public static bool Confirm(Window? owner, string title, string message, string ok = "OK", bool danger = false)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var okB = new Button { Content = ok, IsDefault = true };
        okB.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        if (danger) { okB.SetResourceReference(Control.BackgroundProperty, "Danger"); okB.SetResourceReference(Control.BorderBrushProperty, "Danger"); }
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        body.Children.Add(text);
        body.Children.Add(Buttons(okB, cancel));
        var w = Make(owner, title, body, 440);
        var result = false;
        okB.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.ShowDialog();
        return result;
    }

    // A phone handset (Material "call" shape, 24×24), and the same handset turned down for hanging up.
    private const string Handset = "M6.6,10.8 C8,13.6 10.4,16 13.2,17.4 L15.4,15.2 C15.7,14.9 16.1,14.8 16.4,14.9 C17.5,15.3 18.7,15.5 20,15.5 C20.6,15.5 21,15.9 21,16.5 L21,20 C21,20.6 20.6,21 20,21 C10.6,21 3,13.4 3,4 C3,3.4 3.4,3 4,3 L7.5,3 C8.1,3 8.5,3.4 8.5,4 C8.5,5.3 8.7,6.5 9.1,7.6 C9.2,7.9 9.1,8.3 8.8,8.6 Z";

    /// <summary>Builds the call card's content (also drawn off screen by the self-test).</summary>
    public static FrameworkElement CallCard(string number, Action call, Action hangUp, out Button callButton, out Button hangUpButton)
    {
        var ask = new TextBlock { Text = $"Do you want to call {number}?", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var note = new TextBlock { Text = "(Uses your default phone app)", FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        static (Button, FrameworkElement) Round(string fill, string label, double angle)
        {
            var glyph = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(Handset),
                Fill = System.Windows.Media.Brushes.White,
                Width = 24, Height = 24, Stretch = System.Windows.Media.Stretch.None,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new System.Windows.Media.RotateTransform(angle),
            };
            var face = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Child = glyph, Background = Editor.DocSerializer.Brush(fill) };
            var b = new Button { Content = face, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = label };
            b.Template = HoverTemplate();
            var text = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var stack = new StackPanel { Margin = new Thickness(26, 0, 26, 0) };
            stack.Children.Add(b);
            stack.Children.Add(text);
            return (b, stack);
        }

        var (hang, hangBox) = Round("#E5484D", "Cancel", 135);
        var (dial, dialBox) = Round("#2EA043", "Call", 0);
        hang.Click += (_, _) => hangUp();
        dial.Click += (_, _) => call();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        row.Children.Add(hangBox);
        row.Children.Add(dialBox);

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
        body.Children.Add(ask);
        body.Children.Add(note);
        body.Children.Add(row);
        callButton = dial;
        hangUpButton = hang;
        return body;
    }

    /// <summary>A round button that brightens a little on hover and dips when pressed.</summary>
    private static ControlTemplate HoverTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.Name = "cp";
        cp.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
        cp.SetValue(UIElement.RenderTransformProperty, new System.Windows.Media.ScaleTransform(1, 1));
        t.VisualTree = cp;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88, "cp"));
        var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(UIElement.RenderTransformProperty, new System.Windows.Media.ScaleTransform(0.94, 0.94), "cp"));
        t.Triggers.Add(hover);
        t.Triggers.Add(press);
        return t;
    }

    /// <summary>"Do you want to call …?" with a green call and a red hang-up button, like a phone. True: call.</summary>
    public static bool Call(Window? owner, string number)
    {
        Window? w = null;
        var result = false;
        var card = CallCard(number, () => { result = true; w!.Close(); }, () => w!.Close(), out _, out var hang);
        w = Make(owner, "Call", card, 380);
        hang.IsCancel = true;   // Esc hangs up; nothing calls without a click on the green button
        w.Loaded += (_, _) => hang.Focus();
        w.ShowDialog();
        return result;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var okB = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        body.Children.Add(text);
        body.Children.Add(Buttons(okB));
        var w = Make(owner, title, body, 440);
        okB.Click += (_, _) => w.Close();
        w.ShowDialog();
    }
}
