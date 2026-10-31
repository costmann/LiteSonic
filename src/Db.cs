using Microsoft.Data.Sqlite;

namespace LiteSonic;

/// <summary>
/// Accesso SQLite "a mano" (niente EF Core): meno dipendenze, meno allocazioni, avvio rapido.
/// Ogni operazione apre una connessione dal pool e la chiude subito.
/// </summary>
public sealed class Db
{
    readonly string _cs;

    public Db(AppConfig cfg)
    {
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(cfg.DataFolder, "litesonic.db"),
            Pooling = true,
        }.ToString();

        using var c = Open();
        c.Exec("PRAGMA journal_mode=WAL;");
        c.Exec(Schema);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        // cache di pagina limitata a ~1 MB per connessione, niente mmap
        c.Exec("PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024; PRAGMA mmap_size=0; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return c;
    }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS artists(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            sort_name TEXT NOT NULL,
            album_count INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE IF NOT EXISTS albums(
            id INTEGER PRIMARY KEY,
            artist_id INTEGER NOT NULL REFERENCES artists(id) ON DELETE CASCADE,
            name TEXT NOT NULL COLLATE NOCASE,
            year INTEGER, genre TEXT, cover_path TEXT,
            song_count INTEGER NOT NULL DEFAULT 0,
            duration INTEGER NOT NULL DEFAULT 0,
            play_count INTEGER NOT NULL DEFAULT 0,
            last_played TEXT,
            created TEXT NOT NULL,
            UNIQUE(artist_id, name));

        CREATE TABLE IF NOT EXISTS tracks(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            mtime INTEGER NOT NULL, size INTEGER NOT NULL,
            album_id INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
            artist_id INTEGER NOT NULL REFERENCES artists(id) ON DELETE CASCADE,
            artist TEXT NOT NULL,
            title TEXT NOT NULL,
            track_no INTEGER, disc_no INTEGER, year INTEGER, genre TEXT,
            duration INTEGER NOT NULL DEFAULT 0, bitrate INTEGER NOT NULL DEFAULT 0,
            suffix TEXT NOT NULL, content_type TEXT NOT NULL,
            has_cover INTEGER NOT NULL DEFAULT 0,
            play_count INTEGER NOT NULL DEFAULT 0, last_played TEXT,
            created TEXT NOT NULL,
            scan_id INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album_id, disc_no, track_no);
        CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist_id);
        CREATE INDEX IF NOT EXISTS ix_tracks_genre ON tracks(genre);
        CREATE INDEX IF NOT EXISTS ix_albums_artist ON albums(artist_id);

        CREATE TABLE IF NOT EXISTS users(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password TEXT NOT NULL,
            is_admin INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE IF NOT EXISTS stars(
            user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            item_id TEXT NOT NULL,
            created TEXT NOT NULL,
            PRIMARY KEY(user_id, item_id)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS playlists(
            id INTEGER PRIMARY KEY,
            user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            name TEXT NOT NULL, comment TEXT,
            created TEXT NOT NULL, changed TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS playlist_tracks(
            playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            pos INTEGER NOT NULL,
            track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            PRIMARY KEY(playlist_id, pos)) WITHOUT ROWID;
        """;
}

public static class SqliteExt
{
    public static int Exec(this SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.Cmd(sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.Cmd(sql, ps);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static SqliteCommand Cmd(this SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
