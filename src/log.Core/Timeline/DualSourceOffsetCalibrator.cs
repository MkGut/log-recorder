namespace Log.Core.Timeline;

public readonly record struct OffsetCalibrationSample
{
    public OffsetCalibrationSample(long screenTicks, long cameraTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(screenTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(cameraTicks);
        ScreenTicks = screenTicks;
        CameraTicks = cameraTicks;
    }

    public long ScreenTicks { get; }
    public long CameraTicks { get; }
}

public static class DualSourceOffsetCalibrator
{
    public static long EstimateOffset(IEnumerable<OffsetCalibrationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var offsets = samples
            .Select(static sample => checked(sample.ScreenTicks - sample.CameraTicks))
            .OrderBy(static offset => offset)
            .ToArray();
        if (offsets.Length < 3)
        {
            throw new ArgumentException("At least three paired samples are required for calibration.", nameof(samples));
        }

        var middle = offsets.Length / 2;
        return offsets.Length % 2 == 1
            ? offsets[middle]
            : checked(offsets[middle - 1] + (offsets[middle] - offsets[middle - 1]) / 2);
    }

    public static long AlignCameraTicks(long cameraTicks, long offsetTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cameraTicks);
        return checked(cameraTicks + offsetTicks);
    }
}
