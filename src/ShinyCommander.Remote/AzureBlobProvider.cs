using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Remote;

/// <summary>
/// Azure Blob Storage. The secret is the account key, a SAS token ("sv=..."), or a whole connection string
/// ("DefaultEndpointsProtocol=...", "UseDevelopmentStorage=true" for Azurite). The root lists the containers; a
/// folder made there is a new container, and deleting one removes the container.
/// </summary>
public sealed class AzureBlobProvider : IRemoteProvider
{
    public IReadOnlyList<RemoteProtocol> Protocols { get; } = [RemoteProtocol.AzureBlob];

    public async Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        BlobServiceClient service;
        try
        {
            service = CreateClient(connection, secret);
        }
        catch (FormatException ex)
        {
            // a mistyped account key isn't base 64 - treat it like any refused key, so it's asked for again
            throw new RemoteAuthenticationException("That isn't a valid account key, SAS or connection string", ex);
        }
        var fs = new AzureBlobFileSystem(service);
        var start = ObjectPath.Parse(RemotePath.Split(connection.StartUri).Inner);
        await AzureBlobFileSystem.Call(async () =>
        {
            // a SAS for one container can't list the account - prove the start folder instead
            if (start.IsAccount)
                await service.GetBlobContainersAsync(cancellationToken: ct).AsPages(pageSizeHint: 1).FirstAsync(ct).ConfigureAwait(false);
            else
                await service.GetBlobContainerClient(start.Bucket).GetBlobsAsync(cancellationToken: ct).AsPages(pageSizeHint: 1).FirstAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        return fs;
    }

    static BlobServiceClient CreateClient(RemoteConnection connection, string? secret)
    {
        secret = secret?.Trim() ?? String.Empty;
        if (secret.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase)
            || secret.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase)
            || secret.StartsWith("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase))
            return new BlobServiceClient(secret);

        var endpoint = new Uri(String.IsNullOrWhiteSpace(connection.Endpoint) ? $"https://{connection.Host}.blob.core.windows.net" : connection.Endpoint);
        if (secret.Length == 0)
            return new BlobServiceClient(endpoint); // public containers

        if (secret.StartsWith("sv=", StringComparison.Ordinal) || secret.StartsWith("?sv=", StringComparison.Ordinal))
            return new BlobServiceClient(endpoint, new AzureSasCredential(secret.TrimStart('?')));

        return new BlobServiceClient(endpoint, new StorageSharedKeyCredential(connection.Host, secret));
    }
}

sealed class AzureBlobFileSystem(BlobServiceClient service) : IRemoteFileSystem
{
    /// <summary>
    /// Folders only exist as name prefixes, so an empty one is kept by a zero byte blob inside it. Not a "folder/" blob:
    /// the SDK and Azurite drop a trailing slash from blob names.
    /// </summary>
    internal const string FolderMarker = ".shinycommander-folder";

    internal static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403 && ex.ErrorCode is "AuthenticationFailed" or "AuthorizationFailure" or null)
        {
            throw new RemoteAuthenticationException(ex.Message, ex);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "ContainerNotFound")
        {
            throw new DirectoryNotFoundException(ex.Message, ex);
        }
    }

    static Task Call(Func<Task> call) => Call(async () =>
    {
        await call().ConfigureAwait(false);
        return true;
    });

    static DateTime D(DateTimeOffset? value) => value?.LocalDateTime ?? DateTime.MinValue;

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default) => Call<IReadOnlyList<RemoteItem>>(async () =>
    {
        var at = ObjectPath.Parse(path);
        var list = new List<RemoteItem>();
        if (at.IsAccount)
        {
            await foreach (var c in service.GetBlobContainersAsync(cancellationToken: ct).ConfigureAwait(false))
                list.Add(new RemoteItem(c.Name, true, 0, D(c.Properties.LastModified)));
            return list;
        }

        var container = service.GetBlobContainerClient(at.Bucket);
        await foreach (var item in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", at.Prefix, ct).ConfigureAwait(false))
        {
            if (item.IsPrefix)
                list.Add(new RemoteItem(item.Prefix[at.Prefix.Length..].TrimEnd('/'), true, 0, DateTime.MinValue));
            // folder markers (ours, and "folder/" blobs other tools make) stand for the folder itself
            else if (item.Blob.Name.Length > at.Prefix.Length && !item.Blob.Name.EndsWith('/') && item.Blob.Name[at.Prefix.Length..] != FolderMarker)
                list.Add(new RemoteItem(item.Blob.Name[at.Prefix.Length..], false, item.Blob.Properties.ContentLength ?? 0, D(item.Blob.Properties.LastModified)));
        }

        if (list.Count == 0 && at.IsBucket && !await container.ExistsAsync(ct).ConfigureAwait(false))
            throw new DirectoryNotFoundException($"{path} not found");
        return list;
    });

    public Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default) => Call(async () =>
    {
        var at = ObjectPath.Parse(path);
        if (at.IsAccount)
            return new RemoteItem("/", true, 0, DateTime.MinValue);

        var container = service.GetBlobContainerClient(at.Bucket);
        if (at.IsBucket)
            return await container.ExistsAsync(ct).ConfigureAwait(false) ? new RemoteItem(at.Bucket, true, 0, DateTime.MinValue) : (RemoteItem?)null;

        try
        {
            var props = await container.GetBlobClient(at.Key).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            return new RemoteItem(RemoteUtil.NameOf(at.Key), false, props.Value.ContentLength, props.Value.LastModified.LocalDateTime);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // not a blob - a folder if anything lives under it
        }

        await foreach (var _ in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, at.Prefix, ct).ConfigureAwait(false))
            return new RemoteItem(RemoteUtil.NameOf(at.Key), true, 0, DateTime.MinValue);
        return null;
    });

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Call(async () =>
    {
        var at = ObjectPath.Parse(path);
        try
        {
            return await service.GetBlobContainerClient(at.Bucket).GetBlobClient(at.Key).OpenReadAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException(ex.Message, path, ex);
        }
    });

    public Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default) => Call(async () =>
    {
        var at = ObjectPath.Parse(path);
        if (at.Key.Length == 0)
            throw new IOException("Files go inside a container");
        await service.GetBlobContainerClient(at.Bucket).GetBlobClient(at.Key).UploadAsync(content, overwrite: true, ct).ConfigureAwait(false);
    });

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Call(async () =>
    {
        var at = ObjectPath.Parse(path);
        var container = service.GetBlobContainerClient(at.Bucket);
        if (at.IsBucket)
            await container.CreateAsync(cancellationToken: ct).ConfigureAwait(false);
        else
            await container.GetBlobClient(at.Prefix + FolderMarker).UploadAsync(new BinaryData(Array.Empty<byte>()), overwrite: true, ct).ConfigureAwait(false);
    });

    public Task DeleteFileAsync(string path, CancellationToken ct = default) => Call(async () =>
    {
        var at = ObjectPath.Parse(path);
        await service.GetBlobContainerClient(at.Bucket).GetBlobClient(at.Key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct).ConfigureAwait(false);
    });

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) => Call(async () =>
    {
        var at = ObjectPath.Parse(path);
        var container = service.GetBlobContainerClient(at.Bucket);
        if (at.IsBucket)
        {
            await container.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, at.Prefix, ct).ConfigureAwait(false))
            names.Add(blob.Name);
        foreach (var name in names)
            await container.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct).ConfigureAwait(false);
    });

    public Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default) => Call(async () =>
    {
        var a = ObjectPath.Parse(from);
        var b = ObjectPath.Parse(to);
        if (a.IsBucket || b.IsBucket || a.IsAccount || b.IsAccount)
            throw new NotSupportedException("Containers can't be renamed - make a new one and move the files across");

        var source = service.GetBlobContainerClient(a.Bucket);
        var target = service.GetBlobContainerClient(b.Bucket);
        var moves = new List<(string From, string To)>();
        if (isDirectory)
        {
            await foreach (var blob in source.GetBlobsAsync(BlobTraits.None, BlobStates.None, a.Prefix, ct).ConfigureAwait(false))
                moves.Add((blob.Name, b.Prefix + blob.Name[a.Prefix.Length..]));
        }
        else
        {
            moves.Add((a.Key, b.Key));
        }

        // blobs have no rename - each is streamed to its new name (works for every credential kind, SAS included)
        foreach (var (src, dst) in moves)
        {
            var from = source.GetBlobClient(src);
            await using (var stream = await from.OpenReadAsync(cancellationToken: ct).ConfigureAwait(false))
                await target.GetBlobClient(dst).UploadAsync(stream, overwrite: false, ct).ConfigureAwait(false);
            await from.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
