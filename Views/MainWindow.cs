using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using pxdNotepad.Data;
using pxdNotepad.Sync;
using pxdStandard;

namespace pxdNotepad.Views;

public sealed class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Path _syncIcon;
    private readonly Button _syncButton;
    private readonly Path _focusGlyph;
    private readonly List<Button> _barButtons = [];
    private Chrome.Bar _bar = null!;
    private const string FocusOn = "M2,6 L2,2 L6,2 M10,2 L14,2 L14,6 M14,10 L14,14 L10,14 M6,14 L2,14 L2,10";
    private const string FocusOff = "M6,2 L6,6 L2,6 M14,6 L10,6 L10,2 M10,14 L10,10 L14,10 M2,10 L6,10 L6,14";
    public MainView View { get; }
    public SyncService Sync { get; }

    private const string CloudGlyph = "M5,13.5 L12.5,13.5 C14.4,13.5 15.5,12.2 15.5,10.6 C15.5,9 14.3,7.8 12.8,7.7 C12.4,5.4 10.6,4 8.5,4 C6.3,4 4.6,5.5 4.2,7.6 C2.6,7.9 1.5,9.1 1.5,10.6 C1.5,12.2 2.9,13.5 5,13.5 Z";

    public MainWindow(NoteStore store, AppSettings settings)
    {
        _settings = settings;
        Width = 1320;
        Height = 820;
        MinWidth = 920;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        View = new MainView(store, settings);

        _syncIcon = new Path { Data = Geometry.Parse(CloudGlyph), Width = 17, Height = 17, StrokeThickness = 1.4, Stretch = Stretch.None, StrokeLineJoin = PenLineJoin.Round };
        _syncIcon.SetResourceReference(Shape.StrokeProperty, "Faint");
        _syncButton = TitleButton(_syncIcon, "Sync is off: click to set it up", () =>
        {
            if (_settings.GetSyncKey() is null) OpenSettings(sync: true);
            else _ = Sync!.SyncNowAsync();
        });
        var themeGlyph = new Path { Data = Geometry.Parse("M12.5,10.2 C11.9,10.4 11.3,10.5 10.7,10.5 C7.8,10.5 5.5,8.2 5.5,5.3 C5.5,4.7 5.6,4.1 5.8,3.5 C3.6,4.3 2,6.4 2,8.9 C2,12 4.5,14.5 7.6,14.5 C10.1,14.5 12.2,12.9 12.5,10.2 Z"), Width = 17, Height = 17, StrokeThickness = 1.4, Stretch = Stretch.None };
        themeGlyph.SetResourceReference(Shape.StrokeProperty, "TitleText");
        var theme = TitleButton(themeGlyph, "Dark / light", ToggleTheme);
        var gearGlyph = new Path { Data = Geometry.Parse("M8.5,6 A2.5,2.5 0 1 1 8.49,6 Z M8.5,1.5 L8.5,3.2 M8.5,13.8 L8.5,15.5 M1.5,8.5 L3.2,8.5 M13.8,8.5 L15.5,8.5 M3.5,3.5 L4.8,4.8 M12.2,12.2 L13.5,13.5 M3.5,13.5 L4.8,12.2 M12.2,4.8 L13.5,3.5"), Width = 17, Height = 17, StrokeThickness = 1.4, Stretch = Stretch.None, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        gearGlyph.SetResourceReference(Shape.StrokeProperty, "TitleText");
        var gear = TitleButton(gearGlyph, "Settings", () => OpenSettings());
        var sideGlyph = new Path { Data = Geometry.Parse("M2,3 L14,3 L14,13 L2,13 Z M6,3 L6,13"), Width = 17, Height = 17, StrokeThickness = 1.4, Stretch = Stretch.None };
        sideGlyph.SetResourceReference(Shape.StrokeProperty, "TitleText");
        var side = TitleButton(sideGlyph, "Show / hide the notes list (Ctrl+\\)", View.ToggleSidebar);
        _focusGlyph = new Path { Data = Geometry.Parse(FocusOn), Width = 17, Height = 17, StrokeThickness = 1.4, Stretch = Stretch.None, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _focusGlyph.SetResourceReference(Shape.StrokeProperty, "TitleText");
        var focus = TitleButton(_focusGlyph, "Writing mode: just the page (F11)", View.ToggleFocus);
        var extras = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
        extras.Children.Add(_syncButton);
        extras.Children.Add(side);
        extras.Children.Add(focus);
        extras.Children.Add(theme);
        extras.Children.Add(gear);
        _barButtons.AddRange([_syncButton, side, focus, theme, gear]);

        _bar = Chrome.Apply(this, "pxd Notepad", View, BrandIcon.Image, resizable: true, extras: extras, center: View.SearchBar);
        View.LayoutModeChanged += ApplyMode;
        ApplyMode();
        Chrome.RememberPlacement(this, "main");

        Sync = new SyncService(AppPaths.Database, () => (_settings.SyncServer, _settings.GetSyncKey()));
        View.Sync = Sync;
        Sync.StatusChanged += s => Dispatcher.BeginInvoke(() => ShowSync(s));
        Sync.RemoteChanged += c => Dispatcher.BeginInvoke(() => View.OnRemoteChanges(c));
        ShowSync(Sync.Status);
        Loaded += (_, _) => { if (_settings.GetSyncKey() is not null) Sync.Kick(TimeSpan.FromSeconds(1)); };
        Activated += (_, _) =>
        {
            if (_settings.GetSyncKey() is not null && (Sync.Status.LastOk is null || DateTime.Now - Sync.Status.LastOk > TimeSpan.FromSeconds(30)))
                Sync.Kick(TimeSpan.FromMilliseconds(400));
        };
        Closing += (_, _) =>
        {
            View.SaveLayout();
            if (_settings.GetSyncKey() is not null)
            {
                // Last changes out before closing (a few seconds at most; the rest goes next time).
                try { Task.Run(() => Sync.SyncNowAsync()).Wait(TimeSpan.FromSeconds(4)); } catch { /* next start */ }
            }
            Sync.Dispose();
        };
    }

    /// <summary>Writing mode: a slim title bar (search stays, smaller); normal: the full 38 px one.</summary>
    private void ApplyMode()
    {
        var focus = View.FocusMode;
        var h = focus ? 30 : 38;
        _bar.SetHeight(h);
        foreach (var b in _barButtons) { b.Height = h; b.Width = focus ? 34 : 40; }
        View.SearchBar.Height = focus ? 22 : 28;
        View.SearchBar.Width = focus ? 300 : 420;
        _bar.Title.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        _focusGlyph.Data = Geometry.Parse(focus ? FocusOff : FocusOn);
    }

    private static Button TitleButton(UIElement content, string tip, Action click)
    {
        var b = new Button { Content = content, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "TitleBarButton");
        b.Click += (_, _) => click();
        return b;
    }

    private void ShowSync(SyncStatus s)
    {
        var (brush, tip) = s.State switch
        {
            SyncState.Off => ("Faint", "Sync is off: click to set it up"),
            SyncState.Syncing => ("Accent", "Syncing…"),
            SyncState.Ok => ("Accent", $"Synced {s.LastOk:h:mm tt} · click to sync now"),
            SyncState.Offline => ("Warn", s.Message + (s.LastOk is { } t ? $" · last synced {t:h:mm tt}" : "") + " · click to try again"),
            _ => ("Danger", s.Message + " · click to try again"),
        };
        _syncIcon.SetResourceReference(Shape.StrokeProperty, brush);
        _syncIcon.Opacity = s.State == SyncState.Syncing ? 0.6 : 1;
        _syncButton.ToolTip = tip;
    }

    private void ToggleTheme()
    {
        _settings.Theme = _settings.Theme == "light" ? "dark" : "light";
        App.SetTheme(_settings.Theme);
        _settings.Save();
        View.BuildTree();
    }

    public void OpenSettings(bool sync = false)
    {
        var w = new SettingsWindow(_settings, Sync, View) { Owner = this };
        w.ShowDialog();
    }

    public void BringUp() => Chrome.BringToFront(this);
}
