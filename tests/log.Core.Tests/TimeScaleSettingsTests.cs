using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class TimeScaleSettingsTests
{
    [Fact]
    public void DefaultSettingsProduceTheThirtyTimesOneSecondInterval()
    {
        var settings = new TimeScaleSettings();

        Assert.Equal(30, settings.SpeedMultiplier);
        Assert.Equal(30, settings.OutputFramesPerSecond);
        Assert.Equal(10_000_000, settings.CaptureIntervalTicks);
        Assert.Equal(30, settings.EffectiveSpeedMultiplier);
        Assert.False(settings.UsesAdvancedIntervalOverride);
    }

    [Fact]
    public void PresetsDeriveIntervalsWithoutFloatingPointDriftAtCommonRates()
    {
        Assert.Equal(10_000_000 / 3, TimeScaleCalculator.DeriveCaptureIntervalTicks(10, 30));
        Assert.Equal(20_000_000, TimeScaleCalculator.DeriveCaptureIntervalTicks(60, 30));
    }

    [Fact]
    public void AdvancedIntervalOverrideReportsItsEffectiveSpeed()
    {
        var settings = new TimeScaleSettings(
            speedMultiplier: 30,
            outputFramesPerSecond: 60,
            captureIntervalTicksOverride: 10_000_000);

        Assert.True(settings.UsesAdvancedIntervalOverride);
        Assert.Equal(60, settings.EffectiveSpeedMultiplier);
    }

    [Fact]
    public void CaptureOptionsUseTheDerivedInterval()
    {
        var options = CaptureRunOptions.FromTimeScale(
            durationTicks: 60 * TimelineConstants.TicksPerSecond,
            timeScale: new TimeScaleSettings(speedMultiplier: 60, outputFramesPerSecond: 30),
            maxFrameAgeTicks: TimelineConstants.TicksPerSecond);

        Assert.Equal(2 * TimelineConstants.TicksPerSecond, options.IntervalTicks);
        Assert.Equal(60 * TimelineConstants.TicksPerSecond, options.DurationTicks);
    }

    [Fact]
    public void EstimatesNormalAndHighlightHoldDuration()
    {
        var seconds = TimeScaleCalculator.EstimateOutputDurationSeconds(
            activeDurationTicks: 25 * TimelineConstants.TicksPerSecond,
            captureIntervalTicks: 10 * TimelineConstants.TicksPerSecond,
            outputFramesPerSecond: 30,
            additionalHoldTicks: 15 * TimelineConstants.TicksPerSecond / 10);

        Assert.Equal(1.6, seconds);
    }

    // The export-length preview must match what the renderer actually emits:
    // EstimateNormalFrameCount has to equal the BuildSlots count the render uses.
    [Theory]
    [InlineData(0, 25, 10, 3)]
    [InlineData(0, 20, 10, 2)]
    [InlineData(0, 30, 10, 3)]
    [InlineData(0, 1, 10, 1)]
    [InlineData(100, 100, 10, 0)]
    public void EstimatedNormalFrameCountMatchesTheRenderedSlotCount(
        long start,
        long stop,
        long interval,
        int expected)
    {
        var slots = TimelinePlanner.BuildSlots(start, stop, interval);
        var active = TimelinePlanner.ActiveDuration(stop - start, start, stop, []);

        Assert.Equal(expected, slots.Count);
        Assert.Equal(slots.Count, TimeScaleCalculator.EstimateNormalFrameCount(active, interval));
    }

    [Fact]
    public void EstimatedNormalFrameCountAccountsForElidedPauses()
    {
        var pause = new PauseRange(15, 25);
        var slots = TimelinePlanner.BuildSlots(0, 50, 10, [pause]);
        var active = TimelinePlanner.ActiveDuration(50, 0, 50, [pause]);

        Assert.Equal(slots.Count, TimeScaleCalculator.EstimateNormalFrameCount(active, captureIntervalTicks: 10));
    }

    [Fact]
    public void RejectsUnsupportedSpeedAndOutputRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeScaleSettings(speedMultiplier: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeScaleSettings(outputFramesPerSecond: 25));
    }
}
