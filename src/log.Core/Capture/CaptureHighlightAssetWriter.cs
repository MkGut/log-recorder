using Log.Core.Timeline;

namespace Log.Core.Capture;

/// <summary>
/// Resolves highlight events to the nearest captured frame without changing their timeline timestamp.
/// </summary>
internal static class CaptureHighlightAssetWriter
{
    public static async ValueTask<HighlightEvent> AttachAsync(
        string sessionDirectory,
        HighlightEvent highlight,
        FrameCache? screenCache,
        FrameCache? cameraCache,
        long maxFrameAgeTicks,
        long cameraOffsetTicks,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(highlight);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrameAgeTicks);

        string? screenAssetReference = highlight.ScreenAssetReference;
        string? cameraAssetReference = highlight.CameraAssetReference;
        if (highlight.Source is HighlightSource.Screen or HighlightSource.Both
            && screenAssetReference is null
            && screenCache is not null)
        {
            var selection = Select(screenCache, highlight.EventTicks, maxFrameAgeTicks, 0);
            if (selection?.Sample is { } frame
                && CaptureFrameAssetWriter.CanWriteBgraFrame(frame))
            {
                screenAssetReference = CaptureFrameAssetWriter.CreateHighlightAssetReference(
                    highlight.Id,
                    "screen");
                await CaptureFrameAssetWriter.WriteHighlightAsync(
                    sessionDirectory,
                    screenAssetReference,
                    frame,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (highlight.Source is HighlightSource.Camera or HighlightSource.Both
            && cameraAssetReference is null
            && cameraCache is not null)
        {
            var selection = Select(
                cameraCache,
                highlight.EventTicks,
                maxFrameAgeTicks,
                cameraOffsetTicks);
            if (selection?.Sample is { } frame
                && CaptureFrameAssetWriter.CanWriteBgraFrame(frame))
            {
                cameraAssetReference = CaptureFrameAssetWriter.CreateHighlightAssetReference(
                    highlight.Id,
                    "camera");
                await CaptureFrameAssetWriter.WriteHighlightAsync(
                    sessionDirectory,
                    cameraAssetReference,
                    frame,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (screenAssetReference == highlight.ScreenAssetReference
            && cameraAssetReference == highlight.CameraAssetReference)
        {
            return highlight;
        }

        return new HighlightEvent(
            highlight.Id,
            highlight.EventTicks,
            highlight.Source,
            highlight.HoldTicks,
            highlight.Included,
            screenAssetReference,
            cameraAssetReference);
    }

    private static (FrameSample Sample, bool IsDuplicate, long AgeTicks)? Select(
        FrameCache cache,
        long targetTicks,
        long maxFrameAgeTicks,
        long timestampOffsetTicks)
    {
        return FrameSelector.SelectLatestAtOrBefore(
            cache.SnapshotAll(),
            targetTicks,
            maxFrameAgeTicks,
            timestampOffsetTicks,
            cache.CurrentSourceEpoch);
    }
}
