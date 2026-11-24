namespace Log.Core.Capture;

public interface IEncoder
{
    ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default);

    ValueTask FinalizeAsync(CancellationToken cancellationToken = default);
}

public sealed record EncodedFrame(
    int SlotIndex,
    long TargetTimestampTicks,
    FrameSample? Sample,
    bool IsDuplicate,
    long AgeTicks);
