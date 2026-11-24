using Log.Core.Capture;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class DualSourceSlotPlannerTests
{
    [Fact]
    public void CombinesIndependentTracksOnlyWhenTheirAuthoritativeSlotsMatch()
    {
        var slots = TimelinePlanner.BuildSlots(0, 20, 10);
        var screen = slots.Select(slot => Capture(slot, 1)).ToArray();
        var camera = slots.Select(slot => Capture(slot, 2)).ToArray();

        var combined = DualSourceSlotPlanner.Combine(screen, camera);

        Assert.Equal(2, combined.Count);
        Assert.Equal(1, combined[0].Screen.Frame!.Payload.Span[0]);
        Assert.Equal(2, combined[0].Camera.Frame!.Payload.Span[0]);
    }

    [Fact]
    public void PairsIndependentTracksWhenOneSourceIsAGap()
    {
        var slots = TimelinePlanner.BuildSlots(0, 20, 10);
        var screen = slots.Select(slot => Capture(slot, 1)).ToArray();
        // Camera lost mid-session: same slot lattice, but this slot is a gap.
        var camera = slots
            .Select(slot => new SlotCapture(slot, Frame: null, IsGap: true, IsDuplicate: false, AgeTicks: 0))
            .ToArray();

        var combined = DualSourceSlotPlanner.Combine(screen, camera);

        Assert.All(combined, pair =>
        {
            Assert.False(pair.Screen.IsGap);
            Assert.True(pair.Camera.IsGap);
        });
    }

    [Fact]
    public void RejectsTracksWithDifferentSlotCounts()
    {
        var screen = TimelinePlanner.BuildSlots(0, 30, 10).Select(slot => Capture(slot, 1)).ToArray();
        var camera = TimelinePlanner.BuildSlots(0, 20, 10).Select(slot => Capture(slot, 2)).ToArray();

        Assert.Throws<ArgumentException>(() => DualSourceSlotPlanner.Combine(screen, camera));
    }

    [Fact]
    public void RejectsTracksThatDoNotShareTheAuthoritativeSlotLattice()
    {
        var screen = TimelinePlanner.BuildSlots(0, 20, 10).Select(slot => Capture(slot, 1)).ToArray();
        // Same count, different lattice (offset start) — must not be silently combined.
        var camera = TimelinePlanner.BuildSlots(5, 25, 10).Select(slot => Capture(slot, 2)).ToArray();

        Assert.Throws<ArgumentException>(() => DualSourceSlotPlanner.Combine(screen, camera));
    }

    private static SlotCapture Capture(TimelineSlot slot, byte value)
    {
        return new SlotCapture(
            slot,
            new FrameSample(slot.WallTargetTicks, new byte[] { value }, 2, 2),
            IsGap: false,
            IsDuplicate: false,
            AgeTicks: 0);
    }
}
