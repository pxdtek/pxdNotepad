using Microsoft.Data.Sqlite;

namespace pxdNotepad.Data;

/// <summary>
/// The notes database (SQLite, WAL). Each thread that touches it opens its own connection with <see cref="Open"/>:
/// the UI thread, the search thread and the sync thread.
///
/// Search is an FTS5 table with the trigram tokenizer over each note's title and plain text, kept current by
/// triggers, so any 3+ character piece of a word is found from the index (not just whole words), in a few
/// milliseconds even with tens of thousands of notes.
/// </summary>
public static class Db
{
    private static bool _created;

    public static SqliteConnection Open(string? path = null)
    {
        path ??= AppPaths.Database;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY;");
        if (!_created || path != AppPaths.Database)
        {
            Create(c);
            if (path == AppPaths.Database) _created = true;
        }
        return c;
    }

    public static void Reset() => _created = false;

    private static void Create(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS categories(
              id TEXT PRIMARY KEY, parent TEXT, name TEXT NOT NULL, sort REAL NOT NULL DEFAULT 0,
              updated INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0,
              dirty INTEGER NOT NULL DEFAULT 1, ver INTEGER NOT NULL DEFAULT 0, srv INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS notes(
              rid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, cat TEXT,
              title TEXT NOT NULL DEFAULT '', body TEXT NOT NULL DEFAULT '', doc TEXT NOT NULL DEFAULT '',
              created INTEGER NOT NULL, updated INTEGER NOT NULL,
              trashed INTEGER NOT NULL DEFAULT 0, purged INTEGER NOT NULL DEFAULT 0,
              dirty INTEGER NOT NULL DEFAULT 1, ver INTEGER NOT NULL DEFAULT 0, srv INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS notes_cat ON notes(cat, updated);
            CREATE INDEX IF NOT EXISTS notes_updated ON notes(updated);

            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(title, body, content='notes', content_rowid='rid', tokenize='trigram');
            CREATE TRIGGER IF NOT EXISTS notes_ai AFTER INSERT ON notes BEGIN
              INSERT INTO notes_fts(rowid, title, body) VALUES (new.rid, new.title, new.body);
            END;
            CREATE TRIGGER IF NOT EXISTS notes_ad AFTER DELETE ON notes BEGIN
              INSERT INTO notes_fts(notes_fts, rowid, title, body) VALUES ('delete', old.rid, old.title, old.body);
            END;
            CREATE TRIGGER IF NOT EXISTS notes_au AFTER UPDATE OF title, body ON notes BEGIN
              INSERT INTO notes_fts(notes_fts, rowid, title, body) VALUES ('delete', old.rid, old.title, old.body);
              INSERT INTO notes_fts(rowid, title, body) VALUES (new.rid, new.title, new.body);
            END;

            CREATE TABLE IF NOT EXISTS images(
              id TEXT PRIMARY KEY, mime TEXT NOT NULL, orig BLOB, flat BLOB, marks TEXT NOT NULL DEFAULT '',
              w INTEGER NOT NULL, h INTEGER NOT NULL, updated INTEGER NOT NULL,
              orig_name TEXT, flat_name TEXT,
              dirty INTEGER NOT NULL DEFAULT 1, ver INTEGER NOT NULL DEFAULT 0, srv INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
            """);
    }

    public static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static string? Meta(SqliteConnection c, string key) => Scalar(c, "SELECT v FROM meta WHERE k=$k", ("$k", key)) as string;

    public static void SetMeta(SqliteConnection c, string key, string? value) =>
        Exec(c, "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=excluded.v", ("$k", key), ("$v", value));

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string NewId() => Guid.NewGuid().ToString("N");
}
