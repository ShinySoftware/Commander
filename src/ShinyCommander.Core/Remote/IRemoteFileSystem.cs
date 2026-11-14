namespace ShinyCommander.Core.Remote;

/// <summary>A file or folder on a server, as a provider lists it. <see cref="Name"/> is one segment, never a path.</summary>
public sealed record RemoteItem(string Name, bool IsDirectory, long Size, DateTime Modified, UnixFileMode? Mode = null);

/// <summary>
/// One live connection to a server or storage account. Paths are the "/"-rooted inner part of a remote path
/// (<see cref="RemotePath.Split"/>) - "/var/www/index.html", or "/bucket/key" on object storage, where the root lists
/// the buckets (containers) and creating or deleting a folder there creates or deletes one.
/// Implementations must allow a read stream to stay open while other calls are made (copying within one server).
/// </summary>
public interface IRemoteFileSystem : IAsyncDisposable
{
    /// <summary>The folder's direct children. Throws <see cref="DirectoryNotFoundException"/> when it isn't there.</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>The item at <paramref name="path"/>, or null when there is nothing there.</summary>
    Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>Creates or replaces the file with what <paramref name="content"/> holds. Parent folders exist already.</summary>
    Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);

    Task DeleteFileAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes the folder and everything under it.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Renames or moves within this connection; the target does not exist.</summary>
    Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default);
}

/// <summary>Opens <see cref="IRemoteFileSystem"/>s for the protocols it knows.</summary>
public interface IRemoteProvider
{
    IReadOnlyList<RemoteProtocol> Protocols { get; }

    /// <summary>Connects (and signs in). <paramref name="secret"/> is the password, key passphrase or secret key.</summary>
    Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default);
}

/// <summary>The server turned the credentials down - the UI asks for the password again.</summary>
public sealed class RemoteAuthenticationException(string message, Exception? inner = null) : Exception(message, inner);
