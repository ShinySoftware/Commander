using ShinyCommander.Core.Remote;

namespace ShinyCommander.Core.Tests;

/// <summary>An IRemoteFileSystem in a dictionary - folders are entries with null content.</summary>
public sealed class MemoryRemote : IRemoteFileSystem, IRemoteProvider
{
    readonly Dictionary<string, byte[]?> items = new(StringComparer.Ordinal) { ["/"] = null };
    readonly object gate = new();

    public IReadOnlyList<RemoteProtocol> Protocols { get; } = [RemoteProtocol.Sftp];
    public string? ExpectedSecret { get; set; }
    public int Connects { get; private set; }
    public int Renames { get; private set; }

    public Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        if (this.ExpectedSecret != null && secret != this.ExpectedSecret)
            throw new RemoteAuthenticationException("bad password");
        this.Connects++;
        return Task.FromResult<IRemoteFileSystem>(this);
    }

    public void AddFile(string path, string content)
    {
        lock (this.gate)
        {
            this.EnsureParents(path);
            this.items[path] = System.Text.Encoding.UTF8.GetBytes(content);
        }
    }

    public string? Read(string path)
    {
        lock (this.gate)
            return this.items.TryGetValue(path, out var b) && b != null ? System.Text.Encoding.UTF8.GetString(b) : null;
    }

    public bool Has(string path)
    {
        lock (this.gate)
            return this.items.ContainsKey(path);
    }

    void EnsureParents(string path)
    {
        var parent = Parent(path);
        while (parent != "/" && !this.items.ContainsKey(parent))
        {
            this.items[parent] = null;
            parent = Parent(parent);
        }
    }

    static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (!this.items.TryGetValue(path, out var content) || content != null)
                throw new DirectoryNotFoundException(path);

            IReadOnlyList<RemoteItem> list = this.items
                .Where(x => x.Key != "/" && Parent(x.Key) == path)
                .Select(x => new RemoteItem(x.Key[(x.Key.LastIndexOf('/') + 1)..], x.Value == null, x.Value?.Length ?? 0, new DateTime(2024, 1, 1)))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.items.TryGetValue(path, out var content)
                ? new RemoteItem(path[(path.LastIndexOf('/') + 1)..], content == null, content?.Length ?? 0, new DateTime(2024, 1, 1))
                : null);
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (!this.items.TryGetValue(path, out var content) || content == null)
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(content));
        }
    }

    public async Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        lock (this.gate)
        {
            if (!this.items.TryGetValue(Parent(path), out var parent) || parent != null)
                throw new DirectoryNotFoundException(Parent(path));
            this.items[path] = ms.ToArray();
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (!this.items.ContainsKey(Parent(path)))
                throw new DirectoryNotFoundException(Parent(path));
            this.items[path] = null;
        }
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        lock (this.gate)
            this.items.Remove(path);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            foreach (var key in this.items.Keys.Where(x => x == path || x.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                this.items.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            this.Renames++;
            foreach (var key in this.items.Keys.Where(x => x == from || x.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            {
                this.items[to + key[from.Length..]] = this.items[key];
                this.items.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
