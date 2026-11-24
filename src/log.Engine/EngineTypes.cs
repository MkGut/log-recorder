using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;

namespace Log.Engine;

/// <summary>High-level phase the engine is in, for the overt recording indicator.</summary>
public enum EngineActivity
{
    Idle,
    Recording,
    Paused,
    Stopping,
    Exporting
}

/// <summary>Immutable view of the engine's current state, published on every transition.</summary>
public sealed record EngineStateSnapshot(
    EngineActivity Activity,
    CaptureSafetyState? SafetyState,
    CapturePauseReason? PauseReason,
    string? SessionId);

/// <summary>What the shell asks the engine to record. Wall durations, converted to clock ticks internally.</summary>
public sealed record CaptureRequest
{
    public CaptureRequest(
        TimeSpan maxDuration,
        TimeSpan interval,
        TimeSpan maxFrameAge,
        int outputWidth = 1920,
        int outputHeight = 1080,
        int outputFramesPerSecond = 30,
        string? sessionName = null,
        long estimatedBytesPerHour = 0,
        long reserveBytes = 0,
        bool captureCursor = true,
        CaptureSamplingMode? samplingMode = null,
        long? randomSeed = null)
    {
        // Zero means the user controls the session lifetime and stops it from
        // the shell. Positive values remain supported for bounded tests and
        // supervised runs.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputFramesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytesPerHour);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        if (samplingMode is CaptureSamplingMode invalidSamplingMode
            && !Enum.IsDefined(invalidSamplingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(samplingMode));
        }

        if (maxDuration == TimeSpan.Zero && samplingMode == CaptureSamplingMode.Fixed)
        {
            throw new ArgumentException(
                "An indefinite capture must use adaptive or random sampling.",
                nameof(samplingMode));
        }

        if (maxDuration > TimeSpan.Zero && samplingMode is not null and not CaptureSamplingMode.Fixed)
        {
            throw new ArgumentException(
                "Bounded captures must use fixed sampling.",
                nameof(samplingMode));
        }

        if (samplingMode != CaptureSamplingMode.Random && randomSeed is not null)
        {
            throw new ArgumentException(
                "A random seed is only valid for random adaptive sampling.",
                nameof(randomSeed));
        }

        MaxDuration = maxDuration;
        Interval = interval;
        MaxFrameAge = maxFrameAge;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        OutputFramesPerSecond = outputFramesPerSecond;
        SessionName = sessionName;
        EstimatedBytesPerHour = estimatedBytesPerHour;
        ReserveBytes = reserveBytes;
        CaptureCursor = captureCursor;
        SamplingMode = samplingMode;
        RandomSeed = randomSeed;
    }

    /// <summary>Maximum duration, or zero for an explicit user stop.</summary>
    public TimeSpan MaxDuration { get; }
    public TimeSpan Interval { get; }
    public TimeSpan MaxFrameAge { get; }

    /// <summary>Screen-track output dimensions.</summary>
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int OutputFramesPerSecond { get; }
    public string? SessionName { get; }

    /// <summary>
    /// Estimated on-disk bytes per hour of capture, for the pre-start storage guard.
    /// Zero (the default) disables the estimate-based check.
    /// </summary>
    public long EstimatedBytesPerHour { get; }

    /// <summary>Bytes of free-space headroom the storage guard must preserve. Zero disables the reserve.</summary>
    public long ReserveBytes { get; }

    /// <summary>
    /// When true, the screen track composites the mouse cursor into each captured frame.
    /// Disabling it keeps the OS hardware cursor overlay, which avoids the compositor-bound
    /// cursor lag that cursor capture causes (most visibly on integrated GPUs).
    /// </summary>
    public bool CaptureCursor { get; }

    /// <summary>
    /// Optional sampling override. A bounded request defaults to fixed sampling;
    /// an indefinite request defaults to normal adaptive sampling.
    /// </summary>
    public CaptureSamplingMode? SamplingMode { get; }

    /// <summary>Seed for random adaptive sampling; null lets the core generate one.</summary>
    public long? RandomSeed { get; }

}

/// <summary>What the shell asks the engine to export a recorded session to.</summary>
public sealed record EngineExportRequest(
    RenderSettings RenderSettings,
    string OutputDirectory,
    string? FileName = null,
    long EstimatedOutputBytes = 0,
    long ReserveBytes = 0);

/// <summary>Outcome of an export: the produced file and whether the render job completed.</summary>
public sealed record EngineExportResult(
    string SessionId,
    string OutputPath,
    bool Completed);

/// <summary>App-facing view of a preview render job and its durable paths.</summary>
public sealed record EngineExportPreview(
    string SessionId,
    string JobId,
    string? PreviewPath,
    string SuggestedOutputPath,
    bool Completed,
    DateTimeOffset? FinishedAtUtc = null);

/// <summary>
/// A recording on disk that was never saved: a preview the user kept for later, or a session that
/// stopped (or was killed) without one. <see cref="PreviewPath"/> is null when no preview exists yet.
/// </summary>
public sealed record EngineUnfinishedSave(
    string SessionId,
    string Name,
    TimeSpan Elapsed,
    bool WasInterrupted,
    int FrameWidth,
    int FrameHeight,
    string? JobId = null,
    string? PreviewPath = null,
    string? SuggestedOutputPath = null);

/// <summary>
/// Granted screen input returned by the consent broker once the overt indicator is verified.
/// </summary>
public sealed record CaptureConsent(
    IFrameSource Source,
    bool IndicatorsVerified);

/// <summary>
/// A first-class highlight the shell asks the engine to persist for the active
/// recording (hotkey). The event is timestamped at the moment the engine
/// receives it; <see cref="Hold"/> is how long it holds on screen at render.
/// A non-positive hold records a marker with no on-screen hold.
/// </summary>
public sealed record HighlightRequest(
    TimeSpan Hold,
    HighlightSource Source = HighlightSource.Screen,
    bool Included = true);

/// <summary>
/// What the platform encoder factory receives to construct a capture-time encoder.
/// Carries the <em>live</em> <see cref="SessionStore"/> so a segmented encoder can
/// append crash-safe rolling finalized segments (S8) to the same session the
/// capture coordinator is writing.
/// </summary>
public sealed record EncoderContext(
    SessionStore Store,
    string Track,
    int Width,
    int Height,
    int FramesPerSecond);
