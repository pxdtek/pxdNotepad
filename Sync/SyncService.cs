using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using pxdNotepad.Data;

namespace pxdNotepad.Sync;

public enum SyncState { Off, Syncing, Ok, Offline, Error }

public sealed record SyncStatus(SyncState State, string Message, DateTime? LastOk);

public sealed class SyncChanges
{
    public HashSet<string> Notes { get; } = [];
    public HashSet<string> Images { get; } = [];
    public bool Categories { get; set; }
    public bool Tags { get; set; }
    public bool Any => Notes.Count > 0 || Images.Count > 0 || Categories || Tags;
}

/// <summary>
/// Keeps this PC's notes in step with the vault on the sync server (pxdtek.com/n by default): pull what changed since
/// last time, then push what changed here. A record changed on two PCs keeps the newer edit (per note, not per
/// letter). Runs on its own thread and its own database connection; the UI hears about changes through
/// <see cref="RemoteChanged"/> and <see cref="StatusChanged"/> (raised off the UI thread).
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly string _dbPath;
    private readonly Func<(string Server, byte[]? Key)> _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _timer;
    private static readonly HttpClient Http = MakeClient();
    private volatile bool _disposed;

    public SyncStatus Status { get; private set; } = new(SyncState.Off, "Sync is off", null);
    public event Action<SyncStatus>? StatusChanged;
    public event Action<SyncChanges>? RemoteChanged;

    public SyncService(string dbPath, Func<(string Server, byte[]? Key)> config)
    {
        _dbPath = dbPath;
        _config = config;
        _timer = new Timer(_ => _ = SyncNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("pxdNotepad/1.0");
        return h;
    }

    /// <summary>Sync after <paramref name="delay"/> (restarting the wait if already waiting).</summary>
    public void Kick(TimeSpan delay)
    {
        if (!_disposed) _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void SetStatus(SyncState s, string msg)
    {
        Status = new SyncStatus(s, msg, s == SyncState.Ok ? DateTime.Now : Status.LastOk);
        StatusChanged?.Invoke(Status);
    }

    public async Task<SyncStatus> SyncNowAsync()
    {
        if (_disposed) return Status;
        var (server, key) = _config();
        if (key is null) { SetStatus(SyncState.Off, "Sync is off"); return Status; }
        if (!await _gate.WaitAsync(0)) return Status;   // one already running
        try
        {
            SetStatus(SyncState.Syncing, "Syncing…");
            var keys = new SyncKeys(key);
            using var c = Db.Open(_dbPath);
            var changes = new SyncChanges();
            await RunAsync(c, server.TrimEnd('/'), keys, changes);
            if (changes.Any) RemoteChanged?.Invoke(changes);
            SetStatus(SyncState.Ok, "Synced");
        }
        catch (SyncException e)
        {
            SetStatus(e.Offline ? SyncState.Offline : SyncState.Error, e.Message);
        }
        catch (HttpRequestException)
        {
            SetStatus(SyncState.Offline, "Can't reach the sync server");
        }
        catch (TaskCanceledException)
        {
            SetStatus(SyncState.Offline, "The sync server didn't answer");
        }
        catch (Exception e)
        {
            SetStatus(SyncState.Error, "Sync failed: " + e.Message);
        }
        finally
        {
            _gate.Release();
            if (!_disposed) Kick(TimeSpan.FromMinutes(2));   // the regular check
        }
        return Status;
    }

    private sealed class SyncException(string message, bool offline = false) : Exception(message)
    {
        public bool Offline { get; } = offline;
    }

    // ── one run ──

    private async Task RunAsync(SqliteConnection c, string server, SyncKeys keys, SyncChanges changes)
    {
        // A different vault than last time (new key, joined another): everything here gets sent there.
        if (Db.Meta(c, "sync.vault") != keys.VaultId)
        {
            using var tx = c.BeginTransaction();
            foreach (var t in new[] { "notes", "categories", "images", "tags" })
                Db.Exec(c, $"UPDATE {t} SET srv=0, dirty=1");
            Db.Exec(c, "UPDATE images SET orig_name=NULL, flat_name=NULL");
            Db.SetMeta(c, "sync.seq", "0");
            Db.SetMeta(c, "sync.vault", keys.VaultId);
            tx.Commit();
        }

        await PullAsync(c, server, keys, changes);
        if (changes.Tags || changes.Notes.Count > 0) TagStore.RefreshTagText(c, null);
        for (var round = 0; round < 3; round++)
            if (!await PushAsync(c, server, keys, changes))
                break;
        await FetchMissingBlobsAsync(c, server, keys, changes);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, SyncKeys keys, HttpContent? content = null, string? invite = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keys.Auth);
        if (invite is not null) req.Headers.Add("x-pxd-invite", invite);
        var res = await Http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new SyncException("This sync key has no vault on the server");
        if (res.StatusCode == (HttpStatusCode)507) throw new SyncException("The sync server is full");
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new SyncException("The sync server asked to wait a bit", true);
        if ((int)res.StatusCode >= 500) throw new SyncException("The sync server had a problem", true);
        return res;
    }

    private async Task PullAsync(SqliteConnection c, string server, SyncKeys keys, SyncChanges changes)
    {
        var since = long.TryParse(Db.Meta(c, "sync.seq"), out var s) ? s : 0;
        for (var page = 0; page < 1000; page++)
        {
            using var res = await SendAsync(HttpMethod.Get, $"{server}/n/v1/changes?since={since}", keys);
            if (!res.IsSuccessStatusCode) throw new SyncException($"Sync server said {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            using (var tx = c.BeginTransaction())
            {
                foreach (var it in root.GetProperty("items").EnumerateArray())
                    Apply(c, keys, it.GetProperty("id").GetString()!, it.GetProperty("seq").GetInt64(), it.GetProperty("data").GetString()!, changes);
                since = root.GetProperty("seq").GetInt64();
                Db.SetMeta(c, "sync.seq", since.ToString());
                tx.Commit();
            }
            if (!root.GetProperty("more").GetBoolean()) break;
        }
    }

    /// <summary>One record from the server, merged in: the newer edit wins.</summary>
    private static void Apply(SqliteConnection c, SyncKeys keys, string itemId, long seq, string data, SyncChanges changes)
    {
        var plain = keys.Decrypt(itemId, data);
        if (plain is null) return;   // not ours (or damaged): leave it
        using var d = JsonDocument.Parse(plain);
        var r = d.RootElement;
        var kind = r.GetProperty("k").GetString();
        var id = r.GetProperty("id").GetString()!;
        var remoteUpdated = r.GetProperty("updated").GetInt64();
        var table = kind switch { "n" => "notes", "c" => "categories", "i" => "images", "t" => "tags", _ => null };
        if (table is null || itemId != kind + "_" + id) return;

        long? localUpdated = null;
        using (var cmd = Db.Cmd(c, $"SELECT updated FROM {table} WHERE id=$id", ("$id", id)))
            if (cmd.ExecuteScalar() is long lu) localUpdated = lu;

        if (localUpdated is { } l && l >= remoteUpdated)
        {
            // Ours is as new or newer: keep it, and send it again if it's newer (against the server's latest).
            Db.Exec(c, $"UPDATE {table} SET srv=$s, dirty=CASE WHEN updated>$ru THEN 1 ELSE dirty END WHERE id=$id", ("$id", id), ("$s", seq), ("$ru", remoteUpdated));
            return;
        }

        switch (kind)
        {
            case "n":
                Db.Exec(c, """
                    INSERT INTO notes(id,cat,title,body,doc,created,updated,trashed,purged,tags,dirty,ver,srv)
                    VALUES($id,$cat,$t,$b,$d,$cr,$u,$tr,$p,$tags,0,0,$s)
                    ON CONFLICT(id) DO UPDATE SET cat=excluded.cat, title=excluded.title, body=excluded.body, doc=excluded.doc, tags=excluded.tags,
                      created=excluded.created, updated=excluded.updated, trashed=excluded.trashed, purged=excluded.purged,
                      dirty=0, ver=notes.ver+1, srv=excluded.srv
                    """,
                    ("$id", id), ("$cat", Str(r, "cat")), ("$t", Str(r, "title") ?? ""), ("$b", Str(r, "body") ?? ""), ("$d", Str(r, "doc") ?? ""),
                    ("$cr", r.TryGetProperty("created", out var cr) ? cr.GetInt64() : remoteUpdated), ("$u", remoteUpdated),
                    ("$tr", r.TryGetProperty("trashed", out var tr) ? tr.GetInt64() : 0L), ("$p", r.TryGetProperty("purged", out var p) && p.GetBoolean() ? 1 : 0), ("$s", seq),
                    ("$tags", Str(r, "tags") ?? ""));
                changes.Notes.Add(id);
                break;
            case "c":
                Db.Exec(c, """
                    INSERT INTO categories(id,parent,name,sort,updated,deleted,tags,dirty,ver,srv) VALUES($id,$p,$n,$so,$u,$del,$tags,0,0,$s)
                    ON CONFLICT(id) DO UPDATE SET parent=excluded.parent, name=excluded.name, sort=excluded.sort, updated=excluded.updated, tags=excluded.tags,
                      deleted=excluded.deleted, dirty=0, ver=categories.ver+1, srv=excluded.srv
                    """,
                    ("$id", id), ("$p", Str(r, "parent")), ("$n", Str(r, "name") ?? "Category"), ("$so", r.TryGetProperty("sort", out var so) ? so.GetDouble() : 0.0),
                    ("$u", remoteUpdated), ("$del", r.TryGetProperty("deleted", out var del) && del.GetBoolean() ? 1 : 0), ("$s", seq),
                    ("$tags", Str(r, "tags") ?? ""));
                changes.Categories = true;
                break;
            case "t":
                byte[]? png = null;
                if (Str(r, "png") is { Length: > 0 } b64) try { png = Convert.FromBase64String(b64); } catch { /* bad picture: symbol only */ }
                Db.Exec(c, """
                    INSERT INTO tags(id,name,glyph,color,png,sort,updated,deleted,dirty,ver,srv) VALUES($id,$n,$g,$co,$png,$so,$u,$del,0,0,$s)
                    ON CONFLICT(id) DO UPDATE SET name=excluded.name, glyph=excluded.glyph, color=excluded.color, png=excluded.png, sort=excluded.sort,
                      updated=excluded.updated, deleted=excluded.deleted, dirty=0, ver=tags.ver+1, srv=excluded.srv
                    """,
                    ("$id", id), ("$n", Str(r, "name") ?? "Tag"), ("$g", Str(r, "glyph") ?? ""), ("$co", Str(r, "color") ?? ""), ("$png", png),
                    ("$so", r.TryGetProperty("sort", out var tso) ? tso.GetDouble() : 0.0), ("$u", remoteUpdated),
                    ("$del", r.TryGetProperty("deleted", out var tdel) && tdel.GetBoolean() ? 1 : 0), ("$s", seq));
                changes.Tags = true;
                break;
            case "i":
                var orig = Str(r, "orig");
                var flat = Str(r, "flat");
                Db.Exec(c, """
                    INSERT INTO images(id,mime,orig,flat,marks,w,h,updated,orig_name,flat_name,dirty,ver,srv)
                    VALUES($id,$m,NULL,NULL,$mk,$w,$h,$u,$on,$fn,0,0,$s)
                    ON CONFLICT(id) DO UPDATE SET mime=excluded.mime, marks=excluded.marks, w=excluded.w, h=excluded.h, updated=excluded.updated,
                      orig=CASE WHEN images.orig_name IS excluded.orig_name THEN images.orig ELSE NULL END,
                      flat=CASE WHEN images.flat_name IS excluded.flat_name THEN images.flat ELSE NULL END,
                      orig_name=excluded.orig_name, flat_name=excluded.flat_name, dirty=0, ver=images.ver+1, srv=excluded.srv
                    """,
                    ("$id", id), ("$m", Str(r, "mime") ?? "image/png"), ("$mk", Str(r, "marks") ?? ""), ("$w", r.GetProperty("w").GetInt32()), ("$h", r.GetProperty("h").GetInt32()),
                    ("$u", remoteUpdated), ("$on", orig), ("$fn", flat), ("$s", seq));
                changes.Images.Add(id);
                break;
        }
    }

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record Outgoing(string Table, string Id, long Ver, string ItemId, long Base, string Data);

    /// <summary>Sends what changed here. True when some came back as conflicts (worth another round).</summary>
    private async Task<bool> PushAsync(SqliteConnection c, string server, SyncKeys keys, SyncChanges changes)
    {
        await UploadImageBlobsAsync(c, server, keys);
        var outgoing = new List<Outgoing>();

        using (var cmd = Db.Cmd(c, "SELECT id, ver, srv, cat, title, body, doc, created, updated, trashed, purged, tags FROM notes WHERE dirty=1"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetString(0);
                var rec = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["k"] = "n", ["id"] = id, ["cat"] = r.IsDBNull(3) ? null : r.GetString(3), ["title"] = r.GetString(4), ["body"] = r.GetString(5),
                    ["doc"] = r.GetString(6), ["created"] = r.GetInt64(7), ["updated"] = r.GetInt64(8), ["trashed"] = r.GetInt64(9), ["purged"] = r.GetInt64(10) != 0,
                    ["tags"] = r.GetString(11),
                });
                outgoing.Add(new Outgoing("notes", id, r.GetInt64(1), "n_" + id, r.GetInt64(2), keys.Encrypt("n_" + id, rec)));
            }
        using (var cmd = Db.Cmd(c, "SELECT id, ver, srv, parent, name, sort, updated, deleted, tags FROM categories WHERE dirty=1"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetString(0);
                var rec = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["k"] = "c", ["id"] = id, ["parent"] = r.IsDBNull(3) ? null : r.GetString(3), ["name"] = r.GetString(4), ["sort"] = r.GetDouble(5),
                    ["updated"] = r.GetInt64(6), ["deleted"] = r.GetInt64(7) != 0, ["tags"] = r.GetString(8),
                });
                outgoing.Add(new Outgoing("categories", id, r.GetInt64(1), "c_" + id, r.GetInt64(2), keys.Encrypt("c_" + id, rec)));
            }
        using (var cmd = Db.Cmd(c, "SELECT id, ver, srv, name, glyph, color, png, sort, updated, deleted FROM tags WHERE dirty=1"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetString(0);
                var rec = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["k"] = "t", ["id"] = id, ["name"] = r.GetString(3), ["glyph"] = r.GetString(4), ["color"] = r.GetString(5),
                    ["png"] = r.IsDBNull(6) ? null : Convert.ToBase64String((byte[])r[6]), ["sort"] = r.GetDouble(7),
                    ["updated"] = r.GetInt64(8), ["deleted"] = r.GetInt64(9) != 0,
                });
                outgoing.Add(new Outgoing("tags", id, r.GetInt64(1), "t_" + id, r.GetInt64(2), keys.Encrypt("t_" + id, rec)));
            }
        using (var cmd = Db.Cmd(c, "SELECT id, ver, srv, mime, w, h, marks, updated, orig_name, flat_name FROM images WHERE dirty=1 AND orig_name IS NOT NULL AND (flat IS NULL OR flat_name IS NOT NULL)"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var id = r.GetString(0);
                var rec = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
                {
                    ["k"] = "i", ["id"] = id, ["mime"] = r.GetString(3), ["w"] = r.GetInt32(4), ["h"] = r.GetInt32(5), ["marks"] = r.GetString(6),
                    ["updated"] = r.GetInt64(7), ["orig"] = r.GetString(8), ["flat"] = r.IsDBNull(9) ? null : r.GetString(9),
                });
                outgoing.Add(new Outgoing("images", id, r.GetInt64(1), "i_" + id, r.GetInt64(2), keys.Encrypt("i_" + id, rec)));
            }
        if (outgoing.Count == 0) return false;

        // Images first, so a note never arrives on another PC before the pictures it shows.
        outgoing = [.. outgoing.OrderBy(o => o.Table switch { "images" => 0, "tags" => 1, "categories" => 2, _ => 3 })];
        var conflicted = false;
        var batch = new List<Outgoing>();
        long size = 0;
        async Task Flush()
        {
            if (batch.Count == 0) return;
            var body = JsonSerializer.Serialize(new { items = batch.Select(o => new { id = o.ItemId, @base = o.Base, data = o.Data }) });
            using var res = await SendAsync(HttpMethod.Post, $"{server}/n/v1/changes", keys, new StringContent(body, Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) throw new SyncException($"Sync server said {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var byItem = batch.ToDictionary(o => o.ItemId);
            using var tx = c.BeginTransaction();
            foreach (var a in doc.RootElement.GetProperty("accepted").EnumerateArray())
                if (byItem.TryGetValue(a.GetProperty("id").GetString()!, out var o))
                    Db.Exec(c, $"UPDATE {o.Table} SET srv=$s, dirty=CASE WHEN ver=$v THEN 0 ELSE dirty END WHERE id=$id",
                        ("$id", o.Id), ("$s", a.GetProperty("seq").GetInt64()), ("$v", o.Ver));
            foreach (var x in doc.RootElement.GetProperty("conflicts").EnumerateArray())
            {
                conflicted = true;
                Apply(c, keys, x.GetProperty("id").GetString()!, x.GetProperty("seq").GetInt64(), x.GetProperty("data").GetString()!, changes);
            }
            tx.Commit();
            batch.Clear();
            size = 0;
        }
        foreach (var o in outgoing)
        {
            if (batch.Count >= 200 || size + o.Data.Length > 3_000_000) await Flush();
            batch.Add(o);
            size += o.Data.Length + 100;
        }
        await Flush();
        return conflicted;
    }

    /// <summary>Changed images' bytes go up first (once per distinct picture; the server keeps them by name).</summary>
    private async Task UploadImageBlobsAsync(SqliteConnection c, string server, SyncKeys keys)
    {
        var todo = new List<(string Id, string Col, byte[] Bytes)>();
        using (var cmd = Db.Cmd(c, "SELECT id, orig, flat, orig_name, flat_name FROM images WHERE dirty=1 AND orig IS NOT NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.IsDBNull(3)) todo.Add((r.GetString(0), "orig", (byte[])r[1]));
                if (!r.IsDBNull(2) && r.IsDBNull(4)) todo.Add((r.GetString(0), "flat", (byte[])r[2]));
            }
        foreach (var (id, col, bytes) in todo)
        {
            var name = keys.BlobName(bytes);
            using (var head = await SendAsync(HttpMethod.Head, $"{server}/n/v1/blob/{name}", keys))
            {
                if (head.StatusCode == HttpStatusCode.NotFound)
                {
                    using var put = await SendAsync(HttpMethod.Put, $"{server}/n/v1/blob/{name}", keys, new ByteArrayContent(keys.EncryptBlob(name, bytes)) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } });
                    if (!put.IsSuccessStatusCode) throw new SyncException(put.StatusCode == HttpStatusCode.RequestEntityTooLarge ? "An image is too big to sync (25 MB at most)" : $"Image upload failed ({(int)put.StatusCode})");
                }
                else if (!head.IsSuccessStatusCode) throw new SyncException($"Sync server said {(int)head.StatusCode}");
            }
            Db.Exec(c, $"UPDATE images SET {col}_name=$n WHERE id=$id", ("$id", id), ("$n", name));
        }
    }

    /// <summary>Pictures whose bytes aren't here yet (another PC added or marked them up).</summary>
    private async Task FetchMissingBlobsAsync(SqliteConnection c, string server, SyncKeys keys, SyncChanges changes)
    {
        var todo = new List<(string Id, string Col, string Name)>();
        using (var cmd = Db.Cmd(c, "SELECT id, orig IS NULL AND orig_name IS NOT NULL, flat IS NULL AND flat_name IS NOT NULL, orig_name, flat_name FROM images WHERE (orig IS NULL AND orig_name IS NOT NULL) OR (flat IS NULL AND flat_name IS NOT NULL)"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (r.GetBoolean(1)) todo.Add((r.GetString(0), "orig", r.GetString(3)));
                if (r.GetBoolean(2)) todo.Add((r.GetString(0), "flat", r.GetString(4)));
            }
        foreach (var (id, col, name) in todo)
        {
            using var res = await SendAsync(HttpMethod.Get, $"{server}/n/v1/blob/{name}", keys);
            if (res.StatusCode == HttpStatusCode.NotFound) continue;   // not uploaded yet: next time
            if (!res.IsSuccessStatusCode) throw new SyncException($"Image download failed ({(int)res.StatusCode})");
            var plain = keys.DecryptBlob(name, await res.Content.ReadAsByteArrayAsync());
            if (plain is null) continue;
            Db.Exec(c, $"UPDATE images SET {col}=$b WHERE id=$id AND {col}_name=$n", ("$id", id), ("$b", plain), ("$n", name));
            changes.Images.Add(id);
        }
    }

    // ── setting up ──

    /// <summary>Makes the vault for a new key (needs a one-time invite). Null when it worked, else what went wrong.</summary>
    public static async Task<string?> CreateVaultAsync(string server, byte[] key, string invite)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server.TrimEnd('/')}/n/v1/vault");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new SyncKeys(key).Auth);
            req.Headers.Add("x-pxd-invite", invite.Trim());
            using var res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode) return null;
            return ErrorOf(await res.Content.ReadAsStringAsync()) ?? $"The server said {(int)res.StatusCode}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return "Can't reach " + server + ".";
        }
    }

    public sealed record VaultInfo(long Bytes, int Items, DateTime? BackupAt, long? BackupBytes);

    /// <summary>How much the server keeps for this key, and when the last off-site backup went. Null if it can't say.</summary>
    public static async Task<VaultInfo?> VaultInfoAsync(string server, byte[] key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/n/v1/vault");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new SyncKeys(key).Auth);
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = d.RootElement;
            DateTime? at = null;
            long? bytes = null;
            if (r.TryGetProperty("backup", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                if (b.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) at = t;
                if (b.TryGetProperty("bytes", out var by)) bytes = by.GetInt64();
            }
            return new VaultInfo(r.GetProperty("bytes").GetInt64(), r.GetProperty("items").GetInt32(), at, bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Checks a key someone typed in has a vault there. Null when it does.</summary>
    public static async Task<string?> CheckVaultAsync(string server, byte[] key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/n/v1/vault");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new SyncKeys(key).Auth);
            using var res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode) return null;
            if (res.StatusCode == HttpStatusCode.Forbidden) return "That sync key has no notes on this server. Check it was typed right.";
            return ErrorOf(await res.Content.ReadAsStringAsync()) ?? $"The server said {(int)res.StatusCode}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return "Can't reach " + server + ".";
        }
    }

    private static string? ErrorOf(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
