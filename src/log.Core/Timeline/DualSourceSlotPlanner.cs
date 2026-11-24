namespace Log.Core.Timeline;

public sealed record DualSourceSlot(
    TimelineSlot Slot,
    SlotCapture Screen,
    SlotCapture Camera);

public static class DualSourceSlotPlanner
{
    public static IReadOnlyList<DualSourceSlot> Combine(
        IReadOnlyList<SlotCapture> screen,
        IReadOnlyList<SlotCapture> camera)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(camera);
        if (screen.Count != camera.Count)
        {
            throw new ArgumentException("Screen and camera tracks must have the same slot count.", nameof(camera));
        }

        var combined = new DualSourceSlot[screen.Count];
        for (var index = 0; index < screen.Count; index++)
        {
            if (screen[index].Slot != camera[index].Slot)
            {
                throw new ArgumentException("Screen and camera tracks must share the same authoritative slots.", nameof(camera));
            }

            combined[index] = new DualSourceSlot(screen[index].Slot, screen[index], camera[index]);
        }

        return combined;
    }
}
