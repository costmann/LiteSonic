using Microsoft.Data.Sqlite;

namespace LiteSonic.Subsonic;

/// <summary>Query sulla libreria che producono direttamente nodi Subsonic (nessun DTO intermedio).</summary>
public sealed class Library(Db db, AppConfig cfg)
{
    const string TrackSql = """
        SELECT t.id, t.title, t.album_id, al.name, t.artist, t.artist_id, t.track_no, t.disc_no, t.year, t.genre,
               t.duration, t.bitrate, t.suffix, t.content_type, t.size, t.path, t.play_count, t.created, s.created
        FROM tracks t JOIN albums al ON al.id=t.album_id
        LEFT JOIN stars s ON s.user_id=$uid AND s.item_id='tr-'||t.id
        """;

    const string AlbumSql = """
        SELECT al.id, al.name, ar.name, al.artist_id, al.year, al.genre, al.song_count, al.duration,
               al.play_count, al.created, s.created
        FROM albums al JOIN artists ar ON ar.id=al.artist_id
        LEFT JOIN stars s ON s.user_id=$uid AND s.item_id='al-'||al.id
        """;

    const string ArtistSql = """
        SELECT ar.id, ar.name, ar.album_count, s.created
        FROM artists ar
        LEFT JOIN stars s ON s.user_id=$uid AND s.item_id='ar-'||ar.id
        """;

    public const string TrackOrder = " ORDER BY t.disc_no, t.track_no, t.title";

    public List<Node> Tracks(long uid, string where, string tail, string nodeName, params (string, object?)[] ps)
        => Query(TrackSql + " " + where + " " + tail, uid, ps, r => Song(r, nodeName));

    public List<Node> Albums(long uid, string where, string tail, string nodeName, params (string, object?)[] ps)
        => Query(AlbumSql + " " + where + " " + tail, uid, ps, r => Album(r, nodeName));

    public List<Node> Artists(long uid, string where, string tail, string nodeName, params (string, object?)[] ps)
        => Query(ArtistSql + " " + where + " " + tail, uid, ps, r => Artist(r, nodeName));

    List<Node> Query(string sql, long uid, (string, object?)[] ps, Func<SqliteDataReader, Node> map)
    {
        using var c = db.Open();
        using var cmd = c.Cmd(sql, ps);
        cmd.Parameters.AddWithValue("$uid", uid);
        using var r = cmd.ExecuteReader();
        var list = new List<Node>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    Node Song(SqliteDataReader r, string name)
    {
        long albumId = r.GetInt64(2);
        var rel = Path.GetRelativePath(cfg.MusicFolder, r.GetString(15)).Replace('\\', '/');
        return Node.Item(name)
            .A("id", "tr-" + r.GetInt64(0))
            .A("parent", "al-" + albumId)
            .A("isDir", false)
            .A("title", r.GetString(1))
            .A("album", r.GetString(3))
            .A("artist", r.GetString(4))
            .A("track", r.Long(6))
            .A("discNumber", r.Long(7))
            .A("year", r.Long(8))
            .A("genre", r.Str(9))
            .A("coverArt", "al-" + albumId)
            .A("size", r.GetInt64(14))
            .A("contentType", r.GetString(13))
            .A("suffix", r.GetString(12))
            .A("duration", r.GetInt64(10))
            .A("bitRate", r.GetInt64(11))
            .A("path", rel)
            .A("playCount", r.GetInt64(16))
            .A("created", r.GetString(17))
            .A("starred", r.Str(18))
            .A("albumId", "al-" + albumId)
            .A("artistId", "ar-" + r.GetInt64(5))
            .A("type", "music")
            .A("mediaType", "song")
            .A("isVideo", false);
    }

    // Emette sia gli attributi ID3 (name, songCount…) sia quelli "directory" (title, isDir, parent)
    // così lo stesso nodo va bene per getAlbumList, getAlbumList2, getMusicDirectory, search2/3.
    static Node Album(SqliteDataReader r, string name)
    {
        var id = "al-" + r.GetInt64(0);
        return Node.Item(name)
            .A("id", id)
            .A("parent", "ar-" + r.GetInt64(3))
            .A("isDir", true)
            .A("name", r.GetString(1))
            .A("title", r.GetString(1))
            .A("album", r.GetString(1))
            .A("artist", r.GetString(2))
            .A("artistId", "ar-" + r.GetInt64(3))
            .A("year", r.Long(4))
            .A("genre", r.Str(5))
            .A("coverArt", id)
            .A("songCount", r.GetInt64(6))
            .A("duration", r.GetInt64(7))
            .A("playCount", r.GetInt64(8))
            .A("created", r.GetString(9))
            .A("starred", r.Str(10));
    }

    static Node Artist(SqliteDataReader r, string name)
    {
        var id = "ar-" + r.GetInt64(0);
        return Node.Item(name)
            .A("id", id)
            .A("name", r.GetString(1))
            .A("albumCount", r.GetInt64(2))
            .A("coverArt", id)
            .A("starred", r.Str(3));
    }

    /// <summary>Raggruppa gli artisti per iniziale (getArtists / getIndexes).</summary>
    public IEnumerable<Node> Indexes(long uid)
    {
        Node? current = null;
        foreach (var a in Artists(uid, "", "ORDER BY ar.sort_name COLLATE NOCASE", "artist"))
        {
            var sort = Scanner.SortName((string)a.Attrs[1].Value);
            var ch = char.ToUpperInvariant(sort[0]);
            var letter = char.IsLetter(ch) ? ch.ToString() : "#";
            if (current is null || (string)current.Attrs[0].Value != letter)
            {
                if (current is not null) yield return current;
                current = Node.Item("index").A("name", letter);
            }
            current.Add(a);
        }
        if (current is not null) yield return current;
    }
}
