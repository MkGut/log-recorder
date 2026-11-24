using Log.Core.Timeline;

namespace Log.Core.Store;

/// <summary>
/// Durable random-adaptive sampling metadata. A window's target is the
/// recorded decision; it must never be regenerated during recovery or export.
/// </summary>
public sealed class RandomSamplingSchedule
{
    public long Seed { get; init; }
    public string AlgorithmVersion { get; set; } = RandomAdaptiveSamplingPolicy.AlgorithmVersion;
    public List<RandomSamplingWindow> Windows { get; set; } = [];

    public void Validate()
    {
        if (!string.Equals(
                AlgorithmVersion,
                RandomAdaptiveSamplingPolicy.AlgorithmVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The random sampling algorithm '{AlgorithmVersion}' is not supported.");
        }

        if (Windows is null)
        {
            throw new InvalidDataException("The random sampling window list is missing.");
        }

        var expectedStartTicks = 0L;
        foreach (var window in Windows)
        {
            if (window is null
                || window.StartTicks != expectedStartTicks
                || window.IntervalTicks != AdaptiveSamplingPolicy.GetIntervalTicks(window.StartTicks))
            {
                throw new InvalidDataException("The random sampling windows are not a contiguous adaptive schedule.");
            }

            expectedStartTicks = window.EndTicks;
        }
    }

    public RandomSamplingWindow? FindWindow(long startTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        return Windows.FirstOrDefault(window => window.StartTicks == startTicks);
    }
}
