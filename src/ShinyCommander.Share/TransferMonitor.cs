using System.Collections.Concurrent;
using System.Diagnostics;

namespace ShinyCommander.Share;

public enum TransferDirection
{
    /// <summary>To the visitor - a download.</summary>
    Download,

    /// <summary>From the visitor - an upload.</summary>
    Upload
}

public enum TransferState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>One file (or zipped folder) moving. Bytes are counted on the request thread; everything else reads.</summary>
public sealed class Transfer
{
    readonly CancellationTokenSource cancel = new();
    readonly long startTicks = Stopwatch.GetTimestamp();
    long bytes;
    long endTicks;

    // speed: bytes seen at the last sample, smoothed
    long sampleBytes;
    long sampleTicks;
    double speed;

    internal Transfer(long id, ServedFolder share, string clientIp, string path, TransferDirection direction, long? total, bool tunneled)
    {
        this.Id = id;
        this.Share = share;
        this.ClientIp = clientIp;
        this.Path = path;
        this.Direction = direction;
        this.TotalBytes = total;
        this.IsTunneled = tunneled;
        this.StartedUtc = DateTimeOffset.UtcNow;
        this.sampleTicks = this.startTicks;
    }

    public long Id { get; }
    public ServedFolder Share { get; }
    public string ClientIp { get; }
    public bool IsTunneled { get; }

    /// <summary>Relative to the share, '/' separated.</summary>
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(this.Path.TrimEnd('/')) is { Length: > 0 } n ? n : this.Share.Name;
    public TransferDirection Direction { get; }

    /// <summary>The whole size, when known (a zipped folder knows the bytes it will read, not what it will send).</summary>
    public long? TotalBytes { get; internal set; }
    public long Bytes => Interlocked.Read(ref this.bytes);
    public DateTimeOffset StartedUtc { get; }
    public TransferState State { get; private set; }
    public string? Error { get; private set; }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(this.startTicks, this.endTicks == 0 ? Stopwatch.GetTimestamp() : this.endTicks);
    public double? Fraction => this.TotalBytes is > 0 and var t ? Math.Clamp((double)this.Bytes / t, 0, 1) : null;

    /// <summary>Bytes per second, smoothed over the last few seconds; the average once finished.</summary>
    public double BytesPerSecond
    {
        get
        {
            if (this.State != TransferState.Running)
                return this.Elapsed.TotalSeconds > 0 ? this.Bytes / this.Elapsed.TotalSeconds : 0;

            lock (this.cancel)
            {
                var now = Stopwatch.GetTimestamp();
                var seconds = Stopwatch.GetElapsedTime(this.sampleTicks, now).TotalSeconds;
                if (seconds >= 0.5)
                {
                    var current = this.Bytes;
                    var instant = (current - this.sampleBytes) / seconds;
                    this.speed = this.speed == 0 ? instant : this.speed * 0.6 + instant * 0.4;
                    this.sampleBytes = current;
                    this.sampleTicks = now;
                }
                return this.speed;
            }
        }
    }

    /// <summary>Time left at the current speed, when the size is known and it's moving.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (this.State != TransferState.Running || this.TotalBytes is not { } total)
                return null;

            var speed = this.BytesPerSecond;
            return speed > 1 ? TimeSpan.FromSeconds(Math.Max(0, total - this.Bytes) / speed) : null;
        }
    }

    internal CancellationToken Token => this.cancel.Token;

    /// <summary>Stops it - the visitor's request is cut off.</summary>
    public void Cancel()
    {
        if (this.State == TransferState.Running)
            this.cancel.Cancel();
    }

    internal void Add(long count)
    {
        Interlocked.Add(ref this.bytes, count);
        if (this.Direction == TransferDirection.Download)
            this.Share.AddDown(count);
        else
            this.Share.AddUp(count);
    }

    internal bool Finish(TransferState state, string? error = null)
    {
        if (this.State != TransferState.Running)
            return false;

        this.State = state;
        this.Error = error;
        this.endTicks = Stopwatch.GetTimestamp();
        return true;
    }
}

public enum ActivityKind
{
    Visit,
    KeyAccepted,
    KeyRefused,
    Download,
    Upload,
    CreateFolder,
    Rename,
    Delete,
    Refused,
    Blocked
}

/// <summary>One line of the activity log.</summary>
public sealed record ActivityEntry(DateTimeOffset AtUtc, ActivityKind Kind, string ClientIp, string? ShareName, string? Path, string? Detail, bool IsTunneled);

/// <summary>A client seen recently, by address.</summary>
public sealed record ClientInfo(string Ip, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, int Requests, bool IsTunneled);

/// <summary>
/// Everything the management window shows: transfers in flight and recently finished, the activity log, totals and
/// the clients seen. Written from request threads; the window reads <see cref="Snapshot"/> on a timer, and
/// <see cref="Changed"/> fires (on whatever thread) when something starts or ends.
/// </summary>
public sealed class TransferMonitor
{
    const int MaxFinished = 50;
    const int MaxActivity = 500;
    static readonly TimeSpan ClientWindow = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<long, Transfer> running = new();
    readonly LinkedList<Transfer> finished = new();
    readonly LinkedList<ActivityEntry> activity = new();
    readonly ConcurrentDictionary<string, ClientInfo> clients = new();
    readonly Lock gate = new();
    long nextId;
    long bytesDown, bytesUp;
    int filesDown, filesUp;

    public event EventHandler? Changed;

    /// <summary>Fires for each finished upload - the app can tell the user.</summary>
    public event EventHandler<Transfer>? UploadCompleted;

    public IReadOnlyList<Transfer> Running => this.running.Values.OrderBy(x => x.Id).ToList();
    public int RunningCount => this.running.Count;

    public IReadOnlyList<Transfer> Finished
    {
        get
        {
            lock (this.gate)
                return this.finished.ToList();
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> Activity
    {
        get
        {
            lock (this.gate)
                return this.activity.ToList();
        }
    }

    public long BytesDown => Interlocked.Read(ref this.bytesDown);
    public long BytesUp => Interlocked.Read(ref this.bytesUp);
    public int FilesDown => Volatile.Read(ref this.filesDown);
    public int FilesUp => Volatile.Read(ref this.filesUp);

    /// <summary>Addresses that made a request in the last five minutes, most recent first.</summary>
    public IReadOnlyList<ClientInfo> Clients
    {
        get
        {
            var since = DateTimeOffset.UtcNow - ClientWindow;
            return this.clients.Values.Where(x => x.LastSeenUtc >= since).OrderByDescending(x => x.LastSeenUtc).ToList();
        }
    }

    /// <summary>Total bytes per second over every running transfer.</summary>
    public double BytesPerSecond => this.running.Values.Sum(x => x.BytesPerSecond);

    internal Transfer Start(ServedFolder share, string clientIp, string path, TransferDirection direction, long? total, bool tunneled)
    {
        var transfer = new Transfer(Interlocked.Increment(ref this.nextId), share, clientIp, path, direction, total, tunneled);
        this.running[transfer.Id] = transfer;
        this.Raise();
        return transfer;
    }

    internal void End(Transfer transfer, TransferState state, string? error = null)
    {
        if (!transfer.Finish(state, error))
            return;

        this.running.TryRemove(transfer.Id, out _);
        lock (this.gate)
        {
            this.finished.AddFirst(transfer);
            while (this.finished.Count > MaxFinished)
                this.finished.RemoveLast();
        }

        if (transfer.Direction == TransferDirection.Download)
            Interlocked.Add(ref this.bytesDown, transfer.Bytes);
        else
            Interlocked.Add(ref this.bytesUp, transfer.Bytes);

        if (state == TransferState.Completed)
        {
            if (transfer.Direction == TransferDirection.Download)
            {
                Interlocked.Increment(ref this.filesDown);
                transfer.Share.FileDownloaded();
                this.Log(ActivityKind.Download, transfer.ClientIp, transfer.Share, transfer.Path, null, transfer.IsTunneled);
            }
            else
            {
                Interlocked.Increment(ref this.filesUp);
                transfer.Share.FileUploaded();
                this.Log(ActivityKind.Upload, transfer.ClientIp, transfer.Share, transfer.Path, null, transfer.IsTunneled);
                this.UploadCompleted?.Invoke(this, transfer);
            }
        }
        this.Raise();
    }

    internal void Seen(string ip, bool tunneled)
    {
        var now = DateTimeOffset.UtcNow;
        this.clients.AddOrUpdate(
            ip,
            static (key, state) => new ClientInfo(key, state.now, state.now, 1, state.tunneled),
            static (_, existing, state) => existing with { LastSeenUtc = state.now, Requests = existing.Requests + 1 },
            (now, tunneled)
        );
    }

    internal void Log(ActivityKind kind, string ip, ServedFolder? share, string? path, string? detail, bool tunneled)
    {
        lock (this.gate)
        {
            this.activity.AddFirst(new ActivityEntry(DateTimeOffset.UtcNow, kind, ip, share?.Name, path, detail, tunneled));
            while (this.activity.Count > MaxActivity)
                this.activity.RemoveLast();
        }
        this.Raise();
    }

    /// <summary>Stops every running transfer of a share (or all of them).</summary>
    public void CancelAll(ServedFolder? share = null)
    {
        foreach (var transfer in this.running.Values.Where(x => share == null || x.Share == share))
            transfer.Cancel();
    }

    /// <summary>Forgets the finished transfers and the log.</summary>
    public void ClearHistory()
    {
        lock (this.gate)
        {
            this.finished.Clear();
            this.activity.Clear();
        }
        this.Raise();
    }

    void Raise() => this.Changed?.Invoke(this, EventArgs.Empty);
}
