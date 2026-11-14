using Shiny.Net.Discovery;

namespace ShinyCommander.Services;

public enum NetworkServiceKind
{
    Smb,
    Sftp,
    WebDav,
    WebDavs,
    Ftp
}

/// <summary>A server that announced itself on the local network.</summary>
public sealed record NetworkServer(string Name, string Host, int Port, NetworkServiceKind Kind, string? Path)
{
    public string KindText => this.Kind switch
    {
        NetworkServiceKind.Smb => "SMB",
        NetworkServiceKind.Sftp => "SFTP",
        NetworkServiceKind.WebDav => "WebDAV",
        NetworkServiceKind.WebDavs => "WebDAV (https)",
        _ => "FTP"
    };

    public string Icon => this.Kind == NetworkServiceKind.Smb ? "database" : "wifi";
}

/// <summary>Bonjour / mDNS: file servers nearby - SMB shares (Macs, NAS boxes, Windows with Bonjour), SFTP, WebDAV and FTP servers.</summary>
public sealed class NetworkBrowser(IMdnsManager mdns)
{
    static readonly (string Type, NetworkServiceKind Kind)[] Types =
    [
        ("_smb._tcp", NetworkServiceKind.Smb),
        ("_sftp-ssh._tcp", NetworkServiceKind.Sftp),
        ("_webdav._tcp", NetworkServiceKind.WebDav),
        ("_webdavs._tcp", NetworkServiceKind.WebDavs),
        ("_ftp._tcp", NetworkServiceKind.Ftp)
    ];

    /// <summary>One scan of every type at once; servers that answer for several protocols appear once per protocol.</summary>
    public async Task<IReadOnlyList<NetworkServer>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var scans = Types.Select(async t =>
        {
            try
            {
                var found = await mdns.BrowseOnce(t.Type, duration, ct);
                return found
                    .Where(x => x.IsResolved)
                    .Select(x => new NetworkServer(x.InstanceName, (x.HostName ?? x.Addresses[0].ToString()).TrimEnd('.'), x.Port, t.Kind, x.GetTxt("path")))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // a type the OS refused (missing from NSBonjourServices) shouldn't sink the rest
                return [];
            }
        });

        var all = await Task.WhenAll(scans);
        return all
            .SelectMany(x => x)
            .DistinctBy(x => (x.Host.ToLowerInvariant(), x.Kind))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Kind)
            .ToList();
    }
}
