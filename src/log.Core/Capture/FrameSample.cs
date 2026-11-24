namespace Log.Core.Capture;

public enum FrameTimestampQuality
{
    Unknown,
    SourceProvided,
    ArrivalFallback
}

public sealed record FrameSample
{
    public FrameSample(
        long sourceTimestampTicks,
        ReadOnlyMemory<byte> payload,
        int width,
        int height,
        long? arrivalTimestampTicks = null,
        FrameTimestampQuality timestampQuality = FrameTimestampQuality.Unknown,
        int sourceEpoch = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceTimestampTicks);
        if (arrivalTimestampTicks is long arrivalTicks)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(arrivalTicks);
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceEpoch);
        SourceTimestampTicks = sourceTimestampTicks;
        Payload = payload;
        Width = width;
        Height = height;
        ArrivalTimestampTicks = arrivalTimestampTicks;
        TimestampQuality = timestampQuality;
        SourceEpoch = sourceEpoch;
    }

    public long SourceTimestampTicks { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public int Width { get; }
    public int Height { get; }
    public long? ArrivalTimestampTicks { get; }
    public FrameTimestampQuality TimestampQuality { get; }
    public int SourceEpoch { get; }
}
