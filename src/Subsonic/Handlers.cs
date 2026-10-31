namespace LiteSonic.Subsonic;

using H = Func<Req, Task<Node?>>;

public static class Handlers
{
    /// <summary>Sentinella: l'handler ha già scritto la risposta (stream, immagini).</summary>
    public static readonly Node Raw = new("__raw");

    public static Dictionary<string, H> Build(IServiceProvider sp)
    {
        var lib = sp.GetRequiredService<Library>();
        var db = sp.GetRequiredService<Db>();
        var users = sp.GetRequiredService<Users>();
        var scanner = sp.GetRequiredService<Scanner>();
        var media = sp.GetRequiredService<Media>();

        var h = new Dictionary<string, H>(StringComparer.OrdinalIgnoreCase);
        void On(string name, Func<Req, Node?> f) => h[name] = r => Task.FromResult(f(r));

        // ---------- sistema ----------
        On("ping", _ => null);
        On("getLicense", _ => new Node("license").A("valid", true).A("email", "").A("licenseExpires", "2099-12-31T00:00:00Z"));
        On("getOpenSubsonicExtensions", _ => new Node("").AddRange([], "openSubsonicExtensions"));
        On("getMusicFolders", _ => new Node("musicFolders").Add(Node.Item("musicFolder").A("id", 1).A("name", "Music")));
        On("getScanStatus", _ => ScanStatus(scanner));
        On("startScan", r =>
        {
            if (!r.User.IsAdmin) throw ApiError.Forbidden();
            var full = r.Bool("fullScan", false);
            if (full) media.ClearCoverCache();
            _ = scanner.StartAsync(full);
            return ScanStatus(scanner, forceScanning: true);
        });

        // ---------- navigazione ----------
        On("getArtists", r => new Node("artists").A("ignoredArticles", "The Il Lo La I Gli Le")
            .AddRange(lib.Indexes(r.User.Id), "index"));

        On("getIndexes", r => new Node("indexes").A("ignoredArticles", "The Il Lo La I Gli Le")
            .A("lastModified", new DateTimeOffset(scanner.LastScan ?? DateTime.UtcNow).ToUnixTimeMilliseconds())
            .AddRange(lib.Indexes(r.User.Id), "index"));

        On("getArtist", r =>
        {
            var (_, id) = Req.ParseId(r.Need("id"));
            var artist = lib.Artists(r.User.Id, "WHERE ar.id=$id", "", "artist", ("$id", id)).FirstOrDefault() ?? throw ApiError.NotFound("Artista");
            return artist.AddRange(lib.Albums(r.User.Id, "WHERE al.artist_id=$id", "ORDER BY al.year, al.name", "album", ("$id", id)), "album");
        });

        On("getAlbum", r =>
        {
            var (_, id) = Req.ParseId(r.Need("id"));
            var album = lib.Albums(r.User.Id, "WHERE al.id=$id", "", "album", ("$id", id)).FirstOrDefault() ?? throw ApiError.NotFound("Album");
            return album.AddRange(lib.Tracks(r.User.Id, "WHERE t.album_id=$id", Library.TrackOrder, "song", ("$id", id)), "song");
        });

        On("getSong", r =>
        {
            var (_, id) = Req.ParseId(r.Need("id"));
            return lib.Tracks(r.User.Id, "WHERE t.id=$id", "", "song", ("$id", id)).FirstOrDefault() ?? throw ApiError.NotFound("Brano");
        });

        On("getMusicDirectory", r =>
        {
            var (kind, id) = Req.ParseId(r.Need("id"));
            if (kind == "ar")
            {
                var a = lib.Artists(r.User.Id, "WHERE ar.id=$id", "", "x", ("$id", id)).FirstOrDefault() ?? throw ApiError.NotFound();
                return new Node("directory").A("id", "ar-" + id).A("name", a.Attrs[1].Value).A("parent", "1")
                    .AddRange(lib.Albums(r.User.Id, "WHERE al.artist_id=$id", "ORDER BY al.year, al.name", "child", ("$id", id)), "child");
            }
            if (kind == "al")
            {
                var al = lib.Albums(r.User.Id, "WHERE al.id=$id", "", "x", ("$id", id)).FirstOrDefault() ?? throw ApiError.NotFound();
                return new Node("directory").A("id", "al-" + id).A("name", al.Attrs.First(x => x.Key == "name").Value)
                    .A("parent", al.Attrs.First(x => x.Key == "parent").Value)
                    .AddRange(lib.Tracks(r.User.Id, "WHERE t.album_id=$id", Library.TrackOrder, "child", ("$id", id)), "child");
            }
            throw ApiError.NotFound();
        });

        // ---------- liste ----------
        Node AlbumList(Req r, string container)
        {
            int size = Math.Clamp(r.Int("size", 10), 1, 500), offset = Math.Max(0, r.Int("offset", 0));
            int from = r.Int("fromYear", 0), to = r.Int("toYear", 9999);
            var type = r.Need("type");
            (string where, string order) = type switch
            {
                "random" => ("", "ORDER BY RANDOM()"),
                "newest" => ("", "ORDER BY al.created DESC, al.id DESC"),
                "highest" or "frequent" => ("WHERE al.play_count>0", "ORDER BY al.play_count DESC"),
                "recent" => ("WHERE al.last_played IS NOT NULL", "ORDER BY al.last_played DESC"),
                "alphabeticalByName" => ("", "ORDER BY al.name"),
                "alphabeticalByArtist" => ("", "ORDER BY ar.sort_name COLLATE NOCASE, al.year, al.name"),
                "starred" => ("WHERE s.created IS NOT NULL", "ORDER BY s.created DESC"),
                "byYear" => ("WHERE al.year BETWEEN $min AND $max", from <= to ? "ORDER BY al.year" : "ORDER BY al.year DESC"),
                "byGenre" => ("WHERE al.genre=$genre", "ORDER BY al.name"),
                _ => throw new ApiError(0, "Tipo non valido: " + type),
            };
            if (type == "byGenre") r.Need("genre");
            return new Node(container).AddRange(lib.Albums(r.User.Id, where, order + " LIMIT $size OFFSET $offset", "album",
                ("$size", size), ("$offset", offset), ("$min", Math.Min(from, to)), ("$max", Math.Max(from, to)), ("$genre", r.Get("genre"))), "album");
        }
        On("getAlbumList", r => AlbumList(r, "albumList"));
        On("getAlbumList2", r => AlbumList(r, "albumList2"));

        On("getRandomSongs", r =>
        {
            var where = new List<string>();
            if (r.Get("genre") is not null) where.Add("t.genre=$genre");
            if (r.Get("fromYear") is not null) where.Add("t.year>=$from");
            if (r.Get("toYear") is not null) where.Add("t.year<=$to");
            var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            return new Node("randomSongs").AddRange(lib.Tracks(r.User.Id, w, "ORDER BY RANDOM() LIMIT $size", "song",
                ("$size", Math.Clamp(r.Int("size", 10), 1, 500)), ("$genre", r.Get("genre")),
                ("$from", r.Int("fromYear", 0)), ("$to", r.Int("toYear", 9999))), "song");
        });

        On("getGenres", _ =>
        {
            using var c = db.Open();
            using var cmd = c.Cmd("SELECT genre, COUNT(*), COUNT(DISTINCT album_id) FROM tracks WHERE genre IS NOT NULL GROUP BY genre ORDER BY genre");
            using var rd = cmd.ExecuteReader();
            var list = new List<Node>();
            while (rd.Read())
                list.Add(new Node("genre", true) { Text = rd.GetString(0) }.A("songCount", rd.GetInt64(1)).A("albumCount", rd.GetInt64(2)));
            return new Node("genres").AddRange(list, "genre");
        });

        On("getSongsByGenre", r => new Node("songsByGenre").AddRange(lib.Tracks(r.User.Id, "WHERE t.genre=$g",
            "ORDER BY t.title LIMIT $n OFFSET $o", "song", ("$g", r.Need("genre")),
            ("$n", Math.Clamp(r.Int("count", 10), 1, 500)), ("$o", Math.Max(0, r.Int("offset", 0)))), "song"));

        On("getNowPlaying", _ => new Node("nowPlaying").AddRange([], "entry"));

        // ---------- ricerca ----------
        Node Search(Req r, string container)
        {
            // query vuota o "" = tutta la libreria (usato da client come Symfonium per la sincronizzazione)
            var q = (r.Get("query") ?? "").Trim().Trim('"').Replace("*", "").Trim();
            var like = "%" + q + "%";
            int Cap(string p, int d) => Math.Clamp(r.Int(p, d), 0, 1000);
            var res = new Node(container);
            if (Cap("artistCount", 20) > 0)
                foreach (var n in lib.Artists(r.User.Id, "WHERE ar.name LIKE $q", "ORDER BY ar.sort_name LIMIT $n OFFSET $o", "artist",
                    ("$q", like), ("$n", Cap("artistCount", 20)), ("$o", Cap("artistOffset", 0)))) res.Add(n);
            if (Cap("albumCount", 20) > 0)
                foreach (var n in lib.Albums(r.User.Id, "WHERE al.name LIKE $q OR ar.name LIKE $q", "ORDER BY al.name LIMIT $n OFFSET $o", "album",
                    ("$q", like), ("$n", Cap("albumCount", 20)), ("$o", Cap("albumOffset", 0)))) res.Add(n);
            if (Cap("songCount", 20) > 0)
                foreach (var n in lib.Tracks(r.User.Id, "WHERE t.title LIKE $q OR t.artist LIKE $q OR al.name LIKE $q", "ORDER BY t.title LIMIT $n OFFSET $o", "song",
                    ("$q", like), ("$n", Cap("songCount", 20)), ("$o", Cap("songOffset", 0)))) res.Add(n);
            return res;
        }
        On("search2", r => Search(r, "searchResult2"));
        On("search3", r => Search(r, "searchResult3"));

        // ---------- preferiti e ascolti ----------
        IEnumerable<string> StarIds(Req r) => r.All("id").Concat(r.All("albumId").Select(x => x.StartsWith("al-") ? x : "al-" + x))
            .Concat(r.All("artistId").Select(x => x.StartsWith("ar-") ? x : "ar-" + x))
            .Select(x => x.Contains('-') ? x : "tr-" + x);

        On("star", r =>
        {
            using var c = db.Open();
            foreach (var id in StarIds(r))
                c.Exec("INSERT OR IGNORE INTO stars(user_id, item_id, created) VALUES($u,$i,$c)", ("$u", r.User.Id), ("$i", id), ("$c", SqliteExt.Now()));
            return null;
        });
        On("unstar", r =>
        {
            using var c = db.Open();
            foreach (var id in StarIds(r))
                c.Exec("DELETE FROM stars WHERE user_id=$u AND item_id=$i", ("$u", r.User.Id), ("$i", id));
            return null;
        });

        Node Starred(Req r, string container) => new Node(container)
            .AddRange(lib.Artists(r.User.Id, "WHERE s.created IS NOT NULL", "ORDER BY ar.sort_name", "artist")
                .Concat(lib.Albums(r.User.Id, "WHERE s.created IS NOT NULL", "ORDER BY s.created DESC", "album"))
                .Concat(lib.Tracks(r.User.Id, "WHERE s.created IS NOT NULL", "ORDER BY s.created DESC", "song")), "song");
        On("getStarred", r => Starred(r, "starred"));
        On("getStarred2", r => Starred(r, "starred2"));

        On("scrobble", r =>
        {
            if (!r.Bool("submission", true)) return null;
            using var c = db.Open();
            var now = SqliteExt.Now();
            foreach (var raw in r.All("id"))
            {
                var (_, id) = Req.ParseId(raw);
                c.Exec("""
                    UPDATE tracks SET play_count=play_count+1, last_played=$now WHERE id=$id;
                    UPDATE albums SET play_count=play_count+1, last_played=$now WHERE id=(SELECT album_id FROM tracks WHERE id=$id);
                    """, ("$id", id), ("$now", now));
            }
            return null;
        });

        // ---------- playlist ----------
        Node PlaylistNode(Microsoft.Data.Sqlite.SqliteDataReader rd) => Node.Item("playlist")
            .A("id", "pl-" + rd.GetInt64(0)).A("name", rd.GetString(1)).A("comment", rd.Str(2))
            .A("owner", rd.GetString(3)).A("public", false).A("songCount", rd.GetInt64(4)).A("duration", rd.GetInt64(5))
            .A("created", rd.GetString(6)).A("changed", rd.GetString(7)).A("coverArt", "pl-" + rd.GetInt64(0));

        const string PlaylistSql = """
            SELECT p.id, p.name, p.comment, u.name, COUNT(pt.track_id), COALESCE(SUM(t.duration),0), p.created, p.changed
            FROM playlists p JOIN users u ON u.id=p.user_id
            LEFT JOIN playlist_tracks pt ON pt.playlist_id=p.id LEFT JOIN tracks t ON t.id=pt.track_id
            """;

        Node? LoadPlaylist(Req r, long id, bool withEntries)
        {
            using var c = db.Open();
            using var cmd = c.Cmd(PlaylistSql + " WHERE p.id=$id AND (p.user_id=$u OR $admin) GROUP BY p.id",
                ("$id", id), ("$u", r.User.Id), ("$admin", r.User.IsAdmin));
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;
            var n = PlaylistNode(rd);
            if (withEntries)
                n.AddRange(lib.Tracks(r.User.Id, "JOIN playlist_tracks pt ON pt.track_id=t.id WHERE pt.playlist_id=$pid", "ORDER BY pt.pos", "entry", ("$pid", id)), "entry");
            return n;
        }

        void AddTracks(Microsoft.Data.Sqlite.SqliteConnection c, long pid, IEnumerable<string> ids)
        {
            var pos = (long)(c.Scalar("SELECT COALESCE(MAX(pos)+1,0) FROM playlist_tracks WHERE playlist_id=$p", ("$p", pid)) ?? 0L);
            foreach (var raw in ids)
            {
                var (_, tid) = Req.ParseId(raw);
                c.Exec("INSERT INTO playlist_tracks(playlist_id,pos,track_id) SELECT $p,$pos,id FROM tracks WHERE id=$t",
                    ("$p", pid), ("$pos", pos++), ("$t", tid));
            }
        }

        On("getPlaylists", r =>
        {
            using var c = db.Open();
            using var cmd = c.Cmd(PlaylistSql + " WHERE p.user_id=$u GROUP BY p.id ORDER BY p.name", ("$u", r.User.Id));
            using var rd = cmd.ExecuteReader();
            var list = new List<Node>();
            while (rd.Read()) list.Add(PlaylistNode(rd));
            return new Node("playlists").AddRange(list, "playlist");
        });

        On("getPlaylist", r => LoadPlaylist(r, Req.ParseId(r.Need("id")).Id, true) ?? throw ApiError.NotFound("Playlist"));

        On("createPlaylist", r =>
        {
            using var c = db.Open();
            long pid;
            if (r.Get("playlistId") is { } existing)
            {
                pid = Req.ParseId(existing).Id;
                if (LoadPlaylist(r, pid, false) is null) throw ApiError.NotFound("Playlist");
                c.Exec("DELETE FROM playlist_tracks WHERE playlist_id=$p", ("$p", pid));
            }
            else
            {
                pid = (long)c.Scalar("INSERT INTO playlists(user_id,name,created,changed) VALUES($u,$n,$c,$c) RETURNING id",
                    ("$u", r.User.Id), ("$n", r.Need("name")), ("$c", SqliteExt.Now()))!;
            }
            AddTracks(c, pid, r.All("songId"));
            c.Exec("UPDATE playlists SET changed=$c WHERE id=$p", ("$p", pid), ("$c", SqliteExt.Now()));
            return LoadPlaylist(r, pid, true);
        });

        On("updatePlaylist", r =>
        {
            var pid = Req.ParseId(r.Need("playlistId")).Id;
            if (LoadPlaylist(r, pid, false) is null) throw ApiError.NotFound("Playlist");
            using var c = db.Open();
            if (r.Get("name") is { } name) c.Exec("UPDATE playlists SET name=$n WHERE id=$p", ("$n", name), ("$p", pid));
            if (r.Get("comment") is { } comment) c.Exec("UPDATE playlists SET comment=$n WHERE id=$p", ("$n", comment), ("$p", pid));
            var remove = r.All("songIndexToRemove").Select(int.Parse).ToHashSet();
            if (remove.Count > 0)
            {
                // ricompatta le posizioni togliendo gli indici richiesti
                var keep = new List<long>();
                using (var cmd = c.Cmd("SELECT track_id FROM playlist_tracks WHERE playlist_id=$p ORDER BY pos", ("$p", pid)))
                using (var rd = cmd.ExecuteReader())
                    for (int i = 0; rd.Read(); i++) if (!remove.Contains(i)) keep.Add(rd.GetInt64(0));
                c.Exec("DELETE FROM playlist_tracks WHERE playlist_id=$p", ("$p", pid));
                AddTracks(c, pid, keep.Select(x => "tr-" + x));
            }
            AddTracks(c, pid, r.All("songIdToAdd"));
            c.Exec("UPDATE playlists SET changed=$c WHERE id=$p", ("$p", pid), ("$c", SqliteExt.Now()));
            return null;
        });

        On("deletePlaylist", r =>
        {
            using var c = db.Open();
            var n = c.Exec("DELETE FROM playlists WHERE id=$id AND (user_id=$u OR $admin)",
                ("$id", Req.ParseId(r.Need("id")).Id), ("$u", r.User.Id), ("$admin", r.User.IsAdmin));
            return n > 0 ? null : throw ApiError.NotFound("Playlist");
        });

        // ---------- utenti ----------
        static Node UserNode(User u) => Node.Item("user").A("username", u.Name).A("email", "")
            .A("scrobblingEnabled", true).A("adminRole", u.IsAdmin).A("settingsRole", true).A("downloadRole", true)
            .A("uploadRole", false).A("playlistRole", true).A("coverArtRole", true).A("commentRole", false)
            .A("podcastRole", false).A("streamRole", true).A("jukeboxRole", false).A("shareRole", false);

        On("getUser", r =>
        {
            var name = r.Get("username") ?? r.User.Name;
            if (!r.User.IsAdmin && !string.Equals(name, r.User.Name, StringComparison.OrdinalIgnoreCase)) throw ApiError.Forbidden();
            return UserNode(users.Get(name) ?? throw ApiError.NotFound("Utente"));
        });
        On("getUsers", r => r.User.IsAdmin ? new Node("users").AddRange(users.All().Select(UserNode), "user") : throw ApiError.Forbidden());
        On("createUser", r =>
        {
            if (!r.User.IsAdmin) throw ApiError.Forbidden();
            users.Create(r.Need("username"), Decode(r.Need("password")), r.Bool("adminRole", false));
            return null;
        });
        On("deleteUser", r =>
        {
            if (!r.User.IsAdmin) throw ApiError.Forbidden();
            var name = r.Need("username");
            if (string.Equals(name, r.User.Name, StringComparison.OrdinalIgnoreCase)) throw new ApiError(0, "Non puoi eliminare te stesso");
            return users.Delete(name) ? null : throw ApiError.NotFound("Utente");
        });
        On("changePassword", r =>
        {
            var name = r.Need("username");
            if (!r.User.IsAdmin && !string.Equals(name, r.User.Name, StringComparison.OrdinalIgnoreCase)) throw ApiError.Forbidden();
            return users.ChangePassword(name, Decode(r.Need("password"))) ? null : throw ApiError.NotFound("Utente");
        });

        // ---------- stub per client che li chiamano sempre ----------
        On("getArtistInfo", _ => new Node("artistInfo"));
        On("getArtistInfo2", _ => new Node("artistInfo2"));
        On("getAlbumInfo", _ => new Node("albumInfo"));
        On("getAlbumInfo2", _ => new Node("albumInfo"));
        On("getSimilarSongs", _ => new Node("similarSongs").AddRange([], "song"));
        On("getSimilarSongs2", _ => new Node("similarSongs2").AddRange([], "song"));
        On("getTopSongs", _ => new Node("topSongs").AddRange([], "song"));
        On("getLyrics", _ => new Node("lyrics"));
        On("getInternetRadioStations", _ => new Node("internetRadioStations").AddRange([], "internetRadioStation"));
        On("getPodcasts", _ => new Node("podcasts").AddRange([], "channel"));
        On("getBookmarks", _ => new Node("bookmarks").AddRange([], "bookmark"));
        On("getShares", _ => new Node("shares").AddRange([], "share"));
        On("getPlayQueue", _ => null);
        On("savePlayQueue", _ => null);

        // ---------- media ----------
        h["stream"] = async r => { await media.Stream(r, download: false); return Raw; };
        h["download"] = async r => { await media.Stream(r, download: true); return Raw; };
        h["getCoverArt"] = async r => { await media.CoverArt(r); return Raw; };
        h["getAvatar"] = _ => throw ApiError.NotFound();

        return h;
    }

    static string Decode(string p) => p.StartsWith("enc:", StringComparison.Ordinal)
        ? System.Text.Encoding.UTF8.GetString(Convert.FromHexString(p[4..])) : p;

    static Node ScanStatus(Scanner s, bool forceScanning = false) =>
        new Node("scanStatus").A("scanning", forceScanning || s.IsScanning).A("count", s.Count)
            .A("lastScan", s.LastScan?.ToString("yyyy-MM-ddTHH:mm:ssZ"));
}
