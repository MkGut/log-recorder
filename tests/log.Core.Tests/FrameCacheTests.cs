using Log.Core.Capture;
using Xunit;

namespace Log.Core.Tests;

public sealed class FrameCacheTests
{
    [Fact]
    public void RetainsLatestTimestampedFramesWithinByteBudget()
    {
        var cache = new FrameCache(maxBytes: 3);
        cache.Publish(new FrameSample(10, new byte[] { 1, 1 }, 1, 1));
        cache.Publish(new FrameSample(20, new byte[] { 2, 2 }, 1, 1));
        cache.Publish(new FrameSample(30, new byte[] { 3 }, 1, 1));

        var frames = cache.SnapshotAll();

        Assert.Equal([20, 30], frames.Select(frame => frame.SourceTimestampTicks));
        Assert.Equal(30, cache.Snapshot()!.SourceTimestampTicks);
    }

    [Fact]
    public void DropsAFrameThatCannotFitWithinTheByteBudget()
    {
        var cache = new FrameCache(maxBytes: 2);
        cache.Publish(new FrameSample(10, new byte[] { 1, 2, 3 }, 1, 1));

        Assert.Null(cache.Snapshot());
        Assert.Empty(cache.SnapshotAll());
        Assert.Equal(1, cache.DroppedOversizeFrameCount);
    }

    [Fact]
    public void AdvancesTheCurrentEpochAndPrefersItsNewestFrame()
    {
        var cache = new FrameCache();
        cache.Publish(new FrameSample(100, new byte[] { 1 }, 1, 1, sourceEpoch: 0));
        cache.Publish(new FrameSample(10, new byte[] { 2 }, 1, 1, sourceEpoch: 1));

        Assert.Equal(1, cache.CurrentSourceEpoch);
        Assert.Equal(1, cache.Snapshot()!.SourceEpoch);
        Assert.Equal(10, cache.Snapshot()!.SourceTimestampTicks);
    }

    [Fact]
    public void ClearsRetainedFramesWithoutResettingTheSourceFrontier()
    {
        var cache = new FrameCache();
        cache.Publish(new FrameSample(100, new byte[] { 1 }, 1, 1, sourceEpoch: 2));

        cache.Clear();

        Assert.Null(cache.Snapshot());
        Assert.Empty(cache.SnapshotAll());
        Assert.Equal(2, cache.CurrentSourceEpoch);

        cache.Publish(new FrameSample(90, new byte[] { 2 }, 1, 1, sourceEpoch: 2), out var isOutOfOrder);

        Assert.True(isOutOfOrder);
    }

    [Fact]
    public void ReplacesTheNewestPayloadForAnEqualTimestamp()
    {
        var cache = new FrameCache();
        cache.Publish(new FrameSample(10, new byte[] { 1 }, 1, 1, sourceEpoch: 2));
        cache.Publish(new FrameSample(10, new byte[] { 2 }, 1, 1, sourceEpoch: 2));

        var frame = Assert.Single(cache.SnapshotAll());

        Assert.Equal(new byte[] { 2 }, frame.Payload.ToArray());
        Assert.Equal(new byte[] { 2 }, cache.Snapshot()!.Payload.ToArray());
    }

    [Fact]
    public void BudgetEvictionUsesTimestampOrderAfterOutOfOrderArrival()
    {
        var cache = new FrameCache(maxBytes: 3);
        cache.Publish(new FrameSample(100, new byte[] { 1 }, 1, 1));
        cache.Publish(new FrameSample(200, new byte[] { 2 }, 1, 1));
        cache.Publish(new FrameSample(300, new byte[] { 3 }, 1, 1));

        cache.Publish(new FrameSample(10, new byte[] { 4 }, 1, 1));

        Assert.Equal([100, 200, 300], cache.SnapshotAll().Select(frame => frame.SourceTimestampTicks));
        Assert.Equal(300, cache.Snapshot()!.SourceTimestampTicks);
    }
}
