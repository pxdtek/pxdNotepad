using System.Buffers.Binary;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pxdNotepad.Images;

/// <summary>
/// A small lossless PNG writer that packs pictures tighter than WPF's own encoder: it picks the best PNG filter
/// for each row and compresses at the smallest setting, and leaves out the alpha channel when nothing is see-through.
/// Same pixels, fewer bytes on disk and through sync.
/// </summary>
public static class Png
{
    public static byte[] Encode(BitmapSource src)
    {
        BitmapSource bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var opaque = true;
        for (var i = 3; i < px.Length; i += 4)
            if (px[i] != 255) { opaque = false; break; }
        var bpp = opaque ? 3 : 4;
        var stride = w * bpp;

        // rows in PNG order (RGB / RGBA)
        var raw = new byte[stride * h];
        for (int y = 0, s = 0, d = 0; y < h; y++)
            for (var x = 0; x < w; x++, s += 4)
            {
                raw[d++] = px[s + 2];
                raw[d++] = px[s + 1];
                raw[d++] = px[s];
                if (!opaque) raw[d++] = px[s + 3];
            }

        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var prev = new byte[stride];
            var cur = new byte[stride];
            var trial = new byte[5][];
            for (var f = 0; f < 5; f++) trial[f] = new byte[stride];
            for (var y = 0; y < h; y++)
            {
                Buffer.BlockCopy(raw, y * stride, cur, 0, stride);
                // the usual choice: the filter whose output bytes (as signed) add up smallest
                var best = 0;
                var bestSum = long.MaxValue;
                for (var f = 0; f < 5; f++)
                {
                    var o = trial[f];
                    long sum = 0;
                    for (var i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        var v = (byte)(f switch
                        {
                            0 => cur[i],
                            1 => cur[i] - a,
                            2 => cur[i] - b,
                            3 => cur[i] - ((a + b) >> 1),
                            _ => cur[i] - Paeth(a, b, c),
                        });
                        o[i] = v;
                        sum += v < 128 ? v : 256 - v;
                    }
                    if (sum < bestSum) { bestSum = sum; best = f; }
                }
                z.WriteByte((byte)best);
                z.Write(trial[best], 0, stride);
                (prev, cur) = (cur, prev);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;                          // 8 bits per channel
        ihdr[9] = (byte)(opaque ? 2 : 6);     // RGB or RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, data.Length);
        s.Write(n);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(n, crc);
        s.Write(n);
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
