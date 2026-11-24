using Log.Core.Timeline;

namespace Log.Core.Capture;

public sealed class HighlightInbox
{
    private const int DefaultMaxPendingEvents = 1024;
    private readonly Queue<HighlightEvent> events = new();
    private readonly object gate = new();
    private readonly int maxPendingEvents;
    private int inFlightCount;
    private bool accepting = true;

    public HighlightInbox(int maxPendingEvents = DefaultMaxPendingEvents)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingEvents);
        this.maxPendingEvents = maxPendingEvents;
    }

    public bool TryEnqueue(HighlightEvent highlight, CaptureSafetyGate safetyGate)
    {
        return TryEnqueue(highlight, safetyGate, stopController: null);
    }

    public bool TryEnqueue(
        HighlightEvent highlight,
        CaptureSafetyGate safetyGate,
        CaptureStopController? stopController)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        ArgumentNullException.ThrowIfNull(safetyGate);
        return safetyGate.TryRunWhileRecording(() =>
        {
            lock (gate)
            {
                if (!accepting
                    || stopController?.IsStopRequested == true)
                {
                    return false;
                }

                if (events.Count + inFlightCount >= maxPendingEvents)
                {
                    return false;
                }

                events.Enqueue(highlight);
                return true;
            }
        });
    }

    internal void StopAccepting()
    {
        lock (gate)
        {
            accepting = false;
        }
    }

    internal void BeginAccepting()
    {
        lock (gate)
        {
            if (accepting)
            {
                return;
            }

            if (events.Count != 0 || inFlightCount != 0)
            {
                throw new InvalidOperationException(
                    "The highlight inbox contains undelivered events from a previous capture.");
            }

            accepting = true;
        }
    }

    internal IReadOnlyList<HighlightEvent> Drain()
    {
        lock (gate)
        {
            var drained = new List<HighlightEvent>();
            while (events.Count != 0)
            {
                drained.Add(events.Dequeue());
            }

            inFlightCount = checked(inFlightCount + drained.Count);
            return drained;
        }
    }

    internal void Complete(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, inFlightCount);
            inFlightCount -= count;
        }
    }

    internal void RequeueFront(IReadOnlyList<HighlightEvent> highlights)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        if (highlights.Count == 0)
        {
            return;
        }

        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(highlights.Count, inFlightCount);
            inFlightCount -= highlights.Count;
            var alreadyQueued = events.ToArray();
            events.Clear();
            foreach (var highlight in highlights)
            {
                events.Enqueue(highlight);
            }

            foreach (var highlight in alreadyQueued)
            {
                events.Enqueue(highlight);
            }
        }
    }
}
