using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pxdNotepad.Data;

namespace pxdNotepad.Views;

/// <summary>One tag's icon on a note row or search result.</summary>
public sealed record TagChip(string Id, string Name, ImageSource Image);

/// <summary>
/// Tag icons: the built-in symbols (16×16 shapes drawn in the tag's colour; "S:" ones are outlines) and pictures taken
/// from .ico / .png files (shrunk to 48 px, stored with the tag, so they sync).
/// </summary>
public static class TagIcons
{
    public static readonly (string Key, string Name, string Data)[] Glyphs =
    [
        ("heart", "Heart", "M8,14 C3,10.5 1,8 1,5.3 C1,3.2 2.6,1.8 4.5,1.8 C6,1.8 7.2,2.7 8,4 C8.8,2.7 10,1.8 11.5,1.8 C13.4,1.8 15,3.2 15,5.3 C15,8 13,10.5 8,14 Z"),
        ("star", "Star", "M8,1 L10.1,5.6 L15,6.1 L11.3,9.4 L12.4,14.3 L8,11.8 L3.6,14.3 L4.7,9.4 L1,6.1 L5.9,5.6 Z"),
        ("flag", "Flag", "M3,1.5 L4.5,1.5 L4.5,15 L3,15 Z M4.5,2 L13.5,2 L11.5,5.5 L13.5,9 L4.5,9 Z"),
        ("bookmark", "Bookmark", "M4,1.5 L12,1.5 L12,15 L8,11.5 L4,15 Z"),
        ("pin", "Pin", "F0 M8,15.5 C8,15.5 2.5,9.8 2.5,6.2 C2.5,3.1 5,0.8 8,0.8 C11,0.8 13.5,3.1 13.5,6.2 C13.5,9.8 8,15.5 8,15.5 Z M8,4 A2.2,2.2 0 1 1 7.99,4 Z"),
        ("check", "Done", "F0 M8,1 A7,7 0 1 1 7.99,1 Z M6.8,11.3 L3.6,8.1 L4.8,6.9 L6.8,8.9 L11.2,4.5 L12.4,5.7 Z"),
        ("todo", "To do", "F0 M2,2 L14,2 L14,14 L2,14 Z M3.6,3.6 L12.4,3.6 L12.4,12.4 L3.6,12.4 Z"),
        ("alert", "Important", "F0 M8,1 L15.5,14.5 L0.5,14.5 Z M7.2,5.5 L8.8,5.5 L8.6,10 L7.4,10 Z M7.2,11.3 L8.8,11.3 L8.8,12.9 L7.2,12.9 Z"),
        ("info", "Info", "F0 M8,1 A7,7 0 1 1 7.99,1 Z M7.2,7 L8.8,7 L8.8,12 L7.2,12 Z M7.2,4 L8.8,4 L8.8,5.6 L7.2,5.6 Z"),
        ("fire", "Hot", "M8,15 C4.8,15 3,12.8 3,10.3 C3,7.2 5.5,5.6 6.2,2 C8.3,3.3 9.2,5.4 8.9,7.6 C9.7,7 10.2,6.1 10.3,5 C12.1,6.4 13,8.3 13,10.3 C13,12.8 11.2,15 8,15 Z"),
        ("bulb", "Idea", "M8,1 C10.9,1 13,3.1 13,5.8 C13,7.7 11.9,8.9 11,9.8 C10.5,10.3 10.3,10.8 10.3,11.5 L5.7,11.5 C5.7,10.8 5.5,10.3 5,9.8 C4.1,8.9 3,7.7 3,5.8 C3,3.1 5.1,1 8,1 Z M5.8,12.5 L10.2,12.5 L10.2,13.5 L5.8,13.5 Z M6.5,14.3 L9.5,14.3 L9.2,15.3 L6.8,15.3 Z"),
        ("lock", "Private", "M3,7 L13,7 L13,15 L3,15 Z M5,7 L5,5 C5,3.2 6.3,1.5 8,1.5 C9.7,1.5 11,3.2 11,5 L11,7 L9.6,7 L9.6,5 C9.6,4 8.9,2.9 8,2.9 C7.1,2.9 6.4,4 6.4,5 L6.4,7 Z"),
        ("key", "Key", "F0 M5,2.5 A4.5,4.5 0 1 1 4.99,2.5 Z M5,5.2 A1.8,1.8 0 1 1 4.99,5.2 Z M9.4,6 L15,6 L15,8.5 L13.5,8.5 L13.5,10.5 L12,10.5 L12,8.5 L9.4,8.5 Z"),
        ("home", "Home", "M8,1.5 L15,8 L13,8 L13,14.5 L9.5,14.5 L9.5,10.5 L6.5,10.5 L6.5,14.5 L3,14.5 L3,8 L1,8 Z"),
        ("work", "Work", "F0 M1.5,5 L14.5,5 L14.5,14 L1.5,14 Z M5.5,5 L5.5,3 L10.5,3 L10.5,5 L9.3,5 L9.3,4.2 L6.7,4.2 L6.7,5 Z"),
        ("person", "Person", "M8,1.5 A3.3,3.3 0 1 1 7.99,1.5 Z M1.8,15 C1.8,11.4 4.6,9.2 8,9.2 C11.4,9.2 14.2,11.4 14.2,15 Z"),
        ("money", "Money", "F0 M1,4 L15,4 L15,12 L1,12 Z M8,5.8 A2.2,2.2 0 1 1 7.99,5.8 Z"),
        ("cart", "Shopping", "M1,2 L3.3,2 L4,4 L15,4 L13.5,9.5 L5.5,9.5 L5.8,11 L13.5,11 L13.5,12.3 L4.7,12.3 L2.4,3.3 L1,3.3 Z M6,13 A1.2,1.2 0 1 1 5.99,13 Z M12,13 A1.2,1.2 0 1 1 11.99,13 Z"),
        ("calendar", "Date", "F0 M1.5,3 L14.5,3 L14.5,15 L1.5,15 Z M3,6.5 L13,6.5 L13,13.5 L3,13.5 Z M4,1 L5.5,1 L5.5,3 L4,3 Z M10.5,1 L12,1 L12,3 L10.5,3 Z"),
        ("clock", "Later", "F0 M8,1 A7,7 0 1 1 7.99,1 Z M7.2,3.5 L8.8,3.5 L8.8,7.5 L11.5,9.2 L10.7,10.5 L7.2,8.3 Z"),
        ("book", "Read", "M2,2 L7.3,2 C7.7,2 8,2.3 8,2.7 L8,14 C7.6,13.6 7.2,13.4 6.6,13.4 L2,13.4 Z M14,2 L8.7,2 C8.3,2 8,2.3 8,2.7 L8,14 C8.4,13.6 8.8,13.4 9.4,13.4 L14,13.4 Z"),
        ("code", "Code", "S:M5,4 L1.5,8 L5,12 M11,4 L14.5,8 L11,12 M9.5,2.5 L6.5,13.5"),
        ("mail", "Mail", "S:M1.5,3.5 L14.5,3.5 L14.5,12.5 L1.5,12.5 Z M1.5,3.5 L8,9 L14.5,3.5"),
        ("phone", "Phone", "F0 M4.5,1 L11.5,1 C12.3,1 12.8,1.5 12.8,2.3 L12.8,13.7 C12.8,14.5 12.3,15 11.5,15 L4.5,15 C3.7,15 3.2,14.5 3.2,13.7 L3.2,2.3 C3.2,1.5 3.7,1 4.5,1 Z M4.6,2.5 L11.4,2.5 L11.4,12.5 L4.6,12.5 Z"),
        ("camera", "Photo", "F0 M1.5,4.5 L5,4.5 L6.2,2.5 L9.8,2.5 L11,4.5 L14.5,4.5 L14.5,13.5 L1.5,13.5 Z M8,6 A3,3 0 1 1 7.99,6 Z"),
        ("cloud", "Cloud", "M4.5,13.5 L12,13.5 C13.9,13.5 15,12.2 15,10.6 C15,9 13.8,7.8 12.3,7.7 C11.9,5.4 10.1,4 8,4 C5.8,4 4.1,5.5 3.7,7.6 C2.1,7.9 1,9.1 1,10.6 C1,12.2 2.4,13.5 4.5,13.5 Z"),
        ("eye", "Watch", "F0 M1,8 C3,4.5 5.5,3 8,3 C10.5,3 13,4.5 15,8 C13,11.5 10.5,13 8,13 C5.5,13 3,11.5 1,8 Z M8,5.5 A2.5,2.5 0 1 1 7.99,5.5 Z"),
        ("music", "Music", "M5.2,3.2 L14,1 L14,11.3 C14,12.6 12.9,13.5 11.6,13.5 C10.4,13.5 9.4,12.7 9.4,11.6 C9.4,10.4 10.5,9.6 11.8,9.6 C12.1,9.6 12.3,9.6 12.5,9.7 L12.5,4.6 L6.7,6 L6.7,13 C6.7,14.3 5.6,15.2 4.3,15.2 C3.1,15.2 2.1,14.4 2.1,13.3 C2.1,12.1 3.2,11.3 4.5,11.3 C4.8,11.3 5,11.3 5.2,11.4 Z"),
        ("dot", "Dot", "M8,3 A5,5 0 1 1 7.99,3 Z"),
        ("square", "Square", "M3,3 L13,3 L13,13 L3,13 Z"),
    ];

    public static readonly string[] Colors =
    [
        "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#39C5CF",
        "#0A84FF", "#5856D6", "#AF52DE", "#FF2D55", "#A2845E", "#8E8E93",
    ];

    private static readonly Dictionary<string, (long Ver, ImageSource Image)> Cache = [];

    public static ImageSource For(Tag t)
    {
        if (Cache.TryGetValue(t.Id, out var c) && c.Ver == t.Ver) return c.Image;
        var img = t.Png is { Length: > 0 } png ? FromPng(png) : Glyph(t.Glyph, t.Color);
        Cache[t.Id] = (t.Ver, img);
        return img;
    }

    public static void Forget() => Cache.Clear();

    public static ImageSource Glyph(string key, string color)
    {
        var data = Glyphs.FirstOrDefault(g => g.Key == key).Data ?? Glyphs[^2].Data;
        var brush = Editor.DocSerializer.Brush(string.IsNullOrEmpty(color) ? "#39C5CF" : color) ?? Brushes.Gray;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        if (data.StartsWith("S:"))
            group.Children.Add(new GeometryDrawing(null, new Pen(brush, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(data[2..])));
        else
            group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(data)));
        group.Freeze();
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }

    private static ImageSource FromPng(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = ms;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch
        {
            return Glyph("dot", "#8E8E93");
        }
    }

    /// <summary>An icon file (.ico: the frame nearest 48 px; .png and others: as is) shrunk to fit 48×48, as PNG.</summary>
    public static byte[] ImportFile(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = dec.Frames
            .OrderBy(f => Math.Max(f.PixelWidth, f.PixelHeight) >= 48 ? Math.Max(f.PixelWidth, f.PixelHeight) - 48 : 1000 + 48 - Math.Max(f.PixelWidth, f.PixelHeight))
            .ThenByDescending(f => f.Format.BitsPerPixel)
            .First();
        var side = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (side > 48)
        {
            var s = 48.0 / side;
            var t = new TransformedBitmap(frame, new ScaleTransform(s, s));
            t.Freeze();
            frame = t;
        }
        return Images.ImageStore.Encode(frame, "image/png");
    }
}
