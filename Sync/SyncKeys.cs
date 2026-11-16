using System.Security.Cryptography;
using System.Text;

namespace pxdNotepad.Sync;

/// <summary>
/// Everything made from the user's sync key (32 random bytes, shown as 13 groups of 4 letters/digits). The key itself
/// never leaves the PC: the server gets a token made from it (to find and guard the vault) and records encrypted with
/// AES-256-GCM under a second key made from it. Image blobs are named by an HMAC of their bytes, so the server can't
/// tell what a picture is.
/// </summary>
public sealed class SyncKeys
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32: no I, L, O, U
    private readonly byte[] _enc;
    private readonly byte[] _blob;

    public byte[] Key { get; }
    /// <summary>The bearer token (64 hex).</summary>
    public string Auth { get; }
    /// <summary>Names this vault locally (first 24 hex of the token's SHA-256, as the server does).</summary>
    public string VaultId { get; }

    public SyncKeys(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("A sync key is 32 bytes.");
        Key = key;
        Auth = Convert.ToHexStringLower(HMACSHA256.HashData(key, "pxdNotepad auth v1"u8));
        _enc = HMACSHA256.HashData(key, "pxdNotepad enc v1"u8);
        _blob = HMACSHA256.HashData(key, "pxdNotepad blob v1"u8);
        VaultId = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Auth)))[..24];
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static string Format(byte[] key)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0, n = 0;
        void Put(int v)
        {
            if (n > 0 && n % 4 == 0) sb.Append('-');
            sb.Append(Alphabet[v]);
            n++;
        }
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                Put((buffer >> (bits - 5)) & 31);
                bits -= 5;
            }
        }
        if (bits > 0) Put((buffer << (5 - bits)) & 31);
        return sb.ToString();
    }

    /// <summary>Reads a typed or pasted key (any case, dashes and spaces ignored, O→0, I/L→1). Null if it isn't one.</summary>
    public static byte[]? Parse(string? text)
    {
        if (text is null) return null;
        var s = new StringBuilder();
        foreach (var ch in text.ToUpperInvariant())
        {
            var c = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            if (Alphabet.Contains(c)) s.Append(c);
            else if (!(c is '-' or ' ' or '\t' or '\r' or '\n')) return null;
        }
        if (s.Length != 52) return null;
        var bytes = new List<byte>(33);
        int buffer = 0, bits = 0;
        foreach (var c in s.ToString())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.Count >= 32 ? bytes.Take(32).ToArray() : null;
    }

    // record: 1 byte version | 12 nonce | 16 tag | ciphertext, base64; the item id is bound in as associated data
    public string Encrypt(string itemId, byte[] plain) => Convert.ToBase64String(Seal(_enc, Encoding.ASCII.GetBytes(itemId), plain));

    public byte[]? Decrypt(string itemId, string data)
    {
        try
        {
            return Open(_enc, Encoding.ASCII.GetBytes(itemId), Convert.FromBase64String(data));
        }
        catch
        {
            return null;
        }
    }

    public string BlobName(byte[] plain) => Convert.ToHexStringLower(HMACSHA256.HashData(_blob, plain));

    public byte[] EncryptBlob(string name, byte[] plain) => Seal(_enc, Encoding.ASCII.GetBytes("blob:" + name), plain);

    /// <summary>The image's bytes, or null when they don't decrypt or don't match their name.</summary>
    public byte[]? DecryptBlob(string name, byte[] data)
    {
        try
        {
            var plain = Open(_enc, Encoding.ASCII.GetBytes("blob:" + name), data);
            return plain is not null && BlobName(plain) == name ? plain : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Seal(byte[] key, byte[] aad, byte[] plain)
    {
        var outp = new byte[1 + 12 + 16 + plain.Length];
        outp[0] = 1;
        var nonce = outp.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, outp.AsSpan(29), outp.AsSpan(13, 16), aad);
        return outp;
    }

    private static byte[]? Open(byte[] key, byte[] aad, byte[] data)
    {
        if (data.Length < 29 || data[0] != 1) return null;
        var plain = new byte[data.Length - 29];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(data.AsSpan(1, 12), data.AsSpan(29), data.AsSpan(13, 16), plain, aad);
        return plain;
    }
}
