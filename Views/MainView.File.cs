using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using pxdNotepad.Data;

namespace pxdNotepad.Views;

/// <summary>
/// A .txt opened from Explorer (Open with, or pxd Notepad as the default for text files): edited in place and saved
/// back to the file as plain text, in its own encoding and line endings. It isn't a note (no tabs, tags, sync or
/// search) unless "Save to my notes" copies it in.
/// </summary>
public partial class MainView
{
    private TextFile? _file;

    public string? OpenFilePath => _file?.Path;
    public string? OpenNoteId => _openId;

    public void OpenFile(string path)
    {
        RememberSpot();
        SaveNow();
        TextFile f;
        try
        {
            if (new FileInfo(path).Length > TextFile.BigFile
                && !Dialogs.Confirm(Owner, "Open file", $"{System.IO.Path.GetFileName(path)} is a big file ({new FileInfo(path).Length / 1048576.0:0.#} MB). It may be slow to open and edit here. Open it anyway?", "Open"))
                return;
            f = TextFile.Read(path);
        }
        catch (Exception e)
        {
            Dialogs.Info(Owner, "Open file", $"Couldn't open {System.IO.Path.GetFileName(path)}: {e.Message}");
            return;
        }

        _openId = null;
        _file = f;
        _dirty = false;
        _loading = true;
        try
        {
            HideSecrets();
            var doc = NewDocument();
            foreach (var line in f.Text.Split('\n'))
                doc.Blocks.Add(new Paragraph(new Run(line)));
            _tabs.Clear();
            _tabs.Add(new NoteTab("File", doc));
            _tab = 0;
            _daily = false;
            ShowDocument(doc);
            Editor.IsReadOnly = f.ReadOnly;
            TitleBox.Text = System.IO.Path.GetFileName(path);
            TitleBox.IsReadOnly = true;
        }
        finally
        {
            _loading = false;
        }
        if (Selected is { } sel) sel.IsSelected = false;
        EditorPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        NoteMeta.Text = path + (f.ReadOnly ? "  ·  read-only" : "") + "  ·  plain text (" + EncodingName(f) + ")";
        SaveState.Text = f.ReadOnly ? "This file is read-only" : "Saves to the file as you type";
        WordCount(f.Text);
        Gutter.Reset();
        Editor.ScrollToHome();
        ApplyLayoutMode();
        BuildTabStrip();
        Editor.CaretPosition = Editor.Document.ContentStart;
        Editor.Focus();
    }

    private static string EncodingName(TextFile f) => f.Encoding switch
    {
        System.Text.UnicodeEncoding { } u when u.GetPreamble().FirstOrDefault() == 0xFE => "UTF-16 BE",
        System.Text.UnicodeEncoding => "UTF-16",
        System.Text.UTF8Encoding => f.Bom ? "UTF-8 with BOM" : "UTF-8",
        _ => f.Encoding.WebName.ToUpperInvariant(),
    } + (f.NewLine == "\r\n" ? ", Windows line endings" : f.NewLine == "\n" ? ", Unix line endings" : ", Mac line endings");

    /// <summary>Writes the open file back (plain text only: fonts and colours can't go in a .txt).</summary>
    private void SaveFile()
    {
        if (_file is null || _file.ReadOnly) return;
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
        if (text.EndsWith("\r\n")) text = text[..^2];
        try
        {
            _file.Save(text);
            SaveState.Text = "Saved to " + System.IO.Path.GetFileName(_file.Path);
            WordCount(text);
        }
        catch (Exception e)
        {
            _dirty = true;
            SaveState.Text = "Couldn't save the file: " + e.Message;
        }
    }

    /// <summary>Copies the open file into your notes (top level, or the folder selected before), then opens the note.</summary>
    public void SaveFileToNotes()
    {
        if (_file is null) return;
        SaveNow();
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.TrimEnd('\r', '\n');
        var doc = NewDocument();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            doc.Blocks.Add(new Paragraph(new Run(line)));
        var json = pxdNotepad.Editor.DocSerializer.Write(doc, null, out var plain);
        var id = _store.NewNote(null, System.IO.Path.GetFileNameWithoutExtension(_file.Path), json, plain);
        _file = null;
        BuildTree();
        OpenNote(id, []);
    }

    /// <summary>The bar under the title for a file: where it is, copy to notes.</summary>
    private void BuildFileStrip()
    {
        TabStrip.Children.Clear();
        TabStrip.Visibility = Visibility.Visible;
        Button B(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0) };
            b.SetResourceReference(StyleProperty, "ToolButton");
            b.SetResourceReference(ForegroundProperty, "Dim");
            b.Click += (_, _) => a();
            TabStrip.Children.Add(b);
            return b;
        }
        B("Save to my notes", "Copy this file into your notes (searchable, synced)", SaveFileToNotes);
        B("Show in folder", "Open its folder in File Explorer", () =>
        {
            if (_file is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_file.Path}\"") { UseShellExecute = false })?.Dispose();
        });
    }
}
