using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Remote;

/// <summary>SFTP over SSH.NET - password, keyboard-interactive or a private key file.</summary>
public sealed class SftpProvider(KnownHosts knownHosts) : IRemoteProvider
{
    public SftpProvider() : this(new KnownHosts()) { }

    public IReadOnlyList<RemoteProtocol> Protocols { get; } = [RemoteProtocol.Sftp];

    public async Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        var methods = new List<AuthenticationMethod>();
        if (!String.IsNullOrWhiteSpace(connection.KeyFile))
        {
            var key = String.IsNullOrEmpty(secret) ? new PrivateKeyFile(ExpandHome(connection.KeyFile)) : new PrivateKeyFile(ExpandHome(connection.KeyFile), secret);
            methods.Add(new PrivateKeyAuthenticationMethod(connection.UserName, key));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(connection.UserName, secret ?? String.Empty));
            // servers that only offer keyboard-interactive ask for the same password as a prompt
            var interactive = new KeyboardInteractiveAuthenticationMethod(connection.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = secret ?? String.Empty;
            };
            methods.Add(interactive);
        }

        var info = new ConnectionInfo(connection.Host, connection.EffectivePort, connection.UserName, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        var client = new SftpClient(info);
        string? mismatch = null;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = knownHosts.Check(connection.Host, connection.EffectivePort, e.HostKeyName, e.FingerPrintSHA256, out var previous);
            if (!e.CanTrust)
                mismatch = $"The host key of {connection.Host} changed (now SHA256:{e.FingerPrintSHA256}, was SHA256:{previous}). If the server was reinstalled, forget its key in the connection's settings.";
        };

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new RemoteAuthenticationException(ex.Message, ex);
        }
        catch (SshConnectionException ex) when (mismatch != null)
        {
            client.Dispose();
            throw new IOException(mismatch, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new SftpFileSystem(client);
    }

    static string ExpandHome(string path) => path.StartsWith('~')
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
        : path;
}

sealed class SftpFileSystem(SftpClient client) : IRemoteFileSystem
{
    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default)
    {
        var list = new List<RemoteItem>();
        try
        {
            await foreach (var file in client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                if (file.Name is not ("." or ".."))
                    list.Add(await this.ToItemAsync(file, ct).ConfigureAwait(false));
            }
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new DirectoryNotFoundException(ex.Message, ex);
        }
        return list;
    }

    // a link is listed as itself - follow it so linked folders open like folders
    async Task<RemoteItem> ToItemAsync(ISftpFile file, CancellationToken ct)
    {
        var isDir = file.IsDirectory;
        var size = file.Length;
        if (file.IsSymbolicLink)
        {
            try
            {
                var target = await client.GetAttributesAsync(file.FullName, ct).ConfigureAwait(false);
                isDir = target.IsDirectory;
                size = target.Size;
            }
            catch (SshException)
            {
                // dangling link - show it as a file
            }
        }
        return new RemoteItem(file.Name, isDir, size, file.LastWriteTime, Mode(file));
    }

    static UnixFileMode Mode(ISftpFile f)
    {
        var mode = UnixFileMode.None;
        if (f.OwnerCanRead) mode |= UnixFileMode.UserRead;
        if (f.OwnerCanWrite) mode |= UnixFileMode.UserWrite;
        if (f.OwnerCanExecute) mode |= UnixFileMode.UserExecute;
        if (f.GroupCanRead) mode |= UnixFileMode.GroupRead;
        if (f.GroupCanWrite) mode |= UnixFileMode.GroupWrite;
        if (f.GroupCanExecute) mode |= UnixFileMode.GroupExecute;
        if (f.OthersCanRead) mode |= UnixFileMode.OtherRead;
        if (f.OthersCanWrite) mode |= UnixFileMode.OtherWrite;
        if (f.OthersCanExecute) mode |= UnixFileMode.OtherExecute;
        return mode;
    }

    public async Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        if (path == "/")
            return new RemoteItem("/", true, 0, DateTime.MinValue);

        try
        {
            var file = await client.GetAsync(path, ct).ConfigureAwait(false);
            return await this.ToItemAsync(file, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await client.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new FileNotFoundException(ex.Message, path, ex);
        }
    }

    public async Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default)
    {
        await client.UploadFileAsync(content, path, ct).ConfigureAwait(false);
        if (modified is { } when)
        {
            try { client.SetLastWriteTime(path, when); } catch (SshException) { /* not allowed on every server */ }
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => client.CreateDirectoryAsync(path, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default) => client.DeleteFileAsync(path, ct);

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
    {
        // SFTP only removes empty folders - empty it first, without following links out of it
        await foreach (var file in client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (file.Name is "." or "..")
            {
                // the folder itself and its parent
            }
            else if (file.IsDirectory && !file.IsSymbolicLink)
            {
                await this.DeleteDirectoryAsync(file.FullName, ct).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFileAsync(file.FullName, ct).ConfigureAwait(false);
            }
        }
        await client.DeleteDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default) => client.RenameFileAsync(from, to, ct);

    public ValueTask DisposeAsync()
    {
        try { client.Disconnect(); } catch { }
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
