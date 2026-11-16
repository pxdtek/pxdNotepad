using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace pxdNotepad.Editor;

/// <summary>
/// The line across a note with a day's date on it (the child of a BlockUIContainer). Notes with "Date each day's
/// entries" on get one above the first thing written on a new day.
/// </summary>
public sealed class DateDivider : Grid
{
    public DateOnly Date { get; }

    public DateDivider(DateOnly date)
    {
        Date = date;
        Margin = new Thickness(0, 14, 0, 6);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Line();
        var text = new TextBlock { Text = Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var right = Line();
        SetColumn(text, 1);
        SetColumn(right, 2);
        Children.Add(left);
        Children.Add(text);
        Children.Add(right);
        ToolTip = "A new day's entries start here";
    }

    /// <summary>"Sunday, November 15, 2026" in the PC's own date style.</summary>
    public string Label => Date.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture);

    /// <summary>What search sees: the written-out date and the ISO one (so 2026-11-15 finds it too).</summary>
    public string SearchText => Label + " " + Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Rectangle Line()
    {
        var r = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Shape.FillProperty, "Accent");
        r.Opacity = 0.55;
        return r;
    }
}
