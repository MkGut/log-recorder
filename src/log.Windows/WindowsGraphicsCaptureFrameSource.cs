using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Log.Core.Capture;
using Log.Core.Diagnostics;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Log.Windows;

public sealed class WindowsGraphicsCaptureSourceException : Exception
{
    public WindowsGraphicsCaptureSourceException(string stage, int hresult, Exception? innerException = null)
        : base($"Windows Graphics Capture source creation failed during {stage} with HRESULT 0x{hresult:X8}.", innerException)
    {
        Stage = stage;
        HResultCode = hresult;
        HResult = hresult;
    }

    public string Stage { get; }
    public int HResultCode { get; }
}

/// <summary>
/// Adapts a picker-approved Graphics Capture item to the core frame-source seam.
/// </summary>
public sealed class WindowsGraphicsCaptureFrameSource : IFrameSource, IFrameSourceLifecycle, IFrameSourceSamplingControl, IAsyncDisposable
{
    private const int DefaultBufferCount = 3;
    private static readonly QpcClock Clock = new();

    private readonly GraphicsCaptureItem item;
    private readonly IDirect3DDevice device;
    private readonly int bufferCount;
    private long sampleIntervalTicks;
    private GraphicsCaptureSession? activeCaptureSession;
    private readonly int sourceEpoch;
    private readonly bool cursorCaptureEnabled;
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly TaskCompletionSource readCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Channel<bool>? wakeups;
    private int readerStarted;
    private int disposed;
    private int completion = (int)FrameSourceCompletion.Unknown;

    public WindowsGraphicsCaptureFrameSource(
        GraphicsCaptureItem item,
        IDirect3DDevice device,
        int bufferCount = DefaultBufferCount,
        long sampleIntervalTicks = 0,
        int sourceEpoch = 0,
        bool cursorCaptureEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIntervalTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceEpoch);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture requires Windows.");
        }

        var capabilities = WindowsCaptureCapabilities.Inspect();
        if (!capabilities.ApiAvailable)
        {
            throw new WindowsGraphicsCaptureSourceException(
                "capability probe",
                capabilities.FailureHResult ?? unchecked((int)0x8000FFFF));
        }

        if (!capabilities.IsSupported)
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this device.");
        }

        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentException("The capture item has no usable content size.", nameof(item));
        }

        this.item = item;
        this.device = device;
        this.bufferCount = bufferCount;
        this.sampleIntervalTicks = sampleIntervalTicks;
        this.sourceEpoch = sourceEpoch;
        this.cursorCaptureEnabled = cursorCaptureEnabled;
    }

    public FrameSourceCompletion Completion =>
        (FrameSourceCompletion)Volatile.Read(ref completion);

    public long SampleIntervalTicks => Volatile.Read(ref sampleIntervalTicks);

    public void SetSampleInterval(long intervalTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(intervalTicks);
        Volatile.Write(ref sampleIntervalTicks, intervalTicks);
        var session = Volatile.Read(ref activeCaptureSession);
        if (session is not null)
        {
            try
            {
                session.MinUpdateInterval = intervalTicks > 0
                    ? TimeSpan.FromTicks(intervalTicks)
                    : TimeSpan.Zero;
            }
            catch
            {
                // Some Windows builds reject changing this property mid-session;
                // the source-side timestamp filter still bounds copied frames.
            }
        }
    }

    public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref readerStarted, 1) != 0)
        {
            throw new InvalidOperationException("A Windows Graphics Capture source can only be read once.");
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            DisposeNoThrow(disposalCancellation);
            readCompletion.TrySetResult();
            ObjectDisposedException.ThrowIf(true, this);
        }

        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            disposalCancellation.Token);
        var readToken = readCancellation.Token;
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        wakeups = channel;
        if (Volatile.Read(ref disposed) != 0)
        {
            channel.Writer.TryComplete();
            DisposeNoThrow(disposalCancellation);
            readCompletion.TrySetResult();
            yield break;
        }

        var expectedWidth = item.Size.Width;
        var expectedHeight = item.Size.Height;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? captureSession = null;
        TypedEventHandler<Direct3D11CaptureFramePool, object>? frameArrived = null;
        TypedEventHandler<GraphicsCaptureItem, object>? itemClosed = null;
        var completed = false;
        long? lastEmittedSourceTicks = null;
        try
        {
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                bufferCount,
                item.Size);
            captureSession = framePool.CreateCaptureSession(item);
            Volatile.Write(ref activeCaptureSession, captureSession);
            frameArrived = (_, _) => channel.Writer.TryWrite(true);
            itemClosed = (_, _) =>
            {
                MarkUnavailableIfUnknown();
                channel.Writer.TryComplete();
            };
            framePool.FrameArrived += frameArrived;
            item.Closed += itemClosed;
            captureSession.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly;
            var initialSampleIntervalTicks = Volatile.Read(ref sampleIntervalTicks);
            if (initialSampleIntervalTicks > 0)
            {
                captureSession.MinUpdateInterval = TimeSpan.FromTicks(initialSampleIntervalTicks);
            }

            captureSession.IsCursorCaptureEnabled = cursorCaptureEnabled;
            captureSession.IsBorderRequired = true;
            captureSession.StartCapture();
            // Stop or dispose closes the session at once, so the border never waits on a reader stuck mid-copy.
            using var closeOnCancel = readToken.Register(static s => ((WindowsGraphicsCaptureFrameSource)s!).CloseSession(), this);
            CaptureEventSource.Instance.SourceEpochStarted("screen", sourceEpoch);

            while (await channel.Reader.WaitToReadAsync(readToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out _))
                {
                }

                Direct3D11CaptureFrame? newestFrame = null;
                List<Direct3D11CaptureFrame>? failedDisposals = null;
                try
                {
                    try
                    {
                        while (framePool.TryGetNextFrame() is { } nextFrame)
                        {
                            if (newestFrame is not null
                                && !TryDisposeFrame(newestFrame))
                            {
                                (failedDisposals ??= []).Add(newestFrame);
                            }

                            newestFrame = nextFrame;
                        }
                    }
                    catch (Exception exception) when (!readToken.IsCancellationRequested
                        && IsRecoverableFrameFault(exception))
                    {
                        // A lost pool/device is a source gap, not a session-wide crash.
                        MarkUnavailableIfUnknown();
                        channel.Writer.TryComplete();
                    }

                    if (newestFrame is null)
                    {
                        if (Completion == FrameSourceCompletion.Unavailable)
                        {
                            yield break;
                        }

                        continue;
                    }

                    if (Completion == FrameSourceCompletion.Unavailable)
                    {
                        yield break;
                    }

                    var contentSize = newestFrame.ContentSize;
                    if (contentSize.Width != expectedWidth
                        || contentSize.Height != expectedHeight)
                    {
                        // A fixed-dimension encoder cannot safely consume a resized or rotated target.
                        MarkUnavailableIfUnknown();
                        yield break;
                    }

                    var sourceTimestampTicks = newestFrame.SystemRelativeTime.Ticks;
                    if (sourceTimestampTicks < 0
                        || (lastEmittedSourceTicks is long lastTimestampTicks
                            && sourceTimestampTicks < lastTimestampTicks))
                    {
                        MarkUnavailableIfUnknown();
                        yield break;
                    }

                    var currentSampleIntervalTicks = Volatile.Read(ref sampleIntervalTicks);
                    if (currentSampleIntervalTicks > 0
                        && lastEmittedSourceTicks is long lastEmittedTicks
                        && IsBeforeNextSample(sourceTimestampTicks, lastEmittedTicks, currentSampleIntervalTicks))
                    {
                        continue;
                    }

                    FrameSample sample;
                    try
                    {
                        sample = await WindowsFramePixelCopy.CopySurfaceAsync(
                            newestFrame.Surface,
                            newestFrame.SystemRelativeTime.Ticks,
                            Clock.NowTicks,
                            FrameTimestampQuality.SourceProvided,
                            sourceEpoch: sourceEpoch,
                            cancellationToken: readToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!readToken.IsCancellationRequested
                        && IsRecoverableFrameFault(exception))
                    {
                        MarkUnavailableIfUnknown();
                        yield break;
                    }

                    readToken.ThrowIfCancellationRequested();

                    if (Completion == FrameSourceCompletion.Unavailable
                        || Volatile.Read(ref disposed) != 0)
                    {
                        yield break;
                    }

                    lastEmittedSourceTicks = sample.SourceTimestampTicks;
                    if (TryDisposeFrame(newestFrame))
                    {
                        newestFrame = null;
                    }

                    CaptureEventSource.Instance.FrameCopied(
                        "screen",
                        sample.Width,
                        sample.Height,
                        sample.SourceTimestampTicks);
                    yield return sample;
                }
                finally
                {
                    TryDisposeFrame(newestFrame);
                    if (failedDisposals is not null)
                    {
                        foreach (var failedFrame in failedDisposals)
                        {
                            TryDisposeFrame(failedFrame);
                        }
                    }
                }
            }
            Interlocked.CompareExchange(
                ref completion,
                (int)FrameSourceCompletion.Completed,
                (int)FrameSourceCompletion.Unknown);

            completed = true;
        }
        finally
        {
            try
            {
                if (!completed && !readToken.IsCancellationRequested)
                {
                    MarkUnavailableIfUnknown();
                }

                if (frameArrived is not null)
                {
                    try
                    {
                        framePool!.FrameArrived -= frameArrived;
                    }
                    catch
                    {
                    }
                }

                if (itemClosed is not null)
                {
                    try
                    {
                        item.Closed -= itemClosed;
                    }
                    catch
                    {
                    }
                }

                CloseSession();
                DisposeNoThrow(framePool);
                channel.Writer.TryComplete();
                wakeups = null;
                DisposeNoThrow(disposalCancellation);
            }
            finally
            {
                readCompletion.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            MarkUnavailableIfUnknown();
            CancelNoThrow(disposalCancellation);
            wakeups?.Writer.TryComplete();
            if (Volatile.Read(ref readerStarted) == 0)
            {
                DisposeNoThrow(disposalCancellation);
                readCompletion.TrySetResult();
            }
        }

        // Do not let a new capture overlap teardown of the old frame pool/session.
        return new ValueTask(readCompletion.Task);
    }

    // Closing the session ends the capture and the Windows yellow border. Safe to call twice.
    private void CloseSession() => DisposeNoThrow(Interlocked.Exchange(ref activeCaptureSession, null));

    private static void DisposeNoThrow(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
        }
    }

    private static void CancelNoThrow(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch
        {
        }
    }

    private static bool TryDisposeFrame(Direct3D11CaptureFrame? frame)
    {
        if (frame is null)
        {
            return true;
        }

        try
        {
            frame.Dispose();
            CaptureEventSource.Instance.FrameDisposed("screen");
            return true;
        }
        catch
        {
            CaptureEventSource.Instance.FrameDisposeFailed("screen");
            return false;
        }
    }

    private void MarkUnavailableIfUnknown()
    {
        Interlocked.CompareExchange(
            ref completion,
            (int)FrameSourceCompletion.Unavailable,
            (int)FrameSourceCompletion.Unknown);
    }

    private static bool IsBeforeNextSample(
        long sourceTimestampTicks,
        long lastEmittedTicks,
        long sampleIntervalTicks)
    {
        return sourceTimestampTicks <= lastEmittedTicks
            || sourceTimestampTicks - lastEmittedTicks < sampleIntervalTicks;
    }

    private static bool IsRecoverableFrameFault(Exception exception)
    {
        return exception is InvalidDataException
            or ArgumentException
            or InvalidCastException
            or ObjectDisposedException
            or IOException
            or OverflowException
            or InvalidOperationException
            or COMException;
    }

}
