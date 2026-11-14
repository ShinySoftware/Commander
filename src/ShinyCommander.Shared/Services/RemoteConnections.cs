namespace ShinyCommander.Services;

/// <summary>
/// The saved connections: kept in settings (the secrets in the keychain), handed to <see cref="RemoteSessions"/>, and
/// the password question it asks when a server needs one.
/// </summary>
public sealed class RemoteConnections
{
    readonly AppSettings settings;
    readonly RemoteSessions sessions;
    readonly IAppDialogs dialogs;

    public RemoteConnections(AppSettings settings, RemoteSessions sessions, IAppDialogs dialogs)
    {
        this.settings = settings;
        this.sessions = sessions;
        this.dialogs = dialogs;
        sessions.SetConnections(RemoteConnection.Deserialize(settings.RemoteConnections));
        sessions.SecretRequested = this.AskSecret;
        sessions.Changed += (_, _) => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A connection was added, changed, removed, opened or closed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<RemoteConnection> All => this.sessions.Connections;

    public RemoteSessions Sessions => this.sessions;

    public bool IsConnected(RemoteConnection connection) => this.sessions.IsConnected(connection);

    public bool HasSecret(RemoteConnection connection) => this.sessions.Secrets.Get(connection.Id) != null;

    /// <summary>Adds or replaces (by id) a connection. <paramref name="secret"/>: null keeps the saved one, empty forgets it.</summary>
    public void Save(RemoteConnection connection, string? secret)
    {
        if (secret is { Length: > 0 })
            this.sessions.Secrets.Set(connection.Id, secret);
        else if (secret != null)
            this.sessions.Secrets.Remove(connection.Id);

        var list = this.All.Where(x => x.Id != connection.Id).Append(connection).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        this.Store(list);
    }

    public async Task Remove(RemoteConnection connection)
    {
        await this.sessions.DisconnectAsync(connection);
        this.sessions.Secrets.Remove(connection.Id);
        this.Store(this.All.Where(x => x.Id != connection.Id).ToList());
    }

    public void ForgetSecret(RemoteConnection connection) => this.sessions.Secrets.Remove(connection.Id);

    public Task Disconnect(RemoteConnection connection) => this.sessions.DisconnectAsync(connection);

    void Store(List<RemoteConnection> list)
    {
        this.settings.RemoteConnections = RemoteConnection.Serialize(list);
        this.sessions.SetConnections(list);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Another connection with the same root (user, host and port) - two can't share a panel path.</summary>
    public RemoteConnection? Clash(RemoteConnection connection)
        => this.All.FirstOrDefault(x => x.Id != connection.Id && String.Equals(x.Root, connection.Root, StringComparison.OrdinalIgnoreCase));

    async Task<(string Secret, bool Remember)?> AskSecret(RemoteConnection connection, string? error)
    {
        var who = connection.IsObjectStorage
            ? connection.Name
            : (connection.UserName.Length == 0 ? connection.Host : $"{connection.UserName}@{connection.Host}");
        var message = error == null
            ? L.Remote_PasswordMessageFormat(who)
            : L.Remote_PasswordRetryFormat(who, error);
        var secret = await this.dialogs.PromptSecret(L.Remote_PasswordTitleFormat(connection.Name), message, L.Remote_Connect);
        // the user asked for the keychain - a password typed here is kept there; Connections can forget it
        return secret == null ? null : (secret, true);
    }
}
