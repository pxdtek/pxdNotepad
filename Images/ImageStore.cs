using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using pxdNotepad.Data;

namespace pxdNotepad.Images;

public sealed record ImageInfo(string Id, string Mime, int Width, int Height, string Marks, bool HasFlat, bool Missing);

/// <summary>
/// Images live in the database, not in the note's text: the note just names one (and the size it's shown at). Each
/// keeps its original bytes plus, once marked up, the marks (arrows, boxes) and a flattened copy that the note shows.
/// </summary>
public sealed class ImageStore
{
    public const int MaxSide = 4096;
    private readonly SqliteConnection _c;
    // Decoded pictures, most recently used last, kept to a memory budget (a 4000 px photo is 64 MB decoded at full
    // size, so each is decoded only as big as it's shown, and the oldest go first).
    private sealed record Decoded(BitmapSource Bmp, bool Full, long Bytes);
    private readonly Dictionary<string, LinkedListNode<(string Id, Decoded D)>> _cache = [];
    private readonly LinkedList<(string Id, Decoded D)> _lru = new();
    private long _cacheBytes;
    public const long CacheBudget = 96L * 1024 * 1024;
    public const int MaxDecode = 2560;

    /// <summary>Memory the decoded pictures take now (the self-test checks the budget holds).</summary>
    public long CacheBytes => _cacheBytes;

    public ImageStore(SqliteConnection c) => _c = c;

    /// <summary>An image's marks or bytes changed (an edit here, or sync brought a new copy).</summary>
    public event Action<string>? ImageChanged;
    public event Action? Changed;

    public string Import(byte[] data)
    {
        var (bytes, mime, w, h) = Normalize(data);
        return Insert(bytes, mime, w, h);
    }

    public string Import(BitmapSource bmp)
    {
        var src = Downscale(bmp);
        var bytes = Encode(src, "image/png");
        return Insert(bytes, "image/png", src.PixelWidth, src.PixelHeight);
    }

    private string Insert(byte[] bytes, string mime, int w, int h)
    {
        var id = Db.NewId();
        Db.Exec(_c, "INSERT INTO images(id,mime,orig,w,h,updated) VALUES($id,$m,$o,$w,$h,$u)",
            ("$id", id), ("$m", mime), ("$o", bytes), ("$w", w), ("$h", h), ("$u", Db.Now()));
        Changed?.Invoke();
        return id;
    }

    public ImageInfo? Info(string id)
    {
        using var cmd = Db.Cmd(_c, "SELECT mime, w, h, marks, flat IS NOT NULL, orig IS NULL FROM images WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ImageInfo(id, r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5)) : null;
    }

    public byte[]? Original(string id) => Db.Scalar(_c, "SELECT orig FROM images WHERE id=$id", ("$id", id)) as byte[];

    /// <summary>
    /// What the note shows: the marked-up copy if there is one, else the original. Null when not here (yet).
    /// <paramref name="width"/> is the pixels it's shown at (0: as big as it goes, up to 2560, for copying out).
    /// </summary>
    public BitmapSource? Bitmap(string id, int width = 0)
    {
        if (_cache.TryGetValue(id, out var node))
        {
            var d = node.Value.D;
            if (d.Full || (width > 0 && d.Bmp.PixelWidth >= width))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                return d.Bmp;
            }
        }
        var bytes = Db.Scalar(_c, "SELECT COALESCE(flat, orig) FROM images WHERE id=$id", ("$id", id)) as byte[];
        if (bytes is null) return null;
        BitmapSource b;
        bool full;
        try
        {
            // a little bigger than asked (rounded up), so a small resize doesn't need a new decode
            var want = width <= 0 ? MaxDecode : Math.Min(MaxDecode, (width + 127) / 128 * 128);
            b = Decode(bytes, want, out full);
        }
        catch
        {
            return null;
        }
        if (width <= 0 && !full) return b;   // a one-off big copy (the clipboard): not kept
        Keep(id, new Decoded(b, full, (long)b.PixelWidth * b.PixelHeight * 4));
        return b;
    }

    private void Keep(string id, Decoded d)
    {
        Drop(id);
        _cache[id] = _lru.AddLast((id, d));
        _cacheBytes += d.Bytes;
        while (_cacheBytes > CacheBudget && _lru.First is { } oldest && oldest.Value.Id != id) Drop(oldest.Value.Id);
    }

    private void Drop(string id)
    {
        if (!_cache.Remove(id, out var node)) return;
        _lru.Remove(node);
        _cacheBytes -= node.Value.D.Bytes;
    }

    /// <summary>Saves an edit: the marks (JSON) and the flattened picture, or no marks to go back to the original.</summary>
    public void SaveEdit(string id, string marks, byte[]? flat, int width = 0, int height = 0)
    {
        // w, h are the size the note shows (a cut-out changes it); not given: left as they were
        Db.Exec(_c, "UPDATE images SET marks=$m, flat=$f, flat_name=NULL, w=CASE WHEN $w > 0 THEN $w ELSE w END, h=CASE WHEN $h > 0 THEN $h ELSE h END, updated=$u, dirty=1, ver=ver+1 WHERE id=$id",
            ("$id", id), ("$m", marks), ("$f", flat), ("$w", width), ("$h", height), ("$u", Db.Now()));
        Forget(id);
        Changed?.Invoke();
    }

    public void Forget(string id)
    {
        Drop(id);
        ImageChanged?.Invoke(id);
    }

    /// <summary>Returns a copy of an image under a new id (so pasting one into a second note can be edited apart).</summary>
    public string? Duplicate(string id)
    {
        if (Info(id) is null) return null;
        var nid = Db.NewId();
        Db.Exec(_c, "INSERT INTO images(id,mime,orig,flat,marks,w,h,updated) SELECT $n,mime,orig,flat,marks,w,h,$u FROM images WHERE id=$id",
            ("$n", nid), ("$id", id), ("$u", Db.Now()));
        Changed?.Invoke();
        return nid;
    }

    // ── bytes ──

    public static BitmapSource Decode(byte[] bytes, int maxWidth = MaxDecode) => Decode(bytes, maxWidth, out _);

    /// <summary>Decodes at most <paramref name="maxWidth"/> wide; <paramref name="full"/> says it's at its own full size.</summary>
    public static BitmapSource Decode(byte[] bytes, int maxWidth, out bool full)
    {
        using var ms = new MemoryStream(bytes);
        var probe = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        // Its size is read before rewinding: reading it later moves the stream on, and a JPEG then won't decode.
        var natural = probe.PixelWidth;
        full = natural <= maxWidth;
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (natural > maxWidth) img.DecodePixelWidth = maxWidth;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Keeps PNG and JPEG bytes as they are (unless huge); anything else becomes PNG.</summary>
    public static (byte[] Bytes, string Mime, int W, int H) Normalize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = dec.Frames[0];
        var mime = dec is JpegBitmapDecoder ? "image/jpeg" : "image/png";
        var keep = dec is PngBitmapDecoder or JpegBitmapDecoder;
        if (keep && Math.Max(frame.PixelWidth, frame.PixelHeight) <= MaxSide && !HasOrientation(frame))
            return (data, mime, frame.PixelWidth, frame.PixelHeight);
        BitmapSource src = Orient(frame);
        src = Downscale(src);
        return (Encode(src, mime), mime, src.PixelWidth, src.PixelHeight);
    }

    private static bool HasOrientation(BitmapFrame f) => Orientation(f) > 1;

    private static int Orientation(BitmapFrame f)
    {
        try
        {
            if (f.Metadata is BitmapMetadata m && m.GetQuery("/app1/ifd/{ushort=274}") is ushort o) return o;
        }
        catch
        {
            /* no EXIF */
        }
        return 1;
    }

    /// <summary>Phone photos: turned the way the camera says.</summary>
    private static BitmapSource Orient(BitmapFrame f)
    {
        var angle = Orientation(f) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle == 0) return f;
        var t = new TransformedBitmap(f, new RotateTransform(angle));
        t.Freeze();
        return t;
    }

    public static BitmapSource Downscale(BitmapSource src)
    {
        var side = Math.Max(src.PixelWidth, src.PixelHeight);
        if (side <= MaxSide) return src;
        var s = (double)MaxSide / side;
        var t = new TransformedBitmap(src, new ScaleTransform(s, s));
        t.Freeze();
        return t;
    }

    public static byte[] Encode(BitmapSource src, string mime)
    {
        if (mime != "image/jpeg")
        {
            try { return Png.Encode(src); }   // same pixels, ~15% smaller than WPF's PNG
            catch { /* an odd pixel format: WPF's encoder below */ }
        }
        BitmapEncoder enc = mime == "image/jpeg" ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
