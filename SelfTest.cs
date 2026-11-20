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
            LinkFinding();
            TextFiles();
            Registration();
            Handoff();
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
        var usage = s.StorageUsage();
        Ok(usage.Notes == 2 && usage.Folders == 2 && usage.FileBytes > 0, $"storage: counts and file size ({NoteStore.Size(usage.FileBytes)}, {usage.Notes} notes, {usage.Folders} folders)");
        Ok(NoteStore.Size(512) == "512 bytes" && NoteStore.Size(965318) == "943 KB" && NoteStore.Size(3 * 1048576 + 400000) == "3.4 MB", "sizes read naturally");
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
                CREATE TABLE categories(id TEXT PRIMARY KEY, parent TEXT, name TEXT NOT NULL, sort REAL NOT NULL DEFAULT 0, updated INTEGER NOT NULL,
                  deleted INTEGER NOT NULL DEFAULT 0, dirty INTEGER NOT NULL DEFAULT 1, ver INTEGER NOT NULL DEFAULT 0, srv INTEGER NOT NULL DEFAULT 0);
                INSERT INTO categories(id,name,updated) VALUES('fedcba9876543210fedcba9876543210','Old folder',1);
                """);
        }
        using (var s = new NoteStore(path))
        {
            Ok(Db.Scalar(s.Connection, "SELECT tags FROM notes") is string && s.Categories().Single().Name == "Old folder" && Db.Scalar(s.Connection, "SELECT tags FROM categories") is string, "an older notes file gets the tag columns (notes and folders)");
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

    /// <summary>Lets the UI run for a while. Ends on time even if the UI never goes idle (so a redraw loop shows up as a
    /// failed check, not a hung test).</summary>
    private static void Pump(int ms = 0)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Max(ms, 40)), DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { if (ms <= 0) frame.Continue = false; });
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
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
        Ok(lines.Count > 0 && lines[0].Number == 1 && Math.Abs(lines[0].Size - 32) < 0.1 && Math.Abs(view.Gutter.NumberSize(lines[0]) - 15) < 0.1, "line 1 is the 32 pt heading; its number stays the normal 15 pt");
        view.Gutter.MatchTextSize = true;
        Ok(Math.Abs(view.Gutter.NumberSize(lines[0]) - 32) < 0.1, "with \"match text size\" on, it gets a 32 pt number");
        view.Gutter.MatchTextSize = false;
        var small = lines.FirstOrDefault(l => Math.Abs(l.Size - 12) < 0.1);
        Ok(small.Number > 0 && Math.Abs(view.Gutter.NumberSize(small) - 15) < 0.1, "the 12 pt line's number is the normal size too");
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

        // A big folder: the list makes only the rows in view, and a note far down still gets selected and shown.
        {
            static List<TreeViewItem> Rows(DependencyObject d, List<TreeViewItem>? into = null)
            {
                into ??= [];
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is TreeViewItem t) into.Add(t);
                    Rows(c, into);
                }
                return into;
            }
            var bigCat = store.AddCategory("Big folder", null);
            var bigIds = new List<string>();
            for (var k = 0; k < 120; k++) bigIds.Add(store.NewNote(bigCat.Id, $"Big {k:000}", "", "x"));
            var openBefore = view.OpenNoteId;
            view.BuildTree();
            Pump(250);
            var made = Rows(tree).Count;
            Ok(made < 100, $"a folder of 120 notes: only the rows in view are made ({made} rows)");
            view.OpenNote(bigIds[^1], []);
            Pump(300);
            var far = Rows(tree).FirstOrDefault(t => t.DataContext is SideNode { Id: var rid } && rid == bigIds[^1]);
            Ok(far is { IsSelected: true } && tree.SelectedItem is SideNode sn && sn.Id == bigIds[^1], "the last of them, opened from elsewhere, is selected and scrolled to in the list");
            var bigNode = ((SideNode)far!.DataContext).Parent!.Children[5];
            bigNode.IsSelected = true;
            Pump(150);
            Ok(view.OpenNoteId == bigNode.Id && !((SideNode)far.DataContext).IsSelected, "picking another row opens it (one row selected at a time)");
            foreach (var bid in bigIds) store.Purge(bid);
            store.DeleteCategory(bigCat.Id);
            if (openBefore is not null) view.OpenNote(openBefore, []);
            view.BuildTree();
            Pump(150);
        }

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

        // Editing the picture (blur, cut out) keeps the size it's shown at in the note; a cut only changes its height.
        {
            Pump(100);
            var mw = new MarkupWindow(images, box.ImageId, BrandIcon.Image);
            var (picW, picH) = mw.PictureSize;
            mw.Layer.Add(new Mark { Kind = "blur", X1 = 10, Y1 = 10, X2 = 200, Y2 = 120 });
            mw.Layer.AddCut(new Mark { Kind = "cut", X1 = 0, Y1 = picH * 0.3, X2 = picW, Y2 = picH * 0.6 });
            var (nw, nh) = mw.PictureSize;
            mw.SaveAndClose();
            view.AfterEdit(box, picW);
            Pump(200);
            Ok(Math.Abs(box.ActualWidth - 222) < 1 && Math.Abs(box.ActualHeight - Math.Round(222.0 * nh / nw)) < 1.5,
                $"after an edit the picture stays 222 wide in the note ({box.ActualWidth:0} × {box.ActualHeight:0}, picture now {nw} × {nh})");
            view.SaveNow();
            view.OpenNote(id, []);
            Pump(200);
            var again = FindAll<ImageBox>(view.Editor.Document).First(b => b.ImageId == box.ImageId);
            Ok(Math.Abs(again.ActualWidth - 222) < 1 && Math.Abs(again.ActualHeight - Math.Round(222.0 * nh / nw)) < 1.5, $"and after opening the note again ({again.ActualWidth:0} × {again.ActualHeight:0})");
            box = again;
            // a band cut out down the picture: it gets narrower in the note by the same share
            var mw2 = new MarkupWindow(view.ImagesStore, box.ImageId, BrandIcon.Image);
            var (w2, h2) = mw2.PictureSize;
            mw2.Layer.AddCut(new Mark { Kind = "cut", X1 = w2 * 0.25, Y1 = 0, X2 = w2 * 0.5, Y2 = h2 });
            var (w3, h3) = mw2.PictureSize;
            mw2.SaveAndClose();
            view.AfterEdit(box, w2);
            Pump(200);
            Ok(Math.Abs(box.ActualWidth - Math.Round(222.0 * w3 / w2)) < 1 && Math.Abs(box.ActualHeight - Math.Round(box.ActualWidth * h3 / w3)) < 1.5,
                $"a cut down the picture makes it narrower in the note too ({box.ActualWidth:0} × {box.ActualHeight:0}, picture {w3} × {h3})");
        }

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
        // Folders can have tags too: shown on the folder's row, and searching the tag lists the folder.
        Ok(tags.ToggleFolder(proj.Id, fav.Id), "a tag put on a folder");
        view.BuildTree();
        Pump(100);
        var projRow = tree.Items.Cast<SideNode>().SelectMany(r => r.Walk()).First(x => x.Id == proj.Id);
        Ok(projRow.TagIcons.Select(t => t.Name).SequenceEqual(["Keepers"]), "the folder's row shows the tag icon");
        view.SearchBox.Text = "keepers";
        Pump(400);
        var firstResult = view.Results.Items.Count > 0 ? (NoteItem)view.Results.Items[0] : null;
        Ok(firstResult is { IsFolder: true } && firstResult.Id == proj.Id && view.Results.Items.Cast<NoteItem>().Count(i => !i.IsFolder) == 1, $"searching the tag lists the folder first, then its tagged notes ({view.Results.Items.Count} results)");
        view.SearchBox.Text = "projects";
        Pump(400);
        Ok(view.Results.Items.Cast<NoteItem>().Any(i => i.IsFolder && i.Id == proj.Id), "a folder's name finds it too");
        view.SearchBox.Text = "";
        Pump(150);
        Ok(tags.CountNotes(fav.Id) == 2, "the tag manager counts notes and folders");
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

        // Links in a note: underlined where they show, found at their spot for a click.
        var contacts = store.NewNote(null, "Contacts", """{"v":1,"b":[{"p":[{"t":"Office: (555) 123-4567 x204"}]},{"p":[{"t":"Billing: "},{"t":"billing@example.com","w":700},{"t":" · portal www.example.com"}]}]}""", "");
        view.OpenNote(contacts, []);
        Pump(250);
        view.Links.InvalidateVisual();
        Pump(150);
        var live = view.Links.Links;
        Ok(live.Count == 3 && live.All(l => l.Boxes.Count == 1 && l.Boxes[0].Width > 20), $"links in the editor are underlined ({live.Count}: {string.Join(", ", live.Select(l => l.Link.Text))})");
        var phoneBox = live.FirstOrDefault(l => l.Link.Kind == LinkKind.Phone)?.Boxes.FirstOrDefault() ?? Rect.Empty;
        Ok(!phoneBox.IsEmpty && view.Links.At(new Point(phoneBox.Left + 5, phoneBox.Top + phoneBox.Height / 2))?.Link.Kind == LinkKind.Phone && view.Links.At(new Point(phoneBox.Left - 30, phoneBox.Top + 3)) is null, "a click on the number finds it, beside it doesn't");
        Shot(border, Path.Combine(outDir, "links.png"));

        // Blur: the password shows smeared, copies as itself, and never shows in search or previews.
        var secret = store.NewNote(null, "Wi-Fi", """{"v":1,"b":[{"p":[{"t":"Guest network password: Hunter2-Zebra! (call 555-123-4567)"}]}]}""", "");
        view.OpenNote(secret, []);
        Pump(200);
        var ed3 = view.Editor;
        var para3 = (Paragraph)ed3.Document.Blocks.FirstBlock;
        var run3 = (Run)para3.Inlines.FirstInline;
        var at = run3.Text.IndexOf("Hunter2", StringComparison.Ordinal);
        var selStart = run3.ContentStart.GetPositionAtOffset(at)!;
        ed3.Selection.Select(selStart, selStart.GetPositionAtOffset("Hunter2-Zebra!".Length)!);
        var phoneAt = run3.Text.IndexOf("555", StringComparison.Ordinal);
        var menuHeaders = view.EditMenu().Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Ok(view.Editor.ContextMenu is not null && menuHeaders.Contains("Blur") && menuHeaders.Contains("Copy"), "right-clicking selected text offers Blur (the editor uses our menu: " + string.Join(", ", menuHeaders) + ")");
        view.SetBlur(true);
        Ok(ed3.Selection.Text == "Hunter2-Zebra!", "blurred text is still the real text (what copying takes)");
        view.SaveNow();
        var blurredSaved = store.Load(secret)!;
        Ok(blurredSaved.Doc.Contains("\"x\":1") && blurredSaved.Body.Contains(DocSerializer.Masked) && !blurredSaved.Body.Contains("Hunter2"), "saved as blurred; previews and search get •••••• instead");
        using (var se3 = new SearchEngine())
            Ok(se3.Search("Hunter2", 10, out _).Count == 0 && se3.Search("guest network", 10, out _).Count == 1, "searching the password doesn't find it (the rest of the note still does)");
        view.OpenNote(id, []);
        view.OpenNote(secret, []);
        Pump(250);
        view.Blur.InvalidateVisual();
        Pump(150);
        Ok(view.Blur.Boxes.Count == 1 && view.Blur.Boxes[0].Width > 40, $"still blurred after opening it again ({view.Blur.Boxes.Count} box)");
        var bb = view.Blur.Boxes[0];
        var secretRun = view.Blur.At(new Point(bb.Left + bb.Width / 2, bb.Top + bb.Height / 2));
        Ok(secretRun is not null && view.Blur.At(new Point(bb.Right + 60, bb.Top + 2)) is null, "a click on blurred text finds it (and only there)");
        view.Blur.Hover = secretRun;
        Pump(100);
        Shot(border, Path.Combine(outDir, "blur-hover.png"));
        view.Blur.Hover = null;
        view.RevealOrHide(secretRun!);
        Pump(120);
        Ok(view.Blur.IsRevealed(secretRun!) && DocSerializer.IsBlurred(secretRun!), "clicking shows it for the moment; it's still blurred in the note");
        Shot(border, Path.Combine(outDir, "blur-revealed.png"));
        view.RevealOrHide(secretRun!);
        Ok(!view.Blur.IsRevealed(secretRun!), "clicking again blurs it");
        view.RevealOrHide(secretRun!);
        view.OpenNote(id, []);
        view.OpenNote(secret, []);
        Pump(150);
        Ok(!view.Blur.AnyRevealed, "opening another note blurs everything again");
        Shot(border, Path.Combine(outDir, "blur.png"));
        view.Blur.Outline = false;
        view.Blur.InvalidateVisual();
        Pump(120);
        Shot(border, Path.Combine(outDir, "blur-no-outline.png"));
        view.Blur.Outline = true;
        // A blurred phone number isn't a link (it would give itself away).
        var run4 = ((Paragraph)view.Editor.Document.Blocks.FirstBlock).Inlines.OfType<Run>().First(r => r.Text.Contains("555"));
        var ph = run4.ContentStart.GetPositionAtOffset(run4.Text.IndexOf("555", StringComparison.Ordinal))!;
        view.Editor.Selection.Select(ph, ph.GetPositionAtOffset(12)!);
        view.SetBlur(true);
        Pump(150);
        view.Links.InvalidateVisual();
        Pump(150);
        Ok(view.Links.Links.Count == 0, "a blurred phone number isn't underlined or clickable");
        view.Editor.Selection.Select(view.Editor.Document.ContentStart, view.Editor.Document.ContentEnd);
        view.SetBlur(false);
        view.SaveNow();
        Ok(!store.Load(secret)!.Doc.Contains("\"x\":1") && store.Load(secret)!.Body.Contains("Hunter2"), "unblur puts it back to plain text");
        view.OpenNote(id, []);
        Pump(100);

        // Big text with blur and line numbers: everything settles (no endless redrawing).
        view.OpenNote(secret, []);
        Pump(150);
        var r5 = (Run)((Paragraph)view.Editor.Document.Blocks.FirstBlock).Inlines.FirstInline;
        var at5 = r5.ContentStart.GetPositionAtOffset(r5.Text.IndexOf("Hunter2", StringComparison.Ordinal))!;
        view.Editor.Selection.Select(at5, at5.GetPositionAtOffset(14)!);
        view.SetBlur(true);
        view.Editor.Selection.Select(view.Editor.Document.ContentStart, view.Editor.Document.ContentEnd);
        view.Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, 48.0);
        for (var k = 0; k < 6; k++) view.Editor.Document.Blocks.Add(new Paragraph(new Run("More words in a big size so the page needs to scroll " + k)));
        Pump(800);
        int g0 = view.Gutter.Renders, b0 = view.Blur.Renders, l0 = view.Links.Renders;
        Pump(1000);
        int dg = view.Gutter.Renders - g0, db = view.Blur.Renders - b0, dl = view.Links.Renders - l0;
        Ok(dg <= 2 && db <= 2 && dl <= 2, $"at 48 pt the page settles: redraws in a quiet second: numbers {dg}, blur {db}, links {dl}");
        Shot(border, Path.Combine(outDir, "blur-48.png"));
        view.Editor.Selection.Select(view.Editor.Document.ContentStart, view.Editor.Document.ContentEnd);
        view.SetBlur(false);
        view.SaveNow();
        view.OpenNote(id, []);
        Pump(100);

        // Spell checking: off while a note is being put up (a new checker per page is slow), back on a moment later.
        view.OpenNote(id, []);
        var spellRightAway = System.Windows.Controls.SpellCheck.GetIsEnabled(view.Editor);
        Pump(1100);
        Ok(!spellRightAway && System.Windows.Controls.SpellCheck.GetIsEnabled(view.Editor) == settings.SpellCheck, $"spell checking comes back on after a note opens (setting {settings.SpellCheck})");

        // Where you left off: a note reopens on its tab, caret and scroll; each tab keeps its own place.
        {
            string LongDoc(string word) => "{\"v\":1,\"b\":[" + string.Join(",", Enumerable.Range(0, 300).Select(k => "{\"p\":[{\"t\":\"" + word + " line " + k + "\"}]}")) + "]}";
            var longA = store.NewNote(null, "Spot A", LongDoc("alpha"), "alpha");
            var longB = store.NewNote(null, "Spot B", LongDoc("bravo"), "bravo");
            view.OpenNote(longA, []);
            Pump(150);
            int CaretOffset() => view.Editor.Document.ContentStart.GetOffsetToPosition(view.Editor.CaretPosition);
            var target = view.Editor.Document.Blocks.ElementAt(200).ContentStart.GetPositionAtOffset(3)!;
            view.Editor.CaretPosition = target;
            view.Editor.ScrollToVerticalOffset(3000);
            Pump(150);
            var wantCaret = CaretOffset();
            var wantScroll = view.Editor.VerticalOffset;
            view.OpenNote(longB, []);
            Pump(150);
            Ok(view.Editor.VerticalOffset < 1 && CaretOffset() <= 2, $"another note opens at its top the first time (scroll {view.Editor.VerticalOffset:0})");
            view.OpenNote(longA, []);
            Pump(200);
            Ok(CaretOffset() == wantCaret && Math.Abs(view.Editor.VerticalOffset - wantScroll) < 2 && wantScroll > 1000,
                $"back on the first note: caret and scroll where they were (caret {CaretOffset()} vs {wantCaret}, scroll {view.Editor.VerticalOffset:0} vs {wantScroll:0})");
            // tabs: each keeps its own place
            view.AddTab();
            Pump(100);
            view.Editor.CaretPosition.InsertTextInRun(string.Join(" ", Enumerable.Repeat("tab two words", 400)));
            view.Editor.CaretPosition = view.Editor.Document.ContentStart.GetPositionAtOffset(40)!;
            Pump(100);
            var tab2Caret = CaretOffset();
            view.SwitchTab(0);
            Pump(200);
            Ok(CaretOffset() == wantCaret && Math.Abs(view.Editor.VerticalOffset - wantScroll) < 2, $"switching back to tab 1: its caret and scroll come back (scroll {view.Editor.VerticalOffset:0})");
            view.SwitchTab(1);
            Pump(200);
            Ok(CaretOffset() == tab2Caret, $"and tab 2 keeps its own caret ({CaretOffset()} vs {tab2Caret})");
            view.OpenNote(longB, []);
            Pump(100);
            view.OpenNote(longA, []);
            Pump(200);
            Ok(view.ActiveTab == 1 && CaretOffset() == tab2Caret, $"a note reopens on the tab it was left on (tab {view.ActiveTab + 1})");
            view.SaveLayout();
            Ok(AppSettings.Load().Spots.ContainsKey(longA), "the places are kept in settings for the next start");
            view.OpenNote(id, []);
            store.Purge(longA);
            store.Purge(longB);
            Pump(100);
        }

        // The layers share one map of the note, walked again only when its shape changes (not for every key).
        {
            var mapNote = store.NewNote(null, "Map", "", "");
            view.OpenNote(mapNote, []);
            Pump(80);
            view.Editor.Document.Blocks.Clear();
            for (var k = 0; k < 5; k++) view.Editor.Document.Blocks.Add(new Paragraph(new Run("line " + k)));
            Pump(150);
            var lines0 = view.Index.LineStarts.Count;
            var builds0 = view.Index.Builds;
            var endOf = (Run)((Paragraph)view.Editor.Document.Blocks.LastBlock).Inlines.LastInline;
            for (var k = 0; k < 5; k++) endOf.ContentEnd.InsertTextInRun("x");
            Pump(150);
            Ok(view.Index.Builds == builds0 && lines0 == 5, $"typing doesn't walk the note again ({view.Index.Builds - builds0} walks for 5 keys)");
            endOf.ContentEnd.InsertParagraphBreak();
            Pump(150);
            Ok(view.Index.LineStarts.Count == 6 && view.Gutter.VisibleLines().Count == 6, $"Enter: the new line gets its number ({view.Index.LineStarts.Count} lines)");
            var p5 = (Paragraph)view.Editor.Document.Blocks.LastBlock;
            new TextRange(((Paragraph)p5.PreviousBlock).ContentEnd, p5.ContentStart).Text = "";
            Pump(150);
            Ok(view.Index.LineStarts.Count == 5, $"Backspace joining two lines: one number fewer ({view.Index.LineStarts.Count})");
            // a single character highlighted as blur from anywhere (not through SetBlur): the map still notices
            var one = (Run)((Paragraph)view.Editor.Document.Blocks.FirstBlock).Inlines.FirstInline;
            Ok(!view.Index.AnyBlur, "nothing blurred yet");
            new TextRange(one.ContentStart, one.ContentStart.GetPositionAtOffset(1)).ApplyPropertyValue(TextElement.BackgroundProperty, DocSerializer.BlurBrush);
            Pump(150);
            Ok(view.Index.AnyBlur && view.Blur.Boxes.Count == 1, $"one character blurred by other means is still drawn blurred ({view.Blur.Boxes.Count} box)");
            view.Editor.Undo();
            Pump(150);
            Ok(!view.Index.AnyBlur && view.Blur.Boxes.Count == 0, $"and Undo takes the blur away again ({view.Blur.Boxes.Count} boxes)");
            view.OpenNote(id, []);
            store.Purge(mapNote);
            Pump(100);
        }

        // The call card: "Do you want to call …?", then a red hang-up and a green call button.
        var called = false;
        var callCard = Dialogs.CallCard("(555) 123-4567", () => called = true, () => { }, out var callB, out var hangB);
        var callHost = new Border { Child = callCard };
        callHost.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        callHost.SetResourceReference(TextElement.ForegroundProperty, "Text");
        using (Host(callHost, 380, 240)) { Pump(200); Shot(callHost, Path.Combine(outDir, "call.png")); }
        hangB.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Ok(!called, "the hang-up button doesn't call");
        callB.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Ok(called, "the green button calls");

        // New notes date each day's entries unless that's turned off in Settings.
        settings.DailyByDefault = true;
        var fresh = store.NewNote(null, "", """{"v":1,"daily":1,"b":[]}""", "");
        view.OpenNote(fresh, []);
        Ok(view.DailyOn, "new notes start with date lines on");
        view.OpenNote(id, []);

        // Collapse all / Expand all, remembered right away.
        view.ToggleAllFolders();
        var collapseBtn = (Button)view.FindName("CollapseAllButton");
        Ok(view.AllCollapsed && (string)collapseBtn.ToolTip == "Expand all folders" && settings.Collapsed.Contains(proj.Id), "collapse all closes every folder, saves it, and the button turns into Expand all");
        var reread = AppSettings.Load();
        Ok(reread.Collapsed.Contains(proj.Id) && reread.Collapsed.Contains(work.Id), "the closed folders are saved to disk straight away");
        view.ToggleAllFolders();
        Ok(!view.AllCollapsed && (string)collapseBtn.ToolTip == "Collapse all folders" && !AppSettings.Load().Collapsed.Contains(proj.Id), "expand all opens them again (and the button goes back)");

        // Tabs inside a note.
        var tabbed = store.NewNote(null, "Trip", """{"v":1,"tabs":[{"n":"Plan","b":[{"p":[{"t":"Flights on Friday"}]}]},{"n":"Packing","b":[{"p":[{"t":"Passport, charger, sunscreen"}]}]}]}""", "");
        view.OpenNote(tabbed, []);
        Pump(150);
        var strip = (WrapPanel)view.FindName("TabStrip");
        Ok(view.Tabs.Count == 2 && view.ActiveTab == 0 && strip.Children.Count == 3 && strip.Visibility == Visibility.Visible, "a note with two tabs shows both (plus the + button)");
        view.SwitchTab(1);
        Pump(80);
        Ok(new TextRange(view.Editor.Document.ContentStart, view.Editor.Document.ContentEnd).Text.Contains("sunscreen"), "switching shows the other tab's page");
        view.AddTab();
        view.Editor.CaretPosition.InsertTextInRun("Hotel: the one by the beach");
        view.MoveTab(2, -1);
        Ok(view.Tabs.Count == 3 && view.Tabs[1].Name == "Tab 3" && view.ActiveTab == 1, "a new tab goes after the current one; tabs move");
        view.SaveNow();
        var savedTabs = store.Load(tabbed)!;
        Ok(savedTabs.Doc.Contains("\"tabs\"") && savedTabs.Body.Contains("sunscreen") && savedTabs.Body.Contains("beach") && !savedTabs.Body.Contains("Packing") && !savedTabs.Body.Contains("Tab 3"), "every tab's text is saved and searchable; tab names aren't");
        view.OpenNote(id, []);
        Pump(80);
        view.OpenNote(tabbed, ["sunscreen"]);
        Pump(200);
        Ok(view.ActiveTab == 2 && view.Editor.Selection.Text.Equals("sunscreen", StringComparison.OrdinalIgnoreCase), $"opening a search result goes to the tab with the match (tab {view.ActiveTab + 1})");
        using (var se4 = new SearchEngine())
        {
            var hit = se4.Search("sunscreen", 5, out _).FirstOrDefault(h => h.Id == tabbed);
            Ok(hit is not null && hit.Tab == "Packing" && hit.Snippet == "Passport, charger, sunscreen" && se4.Search("Packing", 5, out _).All(h => h.Id != tabbed), $"a result says which tab it was found in and shows only that tab's words; a tab's name alone doesn't find the note ({hit?.Tab}: \"{hit?.Snippet}\")");
            var friday = se4.Search("friday", 5, out _).FirstOrDefault(h => h.Id == tabbed);
            Ok(friday?.Tab == "Plan", $"a word in the first tab names that tab ({friday?.Tab})");
            var emptyFirst = store.NewNote(null, "Empty first", """{"v":1,"tabs":[{"n":"Blank","b":[{"p":[]}]},{"n":"Second","b":[{"p":[{"t":"quokka sightings"}]}]}]}""", "");
            view.OpenNote(emptyFirst, []);
            view.Editor.CaretPosition.InsertTextInRun("");
            view.SwitchTab(1);
            view.Editor.CaretPosition = view.Editor.Document.ContentEnd;
            view.Editor.CaretPosition.InsertTextInRun(" today");
            view.SaveNow();
            var quokka = se4.Search("quokka", 5, out _).FirstOrDefault(h => h.Id == emptyFirst);
            Ok(quokka?.Tab == "Second", $"with an empty first tab, a match in the second still names the second ({quokka?.Tab})");
            view.OpenNote(tabbed, ["sunscreen"]);
            var plainHit = se4.Search("drivers", 5, out _).FirstOrDefault();
            Ok(plainHit is not null && plainHit.Tab == "", "a note without tabs shows no tab name");
        }
        // A 1.7.0–1.7.2 note (tab names in its search text) gets fixed at start.
        Db.Exec(store.Connection, "UPDATE notes SET body=$b WHERE id=$id", ("$id", tabbed), ("$b", "Plan\nFlights on Friday\n\nPacking\nPassport, charger, sunscreen"));
        Db.SetMeta(store.Connection, "fix.tabsearch3", null);
        _ = new MainView(store, settings);
        Ok(!store.Load(tabbed)!.Body.Contains("Packing") && store.Load(tabbed)!.Body.Contains(SearchEngine.TabMark), "older tabbed notes get their search text fixed on start");
        {
        }
        Shot(border, Path.Combine(outDir, "tabs.png"));
        view.DeleteTab(1, ask: false);
        Ok(view.Tabs.Count == 2, "deleting a tab");
        var oneTab = store.NewNote(null, "One", "", "");
        view.OpenNote(oneTab, []);
        Pump(80);
        Ok(strip.Children.Count == 1, "a one-tab note shows just + Tab");
        view.AddTab();
        view.DeleteTab(1, ask: false);
        view.SaveNow();
        Ok(!store.Load(oneTab)!.Doc.Contains("\"tabs\""), "back to one tab, it's saved the plain way again");
        view.OpenNote(id, []);
        Pump(80);

        // Custom colours: any colour for a tag, from the picker.
        var mauve = tags.Add("Mauve", "bookmark", "#7B61FF", null);
        Ok(TagIcons.For(mauve) is not null && !TagIcons.Colors.Contains("#7B61FF"), "a tag can have a colour that isn't in the presets");
        var (hh, ss, vv) = ColorPicker.ToHsv(Color.FromRgb(0x7B, 0x61, 0xFF));
        var back = ColorPicker.Hsv(hh, ss, vv);
        Ok(back.R == 0x7B && back.G == 0x61 && back.B == 0xFF && ColorPicker.TryHex("#7b61ff", out var hx) && hx.B == 0xFF && ColorPicker.TryHex("f80", out var short3) && short3.R == 0xFF && !ColorPicker.TryHex("nope", out _), "the picker's colour maths and hex box");
        var picker = new ColorPicker(Color.FromRgb(0x7B, 0x61, 0xFF));
        var pickHost = new Border { Child = picker, Padding = new Thickness(10) };
        pickHost.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        using (Host(pickHost, 280, 290)) { Pump(150); Shot(pickHost, Path.Combine(outDir, "picker.png")); }

        // A .txt opened from Explorer: edited in place, saved back as plain text, copied into notes on request.
        var txtPath = Path.Combine(AppPaths.DataDir, "todo.txt");
        File.WriteAllText(txtPath, "milk\r\neggs\r\n", new UTF8Encoding(false));
        var notesBefore = store.Notes(null).Count;
        view.OpenFile(txtPath);
        Pump(150);
        Ok(view.OpenFilePath == txtPath && view.TitleBox.Text == "todo.txt" && ((FrameworkElement)view.FindName("ToolbarBar")).Visibility != Visibility.Visible, "a .txt opens in the editor (no formatting bar: it's plain text)");
        view.Editor.CaretPosition = view.Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        view.Editor.CaretPosition.InsertTextInRun(" and bread");
        view.SaveNow();
        Ok(File.ReadAllText(txtPath) == "milk\r\neggs and bread\r\n" && store.Notes(null).Count == notesBefore, "saving writes the file (same line endings), not a note");
        Shot(border, Path.Combine(outDir, "textfile.png"));
        view.SaveFileToNotes();
        Ok(store.Notes(null).Count == notesBefore + 1 && view.OpenFilePath is null && store.Notes(null).Any(n => n.Title == "todo" && n.Preview.Contains("bread")), "Save to my notes makes a note of it");
        view.OpenNote(id, []);
        Pump(80);

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
        pw.TogglePin();
        Ok(pw.Topmost && settings.PinOnTop, "the pin keeps writing mode on top");
        pw.View.ToggleFocus();
        Ok(!pw.Topmost, "normal mode is never kept on top");
        pw.View.ToggleFocus();
        Ok(pw.Topmost, "back in writing mode, it's on top again (the pin is remembered)");
        pw.TogglePin();
        Ok(!pw.Topmost && !settings.PinOnTop, "unpinning lets it go behind other windows");
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
                // Settings, drawn off screen (Storage and the rest).
                var sw = new SettingsWindow(settings, win.Sync, win.View);
                var swContent = (UIElement)sw.Content;
                sw.Content = null;
                var swHost = new Border { Child = swContent };
                swHost.SetResourceReference(Border.BackgroundProperty, "WindowBg");
                swHost.SetResourceReference(TextElement.ForegroundProperty, "Text");
                using (Host(swHost, 780, 1300))
                {
                    foreach (var page in new[] { "look", "text", "files", "sync" })
                    {
                        sw.ShowPage(page);
                        Pump(200);
                        Shot(swHost, Path.Combine(outDir, $"settings-{page}.png"));
                    }
                    Ok(settings.SettingsPage == "sync", "Settings remembers the page you were on");
                }
                sw.Close();

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

        // Pictures are packed with our own PNG writer: same pixels, smaller than WPF's.
        {
            static byte[] Px(BitmapSource s) { var f = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0); var x = new byte[f.PixelWidth * f.PixelHeight * 4]; f.CopyPixels(x, f.PixelWidth * 4, 0); return x; }
            var pic = ImageStore.Decode(images.Original(id)!, int.MaxValue);
            var ours = Png.Encode(pic);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(pic));
            using var wms = new MemoryStream();
            enc.Save(wms);
            Ok(Px(ImageStore.Decode(ours, int.MaxValue)).AsSpan().SequenceEqual(Px(pic)) && ours.Length < wms.Length, $"PNG: same pixels, {ours.Length / 1024} KB instead of {wms.Length / 1024} KB");
            var seeThrough = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255,0,0,255, 0,255,0,128, 0,0,255,0, 10,20,30,255, 40,50,60,200, 70,80,90,255 }, 12);
            Ok(Px(ImageStore.Decode(Png.Encode(seeThrough), int.MaxValue)).AsSpan().SequenceEqual(Px(seeThrough)), "PNG: see-through pixels come back exactly");
        }

        // Pictures are decoded only as big as they are shown, and the decoded ones are kept to a memory budget.
        {
            // a JPEG photo shows (1.10.0 and before couldn't decode JPEGs at all)
            var jpegBytes = ImageStore.Encode(ImageStore.Decode(TestPicture(3000, 2000), int.MaxValue), "image/jpeg");
            var jpeg = images.Import(jpegBytes);
            Ok(images.Info(jpeg)?.Mime == "image/jpeg" && images.Bitmap(jpeg, 600) is { PixelWidth: >= 600 } && images.Bitmap(jpeg) is not null, "a JPEG photo is kept as JPEG and shows");
            var big = images.Import(TestPicture(3000, 2000));
            var shown = images.Bitmap(big, 600)!;
            Ok(shown.PixelWidth >= 600 && shown.PixelWidth < 1000, $"a 3000 px picture shown 600 px wide is decoded at {shown.PixelWidth} px, not full size");
            Ok(ReferenceEquals(images.Bitmap(big, 500), shown), "a smaller ask reuses it");
            var bigger = images.Bitmap(big, 1500)!;
            Ok(bigger.PixelWidth >= 1500, $"made bigger, it is decoded again sharper ({bigger.PixelWidth} px)");
            Ok(images.Bitmap(big)!.PixelWidth == 2560, "copying it out gets the biggest decode (2560 px)");
            var many = Enumerable.Range(0, 14).Select(_ => images.Import(TestPicture(2600, 1800))).ToList();
            foreach (var m in many) images.Bitmap(m, 2600);
            Ok(images.CacheBytes <= ImageStore.CacheBudget, $"14 big pictures decoded: {images.CacheBytes / 1048576} MB kept (budget {ImageStore.CacheBudget / 1048576} MB)");
            Ok(images.Bitmap(many[^1], 2600) is not null && images.Bitmap(many[0], 2600) is not null, "an older one dropped from memory just decodes again when needed");
        }

        // The markup window's content, drawn off screen.
        images.SaveEdit(id, Mark.ToJson(marks), ImageStore.Encode(flat, "image/png"));
        var w = new MarkupWindow(images, id, BrandIcon.Image);
        var content = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Child = content };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        using var src = Host(host, 1000, 700);
        Pump(250);
        Ok(w.Zoom <= 1.0001 && w.Zoom > 0.5, $"the picture opens fitted to the window, never past actual size ({w.Zoom * 100:0}%)");
        Shot(host, Path.Combine(outDir, "markup.png"));
        w.SetZoom(2);
        Pump(150);
        Ok(Math.Abs(w.Zoom - 2) < 0.001, "zooming in to 200%");
        Shot(host, Path.Combine(outDir, "markup-zoomed.png"));
        w.Fit();
        Ok(w.Zoom <= 1.0001, "Fit goes back to the whole picture");

        // Blur, pixelate and cut out: edits to the picture itself, in the picture's own pixels.
        {
            static byte[] Px(BitmapSource b) { var f = new FormatConvertedBitmap(b, PixelFormats.Pbgra32, null, 0); var x = new byte[f.PixelWidth * f.PixelHeight * 4]; f.CopyPixels(x, f.PixelWidth * 4, 0); return x; }
            static int At(BitmapSource b, byte[] px, int x, int y) => BitConverter.ToInt32(px, (y * b.PixelWidth + x) * 4);
            // a fine black-and-white checkerboard: the hardest thing to hide
            var cw = 400; var ch = 300; var cells = new byte[cw * ch * 4];
            for (var y = 0; y < ch; y++) for (var x = 0; x < cw; x++) { var v = (byte)((x + y) % 2 == 0 ? 0 : 255); var i = (y * cw + x) * 4; cells[i] = cells[i + 1] = cells[i + 2] = v; cells[i + 3] = 255; }
            var checker = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Pbgra32, null, cells, cw * 4);
            var area = new Mark { X1 = 100, Y1 = 80, X2 = 300, Y2 = 220 };

            area.Kind = "pixel";
            var pix = PhotoEdits.Apply(checker, [area]);
            var pp = Px(pix);
            Ok(At(pix, pp, 100, 80) == At(pix, pp, 102, 81) && At(pix, pp, 50, 50) == At(checker, cells, 50, 50) && At(pix, pp, 51, 50) == At(checker, cells, 51, 50),
                "pixelate: the area becomes squares of one colour; outside it nothing changes");

            area.Kind = "blur";
            var blur = PhotoEdits.Apply(checker, [area]);
            var bp = Px(blur);
            var spread = 0;
            for (var y = 85; y < 215; y++) for (var x = 105; x < 295; x++) spread = Math.Max(spread, Math.Abs(bp[(y * cw + x) * 4] - 128));
            Ok(spread < 12 && At(blur, bp, 20, 20) == At(checker, cells, 20, 20), $"blur: no trace of the fine detail is left (every pixel within {spread} of grey); outside unchanged");

            var rowsCut = new Mark { Kind = "cut", X1 = 0, Y1 = 100, X2 = 400, Y2 = 160 };
            var shortPic = PhotoEdits.Apply(TestPictureBitmap(400, 300), [rowsCut]);
            var full = Px(TestPictureBitmap(400, 300));
            var sp = Px(shortPic);
            Ok(shortPic.PixelWidth == 400 && shortPic.PixelHeight == 240 && sp.AsSpan((100 * 400) * 4, 400 * 4).SequenceEqual(full.AsSpan((160 * 400) * 4, 400 * 4)),
                "cut out (wide box): those rows go and the picture closes up (300 → 240 high)");
            var colsCut = new Mark { Kind = "cut", X1 = 150, Y1 = 40, X2 = 250, Y2 = 280 };
            var narrow = PhotoEdits.Apply(TestPictureBitmap(400, 300), [colsCut]);
            var np = Px(narrow);
            Ok(narrow.PixelWidth == 300 && narrow.PixelHeight == 300 && BitConverter.ToInt32(np, (10 * 300 + 150) * 4) == BitConverter.ToInt32(full, (10 * 400 + 250) * 4),
                "cut out (tall box): those columns go (400 → 300 wide)");
            // a box started outside the picture (in the space around it) begins at the picture's edge
            {
                var drawn = w.Layer.Marks.Count;
                w.SetTool("box");
                w.Layer.StartAt(new Point(-60, -40));
                w.Layer.DragTo(new Point(200, 150));
                w.Layer.Finish();
                var started = w.Layer.Marks.Count == drawn + 1 ? w.Layer.Marks[^1] : null;
                Ok(started is { X1: 0, Y1: 0, X2: 200, Y2: 150 }, $"a box started outside the picture begins at its corner ({started?.X1:0},{started?.Y1:0})");
                w.Layer.StartAt(new Point(700, 300));
                w.Layer.DragTo(new Point(5000, 9000));
                w.Layer.Finish();
                Ok(w.Layer.Marks[^1] is { X2: 800, Y2: 500 }, "and one dragged past the picture stops at its far corner");
                w.Layer.Marks.RemoveRange(drawn, 2);
            }
            var everything = new Mark { Kind = "cut", X1 = 0, Y1 = 0, X2 = 400, Y2 = 296 };
            Ok(!PhotoEdits.CutLeavesEnough(everything, 400, 300), "a cut can't take the whole picture");

            // in the editor: arrows and boxes follow the cut; the saved picture and its size are the new shape
            var before = w.Layer.Marks.Where(m => m.Kind == "box").Select(m => m.Y2).First();   // box bottom at 440
            Ok(w.Layer.AddCut(new Mark { Kind = "cut", X1 = 0, Y1 = 220, X2 = 800, Y2 = 260 }), "cutting a band out in the editor");
            Pump(150);
            var boxAfter = w.Layer.Marks.First(m => m.Kind == "box");
            Ok(w.PictureSize == (800, 460) && Math.Abs(boxAfter.Y2 - (before - 40)) < 0.01, $"the picture is 800 × {w.PictureSize.Height} and the box below the band moved up with it");
            w.Layer.Add(new Mark { Kind = "pixel", X1 = 560, Y1 = 40, X2 = 760, Y2 = 160 });
            w.Layer.Add(new Mark { Kind = "blur", X1 = 560, Y1 = 260, X2 = 760, Y2 = 420 });
            w.SetTool("cut");
            w.Fit();
            Pump(200);
            Shot(host, Path.Combine(outDir, "markup-edits.png"));
            w.SaveAndClose();
            var edited = images.Info(id)!;
            Ok(edited.Width == 800 && edited.Height == 460 && Mark.Parse(edited.Marks).Count(m => PhotoEdits.IsPhoto(m)) == 3, $"saved: the note shows it at 800 × {edited.Height}; the edits are kept to change later");
            var shownPic = images.Bitmap(id, 800)!;
            Ok(shownPic.PixelHeight == 460, "the note's copy is the new shape");
            var reopened = new MarkupWindow(images, id, BrandIcon.Image);
            Ok(reopened.PictureSize == (800, 460) && reopened.Layer.Marks.Count == 5, "opening it again shows it edited, every edit still there");
            reopened.Close();
        }
        w.Close();
    }

    private static BitmapSource TestPictureBitmap(int w, int h) => ImageStore.Decode(TestPicture(w, h), int.MaxValue);

    /// <summary>Text files keep their encoding, byte-order mark and line endings: only the words change.</summary>
    private static void TextFiles()
    {
        var dir = Path.Combine(AppPaths.DataDir, "txt");
        Directory.CreateDirectory(dir);
        string Write(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
        byte[] Bytes(string s, Encoding e, bool bom) => [.. (bom ? e.GetPreamble() : []), .. e.GetBytes(s)];

        var a = Write("bom-crlf.txt", Bytes("one\r\ntwo\r\n", new UTF8Encoding(true), true));
        var fa = TextFile.Read(a);
        Ok(fa.Text == "one\ntwo" && fa.Bom && fa.NewLine == "\r\n" && fa.EndsWithNewLine, "reads UTF-8 with a byte-order mark and Windows line endings");
        fa.Save("one\ntwo\nthree");
        Ok(File.ReadAllBytes(a).SequenceEqual(Bytes("one\r\ntwo\r\nthree\r\n", new UTF8Encoding(true), true)), "saves it back the same way (mark, CRLF, final line ending)");

        var b = Write("lf.txt", Encoding.UTF8.GetBytes("alpha\nbeta"));
        var fb = TextFile.Read(b);
        fb.Save("alpha\nbeta\ngamma");
        Ok(File.ReadAllBytes(b).SequenceEqual(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma")) && !fb.Bom, "Unix line endings, no mark, no final newline stay that way");

        var ansi = TextFile.Ansi();
        var c = Write("ansi.txt", ansi.GetBytes("café crème\r\n"));
        var fc = TextFile.Read(c);
        Ok(fc.Text == "café crème", $"an old Notepad (ANSI) file reads right ({fc.Encoding.WebName})");
        fc.Save("café crème brûlée");
        Ok(File.ReadAllBytes(c).SequenceEqual(ansi.GetBytes("café crème brûlée\r\n")), "and is saved in the same code page");

        var d = Write("utf16.txt", Bytes("wide\r\n", new UnicodeEncoding(false, true), true));
        var fd = TextFile.Read(d);
        fd.Save("wider");
        Ok(File.ReadAllBytes(d).SequenceEqual(Bytes("wider\r\n", new UnicodeEncoding(false, true), true)), "UTF-16 files too");
    }

    /// <summary>Registering for text files, in a test-only key (never the real one).</summary>
    private static void Registration()
    {
        const string root = @"Software\pxdNotepadSelfTest";
        FileTypes.Root = root;
        try
        {
            FileTypes.Register(@"C:\Test\pxdNotepad.exe");
            using var owp = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\Classes\.txt\OpenWithProgids");
            using var ra = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\RegisteredApplications");
            using var fa = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\pxd\pxdNotepad\Capabilities\FileAssociations");
            Ok(FileTypes.IsRegistered() && owp?.GetValueNames().Contains(FileTypes.ProgId) == true && ra?.GetValue("pxd Notepad") is string && fa?.GetValue(".txt") as string == FileTypes.ProgId,
                "registers for .txt for this user: Open with, Default apps, the command");
            FileTypes.RefreshIfRegistered(@"D:\Moved\pxdNotepad.exe");
            Ok(FileTypes.RegisteredCommand() == "\"D:\\Moved\\pxdNotepad.exe\" \"%1\"", "a moved install updates the command");
            FileTypes.Unregister();
            Ok(!FileTypes.IsRegistered(), "and can be taken away again");
        }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, false); } catch { /* gone */ }
            FileTypes.Root = "Software";
        }
    }

    /// <summary>A second start hands its file to the copy that's already open.</summary>
    private static void Handoff()
    {
        var id = "pxdNotepadSelfTest-" + Guid.NewGuid().ToString("N")[..8];
        using var first = new pxdStandard.SingleInstance(id);
        string? got = null;
        using var arrived = new ManualResetEventSlim();
        first.FileRequested += p => { got = p; arrived.Set(); };
        using var second = new pxdStandard.SingleInstance(id);
        Ok(first.IsFirst && !second.IsFirst, "only the first start is the app");
        second.SignalFirst(@"C:\Some folder\list.txt");
        Ok(arrived.Wait(5000) && got == @"C:\Some folder\list.txt", "a file opened while it's running goes to the open window");
    }

    private static void LinkFinding()
    {
        var found = LinkDetector.Find("Call (555) 123-4567 x204 or +1 555.987.6543, mail someone@example.com, see www.example.com/page. Also https://pxdtek.com/n.");
        Ok(found.Select(f => f.Kind).SequenceEqual([LinkKind.Phone, LinkKind.Phone, LinkKind.Email, LinkKind.Web, LinkKind.Web]), "finds phones, an email and web links: " + string.Join(" | ", found.Select(f => f.Text)));
        Ok(found[0].Target == "tel:5551234567;ext=204" && found[1].Target == "tel:+15559876543", $"phone links dial digits only, with the extension ({found[0].Target}, {found[1].Target})");
        Ok(found[2].Target == "mailto:someone@example.com" && found[3].Target == "https://www.example.com/page" && found[4].Text == "https://pxdtek.com/n", "email and web targets (a sentence's full stop isn't part of a link)");
        Ok(LinkDetector.Find("Dated 2026-11-15, order 1234567890123, version 10.0.302, 555-1234").Count == 0, "dates, long numbers, versions and 7-digit numbers aren't phone links");
        Ok(LinkDetector.Find("555-123-4567").Count == 1 && LinkDetector.Find("555 123 4567").Count == 1 && LinkDetector.Find("5551234567").Count == 1, "dashes, spaces or no separators");
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
        var vi = await SyncService.VaultInfoAsync(server, key);
        Ok(vi is { Bytes: > 0, Items: > 0 }, $"sync: the server says how much it keeps ({(vi is null ? "?" : NoteStore.Size(vi.Bytes))}, {vi?.Items} records; last backup {(vi?.BackupAt is null ? "none here" : vi.BackupAt.ToString())})");

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
        tagsA.ToggleFolder(cat.Id, star.Id);
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        Ok(tagsB.TagsOfFolder(cat.Id).SequenceEqual([star.Id]), "sync: a folder's tags reach B");

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
