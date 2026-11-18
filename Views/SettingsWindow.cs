using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pxdNotepad.Data;
using pxdNotepad.Sync;
using pxdStandard;

namespace pxdNotepad.Views;

/// <summary>Settings: a list of pages on the left (Look, Text, Text files, Storage, Sync), the chosen page on the right.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly SyncService _sync;
    private readonly MainView _view;
    private readonly StackPanel _syncBox = new();
    private readonly StackPanel _filesBox = new();
    private readonly StackPanel _storageBox = new();

    private const long VaultQuota = 2L * 1024 * 1024 * 1024;

    /// <summary>What's kept where, drawn: this PC (what the notes file is made of), the synced copy on the server
    /// against its 2 GB, the last off-site backup, and charts of notes and space over time.</summary>
    private async Task BuildStorage()
    {
        _storageBox.Children.Clear();
        try { _view.Store.RecordUsage(); } catch { /* history only */ }
        var u = _view.Store.StorageUsage();
        var key = _s.GetSyncKey();
        var host = Uri.TryCreate(_s.SyncServer, UriKind.Absolute, out var su) ? su.Host : _s.SyncServer;

        // This PC: one number, then what it's made of
        _storageBox.Children.Add(Label("On this PC"));
        var notes = u.Notes == 1 ? "1 note" : $"{u.Notes} notes";
        if (u.Trashed > 0) notes += $" ({u.Trashed} in Trash)";
        _storageBox.Children.Add(Headline(NoteStore.Size(u.FileBytes), $"{notes}  ·  {u.Folders} folder{(u.Folders == 1 ? "" : "s")}  ·  {u.Tags} tag{(u.Tags == 1 ? "" : "s")}  ·  {u.Pictures} picture{(u.Pictures == 1 ? "" : "s")}"));
        var other = Math.Max(0, u.FileBytes - u.NoteBytes - u.PictureBytes);
        _storageBox.Children.Add(new SegmentBar { Parts = [(u.NoteBytes, "Chart1"), (u.PictureBytes, "Chart2"), (other, "Chart3")], Total = u.FileBytes, Margin = new Thickness(0, 8, 0, 6) });
        _storageBox.Children.Add(Legend(("Chart1", "Notes", NoteStore.Size(u.NoteBytes)), ("Chart2", "Pictures", NoteStore.Size(u.PictureBytes)), ("Chart3", "Search index & other", NoteStore.Size(other))));

        // The server (filled in once it answers)
        _storageBox.Children.Add(Label("On " + host));
        var serverBox = new StackPanel();
        serverBox.Children.Add(Note(key is null ? "Sync is off, so nothing of yours is kept there." : "Asking the server…"));
        _storageBox.Children.Add(serverBox);

        _storageBox.Children.Add(Label("Off-site backup"));
        var backupBox = new StackPanel();
        backupBox.Children.Add(Note(key is null ? "Only synced notes are backed up off-site (nightly, to Cloudflare R2, encrypted)." : "…"));
        _storageBox.Children.Add(backupBox);

        // Over time
        _storageBox.Children.Add(Label("Notes over time"));
        _storageBox.Children.Add(new LineChart { Data = [new LineChart.Series("Notes", "Chart1", _view.Store.NotesOverTime())], Empty = "No notes yet" });
        _storageBox.Children.Add(Label("Space over time"));
        // One series needs no legend (the heading says what it is); the legend appears when the backup line joins it.
        var spaceLegend = Legend(("Chart1", "This PC", ""), ("Chart2", "Off-site backup", ""));
        spaceLegend.Visibility = Visibility.Collapsed;
        var space = new LineChart { Bytes = true, Format = v => v <= 0 ? "0" : NoteStore.Size((long)v), Empty = "Fills in a day at a time from today", Data = [new LineChart.Series("This PC", "Chart1", _view.Store.UsageHistory())] };
        _storageBox.Children.Add(spaceLegend);
        _storageBox.Children.Add(space);
        var refresh = new Button { Content = "Refresh", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
        refresh.Click += async (_, _) => await BuildStorage();
        _storageBox.Children.Add(refresh);

        if (key is null) return;
        var info = await SyncService.VaultInfoAsync(_s.SyncServer, key);
        serverBox.Children.Clear();
        backupBox.Children.Clear();
        if (info is null)
        {
            serverBox.Children.Add(Note($"Couldn't reach {host} just now."));
            backupBox.Children.Add(Note("Unknown (the server didn't answer)."));
            return;
        }
        serverBox.Children.Add(Headline(NoteStore.Size(info.Bytes), $"of your {NoteStore.Size(VaultQuota)}  ·  encrypted  ·  {info.Items} records"));
        serverBox.Children.Add(new SegmentBar { Parts = [(info.Bytes, "Chart1")], Total = VaultQuota, Margin = new Thickness(0, 8, 0, 0) });
        if (info.BackupAt is { } at)
        {
            var late = (DateTime.UtcNow - at).TotalHours > 36;
            backupBox.Children.Add(Headline(NoteStore.Size(info.BackupBytes ?? 0), $"last copy {at.ToLocalTime():ddd MMM d, h:mm tt}  ·  every night  ·  keeps 30 days + 12 months"));
            if (late)
            {
                var warn = new TextBlock { Text = "⚠ Late: no copy for over a day and a half. The server may need a look.", Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
                warn.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
                backupBox.Children.Add(warn);
            }
        }
        else backupBox.Children.Add(Note("No backup recorded yet."));
        if (info.BackupHistory is { Count: > 0 } hist)
        {
            space.Data = [space.Data[0], new LineChart.Series("Off-site backup", "Chart2", hist)];
            spaceLegend.Visibility = Visibility.Visible;
        }
        space.InvalidateVisual();
    }

    /// <summary>A big number with a line of detail beside it.</summary>
    private static StackPanel Headline(string big, string detail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var b = new TextBlock { Text = big, FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
        b.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var d = new TextBlock { Text = detail, Margin = new Thickness(10, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom, TextWrapping = TextWrapping.Wrap };
        d.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        row.Children.Add(b);
        row.Children.Add(d);
        return row;
    }

    /// <summary>Swatch + name (+ value) for each part or series; the text stays in text colours.</summary>
    private static WrapPanel Legend(params (string BrushKey, string Name, string Value)[] items)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var (brush, name, value) in items)
        {
            var sw = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            sw.SetResourceReference(Border.BackgroundProperty, brush);
            var n = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            row.Children.Add(sw);
            row.Children.Add(n);
            if (value.Length > 0)
            {
                var v = new TextBlock { Text = value, Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                v.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                row.Children.Add(v);
            }
            w.Children.Add(row);
        }
        return w;
    }

    private static DockPanel Line(string label, string value)
    {
        var l = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Top };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Tag = "value" };
        var d = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        d.Children.Add(l);
        d.Children.Add(v);
        return d;
    }

    private static void SetValue(DockPanel line, string value) => ((TextBlock)line.Children[1]).Text = value;

    /// <summary>Offer pxd Notepad for .txt files (this user only, no admin), and send them to Windows to pick it.</summary>
    private void BuildFiles()
    {
        _filesBox.Children.Clear();
        var on = FileTypes.IsRegistered();
        _filesBox.Children.Add(Note(on
            ? "pxd Notepad is offered for .txt, .log and .md files on this PC (just for you, no admin). To make it the default, pick it for .txt in Windows' Default apps; files then open here and save back to themselves as plain text."
            : "Let .txt, .log and .md files open in pxd Notepad: it's added to \"Open with\" and to Windows' Default apps for you (no admin needed). Windows then asks you to pick it once."));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (!on)
        {
            var use = new Button { Content = "Use pxd Notepad for text files" };
            use.SetResourceReference(StyleProperty, "AccentButton");
            use.Click += (_, _) =>
            {
                try
                {
                    FileTypes.Register(Environment.ProcessPath ?? "");
                    FileTypes.OpenDefaultApps();
                }
                catch (Exception e)
                {
                    Dialogs.Info(this, "Text files", "Windows didn't allow it: " + e.Message);
                }
                BuildFiles();
            };
            row.Children.Add(use);
        }
        else
        {
            var open = new Button { Content = "Open Default apps" };
            open.Click += (_, _) => FileTypes.OpenDefaultApps();
            var remove = new Button { Content = "Stop offering it", Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) => { FileTypes.Unregister(); BuildFiles(); };
            row.Children.Add(open);
            row.Children.Add(remove);
        }
        _filesBox.Children.Add(row);
    }

    public SettingsWindow(AppSettings settings, SyncService sync, MainView view)
    {
        _s = settings;
        _sync = sync;
        _view = view;
        Width = 780;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // ── the pages ──
        var look = Page("Look", "How pxd Notepad looks.");
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (key, label) in new[] { ("dark", "Dark"), ("light", "Light") })
        {
            var r = new RadioButton { Content = label, GroupName = "theme", IsChecked = settings.Theme == key };
            r.Checked += (_, _) => { _s.Theme = key; App.SetTheme(key); _s.Save(); _view.BuildTree(); };
            themes.Children.Add(r);
        }
        look.Children.Add(Label("Theme"));
        look.Children.Add(themes);
        look.Children.Add(Switch("Outline blurred text (easier to spot)", settings.BlurOutline, v => { _s.BlurOutline = v; _s.Save(); _view.Blur.Outline = v; _view.Blur.InvalidateVisual(); }));

        var text = Page("Text", "Notes use this font and size unless you change it for part of a note.");
        var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var font = new ComboBox { Width = 230, IsEditable = true, Text = settings.FontFamily, MaxDropDownHeight = 360 };
        font.DropDownOpened += (_, _) =>
        {
            if (font.Items.Count > 0) return;
            var cur = font.Text;
            foreach (var n in System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n)) font.Items.Add(n);
            font.Text = cur;
        };
        var size = new ComboBox { Width = 70, IsEditable = true, Text = settings.FontSize.ToString(), Margin = new Thickness(8, 0, 0, 0) };
        foreach (var x in new[] { 11, 12, 13, 14, 15, 16, 18, 20, 22, 24 }) size.Items.Add(x.ToString());
        var apply = new Button { Content = "Use", Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (font.Text.Trim().Length > 0) _s.FontFamily = font.Text.Trim();
            if (double.TryParse(size.Text, out var z) && z >= 6 && z <= 72) _s.FontSize = z;
            _s.Save();
            _view.ReopenCurrent();
        };
        fontRow.Children.Add(font);
        fontRow.Children.Add(size);
        fontRow.Children.Add(apply);
        text.Children.Add(Label("Font and size"));
        text.Children.Add(fontRow);
        text.Children.Add(Switch("Check spelling", settings.SpellCheck, v => { _s.SpellCheck = v; _s.Save(); _view.SetSpellCheck(v); }));
        text.Children.Add(Switch("New notes date each day's entries", settings.DailyByDefault, v => { _s.DailyByDefault = v; _s.Save(); }));
        text.Children.Add(Switch("Show line numbers", settings.LineNumbers, v => { if (_s.LineNumbers != v) _view.ToggleLineNumbers(); }));
        text.Children.Add(Switch("Line numbers match each line's text size", settings.LineNumbersMatchSize, v => { _s.LineNumbersMatchSize = v; _s.Save(); _view.Gutter.MatchTextSize = v; _view.Gutter.Reset(); }));

        var files = Page("Text files", "Open .txt, .log and .md files in pxd Notepad.");
        files.Children.Add(_filesBox);
        BuildFiles();

        var syncPage = Page("Sync & storage", "Your notes on every PC, encrypted with your own key, and how much space they take.");
        syncPage.Children.Add(_syncBox);
        BuildSync();
        var storageHead = new TextBlock { Text = "Storage", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 30, 0, 2) };
        storageHead.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        syncPage.Children.Add(storageHead);
        syncPage.Children.Add(_storageBox);

        _pages["look"] = look;
        _pages["text"] = text;
        _pages["files"] = files;
        _pages["sync"] = syncPage;

        // ── left: the list of pages ──
        var list = new ListBox { Margin = new Thickness(0, 10, 0, 10) };
        foreach (var (key, label, glyph) in new[]
        {
            ("look", "Look", "M8,1.8 A6.2,6.2 0 1 1 7.99,1.8 Z M8,1.8 L8,14.2 A6.2,6.2 0 0 0 8,1.8 Z"),
            ("text", "Text", "M3,13 L7.2,2.5 L8.8,2.5 L13,13 M4.8,9 L11.2,9"),
            ("files", "Text files", "M4,1.8 L9.6,1.8 L12.5,4.7 L12.5,14.2 L4,14.2 Z M9.6,1.8 L9.6,4.7 L12.5,4.7 M6,8 L10.5,8 M6,10.6 L10.5,10.6"),
            ("sync", "Sync & storage", "M4.5,13 L12,13 C13.9,13 15,11.7 15,10.1 C15,8.5 13.8,7.3 12.3,7.2 C11.9,4.9 10.1,3.5 8,3.5 C5.8,3.5 4.1,5 3.7,7.1 C2.1,7.4 1,8.6 1,10.1 C1,11.7 2.4,13 4.5,13 Z"),
        })
        {
            var icon = Images.MarkupWindow.Glyph(glyph);
            icon.Margin = new Thickness(0, 0, 10, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            list.Items.Add(new ListBoxItem { Content = row, Tag = key, Padding = new Thickness(12, 9, 12, 9) });
        }
        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string key }) return;
            right.Content = _pages[key];
            right.ScrollToTop();
            _s.SettingsPage = key;
            if (key == "sync") _ = BuildStorage();   // asks the server only when you look
        };
        _list = list;

        var side = new Border { Width = 190, BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        side.SetResourceReference(Border.BackgroundProperty, "Panel");
        side.SetResourceReference(Border.BorderBrushProperty, "Line");

        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => { _s.Save(); Close(); };
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 12), Child = new DockPanel { LastChildFill = false, Children = { close } } };
        DockPanel.SetDock(close, Dock.Right);
        foot.SetResourceReference(Border.BorderBrushProperty, "Line");
        foot.SetResourceReference(Border.BackgroundProperty, "Panel");

        var root = new DockPanel();
        DockPanel.SetDock(foot, Dock.Bottom);
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(foot);
        root.Children.Add(side);
        root.Children.Add(right);

        Chrome.Apply(this, "Settings", root, BrandIcon.Image, resizable: true);
        Chrome.RememberPlacement(this, "settings");
        ShowPage(sync is not null && Environment.GetEnvironmentVariable("PXDNOTEPAD_SETTINGS_PAGE") is { Length: > 0 } forced ? forced : settings.SettingsPage);
        _sync.StatusChanged += OnStatus;
        Closed += (_, _) => { _sync.StatusChanged -= OnStatus; _s.Save(); };
    }

    private readonly Dictionary<string, StackPanel> _pages = [];
    private ListBox _list = null!;

    /// <summary>Shows one page ("look", "text", "files", "storage", "sync").</summary>
    public void ShowPage(string? key)
    {
        if (key == "storage") key = "sync";   // Storage is part of Sync & storage now
        var item = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key) ?? (ListBoxItem)_list.Items[0];
        _list.SelectedItem = item;
    }

    private static StackPanel Page(string title, string about)
    {
        var page = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };
        var t = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var a = Note(about);
        a.Margin = new Thickness(0, 4, 0, 16);
        page.Children.Add(t);
        page.Children.Add(a);
        return page;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return t;
    }

    private void OnStatus(SyncStatus s) => Dispatcher.BeginInvoke(BuildSync);

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return t;
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    private static CheckBox Switch(string label, bool on, Action<bool> changed)
    {
        var c = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 10, 0, 0) };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    private void BuildSync()
    {
        _syncBox.Children.Clear();
        var key = _s.GetSyncKey();
        if (key is not null)
        {
            var st = _sync.Status;
            _syncBox.Children.Add(Note($"On, with {_s.SyncServer}. {st.Message}{(st.LastOk is { } t ? $" (last at {t:h:mm tt})" : "")}. Your notes are encrypted on this PC before they go; the server can't read them."));
            _syncBox.Children.Add(Note("To add another PC: install pxd Notepad there, open Settings → Sync → \"I have a sync key\", and type this key in. Keep it safe: anyone with it can read your notes, and without it they can't be recovered."));
            var shown = new TextBox { Text = new string('•', 20), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(0, 10, 0, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var show = new Button { Content = "Show key" };
            show.Click += (_, _) => shown.Text = SyncKeys.Format(key);
            var copy = new Button { Content = "Copy key", Margin = new Thickness(8, 0, 0, 0) };
            copy.Click += (_, _) => { try { Clipboard.SetText(SyncKeys.Format(key)); copy.Content = "Copied"; } catch { /* busy */ } };
            var now = new Button { Content = "Sync now", Margin = new Thickness(8, 0, 0, 0) };
            now.Click += async (_, _) => { now.IsEnabled = false; await _sync.SyncNowAsync(); now.IsEnabled = true; };
            var stop = new Button { Content = "Stop syncing", Margin = new Thickness(8, 0, 0, 0) };
            stop.Click += (_, _) =>
            {
                if (!Dialogs.Confirm(this, "Stop syncing", "Stop syncing on this PC? Your notes stay here and on the server; this PC just stops sending and getting changes. You can turn it back on with your sync key.", "Stop syncing")) return;
                _s.SetSyncKey(null);
                _s.Save();
                _ = _sync.SyncNowAsync();
                BuildSync();
            };
            row.Children.Add(show);
            row.Children.Add(copy);
            row.Children.Add(now);
            row.Children.Add(stop);
            _syncBox.Children.Add(shown);
            _syncBox.Children.Add(row);
            return;
        }

        _syncBox.Children.Add(Note("Sync keeps your notes and pictures the same on every PC you use. They're encrypted on this PC with your own sync key before they're sent, so the server only ever holds data it can't read."));
        var server = new TextBox { Text = _s.SyncServer, Margin = new Thickness(0, 10, 0, 0) };
        var serverLabel = Note("Server");
        serverLabel.Margin = new Thickness(0, 10, 0, 0);
        _syncBox.Children.Add(serverLabel);
        _syncBox.Children.Add(server);

        var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var fresh = new RadioButton { Content = "Start syncing (first PC)", GroupName = "syncmode", IsChecked = true };
        var join = new RadioButton { Content = "I have a sync key", GroupName = "syncmode" };
        mode.Children.Add(fresh);
        mode.Children.Add(join);
        _syncBox.Children.Add(mode);

        var label = Note("Invite code (from whoever runs the server; used once)");
        label.Margin = new Thickness(0, 12, 0, 0);
        var input = new TextBox { Margin = new Thickness(0, 6, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 14 };
        var msg = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        msg.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        var go = new Button { Content = "Turn on sync", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        go.SetResourceReference(StyleProperty, "AccentButton");
        fresh.Checked += (_, _) => label.Text = "Invite code (from whoever runs the server; used once)";
        join.Checked += (_, _) => label.Text = "Your sync key (from Settings → Sync on a PC that already syncs)";
        go.Click += async (_, _) =>
        {
            msg.Text = "";
            var url = server.Text.Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && !u.IsLoopback)) { msg.Text = "The server address has to start with https://"; return; }
            go.IsEnabled = false;
            try
            {
                byte[] k;
                string? err;
                if (join.IsChecked == true)
                {
                    k = SyncKeys.Parse(input.Text) ?? Array.Empty<byte>();
                    if (k.Length == 0) { msg.Text = "That isn't a sync key: it's 13 groups of 4 letters and digits."; return; }
                    err = await SyncService.CheckVaultAsync(url, k);
                }
                else
                {
                    if (input.Text.Trim().Length == 0) { msg.Text = "Type the invite code."; return; }
                    k = SyncKeys.NewKey();
                    err = await SyncService.CreateVaultAsync(url, k, input.Text);
                }
                if (err is not null) { msg.Text = err; return; }
                _s.SyncServer = url;
                _s.SetSyncKey(k);
                _s.Save();
                BuildSync();
                await _sync.SyncNowAsync();
            }
            finally
            {
                go.IsEnabled = true;
            }
        };
        _syncBox.Children.Add(label);
        _syncBox.Children.Add(input);
        _syncBox.Children.Add(go);
        _syncBox.Children.Add(msg);
    }
}
