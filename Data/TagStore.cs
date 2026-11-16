using Microsoft.Data.Sqlite;

namespace pxdNotepad.Data;

/// <summary>A tag: a name plus an icon, either a built-in symbol (Glyph + Color) or a picture (Png, from an .ico/.png).</summary>
public sealed record Tag(string Id, string Name, string Glyph, string Color, byte[]? Png, long Ver);

/// <summary>
/// Tags and which notes have them. A note keeps its tags' ids in notes.tags (space-separated, synced with the note);
/// notes.tagtext holds their names for the search index, so searching a tag's name finds every note that has it.
/// </summary>
public sealed class TagStore
{
    private readonly SqliteConnection _c;

    public TagStore(SqliteConnection c) => _c = c;

    public event Action? Changed;

    public List<Tag> All()
    {
        var list = new List<Tag>();
        using var cmd = Db.Cmd(_c, "SELECT id, name, glyph, color, png, ver FROM tags WHERE deleted=0 ORDER BY sort, name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Tag(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : (byte[])r[4], r.GetInt64(5)));
        return list;
    }

    public Tag Add(string name, string glyph, string color, byte[]? png)
    {
        var sort = Convert.ToDouble(Db.Scalar(_c, "SELECT COALESCE(MAX(sort),0)+1 FROM tags") ?? 1.0);
        var id = Db.NewId();
        Db.Exec(_c, "INSERT INTO tags(id,name,glyph,color,png,sort,updated) VALUES($id,$n,$g,$c,$p,$s,$u)",
            ("$id", id), ("$n", name.Trim()), ("$g", glyph), ("$c", color), ("$p", png), ("$s", sort), ("$u", Db.Now()));
        Changed?.Invoke();
        return new Tag(id, name.Trim(), glyph, color, png, 0);
    }

    public void Update(string id, string name, string glyph, string color, byte[]? png)
    {
        Db.Exec(_c, "UPDATE tags SET name=$n, glyph=$g, color=$c, png=$p, updated=$u, dirty=1, ver=ver+1 WHERE id=$id",
            ("$id", id), ("$n", name.Trim()), ("$g", glyph), ("$c", color), ("$p", png), ("$u", Db.Now()));
        RefreshTagText(_c, id);
        Changed?.Invoke();
    }

    /// <summary>Removes a tag from every note that has it, then the tag itself.</summary>
    public void Delete(string id)
    {
        var now = Db.Now();
        using (var tx = _c.BeginTransaction())
        {
            foreach (var (noteId, tags) in NotesWith(id))
                Db.Exec(_c, "UPDATE notes SET tags=$t, updated=$u, dirty=1, ver=ver+1 WHERE id=$id",
                    ("$id", noteId), ("$t", Join(Split(tags).Where(t => t != id))), ("$u", now));
            Db.Exec(_c, "UPDATE tags SET deleted=1, updated=$u, dirty=1, ver=ver+1 WHERE id=$id", ("$id", id), ("$u", now));
            tx.Commit();
        }
        RefreshTagText(_c, null);
        Changed?.Invoke();
    }

    public int CountNotes(string tagId) =>
        NotesWith(tagId).Count(n => Split(n.Tags).Contains(tagId));

    private List<(string Id, string Tags)> NotesWith(string tagId)
    {
        var list = new List<(string, string)>();
        using var cmd = Db.Cmd(_c, "SELECT id, tags FROM notes WHERE purged=0 AND instr(tags, $t) > 0", ("$t", tagId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public string[] TagsOf(string noteId) =>
        Split(Db.Scalar(_c, "SELECT tags FROM notes WHERE id=$id", ("$id", noteId)) as string);

    /// <summary>Puts the tag on the note, or takes it off. Returns whether the note has it now.</summary>
    public bool Toggle(string noteId, string tagId)
    {
        var tags = TagsOf(noteId).ToList();
        var on = !tags.Remove(tagId);
        if (on) tags.Add(tagId);
        SetTags(noteId, tags);
        return on;
    }

    public void SetTags(string noteId, IEnumerable<string> tagIds)
    {
        Db.Exec(_c, "UPDATE notes SET tags=$t, updated=$u, dirty=1, ver=ver+1 WHERE id=$id",
            ("$id", noteId), ("$t", Join(tagIds)), ("$u", Db.Now()));
        RefreshTagText(_c, null, noteId);
        Changed?.Invoke();
    }

    public static string[] Split(string? tags) =>
        string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Join(IEnumerable<string> ids) => string.Join(' ', ids.Distinct());

    /// <summary>
    /// Re-writes notes' tag names for search (after a tag is renamed or deleted, or sync brought changes): the notes
    /// with <paramref name="tagId"/>, or just <paramref name="noteId"/>, or every tagged note. Not a note change, so it
    /// isn't synced.
    /// </summary>
    public static void RefreshTagText(SqliteConnection c, string? tagId, string? noteId = null)
    {
        var names = new Dictionary<string, string>();
        using (var cmd = Db.Cmd(c, "SELECT id, name FROM tags WHERE deleted=0"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names[r.GetString(0)] = r.GetString(1);

        var sql = noteId is not null ? "SELECT id, tags, tagtext FROM notes WHERE id=$k"
            : tagId is not null ? "SELECT id, tags, tagtext FROM notes WHERE instr(tags, $k) > 0 OR tagtext <> ''"
            : "SELECT id, tags, tagtext FROM notes WHERE tags <> '' OR tagtext <> ''";
        var updates = new List<(string Id, string Text)>();
        using (var cmd = Db.Cmd(c, sql, ("$k", noteId ?? tagId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var text = string.Join(' ', Split(r.GetString(1)).Select(t => names.GetValueOrDefault(t)).Where(n => n is not null));
                if (text != r.GetString(2)) updates.Add((r.GetString(0), text));
            }
        foreach (var (id, text) in updates)
            Db.Exec(c, "UPDATE notes SET tagtext=$t WHERE id=$id", ("$id", id), ("$t", text));
    }
}
