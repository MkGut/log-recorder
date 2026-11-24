using Log.Core.Capture;

namespace Log.Core.Timeline;

public static class FrameSelector
{
    public static (FrameSample Sample, bool IsDuplicate, long AgeTicks)?
        SelectLatestAtOrBefore(IEnumerable<FrameSample> frames, long targetTicks, long maxAgeTicks)
    {
        return SelectLatestAtOrBefore(frames, targetTicks, maxAgeTicks, timestampOffsetTicks: 0);
    }

    public static (FrameSample Sample, bool IsDuplicate, long AgeTicks)?
        SelectLatestAtOrBefore(
            IEnumerable<FrameSample> frames,
            long targetTicks,
            long maxAgeTicks,
            long timestampOffsetTicks,
            int? sourceEpoch = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAgeTicks);

        FrameSample? selected = null;
        var selectedTimestamp = 0L;
        foreach (var frame in frames)
        {
            if (sourceEpoch is int requiredEpoch && frame.SourceEpoch != requiredEpoch)
            {
                continue;
            }

            var alignedTimestamp = checked(frame.SourceTimestampTicks + timestampOffsetTicks);
            if (alignedTimestamp > targetTicks)
            {
                continue;
            }

            if (selected is null || alignedTimestamp > selectedTimestamp)
            {
                selected = frame;
                selectedTimestamp = alignedTimestamp;
            }
        }

        if (selected is null)
        {
            return null;
        }

        var age = targetTicks - selectedTimestamp;
        return age <= maxAgeTicks ? (selected, age > 0, age) : null;
    }
}
