using System.Runtime.InteropServices;
using Log.Core.Capture;
using Log.Core.Diagnostics;
using Log.Core.Timeline;

namespace Log.Windows;

/// <summary>
/// Feeds owned BGRA frames into the native in-box Media Foundation SinkWriter bridge.
/// </summary>
public sealed class MediaFoundationH264Encoder : IEncoder, IDisposable
{
    private const string NativeLibraryName = "log.MediaFoundationBridge";
    private const uint DefaultAverageBitrate = 8_000_000;
    private const int UnexpectedError = unchecked((int)0x8000FFFF);
    private const int NativeBackendHardware = 1;
    private const int NativeBackendSoftware = 2;

    private readonly object gate = new();
    private readonly MediaFoundationSession mediaFoundationSession;
    private readonly string outputPath;
    private readonly int width;
    private readonly int height;
    private readonly long frameDurationTicks;
    private readonly uint frameByteLength;
    private readonly uint averageBitrate;
    private readonly uint frameRateNumerator;
    private readonly uint frameRateDenominator;
    private readonly bool preferHardwareTransforms;
    private readonly byte[] gapFrame;
    private nint nativeEncoder;
    private long? lastSampleTimestampTicks;
    private bool nativeFinalized;
    private bool finalized;
    private bool softwareFallbackAttempted;
    private bool hasWrittenSample;
    private bool mediaFoundationSessionDisposed;
    private bool disposed;

    public MediaFoundationH264Encoder(
        string outputPath,
        int width,
        int height,
        long frameDurationTicks,
        uint averageBitrate = DefaultAverageBitrate,
        bool preferHardwareTransforms = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameDurationTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageBitrate);

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullOutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Media Foundation H.264 output paths must use the .mp4 extension.",
                nameof(outputPath));
        }

        EnsureOutputPathNotReparsePoint(fullOutputPath);
        if (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath))
        {
            throw new IOException("The Media Foundation H.264 output path already exists.");
        }

        var pixelCount = (long)width * height;
        if (pixelCount > int.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The BGRA frame is too large for the native bridge.");
        }

        var frameBytes = pixelCount * 4L;
        var frameRate = FrameRate.FromInterval(frameDurationTicks);
        this.outputPath = fullOutputPath;
        this.width = width;
        this.height = height;
        this.frameDurationTicks = frameDurationTicks;
        this.averageBitrate = averageBitrate;
        frameRateNumerator = frameRate.Numerator;
        frameRateDenominator = frameRate.Denominator;
        this.preferHardwareTransforms = preferHardwareTransforms;
        frameByteLength = (uint)frameBytes;
        gapFrame = new byte[checked((int)frameBytes)];

        mediaFoundationSession = MediaFoundationSession.Start();
        try
        {
            var hresult = CreateNativeEncoder(
                fullOutputPath,
                width,
                height,
                frameRate.Numerator,
                frameRate.Denominator,
                averageBitrate,
                enableHardwareTransforms: preferHardwareTransforms,
                out nativeEncoder);
            if (hresult < 0 && preferHardwareTransforms)
            {
                softwareFallbackAttempted = true;
                CaptureEventSource.Instance.EncoderSoftwareFallback(
                    "media-foundation",
                    hresult);
                ReleaseNativeEncoderNoThrow();
                hresult = CreateNativeEncoder(
                    fullOutputPath,
                    width,
                    height,
                    frameRate.Numerator,
                    frameRate.Denominator,
                    averageBitrate,
                    enableHardwareTransforms: false,
                    out nativeEncoder);
            }

            if (hresult >= 0 && nativeEncoder == 0)
            {
                hresult = UnexpectedError;
            }

            if (hresult < 0)
            {
                ReleaseNativeEncoderNoThrow();
                throw new MediaFoundationException("SinkWriter creation", hresult);
            }

            CaptureEventSource.Instance.EncoderBackendRequested(
                "media-foundation",
                preferHardwareTransforms && !softwareFallbackAttempted
                    ? "hardware"
                    : "software");
        }
        catch (MediaFoundationException)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw;
        }
        catch (DllNotFoundException exception)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw new MediaFoundationException("SinkWriter bridge load", exception.HResult, exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw new MediaFoundationException("SinkWriter bridge entry point lookup", exception.HResult, exception);
        }
        catch (BadImageFormatException exception)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw new MediaFoundationException("SinkWriter bridge architecture check", exception.HResult, exception);
        }
        catch (FileLoadException exception)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw new MediaFoundationException("SinkWriter bridge load", exception.HResult, exception);
        }
        catch (TypeLoadException exception)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw new MediaFoundationException("SinkWriter bridge type load", exception.HResult, exception);
        }
        catch (TypeInitializationException exception)
        {
            DisposeMediaFoundationSessionNoThrow();
            throw new MediaFoundationException(
                "SinkWriter bridge type initialization",
                exception.HResult,
                exception);
        }
        catch
        {
            ReleaseNativeEncoderNoThrow();
            DisposeMediaFoundationSessionNoThrow();
            throw;
        }
    }

    public int Width => width;
    public int Height => height;
    public long FrameDurationTicks => frameDurationTicks;

    public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentOutOfRangeException.ThrowIfNegative(frame.TargetTimestampTicks);
            if (frame.TargetTimestampTicks > long.MaxValue - frameDurationTicks)
            {
                throw new InvalidDataException(
                    "The encoded frame timestamp cannot be represented with its duration.");
            }

            if (lastSampleTimestampTicks is long previousTimestampTicks
                && frame.TargetTimestampTicks <= previousTimestampTicks)
            {
                throw new InvalidDataException("Encoded frame timestamps must be strictly increasing.");
            }

            ReadOnlyMemory<byte> payload = gapFrame;
            if (frame.Sample is { } sample)
            {
                if (sample.Width != width
                    || sample.Height != height
                    || sample.Payload.Length != frameByteLength)
                {
                    throw new InvalidDataException("The frame dimensions do not match the H.264 encoder.");
                }

                payload = sample.Payload;
            }

            int hresult;
            var failureStage = "SinkWriter sample write";
            try
            {
                hresult = WriteNativeFramePinned(
                    nativeEncoder,
                    payload,
                    frame.TargetTimestampTicks,
                    frameDurationTicks);
                if (hresult < 0
                    && preferHardwareTransforms
                    && !softwareFallbackAttempted
                    && !hasWrittenSample)
                {
                    // A hardware MFT can accept creation and still reject its first
                    // sample. Retry once with the in-box software path before any
                    // frame has been accepted into this output segment.
                    softwareFallbackAttempted = true;
                    CaptureEventSource.Instance.EncoderSoftwareFallback(
                        "media-foundation",
                        hresult);
                    ReleaseNativeEncoderNoThrow();
                    try
                    {
                        hresult = CreateNativeEncoder(
                            outputPath,
                            width,
                            height,
                            frameRateNumerator,
                            frameRateDenominator,
                            averageBitrate,
                            enableHardwareTransforms: false,
                            out nativeEncoder);
                    }
                    catch (Exception exception) when (IsNativeInteropFailure(exception))
                    {
                        throw new MediaFoundationException(
                            "SinkWriter fallback creation",
                            exception.HResult,
                            exception);
                    }

                    if (hresult >= 0 && nativeEncoder == 0)
                    {
                        hresult = UnexpectedError;
                    }

                    if (hresult < 0)
                    {
                        failureStage = "SinkWriter fallback creation";
                    }
                    else
                    {
                        failureStage = "SinkWriter fallback sample write";
                        hresult = WriteNativeFramePinned(
                            nativeEncoder,
                            payload,
                            frame.TargetTimestampTicks,
                            frameDurationTicks);
                        if (hresult >= 0)
                        {
                            CaptureEventSource.Instance.EncoderBackendRequested(
                                "media-foundation",
                                "software");
                        }
                    }
                }
            }
            catch (Exception exception) when (IsNativeInteropFailure(exception))
            {
                throw new MediaFoundationException(failureStage, exception.HResult, exception);
            }

            if (hresult < 0)
            {
                throw new MediaFoundationException(failureStage, hresult);
            }

            if (!hasWrittenSample)
            {
                CaptureEventSource.Instance.EncoderBackendSelected(
                    "media-foundation",
                    GetBackendName(GetNativeBackendNoThrow(nativeEncoder)));
            }

            lastSampleTimestampTicks = frame.TargetTimestampTicks;
            hasWrittenSample = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (finalized)
            {
                return ValueTask.CompletedTask;
            }

            if (nativeEncoder == 0)
            {
                throw new InvalidOperationException("The H.264 encoder is unavailable for finalization.");
            }

            try
            {
                if (!nativeFinalized)
                {
                    int hresult;
                    try
                    {
                        hresult = FinalizeNativeEncoder(nativeEncoder);
                    }
                    catch (Exception exception) when (IsNativeInteropFailure(exception))
                    {
                        throw new MediaFoundationException("SinkWriter finalization", exception.HResult, exception);
                    }

                    if (hresult < 0)
                    {
                        throw new MediaFoundationException("SinkWriter finalization", hresult);
                    }

                    nativeFinalized = true;
                }

                finalized = true;
            }
            finally
            {
                ReleaseNativeEncoderNoThrow();
                DisposeMediaFoundationSessionNoThrow();
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            ReleaseNativeEncoderNoThrow();
            if (DisposeMediaFoundationSessionNoThrow())
            {
                disposed = true;
            }
        }
    }

    private static int WriteNativeFramePinned(
        nint encoder,
        ReadOnlyMemory<byte> payload,
        long sampleTime,
        long sampleDuration)
    {
        if (!MemoryMarshal.TryGetArray(payload, out var segment)
            || segment.Array is null
            || segment.Count != payload.Length)
        {
            segment = new ArraySegment<byte>(payload.ToArray());
        }

        var handle = GCHandle.Alloc(segment.Array!, GCHandleType.Pinned);
        try
        {
            var address = IntPtr.Add(handle.AddrOfPinnedObject(), segment.Offset);
            return log_mf_encoder_write_bgra(
                encoder,
                address,
                checked((uint)payload.Length),
                sampleTime,
                sampleDuration);
        }
        finally
        {
            handle.Free();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (finalized || nativeFinalized || nativeEncoder == 0)
        {
            throw new InvalidOperationException("The H.264 encoder has already been finalized.");
        }
    }

    private void ReleaseNativeEncoderNoThrow()
    {
        if (nativeEncoder == 0)
        {
            return;
        }

        try
        {
            ReleaseNativeEncoder(nativeEncoder);
        }
        catch
        {
            // Native cleanup must not prevent the managed object from releasing its runtime lease.
        }
        finally
        {
            nativeEncoder = 0;
        }
    }

    private bool DisposeMediaFoundationSessionNoThrow()
    {
        if (mediaFoundationSessionDisposed)
        {
            return true;
        }

        try
        {
            mediaFoundationSession.Dispose();
            mediaFoundationSessionDisposed = true;
            return true;
        }
        catch
        {
            // MediaFoundationSession retains its lease on shutdown failure so a later
            // encoder Dispose can retry without allowing capture cleanup to mask its
            // primary error.
            return false;
        }
    }

    private static void EnsureOutputPathNotReparsePoint(string outputPath)
    {
        string? currentPath = outputPath;
        while (currentPath is not null)
        {
            try
            {
                if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("The Media Foundation H.264 output path cannot contain a reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
                // The output file is expected to be absent; continue with its ancestors.
            }
            catch (DirectoryNotFoundException)
            {
                // Missing output directories are validated by SinkWriter during creation.
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }
    }

    private static bool IsNativeInteropFailure(Exception exception)
    {
        return exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or FileLoadException
            or TypeLoadException
            or TypeInitializationException;
    }

    private static int GetNativeBackendNoThrow(nint encoder)
    {
        try
        {
            var hresult = log_mf_encoder_get_backend(encoder, out var backend);
            return hresult >= 0 ? backend : 0;
        }
        catch (Exception exception) when (IsNativeInteropFailure(exception))
        {
            return 0;
        }
    }

    private static string GetBackendName(int backend)
    {
        return backend switch
        {
            NativeBackendHardware => "hardware",
            NativeBackendSoftware => "software",
            _ => "unknown"
        };
    }

    private readonly record struct FrameRate(uint Numerator, uint Denominator)
    {
        public static FrameRate FromInterval(long intervalTicks)
        {
            var numerator = TimelineConstants.TicksPerSecond;
            var denominator = intervalTicks;
            var divisor = GreatestCommonDivisor(numerator, denominator);
            numerator /= divisor;
            denominator /= divisor;
            if (numerator > uint.MaxValue || denominator > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(intervalTicks),
                    "The interval cannot be represented by the native frame-rate fields.");
            }

            return new FrameRate((uint)numerator, (uint)denominator);
        }

        private static long GreatestCommonDivisor(long left, long right)
        {
            while (right != 0)
            {
                (left, right) = (right, left % right);
            }

            return left;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int log_mf_encoder_create(
        string outputPath,
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator,
        uint averageBitrate,
        [MarshalAs(UnmanagedType.Bool)] bool enableHardwareTransforms,
        out nint encoder);

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int log_mf_encoder_write_bgra(
        nint encoder,
        nint bgra,
        uint byteLength,
        long sampleTime,
        long sampleDuration);

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int log_mf_encoder_finalize(nint encoder);

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int log_mf_encoder_get_backend(
        nint encoder,
        out int backend);

    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern void log_mf_encoder_release(nint encoder);

    private static int CreateNativeEncoder(
        string outputPath,
        int width,
        int height,
        uint frameRateNumerator,
        uint frameRateDenominator,
        uint averageBitrate,
        bool enableHardwareTransforms,
        out nint encoder)
    {
        return log_mf_encoder_create(
            outputPath,
            (uint)width,
            (uint)height,
            frameRateNumerator,
            frameRateDenominator,
            averageBitrate,
            enableHardwareTransforms,
            out encoder);
    }

    private static int FinalizeNativeEncoder(nint encoder)
    {
        return log_mf_encoder_finalize(encoder);
    }

    private static void ReleaseNativeEncoder(nint encoder)
    {
        log_mf_encoder_release(encoder);
    }
}
