using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pxdNotepad.Data;
using pxdNotepad.Sync;
using pxdStandard;

namespace pxdNotepad.Views;

/// <summary>Look, text defaults and sync.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly SyncService _sync;
    private readonly MainView _view;
    private readonly StackPanel _syncBox = new();

    public SettingsWindow(AppSettings settings, SyncService sync, MainView view)
    {
        _s = settings;
        _sync = sync;
        _view = view;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(22, 14, 22, 20) };

        body.Children.Add(Heading("Look"));
        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in new[] { ("dark", "Dark"), ("light", "Light") })
        {
            var r = new RadioButton { Content = label, GroupName = "theme", IsChecked = settings.Theme == key };
            r.Checked += (_, _) => { _s.Theme = key; App.SetTheme(key); _s.Save(); _view.BuildTree(); };
            themes.Children.Add(r);
        }
        body.Children.Add(themes);

        body.Children.Add(Heading("Text"));
        body.Children.Add(Note("Notes use this font and size unless you change it for part of a note."));
        var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
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
        body.Children.Add(fontRow);
        body.Children.Add(Switch("Check spelling", settings.SpellCheck, v => { _s.SpellCheck = v; _s.Save(); SpellCheck.SetIsEnabled(_view.Editor, v); }));
        body.Children.Add(Switch("Show line numbers", settings.LineNumbers, v => { if (_s.LineNumbers != v) _view.ToggleLineNumbers(); }));
        body.Children.Add(Switch("New notes date each day's entries", settings.DailyByDefault, v => { _s.DailyByDefault = v; _s.Save(); }));
        body.Children.Add(Switch("Line numbers match each line's text size", settings.LineNumbersMatchSize, v => { _s.LineNumbersMatchSize = v; _s.Save(); _view.Gutter.MatchTextSize = v; _view.Gutter.Reset(); }));
        body.Children.Add(Switch("Outline blurred text (easier to spot)", settings.BlurOutline, v => { _s.BlurOutline = v; _s.Save(); _view.Blur.Outline = v; _view.Blur.InvalidateVisual(); }));

        body.Children.Add(Heading("Sync"));
        body.Children.Add(_syncBox);
        BuildSync();

        var close = new Button { Content = "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        Chrome.Apply(this, "Settings", body, BrandIcon.Image, resizable: false);
        _sync.StatusChanged += OnStatus;
        Closed += (_, _) => _sync.StatusChanged -= OnStatus;
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
