namespace ShinyCommander.Share;

/// <summary>
/// Wraps a file stream so every byte read (a download) or written (an upload) is counted against a
/// <see cref="Transfer"/>, held to the share's bandwidth limit, and stopped when the transfer is cancelled from the
/// management window. Seeking passes through, so range requests still work on downloads.
/// </summary>
sealed class TrackedStream(Stream inner, Transfer transfer, TransferMonitor monitor) : Stream
{
    bool ended;

    public Transfer Transfer => transfer;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Token);
        try
        {
            var read = await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            if (read > 0)
            {
                await transfer.Share.Limiter.WaitAsync(read, linked.Token).ConfigureAwait(false);
                transfer.Add(read);
            }
            return read;
        }
        catch (OperationCanceledException) when (transfer.Token.IsCancellationRequested)
        {
            this.End(TransferState.Cancelled, "Stopped from ShinyCommander");
            throw;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Token);
        try
        {
            await transfer.Share.Limiter.WaitAsync(buffer.Length, linked.Token).ConfigureAwait(false);
            await inner.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
            transfer.Add(buffer.Length);
        }
        catch (OperationCanceledException) when (transfer.Token.IsCancellationRequested)
        {
            this.End(TransferState.Cancelled, "Stopped from ShinyCommander");
            throw;
        }
    }

    /// <summary>Settles the transfer; later calls do nothing.</summary>
    public void End(TransferState state, string? error = null)
    {
        if (this.ended)
            return;

        this.ended = true;
        monitor.End(transfer, state, error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var atEnd = AtEnd();
            inner.Dispose();
            this.SettleOnClose(atEnd);
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var atEnd = AtEnd();
        await inner.DisposeAsync().ConfigureAwait(false);
        this.SettleOnClose(atEnd);
        GC.SuppressFinalize(this);
    }

    bool AtEnd()
    {
        try
        {
            return inner.CanSeek && inner.Position >= inner.Length;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A download closed after its last byte went out finished; one closed earlier was cut off (the visitor went away,
    /// or it was a range request for part of the file - which still counts as a download only when it reached the end).
    /// </summary>
    void SettleOnClose(bool atEnd)
    {
        if (this.ended)
            return;

        var complete = atEnd || (transfer.TotalBytes is { } total ? transfer.Bytes >= total : true);
        if (transfer.Direction == TransferDirection.Download)
            this.End(complete ? TransferState.Completed : TransferState.Cancelled, complete ? null : "The visitor stopped the download");
        else
            this.End(TransferState.Completed);
    }
}
