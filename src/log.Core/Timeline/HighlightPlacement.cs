namespace Log.Core.Timeline;

public readonly record struct HighlightPlacement(int NormalFramesBefore, int HoldFrames, long ActiveEventTicks)
{
    public int FirstHoldFrameIndex => NormalFramesBefore;
}

public static class HighlightPlacementCalculator
{
    public static HighlightPlacement Calculate(
        long eventTicks,
        long startTicks,
        long stopTicks,
        long intervalTicks,
        long holdTicks,
        int outputFramesPerSecond,
        IReadOnlyList<PauseRange>? pauses = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eventTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(holdTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputFramesPerSecond);

        var boundedEventTicks = Math.Clamp(eventTicks, startTicks, stopTicks);
        var slots = TimelinePlanner.BuildSlots(startTicks, stopTicks, intervalTicks, pauses);
        var activeEventTicks = TimelinePlanner.ActiveDuration(
            boundedEventTicks - startTicks,
            startTicks,
            boundedEventTicks,
            pauses ?? []);
        var normalFramesBefore = Math.Clamp(
            checked((int)(activeEventTicks / intervalTicks + 1)),
            0,
            slots.Count);
        var holdFrames = RoundHalfUp(
            checked(holdTicks * outputFramesPerSecond),
            TimelineConstants.TicksPerSecond);

        return new HighlightPlacement(normalFramesBefore, holdFrames, activeEventTicks);
    }

    private static int RoundHalfUp(long numerator, long denominator)
    {
        return checked((int)((numerator + denominator / 2) / denominator));
    }
}
