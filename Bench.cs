using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using pxdNotepad.Data;
using pxdNotepad.Editor;
using pxdNotepad.Images;
using pxdNotepad.Views;

namespace pxdNotepad;

/// <summary>
/// pxdNotepad.exe --bench [report.txt]: times what users feel (start, typing in a long note, opening notes, search) and
/// measures memory, all off screen, in a temp data folder. For comparing before/after performance changes.
/// </summary>
public static class Bench
{
    private static readonly StringBuilder Out = new();
    private static void Say(string s) { Out.AppendLine(s); Console.WriteLine(s); }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Max(ms, 1)), DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        t.Start();
        Dispatcher.PushFrame(frame);
        t.Stop();
    }

    /// <summary>Runs queued UI work until idle (layout, render), timing it.</summary>
    private static void Idle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>What fills a notes file (pass a copy): space per table and index, free pages.</summary>
    private static int DbStats(string path)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        c.Open();
        void Q(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) Console.WriteLine(string.Join("  ", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i))));
        }
        Q("SELECT sqlite_version()");
        Q("SELECT (SELECT page_count FROM pragma_page_count), (SELECT freelist_count FROM pragma_freelist_count), (SELECT page_size FROM pragma_page_size)");
        try { Q("SELECT name, SUM(pgsize) FROM dbstat GROUP BY name ORDER BY 2 DESC LIMIT 15"); } catch (Exception e) { Console.WriteLine("no dbstat: " + e.Message); }
        Q("SELECT mime, w, h, length(orig), length(flat) FROM images");
        Q("SELECT COUNT(*), SUM(length(doc)), SUM(length(body)), SUM(purged) FROM notes");
        return 0;
    }

    /// <summary>Re-packs a notes file copy's PNG pictures with Png.Encode: sizes before/after, and the pixels must match.</summary>
    private static int PngTest(string path)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT orig FROM images WHERE mime='image/png' AND orig IS NOT NULL";
        using var r = cmd.ExecuteReader();
        long a = 0, b = 0, wpf = 0;
        while (r.Read())
        {
            var bytes = (byte[])r.GetValue(0);
            var src = ImageStore.Decode(bytes, int.MaxValue);
            var sw = Stopwatch.StartNew();
            var ours = Png.Encode(src);
            var t = sw.ElapsedMilliseconds;
            var again = ImageStore.Encode(src, "image/png");
            var back = ImageStore.Decode(ours, int.MaxValue);
            static byte[] Px(BitmapSource s) { var f = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0); var x = new byte[f.PixelWidth * f.PixelHeight * 4]; f.CopyPixels(x, f.PixelWidth * 4, 0); return x; }
            var same = Px(src).AsSpan().SequenceEqual(Px(back));
            Console.WriteLine($"{src.PixelWidth}x{src.PixelHeight}: stored {bytes.Length / 1024} KB, WPF again {again.Length / 1024} KB, new {ours.Length / 1024} KB ({t} ms) same pixels: {same}");
            a += bytes.Length; b += ours.Length; wpf += again.Length;
        }
        Console.WriteLine($"total: stored {a / 1024} KB, WPF {wpf / 1024} KB, new {b / 1024} KB");
        return 0;
    }

    private static int JpegTest()
    {
        foreach (var (w, h) in new[] { (4000, 3000), (3000, 2000), (2600, 1800) })
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Colors.Teal, Colors.Orange, 30), null, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            foreach (var mime in new[] { "image/jpeg", "image/png" })
            {
                var bytes = ImageStore.Encode(bmp, mime);
                var line = $"{w}x{h} {mime} {bytes.Length} {BitConverter.ToString(bytes, 0, 4)} {BitConverter.ToString(bytes, bytes.Length - 2)}:";
                foreach (var want in new[] { 640, 1024, 1280, 1408, 1920, 2048, 2560, int.MaxValue })
                {
                    try { line += $" {want}->{ImageStore.Decode(bytes, want).PixelWidth}"; }
                    catch (Exception e) { line += $" {want}->FAIL({e.GetType().Name})"; }
                }
                Console.WriteLine(line);
            }
        }
        return 0;
    }

    private static double PrivateMb() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64 / 1048576.0; }

    public static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "--dbstats") return DbStats(args[1]);
        if (args.Length == 2 && args[0] == "--pngtest") return PngTest(args[1]);
        if (args.Length == 1 && args[0] == "--jpegtest") return JpegTest();
        var report = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pxdNotepad-bench.txt");
        var data = Path.Combine(Path.GetTempPath(), "pxdNotepad-bench-" + Guid.NewGuid().ToString("N")[..8]);
        AppPaths.DataDir = data;
        pxdStandard.Chrome.DataDir = data;
        Db.Reset();
        var total = Stopwatch.StartNew();
        try
        {
            // a realistic notebook: 400 notes in 20 folders, one long note, 8 large pictures in one note
            using (var seed = new NoteStore())
            {
                var rnd = new Random(1);
                var cats = Enumerable.Range(0, 20).Select(i => seed.AddCategory($"Folder {i}", null).Id).ToList();
                using (var tx = seed.Connection.BeginTransaction())
                {
                    var count = int.TryParse(Environment.GetEnvironmentVariable("PXDN_BENCH_NOTES"), out var nn) ? nn : 400;
                    for (var i = 0; i < count; i++)
                    {
                        var body = string.Join(' ', Enumerable.Range(0, 200).Select(_ => "word" + rnd.Next(5000)));
                        seed.NewNote(cats[i % 20], $"Note {i}", "", body);
                    }
                    tx.Commit();
                }
                var sb = new StringBuilder("{\"v\":1,\"b\":[");
                for (var i = 0; i < 3000; i++) sb.Append(i > 0 ? "," : "").Append("{\"p\":[{\"t\":\"Line ").Append(i).Append(" of a long running log with some words on it\"}]}");
                sb.Append("]}");
                seed.NewNote(cats[0], "Long log", sb.ToString(), "long log");
            }

            var sw = Stopwatch.StartNew();
            var toWindow = Stopwatch.StartNew();
            // as Program does: the update check (a web request) runs while the window is built
            var upd = pxdStandard.UpdateAtStart.Begin("pxdtek/pxdNotepad", @"^pxdNotepad-.*-win-x64\.zip$", []);
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Say($"start: App resources        {sw.ElapsedMilliseconds,6} ms");
            sw.Restart();
            var settings = new AppSettings { LineNumbers = true, SpellCheck = Environment.GetEnvironmentVariable("PXDN_BENCH_NOSPELL") != "1" };
            var store = new NoteStore();
            Say($"start: open database        {sw.ElapsedMilliseconds,6} ms");
            sw.Restart();
            var win = new MainWindow(store, settings);
            Say($"start: build window         {sw.ElapsedMilliseconds,6} ms");
            upd?.Wait();
            Say($"start: ready to show window {toWindow.ElapsedMilliseconds,6} ms (with the update check)");
            sw.Restart();
            var frame = (FrameworkElement)win.Content;
            win.Content = null;
            var host = new Border { Child = frame, Width = 1400, Height = 860 };
            host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
            using var src = new HwndSource(new HwndSourceParameters("bench", 1400, 860) { WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 }) { RootVisual = host };
            Idle();
            Say($"start: first layout+render  {sw.ElapsedMilliseconds,6} ms");
            Pump(300);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Say($"memory after start:         {PrivateMb(),6:0} MB private, {GC.GetTotalMemory(false) / 1048576.0:0.0} MB managed");

            // update check (network) as it runs before the window today
            sw.Restart();
            var plan = pxdStandard.pxdUpdate.FindUpdateAsync("pxdtek/pxdNotepad", @"^pxdNotepad-.*-win-x64\.zip$", new Version(99, 0)).GetAwaiter().GetResult();
            Say($"start: update check (net)   {sw.ElapsedMilliseconds,6} ms");

            var view = win.View;
            var longId = store.Notes(null).First(n => n.Title == "Long log").Id;
            sw.Restart();
            view.OpenNote(longId, []);
            Idle();
            Say($"open the 3000-line note     {sw.ElapsedMilliseconds,6} ms");
            view.Editor.ScrollToEnd();
            view.Editor.CaretPosition = view.Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            Idle();
            Pump(1000);   // spell checking comes back on a moment after a note opens;

            // typing at the end of the long note: each key, then let layout/render (and the layers) catch up
            void Type(string what)
            {
                var times = new List<double>();
                for (var i = 0; i < 40; i++)
                {
                    sw.Restart();
                    view.Editor.CaretPosition.InsertTextInRun("x");
                    Idle();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                Say($"typing, 3000 lines, {what,-22} median {times[times.Count / 2],6:0.0} ms, slowest {times[^1]:0.0} ms per key");
            }
            Type("everything on:");
            if (Environment.GetEnvironmentVariable("PXDN_BENCH_PARTS") == "1")
            {
                sw.Restart();
                for (var k = 0; k < 50; k++) view.Gutter.VisibleLines();
                Say($"  line numbers: finding the lines on screen {sw.Elapsed.TotalMilliseconds / 50:0.00} ms");
                sw.Restart();
                for (var k = 0; k < 50; k++) view.Links.Refresh();
                Say($"  links: finding the links on screen        {sw.Elapsed.TotalMilliseconds / 50:0.00} ms");
            }
            if (Environment.GetEnvironmentVariable("PXDN_BENCH_PARTS") == "1")
            {
                view.Blur.Visibility = Visibility.Collapsed; Type("no blur layer:");
                view.Links.Visibility = Visibility.Collapsed; Type("+ no link layer:");
                view.Gutter.Visibility = Visibility.Collapsed; Type("+ no line numbers:");
                System.Windows.Controls.SpellCheck.SetIsEnabled(view.Editor, false); Type("+ no spell check:");
                view.Blur.Visibility = view.Links.Visibility = view.Gutter.Visibility = Visibility.Visible;
                System.Windows.Controls.SpellCheck.SetIsEnabled(view.Editor, true);
            }
            var saves = new List<double>();
            for (var k = 0; k < 5; k++)
            {
                view.Editor.CaretPosition.InsertTextInRun("y");
                Idle();
                sw.Restart();
                view.SaveNow();
                saves.Add(sw.Elapsed.TotalMilliseconds);
            }
            saves.Sort();
            Say($"save the long note          {saves[2],6:0} ms (median of 5)");

            sw.Restart();
            var shortId = store.Notes(null).First(n => n.Title.StartsWith("Note ")).Id;
            view.OpenNote(shortId, []);
            Idle();
            Say($"open a short note           {sw.ElapsedMilliseconds,6} ms");

            // pictures: 8 photos 4000x3000 in one note
            var imgs = new ImageStore(store.Connection);
            var ids = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb((byte)(i * 30), 80, 160), Colors.Orange, 30), null, new Rect(0, 0, 4000, 3000));
                var bmp = new RenderTargetBitmap(4000, 3000, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(dv);
                var penc = new PngBitmapEncoder();   // PNG: 1.10.0 couldn't show JPEGs
                penc.Frames.Add(BitmapFrame.Create(bmp));
                using var pms = new MemoryStream();
                penc.Save(pms);
                ids.Add(imgs.Import(pms.ToArray()));
            }
            var picDoc = "{\"v\":1,\"b\":[" + string.Join(",", ids.Select(id => "{\"p\":[{\"img\":\"" + id + "\",\"w\":600}]}")) + "]}";
            var picId = store.NewNote(null, "Photos", picDoc, "");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = PrivateMb();
            sw.Restart();
            view.OpenNote(picId, []);
            Idle();
            Pump(300);
            Say($"open a note with 8 photos   {sw.ElapsedMilliseconds,6} ms");
            long decoded = 0;
            foreach (var b in view.Editor.Document.Blocks.OfType<Paragraph>())
                foreach (var c in b.Inlines.OfType<InlineUIContainer>())
                    if (c.Child is ImageBox box && box.Children[0] is Image { Source: BitmapSource bs }) decoded += (long)bs.PixelWidth * bs.PixelHeight * 4;
            Say($"pictures decoded for 8 photos shown at 600 px: {decoded / 1048576.0,6:0.0} MB");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Say($"memory with the photos open: {PrivateMb(),6:0} MB private");

            // sitting still with the photos open (line numbers, links and blur layers on): nothing should keep running
            Pump(1500);
            using (var me = Process.GetCurrentProcess())
            {
                var cpu0 = me.TotalProcessorTime;
                int g0 = view.Gutter.Renders, l0 = view.Links.Renders, b0 = view.Blur.Renders;
                Pump(3000);
                me.Refresh();
                Say($"idle 3 s with photos open:  {(me.TotalProcessorTime - cpu0).TotalMilliseconds,6:0} ms CPU, redraws {view.Gutter.Renders - g0}/{view.Links.Renders - l0}/{view.Blur.Renders - b0}");
            }

            // search
            using var se = new SearchEngine();
            se.Search("warm", 10, out _);
            se.Search("word123", 200, out var ms);
            Say($"search 400 notes            {ms,6:0.0} ms");

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Say($"memory at the end:          {PrivateMb(),6:0} MB private, threads {Process.GetCurrentProcess().Threads.Count}");
            win.Sync.Dispose();
        }
        catch (Exception e)
        {
            Say("bench crashed: " + e);
        }
        finally
        {
            try { Directory.Delete(data, true); } catch { /* temp */ }
        }
        Say($"(bench took {total.Elapsed.TotalSeconds:0.0} s)");
        File.WriteAllText(report, Out.ToString());
        return 0;
    }
}
