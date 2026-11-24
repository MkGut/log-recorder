namespace Log.Core.Timeline;

/// <summary>
/// The built-in retention schedule for an open-ended capture.
///
/// A stage keeps a bounded sample budget: the one-second stage lasts one
/// minute, the two-second stage lasts two minutes, the four-second stage lasts
/// four minutes, and so on. When a stage changes, the retained timeline is
/// rebuilt at the new interval, which is what removes the superseded samples
/// from the earlier stages. The schedule is independent of output frame rate.
/// </summary>
public static class AdaptiveSamplingPolicy
{
    private const long InitialIntervalSeconds = 1;
    private const long StageBudgetMinutes = 1;

    /// <summary>
    /// Prevents an unbounded session from eventually overflowing a QPC
    /// interval or becoming so sparse that it is no longer useful.
    /// </summary>
    public static long MaximumIntervalTicks { get; } =
        checked(60 * TimelineConstants.TicksPerSecond);

    public static long GetIntervalTicks(long activeElapsedTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeElapsedTicks);

        var intervalTicks = checked(InitialIntervalSeconds * TimelineConstants.TicksPerSecond);
        var stageDurationTicks = checked(StageBudgetMinutes * 60 * intervalTicks);
        var remainingTicks = activeElapsedTicks;

        while (intervalTicks < MaximumIntervalTicks && remainingTicks >= stageDurationTicks)
        {
            intervalTicks = Math.Min(MaximumIntervalTicks, checked(intervalTicks * 2));
            stageDurationTicks = Math.Min(
                long.MaxValue,
                checked(stageDurationTicks * 2));
            remainingTicks -= stageDurationTicks / 2;
        }

        return intervalTicks;
    }

    /// <summary>
    /// Builds the samples that should remain for a completed active duration.
    /// A target exactly at the duration is excluded, matching the master
    /// timeline's half-open slot semantics.
    /// </summary>
    public static IReadOnlyList<long> BuildRetainedTargets(long activeDurationTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeDurationTicks);
        if (activeDurationTicks == 0)
        {
            return [];
        }

        // At a stage boundary the next cadence is already active. The target
        // exactly at the duration is excluded, so using the duration itself
        // selects the new cadence without adding an out-of-range target.
        var intervalTicks = GetIntervalTicks(activeDurationTicks);
        var count = checked((int)((activeDurationTicks - 1) / intervalTicks + 1));
        var targets = new long[count];
        for (var index = 0; index < count; index++)
        {
            targets[index] = checked((long)index * intervalTicks);
        }

        return targets;
    }

    /// <summary>Returns the stage ranges intersecting an active duration.</summary>
    public static IReadOnlyList<AdaptiveSamplingStage> BuildStages(long activeDurationTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeDurationTicks);
        if (activeDurationTicks == 0)
        {
            return [];
        }

        var stages = new List<AdaptiveSamplingStage>();
        var startTicks = 0L;
        var intervalTicks = checked(InitialIntervalSeconds * TimelineConstants.TicksPerSecond);
        while (startTicks < activeDurationTicks)
        {
            var stageDurationTicks = StageDurationTicks(intervalTicks);
            // Once capped, keep the final stage open for the rest of the
            // session. It must not silently stop describing an indefinite run.
            var endTicks = intervalTicks == MaximumIntervalTicks
                ? activeDurationTicks
                : Math.Min(activeDurationTicks, checked(startTicks + stageDurationTicks));
            stages.Add(new AdaptiveSamplingStage(startTicks, endTicks, intervalTicks));
            if (intervalTicks == MaximumIntervalTicks)
            {
                break;
            }

            startTicks = endTicks;
            intervalTicks = Math.Min(MaximumIntervalTicks, checked(intervalTicks * 2));
        }

        return stages;
    }

    private static long StageDurationTicks(long intervalTicks)
    {
        return checked(intervalTicks * 60);
    }
}

public sealed record AdaptiveSamplingStage
{
    public AdaptiveSamplingStage(long startTicks, long endTicks, long intervalTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endTicks, startTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);
        StartTicks = startTicks;
        EndTicks = endTicks;
        IntervalTicks = intervalTicks;
    }

    public long StartTicks { get; }
    public long EndTicks { get; }
    public long IntervalTicks { get; }
}
