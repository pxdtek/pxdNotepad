using System.Windows;
using pxdNotepad.Data;
using pxdNotepad.Views;
using pxdStandard;

namespace pxdNotepad;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--self-test") return SelfTest.Run(args.Skip(1).ToArray());
#if BENCH
        if (args.Length >= 1 && args[0] == "--bench") return Bench.Run(args.Skip(1).ToArray());
#endif
        if (args.Length >= 2 && args[0] == "--make-icon") { BrandIcon.WriteIco(args[1]); return 0; }
        if (args.Length >= 2 && args[0] == "--make-png") { BrandIcon.WritePng(args[1], args.Length > 2 ? int.Parse(args[2]) : 256); return 0; }

        // pxd App Standard 6: update before anything opens. The check runs while the window is built (below).
        var update = UpdateAtStart.Begin("pxdtek/pxdNotepad", @"^pxdNotepad-.*-win-x64\.zip$", args);

        // A file to open (Explorer passes the .txt that was double-clicked).
        var file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)) is { } f ? Path.GetFullPath(f) : null;

        using var single = new SingleInstance("pxdNotepad");
        if (!single.IsFirst)
        {
            single.SignalFirst(file);
            return 0;
        }

        Chrome.DataDir = AppPaths.DataDir;
        var settings = AppSettings.Load();
        var app = new App();
        app.InitializeComponent();
        App.SetTheme(settings.Theme);
        using var store = new NoteStore();
        var w = new MainWindow(store, settings);
        single.ShowRequested += () => app.Dispatcher.BeginInvoke(w.BringUp);
        single.FileRequested += path => app.Dispatcher.BeginInvoke(() => { w.BringUp(); w.View.OpenFile(path); });
        app.MainWindow = w;
        if (UpdateAtStart.ApplyIfFound(update, args))
        {
            w.Sync.Dispose();
            return 0;
        }
        w.Show();
        if (file is not null) w.View.OpenFile(file);
        if (Environment.ProcessPath is { } exe) FileTypes.RefreshIfRegistered(exe);
        return app.Run();
    }
}
