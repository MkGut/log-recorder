namespace Log.Core.Capture;

public interface IFrameSource
{
    IAsyncEnumerable<FrameSample> ReadFramesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional control for sources that can reduce their delivery/copy cadence while
/// a long sampled capture is running. The capture clock remains authoritative;
/// this only prevents the platform source from doing work that no slot can use.
/// </summary>
public interface IFrameSourceSamplingControl
{
    long SampleIntervalTicks { get; }

    void SetSampleInterval(long intervalTicks);
}

public enum FrameSourceCompletion
{
    Unknown,
    Completed,
    Unavailable
}

public interface IFrameSourceLifecycle
{
    FrameSourceCompletion Completion { get; }
}
