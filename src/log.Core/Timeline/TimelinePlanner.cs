namespace Log.Core.Timeline;

public static class TimelinePlanner
{
    public static IReadOnlyList<TimelineSlot> BuildSlots(
        long startTicks,
        long stopTicks,
        long intervalTicks,
        IEnumerable<PauseRange>? pauses = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(stopTicks, startTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);

        var normalizedPauses = NormalizePauses(pauses ?? []);
        var activeDuration = ActiveDuration(stopTicks - startTicks, startTicks, stopTicks, normalizedPauses);
        var count = CeilingDivide(activeDuration, intervalTicks);
        var slots = new TimelineSlot[count];

        for (var index = 0; index < count; index++)
        {
            var activeTarget = checked((long)index * intervalTicks);
            slots[index] = new TimelineSlot(
                index,
                activeTarget,
                WallTargetForActiveTarget(startTicks, activeTarget, normalizedPauses));
        }

        return slots;
    }

    public static long ActiveDuration(
        long elapsedTicks,
        long startTicks,
        long stopTicks,
        IReadOnlyList<PauseRange> pauses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        var paused = 0L;

        foreach (var pause in NormalizePauses(pauses))
        {
            var overlapStart = Math.Max(startTicks, pause.StartTicks);
            var overlapEnd = Math.Min(stopTicks, pause.EndTicks);
            if (overlapEnd > overlapStart)
            {
                paused = checked(paused + overlapEnd - overlapStart);
            }
        }

        return Math.Max(0, elapsedTicks - paused);
    }

    public static long WallTargetForActiveTarget(
        long startTicks,
        long activeTargetTicks,
        IReadOnlyList<PauseRange>? pauses = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(activeTargetTicks);

        var wallTarget = checked(startTicks + activeTargetTicks);
        foreach (var pause in NormalizePauses(pauses ?? []))
        {
            if (wallTarget <= pause.StartTicks)
            {
                break;
            }

            wallTarget = checked(wallTarget + pause.EndTicks - pause.StartTicks);
        }

        return wallTarget;
    }

    private static IReadOnlyList<PauseRange> NormalizePauses(IEnumerable<PauseRange> pauses)
    {
        var ordered = pauses.OrderBy(static pause => pause.StartTicks).ToArray();
        if (ordered.Length == 0)
        {
            return ordered;
        }

        var normalized = new List<PauseRange>(ordered.Length) { ordered[0] };
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = normalized[^1];
            var current = ordered[index];
            if (current.StartTicks <= previous.EndTicks)
            {
                normalized[^1] = new PauseRange(previous.StartTicks, Math.Max(previous.EndTicks, current.EndTicks));
            }
            else
            {
                normalized.Add(current);
            }
        }

        return normalized;
    }

    private static int CeilingDivide(long value, long divisor)
    {
        if (value == 0)
        {
            return 0;
        }

        return checked((int)((value - 1) / divisor + 1));
    }
}
