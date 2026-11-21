using ShinyCommander.Core.Operations;

namespace ShinyCommander.Core.Tests;

public class TransferMeterTests
{
    const long MB = 1024 * 1024;

    [Fact]
    public void UnknownUntilASecondHasPassed()
    {
        var meter = new TransferMeter();
        meter.Add(0, 0);
        meter.Add(10 * MB, 900);

        meter.BytesPerSecond.ShouldBeNull();
        meter.Remaining(10 * MB, 100 * MB).ShouldBeNull();
    }

    [Fact]
    public void MeasuresSpeedAndTimeLeft()
    {
        var meter = new TransferMeter();
        for (var t = 0; t <= 2000; t += 100)
            meter.Add(t * 10 * MB / 1000, t); // 10 MB/s

        meter.BytesPerSecond!.Value.ShouldBe(10 * MB, 1);
        meter.Remaining(20 * MB, 120 * MB)!.Value.TotalSeconds.ShouldBe(10, 0.01);
    }

    [Fact]
    public void FollowsTheLastFewSecondsNotTheWholeRun()
    {
        var meter = new TransferMeter();
        var bytes = 0L;
        for (var t = 0; t <= 20_000; t += 100)
        {
            bytes += t <= 10_000 ? 10 * MB / 10 : 1 * MB / 10; // 10 MB/s, then 1 MB/s
            meter.Add(bytes, t);
        }

        meter.BytesPerSecond!.Value.ShouldBe(1 * MB, MB / 10.0);
    }

    [Fact]
    public void HoldsTheFiguresBetweenRefreshes()
    {
        var meter = new TransferMeter();
        meter.Add(0, 0);
        meter.Add(10 * MB, 1000);
        var first = meter.BytesPerSecond;

        meter.Add(100 * MB, 1200); // a burst inside the half second - not shown yet
        meter.BytesPerSecond.ShouldBe(first);

        meter.Add(100 * MB, 1500);
        meter.BytesPerSecond.ShouldNotBe(first);
    }

    [Fact]
    public void NoTimeLeftWhenStalled()
    {
        var meter = new TransferMeter();
        meter.Add(5 * MB, 0);
        meter.Add(5 * MB, 2000);

        meter.BytesPerSecond.ShouldBe(0);
        meter.Remaining(5 * MB, 10 * MB).ShouldBeNull();
    }
}
