using Log.Core.Timeline;

namespace Log.Core.Render;

public readonly record struct MediaFoundationFrameTiming(
    long PresentationTicks,
    long DurationTicks);

public static class MediaFoundationFrameTimingPlanner
{
    public static IReadOnlyList<MediaFoundationFrameTiming> Plan(
        int frameCount,
        int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);

        var timings = new MediaFoundationFrameTiming[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var presentation = TimestampForFrame(index, framesPerSecond);
            var nextPresentation = TimestampForFrame(index + 1, framesPerSecond);
            timings[index] = new MediaFoundationFrameTiming(
                presentation,
                checked(nextPresentation - presentation));
        }

        return timings;
    }

    public static long TimestampForFrame(int frameIndex, int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);

        var wholeSeconds = frameIndex / framesPerSecond;
        var remainder = frameIndex % framesPerSecond;
        return checked(
            wholeSeconds * TimelineConstants.TicksPerSecond
            + remainder * TimelineConstants.TicksPerSecond / framesPerSecond);
    }
}
