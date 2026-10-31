using System.Diagnostics;

namespace LiteSonic.Subsonic;

/// <summary>
/// Streaming e copertine.
/// - Streaming diretto: sendfile del kernel + Range (seek), nessun buffer in RAM.
/// - Transcodifica: ffmpeg esterno in pipe, con un limite di processi concorrenti.
/// - Copertine incorporate: estratte una volta sola e messe in cache su disco, non in RAM.
/// </summary>
public sealed class Media(AppConfig cfg, Db db, ILogger<Media> log)
{
    readonly SemaphoreSlim _transcodes = new(cfg.MaxConcurrentTranscodes);
    readonly string? _ffmpeg = ResolveExecutable(cfg.FfmpegPath);
    readonly string _coverCache = Path.Combine(cfg.DataFolder, "covers");

    public bool CanTranscode => _ffmpeg is not null;

    public async Task Stream(Req r, bool download)
    {
        var (_, id) = Req.ParseId(r.Need("id"));
        string path, suffix, contentType;
        long bitrate;
        using (var c = db.Open())
        using (var cmd = c.Cmd("SELECT path, suffix, content_type, bitrate FROM tracks WHERE id=$id", ("$id", id)))
        using (var rd = cmd.ExecuteReader())
        {
            if (!rd.Read()) throw ApiError.NotFound("Brano");
            (path, suffix, contentType, bitrate) = (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt64(3));
        }
        if (!File.Exists(path)) throw ApiError.NotFound("File");

        var format = r.Get("format");
        int maxBitRate = r.Int("maxBitRate", 0);
        int timeOffset = r.Int("timeOffset", 0);
        bool otherFormat = format is { Length: > 0 } && format != "raw" && !format.Equals(suffix, StringComparison.OrdinalIgnoreCase);
        bool lowerBitRate = maxBitRate > 0 && bitrate > maxBitRate;

        if (!download && _ffmpeg is not null && (otherFormat || lowerBitRate || timeOffset > 0))
        {
            await Transcode(r.Ctx, path, format, maxBitRate, timeOffset);
            return;
        }

        var result = Results.File(path, contentType,
            fileDownloadName: download ? Path.GetFileName(path) : null,
            lastModified: File.GetLastWriteTimeUtc(path),
            enableRangeProcessing: true);
        await result.ExecuteAsync(r.Ctx);
    }

    async Task Transcode(HttpContext ctx, string path, string? format, int maxBitRate, int timeOffset)
    {
        var opus = format is "opus" or "ogg";
        int br = maxBitRate > 0 ? Math.Min(maxBitRate, 320) : cfg.DefaultTranscodeBitRate;

        // Su hardware debole non si accodano infinite transcodifiche: dopo 10 s si risponde 503
        if (!await _transcodes.WaitAsync(TimeSpan.FromSeconds(10), ctx.RequestAborted))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var psi = new ProcessStartInfo(_ffmpeg!)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-threads", "1" }) psi.ArgumentList.Add(a);
        if (timeOffset > 0) { psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(timeOffset.ToString()); }
        foreach (var a in new[] { "-i", path, "-map", "0:a:0", "-vn", "-map_metadata", "-1" }) psi.ArgumentList.Add(a);
        foreach (var a in opus
                     ? new[] { "-c:a", "libopus", "-b:a", $"{br}k", "-f", "ogg", "pipe:1" }
                     : new[] { "-c:a", "libmp3lame", "-b:a", $"{br}k", "-f", "mp3", "pipe:1" })
            psi.ArgumentList.Add(a);

        Process? p = null;
        try
        {
            p = Process.Start(psi)!;
            p.StandardInput.Close();
            ctx.Response.ContentType = opus ? "audio/ogg" : "audio/mpeg";
            ctx.Response.Headers.CacheControl = "no-cache";
            await p.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 32 * 1024, ctx.RequestAborted);
        }
        catch (OperationCanceledException) { /* il client ha chiuso / cambiato brano */ }
        catch (IOException) { }
        catch (Exception e) { log.LogWarning(e, "Transcodifica fallita per {Path}", path); }
        finally
        {
            if (p is not null)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                p.Dispose();
            }
            _transcodes.Release();
        }
    }

    public async Task CoverArt(Req r)
    {
        var (kind, id) = Req.ParseId(r.Need("id"));
        using var c = db.Open();
        long? albumId = kind switch
        {
            "al" => id,
            "tr" => (long?)c.Scalar("SELECT album_id FROM tracks WHERE id=$id", ("$id", id)),
            "ar" => (long?)c.Scalar("SELECT id FROM albums WHERE artist_id=$id ORDER BY year DESC LIMIT 1", ("$id", id)),
            "pl" => (long?)c.Scalar("SELECT t.album_id FROM playlist_tracks pt JOIN tracks t ON t.id=pt.track_id WHERE pt.playlist_id=$id ORDER BY pt.pos LIMIT 1", ("$id", id)),
            _ => null,
        };
        var file = albumId is null ? null : AlbumCover(c, albumId.Value);
        if (file is null)
        {
            r.Ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        r.Ctx.Response.Headers.CacheControl = "public, max-age=604800";
        await Results.File(file, Mime.ForExtension(Path.GetExtension(file)), lastModified: File.GetLastWriteTimeUtc(file))
            .ExecuteAsync(r.Ctx);
    }

    string? AlbumCover(Microsoft.Data.Sqlite.SqliteConnection c, long albumId)
    {
        if (c.Scalar("SELECT cover_path FROM albums WHERE id=$id", ("$id", albumId)) is string folderCover && File.Exists(folderCover))
            return folderCover;

        var cacheBase = Path.Combine(_coverCache, "al-" + albumId);
        foreach (var ext in new[] { ".jpg", ".png" })
            if (File.Exists(cacheBase + ext)) return cacheBase + ext;

        if (c.Scalar("SELECT path FROM tracks WHERE album_id=$id AND has_cover=1 LIMIT 1", ("$id", albumId)) is not string trackPath)
            return null;

        try
        {
            using var f = TagLib.File.Create(trackPath, TagLib.ReadStyle.None);
            var pic = f.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? f.Tag.Pictures.FirstOrDefault();
            if (pic is null) return null;
            var target = cacheBase + (pic.MimeType?.Contains("png") == true ? ".png" : ".jpg");
            var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, pic.Data.Data);
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        catch (Exception e)
        {
            log.LogDebug("Copertina non estraibile da {Path}: {Err}", trackPath, e.Message);
            return null;
        }
    }

    /// <summary>Svuota la cache copertine (dopo una scansione completa).</summary>
    public void ClearCoverCache()
    {
        foreach (var f in Directory.EnumerateFiles(_coverCache)) try { File.Delete(f); } catch { }
    }

    static string? ResolveExecutable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var ext in exts)
            {
                var full = Path.Combine(dir, name + ext);
                if (File.Exists(full)) return full;
            }
        return null;
    }
}
