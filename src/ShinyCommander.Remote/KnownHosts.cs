namespace ShinyCommander.Remote;

/// <summary>
/// SSH host keys seen before, trusted on first use like OpenSSH: a new server's key is remembered, a known server
/// presenting a different key is refused (it may be someone in the middle). Kept in the app's own file, one
/// "host:port key-type sha256" line each, so ~/.ssh/known_hosts is never written to.
/// </summary>
public sealed class KnownHosts(string file)
{
    readonly object gate = new();

    public KnownHosts() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShinyCommander", "known_hosts")) { }

    /// <summary>True when the key matches, or the host is new (and is now remembered).</summary>
    public bool Check(string host, int port, string keyType, string fingerprint, out string? previous)
    {
        var id = $"{host.ToLowerInvariant()}:{port}";
        lock (this.gate)
        {
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
            var known = lines
                .Select(x => x.Split(' ', 3))
                .FirstOrDefault(x => x.Length == 3 && x[0] == id && x[1] == keyType);

            previous = known?[2];
            if (known != null)
                return known[2] == fingerprint;

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            lines.Add($"{id} {keyType} {fingerprint}");
            File.WriteAllLines(file, lines);
            return true;
        }
    }

    /// <summary>Forgets a host's keys - after the user accepts that its key really did change.</summary>
    public void Forget(string host, int port)
    {
        var id = $"{host.ToLowerInvariant()}:{port} ";
        lock (this.gate)
        {
            if (File.Exists(file))
                File.WriteAllLines(file, File.ReadAllLines(file).Where(x => !x.StartsWith(id, StringComparison.Ordinal)));
        }
    }
}
