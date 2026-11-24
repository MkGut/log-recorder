namespace Log.Core.Capture;

public sealed class FrameCache
{
    private const int DefaultMaxBytes = 32 * 1024 * 1024;
    private readonly object gate = new();
    private readonly int maxBytes;
    private readonly List<FrameSample> frames = new();
    private long cachedBytes;
    private int droppedOversizeFrameCount;
    private int droppedBudgetFrameCount;
    private FrameSample? latest;
    private FrameSample? publishedFrontier;
    private int currentSourceEpoch;

    public FrameCache(int maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        this.maxBytes = maxBytes;
    }

    public bool Publish(FrameSample frame)
    {
        return Publish(frame, out _);
    }

    public bool Publish(FrameSample frame, out bool isOutOfOrder)
    {
        ArgumentNullException.ThrowIfNull(frame);
        isOutOfOrder = false;
        lock (gate)
        {
            if (frame.Payload.Length > maxBytes)
            {
                droppedOversizeFrameCount++;
                return false;
            }

            // Sources may recycle their backing buffer after yielding a frame.
            var ownedFrame = new FrameSample(
                frame.SourceTimestampTicks,
                frame.Payload.ToArray(),
                frame.Width,
                frame.Height,
                frame.ArrivalTimestampTicks,
                frame.TimestampQuality,
                frame.SourceEpoch);
            isOutOfOrder = publishedFrontier is not null
                && (ownedFrame.SourceEpoch < publishedFrontier.SourceEpoch
                    || (ownedFrame.SourceEpoch == publishedFrontier.SourceEpoch
                        && ownedFrame.SourceTimestampTicks < publishedFrontier.SourceTimestampTicks));
            if (publishedFrontier is null
                || ownedFrame.SourceEpoch > publishedFrontier.SourceEpoch
                || (ownedFrame.SourceEpoch == publishedFrontier.SourceEpoch
                    && ownedFrame.SourceTimestampTicks > publishedFrontier.SourceTimestampTicks))
            {
                publishedFrontier = ownedFrame;
            }
            currentSourceEpoch = Math.Max(currentSourceEpoch, ownedFrame.SourceEpoch);
            if (latest is null
                || ownedFrame.SourceEpoch > latest.SourceEpoch
                || (ownedFrame.SourceEpoch == latest.SourceEpoch
                    && ownedFrame.SourceTimestampTicks >= latest.SourceTimestampTicks))
            {
                latest = ownedFrame;
            }

            var replacementIndex = -1;
            for (var index = frames.Count - 1; index >= 0; index--)
            {
                var retained = frames[index];
                if (retained.SourceEpoch == ownedFrame.SourceEpoch
                    && retained.SourceTimestampTicks == ownedFrame.SourceTimestampTicks)
                {
                    replacementIndex = index;
                    break;
                }
            }

            if (replacementIndex >= 0)
            {
                cachedBytes -= frames[replacementIndex].Payload.Length;
                frames[replacementIndex] = ownedFrame;
            }
            else
            {
                frames.Add(ownedFrame);
            }

            cachedBytes = checked(cachedBytes + ownedFrame.Payload.Length);
            TrimToBudget();
        }

        return true;
    }

    public int DroppedOversizeFrameCount
    {
        get
        {
            lock (gate)
            {
                return droppedOversizeFrameCount;
            }
        }
    }

    internal int DrainDroppedBudgetFrameCount()
    {
        lock (gate)
        {
            var count = droppedBudgetFrameCount;
            droppedBudgetFrameCount = 0;
            return count;
        }
    }

    public FrameSample? Snapshot()
    {
        lock (gate)
        {
            return latest;
        }
    }

    public int CurrentSourceEpoch
    {
        get
        {
            lock (gate)
            {
                return currentSourceEpoch;
            }
        }
    }

    public IReadOnlyList<FrameSample> SnapshotAll()
    {
        lock (gate)
        {
            return frames.ToArray();
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            frames.Clear();
            cachedBytes = 0;
            latest = null;
        }
    }

    private void TrimToBudget()
    {
        while (cachedBytes > maxBytes && frames.Count > 1)
        {
            var evictionIndex = FindOldestEvictableFrameIndex();
            if (evictionIndex < 0)
            {
                break;
            }

            cachedBytes -= frames[evictionIndex].Payload.Length;
            frames.RemoveAt(evictionIndex);
            droppedBudgetFrameCount++;
        }
    }

    private int FindOldestEvictableFrameIndex()
    {
        var oldestIndex = -1;
        for (var index = 0; index < frames.Count; index++)
        {
            if (ReferenceEquals(frames[index], latest))
            {
                continue;
            }

            if (oldestIndex < 0 || IsOlder(frames[index], frames[oldestIndex]))
            {
                oldestIndex = index;
            }
        }

        return oldestIndex;
    }

    private static bool IsOlder(FrameSample candidate, FrameSample current)
    {
        return candidate.SourceEpoch < current.SourceEpoch
            || (candidate.SourceEpoch == current.SourceEpoch
                && candidate.SourceTimestampTicks < current.SourceTimestampTicks);
    }
}
