using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pxdNotepad.Images;

/// <summary>
/// Edits to the picture itself (not drawn on top): blur, pixelate and cut out. They're applied to the original in the
/// order they were made, each in the picture's size at that moment, so they stay changeable. Blur and pixelate work
/// from block averages, so the detail underneath is gone from the saved copy, not just softened: zooming in or
/// sharpening can't bring text back.
/// </summary>
public static class PhotoEdits
{
    public static bool IsPhoto(Mark m) => m.Kind is "blur" or "pixel" or "cut";

    /// <summary>A cut takes out the rows its box spans ("rows") or the columns ("cols"); unset: by the box's shape.</summary>
    public static string CutDir(Mark m) => m.Dir ?? (Math.Abs(m.X2 - m.X1) >= Math.Abs(m.Y2 - m.Y1) ? "rows" : "cols");

    /// <summary>The picture with its blurs, pixelations and cut-outs, in order (the original itself when there are none).</summary>
    public static BitmapSource Apply(BitmapSource original, IEnumerable<Mark> marks)
    {
        var ops = marks.Where(IsPhoto).ToList();
        if (ops.Count == 0) return original;
        var px = Pixels(original, out var w, out var h);
        foreach (var m in ops)
        {
            var r = Clip(m, w, h);
            switch (m.Kind)
            {
                case "pixel" when r.W >= 2 && r.H >= 2:
                    Pixelate(px, w, r, Block(w, h, r, 90));
                    break;
                case "blur" when r.W >= 2 && r.H >= 2:
                    Blur(px, w, r, Block(w, h, r, 55));
                    break;
                case "cut":
                    (px, w, h) = Cut(px, w, h, r, CutDir(m));
                    break;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>The band a cut takes out, in the picture as it was when the cut was made.</summary>
    public static (int From, int To, bool Rows) Band(Mark cut, int w, int h)
    {
        var r = Clip(cut, w, h);
        return CutDir(cut) == "rows" ? (r.Y, r.Y + r.H, true) : (r.X, r.X + r.W, false);
    }

    /// <summary>A cut can't take (nearly) everything: at least 8 pixels stay each way.</summary>
    public static bool CutLeavesEnough(Mark cut, int w, int h)
    {
        var (from, to, rows) = Band(cut, w, h);
        return to - from >= 1 && (rows ? h : w) - (to - from) >= 8;
    }

    /// <summary>An arrow or box moved as the picture closes up after a cut (what was in the band ends up on its edge).</summary>
    public static void Follow(Mark m, int from, int to, bool rows)
    {
        double Move(double v) => v >= to ? v - (to - from) : v > from ? from : v;
        if (rows) { m.Y1 = Move(m.Y1); m.Y2 = Move(m.Y2); }
        else { m.X1 = Move(m.X1); m.X2 = Move(m.X2); }
    }

    private readonly record struct IRect(int X, int Y, int W, int H);

    private static IRect Clip(Mark m, int w, int h)
    {
        var x0 = (int)Math.Round(Math.Clamp(Math.Min(m.X1, m.X2), 0, w));
        var x1 = (int)Math.Round(Math.Clamp(Math.Max(m.X1, m.X2), 0, w));
        var y0 = (int)Math.Round(Math.Clamp(Math.Min(m.Y1, m.Y2), 0, h));
        var y1 = (int)Math.Round(Math.Clamp(Math.Max(m.Y1, m.Y2), 0, h));
        return new IRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Block size: scaled to the picture (so it looks the same at any size), and never so big a small area keeps its detail in one block row.</summary>
    private static int Block(int w, int h, IRect r, int across)
    {
        var b = Math.Max(4, (int)Math.Round(Math.Max(w, h) / (double)across));
        return Math.Max(2, Math.Min(b, Math.Max(2, Math.Min(r.W, r.H) / 2)));
    }

    private static byte[] Pixels(BitmapSource src, out int w, out int h)
    {
        BitmapSource p = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        w = p.PixelWidth;
        h = p.PixelHeight;
        var px = new byte[w * h * 4];
        p.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>Block averages over the area: [block row, block col] → B, G, R, A.</summary>
    private static double[,,] Averages(byte[] px, int w, IRect r, int b, out int gw, out int gh)
    {
        gw = (r.W + b - 1) / b;
        gh = (r.H + b - 1) / b;
        var avg = new double[gh, gw, 4];
        for (var by = 0; by < gh; by++)
            for (var bx = 0; bx < gw; bx++)
            {
                int x0 = r.X + bx * b, y0 = r.Y + by * b, x1 = Math.Min(x0 + b, r.X + r.W), y1 = Math.Min(y0 + b, r.Y + r.H);
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                var n = 0;
                for (var y = y0; y < y1; y++)
                    for (int x = x0, i = (y * w + x0) * 4; x < x1; x++, i += 4, n++)
                    {
                        s0 += px[i]; s1 += px[i + 1]; s2 += px[i + 2]; s3 += px[i + 3];
                    }
                if (n == 0) continue;
                avg[by, bx, 0] = s0 / n; avg[by, bx, 1] = s1 / n; avg[by, bx, 2] = s2 / n; avg[by, bx, 3] = s3 / n;
            }
        return avg;
    }

    /// <summary>Squares of one colour each (the average of what was there).</summary>
    private static void Pixelate(byte[] px, int w, IRect r, int b)
    {
        var avg = Averages(px, w, r, b, out _, out _);
        for (var y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X, i = (y * w + r.X) * 4; x < r.X + r.W; x++, i += 4)
            {
                int by = (y - r.Y) / b, bx = (x - r.X) / b;
                for (var c = 0; c < 4; c++) px[i + c] = (byte)Math.Round(avg[by, bx, c]);
            }
    }

    /// <summary>A smooth blur made only from the block averages (blended between block centres), so no fine detail survives.</summary>
    private static void Blur(byte[] px, int w, IRect r, int b)
    {
        var avg = Averages(px, w, r, b, out var gw, out var gh);
        for (var y = r.Y; y < r.Y + r.H; y++)
        {
            var fy = Math.Clamp((y - r.Y + 0.5) / b - 0.5, 0, gh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, gh - 1);
            var ty = fy - y0;
            for (int x = r.X, i = (y * w + r.X) * 4; x < r.X + r.W; x++, i += 4)
            {
                var fx = Math.Clamp((x - r.X + 0.5) / b - 0.5, 0, gw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, gw - 1);
                var tx = fx - x0;
                for (var c = 0; c < 4; c++)
                {
                    var top = avg[y0, x0, c] + (avg[y0, x1, c] - avg[y0, x0, c]) * tx;
                    var bottom = avg[y1, x0, c] + (avg[y1, x1, c] - avg[y1, x0, c]) * tx;
                    px[i + c] = (byte)Math.Round(top + (bottom - top) * ty);
                }
            }
        }
    }

    /// <summary>Takes the band out and joins what's either side of it.</summary>
    private static (byte[], int, int) Cut(byte[] px, int w, int h, IRect r, string dir)
    {
        if (dir == "rows")
        {
            int from = r.Y, to = r.Y + r.H, nh = h - (to - from);
            if (to - from < 1 || nh < 8) return (px, w, h);
            var o = new byte[w * nh * 4];
            Buffer.BlockCopy(px, 0, o, 0, from * w * 4);
            Buffer.BlockCopy(px, to * w * 4, o, from * w * 4, (h - to) * w * 4);
            return (o, w, nh);
        }
        else
        {
            int from = r.X, to = r.X + r.W, nw = w - (to - from);
            if (to - from < 1 || nw < 8) return (px, w, h);
            var o = new byte[nw * h * 4];
            for (var y = 0; y < h; y++)
            {
                Buffer.BlockCopy(px, y * w * 4, o, y * nw * 4, from * 4);
                Buffer.BlockCopy(px, (y * w + to) * 4, o, (y * nw + from) * 4, (w - to) * 4);
            }
            return (o, nw, h);
        }
    }
}
