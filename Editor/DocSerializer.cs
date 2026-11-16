using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pxdNotepad.Images;

namespace pxdNotepad.Editor;

/// <summary>
/// A note's rich text as compact JSON (what's stored and synced), and back. Only what the editor offers is kept: per-run
/// font, size, colour, highlight, bold/italic/underline/strike; paragraph alignment, indent and size; bullet and
/// numbered lists; line breaks; images (by id, with the width they're shown at). Anything else that gets pasted in
/// (tables, sections, links) is kept as its text and formatting.
///
///   {"v":1,"b":[{"p":[{"t":"Hello","z":24,"w":700,"c":"#FF3B30"},{"br":1},{"img":"…","w":320}],"a":"c"},
///                {"l":"disc","i":[[{"p":[…]}],[…]]}]}
/// </summary>
public static class DocSerializer
{
    public const string ClipboardFormat = "pxdNotepad.Fragment";

    private sealed class Ctx
    {
        public required FlowDocument Doc;
        public TextPointer? From, To;
        public ImageStore? Store;
        public readonly StringBuilder Plain = new();
        public bool Any;
    }

    /// <summary>The document (or just From..To of it) as JSON; <paramref name="plain"/> gets its text for search.</summary>
    public static string Write(FlowDocument doc, ImageStore? store, out string plain, TextPointer? from = null, TextPointer? to = null)
    {
        var ctx = new Ctx { Doc = doc, From = from, To = to, Store = store };
        var buf = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteStartArray("b");
            WriteBlocks(w, doc.Blocks, ctx);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        plain = ctx.Plain.ToString().TrimEnd('\n');
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static bool Outside(TextElement e, Ctx c) =>
        (c.From is not null && e.ContentEnd.CompareTo(c.From) < 0) || (c.To is not null && e.ContentStart.CompareTo(c.To) > 0);

    private static void WriteBlocks(Utf8JsonWriter w, BlockCollection blocks, Ctx c)
    {
        foreach (var b in blocks)
        {
            if (Outside(b, c)) continue;
            switch (b)
            {
                case Paragraph p:
                    WriteParagraph(w, p, c);
                    break;
                case List l:
                    w.WriteStartObject();
                    w.WriteString("l", Marker(l.MarkerStyle));
                    if (l.StartIndex != 1) w.WriteNumber("s", l.StartIndex);
                    w.WriteStartArray("i");
                    foreach (var item in l.ListItems)
                    {
                        if (Outside(item, c)) continue;
                        w.WriteStartArray();
                        WriteBlocks(w, item.Blocks, c);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;
                case Section s:
                    WriteBlocks(w, s.Blocks, c);
                    break;
                case Table t:
                    foreach (var g in t.RowGroups)
                        foreach (var row in g.Rows)
                            foreach (var cell in row.Cells)
                                WriteBlocks(w, cell.Blocks, c);
                    break;
                case BlockUIContainer u:
                    if (ImageOf(u.Child, c) is { } img)
                    {
                        w.WriteStartObject();
                        w.WriteStartArray("p");
                        WriteImage(w, img.Id, img.Width);
                        w.WriteEndArray();
                        w.WriteEndObject();
                        c.Plain.Append('\n');
                    }
                    break;
            }
        }
    }

    private static void WriteParagraph(Utf8JsonWriter w, Paragraph p, Ctx c)
    {
        w.WriteStartObject();
        w.WriteStartArray("p");
        WriteInlines(w, p.Inlines, c);
        w.WriteEndArray();
        if (p.TextAlignment != TextAlignment.Left)
            w.WriteString("a", p.TextAlignment switch { TextAlignment.Center => "c", TextAlignment.Right => "r", _ => "j" });
        if (p.Margin.Left > 0.5) w.WriteNumber("ml", Math.Round(p.Margin.Left, 1));
        if (Math.Abs(p.FontSize - c.Doc.FontSize) > 0.01) w.WriteNumber("z", Math.Round(p.FontSize, 2));
        if (p.FontFamily.Source != c.Doc.FontFamily.Source) w.WriteString("f", p.FontFamily.Source);
        w.WriteEndObject();
        c.Plain.Append('\n');
    }

    private static void WriteInlines(Utf8JsonWriter w, InlineCollection inlines, Ctx c)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case Run r:
                    WriteRun(w, r, c);
                    break;
                case LineBreak lb:
                    if (c.From is not null && lb.ElementEnd.CompareTo(c.From) <= 0) break;
                    if (c.To is not null && lb.ElementStart.CompareTo(c.To) >= 0) break;
                    w.WriteStartObject();
                    w.WriteNumber("br", 1);
                    w.WriteEndObject();
                    c.Plain.Append('\n');
                    break;
                case Span s:
                    if (!Outside(s, c)) WriteInlines(w, s.Inlines, c);
                    break;
                case InlineUIContainer u:
                    if (c.From is not null && u.ElementStart.CompareTo(c.From) < 0) break;
                    if (c.To is not null && u.ElementEnd.CompareTo(c.To) > 0) break;
                    if (ImageOf(u.Child, c) is { } img)
                    {
                        WriteImage(w, img.Id, img.Width);
                        c.Plain.Append(' ');
                    }
                    break;
            }
        }
    }

    private static (string Id, double Width)? ImageOf(UIElement? child, Ctx c)
    {
        if (child is ImageBox ib) return (ib.ImageId, ib.DisplayWidth);
        // A picture pasted from another program (Word, a web page): taken into the store.
        if (child is Image { Source: BitmapSource bmp } im && c.Store is not null)
            return (c.Store.Import(bmp), double.IsNaN(im.Width) ? Math.Min(bmp.PixelWidth, 640) : im.Width);
        return null;
    }

    private static void WriteImage(Utf8JsonWriter w, string id, double width)
    {
        w.WriteStartObject();
        w.WriteString("img", id);
        w.WriteNumber("w", Math.Round(width));
        w.WriteEndObject();
    }

    private static void WriteRun(Utf8JsonWriter w, Run r, Ctx c)
    {
        var text = r.Text;
        if (c.From is not null || c.To is not null)
        {
            var s = r.ContentStart;
            int a = 0, b = text.Length;
            if (c.From is not null && c.From.CompareTo(s) > 0) a = Math.Min(text.Length, s.GetOffsetToPosition(c.From));
            if (c.To is not null && c.To.CompareTo(r.ContentEnd) < 0) b = Math.Max(0, Math.Min(text.Length, s.GetOffsetToPosition(c.To)));
            if (b <= a) return;
            text = text.Substring(a, b - a);
        }
        if (text.Length == 0) return;
        c.Plain.Append(text);
        w.WriteStartObject();
        w.WriteString("t", text);
        if (r.FontFamily.Source != c.Doc.FontFamily.Source) w.WriteString("f", r.FontFamily.Source);
        if (Math.Abs(r.FontSize - c.Doc.FontSize) > 0.01) w.WriteNumber("z", Math.Round(r.FontSize, 2));
        var weight = r.FontWeight.ToOpenTypeWeight();
        if (weight != 400) w.WriteNumber("w", weight);
        if (r.FontStyle != FontStyles.Normal) w.WriteNumber("i", 1);
        if (r.Foreground is SolidColorBrush fg && c.Doc.Foreground is SolidColorBrush df && fg.Color != df.Color)
            w.WriteString("c", Hex(fg.Color));
        if (Highlight(r) is { } hl) w.WriteString("h", Hex(hl));
        Decorations(r, out var u, out var st);
        if (u) w.WriteNumber("u", 1);
        if (st) w.WriteNumber("s", 1);
        if (r.BaselineAlignment == BaselineAlignment.Superscript) w.WriteNumber("sup", 1);
        else if (r.BaselineAlignment == BaselineAlignment.Subscript) w.WriteNumber("sub", 1);
        w.WriteEndObject();
    }

    private static Color? Highlight(Inline r)
    {
        for (DependencyObject? e = r; e is Inline; e = ((Inline)e).Parent)
            if (e.ReadLocalValue(TextElement.BackgroundProperty) is SolidColorBrush b && b.Color.A > 0)
                return b.Color;
        return null;
    }

    private static void Decorations(Inline r, out bool underline, out bool strike)
    {
        underline = strike = false;
        for (DependencyObject? e = r; e is Inline or Paragraph; e = (e as TextElement)?.Parent)
        {
            var decos = e is Inline i ? i.TextDecorations : ((Paragraph)e).TextDecorations;
            if (decos is null) continue;
            foreach (var d in decos)
            {
                if (d.Location == TextDecorationLocation.Underline) underline = true;
                else if (d.Location == TextDecorationLocation.Strikethrough) strike = true;
            }
        }
    }

    public static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Marker(TextMarkerStyle m) => m switch
    {
        TextMarkerStyle.Decimal => "decimal",
        TextMarkerStyle.Circle => "circle",
        TextMarkerStyle.Square => "square",
        TextMarkerStyle.LowerLatin => "lower-latin",
        TextMarkerStyle.UpperLatin => "upper-latin",
        TextMarkerStyle.LowerRoman => "lower-roman",
        TextMarkerStyle.UpperRoman => "upper-roman",
        TextMarkerStyle.Box => "box",
        TextMarkerStyle.None => "none",
        _ => "disc",
    };

    private static TextMarkerStyle Marker(string? m) => m switch
    {
        "decimal" => TextMarkerStyle.Decimal,
        "circle" => TextMarkerStyle.Circle,
        "square" => TextMarkerStyle.Square,
        "lower-latin" => TextMarkerStyle.LowerLatin,
        "upper-latin" => TextMarkerStyle.UpperLatin,
        "lower-roman" => TextMarkerStyle.LowerRoman,
        "upper-roman" => TextMarkerStyle.UpperRoman,
        "box" => TextMarkerStyle.Box,
        "none" => TextMarkerStyle.None,
        _ => TextMarkerStyle.Disc,
    };

    // ── reading ──

    /// <summary>Replaces the document's content with the note. Empty or damaged JSON gives one empty paragraph.</summary>
    public static void Read(FlowDocument doc, string json, string? fallbackText = null)
    {
        doc.Blocks.Clear();
        foreach (var b in ReadBlocks(json))
            doc.Blocks.Add(b);
        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(string.IsNullOrEmpty(fallbackText) ? new Paragraph() : PlainParagraphs(doc, fallbackText));
    }

    private static Block PlainParagraphs(FlowDocument doc, string text)
    {
        var sec = new Section();
        foreach (var line in text.Split('\n'))
            sec.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))));
        return sec;
    }

    public static List<Block> ReadBlocks(string json)
    {
        var list = new List<Block>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("b", out var blocks))
                ReadBlocks(blocks, list);
        }
        catch (JsonException)
        {
            /* damaged: caller falls back */
        }
        return list;
    }

    private static void ReadBlocks(JsonElement arr, ICollection<Block> into)
    {
        foreach (var b in arr.EnumerateArray())
        {
            if (b.TryGetProperty("p", out var inl))
            {
                var p = new Paragraph();
                if (b.TryGetProperty("a", out var a))
                    p.TextAlignment = a.GetString() switch { "c" => TextAlignment.Center, "r" => TextAlignment.Right, "j" => TextAlignment.Justify, _ => TextAlignment.Left };
                if (b.TryGetProperty("ml", out var ml)) p.Margin = new Thickness(ml.GetDouble(), 0, 0, 0);
                if (b.TryGetProperty("z", out var z)) p.FontSize = Math.Clamp(z.GetDouble(), 1, 1000);
                if (b.TryGetProperty("f", out var f) && f.GetString() is { Length: > 0 } fs) p.FontFamily = FontCache.Get(fs);
                ReadInlines(inl, p.Inlines);
                into.Add(p);
            }
            else if (b.TryGetProperty("l", out var l))
            {
                var list = new List { MarkerStyle = Marker(l.GetString()) };
                if (b.TryGetProperty("s", out var s)) list.StartIndex = Math.Max(1, s.GetInt32());
                if (b.TryGetProperty("i", out var items))
                    foreach (var item in items.EnumerateArray())
                    {
                        var li = new ListItem();
                        ReadBlocks(item, li.Blocks);
                        if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
                        list.ListItems.Add(li);
                    }
                into.Add(list);
            }
        }
    }

    private static void ReadInlines(JsonElement arr, InlineCollection into)
    {
        foreach (var i in arr.EnumerateArray())
            if (MakeInline(i) is { } inline)
                into.Add(inline);
    }

    /// <summary>One inline from its JSON (also used to paste a fragment at the caret).</summary>
    public static Inline? MakeInline(JsonElement i, Func<string, string>? imageId = null)
    {
        if (i.TryGetProperty("t", out var t))
        {
            var r = new Run(t.GetString() ?? "");
            ApplyRunProps(r, i);
            return r;
        }
        if (i.TryGetProperty("br", out _)) return new LineBreak();
        if (i.TryGetProperty("img", out var img) && img.GetString() is { Length: > 0 } id)
        {
            var w = i.TryGetProperty("w", out var ww) ? ww.GetDouble() : 0;
            return new InlineUIContainer(new ImageBox(imageId?.Invoke(id) ?? id, w)) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        return null;
    }

    public static void ApplyRunProps(Run r, JsonElement i)
    {
        if (i.TryGetProperty("f", out var f) && f.GetString() is { Length: > 0 } fs) r.FontFamily = FontCache.Get(fs);
        if (i.TryGetProperty("z", out var z)) r.FontSize = Math.Clamp(z.GetDouble(), 1, 1000);
        if (i.TryGetProperty("w", out var w)) r.FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(w.GetInt32(), 1, 999));
        if (i.TryGetProperty("i", out _)) r.FontStyle = FontStyles.Italic;
        if (i.TryGetProperty("c", out var c) && Brush(c.GetString()) is { } cb) r.Foreground = cb;
        if (i.TryGetProperty("h", out var h) && Brush(h.GetString()) is { } hb) r.Background = hb;
        var u = i.TryGetProperty("u", out _);
        var s = i.TryGetProperty("s", out _);
        if (u || s)
        {
            var decos = new TextDecorationCollection();
            if (u) foreach (var d in TextDecorations.Underline) decos.Add(d);
            if (s) foreach (var d in TextDecorations.Strikethrough) decos.Add(d);
            decos.Freeze();
            r.TextDecorations = decos;
        }
        if (i.TryGetProperty("sup", out _)) r.BaselineAlignment = BaselineAlignment.Superscript;
        else if (i.TryGetProperty("sub", out _)) r.BaselineAlignment = BaselineAlignment.Subscript;
    }

    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    public static SolidColorBrush? Brush(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        if (Brushes.TryGetValue(hex, out var b)) return b;
        try
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Brushes[hex] = b;
            return b;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A pasted fragment, flattened to its paragraphs (lists become their items' paragraphs).</summary>
    public static List<JsonElement> FragmentParagraphs(JsonDocument d)
    {
        var list = new List<JsonElement>();
        void Walk(JsonElement arr)
        {
            foreach (var b in arr.EnumerateArray())
            {
                if (b.TryGetProperty("p", out _)) list.Add(b);
                else if (b.TryGetProperty("i", out var items))
                    foreach (var item in items.EnumerateArray()) Walk(item);
            }
        }
        if (d.RootElement.TryGetProperty("b", out var blocks)) Walk(blocks);
        return list;
    }
}

/// <summary>FontFamily objects, made once per name.</summary>
public static class FontCache
{
    private static readonly Dictionary<string, FontFamily> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FontFamily Get(string name)
    {
        if (!Cache.TryGetValue(name, out var f))
            Cache[name] = f = new FontFamily(name);
        return f;
    }
}
