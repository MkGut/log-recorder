namespace Log.Core.Render;

public static class CompositionPlanner
{
    public static CompositionFrame PlanFrame(
        int slotIndex,
        CompositionSettings settings,
        bool hasScreenSource,
        bool hasCameraSource)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        ArgumentNullException.ThrowIfNull(settings);
        var output = settings.OutputSize.EvenAligned();
        var screen = new PixelRect(0, 0, output.Width, output.Height);
        PixelRect? camera = null;
        var mode = ResolveMode(settings, hasScreenSource, hasCameraSource);
        var cameraIsFullFrame = hasCameraSource && mode == CompositionMode.CameraOnly;
        var showScreen = hasScreenSource && mode != CompositionMode.CameraOnly;
        if (cameraIsFullFrame)
        {
            camera = screen;
        }
        else if (hasCameraSource
            && hasScreenSource
            && mode == CompositionMode.PictureInPicture
            && settings.Pip.Visible
            && settings.Pip.WidthFraction > 0)
        {
            var inset = Math.Min(
                EvenFloor(output.Width * settings.Pip.InsetFraction),
                Math.Max(0, Math.Min(output.Width, output.Height) / 2 - 1));
            var maxWidth = Math.Max(2, output.Width - 2 * inset);
            var maxHeight = Math.Max(2, output.Height - 2 * inset);
            var width = EvenAtLeastTwo(
                Math.Min(output.Width * settings.Pip.WidthFraction, maxWidth),
                maxWidth);
            var cameraAspectRatio = settings.CameraSourceSize is PixelSize cameraSize
                ? cameraSize.Width / (double)cameraSize.Height
                : 16d / 9d;
            var height = EvenAtLeastTwo(width / cameraAspectRatio, maxHeight);
            if (height == 0 || width == 0)
            {
                return new CompositionFrame(slotIndex, screen, null, settings.Pip.Mirror)
                {
                    ScreenVisible = showScreen,
                    CameraIsFullFrame = cameraIsFullFrame
                };
            }

            if (height > maxHeight)
            {
                height = EvenAtLeastTwo(maxHeight, maxHeight);
                width = EvenAtLeastTwo(Math.Min(width, height * cameraAspectRatio), maxWidth);
            }

            var x = settings.Pip.Corner is PipCorner.TopRight or PipCorner.BottomRight
                ? output.Width - inset - width
                : inset;
            var y = settings.Pip.Corner is PipCorner.BottomLeft or PipCorner.BottomRight
                ? output.Height - inset - height
                : inset;
            camera = new PixelRect(x, y, width, height);
        }

        return new CompositionFrame(slotIndex, screen, camera, settings.Pip.Mirror)
        {
            ScreenVisible = showScreen,
            CameraIsFullFrame = cameraIsFullFrame
        };
    }

    private static CompositionMode ResolveMode(
        CompositionSettings settings,
        bool hasScreenSource,
        bool hasCameraSource)
    {
        if (!hasScreenSource && hasCameraSource)
        {
            return CompositionMode.CameraOnly;
        }

        if (hasScreenSource && !hasCameraSource)
        {
            return CompositionMode.ScreenOnly;
        }

        if (!hasScreenSource && !hasCameraSource)
        {
            return CompositionMode.ScreenOnly;
        }

        return settings.Mode switch
        {
            CompositionMode.CameraOnly when hasCameraSource => CompositionMode.CameraOnly,
            CompositionMode.CameraOnly => CompositionMode.ScreenOnly,
            CompositionMode.PictureInPicture when settings.Pip.Visible => CompositionMode.PictureInPicture,
            CompositionMode.ScreenOnly => CompositionMode.ScreenOnly,
            _ => settings.Pip.Visible
                ? CompositionMode.PictureInPicture
                : CompositionMode.ScreenOnly
        };
    }

    private static int EvenFloor(double value)
    {
        var pixels = Math.Max(0, (int)Math.Floor(value));
        return pixels - pixels % 2;
    }

    private static int EvenAtLeastTwo(double value, int maximum)
    {
        var maximumEven = Math.Max(0, maximum - maximum % 2);
        if (maximumEven < 2)
        {
            return 0;
        }

        return Math.Clamp(EvenFloor(value), 2, maximumEven);
    }
}
