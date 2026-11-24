using System.Diagnostics.Tracing;

namespace Log.Core.Diagnostics;

[EventSource(Name = "log.Capture")]
public sealed class CaptureEventSource : EventSource
{
    public static readonly CaptureEventSource Instance = new();

    private CaptureEventSource()
    {
    }

    [Event(1, Level = EventLevel.Verbose)]
    public void CaptureTickScheduled(string source, long targetTicks)
    {
        WriteEvent(1, source, targetTicks);
    }

    [Event(2, Level = EventLevel.Verbose)]
    public void FrameArrived(string source, long sourceTicks)
    {
        WriteEvent(2, source, sourceTicks);
    }

    [Event(3, Level = EventLevel.Informational)]
    public void SlotRecorded(string source, int slotIndex, string state)
    {
        WriteEvent(3, source, slotIndex, state);
    }

    [Event(4, Level = EventLevel.Informational)]
    public void EncoderFinalizationStarted(string source)
    {
        WriteEvent(4, source);
    }

    [Event(5, Level = EventLevel.Informational)]
    public void EncoderFinalizationCompleted(string source)
    {
        WriteEvent(5, source);
    }

    [Event(6, Level = EventLevel.Warning)]
    public void FrameDropped(string source, string reason)
    {
        WriteEvent(6, source, reason);
    }

    [Event(7, Level = EventLevel.Warning)]
    public void SourceUnavailable(string source, string reason)
    {
        WriteEvent(7, source, reason);
    }

    [Event(8, Level = EventLevel.Warning)]
    public void FrameTimestampOutOfOrder(string source, int sourceEpoch)
    {
        WriteEvent(8, source, sourceEpoch);
    }

    [Event(9, Level = EventLevel.Verbose)]
    public void EncoderWriteStarted(string source, int slotIndex)
    {
        WriteEvent(9, source, slotIndex);
    }

    [Event(10, Level = EventLevel.Verbose)]
    public void EncoderWriteCompleted(string source, int slotIndex, long elapsedStopwatchTicks)
    {
        WriteEvent(10, source, slotIndex, elapsedStopwatchTicks);
    }

    [Event(11, Level = EventLevel.Warning)]
    public void EncoderWriteFailed(string source, int slotIndex)
    {
        WriteEvent(11, source, slotIndex);
    }

    [Event(12, Level = EventLevel.Error)]
    public void SourceShutdownTimedOut(string source)
    {
        WriteEvent(12, source);
    }

    [Event(13, Level = EventLevel.Verbose)]
    public void DiskWriteStarted(string source, int slotIndex)
    {
        WriteEvent(13, source, slotIndex);
    }

    [Event(14, Level = EventLevel.Verbose)]
    public void DiskWriteCompleted(
        string source,
        int slotIndex,
        long elapsedStopwatchTicks,
        long byteLength)
    {
        WriteEvent(14, source, slotIndex, elapsedStopwatchTicks, byteLength);
    }

    [Event(15, Level = EventLevel.Warning)]
    public void DiskWriteFailed(string source, int slotIndex)
    {
        WriteEvent(15, source, slotIndex);
    }

    [Event(16, Level = EventLevel.Warning)]
    public void StateObserverFailed()
    {
        WriteEvent(16);
    }

    [Event(17, Level = EventLevel.Verbose)]
    public void FrameCopied(string source, int width, int height, long sourceTicks)
    {
        WriteEvent(17, source, width, height, sourceTicks);
    }

    [Event(18, Level = EventLevel.Verbose)]
    public void FrameDisposed(string source)
    {
        WriteEvent(18, source);
    }

    [Event(19, Level = EventLevel.Warning)]
    public void FrameDisposeFailed(string source)
    {
        WriteEvent(19, source);
    }

    [Event(20, Level = EventLevel.Verbose)]
    public void CaptureTickCompleted(string source, int slotIndex, long servicedTicks)
    {
        WriteEvent(20, source, slotIndex, servicedTicks);
    }

    [Event(21, Level = EventLevel.Informational)]
    public void SourceEpochStarted(string source, int sourceEpoch)
    {
        WriteEvent(21, source, sourceEpoch);
    }

    [Event(22, Level = EventLevel.Informational)]
    public void SegmentFinalizationStarted(string source, int segmentIndex)
    {
        WriteEvent(22, source, segmentIndex);
    }

    [Event(23, Level = EventLevel.Informational)]
    public void SegmentFinalized(string source, int segmentIndex, long byteLength)
    {
        WriteEvent(23, source, segmentIndex, byteLength);
    }

    [Event(24, Level = EventLevel.Informational)]
    public void SegmentIndexed(string source, int segmentIndex)
    {
        WriteEvent(24, source, segmentIndex);
    }

    [Event(25, Level = EventLevel.Warning)]
    public void SegmentFinalizationFailed(string source, int segmentIndex)
    {
        WriteEvent(25, source, segmentIndex);
    }

    [Event(26, Level = EventLevel.Informational)]
    public void EncoderBackendRequested(string source, string backend)
    {
        WriteEvent(26, source, backend);
    }

    [Event(27, Level = EventLevel.Warning)]
    public void EncoderSoftwareFallback(string source, int hresult)
    {
        WriteEvent(27, source, hresult);
    }

    [Event(28, Level = EventLevel.Informational)]
    public void EncoderBackendSelected(string source, string backend)
    {
        WriteEvent(28, source, backend);
    }

    [Event(29, Level = EventLevel.Informational)]
    public void ExportStarted(string sessionId, int framesPerSecond, string composition)
    {
        WriteEvent(29, sessionId, framesPerSecond, composition);
    }

    [Event(30, Level = EventLevel.Informational)]
    public void ExportFinished(string sessionId, string status, long bytesFreed)
    {
        WriteEvent(30, sessionId, status, bytesFreed);
    }

    [Event(31, Level = EventLevel.Informational)]
    public void RecoveryFinished(string sessionId, string status)
    {
        WriteEvent(31, sessionId, status);
    }

    [Event(32, Level = EventLevel.Error)]
    public void StorageProbeFailed(string exceptionType, int hresult)
    {
        WriteEvent(32, exceptionType, hresult);
    }
}
