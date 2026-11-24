using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class RandomAdaptiveSamplingPolicyTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    [Fact]
    public void SameSeedProducesTheSameWindowsAndTargets()
    {
        var first = RandomAdaptiveSamplingPolicy.BuildSchedule(430 * Second, 42);
        var second = RandomAdaptiveSamplingPolicy.BuildSchedule(430 * Second, 42);

        Assert.Equal(first, second);
        Assert.Equal(182, first.Count);
    }

    [Fact]
    public void WindowsAreContiguousAndTargetsStayInsideTheirHalfOpenBounds()
    {
        var schedule = RandomAdaptiveSamplingPolicy.BuildSchedule(181 * Second, -7);

        Assert.Equal(
            [Second, 2 * Second, 4 * Second],
            schedule.Select(static window => window.IntervalTicks).Distinct());
        Assert.Equal(121, schedule.Count);
        var expectedStart = 0L;
        foreach (var window in schedule)
        {
            Assert.Equal(expectedStart, window.StartTicks);
            Assert.Equal(window.StartTicks + window.IntervalTicks, window.EndTicks);
            Assert.InRange(window.TargetTicks, window.StartTicks, window.EndTicks - 1);
            expectedStart = window.EndTicks;
        }

        // The final window is durable even though this short active duration
        // ends before its target may be reached; rendering filters it out.
        Assert.Equal(180 * Second, schedule[^1].StartTicks);
        Assert.True(schedule[^1].TargetTicks >= 180 * Second);
    }

    [Fact]
    public void DifferentSeedsChangeTheDeterministicTargetSequence()
    {
        var first = RandomAdaptiveSamplingPolicy.BuildSchedule(430 * Second, 1);
        var second = RandomAdaptiveSamplingPolicy.BuildSchedule(430 * Second, 2);

        Assert.Contains(
            first.Zip(second),
            pair => pair.First.TargetTicks != pair.Second.TargetTicks);
    }
}
