using System.Windows;

namespace pxdNotepad;

public partial class App : Application
{
    /// <summary>Swaps the colour dictionary (the first merged one); everything uses DynamicResource, so it's live.</summary>
    public static void SetTheme(string theme)
    {
        var dicts = Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/pxdNotepad;component/Themes/{(theme == "light" ? "Light" : "Dark")}.xaml") };
    }
}
