using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pxdNotepad;

/// <summary>pxd Notepad's own icon: a note page with cyan lines on the pxd dark tile. Also writes app.ico.</summary>
public static class BrandIcon
{
    private static DrawingImage? _image;

    public static DrawingImage Image => _image ??= Make();

    private static DrawingImage Make()
    {
        var g = new DrawingGroup();
        Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        var cyan = B("#39C5CF");
        g.Children.Add(new GeometryDrawing(B("#0D1117"), new Pen(cyan, 2.5), new RectangleGeometry(new Rect(2.5, 2.5, 59, 59), 14, 14)));
        g.Children.Add(new GeometryDrawing(B("#E6EDF3"), null, Geometry.Parse("M18,12 L38.5,12 L47,20.5 L47,52 L18,52 Z")));
        g.Children.Add(new GeometryDrawing(B("#8E99A8"), null, Geometry.Parse("M38.5,12 L38.5,20.5 L47,20.5 Z")));
        var line = new Pen(cyan, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        g.Children.Add(new GeometryDrawing(null, line, Geometry.Parse("M23.5,28 L41.5,28 M23.5,35.5 L41.5,35.5 M23.5,43 L34,43")));
        // keep the 64x64 frame even though the strokes sit inside it
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        g.Freeze();
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    public static BitmapSource Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawImage(Image, new Rect(0, 0, size, size));
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A multi-size .ico (PNG frames, which Windows reads since Vista).</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var pngs = sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(s)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToArray();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}
