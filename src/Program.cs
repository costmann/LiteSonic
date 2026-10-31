using LiteSonic;
using LiteSonic.Subsonic;

// CreateSlimBuilder: niente HTTPS/HTTP3/IIS/EventLog di default -> meno assembly caricati e meno RAM
var builder = WebApplication.CreateSlimBuilder(args);

var cfg = new AppConfig();
builder.Configuration.GetSection("LiteSonic").Bind(cfg);
cfg.Normalize();

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(cfg.Port);
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 1024 * 1024; // nessun upload: 1 MB basta per i form POST
    k.Limits.MaxConcurrentConnections = 200;
});

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Users>();
builder.Services.AddSingleton<Scanner>();
builder.Services.AddSingleton<Library>();
builder.Services.AddSingleton<Media>();
builder.Services.AddHostedService<ScanService>();

var app = builder.Build();

var log = app.Services.GetRequiredService<ILogger<Program>>();
app.Services.GetRequiredService<Users>(); // crea l'admin al primo avvio
log.LogInformation("LiteSonic su http://0.0.0.0:{Port}  musica: {Music}  dati: {Data}  ffmpeg: {Ffmpeg}",
    cfg.Port, cfg.MusicFolder, cfg.DataFolder, app.Services.GetRequiredService<Media>().CanTranscode ? "sì" : "no");

app.MapGet("/", () => Results.Text("LiteSonic è attivo. Collega un client Subsonic a questo indirizzo.\n"));
Api.Map(app);

app.Run();
