using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class AdaptiveSamplingPolicyTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    [Fact]
    public void UsesOneSecondForTheFirstMinute()
    {
        Assert.Equal(Second, AdaptiveSamplingPolicy.GetIntervalTicks(0));
        Assert.Equal(Second, AdaptiveSamplingPolicy.GetIntervalTicks(59 * Second));
        Assert.Equal(2 * Second, AdaptiveSamplingPolicy.GetIntervalTicks(60 * Second));
    }

    [Fact]
    public void StageDurationsGrowWithTheInterval()
    {
        var stages = AdaptiveSamplingPolicy.BuildStages(8 * 60 * Second);

        Assert.Equal(
            [
                new AdaptiveSamplingStage(0, 60 * Second, Second),
                new AdaptiveSamplingStage(60 * Second, 180 * Second, 2 * Second),
                new AdaptiveSamplingStage(180 * Second, 420 * Second, 4 * Second),
                new AdaptiveSamplingStage(420 * Second, 8 * 60 * Second, 8 * Second)
            ],
            stages);
    }

    [Fact]
    public void RetainedTargetsUseTheCurrentCadenceAcrossTheWholeTimeline()
    {
        var targets = AdaptiveSamplingPolicy.BuildRetainedTargets(2 * 60 * Second);

        Assert.Equal(60, targets.Count);
        Assert.Equal(0, targets[0]);
        Assert.Equal(2 * Second, targets[1]);
        Assert.Equal(118 * Second, targets[^1]);
    }

    [Fact]
    public void EmptyDurationHasNoTargets()
    {
        Assert.Empty(AdaptiveSamplingPolicy.BuildRetainedTargets(0));
        Assert.Empty(AdaptiveSamplingPolicy.BuildStages(0));
    }

    [Fact]
    public void IndefiniteScheduleCapsAtOneMinuteCadence()
    {
        Assert.Equal(
            AdaptiveSamplingPolicy.MaximumIntervalTicks,
            AdaptiveSamplingPolicy.GetIntervalTicks(long.MaxValue));
    }

    [Fact]
    public void LongSessionsNeverExceedOneMinuteBetweenRetainedTargets()
    {
        var targets = AdaptiveSamplingPolicy.BuildRetainedTargets(100 * 60 * Second);

        Assert.Equal(AdaptiveSamplingPolicy.MaximumIntervalTicks,
            AdaptiveSamplingPolicy.GetIntervalTicks(100 * 60 * Second));
        Assert.Equal(100, targets.Count);
        Assert.All(
            targets.Zip(targets.Skip(1)),
            pair => Assert.InRange(
                pair.Second - pair.First,
                Second,
                AdaptiveSamplingPolicy.MaximumIntervalTicks));
    }
}
