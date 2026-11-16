using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

namespace pxdNotepad.Data;

public sealed record Category(string Id, string? Parent, string Name, double Sort, string Tags = "");

public sealed record NoteRow(string Id, string? Cat, string Title, string Preview, long Updated, bool Trashed, string Tags = "");

public sealed record NoteFull(string Id, string? Cat, string Title, string Doc, string Body, long Updated, bool Trashed, long Ver);

public sealed record SearchHit(string Id, string? Cat, string Title, string Snippet, long Updated, string Tags = "");

/// <summary>Notes and categories, on the UI thread's connection. Every change marks the row dirty for sync.</summary>
public sealed class NoteStore : IDisposable
{
    private readonly SqliteConnection _c;

    public NoteStore(string? path = null) => _c = Db.Open(path);
    public SqliteConnection Connection => _c;
    public void Dispose() => _c.Dispose();

    /// <summary>Raised after anything here changes a row (sync waits a moment, then sends it).</summary>
    public event Action? Changed;

    private void Touch() => Changed?.Invoke();

    // ── categories ──

    public List<Category> Categories()
    {
        var list = new List<Category>();
        using var cmd = Db.Cmd(_c, "SELECT id, parent, name, sort, tags FROM categories WHERE deleted=0 ORDER BY sort, name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Category(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetString(4)));
        return list;
    }

    public Category AddCategory(string name, string? parent)
    {
        var sort = Convert.ToDouble(Db.Scalar(_c, "SELECT COALESCE(MAX(sort),0)+1 FROM categories WHERE deleted=0 AND parent IS $p", ("$p", parent)) ?? 1.0);
        var c = new Category(Db.NewId(), parent, name.Trim(), sort);
        Db.Exec(_c, "INSERT INTO categories(id,parent,name,sort,updated) VALUES($id,$p,$n,$s,$u)",
            ("$id", c.Id), ("$p", parent), ("$n", c.Name), ("$s", sort), ("$u", Db.Now()));
        Touch();
        return c;
    }

    public void RenameCategory(string id, string name)
    {
        Db.Exec(_c, "UPDATE categories SET name=$n, updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$n", name.Trim()), ("$u", Db.Now()));
        Touch();
    }

    /// <summary>False when that would put a category inside itself.</summary>
    public bool MoveCategory(string id, string? newParent)
    {
        for (var p = newParent; p is not null; p = Db.Scalar(_c, "SELECT parent FROM categories WHERE id=$id", ("$id", p)) as string)
            if (p == id) return false;
        var sort = Convert.ToDouble(Db.Scalar(_c, "SELECT COALESCE(MAX(sort),0)+1 FROM categories WHERE deleted=0 AND parent IS $p", ("$p", newParent)) ?? 1.0);
        Db.Exec(_c, "UPDATE categories SET parent=$p, sort=$s, updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$p", newParent), ("$s", sort), ("$u", Db.Now()));
        Touch();
        return true;
    }

    /// <summary>Removes a category; its notes and sub-categories move up to its parent.</summary>
    public void DeleteCategory(string id)
    {
        var parent = Db.Scalar(_c, "SELECT parent FROM categories WHERE id=$id", ("$id", id)) as string;
        var now = Db.Now();
        using var tx = _c.BeginTransaction();
        Db.Exec(_c, "UPDATE categories SET parent=$p, updated=$u, dirty=1, ver=ver+1 WHERE parent=$id AND deleted=0", ("$id", id), ("$p", parent), ("$u", now));
        Db.Exec(_c, "UPDATE notes SET cat=$p, updated=$u, dirty=1, ver=ver+1 WHERE cat=$id", ("$id", id), ("$p", parent), ("$u", now));
        Db.Exec(_c, "UPDATE categories SET deleted=1, updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$u", now));
        tx.Commit();
        Touch();
    }

    // ── notes ──

    /// <summary>Note counts per category (null key: unfiled), trash not counted.</summary>
    public Dictionary<string, int> Counts(out int all, out int unfiled, out int trash)
    {
        var d = new Dictionary<string, int>();
        all = unfiled = trash = 0;
        using var cmd = Db.Cmd(_c, "SELECT cat, trashed<>0, COUNT(*) FROM notes WHERE purged=0 GROUP BY cat, trashed<>0");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = r.GetInt32(2);
            if (r.GetBoolean(1)) { trash += n; continue; }
            all += n;
            if (r.IsDBNull(0)) unfiled += n;
            else d[r.GetString(0)] = d.GetValueOrDefault(r.GetString(0)) + n;
        }
        return d;
    }

    /// <summary>Notes in these categories (null: all notes; empty set + unfiled: notes with no category), newest first.</summary>
    public List<NoteRow> Notes(IReadOnlyCollection<string>? cats, bool unfiled = false, bool trash = false)
    {
        var sql = new StringBuilder("SELECT id, cat, title, substr(body,1,200), updated, trashed, tags FROM notes WHERE purged=0 AND ");
        sql.Append(trash ? "trashed<>0" : "trashed=0");
        var args = new List<(string, object?)>();
        if (!trash)
        {
            if (unfiled) sql.Append(" AND cat IS NULL");
            else if (cats is not null)
            {
                sql.Append(" AND cat IN (");
                var i = 0;
                foreach (var c in cats)
                {
                    if (i > 0) sql.Append(',');
                    sql.Append("$c").Append(i);
                    args.Add(("$c" + i, c));
                    i++;
                }
                if (i == 0) sql.Append("''");
                sql.Append(')');
            }
        }
        sql.Append(" ORDER BY updated DESC");
        var list = new List<NoteRow>();
        using var cmd = Db.Cmd(_c, sql.ToString(), [.. args]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new NoteRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), Preview(r.GetString(3)), r.GetInt64(4), r.GetInt64(5) != 0, r.GetString(6)));
        return list;
    }

    public static string Preview(string body)
    {
        var sb = new StringBuilder(Math.Min(body.Length, 160));
        var space = false;
        foreach (var ch in body)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length >= 150) break;
        }
        return sb.ToString();
    }

    public NoteFull? Load(string id)
    {
        using var cmd = Db.Cmd(_c, "SELECT id, cat, title, doc, body, updated, trashed, ver FROM notes WHERE id=$id AND purged=0", ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new NoteFull(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6) != 0, r.GetInt64(7));
    }

    public long Version(string id) => Convert.ToInt64(Db.Scalar(_c, "SELECT ver FROM notes WHERE id=$id", ("$id", id)) ?? -1L);

    public string NewNote(string? cat, string title = "", string doc = "", string body = "")
    {
        var id = Db.NewId();
        var now = Db.Now();
        Db.Exec(_c, "INSERT INTO notes(id,cat,title,body,doc,created,updated) VALUES($id,$c,$t,$b,$d,$n,$n)",
            ("$id", id), ("$c", cat), ("$t", title), ("$b", body), ("$d", doc), ("$n", now));
        Touch();
        return id;
    }

    /// <summary>Saves the editor's copy; returns the note's new local version.</summary>
    public long Save(string id, string title, string doc, string body)
    {
        Db.Exec(_c, "UPDATE notes SET title=$t, doc=$d, body=$b, updated=$u, dirty=1, ver=ver+1 WHERE id=$id",
            ("$id", id), ("$t", title), ("$d", doc), ("$b", body), ("$u", Db.Now()));
        Touch();
        return Version(id);
    }

    public void MoveNote(string id, string? cat)
    {
        Db.Exec(_c, "UPDATE notes SET cat=$c, updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$c", cat), ("$u", Db.Now()));
        Touch();
    }

    public void Trash(string id, bool trashed)
    {
        Db.Exec(_c, "UPDATE notes SET trashed=$t, updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$t", trashed ? Db.Now() : 0L), ("$u", Db.Now()));
        Touch();
    }

    /// <summary>Gone for good: the text is wiped, a small tombstone stays so other PCs drop it too.</summary>
    public void Purge(string id)
    {
        Db.Exec(_c, "UPDATE notes SET purged=1, title='', body='', doc='', updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$u", Db.Now()));
        Touch();
    }

    public int EmptyTrash()
    {
        using var cmd = Db.Cmd(_c, "UPDATE notes SET purged=1, title='', body='', doc='', updated=$u, dirty=1, ver=ver+1 WHERE trashed<>0 AND purged=0", ("$u", Db.Now()));
        var n = cmd.ExecuteNonQuery();
        if (n > 0) Touch();
        return n;
    }
}

/// <summary>
/// Search, on its own connection and thread. Words of 3+ characters go through the trigram index; shorter ones are
/// checked with LIKE on the notes the index already narrowed down (or all notes when every word is short).
/// </summary>
public sealed class SearchEngine : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly object _gate = new();

    public SearchEngine(string? path = null) => _c = Db.Open(path);
    public void Dispose() { lock (_gate) _c.Dispose(); }

    public static string[] Terms(string query) =>
        query.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();

    public List<SearchHit> Search(string query, int limit, out double ms)
    {
        var sw = Stopwatch.StartNew();
        var terms = Terms(query);
        var hits = new List<SearchHit>();
        if (terms.Length == 0) { ms = 0; return hits; }
        var longs = terms.Where(t => t.Length >= 3).ToArray();
        var shorts = terms.Where(t => t.Length < 3).ToArray();
        var args = new List<(string, object?)>();
        var sql = new StringBuilder();
        // Title hits first, then the index's own ranking (bm25, title weighted), then newest.
        var best = terms.OrderByDescending(t => t.Length).First();
        args.Add(("$best", "%" + Escape(best) + "%"));
        if (longs.Length > 0)
        {
            sql.Append("SELECT n.id, n.cat, n.title, n.body, n.updated, n.tags FROM notes_fts f JOIN notes n ON n.rid=f.rowid WHERE notes_fts MATCH $q AND n.trashed=0 AND n.purged=0");
            args.Add(("$q", string.Join(' ', longs.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""))));
        }
        else
            sql.Append("SELECT n.id, n.cat, n.title, n.body, n.updated, n.tags FROM notes n WHERE n.trashed=0 AND n.purged=0");
        for (var i = 0; i < shorts.Length; i++)
        {
            sql.Append($" AND (n.title LIKE $s{i} ESCAPE '\\' OR n.body LIKE $s{i} ESCAPE '\\' OR n.tagtext LIKE $s{i} ESCAPE '\\')");
            args.Add(($"$s{i}", "%" + Escape(shorts[i]) + "%"));
        }
        sql.Append(longs.Length > 0
            ? " ORDER BY (n.title LIKE $best ESCAPE '\\' OR n.tagtext LIKE $best ESCAPE '\\') DESC, bm25(notes_fts, 8.0, 1.0, 6.0), n.updated DESC"
            : " ORDER BY (n.title LIKE $best ESCAPE '\\' OR n.tagtext LIKE $best ESCAPE '\\') DESC, n.updated DESC");
        sql.Append(" LIMIT $lim");
        args.Add(("$lim", limit));

        lock (_gate)
        {
            using var cmd = Db.Cmd(_c, sql.ToString(), [.. args]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                hits.Add(new SearchHit(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), Snippet(r.GetString(3), terms), r.GetInt64(4), r.GetString(5)));
        }
        ms = sw.Elapsed.TotalMilliseconds;
        return hits;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>About 150 characters of the text around the first match, on one line.</summary>
    public static string Snippet(string body, string[] terms)
    {
        var at = -1;
        foreach (var t in terms.OrderByDescending(t => t.Length))
        {
            at = body.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) break;
        }
        if (at < 0) return NoteStore.Preview(body);
        var start = Math.Max(0, at - 50);
        while (start > 0 && start > at - 70 && !char.IsWhiteSpace(body[start - 1])) start--;
        var piece = body.Substring(start, Math.Min(body.Length - start, 220));
        var p = NoteStore.Preview(piece);
        return start > 0 ? "…" + p : p;
    }
}
