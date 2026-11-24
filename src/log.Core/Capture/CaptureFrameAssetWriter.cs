using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace Log.Core.Capture;

/// <summary>
/// Writes sampled BGRA frames as durable PNG assets before their manifest journal record.
/// </summary>
public static class CaptureFrameAssetWriter
{
    private const int MaxPngChunkBytes = 64 * 1024 * 1024;
    private const long MaxPngFileBytes = 256L * 1024 * 1024;
    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
    ];

    public static bool CanWriteBgraFrame(FrameSample sourceFrame)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        if (sourceFrame.Width > int.MaxValue / 4)
        {
            return false;
        }

        var bytesPerRow = (long)sourceFrame.Width * 4L;
        if (bytesPerRow > int.MaxValue / sourceFrame.Height)
        {
            return false;
        }

        var expectedPayloadLength = bytesPerRow * sourceFrame.Height;
        return sourceFrame.Payload.Length == expectedPayloadLength;
    }

    public static async ValueTask<long> WriteAsync(
        string sessionDirectory,
        string track,
        string assetReference,
        FrameSample sourceFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference);
        ArgumentNullException.ThrowIfNull(sourceFrame);
        if (track is not ("screen" or "camera"))
        {
            throw new ArgumentException("Frame assets must belong to the screen or camera track.", nameof(track));
        }

        var normalizedReference = ValidateReference(track, assetReference);
        return await WriteAssetAsync(
            sessionDirectory,
            normalizedReference,
            sourceFrame,
            allowExisting: false,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string CreateHighlightAssetReference(string highlightId, string track)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highlightId);
        if (track is not ("screen" or "camera"))
        {
            throw new ArgumentException("Frame assets must belong to the screen or camera track.", nameof(track));
        }

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(highlightId))).ToLowerInvariant();
        return $"highlights/hl-{digest}-{track}.png";
    }

    internal static async ValueTask<long> WriteHighlightAsync(
        string sessionDirectory,
        string assetReference,
        FrameSample sourceFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference);
        ArgumentNullException.ThrowIfNull(sourceFrame);

        var normalizedReference = ValidateHighlightReference(assetReference);
        return await WriteAssetAsync(
            sessionDirectory,
            normalizedReference,
            sourceFrame,
            allowExisting: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> WriteAssetAsync(
        string sessionDirectory,
        string normalizedReference,
        FrameSample sourceFrame,
        bool allowExisting,
        CancellationToken cancellationToken)
    {
        var sessionRoot = Path.GetFullPath(sessionDirectory);
        if (!CanWriteBgraFrame(sourceFrame))
        {
            throw new InvalidDataException("A BGRA frame payload does not match its dimensions.");
        }

        var targetPath = ResolveInsideSession(sessionRoot, normalizedReference);
        var replaceInvalidExisting = false;
        EnsurePathNotReparsePoint(
            sessionRoot,
            targetPath,
            "The frame asset path cannot contain a reparse point.");
        if (allowExisting && File.Exists(targetPath))
        {
            if (IsPngFile(targetPath, sourceFrame.Width, sourceFrame.Height))
            {
                return 0;
            }

            replaceInvalidExisting = true;
        }

        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new IOException("The frame asset has no parent directory.");
        Directory.CreateDirectory(targetDirectory);
        EnsurePathNotReparsePoint(
            sessionRoot,
            targetDirectory,
            "The frame asset directory cannot contain a reparse point.");
        var temporaryDirectory = Path.Combine(sessionRoot, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        EnsurePathNotReparsePoint(
            sessionRoot,
            temporaryDirectory,
            "The temporary frame directory cannot contain a reparse point.");
        var temporaryPath = Path.Combine(temporaryDirectory, $"frame-{Guid.NewGuid():N}.tmp");
        long writtenLength;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await WritePngAsync(stream, sourceFrame, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                writtenLength = stream.Length;
            }

            PromoteAssetFile(temporaryPath, targetPath, replaceInvalidExisting);
            return writtenLength;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static bool IsPngFile(string path, int expectedWidth, int expectedHeight)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxPngFileBytes)
            {
                return false;
            }

            if (stream.Length < PngSignature.Length + 12)
            {
                return false;
            }

            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            var sawHeader = false;
            var sawImageData = false;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> crcBytes = stackalloc byte[4];
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 12)
                {
                    return false;
                }

                stream.ReadExactly(chunkHeader);
                var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
                if (chunkLength > MaxPngChunkBytes || chunkLength > int.MaxValue)
                {
                    return false;
                }

                var chunkType = chunkHeader[4..8].ToArray();
                var chunkData = new byte[(int)chunkLength];
                stream.ReadExactly(chunkData);
                stream.ReadExactly(crcBytes);
                if (BinaryPrimitives.ReadUInt32BigEndian(crcBytes)
                    != ComputeCrc(chunkType, chunkData))
                {
                    return false;
                }

                if (!sawHeader)
                {
                    if (chunkLength != 13
                        || !chunkType.AsSpan().SequenceEqual("IHDR"u8)
                        || !IsValidHeader(chunkData, expectedWidth, expectedHeight))
                    {
                        return false;
                    }

                    sawHeader = true;
                }
                else if (chunkType.AsSpan().SequenceEqual("IHDR"u8))
                {
                    return false;
                }

                if (chunkType.AsSpan().SequenceEqual("IDAT"u8))
                {
                    sawImageData = true;
                }

                if (chunkType.AsSpan().SequenceEqual("IEND"u8))
                {
                    return chunkLength == 0
                        && sawImageData
                        && stream.Position == stream.Length;
                }
            }
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        return false;
    }

    private static bool IsValidHeader(
        ReadOnlySpan<byte> chunkData,
        int expectedWidth,
        int expectedHeight)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(chunkData) == (uint)expectedWidth
            && BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..]) == (uint)expectedHeight
            && chunkData[8] == 8
            && chunkData[9] == 6
            && chunkData[10] == 0
            && chunkData[11] == 0
            && chunkData[12] == 0;
    }

    private static async Task WritePngAsync(
        Stream stream,
        FrameSample sourceFrame,
        CancellationToken cancellationToken)
    {
        var rowLength = checked(sourceFrame.Width * 4 + 1);
        var expectedPayloadLength = checked(sourceFrame.Width * sourceFrame.Height * 4);
        if (sourceFrame.Payload.Length != expectedPayloadLength)
        {
            throw new InvalidDataException("A BGRA frame payload does not match its dimensions.");
        }

        // Keep the immutable frame payload in place. Copying a full BGRA frame
        // here doubles peak memory and GC pressure at every sampled slot.
        var payload = sourceFrame.Payload;
        stream.Write(PngSignature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)sourceFrame.Width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), checked((uint)sourceFrame.Height));
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(stream, "IHDR"u8, header);

        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            // Batch rows to avoid thousands of async scheduler transitions per
            // screenshot while keeping the temporary buffer bounded.
            const int rowsPerBatch = 16;
            var batch = ArrayPool<byte>.Shared.Rent(checked(rowLength * rowsPerBatch));
            try
            {
                for (var firstRow = 0; firstRow < sourceFrame.Height; firstRow += rowsPerBatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rowCount = Math.Min(rowsPerBatch, sourceFrame.Height - firstRow);
                    for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                    {
                        var y = firstRow + rowIndex;
                        var rowOffset = checked(rowIndex * rowLength);
                        batch[rowOffset] = 0;
                        var sourceOffset = checked(y * sourceFrame.Width * 4);
                        for (var x = 0; x < sourceFrame.Width; x++)
                        {
                            var sourcePixel = sourceOffset + x * 4;
                            var destinationPixel = rowOffset + 1 + x * 4;
                            batch[destinationPixel] = payload.Span[sourcePixel + 2];
                            batch[destinationPixel + 1] = payload.Span[sourcePixel + 1];
                            batch[destinationPixel + 2] = payload.Span[sourcePixel];
                            batch[destinationPixel + 3] = payload.Span[sourcePixel + 3];
                        }
                    }

                    await zlib.WriteAsync(
                        batch.AsMemory(0, checked(rowCount * rowLength)),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(batch);
            }
        }

        var compressedLength = checked((int)compressed.Length);
        WriteChunk(stream, "IDAT"u8, compressed.GetBuffer().AsSpan(0, compressedLength));
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static string ValidateReference(string track, string assetReference)
    {
        var normalized = assetReference.Replace('\\', '/');
        if (Path.IsPathRooted(assetReference)
            || assetReference.Contains(':')
            || assetReference.Contains('\0')
            || !normalized.StartsWith($"{track}/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Frame asset references must stay under their track directory.", nameof(assetReference));
        }

        return normalized;
    }

    private static string ValidateHighlightReference(string assetReference)
    {
        var normalized = assetReference.Replace('\\', '/');
        if (Path.IsPathRooted(assetReference)
            || assetReference.Contains(':')
            || assetReference.Contains('\0')
            || !normalized.StartsWith("highlights/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(static part => part is "" or "." or "..")
            || !string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Highlight assets must be normalized relative .png paths under highlights.",
                nameof(assetReference));
        }

        return normalized;
    }

    private static string ResolveInsideSession(string sessionRoot, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(sessionRoot, relativePath));
        var prefix = sessionRoot.EndsWith(Path.DirectorySeparatorChar)
            ? sessionRoot
            : sessionRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The frame asset path escapes the session directory.");
        }

        return candidate;
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
            // A not-yet-created asset is expected; its existing ancestors are still checked.
        }
        catch (DirectoryNotFoundException)
        {
            // A not-yet-created directory is expected; its existing ancestors are still checked.
        }
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

    private static void PromoteAssetFile(
        string temporaryPath,
        string targetPath,
        bool replaceExisting)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(temporaryPath, targetPath, overwrite: replaceExisting);
            return;
        }

        var flags = MoveFileWriteThrough
            | (replaceExisting ? MoveFileReplaceExisting : 0U);
        if (MoveFileEx(temporaryPath, targetPath, flags))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        throw new IOException(
            "The frame asset could not be promoted durably.",
            new Win32Exception(error));
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        if (type.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc(type, data));
        stream.Write(crc);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB8_8320u
                    : crc >> 1;
            }
        }

        return crc;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "MoveFileExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}
