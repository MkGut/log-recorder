using Log.Core.Capture;

namespace Log.Core.Timeline;

public sealed record SlotCapture(
    TimelineSlot Slot,
    FrameSample? Frame,
    bool IsGap,
    bool IsDuplicate,
    long AgeTicks);

public static class SlotCapturePlanner
{
    public static IReadOnlyList<SlotCapture> Plan(
        IReadOnlyList<TimelineSlot> slots,
        IEnumerable<FrameSample> frames,
        long maxAgeTicks)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAgeTicks);

        var orderedFrames = frames
            .OrderBy(static frame => frame.SourceTimestampTicks)
            .ToArray();
        var captures = new SlotCapture[slots.Count];
        FrameSample? lastFrame = null;

        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var selected = FrameSelector.SelectLatestAtOrBefore(
                orderedFrames,
                slot.WallTargetTicks,
                maxAgeTicks);

            if (selected is not null)
            {
                lastFrame = selected.Value.Sample;
                captures[index] = new SlotCapture(
                    slot,
                    selected.Value.Sample,
                    IsGap: false,
                    selected.Value.IsDuplicate,
                    selected.Value.AgeTicks);
            }
            else if (lastFrame is not null
                && slot.WallTargetTicks - lastFrame.SourceTimestampTicks <= maxAgeTicks)
            {
                var age = checked(slot.WallTargetTicks - lastFrame.SourceTimestampTicks);
                captures[index] = new SlotCapture(slot, lastFrame, IsGap: false, IsDuplicate: true, age);
            }
            else
            {
                captures[index] = new SlotCapture(slot, Frame: null, IsGap: true, IsDuplicate: false, AgeTicks: 0);
            }
        }

        return captures;
    }
}
