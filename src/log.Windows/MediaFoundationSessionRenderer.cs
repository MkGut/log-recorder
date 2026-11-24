using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBuffer = global::Windows.Storage.Streams.Buffer;

namespace Log.Windows;

/// <summary>
/// Renders the planner's nondestructive source references into one in-box H.264
/// MP4. Source PNGs remain untouched; only the output composition is written.
/// </summary>
public sealed class MediaFoundationSessionRenderer
{
    private const uint DefaultAverageBitrate = 8_000_000;

    private readonly string rootDirectory;
    private readonly uint averageBitrate;
    private readonly bool preferHardwareTransforms;

    public MediaFoundationSessionRenderer(
        string rootDirectory,
        uint averageBitrate = DefaultAverageBitrate,
        bool preferHardwareTransforms = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfZero(averageBitrate);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        this.averageBitrate = averageBitrate;
        this.preferHardwareTransforms = preferHardwareTransforms;
    }

    public async Task RenderAsync(
        SessionExportContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = context.RenderSettings.Composition;
        var outputSize = settings.OutputSize.EvenAligned();
        var plans = RenderTimelinePlanner.Plan(context.RenderInput, context.RenderSettings);
        // Spread the frames evenly over the target length, but never slower than the minimum fps.
        var frameDurationTicks = Math.Max(
            1,
            Math.Min(
                RenderPreset.TargetDurationSeconds * TimelineConstants.TicksPerSecond / Math.Max(1, plans.Count),
                TimelineConstants.TicksPerSecond / RenderPreset.MinimumFramesPerSecond));
        var sessionDirectory = Path.GetFullPath(
            LogPaths.SessionDirectory(rootDirectory, context.SessionId));
        EnsureNotReparsePoint(sessionDirectory);

        using var encoder = new MediaFoundationH264Encoder(
            context.OutputPath,
            outputSize.Width,
            outputSize.Height,
            frameDurationTicks,
            averageBitrate,
            preferHardwareTransforms);
        var screenCache = new RenderAssetCache();
        var cameraCache = new RenderAssetCache();
        var outputPixels = new byte[checked(outputSize.Width * outputSize.Height * 4)];
        // A timelapse must never flash to a blank frame at a gap slot (an
        // accessibility hazard). Hold the last composed frame across gaps.
        byte[]? heldComposite = null;

        if (plans.Count == 0)
        {
            var blank = CreateBlankFrame(outputSize);
            await encoder.WriteAsync(
                new EncodedFrame(0, 0, blank, IsDuplicate: false, AgeTicks: 0),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            for (var index = 0; index < plans.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = plans[index];
                Array.Clear(outputPixels);
                var composed = false;

                if (plan.Composition.ScreenVisible
                    && plan.Composition.ScreenDestination is { } screenDestination)
                {
                    var screen = await screenCache
                        .GetAsync(
                            plan.Sources.ScreenAssetReference,
                            sessionDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (screen is not null)
                    {
                        Blit(
                            screen,
                            outputPixels,
                            outputSize.Width,
                            screenDestination,
                            CameraMirror.None);
                        composed = true;
                    }
                }

                if (plan.Composition.CameraDestination is { } cameraDestination)
                {
                    var camera = await cameraCache
                        .GetAsync(
                            plan.Sources.CameraAssetReference,
                            sessionDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (camera is not null)
                    {
                        Blit(
                            camera,
                            outputPixels,
                            outputSize.Width,
                            cameraDestination,
                            plan.Composition.CameraMirror);
                        composed = true;
                    }
                }

                if (composed)
                {
                    // Save the composition (before the timer overlay) so a later
                    // gap slot can reuse it rather than emitting a blank frame.
                    heldComposite ??= new byte[outputPixels.Length];
                    Array.Copy(outputPixels, heldComposite, outputPixels.Length);
                }
                else if (heldComposite is not null)
                {
                    Array.Copy(heldComposite, outputPixels, outputPixels.Length);
                }

                if (plan.ElapsedTimerText is { Length: > 0 } timerText)
                {
                    DrawTimerOverlay(
                        outputPixels,
                        outputSize.Width,
                        outputSize.Height,
                        timerText,
                        context.RenderSettings.Timer.Opacity);
                }

                if (context.RenderSettings.WatermarkEnabled)
                {
                    DrawWatermarkOverlay(outputPixels, outputSize.Width, outputSize.Height);
                }

                var frame = new FrameSample(
                    sourceTimestampTicks: index * frameDurationTicks,
                    payload: outputPixels,
                    width: outputSize.Width,
                    height: outputSize.Height,
                    timestampQuality: FrameTimestampQuality.SourceProvided);
                await encoder.WriteAsync(
                    new EncodedFrame(
                        index,
                        index * frameDurationTicks,
                        frame,
                        IsDuplicate: false,
                        AgeTicks: 0),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await encoder.FinalizeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FrameSample CreateBlankFrame(PixelSize outputSize)
    {
        return new FrameSample(
            sourceTimestampTicks: 0,
            payload: new byte[checked(outputSize.Width * outputSize.Height * 4)],
            width: outputSize.Width,
            height: outputSize.Height,
            timestampQuality: FrameTimestampQuality.SourceProvided);
    }

    private static void Blit(
        DecodedAsset source,
        byte[] destination,
        int destinationWidth,
        PixelRect target,
        CameraMirror mirror)
    {
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        if (mirror == CameraMirror.None && target.Width == source.Width)
        {
            var rowByteLength = checked(target.Width * 4);
            for (var y = 0; y < target.Height; y++)
            {
                var sourceY = y * source.Height / target.Height;
                var sourceOffset = checked(sourceY * rowByteLength);
                var destinationOffset = checked(
                    ((target.Y + y) * destinationWidth + target.X) * 4);
                source.Pixels.AsSpan(sourceOffset, rowByteLength)
                    .CopyTo(destination.AsSpan(destinationOffset, rowByteLength));
            }

            return;
        }

        var sourcePixels = source.Pixels;
        for (var y = 0; y < target.Height; y++)
        {
            var sourceY = y * source.Height / target.Height;
            var destinationRow = checked((target.Y + y) * destinationWidth * 4);
            var sourceRow = checked(sourceY * source.Width * 4);
            for (var x = 0; x < target.Width; x++)
            {
                var sourceX = x * source.Width / target.Width;
                if (mirror == CameraMirror.Horizontal)
                {
                    sourceX = source.Width - 1 - sourceX;
                }

                var sourceOffset = checked(sourceRow + sourceX * 4);
                var destinationOffset = checked(
                    destinationRow + (target.X + x) * 4);
                destination[destinationOffset] = sourcePixels[sourceOffset];
                destination[destinationOffset + 1] = sourcePixels[sourceOffset + 1];
                destination[destinationOffset + 2] = sourcePixels[sourceOffset + 2];
                destination[destinationOffset + 3] = sourcePixels[sourceOffset + 3];
            }
        }
    }

    private static void DrawTimerOverlay(
        byte[] pixels,
        int width,
        int height,
        string text,
        double opacity)
    {
        if (text.Length == 0 || width <= 0 || height <= 0 || opacity <= 0)
        {
            return;
        }

        var scale = Math.Clamp(
            Math.Min(
                Math.Max(1, width / checked(text.Length * 6 + 4)),
                Math.Max(1, height / 32)),
            1,
            4);
        var padding = checked(scale * 3);
        var glyphColumns = checked(text.Length * 5 + Math.Max(0, text.Length - 1));
        var boxWidth = checked(glyphColumns * scale + padding * 2);
        var boxHeight = checked(7 * scale + padding * 2);
        var margin = Math.Max(scale * 3, Math.Min(width, height) / 40);
        var boxX = Math.Max(0, width - margin - boxWidth);
        var boxY = margin;
        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * byte.MaxValue), 0, byte.MaxValue);

        BlendRectangle(pixels, width, height, boxX, boxY, boxWidth, boxHeight, 0, 0, 0, alpha);
        var cursorX = boxX + padding;
        var glyphAlpha = alpha;
        for (var index = 0; index < text.Length; index++)
        {
            var glyph = GetTimerGlyph(text[index]);
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < glyph[row].Length; column++)
                {
                    if (glyph[row][column] != '1')
                    {
                        continue;
                    }

                    BlendRectangle(
                        pixels,
                        width,
                        height,
                        cursorX + column * scale,
                        boxY + padding + row * scale,
                        scale,
                        scale,
                        byte.MaxValue,
                        byte.MaxValue,
                        byte.MaxValue,
                        glyphAlpha);
                }
            }

            cursorX += checked(6 * scale);
        }
    }

    private static void DrawWatermarkOverlay(byte[] pixels, int width, int height)
    {
        const string text = "log";
        const byte alpha = 180;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = Math.Clamp(Math.Min(width / 120, height / 42), 1, 4);
        var padding = checked(scale * 3);
        var glyphColumns = checked(text.Length * 5 + text.Length - 1);
        var boxWidth = checked(glyphColumns * scale + padding * 2);
        var boxHeight = checked(7 * scale + padding * 2);
        var margin = Math.Max(scale * 3, Math.Min(width, height) / 40);
        var boxX = margin;
        var boxY = Math.Max(0, height - margin - boxHeight);
        BlendRectangle(pixels, width, height, boxX, boxY, boxWidth, boxHeight, 0, 0, 0, alpha);

        var cursorX = boxX + padding;
        for (var index = 0; index < text.Length; index++)
        {
            var glyph = text[index] switch
            {
                'l' => WatermarkL,
                'o' => WatermarkO,
                'g' => WatermarkG,
                _ => TimerBlank
            };
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < glyph[row].Length; column++)
                {
                    if (glyph[row][column] != '1')
                    {
                        continue;
                    }

                    BlendRectangle(
                        pixels,
                        width,
                        height,
                        cursorX + column * scale,
                        boxY + padding + row * scale,
                        scale,
                        scale,
                        byte.MaxValue,
                        byte.MaxValue,
                        byte.MaxValue,
                        alpha);
                }
            }

            cursorX += checked(6 * scale);
        }
    }

    private static void BlendRectangle(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        int rectangleWidth,
        int rectangleHeight,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        if (alpha == 0 || rectangleWidth <= 0 || rectangleHeight <= 0)
        {
            return;
        }

        var left = Math.Clamp(x, 0, width);
        var top = Math.Clamp(y, 0, height);
        var right = Math.Clamp(checked(x + rectangleWidth), 0, width);
        var bottom = Math.Clamp(checked(y + rectangleHeight), 0, height);
        var inverseAlpha = byte.MaxValue - alpha;
        for (var row = top; row < bottom; row++)
        {
            var rowOffset = checked(row * width * 4);
            for (var column = left; column < right; column++)
            {
                var offset = checked(rowOffset + column * 4);
                pixels[offset] = BlendChannel(pixels[offset], blue, alpha, inverseAlpha);
                pixels[offset + 1] = BlendChannel(pixels[offset + 1], green, alpha, inverseAlpha);
                pixels[offset + 2] = BlendChannel(pixels[offset + 2], red, alpha, inverseAlpha);
                pixels[offset + 3] = byte.MaxValue;
            }
        }
    }

    private static byte BlendChannel(byte existing, byte overlay, byte alpha, int inverseAlpha)
    {
        return (byte)((existing * inverseAlpha + overlay * alpha + byte.MaxValue / 2) / byte.MaxValue);
    }

    private static string[] GetTimerGlyph(char character)
    {
        return character switch
        {
            >= '0' and <= '9' => TimerDigits[character - '0'],
            ':' => TimerColon,
            _ => TimerBlank
        };
    }

    private static readonly string[][] TimerDigits =
    [
        ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ["01110", "10001", "10001", "01111", "00001", "00010", "11100"]
    ];

    private static readonly string[] TimerColon =
    ["00000", "00100", "00100", "00000", "00100", "00100", "00000"];

    private static readonly string[] TimerBlank =
    ["00000", "00000", "00000", "00000", "00000", "00000", "00000"];

    private static readonly string[] WatermarkL =
    ["10000", "10000", "10000", "10000", "10000", "10000", "11111"];

    private static readonly string[] WatermarkO =
    ["01110", "10001", "10001", "10001", "10001", "10001", "01110"];

    private static readonly string[] WatermarkG =
    ["01110", "10001", "10000", "10111", "10001", "10001", "01110"];

    private static async Task<DecodedAsset> DecodeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = await StorageFile
            .GetFileFromPathAsync(path)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        using var stream = await file
            .OpenAsync(FileAccessMode.Read)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var decoder = await BitmapDecoder
            .CreateAsync(stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        using var bitmap = await decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The render asset has no pixels.");
        }

        var byteCount = checked(width * height * 4);
        var buffer = new WinBuffer((uint)byteCount)
        {
            Length = (uint)byteCount
        };
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[byteCount];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        return new DecodedAsset(width, height, pixels);
    }

    private static string ResolveAssetPath(string sessionDirectory, string assetReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference);
        var normalizedReference = assetReference.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(sessionDirectory, normalizedReference));
        var prefix = sessionDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? sessionDirectory
            : sessionDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A render asset reference escapes its session directory.");
        }

        EnsurePathNotReparsePoint(sessionDirectory, path);
        return path;
    }

    private static void EnsurePathNotReparsePoint(string sessionDirectory, string path)
    {
        EnsureNotReparsePoint(sessionDirectory);
        var relativePath = Path.GetRelativePath(sessionDirectory, path);
        if (relativePath == ".")
        {
            return;
        }

        var currentPath = sessionDirectory;
        foreach (var component in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            EnsureNotReparsePoint(currentPath);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(System.IO.FileAttributes.ReparsePoint))
            {
                throw new IOException("A render path cannot contain a reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class RenderAssetCache
    {
        private string? reference;
        private DecodedAsset? asset;

        public async Task<DecodedAsset?> GetAsync(
            string? assetReference,
            string sessionDirectory,
            CancellationToken cancellationToken)
        {
            if (assetReference is null)
            {
                return null;
            }

            if (asset is not null
                && string.Equals(reference, assetReference, StringComparison.Ordinal))
            {
                return asset;
            }

            var path = ResolveAssetPath(sessionDirectory, assetReference);
            var loaded = await DecodeAsync(path, cancellationToken).ConfigureAwait(false);
            reference = assetReference;
            asset = loaded;
            return loaded;
        }
    }

    private sealed record DecodedAsset(int Width, int Height, byte[] Pixels);
}
