using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using ShinyCommander.Core.Remote;

namespace ShinyCommander.Remote;

/// <summary>
/// Amazon S3 and the services that speak its API (MinIO, Cloudflare R2, Wasabi, Backblaze B2...). The root lists the
/// buckets; a folder made there is a new bucket, and deleting one empties and removes the bucket.
/// </summary>
public sealed class S3Provider : IRemoteProvider
{
    public IReadOnlyList<RemoteProtocol> Protocols { get; } = [RemoteProtocol.S3];

    public async Task<IRemoteFileSystem> ConnectAsync(RemoteConnection connection, string? secret, CancellationToken ct = default)
    {
        var config = new AmazonS3Config();
        if (!String.IsNullOrWhiteSpace(connection.Endpoint))
        {
            config.ServiceURL = connection.Endpoint;
            config.ForcePathStyle = true;
            if (!String.IsNullOrWhiteSpace(connection.Region))
                config.AuthenticationRegion = connection.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(String.IsNullOrWhiteSpace(connection.Region) ? "us-east-1" : connection.Region);
        }

        AWSCredentials credentials = connection.UserName.Length == 0
            ? new AnonymousAWSCredentials()
            : new BasicAWSCredentials(connection.UserName, secret ?? String.Empty);
        var client = new AmazonS3Client(credentials, config);
        var fs = new S3FileSystem(client);
        try
        {
            var start = ObjectPath.Parse(RemotePath.Split(connection.StartUri).Inner);
            if (start.IsAccount)
                await S3FileSystem.Call(() => client.ListBucketsAsync(ct)).ConfigureAwait(false);
            else
                await S3FileSystem.Call(() => client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = start.Bucket, MaxKeys = 1 }, ct)).ConfigureAwait(false);
        }
        catch
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return fs;
    }
}

sealed class S3FileSystem(AmazonS3Client client) : IRemoteFileSystem
{
    // AWSSDK v4 made most value properties nullable - these read either shape
    static long N(long? value) => value ?? 0;
    static DateTime D(DateTime? value) => value?.ToLocalTime() ?? DateTime.MinValue;

    /// <summary>An S3 call, with refused credentials turned into the error the UI asks for a password on.</summary>
    internal static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "ExpiredToken")
        {
            throw new RemoteAuthenticationException(ex.Message, ex);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode == "NoSuchBucket")
        {
            throw new DirectoryNotFoundException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        if (at.IsAccount)
        {
            var buckets = await Call(() => client.ListBucketsAsync(ct)).ConfigureAwait(false);
            return (buckets.Buckets ?? []).Select(x => new RemoteItem(x.BucketName, true, 0, D(x.CreationDate))).ToList();
        }

        var list = new List<RemoteItem>();
        var request = new ListObjectsV2Request { BucketName = at.Bucket, Prefix = at.Prefix, Delimiter = "/" };
        do
        {
            var page = await Call(() => client.ListObjectsV2Async(request, ct)).ConfigureAwait(false);
            list.AddRange((page.CommonPrefixes ?? []).Select(x => new RemoteItem(x[at.Prefix.Length..].TrimEnd('/'), true, 0, DateTime.MinValue)));
            // "folder/" marker objects stand for the folder itself
            list.AddRange((page.S3Objects ?? [])
                .Where(x => x.Key.Length > at.Prefix.Length && !x.Key.EndsWith('/'))
                .Select(x => new RemoteItem(x.Key[at.Prefix.Length..], false, N(x.Size), D(x.LastModified))));
            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (request.ContinuationToken != null);

        if (list.Count == 0 && at.Key.Length > 0 && await this.GetItemAsync(path, ct).ConfigureAwait(false) == null)
            throw new DirectoryNotFoundException($"{path} not found");
        return list;
    }

    public async Task<RemoteItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        if (at.IsAccount)
            return new RemoteItem("/", true, 0, DateTime.MinValue);

        if (at.IsBucket)
        {
            try
            {
                await Call(() => client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = at.Bucket, MaxKeys = 1 }, ct)).ConfigureAwait(false);
                return new RemoteItem(at.Bucket, true, 0, DateTime.MinValue);
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        try
        {
            var meta = await client.GetObjectMetadataAsync(at.Bucket, at.Key, ct).ConfigureAwait(false);
            return new RemoteItem(RemoteUtil.NameOf(at.Key), false, N(meta.ContentLength), D(meta.LastModified));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // not an object - a folder if anything lives under it
        }

        var under = await Call(() => client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = at.Bucket, Prefix = at.Prefix, MaxKeys = 1 }, ct)).ConfigureAwait(false);
        return (under.S3Objects?.Count ?? 0) > 0 ? new RemoteItem(RemoteUtil.NameOf(at.Key), true, 0, DateTime.MinValue) : null;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        try
        {
            var response = await Call(() => client.GetObjectAsync(at.Bucket, at.Key, ct)).ConfigureAwait(false);
            return new ResponseStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(ex.Message, path, ex);
        }
    }

    public async Task WriteAsync(string path, Stream content, long length, DateTime? modified, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        if (at.Key.Length == 0)
            throw new IOException("Files go inside a bucket");

        // multipart uploads (anything big) need to know the length and seek - spool what can't
        Stream? spool = null;
        if (!content.CanSeek)
        {
            spool = RemoteUtil.CreateTempStream();
            await content.CopyToAsync(spool, ct).ConfigureAwait(false);
            spool.Position = 0;
        }
        try
        {
            using var transfer = new TransferUtility(client);
            await Call(async () =>
            {
                await transfer.UploadAsync(new TransferUtilityUploadRequest { BucketName = at.Bucket, Key = at.Key, InputStream = spool ?? content, AutoCloseStream = false }, ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (spool != null)
                await spool.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        if (at.IsBucket)
        {
            await Call(() => client.PutBucketAsync(new PutBucketRequest { BucketName = at.Bucket }, ct)).ConfigureAwait(false);
            return;
        }
        // folders are only implied by keys - a zero byte "folder/" object keeps an empty one around
        await Call(() => client.PutObjectAsync(new PutObjectRequest { BucketName = at.Bucket, Key = at.Prefix, InputStream = new MemoryStream() }, ct)).ConfigureAwait(false);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        return Call(() => client.DeleteObjectAsync(at.Bucket, at.Key, ct));
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
    {
        var at = ObjectPath.Parse(path);
        await this.DeleteUnderAsync(at.Bucket, at.Prefix, ct).ConfigureAwait(false);
        if (at.IsBucket)
            await Call(() => client.DeleteBucketAsync(at.Bucket, ct)).ConfigureAwait(false);
    }

    async Task DeleteUnderAsync(string bucket, string prefix, CancellationToken ct)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        do
        {
            var page = await Call(() => client.ListObjectsV2Async(request, ct)).ConfigureAwait(false);
            var keys = (page.S3Objects ?? []).Select(x => new KeyVersion { Key = x.Key }).ToList();
            if (keys.Count > 0)
                await Call(() => client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = keys }, ct)).ConfigureAwait(false);
            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (request.ContinuationToken != null);
    }

    public async Task RenameAsync(string from, string to, bool isDirectory, CancellationToken ct = default)
    {
        var a = ObjectPath.Parse(from);
        var b = ObjectPath.Parse(to);
        if (a.IsBucket || b.IsBucket || a.IsAccount || b.IsAccount)
            throw new NotSupportedException("Buckets can't be renamed - make a new one and move the files across");

        if (!isDirectory)
        {
            await this.CopyDeleteAsync(a.Bucket, a.Key, b.Bucket, b.Key, ct).ConfigureAwait(false);
            return;
        }

        // a folder is every key under it - each is copied to the new prefix, then removed
        var request = new ListObjectsV2Request { BucketName = a.Bucket, Prefix = a.Prefix };
        var keys = new List<string>();
        do
        {
            var page = await Call(() => client.ListObjectsV2Async(request, ct)).ConfigureAwait(false);
            keys.AddRange((page.S3Objects ?? []).Select(x => x.Key));
            request.ContinuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (request.ContinuationToken != null);

        foreach (var key in keys)
            await this.CopyDeleteAsync(a.Bucket, key, b.Bucket, b.Prefix + key[a.Prefix.Length..], ct).ConfigureAwait(false);
    }

    async Task CopyDeleteAsync(string fromBucket, string fromKey, string toBucket, string toKey, CancellationToken ct)
    {
        await Call(() => client.CopyObjectAsync(fromBucket, fromKey, toBucket, toKey, ct)).ConfigureAwait(false);
        await Call(() => client.DeleteObjectAsync(fromBucket, fromKey, ct)).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
