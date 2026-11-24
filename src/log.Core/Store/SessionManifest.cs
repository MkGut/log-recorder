using System.Text.Json.Serialization;
using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Timeline;

namespace Log.Core.Store;

public enum SessionStatus
{
    Starting,
    Recording,
    Finalizing,
    Ready,
    Interrupted,
    Rendering,
    Exported,
    Deleting
}

// Settable (not init) so source-generated JSON keeps the initializer defaults for absent properties.
public sealed class SessionManifest
{
    public const string FixedSamplingMode = "fixed";
    public const string AdaptiveSamplingMode = "adaptive";
    public const string RandomSamplingMode = "random";

    public int SchemaVersion { get; set; } = 1;
    public required string Id { get; set; }
    public string? GroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public required string SourceMode { get; set; }
    public string Clock { get; set; } = "qpc-100ns";
    public long StartedAtTicks { get; set; }
    public long? StoppedAtTicks { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? StoppedAtUtc { get; set; }
    public long CaptureIntervalTicks { get; set; }
    public string SamplingMode { get; set; } = FixedSamplingMode;
    public RandomSamplingSchedule? RandomSampling { get; set; }
    public long CameraOffsetTicks { get; set; }
    public SessionStatus Status { get; set; }
    public List<PauseRange> Pauses { get; set; } = [];
    public Dictionary<string, string> Tracks { get; set; } = new(StringComparer.Ordinal);
    public List<ManifestSlot> Slots { get; set; } = [];
    public List<ManifestTrackSlot> TrackSlots { get; set; } = [];
    public List<SessionSegment> Segments { get; set; } = [];
    public List<HighlightEvent> Highlights { get; set; } = [];
    public List<string> PrunedTracks { get; set; } = [];
    public CaptureMetricsSnapshot? Metrics { get; set; }
    public ExportSettingsSnapshot? LastExportSettings { get; set; }

    // The asset naming check keeps manifests written before SamplingMode was
    // added renderable without rebuilding a dense, incorrect timeline.
    [JsonIgnore]
    public bool UsesAdaptiveSampling =>
        SamplingMode == AdaptiveSamplingMode
        || Slots.Any(static slot => IsAdaptiveAssetReference(slot?.AssetReference))
        || TrackSlots.Any(static slot => IsAdaptiveAssetReference(slot?.AssetReference));

    [JsonIgnore]
    public bool UsesRandomSampling =>
        SamplingMode == RandomSamplingMode || RandomSampling is not null;

    private static bool IsAdaptiveAssetReference(string? assetReference) =>
        assetReference?.StartsWith("screen/slot-a-", StringComparison.Ordinal) == true
        || assetReference?.StartsWith("camera/slot-a-", StringComparison.Ordinal) == true;
}

/// <summary>
/// JSON-safe, per-session copy of the last successful render choices. Raw
/// capture assets remain the source of truth for every later re-export.
/// </summary>
public sealed record ExportSettingsSnapshot(
    int OutputWidth,
    int OutputHeight,
    int OutputFramesPerSecond,
    CompositionMode Mode,
    bool PipVisible,
    double PipWidthFraction,
    double PipInsetFraction,
    PipCorner PipCorner,
    CameraMirror CameraMirror,
    bool TimerEnabled,
    double TimerOpacity,
    bool IncludePausedTime,
    int? CameraSourceWidth,
    int? CameraSourceHeight,
    bool WatermarkEnabled = false)
{
    public static ExportSettingsSnapshot FromRenderSettings(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var composition = settings.Composition;
        var cameraSource = composition.CameraSourceSize;
        return new ExportSettingsSnapshot(
            composition.OutputSize.Width,
            composition.OutputSize.Height,
            composition.OutputFramesPerSecond,
            composition.Mode,
            composition.Pip.Visible,
            composition.Pip.WidthFraction,
            composition.Pip.InsetFraction,
            composition.Pip.Corner,
            composition.Pip.Mirror,
            settings.Timer.Enabled,
            settings.Timer.Opacity,
            settings.Timer.IncludePausedTime,
            cameraSource?.Width,
            cameraSource?.Height,
            settings.WatermarkEnabled);
    }

    public RenderSettings ToRenderSettings()
    {
        if (OutputFramesPerSecond is < 1 or > 240)
        {
            throw new InvalidDataException("Saved export frame rate must be between 1 and 240 fps.");
        }

        if (!Enum.IsDefined(Mode)
            || !Enum.IsDefined(PipCorner)
            || !Enum.IsDefined(CameraMirror))
        {
            throw new InvalidDataException("Saved export composition contains an unknown enum value.");
        }

        PixelSize? cameraSource = CameraSourceWidth is int width
            && CameraSourceHeight is int height
            ? new PixelSize(width, height)
            : null;
        if ((CameraSourceWidth is null) != (CameraSourceHeight is null))
        {
            throw new InvalidDataException("The saved camera source size is incomplete.");
        }

        var composition = new CompositionSettings(
            new PixelSize(OutputWidth, OutputHeight),
            new PipLayout(
                PipVisible,
                PipWidthFraction,
                PipInsetFraction,
                PipCorner,
                CameraMirror),
            OutputFramesPerSecond)
        {
            Mode = Mode,
            CameraSourceSize = cameraSource
        };
        return new RenderSettings(
            composition,
            new TimerSettings(TimerEnabled, TimerOpacity, IncludePausedTime),
            WatermarkEnabled);
    }
}

public sealed record ManifestSlot(
    int Index,
    long TargetTicks,
    long SourceTicks,
    bool IsDuplicate,
    long AgeTicks,
    string AssetReference)
{
    public long? ArrivalTicks { get; init; }
    public string TimestampQuality { get; set; } = "unknown";
    public int SourceEpoch { get; init; }
}

public sealed record ManifestTrackSlot(
    string Track,
    int Index,
    long TargetTicks,
    long SourceTicks,
    bool IsDuplicate,
    long AgeTicks,
    string AssetReference)
{
    public long? ArrivalTicks { get; init; }
    public string TimestampQuality { get; set; } = "unknown";
    public int SourceEpoch { get; init; }
}
