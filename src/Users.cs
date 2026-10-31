using System.Security.Cryptography;
using System.Text;

namespace LiteSonic;

public sealed record User(long Id, string Name, bool IsAdmin);

/// <summary>
/// Il protocollo Subsonic (auth con token = md5(password + salt)) richiede la password in chiaro
/// lato server: la salviamo cifrata AES con una chiave locale in DataFolder/secret.key.
/// </summary>
public sealed class Users
{
    readonly Db _db;
    readonly byte[] _key;
    readonly ILogger<Users> _log;

    public Users(AppConfig cfg, Db db, ILogger<Users> log)
    {
        _db = db;
        _log = log;
        var keyFile = Path.Combine(cfg.DataFolder, "secret.key");
        if (!File.Exists(keyFile))
        {
            File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(keyFile);

        using var c = db.Open();
        if ((long)c.Scalar("SELECT COUNT(*) FROM users")! == 0)
        {
            var pwd = string.IsNullOrEmpty(cfg.AdminPassword) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() : cfg.AdminPassword;
            Create(cfg.AdminUser, pwd, true);
            if (string.IsNullOrEmpty(cfg.AdminPassword))
                log.LogWarning("Creato utente '{User}' con password generata: {Pwd}  (cambiala con changePassword)", cfg.AdminUser, pwd);
        }
        else if (!string.IsNullOrEmpty(cfg.AdminPassword) && Get(cfg.AdminUser) is null)
        {
            // AdminUser cambiato in configurazione dopo il primo avvio: lo aggiungiamo senza toccare gli utenti esistenti
            Create(cfg.AdminUser, cfg.AdminPassword, true);
            log.LogInformation("Creato utente amministratore '{User}' dalla configurazione", cfg.AdminUser);
        }
        else if (!string.IsNullOrEmpty(cfg.AdminPassword) && Authenticate(cfg.AdminUser, cfg.AdminPassword, null, null) is null)
        {
            log.LogWarning("La password di '{User}' in configurazione è diversa da quella salvata: vale quella salvata (cambiala con changePassword)", cfg.AdminUser);
        }
    }

    public User? Authenticate(string? user, string? password, string? token, string? salt)
    {
        if (string.IsNullOrEmpty(user)) return null;
        using var c = _db.Open();
        using var cmd = c.Cmd("SELECT id, name, password, is_admin FROM users WHERE name=$n", ("$n", user));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var stored = Decrypt(r.GetString(2));
        bool ok;
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(salt))
        {
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(stored + salt)));
            ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.ToLowerInvariant()), Encoding.ASCII.GetBytes(token.ToLowerInvariant()));
        }
        else if (password is not null)
        {
            if (password.StartsWith("enc:", StringComparison.Ordinal))
            {
                try { password = Encoding.UTF8.GetString(Convert.FromHexString(password[4..])); }
                catch (FormatException) { return null; }
            }
            ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(stored));
        }
        else ok = false;
        return ok ? new User(r.GetInt64(0), r.GetString(1), r.GetInt64(3) == 1) : null;
    }

    public void Create(string name, string password, bool admin)
    {
        using var c = _db.Open();
        c.Exec("INSERT INTO users(name, password, is_admin) VALUES($n,$p,$a)", ("$n", name), ("$p", Encrypt(password)), ("$a", admin ? 1 : 0));
    }

    public bool ChangePassword(string name, string password)
    {
        using var c = _db.Open();
        return c.Exec("UPDATE users SET password=$p WHERE name=$n", ("$n", name), ("$p", Encrypt(password))) > 0;
    }

    public bool Delete(string name)
    {
        using var c = _db.Open();
        return c.Exec("DELETE FROM users WHERE name=$n", ("$n", name)) > 0;
    }

    public List<User> All()
    {
        using var c = _db.Open();
        using var cmd = c.Cmd("SELECT id, name, is_admin FROM users ORDER BY name");
        using var r = cmd.ExecuteReader();
        var list = new List<User>();
        while (r.Read()) list.Add(new User(r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1));
        return list;
    }

    public User? Get(string name) => All().FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    string Encrypt(string plain)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();
        var data = aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), aes.IV);
        return Convert.ToBase64String([.. aes.IV, .. data]);
    }

    string Decrypt(string stored)
    {
        var raw = Convert.FromBase64String(stored);
        using var aes = Aes.Create();
        aes.Key = _key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(raw.AsSpan(16), raw.AsSpan(0, 16)));
    }
}
