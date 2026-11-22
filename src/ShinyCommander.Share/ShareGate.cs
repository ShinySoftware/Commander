using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer;

namespace ShinyCommander.Share;

/// <summary>
/// Keys, blocked addresses and the guess limit. A visitor proves they have a share's key once - typed on the key
/// page, in the link (<c>?key=</c>), as a WebDAV client's Basic password or an <c>X-Share-Key</c> header - and gets a
/// cookie for that share, so the browser doesn't ask again. Ten wrong keys from one address inside ten minutes lock
/// it out for ten minutes.
/// </summary>
sealed class ShareGate
{
    const int MaxFailures = 10;
    static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    // signs the cookies; new every run, so cookies never outlive the server (nothing is restored on the next launch)
    readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    readonly ConcurrentDictionary<string, Failures> failures = new();
    readonly ConcurrentDictionary<string, byte> blocked = new();

    sealed class Failures
    {
        public int Count;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    public IReadOnlyCollection<string> Blocked => this.blocked.Keys.ToList();
    public bool IsBlocked(string ip) => this.blocked.ContainsKey(ip);
    public void Block(string ip) => this.blocked[ip] = 0;
    public void Unblock(string ip) => this.blocked.TryRemove(ip, out _);

    public static string CookieName(ServedFolder share) => "sk_" + share.Id;

    /// <summary>The cookie value that proves the key - tied to the key, so changing the key signs everyone out.</summary>
    public string Token(ServedFolder share)
    {
        var mac = HMACSHA256.HashData(this.secret, Encoding.UTF8.GetBytes(share.Id + "\n" + share.Options.Key));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool IsLockedOut(string ip, DateTimeOffset now)
        => this.failures.TryGetValue(ip, out var f) && f.LockedUntil is { } until && until > now;

    /// <summary>True when the request already carries the key (cookie, header or Basic password).</summary>
    public bool HasAccess(HttpContext ctx, ServedFolder share)
    {
        if (!share.HasKey)
            return true;

        if (ctx.Request.Cookies.TryGetValue(CookieName(share), out var cookie) && FixedEquals(cookie, this.Token(share)))
            return true;

        var header = ctx.Request.Headers.GetFirst("X-Share-Key");
        if (header != null && FixedEquals(header, share.Options.Key!))
            return true;

        var basic = BasicPassword(ctx);
        return basic != null && FixedEquals(basic, share.Options.Key!);
    }

    /// <summary>Checks a key given on purpose (the key page, or <c>?key=</c>), counting a wrong one.</summary>
    public bool TryKey(string ip, ServedFolder share, string? key, DateTimeOffset now)
    {
        if (key != null && FixedEquals(key.Trim(), share.Options.Key!))
        {
            this.failures.TryRemove(ip, out _);
            return true;
        }

        var f = this.failures.GetOrAdd(ip, _ => new Failures { WindowStart = now });
        lock (f)
        {
            if (now - f.WindowStart > FailureWindow)
            {
                f.WindowStart = now;
                f.Count = 0;
            }
            f.Count++;
            if (f.Count >= MaxFailures)
                f.LockedUntil = now + FailureWindow;
        }
        return false;
    }

    /// <summary>Remembers the key for this browser and this share.</summary>
    public void GrantCookie(HttpContext ctx, ServedFolder share)
        => ctx.Response.Cookies.Append(CookieName(share), this.Token(share), new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps
        });

    static string? BasicPassword(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.GetFirst("Authorization");
        if (auth == null || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? decoded : decoded[(colon + 1)..];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null)
            return "?";

        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }

    public static bool IsLoopback(HttpContext ctx)
        => !ctx.Connection.IsTunneled && ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
}
