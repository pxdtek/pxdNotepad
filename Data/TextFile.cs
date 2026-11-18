using System.Globalization;
using System.Text;

namespace pxdNotepad.Data;

/// <summary>
/// A plain text file opened from Explorer: read as it is (its encoding, byte-order mark and line endings noted) and
/// written back the same way, so saving never changes anything but the words.
/// </summary>
public sealed class TextFile
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public required Encoding Encoding { get; init; }
    public bool Bom { get; init; }
    /// <summary>"\r\n" (Windows), "\n" or "\r".</summary>
    public string NewLine { get; init; } = "\r\n";
    public bool EndsWithNewLine { get; init; }
    public bool ReadOnly { get; init; }

    public const long BigFile = 8 * 1024 * 1024;

    public static TextFile Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding enc;
        int skip = 0;
        var bom = false;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { enc = new UTF8Encoding(true); skip = 3; bom = true; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = new UnicodeEncoding(false, true); skip = 2; bom = true; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = new UnicodeEncoding(true, true); skip = 2; bom = true; }
        else
        {
            // No mark: UTF-8 if it's valid UTF-8, otherwise the PC's own "ANSI" code page (old Notepad files).
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                enc = new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                enc = Ansi();
            }
        }
        var text = enc.GetString(bytes, skip, bytes.Length - skip);
        var nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : "\r\n";
        var ends = text.EndsWith('\n') || text.EndsWith('\r');
        // The editor works in plain \n; the file's own line ending goes back on when it's saved.
        var plain = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (ends) plain = plain[..^1];
        bool ro;
        try { ro = new FileInfo(path).IsReadOnly; } catch { ro = true; }
        return new TextFile { Path = path, Text = plain, Encoding = enc, Bom = bom, NewLine = nl, EndsWithNewLine = ends, ReadOnly = ro };
    }

    public static Encoding Ansi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Writes the text (lines split on \n) back in the file's own encoding and line endings, safely:
    /// a temporary file next to it first, then swapped in.</summary>
    public void Save(string text)
    {
        var body = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", NewLine);
        if (EndsWithNewLine) body += NewLine;
        var data = Encoding.GetBytes(body);
        var pre = Bom ? Encoding.GetPreamble() : [];
        var tmp = Path + ".pxdsave";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                fs.Write(pre);
                fs.Write(data);
                fs.Flush(true);
            }
            File.Replace(tmp, Path, null, ignoreMetadataErrors: true);
        }
        catch
        {
            // Some folders allow writing the file but not making new ones next to it: write it directly.
            try { File.Delete(tmp); } catch { /* not there */ }
            using var fs = new FileStream(Path, FileMode.Create, FileAccess.Write);
            fs.Write(pre);
            fs.Write(data);
        }
    }
}
