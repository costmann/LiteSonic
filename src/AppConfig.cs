namespace LiteSonic;

public sealed class AppConfig
{
    public string MusicFolder { get; set; } = "./music";
    public string DataFolder { get; set; } = "./data";
    public int Port { get; set; } = 4533;
    public string AdminUser { get; set; } = "admin";
    /// <summary>Se vuota al primo avvio viene generata e scritta nel log.</summary>
    public string? AdminPassword { get; set; }
    /// <summary>0 = solo scansione all'avvio e su richiesta (startScan).</summary>
    public int ScanIntervalMinutes { get; set; } = 60;
    /// <summary>Scansione incrementale automatica quando cambiano i file in MusicFolder (inotify).</summary>
    public bool WatchMusicFolder { get; set; } = true;
    /// <summary>Attesa dopo l'ultima modifica prima di scansionare (copie lunghe generano molti eventi).</summary>
    public int WatchDelaySeconds { get; set; } = 10;
    /// <summary>Vuoto = transcodifica disabilitata (lo streaming resta diretto).</summary>
    public string? FfmpegPath { get; set; } = "ffmpeg";
    public int MaxConcurrentTranscodes { get; set; } = 1;
    public int DefaultTranscodeBitRate { get; set; } = 128;

    public void Normalize()
    {
        MusicFolder = Path.GetFullPath(MusicFolder);
        DataFolder = Path.GetFullPath(DataFolder);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(Path.Combine(DataFolder, "covers"));
        if (MaxConcurrentTranscodes < 1) MaxConcurrentTranscodes = 1;
    }
}
