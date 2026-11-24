using Log.Core.Timeline;

namespace Log.Core.Render;

/// <summary>
/// Fixed-interval time scaling for bounded runs and deterministic callers.
/// Open-ended shell capture uses <see cref="AdaptiveSamplingPolicy"/> instead;
/// its effective speed is intentionally determined at export from retained
/// semantic slots and the selected output frame rate.
/// </summary>
public sealed record TimeScaleSettings
{
    public TimeScaleSettings(
        double speedMultiplier = 30,
        int outputFramesPerSecond = 30,
        long? captureIntervalTicksOverride = null)
    {
        TimeScaleCalculator.ValidateSpeedMultiplier(speedMultiplier);
        TimeScaleCalculator.ValidateOutputFramesPerSecond(outputFramesPerSecond);
        if (captureIntervalTicksOverride is long intervalTicks)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);
        }

        SpeedMultiplier = speedMultiplier;
        OutputFramesPerSecond = outputFramesPerSecond;
        CaptureIntervalTicks = captureIntervalTicksOverride
            ?? TimeScaleCalculator.DeriveCaptureIntervalTicks(speedMultiplier, outputFramesPerSecond);
        UsesAdvancedIntervalOverride = captureIntervalTicksOverride is not null;
    }

    public double SpeedMultiplier { get; }
    public int OutputFramesPerSecond { get; }
    public long CaptureIntervalTicks { get; }
    public bool UsesAdvancedIntervalOverride { get; }

    public double EffectiveSpeedMultiplier => TimeScaleCalculator.CalculateSpeedMultiplier(
        CaptureIntervalTicks,
        OutputFramesPerSecond);
}

public static class TimeScaleCalculator
{
    public static long DeriveCaptureIntervalTicks(
        double speedMultiplier,
        int outputFramesPerSecond)
    {
        ValidateSpeedMultiplier(speedMultiplier);
        ValidateOutputFramesPerSecond(outputFramesPerSecond);
        var interval = speedMultiplier * TimelineConstants.TicksPerSecond / outputFramesPerSecond;
        if (interval > long.MaxValue)
        {
            throw new OverflowException("The requested speed produces an interval outside the QPC range.");
        }

        return Math.Max(1, checked((long)Math.Round(interval, MidpointRounding.AwayFromZero)));
    }

    public static double CalculateSpeedMultiplier(
        long captureIntervalTicks,
        int outputFramesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureIntervalTicks);
        ValidateOutputFramesPerSecond(outputFramesPerSecond);
        return captureIntervalTicks * (double)outputFramesPerSecond
            / TimelineConstants.TicksPerSecond;
    }

    public static long EstimateNormalFrameCount(
        long activeDurationTicks,
        long captureIntervalTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeDurationTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureIntervalTicks);
        return activeDurationTicks == 0
            ? 0
            : checked((activeDurationTicks - 1) / captureIntervalTicks + 1);
    }

    public static double EstimateOutputDurationSeconds(
        long activeDurationTicks,
        long captureIntervalTicks,
        int outputFramesPerSecond,
        long additionalHoldTicks = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalHoldTicks);
        ValidateOutputFramesPerSecond(outputFramesPerSecond);
        var normalFrames = EstimateNormalFrameCount(activeDurationTicks, captureIntervalTicks);
        var holdFrames = additionalHoldTicks == 0
            ? 0
            : checked((additionalHoldTicks * outputFramesPerSecond
                + TimelineConstants.TicksPerSecond / 2)
                / TimelineConstants.TicksPerSecond);
        return checked((normalFrames + holdFrames) / (double)outputFramesPerSecond);
    }

    internal static void ValidateSpeedMultiplier(double speedMultiplier)
    {
        if (double.IsNaN(speedMultiplier)
            || double.IsInfinity(speedMultiplier)
            || speedMultiplier < 2
            || speedMultiplier > 600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedMultiplier),
                "Speed must be between 2x and 600x.");
        }
    }

    internal static void ValidateOutputFramesPerSecond(int outputFramesPerSecond)
    {
        if (outputFramesPerSecond is not (24 or 30 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputFramesPerSecond),
                "Output frame rate must be 24, 30, or 60 fps.");
        }
    }
}
