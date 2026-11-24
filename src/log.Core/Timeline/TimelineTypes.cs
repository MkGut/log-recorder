using System.Text.Json.Serialization;

namespace Log.Core.Timeline;

public readonly record struct PauseRange
{
    // A struct always has an implicit parameterless ctor that System.Text.Json
    // would prefer, zeroing the get-only props on load; bind through this instead.
    [JsonConstructor]
    public PauseRange(long startTicks, long endTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(endTicks, startTicks);
        StartTicks = startTicks;
        EndTicks = endTicks;
    }

    public long StartTicks { get; }
    public long EndTicks { get; }
}

public readonly record struct TimelineSlot
{
    [JsonConstructor]
    public TimelineSlot(int index, long activeTargetTicks, long wallTargetTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        ActiveTargetTicks = activeTargetTicks;
        WallTargetTicks = wallTargetTicks;
    }

    public int Index { get; }
    public long ActiveTargetTicks { get; }
    public long WallTargetTicks { get; }
}

public static class TimelineConstants
{
    public const long TicksPerSecond = 10_000_000;
}
