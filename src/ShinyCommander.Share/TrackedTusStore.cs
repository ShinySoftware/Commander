using System.Collections.Concurrent;
using Shiny.Net.HttpServer.Tus;

namespace ShinyCommander.Share;

/// <summary>
/// A share's resumable uploads: <see cref="DiskTusStore"/> in a temporary folder, with each upload shown as one
/// transfer for as long as it's coming in - across however many PATCH requests it takes.
/// </summary>
sealed class TrackedTusStore(DiskTusStore inner, ShareFileSystem files, TransferMonitor monitor, Func<ClientContext?> client) : ITusStore
{
    readonly ConcurrentDictionary<string, Transfer> transfers = new();

    public DiskTusStore Inner => inner;

    public async ValueTask<string> CreateAsync(TusCreateRequest request, CancellationToken cancellationToken)
    {
        var id = await inner.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        var who = client();
        var name = TusMeta.Get(request.Metadata, "filename") ?? id;
        var path = TusMeta.Get(request.Metadata, "path") is { Length: > 0 } folder ? folder.TrimEnd('/') + "/" + name : name;
        this.transfers[id] = monitor.Start(files.Share, who?.Ip ?? "?", path, TransferDirection.Upload, request.Length, who?.IsTunneled ?? false);
        return id;
    }

    public ValueTask<TusUpload?> GetAsync(string id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

    public async ValueTask<long> AppendAsync(string id, long offset, Stream data, CancellationToken cancellationToken)
    {
        if (!this.transfers.TryGetValue(id, out var transfer))
            return await inner.AppendAsync(id, offset, data, cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Token);
        try
        {
            await using var counted = new CountingReadStream(data, transfer);
            return await inner.AppendAsync(id, offset, counted, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (transfer.Token.IsCancellationRequested)
        {
            this.End(id, TransferState.Cancelled, "Stopped from ShinyCommander");
            await inner.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask SetLengthAsync(string id, long length, CancellationToken cancellationToken)
    {
        if (this.transfers.TryGetValue(id, out var transfer))
            transfer.TotalBytes = length;
        return inner.SetLengthAsync(id, length, cancellationToken);
    }

    public ValueTask SetExpirationAsync(string id, DateTimeOffset? expiresUtc, CancellationToken cancellationToken)
        => inner.SetExpirationAsync(id, expiresUtc, cancellationToken);

    public ValueTask ConcatenateAsync(string id, IReadOnlyList<string> partialIds, CancellationToken cancellationToken)
        => inner.ConcatenateAsync(id, partialIds, cancellationToken);

    public ValueTask<Stream> OpenReadAsync(string id, CancellationToken cancellationToken) => inner.OpenReadAsync(id, cancellationToken);

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        this.End(id, TransferState.Cancelled, "The upload was abandoned");
        return await inner.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<string>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => inner.GetExpiredAsync(nowUtc, cancellationToken);

    /// <summary>Settles the transfer for an upload (finished, failed or abandoned).</summary>
    public void End(string id, TransferState state, string? error = null)
    {
        if (this.transfers.TryRemove(id, out var transfer))
            monitor.End(transfer, state, error);
    }

    /// <summary>Counts what the upload reads, held to the share's bandwidth limit.</summary>
    sealed class CountingReadStream(Stream inner, Transfer transfer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                await transfer.Share.Limiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                transfer.Add(read);
            }
            return read;
        }
    }
}
