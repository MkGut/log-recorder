using System.ComponentModel;
using System.Runtime.InteropServices;
using Log.Core.Capture;
using Log.Core.Diagnostics;
using Log.Core.Store;
using Log.Core.Timeline;

namespace Log.Windows;

/// <summary>
/// Rolls capture samples into independently finalized H.264/MP4 files.
/// </summary>
/// <remarks>
/// The caller owns the session store. Each committed segment is journaled only after
/// the underlying SinkWriter has finalized and the file length has been observed.
/// </remarks>
public sealed class MediaFoundationSegmentedH264Encoder : IEncoder, IDisposable
{
    public const long DefaultSegmentDurationTicks = 10L * TimelineConstants.TicksPerSecond;
    private const uint MoveFileWriteThrough = 0x00000008;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SessionStore store;
    private readonly string track;
    private readonly int width;
    private readonly int height;
    private readonly long frameDurationTicks;
    private readonly long segmentDurationTicks;
    private readonly uint averageBitrate;
    private readonly bool preferHardwareTransforms;
    private MediaFoundationH264Encoder? currentEncoder;
    private string? currentAssetReference;
    private string? currentTemporaryPath;
    private bool currentPromoted;
    private int currentSegmentIndex;
    private int currentFirstSlotIndex;
    private int currentSlotCount;
    private long currentStartedAtTicks;
    private long currentEndedAtTicks;
    private int nextSegmentIndex;
    private int? lastSlotIndex;
    private long? lastTargetTimestampTicks;
    private bool finalized;
    private int disposed;

    public MediaFoundationSegmentedH264Encoder(
        SessionStore store,
        string track,
        int width,
        int height,
        long frameDurationTicks,
        long segmentDurationTicks = DefaultSegmentDurationTicks,
        uint averageBitrate = 8_000_000,
        bool preferHardwareTransforms = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(track);
        if (track is not ("screen" or "camera"))
        {
            throw new ArgumentException("Segmented encoders support screen or camera tracks.", nameof(track));
        }

        if (!store.Manifest.Tracks.ContainsKey(track))
        {
            throw new ArgumentException(
                $"Track '{track}' is not configured for session '{store.Manifest.Id}'.",
                nameof(track));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameDurationTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentDurationTicks);
        ArgumentOutOfRangeException.ThrowIfZero(averageBitrate);

        this.store = store;
        this.track = track;
        this.width = width;
        this.height = height;
        this.frameDurationTicks = frameDurationTicks;
        this.segmentDurationTicks = segmentDurationTicks;
        this.averageBitrate = averageBitrate;
        this.preferHardwareTransforms = preferHardwareTransforms;
        nextSegmentIndex = GetNextSegmentIndex(store.Manifest, track);
    }

    public ValueTask WriteAsync(
        EncodedFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return WriteCoreAsync(frame, cancellationToken);
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        return FinalizeCoreAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        gate.Wait();
        try
        {
            var temporaryPath = !currentPromoted ? currentTemporaryPath : null;
            DisposeNoThrow(currentEncoder);
            currentEncoder = null;
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            ClearCurrentSegment();
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask WriteCoreAsync(
        EncodedFrame frame,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFrameOrder(frame);

            if (currentEncoder is not null && ShouldRotate(frame))
            {
                await FinalizeCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
            }

            if (currentEncoder is null)
            {
                StartSegment(frame);
            }

            var segmentFrame = frame with
            {
                TargetTimestampTicks = checked(frame.TargetTimestampTicks - currentStartedAtTicks)
            };
            await currentEncoder!.WriteAsync(segmentFrame, cancellationToken).ConfigureAwait(false);
            currentSlotCount = checked(currentSlotCount + 1);
            currentEndedAtTicks = checked(frame.TargetTimestampTicks + frameDurationTicks);
            lastSlotIndex = frame.SlotIndex;
            lastTargetTimestampTicks = frame.TargetTimestampTicks;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask FinalizeCoreAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (finalized)
            {
                return;
            }

            await FinalizeCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
            finalized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask FinalizeCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        var encoder = currentEncoder;
        if (encoder is null)
        {
            return;
        }

        if (currentSlotCount == 0)
        {
            var temporaryPath = !currentPromoted ? currentTemporaryPath : null;
            ClearCurrentSegment();
            DisposeNoThrow(encoder);
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            return;
        }

        var segmentIndex = currentSegmentIndex;
        CaptureEventSource.Instance.SegmentFinalizationStarted(track, segmentIndex);
        try
        {
            var assetReference = currentAssetReference
                ?? throw new InvalidOperationException("The active segment has no asset reference.");
            var segmentPath = ResolveSegmentPath(assetReference);
            EnsurePathNotReparsePoint(
                store.DirectoryPath,
                segmentPath,
                "The segment path cannot contain a reparse point.");
            if (!currentPromoted)
            {
                var temporaryPath = currentTemporaryPath
                    ?? throw new InvalidOperationException("The active segment has no temporary path.");
                EnsurePathNotReparsePoint(
                    store.DirectoryPath,
                    temporaryPath,
                    "The temporary segment path cannot contain a reparse point.");
                await encoder.FinalizeAsync(cancellationToken).ConfigureAwait(false);
                EnsurePathNotReparsePoint(
                    store.DirectoryPath,
                    temporaryPath,
                    "The temporary segment path cannot contain a reparse point.");
                if (!File.Exists(temporaryPath))
                {
                    throw new FileNotFoundException("The finalized temporary segment file was not found.", temporaryPath);
                }

                FlushFinalizedFile(temporaryPath);
                EnsurePathNotReparsePoint(
                    store.DirectoryPath,
                    segmentPath,
                    "The segment path cannot contain a reparse point.");
                if (File.Exists(segmentPath))
                {
                    throw new IOException($"The segment path already exists: {assetReference}.");
                }

                PromoteSegmentFile(temporaryPath, segmentPath);
                EnsurePathNotReparsePoint(
                    store.DirectoryPath,
                    segmentPath,
                    "The segment path cannot contain a reparse point.");
                currentPromoted = true;
            }

            EnsurePathNotReparsePoint(
                store.DirectoryPath,
                segmentPath,
                "The segment path cannot contain a reparse point.");
            var fileInfo = new FileInfo(segmentPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("The finalized segment file was not found.", segmentPath);
            }

            var nextIndex = checked(segmentIndex + 1);
            var segment = new SessionSegment(
                track,
                segmentIndex,
                currentFirstSlotIndex,
                currentSlotCount,
                currentStartedAtTicks,
                currentEndedAtTicks,
                assetReference,
                fileInfo.Length);
            CaptureEventSource.Instance.SegmentFinalized(track, segmentIndex, fileInfo.Length);
            await store.AppendFinalizedSegmentAsync(segment, cancellationToken).ConfigureAwait(false);
            CaptureEventSource.Instance.SegmentIndexed(track, segmentIndex);

            nextSegmentIndex = nextIndex;
        }
        catch
        {
            CaptureEventSource.Instance.SegmentFinalizationFailed(track, segmentIndex);
            throw;
        }

        ClearCurrentSegment();
        DisposeNoThrow(encoder);
    }

    private void StartSegment(EncodedFrame frame)
    {
        if (nextSegmentIndex == int.MaxValue)
        {
            throw new InvalidOperationException("The session has exhausted its segment index range.");
        }

        var assetReference = $"{track}/segment-{nextSegmentIndex:D6}.mp4";
        var segmentPath = ResolveSegmentPath(assetReference);
        EnsurePathNotReparsePoint(
            store.DirectoryPath,
            segmentPath,
            "The segment path cannot contain a reparse point.");
        if (File.Exists(segmentPath))
        {
            throw new IOException($"The segment path already exists: {assetReference}.");
        }

        var temporaryDirectory = Path.Combine(store.DirectoryPath, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        EnsurePathNotReparsePoint(
            store.DirectoryPath,
            temporaryDirectory,
            "The temporary segment directory cannot contain a reparse point.");
        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $"segment-{track}-{nextSegmentIndex:D6}-{Guid.NewGuid():N}.mp4");
        var parentDirectory = Path.GetDirectoryName(segmentPath)
            ?? throw new IOException("The segment path has no parent directory.");
        Directory.CreateDirectory(parentDirectory);
        EnsurePathNotReparsePoint(
            store.DirectoryPath,
            parentDirectory,
            "The segment directory cannot contain a reparse point.");
        MediaFoundationH264Encoder encoder;
        try
        {
            encoder = new MediaFoundationH264Encoder(
                temporaryPath,
                width,
                height,
                frameDurationTicks,
                averageBitrate,
                preferHardwareTransforms);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }

        currentEncoder = encoder;
        currentAssetReference = assetReference;
        currentTemporaryPath = temporaryPath;
        currentPromoted = false;
        currentSegmentIndex = nextSegmentIndex;
        currentFirstSlotIndex = frame.SlotIndex;
        currentSlotCount = 0;
        currentStartedAtTicks = frame.TargetTimestampTicks;
        currentEndedAtTicks = frame.TargetTimestampTicks;
    }

    private bool ShouldRotate(EncodedFrame frame)
    {
        return frame.TargetTimestampTicks - currentStartedAtTicks >= segmentDurationTicks;
    }

    private void ValidateFrameOrder(EncodedFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame.SlotIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(frame.TargetTimestampTicks);
        if (lastSlotIndex is int previousSlotIndex
            && frame.SlotIndex <= previousSlotIndex)
        {
            throw new InvalidDataException("Segmented encoder slot indices must be strictly increasing.");
        }

        if (lastTargetTimestampTicks is long previousTargetTimestampTicks
            && frame.TargetTimestampTicks <= previousTargetTimestampTicks)
        {
            throw new InvalidDataException("Segmented encoder timestamps must be strictly increasing.");
        }

        if (frame.TargetTimestampTicks > long.MaxValue - frameDurationTicks)
        {
            throw new InvalidDataException(
                "The encoded frame timestamp cannot be represented with its duration.");
        }
    }

    private void ClearCurrentSegment()
    {
        currentEncoder = null;
        currentAssetReference = null;
        currentTemporaryPath = null;
        currentPromoted = false;
        currentSlotCount = 0;
        currentStartedAtTicks = 0;
        currentEndedAtTicks = 0;
    }

    private string ResolveSegmentPath(string assetReference)
    {
        return Path.GetFullPath(
            Path.Combine(store.DirectoryPath, assetReference.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void EnsurePathNotReparsePoint(
        string sessionRoot,
        string path,
        string message)
    {
        EnsureNotReparsePoint(sessionRoot, message);
        var relativePath = Path.GetRelativePath(sessionRoot, path);
        if (relativePath == ".")
        {
            return;
        }

        var currentPath = sessionRoot;
        foreach (var component in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            EnsureNotReparsePoint(currentPath, message);
        }
    }

    private static void EnsureNotReparsePoint(string path, string message)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException(message);
            }
        }
        catch (FileNotFoundException)
        {
            // A not-yet-created segment is expected; its existing ancestors are still checked.
        }
        catch (DirectoryNotFoundException)
        {
            // A not-yet-created directory is expected; its existing ancestors are still checked.
        }
    }

    private static void FlushFinalizedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void PromoteSegmentFile(string temporaryPath, string segmentPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(temporaryPath, segmentPath);
            return;
        }

        if (MoveFileEx(temporaryPath, segmentPath, MoveFileWriteThrough))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        throw new IOException(
            "The finalized segment could not be promoted durably.",
            new Win32Exception(error));
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (finalized)
        {
            throw new InvalidOperationException("The segmented H.264 encoder has already been finalized.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static int GetNextSegmentIndex(SessionManifest manifest, string track)
    {
        var highestIndex = manifest.Segments
            .Where(segment => string.Equals(segment.Track, track, StringComparison.Ordinal))
            .Select(static segment => segment.Index)
            .DefaultIfEmpty(-1)
            .Max();
        return highestIndex == int.MaxValue
            ? throw new InvalidOperationException("The session has exhausted its segment index range.")
            : highestIndex + 1;
    }

    private static void DisposeNoThrow(MediaFoundationH264Encoder? encoder)
    {
        try
        {
            encoder?.Dispose();
        }
        catch
        {
            // The segment is already durable; cleanup must not rewrite its journal state.
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A failed startup must not hide its original encoder error.
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}
