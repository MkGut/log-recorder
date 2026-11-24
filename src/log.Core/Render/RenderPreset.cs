namespace Log.Core.Render;

/// <summary>
/// The single render configuration used by the small daily recorder.
/// </summary>
public static class RenderPreset
{
    public const int OutputFramesPerSecond = 30;
    /// <summary>Every timelapse plays for about this long, however long the recording was.</summary>
    public const int TargetDurationSeconds = 15;
    /// <summary>Short recordings play at this rate (and so run shorter than the target) rather than slower.</summary>
    public const int MinimumFramesPerSecond = 7;
    public const double PictureInPictureWidthFraction = 0.28;
    public const double PictureInPictureInsetFraction = 0.04;
    public const bool IncludeElapsedTimer = true;

    public static RenderSettings CreateSettings(
        string sourceMode,
        PixelSize outputSize,
        PixelSize? cameraSourceSize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMode);

        var mode = sourceMode switch
        {
            "screen" => CompositionMode.ScreenOnly,
            "camera" => CompositionMode.CameraOnly,
            "screen+camera" => CompositionMode.PictureInPicture,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceMode),
                sourceMode,
                "Source mode must be screen, camera, or screen+camera.")
        };
        var usesPictureInPicture = sourceMode == "screen+camera";
        var composition = new CompositionSettings(
            outputSize,
            new PipLayout(
                visible: usesPictureInPicture,
                widthFraction: usesPictureInPicture ? PictureInPictureWidthFraction : 0,
                insetFraction: usesPictureInPicture ? PictureInPictureInsetFraction : 0,
                corner: PipCorner.BottomRight),
            OutputFramesPerSecond)
        {
            Mode = mode,
            CameraSourceSize = cameraSourceSize
        };

        return new RenderSettings(
            composition,
            new TimerSettings(enabled: IncludeElapsedTimer, opacity: 0.75),
            watermarkEnabled: false);
    }

    public static RenderDurationPlan EstimateDuration(SessionRenderInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var settings = CreateSettings(input.SourceMode, new PixelSize(1920, 1080));
        var plannedFrames = RenderTimelinePlanner.Plan(input, settings).Count;
        // The existing Media Foundation renderer writes one black frame for an
        // empty plan so it can still finalize a playable MP4.
        var outputFrames = Math.Max(1, plannedFrames);
        var durationTicks = MediaFoundationFrameTimingPlanner.TimestampForFrame(
            outputFrames,
            OutputFramesPerSecond);
        return new RenderDurationPlan(outputFrames, OutputFramesPerSecond, durationTicks);
    }
}

public sealed record RenderDurationPlan(
    int OutputFrameCount,
    int OutputFramesPerSecond,
    long DurationTicks)
{
    public double DurationSeconds => OutputFrameCount / (double)OutputFramesPerSecond;
}
