using Log.Core.Capture;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class HighlightInboxTests
{
    [Fact]
    public void PendingCapacityIncludesInFlightHighlightsAndPreservesRequeues()
    {
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        var inbox = new HighlightInbox(maxPendingEvents: 2);
        var first = new HighlightEvent("first", 0, HighlightSource.Screen, 10);
        var second = new HighlightEvent("second", 1, HighlightSource.Screen, 10);
        var third = new HighlightEvent("third", 2, HighlightSource.Screen, 10);

        Assert.True(inbox.TryEnqueue(first, gate));
        Assert.True(inbox.TryEnqueue(second, gate));
        Assert.False(inbox.TryEnqueue(third, gate));

        Assert.Equal([first, second], inbox.Drain());
        Assert.False(inbox.TryEnqueue(third, gate));

        inbox.Complete(1);
        Assert.True(inbox.TryEnqueue(third, gate));
        inbox.RequeueFront([second]);

        Assert.Equal([second, third], inbox.Drain());
        inbox.Complete(2);
    }

    [Fact]
    public void RejectsNonPositivePendingCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighlightInbox(0));
    }
}
