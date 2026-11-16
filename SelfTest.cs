using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
using pxdNotepad.Sync;
using pxdNotepad.Views;

namespace pxdNotepad;

/// <summary>
/// pxdNotepad.exe --self-test [out-dir]: checks storage, search speed, the note format, line numbers, images, markup
/// and (with PXDNOTEPAD_TEST_SERVER set) sync between two copies. Nothing is ever shown on screen: the UI is laid out
/// in a window that is never made visible and drawn to PNG files in out-dir. Uses a temp data folder, never the real one.
/// </summary>
public static class SelfTest
{
    private static int _fail, _pass;
    private static readonly StringBuilder Report = new();

    private static void Ok(bool cond, string what)
    {
        var line = (cond ? "ok   " : "FAIL ") + what;
        Report.AppendLine(line);
        Console.WriteLine(line);
        if (cond) _pass++; else _fail++;
    }

    public static int Run(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pxdNotepad-selftest");
        Directory.CreateDirectory(outDir);
        var data = Path.Combine(Path.GetTempPath(), "pxdNotepad-test-" + Guid.NewGuid().ToString("N")[..8]);
        AppPaths.DataDir = data;
        pxdStandard.Chrome.DataDir = data;
        Db.Reset();
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Migration();
            Storage();
            SearchSpeed();
            Format();
            Ui(outDir);
            Markup(outDir);
            Keys();
            SyncTest(outDir).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Ok(false, "crashed: " + e);
        }
        finally
        {
            try { Directory.Delete(data, true); } catch { /* sqlite may hold it a moment */ }
        }
        Report.AppendLine($"{_pass} passed, {_fail} failed");
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report.ToString());
        return _fail == 0 ? 0 : 1;
    }

    // ── storage ──

    private static void Storage()
    {
        using var s = new NoteStore();
        var work = s.AddCategory("Work", null);
        var proj = s.AddCategory("Projects", work.Id);
        var deep = s.AddCategory("Q4", proj.Id);
        Ok(s.Categories().Count == 3, "categories and sub-categories");
        Ok(!s.MoveCategory(work.Id, deep.Id), "a category can't go inside its own sub-category");
        var n1 = s.NewNote(proj.Id, "Kickoff", "", "agenda and owners");
        s.NewNote(deep.Id, "Budget", "", "numbers");
        s.NewNote(null, "Loose", "", "unfiled text");
        var counts = s.Counts(out var all, out var unfiled, out _);
        Ok(all == 3 && unfiled == 1 && counts[proj.Id] == 1, "counts per category");
        s.DeleteCategory(proj.Id);
        var cats = s.Categories();
        Ok(cats.Count == 2 && cats.First(c => c.Id == deep.Id).Parent == work.Id && s.Load(n1)!.Cat == work.Id, "deleting a category moves its notes and sub-categories up");
        s.Trash(n1, true);
        Ok(s.Notes(null).Count == 2 && s.Notes(null, trash: true).Count == 1, "trash");
        s.Purge(n1);
        Ok(s.Load(n1) is null && s.Notes(null, trash: true).Count == 0, "delete for good");
    }

    /// <summary>A 1.1 database (no tags) opens in 1.2: columns added, search index rebuilt, notes still found.</summary>
    private static void Migration()
    {
        var path = Path.Combine(AppPaths.DataDir, "old.db");
        Directory.CreateDirectory(AppPaths.DataDir);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            Db.Exec(c, """
                CREATE TABLE notes(rid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, cat TEXT, title TEXT NOT NULL DEFAULT '', body TEXT NOT NULL DEFAULT '', doc TEXT NOT NULL DEFAULT '',
                  created INTEGER NOT NULL, updated INTEGER NOT NULL, trashed INTEGER NOT NULL DEFAULT 0, purged INTEGER NOT NULL DEFAULT 0,
                  dirty INTEGER NOT NULL DEFAULT 1, ver INTEGER NOT NULL DEFAULT 0, srv INTEGER NOT NULL DEFAULT 0);
                CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, content='notes', content_rowid='rid', tokenize='trigram');
                CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN INSERT INTO notes_fts(rowid, title, body) VALUES (new.rid, new.title, new.body); END;
                INSERT INTO notes(id,title,body,created,updated) VALUES('0123456789abcdef0123456789abcdef','Old note','written before tags existed',1,1);
                """);
        }
        using (var s = new NoteStore(path))
        {
            Ok(Db.Scalar(s.Connection, "SELECT tags FROM notes") is string, "an older notes file gets the tag columns");
            using var e = new SearchEngine(path);
            Ok(e.Search("before tags", 5, out _).Count == 1, "its notes are still found after the upgrade");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static void SearchSpeed()
    {
        using var s = new NoteStore();
        var rnd = new Random(7);
        string[] words = ["router", "invoice", "meeting", "firmware", "kitchen", "grocery", "password", "backup", "server", "printer", "garden", "budget", "travel", "camera", "network", "project", "deadline", "recipe", "battery", "license"];
        var sw = Stopwatch.StartNew();
        using (var tx = s.Connection.BeginTransaction())
        {
            for (var i = 0; i < 20000; i++)
            {
                var sb = new StringBuilder();
                for (var w = 0; w < 120; w++) sb.Append(words[rnd.Next(words.Length)]).Append(' ');
                if (i == 12345) sb.Append("the zanzibar lighthouse key is under the mat");
                Db.Exec(s.Connection, "INSERT INTO notes(id,title,body,created,updated) VALUES($id,$t,$b,1,$u)", ("$id", Db.NewId()), ("$t", $"Note {i} {words[i % words.Length]}"), ("$b", sb.ToString()), ("$u", (long)i));
            }
            tx.Commit();
        }
        Report.AppendLine($"     (20,000 notes written in {sw.ElapsedMilliseconds} ms)");
        using var e = new SearchEngine();
        e.Search("warm up", 10, out _);
        var hits = e.Search("zanzib", 50, out var ms1);
        Ok(hits.Count == 1 && hits[0].Snippet.Contains("zanzibar"), $"part of a word found in 20,000 notes: {ms1:0.00} ms");
        hits = e.Search("lighthouse mat", 50, out var ms2);
        Ok(hits.Count == 1, $"two words (one short): {ms2:0.00} ms");
        hits = e.Search("ZANZIBAR", 50, out _);
        Ok(hits.Count == 1, "any case");
        e.Search("router", 300, out var ms3);
        Ok(ms3 < 150, $"a common word, first 300 of thousands: {ms3:0.00} ms");
        hits = e.Search("ke", 50, out var ms4);
        Ok(hits.Count > 0, $"a two-letter search (no index): {ms4:0.00} ms");
        Ok(ms1 < 50 && ms2 < 80, "searches well under a frame");
        Db.Exec(s.Connection, "DELETE FROM notes");
    }

    // ── the note format ──

    private static FlowDocument SampleDoc(string? imageId)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 15, Foreground = Brushes.White };
        var h = new Paragraph(new Run("Big heading") { FontSize = 32, FontWeight = FontWeights.Bold, Foreground = DocSerializer.Brush("#39C5CF") });
        var p = new Paragraph();
        p.Inlines.Add(new Run("Normal, "));
        p.Inlines.Add(new Run("red italic") { Foreground = DocSerializer.Brush("#FF3B30"), FontStyle = FontStyles.Italic });
        p.Inlines.Add(new Run(", highlighted") { Background = DocSerializer.Brush("#FFE066") });
        p.Inlines.Add(new Underline(new Run(" underlined")));
        p.Inlines.Add(new LineBreak());
        p.Inlines.Add(new Run("Consolas line") { FontFamily = new FontFamily("Consolas"), FontSize = 12 });
        var center = new Paragraph(new Run("Centered")) { TextAlignment = TextAlignment.Center };
        var list = new List { MarkerStyle = TextMarkerStyle.Decimal };
        list.ListItems.Add(new ListItem(new Paragraph(new Run("first"))));
        list.ListItems.Add(new ListItem(new Paragraph(new Run("second"))));
        doc.Blocks.Add(h);
        doc.Blocks.Add(p);
        doc.Blocks.Add(center);
        doc.Blocks.Add(list);
        if (imageId is not null)
            doc.Blocks.Add(new Paragraph(new InlineUIContainer(new ImageBox(imageId, 300))));
        doc.Blocks.Add(new Paragraph(new Run("Small last line") { FontSize = 11 }));
        return doc;
    }

    private static void Format()
    {
        var doc = SampleDoc(null);
        var json = DocSerializer.Write(doc, null, out var plain);
        Ok(plain.Contains("Big heading") && plain.Contains("Consolas line") && plain.Contains("second"), "plain text for search");
        var back = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 15, Foreground = Brushes.White };
        DocSerializer.Read(back, json);
        var again = DocSerializer.Write(back, null, out _);
        Ok(json == again, "format round-trips exactly");
        Ok(json.Contains("\"z\":32") && json.Contains("\"w\":700") && json.Contains("#FF3B30") && json.Contains("\"h\":\"#FFE066\"") && json.Contains("\"u\":1") && json.Contains("Consolas") && json.Contains("\"a\":\"c\"") && json.Contains("\"l\":\"decimal\""),
            "keeps size, bold, colour, highlight, underline, font, alignment, lists");
        Report.AppendLine($"     (sample note: {json.Length} bytes of JSON)");
        DocSerializer.Read(back, "not json", "line one\nline two");
        Ok(new TextRange(back.ContentStart, back.ContentEnd).Text.Contains("line two"), "damaged note falls back to its text");
    }

    // ── UI, drawn off screen ──

    private static HwndSource Host(UIElement root, int w, int h)
    {
        var p = new HwndSourceParameters("pxdNotepad self-test", w, h)
        {
            WindowStyle = unchecked((int)0x80000000),    // WS_POPUP, and never WS_VISIBLE: nothing appears
            ExtendedWindowStyle = 0x00000080 | 0x08000000, // tool window, no activate
            PositionX = -32000,
            PositionY = -32000,
        };
        var src = new HwndSource(p) { RootVisual = root, SizeToContent = SizeToContent.Manual };
        if (root is FrameworkElement fe) { fe.Width = w; fe.Height = h; }
        Pump(150);
        return src;
    }

    private static void Pump(int ms = 0)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (ms > 0) Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
    }

    private static void Shot(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static byte[] TestPicture(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1d, 0x4e, 0x89), Color.FromRgb(0x39, 0xC5, 0xCF), 30), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(Brushes.White, null, new Point(w * 0.7, h * 0.45), w * 0.12, w * 0.12);
            dc.DrawRectangle(Brushes.Orange, null, new Rect(w * 0.12, h * 0.6, w * 0.25, h * 0.25));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return ImageStore.Encode(rtb, "image/png");
    }

    private static void Ui(string outDir)
    {
        using var store = new NoteStore();
        var settings = new AppSettings { LineNumbers = true, FontSize = 15 };
        var images = new ImageStore(store.Connection);
        ImageBox.Store = images;
        var img = images.Import(TestPicture(1200, 700));
        Ok(images.Info(img) is { Width: 1200, Height: 700 }, "image stored with its size");

        var work = store.AddCategory("Work", null);
        var proj = store.AddCategory("Projects", work.Id);
        store.AddCategory("Home", null);
        store.AddCategory("Recipes", null);
        var docJson = DocSerializer.Write(SampleDoc(img), images, out var plain);
        var id = store.NewNote(proj.Id, "Network rollout plan", docJson, plain);
        store.NewNote(proj.Id, "Printer drivers", "", "Find the right driver for the office printer and test it.");
        store.NewNote(work.Id, "Meeting notes", "", "Discussed the router firmware and the backup schedule.");
        store.NewNote(null, "Groceries", "", "eggs, milk, coffee");
        settings.LastNote = id;

        var view = new MainView(store, settings);
        var border = new Border { Child = view };
        border.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        using var src = Host(border, 1400, 860);
        Pump(300);
        view.Gutter.Reset();
        Pump(200);

        var lines = view.Gutter.VisibleLines();
        Ok(lines.Count >= 6, $"line numbers drawn ({lines.Count} visible)");
        Ok(lines.Count > 0 && lines[0].Number == 1 && Math.Abs(lines[0].Size - 32) < 0.1, "line 1 (the 32 pt heading) gets a 32 pt number");
        var small = lines.FirstOrDefault(l => Math.Abs(l.Size - 12) < 0.1);
        Ok(small.Number > 0, "the 12 pt Consolas line gets a 12 pt number");
        Ok(lines.Zip(lines.Skip(1)).All(p => p.Second.Baseline > p.First.Baseline), "numbers go down the page in order");
        Ok(lines.Any(l => Math.Abs(l.Size - 15) < 0.1), "normal lines use the note's 15 pt");
        Shot(border, Path.Combine(outDir, "main-dark.png"));

        // The tree: folders hold sub-folders and then their notes; loose notes at the top level; Trash last.
        var tree = (TreeView)view.FindName("Tree");
        var roots = tree.Items.Cast<SideNode>().ToList();
        var workNode = roots.First(r => r.Id == work.Id);
        var projNode = workNode.Children.FirstOrDefault(c => c.IsFolder && c.Name == "Projects");
        Ok(projNode is not null && workNode.Children.IndexOf(projNode) == 0 && workNode.Children.Any(c => c.IsNote && c.Name == "Meeting notes"), "folders show their sub-folders first, then their notes");
        Ok(projNode!.Children.Select(c => c.Name).SequenceEqual(["Network rollout plan", "Printer drivers"]) && projNode.Count == 2 && workNode.Count == 3, "notes inside folders, A to Z, with counts");
        Ok(roots.Any(r => r.IsNote && r.Name == "Groceries") && roots[^1].Kind == NodeKind.Trash, "loose notes at the top level, Trash last");
        Ok(projNode.Children[0].IsSelected && projNode.IsExpanded && workNode.IsExpanded, "the open note is selected with its folders expanded");

        // Copy a range with the image and paste it at the end: a copy of the picture comes along.
        var ed = view.Editor;
        var json = DocSerializer.Write(ed.Document, null, out _, ed.Document.ContentStart, ed.Document.ContentEnd);
        ed.CaretPosition = ed.Document.ContentEnd;
        var before = Convert.ToInt64(Db.Scalar(store.Connection, "SELECT COUNT(*) FROM images"));
        view.InsertFragment(json);
        Pump(100);
        var after = Convert.ToInt64(Db.Scalar(store.Connection, "SELECT COUNT(*) FROM images"));
        var text = new TextRange(ed.Document.ContentStart, ed.Document.ContentEnd).Text;
        Ok(after == before + 1 && text.Split("Big heading").Length == 3, "copy + paste inside a note keeps text and picture (picture copied)");
        view.SaveNow();
        var saved = store.Load(id)!;
        Ok(saved.Body.Split("Small last line").Length == 3, "pasted text saved and searchable");

        // Resize the picture and check the saved width.
        var box = FindAll<ImageBox>(ed.Document).First();
        box.SetWidth(222);
        Pump(50);
        view.SaveNow();
        Ok(store.Load(id)!.Doc.Contains("\"w\":222"), "resized picture keeps its new size");

        // Search: results for the title bar's drop-down (drawn on its own, since a real drop-down would show on screen).
        view.SearchBox.Text = "drive";
        Pump(400);
        Ok(view.Results.Items.Count == 1 && ((NoteItem)view.Results.Items[0]).Title == "Printer drivers", "title-bar search finds the note");
        var card = view.SearchCard;
        view.DetachSearchCard();
        var cardHost = new Border { Child = card, Padding = new Thickness(10) };
        cardHost.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        using (Host(cardHost, 600, 230)) { Pump(200); Shot(cardHost, Path.Combine(outDir, "search-dropdown.png")); }
        cardHost.Child = null;
        view.AttachSearchCard(card);
        view.SearchBox.Text = "";
        Pump(200);

        // Tags: a symbol tag and one made from an .ico file, shown on the note's row and found by name.
        var tags = view.Tags;
        var fav = tags.Add("Favorites", "heart", "#FF3B30", null);
        var icoPath = Path.Combine(AppPaths.DataDir, "test.ico");
        BrandIcon.WriteIco(icoPath);
        var png = TagIcons.ImportFile(icoPath);
        var custom = tags.Add("Splashtop", "", "", png);
        var pngSize = ImageStore.Decode(png);
        Ok(pngSize.PixelWidth == 48, $"an .ico file becomes a 48 px tag icon ({pngSize.PixelWidth} px)");
        var printer = store.Notes(null).First(n => n.Title == "Printer drivers").Id;
        Ok(tags.Toggle(id, fav.Id) && tags.Toggle(id, custom.Id) && tags.Toggle(printer, fav.Id), "tags put on notes");
        view.BuildTree();
        Pump(150);
        var plan = tree.Items.Cast<SideNode>().SelectMany(r => r.Walk()).First(x => x.Id == id);
        Ok(plan.TagIcons.Select(t => t.Name).SequenceEqual(["Favorites", "Splashtop"]), "the note's row shows both tag icons");
        using (var se = new SearchEngine())
        {
            Ok(se.Search("favorites", 50, out _).Select(h => h.Id).OrderBy(x => x).SequenceEqual(new[] { id, printer }.OrderBy(x => x)), "searching a tag's name finds every note with it");
            Ok(se.Search("#splash", 50, out _).Select(h => h.Id).SequenceEqual([id]), "#name works too, and part of the name");
            tags.Update(fav.Id, "Keepers", "star", "#FFCC00", null);
            Ok(se.Search("keepers", 50, out _).Count == 2 && se.Search("favorites", 50, out _).Count == 0, "a renamed tag is found by its new name");
            Ok(!tags.Toggle(printer, fav.Id) && se.Search("keepers", 50, out _).Count == 1, "taking a tag off a note");
            tags.Delete(custom.Id);
            Ok(se.Search("splashtop", 50, out _).Count == 0 && tags.TagsOf(id).SequenceEqual([fav.Id]), "deleting a tag takes it off its notes");
        }
        tags.Toggle(printer, fav.Id);
        tags.Add("Work stuff", "work", "#0A84FF", null);
        var splash = tags.Add("Splashtop", "", "", png);
        tags.Toggle(printer, splash.Id);
        view.BuildTree();
        Pump(150);
        Shot(border, Path.Combine(outDir, "tags-tree.png"));
        var tw = new TagsWindow(tags);
        var twContent = (UIElement)tw.Content;
        tw.Content = null;
        var twHost = new Border { Child = twContent };
        twHost.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        using (Host(twHost, 760, 560)) { Pump(250); Shot(twHost, Path.Combine(outDir, "tags-window.png")); }
        tw.Close();

        // Date each day's entries: off by default; on, the first words of a new day get a date line.
        var yesterday = DateOnly.FromDateTime(DateTime.Now.AddDays(-1)).ToString("yyyy-MM-dd");
        var journal = store.NewNote(null, "Work log", $$"""{"v":1,"daily":1,"b":[{"d":"{{yesterday}}"},{"p":[{"t":"Fixed the router."}]}]}""", "Fixed the router.");
        var plainNote = store.NewNote(null, "Plain", "", "");
        view.OpenNote(plainNote, []);
        Pump(100);
        Ok(!view.DailyOn && !view.EnsureTodayDivider(), "date lines are off by default");
        view.OpenNote(journal, []);
        Pump(150);
        Ok(view.DailyOn, "a note that has them on opens with them on");
        var ed2 = view.Editor;
        ed2.CaretPosition = ed2.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Ok(!view.EnsureTodayDivider(), "editing older text doesn't add a date line");
        ed2.CaretPosition = ed2.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        view.EnsureTodayDivider();
        ed2.CaretPosition.InsertTextInRun("Called the ISP.");
        Pump(50);
        var dividers = ed2.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<DateDivider>().ToList();
        var last = ed2.Document.Blocks.LastBlock as Paragraph;
        Ok(dividers.Count == 2 && dividers[1].Date == DateOnly.FromDateTime(DateTime.Now) && last is not null && new TextRange(last.ContentStart, last.ContentEnd).Text == "Called the ISP.",
            "a new day's first words go under a new date line");
        Ok(!view.EnsureTodayDivider(), "only one date line per day");
        view.SaveNow();
        var jsaved = store.Load(journal)!;
        Ok(jsaved.Doc.Contains("\"daily\":1") && jsaved.Doc.Contains("\"d\":\"" + DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") + "\""), "the switch and the date lines are saved with the note (so they sync)");
        using (var se2 = new SearchEngine())
            Ok(se2.Search(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"), 10, out _).Any(h => h.Id == journal), "a day's date finds the note in search");
        Gutter_Check(view);
        Shot(border, Path.Combine(outDir, "daily.png"));
        view.ToggleDaily();
        Ok(!view.DailyOn && !DocSerializer.ReadDaily(store.Load(journal)!.Doc) && store.Load(journal)!.Doc.Contains("\"d\":"), "turning it off keeps the existing date lines");
        view.OpenNote(id, []);
        Pump(100);

        // Moving a note into another folder.
        var groceries = store.Notes(null).First(n => n.Title == "Groceries").Id;
        store.MoveNote(groceries, work.Id);
        view.BuildTree();
        roots = tree.Items.Cast<SideNode>().ToList();
        Ok(roots.First(r => r.Id == work.Id).Children.Any(c => c.Name == "Groceries") && !roots.Any(r => r.Name == "Groceries"), "a moved note shows in its new folder");

        view.SaveLayout();
        src.Dispose();

        // Each mode keeps its own spot: writing mode goes to where it was last, normal mode comes back to its own.
        File.WriteAllText(Path.Combine(AppPaths.DataDir, "windows.json"),
            """{"main":{"X":100,"Y":80,"Width":1200,"Height":800,"Maximized":false},"main-focus":{"X":1500,"Y":120,"Width":360,"Height":520,"Maximized":false}}""");
        settings.Focus = false;
        var pw = new MainWindow(store, settings);
        Ok(pw.Left == 100 && pw.Width == 1200, "opens at normal mode's spot");
        pw.View.ToggleFocus();
        Ok(pw.Left == 1500 && pw.Top == 120 && pw.Width == 360 && pw.Height == 520 && pw.MinWidth == 300, $"writing mode goes to its own spot ({pw.Left},{pw.Top} {pw.Width}x{pw.Height})");
        pw.View.ToggleFocus();
        Ok(pw.Left == 100 && pw.Top == 80 && pw.Width == 1200 && pw.Height == 800 && pw.MinWidth == 560, $"leaving it goes back to normal mode's spot ({pw.Left},{pw.Top} {pw.Width}x{pw.Height})");
        pw.Sync.Dispose();
        pw.Close();
        settings.Focus = true;
        var pw2 = new MainWindow(store, settings);
        Ok(pw2.Left == 1500 && pw2.Width == 360, "closed in writing mode, it opens at writing mode's spot");
        pw2.Sync.Dispose();
        pw2.Close();
        settings.Focus = false;
        File.Delete(Path.Combine(AppPaths.DataDir, "windows.json"));

        // The real window (title bar and all), never shown: its content is drawn off screen, in both themes.
        foreach (var theme in new[] { "light", "dark" })
        {
            App.SetTheme(theme);
            settings.Theme = theme;
            var win = new MainWindow(store, settings);
            var frame = (FrameworkElement)win.Content;
            win.Content = null;
            var host = new Border { Child = frame };
            host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
            using var s2 = Host(host, 1400, 860);
            Pump(400);
            win.View.Gutter.Reset();
            Pump(200);
            Shot(host, Path.Combine(outDir, $"window-{theme}.png"));
            if (theme == "dark")
            {
                win.View.ToggleFocus();
                Pump(300);
                Ok(win.View.FocusMode && ((FrameworkElement)win.View.FindName("Sidebar")).Visibility != Visibility.Visible && ((FrameworkElement)win.View.FindName("ToolbarBar")).Visibility != Visibility.Visible, "writing mode hides the sidebar and toolbar");
                Shot(host, Path.Combine(outDir, "window-focus.png"));
                // As small as writing mode allows: the title bar keeps search, writing mode and the window buttons.
                win.Width = 320;
                win.FitTitleBar();
                host.Child = null;
                var tiny = new Border { Child = frame };
                tiny.SetResourceReference(Border.BackgroundProperty, "WindowBg");
                using (Host(tiny, 320, 240))
                {
                    Pump(200);
                    win.FitTitleBar();   // as a real resize does, with the bar on screen
                    Pump(200);
                    Ok(win.MinWidth <= 300 && win.MinHeight <= 150 && win.View.SearchBar.Width >= 96, $"writing mode goes down to {win.MinWidth} x {win.MinHeight} (search box {win.View.SearchBar.Width} px, drawn {win.View.SearchBar.ActualWidth:0} px, bar {((FrameworkElement)win.View.SearchBar.Parent).ActualWidth:0})");
                    Shot(tiny, Path.Combine(outDir, "window-tiny.png"));
                    var sbx = win.View.SearchBar.TranslatePoint(new Point(0, 0), tiny);
                    var barGrid = (Grid)win.View.SearchBar.Parent;
                    Report.AppendLine("     (tiny cols: " + string.Join(", ", barGrid.ColumnDefinitions.Select(c => c.ActualWidth.ToString("0"))) + "; right: " + string.Join(", ", ((Panel)barGrid.Children[^1]).Children.Cast<FrameworkElement>().Select(e => e.GetType().Name + " " + e.ActualWidth.ToString("0") + " " + e.Visibility + (e is Panel p2 ? "[" + string.Join(" ", p2.Children.Cast<FrameworkElement>().Select(k => k.ActualWidth.ToString("0") + k.Visibility.ToString()[0])) + "]" : ""))) + ")");
                    Report.AppendLine($"     (tiny: search at {sbx.X:0},{sbx.Y:0} size {win.View.SearchBar.ActualWidth:0}x{win.View.SearchBar.ActualHeight:0}, vis {win.View.SearchBar.IsVisible}, box {win.View.SearchBox.ActualWidth:0}x{win.View.SearchBox.ActualHeight:0})");
                }
                tiny.Child = null;
                host.Child = frame;
                win.Width = 1320;
                win.FitTitleBar();
                win.View.ToggleFocus();
                win.View.ToggleSidebar();
                Pump(200);
                Ok(((FrameworkElement)win.View.FindName("Sidebar")).Visibility != Visibility.Visible && ((FrameworkElement)win.View.FindName("ToolbarBar")).Visibility == Visibility.Visible, "the sidebar hides on its own");
                win.View.ToggleSidebar();
            }
            win.Sync.Dispose();
            win.Close();
        }
    }

    /// <summary>Date lines aren't numbered: line 1 is still the first line of text.</summary>
    private static void Gutter_Check(MainView view)
    {
        view.Gutter.Reset();
        Pump(150);
        var lines = view.Gutter.VisibleLines();
        Ok(lines.Count == 2 && lines[0].Number == 1 && lines[1].Number == 2, $"date lines don't take a line number ({lines.Count} numbered)");
    }

    private static IEnumerable<T> FindAll<T>(FlowDocument doc) where T : DependencyObject
    {
        for (var p = doc.ContentStart; p is not null && p.CompareTo(doc.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Child: T t }) yield return t;
    }

    private static void Markup(string outDir)
    {
        using var store = new NoteStore();
        var images = new ImageStore(store.Connection);
        var id = images.Import(TestPicture(800, 500));
        var marks = new List<Mark>
        {
            new() { Kind = "arrow", X1 = 120, Y1 = 80, X2 = 520, Y2 = 210, Color = "#FF3B30", Width = 8 },
            new() { Kind = "box", X1 = 80, Y1 = 290, X2 = 330, Y2 = 440, Color = "#FFCC00", Width = 6 },
        };
        var orig = ImageStore.Decode(images.Original(id)!, int.MaxValue);
        var flat = MarkDrawing.Flatten(orig, marks);
        var px = new byte[4];
        flat.CopyPixels(new Int32Rect(320, 145, 1, 1), px, 4, 0);   // on the arrow's shaft
        Ok(px[2] > 200 && px[1] < 120, "arrow drawn into the picture (red where it runs)");
        flat.CopyPixels(new Int32Rect(80, 365, 1, 1), px, 4, 0);    // on the box's left edge
        Ok(px[2] > 200 && px[1] > 160 && px[0] < 100, "box outline drawn (yellow on its edge)");
        images.SaveEdit(id, Mark.ToJson(marks), ImageStore.Encode(flat, "image/png"));
        var info = images.Info(id)!;
        Ok(info.HasFlat && Mark.Parse(info.Marks).Count == 2 && images.Original(id) is not null, "marks saved; original kept for changing them later");
        images.SaveEdit(id, "", null);
        Ok(!images.Info(id)!.HasFlat, "removing all marks goes back to the original");
        Ok(MarkDrawing.Distance(marks[0], new Point(320, 145)) < 5 && MarkDrawing.Distance(marks[1], new Point(200, 200)) > 50, "picking marks by distance");

        // The markup window's content, drawn off screen.
        images.SaveEdit(id, Mark.ToJson(marks), ImageStore.Encode(flat, "image/png"));
        var w = new MarkupWindow(images, id, BrandIcon.Image);
        var content = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Child = content };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        using var src = Host(host, 1000, 700);
        Pump(200);
        Shot(host, Path.Combine(outDir, "markup.png"));
        w.Close();
    }

    private static void Keys()
    {
        var k = SyncKeys.NewKey();
        var text = SyncKeys.Format(k);
        Ok(text.Length == 64 && text.Split('-').Length == 13, "sync key shows as 13 groups of 4");
        Ok(SyncKeys.Parse(text.ToLowerInvariant().Replace("-", " "))!.SequenceEqual(k), "sync key reads back (any case, spaces)");
        Ok(SyncKeys.Parse("nope") is null, "a bad key is refused");
        var keys = new SyncKeys(k);
        var data = keys.Encrypt("n_abc", Encoding.UTF8.GetBytes("secret note"));
        Ok(Encoding.UTF8.GetString(keys.Decrypt("n_abc", data)!) == "secret note", "records encrypt and decrypt");
        Ok(keys.Decrypt("n_other", data) is null, "a record can't be passed off as another");
        Ok(new SyncKeys(SyncKeys.NewKey()).Decrypt("n_abc", data) is null, "another key can't read it");
        var blob = Encoding.UTF8.GetBytes("picture bytes");
        var name = keys.BlobName(blob);
        Ok(keys.DecryptBlob(name, keys.EncryptBlob(name, blob))!.SequenceEqual(blob), "images encrypt and check their name");
    }

    // ── two PCs syncing through a local copy of the pxdtek.com server ──

    private static async Task SyncTest(string outDir)
    {
        var server = Environment.GetEnvironmentVariable("PXDNOTEPAD_TEST_SERVER");
        var invite = Environment.GetEnvironmentVariable("PXDNOTEPAD_TEST_INVITE");
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(invite))
        {
            Report.AppendLine("skip sync (set PXDNOTEPAD_TEST_SERVER and PXDNOTEPAD_TEST_INVITE)");
            return;
        }
        var key = SyncKeys.NewKey();
        Ok(await SyncService.CreateVaultAsync(server, key, "WRONG") is not null, "sync: a wrong invite is refused");
        Ok(await SyncService.CreateVaultAsync(server, key, invite) is null, "sync: vault created with the invite");
        Ok(await SyncService.CheckVaultAsync(server, key) is null, "sync: the key finds its vault");

        var dbA = Path.Combine(AppPaths.DataDir, "a.db");
        var dbB = Path.Combine(AppPaths.DataDir, "b.db");
        using var a = new NoteStore(dbA);
        using var b = new NoteStore(dbB);
        var imgA = new ImageStore(a.Connection);
        var cat = a.AddCategory("Synced", null);
        var picture = imgA.Import(TestPicture(640, 400));
        var nid = a.NewNote(cat.Id, "From PC A", $$"""{"v":1,"b":[{"p":[{"t":"hello from A"},{"img":"{{picture}}","w":300}]}]}""", "hello from A");

        var syncA = new SyncService(dbA, () => (server, key));
        var syncB = new SyncService(dbB, () => (server, key));
        var changesB = new List<SyncChanges>();
        syncB.RemoteChanged += c => changesB.Add(c);
        var st = await syncA.SyncNowAsync();
        Ok(st.State == SyncState.Ok, $"sync: PC A sends ({st.Message})");
        st = await syncB.SyncNowAsync();
        Ok(st.State == SyncState.Ok, $"sync: PC B receives ({st.Message})");
        var got = b.Load(nid);
        Ok(got?.Title == "From PC A" && got.Cat == cat.Id && b.Categories().Any(c => c.Name == "Synced"), "sync: note and category arrived");
        var imgB = new ImageStore(b.Connection);
        Ok(imgB.Original(picture)?.Length == imgA.Original(picture)?.Length && imgB.Info(picture)?.Width == 640, "sync: the picture arrived whole");
        Ok(changesB.Any(c => c.Notes.Contains(nid)), "sync: PC B was told what changed");

        b.Save(nid, "Edited on B", got!.Doc, "changed by B");
        await syncB.SyncNowAsync();
        await syncA.SyncNowAsync();
        Ok(a.Load(nid)?.Title == "Edited on B", "sync: an edit on B reaches A");

        // Both edit the same note; the later edit wins on both.
        a.Save(nid, "A's later edit?", got.Doc, "a");
        Thread.Sleep(5);
        b.Save(nid, "B's latest edit", got.Doc, "b");
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        await syncA.SyncNowAsync();
        Ok(a.Load(nid)?.Title == "B's latest edit" && b.Load(nid)?.Title == "B's latest edit", "sync: same note edited on both: the newer edit wins on both");

        // Mark the picture up on A: B gets the new version.
        imgA.SaveEdit(picture, Mark.ToJson([new Mark { Kind = "box", X1 = 10, Y1 = 10, X2 = 200, Y2 = 100 }]), TestPicture(640, 400));
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        Ok(new ImageStore(b.Connection).Info(picture) is { HasFlat: true } i && Mark.Parse(i.Marks).Count == 1, "sync: picture mark-up reaches B");

        var tagsA = new TagStore(a.Connection);
        var star = tagsA.Add("Starred", "star", "#FFCC00", null);
        tagsA.Toggle(nid, star.Id);
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        var tagsB = new TagStore(b.Connection);
        Ok(tagsB.All().Any(t => t.Name == "Starred") && tagsB.TagsOf(nid).SequenceEqual([star.Id]), "sync: tags and which notes have them reach B");
        using (var seB = new SearchEngine(dbB))
            Ok(seB.Search("starred", 5, out _).Count == 1, "sync: B finds the note by its tag's name");

        // A tag made from the user's own icon file: the picture itself travels (encrypted) and arrives byte for byte.
        var icoPath = Path.Combine(AppPaths.DataDir, "custom.ico");
        BrandIcon.WriteIco(icoPath);
        var customPng = TagIcons.ImportFile(icoPath);
        var customTag = tagsA.Add("Splashtop", "", "", customPng);
        tagsA.Toggle(nid, customTag.Id);
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        var gotTag = tagsB.All().FirstOrDefault(t => t.Id == customTag.Id);
        Ok(gotTag?.Png is { } gotPng && gotPng.SequenceEqual(customPng) && tagsB.TagsOf(nid).Contains(customTag.Id), $"sync: a custom-icon tag reaches B with its picture ({customPng.Length} bytes)");
        tagsA.Update(customTag.Id, "Remote", "", "", customPng);
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        Ok(tagsB.All().Any(t => t.Id == customTag.Id && t.Name == "Remote"), "sync: renaming a custom tag reaches B");
        var bTag = tagsB.Add("Made on B", "flag", "#34C759", null);
        await syncB.SyncNowAsync();
        await syncA.SyncNowAsync();
        Ok(tagsA.All().Any(t => t.Id == bTag.Id), "sync: a tag made on B reaches A");

        a.Trash(nid, true);
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        Ok(b.Load(nid)?.Trashed == true, "sync: trashing reaches B");

        // Search on B finds what came from A.
        using var se = new SearchEngine(dbB);
        b.NewNote(null, "local", "", "zebra crossing");
        Ok(se.Search("zebra", 5, out _).Count == 1, "sync: B's search still works");
        syncA.Dispose();
        syncB.Dispose();
    }
}
