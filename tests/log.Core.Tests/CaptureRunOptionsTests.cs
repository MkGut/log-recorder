using Log.Core.Capture;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class CaptureRunOptionsTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    // Regression guard for the "starts fine, then goes black" failure: an
    // indefinite run rides the adaptive schedule out to one sample per 60s, so a
    // freshness ceiling smaller than the interval (the shell requested 10s) would
    // age held frames into black gaps. Indefinite runs must therefore hold the
    // last frame regardless of the requested ceiling.
    [Fact]
    public void IndefiniteRunHoldsTheLastFrameRegardlessOfRequestedMaxFrameAge()
    {
        var options = new CaptureRunOptions(
            durationTicks: 0,
            intervalTicks: Second,
            maxFrameAgeTicks: 10 * Second,
            sourceMode: "screen");

        Assert.True(options.IsIndefinite);
        Assert.Equal(long.MaxValue, options.MaxFrameAgeTicks);
        // The held ceiling must exceed the coarsest adaptive interval, or coarse
        // stages would still gap to black.
        Assert.True(options.MaxFrameAgeTicks >= AdaptiveSamplingPolicy.MaximumIntervalTicks);
    }

    // A bounded, fixed-interval run (deterministic tests, supervised runs) keeps
    // its requested freshness ceiling: staleness there is a real source stall.
    [Fact]
    public void BoundedRunKeepsTheRequestedMaxFrameAge()
    {
        var options = new CaptureRunOptions(
            durationTicks: 30 * Second,
            intervalTicks: 10 * Second,
            maxFrameAgeTicks: 2 * Second,
            sourceMode: "screen");

        Assert.False(options.IsIndefinite);
        Assert.Equal(2 * Second, options.MaxFrameAgeTicks);
    }

    [Fact]
    public void RandomAdaptiveRunKeepsFreshnessCeilingAndSeed()
    {
        var options = new CaptureRunOptions(
            durationTicks: 0,
            intervalTicks: Second,
            maxFrameAgeTicks: 10 * Second,
            sourceMode: "screen",
            samplingMode: CaptureSamplingMode.Random,
            randomSeed: 123);

        Assert.True(options.IsIndefinite);
        Assert.True(options.IsRandomSampling);
        Assert.False(options.IsAdaptiveSampling);
        Assert.Equal(10 * Second, options.MaxFrameAgeTicks);
        Assert.Equal(123, options.RandomSeed);
    }

    [Fact]
    public void RandomAdaptiveIsOnlyValidForIndefiniteRuns()
    {
        Assert.Throws<ArgumentException>(() => new CaptureRunOptions(
            durationTicks: Second,
            intervalTicks: Second,
            maxFrameAgeTicks: Second,
            samplingMode: CaptureSamplingMode.Random));
    }

    [Theory]
    [InlineData("camera")]
    [InlineData("screen+camera")]
    public void NewCaptureOptionsRejectCameraModes(string sourceMode)
    {
        Assert.Throws<ArgumentException>(() => new CaptureRunOptions(
            durationTicks: Second,
            intervalTicks: Second,
            maxFrameAgeTicks: Second,
            sourceMode: sourceMode));
    }
}
