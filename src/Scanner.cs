using Microsoft.Data.Sqlite;

namespace LiteSonic;

/// <summary>
/// Scansione incrementale della libreria: legge i tag solo dei file nuovi o modificati
/// (path + mtime + size), elabora un file alla volta e scrive in transazioni da 500.
/// Non tiene in memoria l'elenco dei brani: la RAM usata non cresce con la libreria.
/// </summary>
public sealed class Scanner(AppConfig cfg, Db db, ILogger<Scanner> log)
{
    static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".m4b", ".mp4", ".aac", ".wav", ".wma", ".ape", ".wv", ".aif", ".aiff", ".dsf", ".mpc" };

    static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart"];
    static readonly string[] CoverExt = [".jpg", ".jpeg", ".png", ".webp"];

    int _running, _pending;
    public bool IsScanning => Volatile.Read(ref _running) == 1;
    public long Count { get; private set; }
    public DateTime? LastScan { get; private set; }

    /// <summary>
    /// Avvia una scansione. Se ce n'è già una in corso la richiesta non va persa:
    /// ne viene eseguita un'altra (incrementale) appena la corrente termina.
    /// </summary>
    public Task StartAsync(bool full = false)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Volatile.Write(ref _pending, 1);
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            while (true)
            {
                Volatile.Write(ref _pending, 0);
                try { Scan(full); }
                catch (Exception e) { log.LogError(e, "Scansione fallita"); }
                full = false;
                if (Volatile.Read(ref _pending) == 1) continue;
                Volatile.Write(ref _running, 0);
                // richiesta arrivata tra il controllo e il rilascio: riprendila se nessun altro l'ha fatto
                if (Volatile.Read(ref _pending) == 1 && Interlocked.Exchange(ref _running, 1) == 0) continue;
                return;
            }
        });
    }

    void Scan(bool full)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!Directory.Exists(cfg.MusicFolder))
        {
            log.LogWarning("Cartella musica inesistente: {Folder}", cfg.MusicFolder);
            return;
        }

        long scanId = DateTime.UtcNow.Ticks;
        Count = 0;
        int changed = 0;
        var now = SqliteExt.Now();

        using var c = db.Open();
        using var sel = c.Cmd("SELECT id, mtime, size FROM tracks WHERE path=$p", ("$p", ""));
        using var touch = c.Cmd("UPDATE tracks SET scan_id=$s WHERE id=$id", ("$s", scanId), ("$id", 0L));
        using var upsert = c.Cmd("""
            INSERT INTO tracks(path, mtime, size, album_id, artist_id, artist, title, track_no, disc_no, year, genre,
                               duration, bitrate, suffix, content_type, has_cover, created, scan_id)
            VALUES($path,$mtime,$size,$album,$artistId,$artist,$title,$track,$disc,$year,$genre,
                   $dur,$br,$suffix,$ct,$cover,$created,$scan)
            ON CONFLICT(path) DO UPDATE SET
                mtime=excluded.mtime, size=excluded.size, album_id=excluded.album_id, artist_id=excluded.artist_id,
                artist=excluded.artist, title=excluded.title, track_no=excluded.track_no, disc_no=excluded.disc_no,
                year=excluded.year, genre=excluded.genre, duration=excluded.duration, bitrate=excluded.bitrate,
                suffix=excluded.suffix, content_type=excluded.content_type, has_cover=excluded.has_cover,
                scan_id=excluded.scan_id
            """);
        foreach (var p in new[] { "$path", "$mtime", "$size", "$album", "$artistId", "$artist", "$title", "$track", "$disc", "$year", "$genre", "$dur", "$br", "$suffix", "$ct", "$cover", "$created", "$scan" })
            upsert.Parameters.Add(new SqliteParameter(p, DBNull.Value));

        // Cache piccole e limitate alla scansione corrente
        var artistIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var albumIds = new Dictionary<(long, string), long>();
        string? lastDir = null, lastDirCover = null;

        var tx = c.BeginTransaction();
        sel.Transaction = touch.Transaction = upsert.Transaction = tx;
        int inTx = 0;

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var path in Directory.EnumerateFiles(cfg.MusicFolder, "*", opts))
        {
            var ext = Path.GetExtension(path);
            if (!AudioExt.Contains(ext)) continue;

            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            long mtime = fi.LastWriteTimeUtc.Ticks;
            Count++;

            sel.Parameters["$p"].Value = path;
            long? existingId = null;
            using (var r = sel.ExecuteReader())
            {
                if (r.Read())
                {
                    existingId = r.GetInt64(0);
                    if (!full && r.GetInt64(1) == mtime && r.GetInt64(2) == fi.Length)
                    {
                        r.Close();
                        touch.Parameters["$id"].Value = existingId;
                        touch.ExecuteNonQuery();
                        goto next;
                    }
                }
            }

            var dir = Path.GetDirectoryName(path)!;
            if (dir != lastDir) { lastDir = dir; lastDirCover = FindDirCover(dir); }

            var m = ReadTags(path, ext);
            long artistId = GetArtist(c, tx, artistIds, m.AlbumArtist);
            long albumId = GetAlbum(c, tx, albumIds, artistId, m.Album, m.Year, m.Genre, lastDirCover, now);

            var ps = upsert.Parameters;
            ps["$path"].Value = path; ps["$mtime"].Value = mtime; ps["$size"].Value = fi.Length;
            ps["$album"].Value = albumId; ps["$artistId"].Value = artistId; ps["$artist"].Value = m.Artist;
            ps["$title"].Value = m.Title;
            ps["$track"].Value = m.Track > 0 ? m.Track : DBNull.Value;
            ps["$disc"].Value = m.Disc > 0 ? m.Disc : DBNull.Value;
            ps["$year"].Value = m.Year > 0 ? m.Year : DBNull.Value;
            ps["$genre"].Value = (object?)m.Genre ?? DBNull.Value;
            ps["$dur"].Value = m.Duration; ps["$br"].Value = m.BitRate;
            ps["$suffix"].Value = ext.TrimStart('.').ToLowerInvariant();
            ps["$ct"].Value = Mime.ForExtension(ext);
            ps["$cover"].Value = m.HasCover ? 1 : 0;
            ps["$created"].Value = now; ps["$scan"].Value = scanId;
            upsert.ExecuteNonQuery();
            changed++;

        next:
            if (++inTx >= 500)
            {
                tx.Commit(); tx.Dispose();
                tx = c.BeginTransaction();
                sel.Transaction = touch.Transaction = upsert.Transaction = tx;
                inTx = 0;
            }
        }
        tx.Commit(); tx.Dispose();

        // Pulizia: brani spariti, album/artisti orfani, contatori denormalizzati
        int removed = c.Exec("DELETE FROM tracks WHERE scan_id<>$s", ("$s", scanId));
        c.Exec("""
            DELETE FROM albums WHERE id NOT IN (SELECT DISTINCT album_id FROM tracks);
            DELETE FROM artists WHERE id NOT IN (SELECT DISTINCT artist_id FROM albums);
            UPDATE albums SET
                song_count=(SELECT COUNT(*) FROM tracks t WHERE t.album_id=albums.id),
                duration=(SELECT COALESCE(SUM(duration),0) FROM tracks t WHERE t.album_id=albums.id);
            UPDATE artists SET album_count=(SELECT COUNT(*) FROM albums a WHERE a.artist_id=artists.id);
            PRAGMA optimize;
            """);

        LastScan = DateTime.UtcNow;
        log.LogInformation("Scansione completata in {Ms} ms: {Count} brani, {Changed} aggiornati, {Removed} rimossi",
            sw.ElapsedMilliseconds, Count, changed, removed);
    }

    static long GetArtist(SqliteConnection c, SqliteTransaction tx, Dictionary<string, long> cache, string name)
    {
        if (cache.TryGetValue(name, out var id)) return id;
        using var cmd = c.Cmd("""
            INSERT INTO artists(name, sort_name) VALUES($n, $s) ON CONFLICT(name) DO NOTHING;
            SELECT id FROM artists WHERE name=$n;
            """, ("$n", name), ("$s", SortName(name)));
        cmd.Transaction = tx;
        id = (long)cmd.ExecuteScalar()!;
        if (cache.Count > 5000) cache.Clear();
        return cache[name] = id;
    }

    static long GetAlbum(SqliteConnection c, SqliteTransaction tx, Dictionary<(long, string), long> cache,
        long artistId, string name, int year, string? genre, string? cover, string now)
    {
        var key = (artistId, name.ToLowerInvariant());
        if (cache.TryGetValue(key, out var id)) return id;
        using var cmd = c.Cmd("""
            INSERT INTO albums(artist_id, name, year, genre, cover_path, created) VALUES($a,$n,$y,$g,$c,$now)
            ON CONFLICT(artist_id, name) DO UPDATE SET
                year=COALESCE(excluded.year, albums.year),
                genre=COALESCE(excluded.genre, albums.genre),
                cover_path=COALESCE(excluded.cover_path, albums.cover_path);
            SELECT id FROM albums WHERE artist_id=$a AND name=$n;
            """, ("$a", artistId), ("$n", name), ("$y", year > 0 ? year : null), ("$g", genre), ("$c", cover), ("$now", now));
        cmd.Transaction = tx;
        id = (long)cmd.ExecuteScalar()!;
        if (cache.Count > 5000) cache.Clear();
        return cache[key] = id;
    }

    static string? FindDirCover(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                var e = Path.GetExtension(f);
                if (CoverExt.Contains(e, StringComparer.OrdinalIgnoreCase) && CoverNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { }
        return null;
    }

    public static string SortName(string name)
    {
        foreach (var a in new[] { "The ", "Il ", "Lo ", "La ", "I ", "Gli ", "Le " })
            if (name.Length > a.Length && name.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                return name[a.Length..];
        return name;
    }

    record struct Meta(string Title, string Artist, string AlbumArtist, string Album, int Track, int Disc,
        int Year, string? Genre, int Duration, int BitRate, bool HasCover);

    Meta ReadTags(string path, string ext)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);
        var fallbackAlbum = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Unknown Album";
        try
        {
            using var f = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var t = f.Tag;
            var artist = Clean(t.JoinedPerformers) ?? Clean(t.FirstAlbumArtist) ?? "Unknown Artist";
            var albumArtist = Clean(t.FirstAlbumArtist) ?? Clean(t.FirstPerformer) ?? "Unknown Artist";
            return new Meta(
                Clean(t.Title) ?? fallbackTitle, artist, albumArtist,
                Clean(t.Album) ?? fallbackAlbum,
                (int)t.Track, (int)t.Disc, (int)t.Year, Clean(t.FirstGenre),
                (int)f.Properties.Duration.TotalSeconds, f.Properties.AudioBitrate,
                t.Pictures is { Length: > 0 });
        }
        catch (Exception e)
        {
            log.LogDebug("Tag illeggibili in {Path}: {Err}", path, e.Message);
            return new Meta(fallbackTitle, "Unknown Artist", "Unknown Artist", fallbackAlbum, 0, 0, 0, null, 0, 0, false);
        }
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Replace("\0", "");
}

public sealed class ScanService(Scanner scanner, AppConfig cfg, ILogger<ScanService> log) : BackgroundService
{
    FileSystemWatcher? _watcher;
    Timer? _debounce;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await scanner.StartAsync();
        if (cfg.WatchMusicFolder) StartWatcher();
        if (cfg.ScanIntervalMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(cfg.ScanIntervalMinutes));
        while (await timer.WaitForNextTickAsync(ct))
            await scanner.StartAsync();
    }

    void StartWatcher()
    {
        if (!Directory.Exists(cfg.MusicFolder)) return;
        try
        {
            var delay = TimeSpan.FromSeconds(Math.Max(1, cfg.WatchDelaySeconds));
            _debounce = new Timer(_ => scanner.StartAsync());
            _watcher = new FileSystemWatcher(cfg.MusicFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler onChange = (_, _) => _debounce.Change(delay, Timeout.InfiniteTimeSpan);
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Changed += onChange;
            _watcher.Renamed += (_, _) => _debounce.Change(delay, Timeout.InfiniteTimeSpan);
            _watcher.Error += (_, e) =>
            {
                // tipicamente buffer pieno: una scansione recupera comunque tutto
                log.LogWarning("File watcher: {Err}", e.GetException().Message);
                _debounce.Change(delay, Timeout.InfiniteTimeSpan);
            };
            _watcher.EnableRaisingEvents = true;
            log.LogInformation("File watcher attivo su {Folder}", cfg.MusicFolder);
        }
        catch (Exception e)
        {
            // es. limite fs.inotify.max_user_watches raggiunto: resta la scansione periodica
            log.LogWarning("File watcher non disponibile ({Err}): uso solo la scansione periodica", e.Message);
            _watcher?.Dispose(); _watcher = null;
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        base.Dispose();
    }
}

public static class Mime
{
    public static string ForExtension(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "ogg" or "oga" => "audio/ogg",
        "opus" => "audio/ogg",
        "m4a" or "m4b" or "mp4" or "aac" => "audio/mp4",
        "wav" => "audio/wav",
        "wma" => "audio/x-ms-wma",
        "aif" or "aiff" => "audio/aiff",
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
