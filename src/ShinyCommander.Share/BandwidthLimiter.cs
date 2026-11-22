using System.Diagnostics;

namespace ShinyCommander.Share;

/// <summary>
/// A token bucket shared by every transfer of one share: <see cref="BytesPerSecond"/> across all of them, with up to a
/// quarter of a second of burst. 0 lets everything through untouched.
/// </summary>
sealed class BandwidthLimiter(long bytesPerSecond)
{
    readonly Lock gate = new();
    double tokens;
    long lastTicks = Stopwatch.GetTimestamp();

    public long BytesPerSecond { get; set; } = bytesPerSecond;

    /// <summary>Waits until <paramref name="bytes"/> may move.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var rate = this.BytesPerSecond;
        if (rate <= 0 || bytes <= 0)
            return;

        TimeSpan delay;
        lock (this.gate)
        {
            var now = Stopwatch.GetTimestamp();
            var burst = rate / 4.0;
            this.tokens = Math.Min(burst, this.tokens + Stopwatch.GetElapsedTime(this.lastTicks, now).TotalSeconds * rate);
            this.lastTicks = now;

            // spend now, owe the rest - the debt is what the caller sleeps off
            this.tokens -= bytes;
            delay = this.tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-this.tokens / rate);
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
