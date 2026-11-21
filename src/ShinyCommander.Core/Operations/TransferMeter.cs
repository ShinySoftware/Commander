namespace ShinyCommander.Core.Operations;

/// <summary>
/// The speed and time left of a running copy/move, measured over the last few seconds rather than since it started -
/// so it follows a slow patch (a network share, a pile of tiny files) without one buffer swinging it about. It only
/// re-measures every half second, so the figures can be shown as they are without flickering.
/// </summary>
public sealed class TransferMeter
{
    const long WindowMs = 5000;
    const long MinSpanMs = 1000;
    const long RefreshMs = 500;
    static readonly double MaxRemainingSeconds = TimeSpan.FromHours(100).TotalSeconds;

    // (tick, bytes done) - one sample at or past the window is kept so the span covers the whole window
    readonly List<(long Tick, long Bytes)> samples = [];
    long? measured;

    /// <summary>Null until there's at least a second to go on.</summary>
    public double? BytesPerSecond { get; private set; }

    /// <param name="bytesDone">The running total, not the bytes since the last call.</param>
    /// <param name="tick">Milliseconds, <see cref="Environment.TickCount64"/> style.</param>
    public void Add(long bytesDone, long tick)
    {
        this.samples.Add((tick, bytesDone));
        var drop = this.samples.FindIndex(x => tick - x.Tick <= WindowMs) - 1;
        if (drop > 0)
            this.samples.RemoveRange(0, drop);

        var (firstTick, firstBytes) = this.samples[0];
        var span = tick - firstTick;
        if (span >= MinSpanMs && (this.measured is not long last || tick - last >= RefreshMs))
        {
            this.measured = tick;
            this.BytesPerSecond = Math.Max(0, bytesDone - firstBytes) * 1000.0 / span;
        }
    }

    /// <summary>Null while the speed isn't known, the transfer has stalled, or the answer is too far off to mean anything.</summary>
    public TimeSpan? Remaining(long bytesDone, long bytesTotal)
    {
        if (this.BytesPerSecond is not double rate || rate <= 0)
            return null;

        var seconds = Math.Max(0, bytesTotal - bytesDone) / rate;
        return seconds > MaxRemainingSeconds ? null : TimeSpan.FromSeconds(seconds);
    }
}
