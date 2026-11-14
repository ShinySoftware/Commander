namespace ShinyCommander.Core.Remote;

/// <summary>
/// The saved connections, and one live <see cref="IRemoteFileSystem"/> per connection that's been opened. A remote
/// path finds its connection by its root (<see cref="RemoteConnection.Root"/>); the first use connects, with the
/// secret from the keychain - or, on the async path, by asking for it through <see cref="SecretRequested"/>.
/// </summary>
public sealed class RemoteSessions(ISecretStore secrets, IEnumerable<IRemoteProvider> providers) : IAsyncDisposable
{
    readonly Dictionary<string, IRemoteFileSystem> live = new(StringComparer.Ordinal);
    readonly Dictionary<string, SemaphoreSlim> connecting = new(StringComparer.Ordinal);
    readonly object gate = new();
    readonly List<IRemoteProvider> providers = providers.ToList();
    IReadOnlyList<RemoteConnection> connections = [];

    public RemoteSessions() : this(new MemorySecretStore(), []) { }

    public ISecretStore Secrets => secrets;

    /// <summary>
    /// Asked when a connection has no saved secret, or its saved one was refused: the connection, and the error from
    /// the last try (null the first time). Returns the secret and whether to keep it, or null to give up.
    /// </summary>
    public Func<RemoteConnection, string?, Task<(string Secret, bool Remember)?>>? SecretRequested { get; set; }

    /// <summary>Raised when a connection is opened or closed - the drive bar shows which are live.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<RemoteConnection> Connections
    {
        get
        {
            lock (this.gate)
                return this.connections;
        }
    }

    public void AddProvider(IRemoteProvider provider) => this.providers.Add(provider);

    public bool Supports(RemoteProtocol protocol) => this.providers.Any(x => x.Protocols.Contains(protocol));

    /// <summary>Replaces the saved connections; sessions of removed or changed ones are closed.</summary>
    public void SetConnections(IEnumerable<RemoteConnection> list)
    {
        List<IRemoteFileSystem> stale;
        lock (this.gate)
        {
            var next = list.ToList();
            var keep = next.ToDictionary(x => x.Id);
            var old = this.connections.ToDictionary(x => x.Id);
            stale = old.Values
                .Where(x => !keep.TryGetValue(x.Id, out var now) || now != x)
                .Where(x => this.live.ContainsKey(x.Id))
                .Select(x => this.Take(x.Id))
                .ToList();
            this.connections = next;
        }
        foreach (var session in stale)
            _ = session.DisposeAsync().AsTask();
        if (stale.Count > 0)
            this.Changed?.Invoke(this, EventArgs.Empty);
    }

    IRemoteFileSystem Take(string id)
    {
        var session = this.live[id];
        this.live.Remove(id);
        return session;
    }

    public RemoteConnection? Find(string path)
    {
        if (!RemotePath.IsRemote(path))
            return null;

        var (root, _) = RemotePath.Split(path);
        return this.Connections.FirstOrDefault(x => String.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    public RemoteConnection FindRequired(string path)
        => this.Find(path) ?? throw new DirectoryNotFoundException($"No saved connection for {RemotePath.Split(path).Root} - add one under Connections");

    public bool IsConnected(RemoteConnection connection)
    {
        lock (this.gate)
            return this.live.ContainsKey(connection.Id);
    }

    /// <summary>The live session for a path, connecting first if needed (asking for a password if none is saved).</summary>
    public async Task<(IRemoteFileSystem Fs, string Inner)> GetAsync(string path, CancellationToken ct = default)
    {
        var connection = this.FindRequired(path);
        var fs = await this.ConnectAsync(connection, interactive: true, ct).ConfigureAwait(false);
        return (fs, RemotePath.Split(path).Inner);
    }

    /// <summary>
    /// The same, for code that can't await (inside file operations). Only the saved secret is tried - asking for a
    /// password from here could deadlock the UI thread - so a connection that needs one must have been opened first.
    /// </summary>
    public (IRemoteFileSystem Fs, string Inner) Get(string path)
    {
        var connection = this.FindRequired(path);
        var fs = Task.Run(() => this.ConnectAsync(connection, interactive: false, CancellationToken.None)).GetAwaiter().GetResult();
        return (fs, RemotePath.Split(path).Inner);
    }

    public async Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, bool interactive, CancellationToken ct = default)
    {
        SemaphoreSlim slot;
        lock (this.gate)
        {
            if (this.live.TryGetValue(connection.Id, out var existing))
                return existing;
            if (!this.connecting.TryGetValue(connection.Id, out slot!))
                this.connecting[connection.Id] = slot = new SemaphoreSlim(1, 1);
        }

        // one connect per connection at a time - both panels opening the same server get one session
        await slot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (this.gate)
            {
                if (this.live.TryGetValue(connection.Id, out var existing))
                    return existing;
            }

            var fs = await this.OpenAsync(connection, interactive, ct).ConfigureAwait(false);
            lock (this.gate)
                this.live[connection.Id] = fs;
            this.Changed?.Invoke(this, EventArgs.Empty);
            return fs;
        }
        finally
        {
            slot.Release();
        }
    }

    async Task<IRemoteFileSystem> OpenAsync(RemoteConnection connection, bool interactive, CancellationToken ct)
    {
        var provider = this.providers.FirstOrDefault(x => x.Protocols.Contains(connection.Protocol))
            ?? throw new NotSupportedException($"{connection.Protocol} isn't available in this build");

        var secret = secrets.Get(connection.Id);
        string? error = null;

        // anonymous FTP, public WebDAV, key files without a passphrase: try without asking first
        var mayNeedNoSecret = connection.UserName.Length == 0 || connection.KeyFile != null;
        if (secret == null && !mayNeedNoSecret)
        {
            if (!interactive || this.SecretRequested == null)
                throw new RemoteAuthenticationException($"Open {connection.Name} first - it needs a password");

            var asked = await this.SecretRequested(connection, null).ConfigureAwait(false) ?? throw new OperationCanceledException(ct);
            secret = asked.Secret;
            if (asked.Remember)
                secrets.Set(connection.Id, secret);
        }

        while (true)
        {
            try
            {
                return await provider.ConnectAsync(connection, secret, ct).ConfigureAwait(false);
            }
            catch (RemoteAuthenticationException ex) when (interactive && this.SecretRequested != null)
            {
                error = ex.Message;
            }

            var again = await this.SecretRequested!(connection, error).ConfigureAwait(false) ?? throw new OperationCanceledException(ct);
            secret = again.Secret;
            if (again.Remember)
                secrets.Set(connection.Id, secret);
            else
                secrets.Remove(connection.Id);
        }
    }

    /// <summary>Closes a connection's session; the next use connects again.</summary>
    public async Task DisconnectAsync(RemoteConnection connection)
    {
        IRemoteFileSystem? session = null;
        lock (this.gate)
        {
            if (this.live.ContainsKey(connection.Id))
                session = this.Take(connection.Id);
        }
        if (session == null)
            return;

        try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Connects with the given secret and closes again - the editor's "Test" button.</summary>
    public async Task TestAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        var provider = this.providers.FirstOrDefault(x => x.Protocols.Contains(connection.Protocol))
            ?? throw new NotSupportedException($"{connection.Protocol} isn't available in this build");

        await using var fs = await provider.ConnectAsync(connection, secret, ct).ConfigureAwait(false);
        await fs.ListAsync(RemotePath.Split(connection.StartUri).Inner, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        List<IRemoteFileSystem> all;
        lock (this.gate)
        {
            all = this.live.Values.ToList();
            this.live.Clear();
        }
        foreach (var session in all)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }
}
