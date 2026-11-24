namespace Log.Core.Capture;

public sealed class CaptureMetrics
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> dropCounts = new(StringComparer.Ordinal);
    private int scheduledSlotCount;
    private int filledSlotCount;
    private int duplicateSlotCount;
    private int gapSlotCount;
    private int frameArrivalCount;
    private long maxJitterTicks;

    public void RecordFrameArrived()
    {
        lock (gate)
        {
            frameArrivalCount++;
        }
    }

    public void RecordScheduledSlot(long intendedTicks, long servicedTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(intendedTicks);
            ArgumentOutOfRangeException.ThrowIfNegative(servicedTicks);
            lock (gate)
            {
                scheduledSlotCount++;
                var jitter = servicedTicks >= intendedTicks
                    ? servicedTicks - intendedTicks
                    : intendedTicks - servicedTicks;
                maxJitterTicks = Math.Max(maxJitterTicks, jitter);
            }
    }

    public void RecordSlot(bool isGap, bool isDuplicate)
    {
        lock (gate)
        {
            if (isGap)
            {
                gapSlotCount++;
            }
            else
            {
                filledSlotCount++;
            }

            if (isDuplicate)
            {
                duplicateSlotCount++;
            }
        }
    }

    public void RecordDropped(CaptureDropReason reason)
    {
        lock (gate)
        {
            var key = reason switch
            {
                CaptureDropReason.Encoder => "encoder",
                CaptureDropReason.Disk => "disk",
                CaptureDropReason.Gpu => "gpu",
                CaptureDropReason.Scheduler => "scheduler",
                CaptureDropReason.OversizeFrame => "oversizeframe",
                _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown capture drop reason."),
            };
            dropCounts[key] = dropCounts.TryGetValue(key, out var count) ? count + 1 : 1;
        }
    }

    public CaptureMetricsSnapshot Snapshot()
    {
        lock (gate)
        {
            return new CaptureMetricsSnapshot(
                scheduledSlotCount,
                filledSlotCount,
                duplicateSlotCount,
                gapSlotCount,
                frameArrivalCount,
                maxJitterTicks,
                dropCounts.Values.Sum(),
                new Dictionary<string, int>(dropCounts, StringComparer.Ordinal));
        }
    }
}

public enum CaptureDropReason
{
    Encoder,
    Disk,
    Gpu,
    Scheduler,
    OversizeFrame
}

public sealed record CaptureMetricsSnapshot(
    int ScheduledSlotCount,
    int FilledSlotCount,
    int DuplicateSlotCount,
    int GapSlotCount,
    int FrameArrivalCount,
    long MaxJitterTicks,
    int DroppedFrameCount,
    IReadOnlyDictionary<string, int> DropCounts);
