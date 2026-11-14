using FluentFTP;
using FluentFTP.Exceptions;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Remote;

/// <summary>FTP and FTPS (explicit TLS) over FluentFTP. Anonymous when the connection has no user name.</summary>
public sealed class FtpProvider : IRemoteProvider
{
    public IReadOnlyList<RemoteProtocol> Protocols { get; } = [RemoteProtocol.Ftp, RemoteProtocol.Ftps];

    public async Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        var config = new FtpConfig
        {
            EncryptionMode = connection.Protocol == RemoteProtocol.Ftps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            ConnectTimeout = 20_000
        };
        var user = connection.UserName.Length == 0 ? "anonymous" : connection.UserName;
        var pass = connection.UserName.Length == 0 ? "anonymous@" : secret ?? String.Empty;
        var client = new AsyncFtpClient(connection.Host, user, pass, connection.EffectivePort, config);
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch (FtpAuthenticationException ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new RemoteAuthenticationException(ex.Message, ex);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new FtpFileSystem(client);
    }
}

/// <summary>
/// One FTP control connection does one thing at a time, so every call takes turns - and downloads go to a scratch
/// file first, so a copy within the server isn't left waiting on a read stream that holds the connection.
/// </summary>
sealed class FtpFileSystem(AsyncFtpClient client) : IRemoteFileSystem
{
    readonly SemaphoreSlim gate = new(1, 1);

    async Task<T> Turn<T>(Func<Task<T>> call, CancellationToken ct)
    {
        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    Task Turn(Func<Task> call, CancellationToken ct) => this.Turn(async () =>
    {
        await call().ConfigureAwait(false);
        return true;
    }, ct);

    static RemoteItem ToItem(FtpListItem x) => new(
        x.Name,
        x.Type == FtpObjectType.Directory || x.Type == FtpObjectType.Link && x.LinkObject?.Type == FtpObjectType.Directory,
        Math.Max(0, x.Size),
        x.Modified,
        x.Chmod > 0 ? (UnixFileMode)Convert.ToInt32(x.Chmod.ToString(), 8) : null
    );

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default) => this.Turn<IReadOnlyList<RemoteItem>>(async () =>
    {
        if (path != "/" && !await client.DirectoryExists(path, ct).ConfigureAwait(false))
            throw new DirectoryNotFoundException($"{path} not found");

        var items = await client.GetListing(path, ct).ConfigureAwait(false);
        return items.Where(x => x.Name is not ("." or "..")).Select(ToItem).ToList();
    }, ct);

    public Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default) => this.Turn(async () =>
    {
        if (path == "/")
            return new RemoteItem("/", true, 0, DateTime.MinValue);

        if (await client.DirectoryExists(path, ct).ConfigureAwait(false))
            return new RemoteItem(RemoteUtil.NameOf(path), true, 0, DateTime.MinValue);

        if (!await client.FileExists(path, ct).ConfigureAwait(false))
            return (RemoteItem?)null;

        var info = await client.GetObjectInfo(path, true, ct).ConfigureAwait(false);
        return info == null ? new RemoteItem(RemoteUtil.NameOf(path), false, 0, DateTime.MinValue) : ToItem(info) with { Name = RemoteUtil.NameOf(path) };
    }, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => this.Turn<Stream>(async () =>
    {
        var temp = RemoteUtil.CreateTempStream();
        try
        {
            if (!await client.DownloadStream(temp, path, 0, null, ct).ConfigureAwait(false))
                throw new FileNotFoundException($"{path} couldn't be downloaded", path);
            temp.Position = 0;
            return temp;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }, ct);

    public Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default) => this.Turn(async () =>
    {
        var status = await client.UploadStream(content, path, FtpRemoteExists.Overwrite, false, null, ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"{path} couldn't be uploaded");
        if (modified is { } when)
        {
            try { await client.SetModifiedTime(path, when, ct).ConfigureAwait(false); } catch (FtpException) { /* MFMT isn't everywhere */ }
        }
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => this.Turn(() => client.CreateDirectory(path, false, ct), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default) => this.Turn(() => client.DeleteFile(path, ct), ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) => this.Turn(() => client.DeleteDirectory(path, ct), ct);

    public Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default) => this.Turn(() => client.Rename(from, to, ct), ct);

    public async ValueTask DisposeAsync()
    {
        try { await client.Disconnect().ConfigureAwait(false); } catch { }
        await client.DisposeAsync().ConfigureAwait(false);
        this.gate.Dispose();
    }
}
