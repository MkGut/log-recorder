using Log.Core.Render;
using Log.Core.Timeline;

namespace Log.Core.Capture;

public sealed record CaptureRunOptions
{
    /// <summary>
    /// Creates capture options. Bounded runs use fixed intervals; an indefinite
    /// run uses normal or random adaptive sampling.
    /// </summary>
    public CaptureRunOptions(
        long durationTicks,
        long intervalTicks,
        long maxFrameAgeTicks,
        string sourceMode = "screen",
        long estimatedBytesPerHour = 0,
        long reserveBytes = 0,
        CaptureSamplingMode? samplingMode = null,
        long? randomSeed = null)
    {
        // Zero means the user-controlled indefinite mode. A positive value
        // keeps the bounded mode used by deterministic tests and automation.
        ArgumentOutOfRangeException.ThrowIfNegative(durationTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrameAgeTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytesPerHour);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        var slotCount = durationTicks == 0
            ? 0
            : checked((durationTicks - 1) / intervalTicks + 1);
        if (slotCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationTicks),
                "The duration and interval would produce more capture slots than the engine can index.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMode);
        if (sourceMode != "screen")
        {
            throw new ArgumentException("New captures support screen mode only.", nameof(sourceMode));
        }
        var resolvedSamplingMode = samplingMode
            ?? (durationTicks == 0 ? CaptureSamplingMode.Adaptive : CaptureSamplingMode.Fixed);
        if (!Enum.IsDefined(resolvedSamplingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(samplingMode));
        }

        if (durationTicks == 0 && resolvedSamplingMode == CaptureSamplingMode.Fixed)
        {
            throw new ArgumentException(
                "An indefinite run must use adaptive or random sampling.",
                nameof(samplingMode));
        }

        if (durationTicks > 0 && resolvedSamplingMode != CaptureSamplingMode.Fixed)
        {
            throw new ArgumentException(
                "Bounded runs must use fixed sampling.",
                nameof(samplingMode));
        }

        if (resolvedSamplingMode != CaptureSamplingMode.Random && randomSeed is not null)
        {
            throw new ArgumentException(
                "A random seed is only valid for random adaptive sampling.",
                nameof(randomSeed));
        }

        DurationTicks = durationTicks;
        IntervalTicks = intervalTicks;
        // Normal adaptive mode grows to one target per 60s (ADR-0009) and holds
        // its last frame so coarse targets do not age into black gaps. Random
        // adaptive mode keeps the requested freshness ceiling because it samples
        // at the normal source cadence and treats stale targets as real gaps.
        MaxFrameAgeTicks = resolvedSamplingMode == CaptureSamplingMode.Adaptive
            ? long.MaxValue
            : maxFrameAgeTicks;
        SourceMode = sourceMode;
        EstimatedBytesPerHour = estimatedBytesPerHour;
        ReserveBytes = reserveBytes;
        SamplingMode = resolvedSamplingMode;
        RandomSeed = resolvedSamplingMode == CaptureSamplingMode.Random
            ? randomSeed ?? RandomAdaptiveSamplingPolicy.CreateSeed()
            : null;
    }

    public static CaptureRunOptions FromTimeScale(
        long durationTicks,
        TimeScaleSettings timeScale,
        long maxFrameAgeTicks,
        string sourceMode = "screen",
        long estimatedBytesPerHour = 0,
        long reserveBytes = 0,
        CaptureSamplingMode? samplingMode = null,
        long? randomSeed = null)
    {
        ArgumentNullException.ThrowIfNull(timeScale);
        return new CaptureRunOptions(
            durationTicks,
            timeScale.CaptureIntervalTicks,
            maxFrameAgeTicks,
            sourceMode,
            estimatedBytesPerHour,
            reserveBytes,
            samplingMode,
            randomSeed);
    }

    public long DurationTicks { get; }
    public bool IsIndefinite => DurationTicks == 0;
    public long IntervalTicks { get; }

    /// <summary>
    /// How stale a selected frame may be before a slot becomes a gap. For an
    /// normal adaptive run this is normalized to <see cref="long.MaxValue"/> (hold
    /// the last frame forever). Random adaptive runs retain the requested ceiling
    /// so stale scheduled targets remain explicit gaps.
    /// </summary>
    public long MaxFrameAgeTicks { get; }
    public string SourceMode { get; }
    public long EstimatedBytesPerHour { get; }
    public long ReserveBytes { get; }
    public CaptureSamplingMode SamplingMode { get; }
    public bool IsFixedSampling => SamplingMode == CaptureSamplingMode.Fixed;
    public bool IsAdaptiveSampling => SamplingMode == CaptureSamplingMode.Adaptive;
    public bool IsRandomSampling => SamplingMode == CaptureSamplingMode.Random;
    public long? RandomSeed { get; }
}

public sealed record CaptureRunResult(
    string SessionId,
    long StartTicks,
    long StopTicks,
    int SlotCount,
    int GapCount,
    int DuplicateCount);
