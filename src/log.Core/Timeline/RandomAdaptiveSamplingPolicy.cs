using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Log.Core.Timeline;

/// <summary>
/// Chooses one deterministic target inside each normal adaptive window.
/// The seed and selected windows are persisted with the session, so this
/// policy is only used to create a window the first time it is reached.
/// </summary>
public static class RandomAdaptiveSamplingPolicy
{
    public const string AlgorithmVersion = "splitmix64-v1";

    public static long CreateSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToInt64(bytes);
    }

    public static RandomSamplingWindow BuildWindow(long startTicks, long seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);

        var intervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(startTicks);
        var endTicks = checked(startTicks + intervalTicks);
        var targetTicks = checked(startTicks + SelectOffset(seed, startTicks, intervalTicks));
        return new RandomSamplingWindow(startTicks, endTicks, intervalTicks, targetTicks);
    }

    /// <summary>
    /// Builds the windows intersecting an active duration. The final window is
    /// included even when the duration ends before its scheduled target; the
    /// caller decides whether that target was actually reached.
    /// </summary>
    public static IReadOnlyList<RandomSamplingWindow> BuildSchedule(
        long activeDurationTicks,
        long seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeDurationTicks);
        var windows = new List<RandomSamplingWindow>();
        for (var startTicks = 0L; startTicks < activeDurationTicks;)
        {
            var window = BuildWindow(startTicks, seed);
            windows.Add(window);
            startTicks = window.EndTicks;
        }

        return windows;
    }

    private static long SelectOffset(long seed, long startTicks, long intervalTicks)
    {
        var bound = checked((ulong)intervalTicks);
        var state = unchecked((ulong)seed)
            ^ unchecked((ulong)startTicks * 0x9E37_79B9_7F4A_7C15UL)
            ^ 0xD1B5_4A32_D192_ED03UL;
        var rejectionThreshold = unchecked(0UL - bound) % bound;

        while (true)
        {
            var value = NextUInt64(ref state);
            if (value >= rejectionThreshold)
            {
                return checked((long)(value % bound));
            }
        }
    }

    private static ulong NextUInt64(ref ulong state)
    {
        state += 0x9E37_79B9_7F4A_7C15UL;
        var value = state;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }
}

public sealed record RandomSamplingWindow
{
    [JsonConstructor]
    public RandomSamplingWindow(
        long startTicks,
        long endTicks,
        long intervalTicks,
        long targetTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endTicks, startTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);
        if (endTicks != checked(startTicks + intervalTicks))
        {
            throw new ArgumentException(
                "A random sampling window end must equal its start plus interval.",
                nameof(endTicks));
        }

        if (targetTicks < startTicks || targetTicks >= endTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTicks),
                "A random sampling target must be inside its half-open window.");
        }

        StartTicks = startTicks;
        EndTicks = endTicks;
        IntervalTicks = intervalTicks;
        TargetTicks = targetTicks;
    }

    public long StartTicks { get; }
    public long EndTicks { get; }
    public long IntervalTicks { get; }
    public long TargetTicks { get; }
}
