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
