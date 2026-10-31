using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace LiteSonic.Subsonic;

public sealed class ApiError(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public static ApiError Missing(string p) => new(10, $"Parametro obbligatorio mancante: {p}");
    public static ApiError NotFound(string what = "Elemento") => new(70, $"{what} non trovato");
    public static ApiError Forbidden() => new(50, "Operazione non consentita");
}

/// <summary>Contesto di una chiamata: parametri (query + form POST) e utente autenticato.</summary>
public sealed class Req(HttpContext ctx, User user, Dictionary<string, StringValues> ps)
{
    public HttpContext Ctx { get; } = ctx;
    public User User { get; } = user;

    public string? Get(string name) => ps.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;
    public string Need(string name) => Get(name) is { Length: > 0 } v ? v : throw ApiError.Missing(name);
    public string[] All(string name) => ps.TryGetValue(name, out var v) ? v.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray() : [];
    public int Int(string name, int def) => int.TryParse(Get(name), out var i) ? i : def;
    public bool Bool(string name, bool def) => bool.TryParse(Get(name), out var b) ? b : def;

    /// <summary>"tr-12" → ("tr", 12). Accetta anche id numerici puri come brani.</summary>
    public static (string Kind, long Id) ParseId(string raw)
    {
        var dash = raw.IndexOf('-');
        if (dash > 0 && long.TryParse(raw.AsSpan(dash + 1), out var id)) return (raw[..dash], id);
        if (long.TryParse(raw, out id)) return ("tr", id);
        throw ApiError.NotFound();
    }
}

public static class Api
{
    public const string Version = "1.16.1";

    public static void Map(WebApplication app)
    {
        var handlers = Handlers.Build(app.Services);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LiteSonic.Api");
        RequestDelegate dispatch = async ctx =>
        {
            var method = (string)ctx.Request.RouteValues["method"]!;
            if (method.EndsWith(".view", StringComparison.OrdinalIgnoreCase)) method = method[..^5];

            var ps = new Dictionary<string, StringValues>(StringComparer.Ordinal);
            foreach (var (k, v) in ctx.Request.Query) ps[k] = v;
            if (ctx.Request.HasFormContentType)
                foreach (var (k, v) in await ctx.Request.ReadFormAsync(ctx.RequestAborted)) ps[k] = StringValues.Concat(ps.GetValueOrDefault(k), v);

            bool json = ps.TryGetValue("f", out var f) && (f == "json" || f == "jsonp");
            var users = ctx.RequestServices.GetRequiredService<Users>();
            var user = users.Authenticate(Val(ps, "u"), Val(ps, "p"), Val(ps, "t"), Val(ps, "s"));
            if (user is null)
            {
                var name = Val(ps, "u");
                log.LogWarning("Login fallito da {Ip}: metodo={Method} utente='{User}' (esiste={Exists}) auth={Auth} client={Client} v={Version}",
                    ctx.Connection.RemoteIpAddress, method, name, name is not null && users.Get(name) is not null,
                    Val(ps, "t") is not null ? "token" : Val(ps, "p") is { } p ? (p.StartsWith("enc:") ? "enc" : "plain") : Val(ps, "apiKey") is not null ? "apiKey" : "nessuna",
                    Val(ps, "c"), Val(ps, "v"));
                await Write(ctx, json, null, new ApiError(40, "Utente o password errati"));
                return;
            }

            log.LogInformation("{Ip} {User} {Method} client={Client}", ctx.Connection.RemoteIpAddress, user.Name, method, Val(ps, "c"));
            if (!handlers.TryGetValue(method, out var h))
            {
                log.LogWarning("Metodo non supportato richiesto: {Method}", method);
                await Write(ctx, json, null, new ApiError(0, $"Metodo non supportato: {method}"));
                return;
            }

            try
            {
                var result = await h(new Req(ctx, user, ps));
                if (!ReferenceEquals(result, Handlers.Raw)) await Write(ctx, json, result, null);
            }
            catch (ApiError e) when (!ctx.Response.HasStarted)
            {
                await Write(ctx, json, null, e);
            }
        };

        app.Map("/rest/{method}", dispatch);

        // Richieste fuori da /rest (indirizzo sbagliato nel client): le registriamo per la diagnosi
        app.MapFallback("{*path}", ctx =>
        {
            log.LogWarning("Richiesta fuori dall'API da {Ip}: {Verb} {Path}{Query}", ctx.Connection.RemoteIpAddress,
                ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString.HasValue ? " (con parametri)" : "");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    static string? Val(Dictionary<string, StringValues> ps, string k) => ps.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null;

    static async Task Write(HttpContext ctx, bool json, Node? content, ApiError? err)
    {
        var root = new Node("subsonic-response")
            .A("status", err is null ? "ok" : "failed")
            .A("version", Version)
            .A("type", "litesonic")
            .A("serverVersion", "0.1.0")
            .A("openSubsonic", true);
        if (err is not null) root.Add(new Node("error").A("code", err.Code).A("message", err.Message));
        else if (content is not null)
        {
            // Nodo "contenitore" vuoto per le risposte che sono solo un array (es. openSubsonicExtensions)
            if (content.Name == "" && content.EmptyArray is not null) root.AddRange(content.Children ?? [], content.EmptyArray);
            else
            {
                content.IsListItem = false; // il contenuto principale è sempre un oggetto
                root.Add(content);
            }
        }

        if (json)
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            using (var w = new Utf8JsonWriter(ctx.Response.BodyWriter))
                Node.WriteJson(w, root);
            await ctx.Response.BodyWriter.FlushAsync(ctx.RequestAborted);
        }
        else
        {
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            await ctx.Response.WriteAsync(Node.ToXml(root), Encoding.UTF8, ctx.RequestAborted);
        }
    }
}
