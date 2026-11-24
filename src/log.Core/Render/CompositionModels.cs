using System.Text.Json.Serialization;

namespace Log.Core.Render;

public enum CameraMirror
{
    None,
    Horizontal
}

/// <summary>
/// Render-time source selection. Adaptive capture owns sampling cadence; this
/// setting only controls how the retained tracks are composed at export.
/// </summary>
public enum CompositionMode
{
    Auto,
    ScreenOnly,
    CameraOnly,
    PictureInPicture
}

public readonly record struct PixelSize
{
    [JsonConstructor]
    public PixelSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }

    public PixelSize EvenAligned()
    {
        return new PixelSize(
            checked(Width + Width % 2),
            checked(Height + Height % 2));
    }
}

public readonly record struct PixelRect
{
    public PixelRect(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}

public sealed record PipLayout
{
    public PipLayout(
        bool visible,
        double widthFraction,
        double insetFraction,
        PipCorner corner,
        CameraMirror mirror = CameraMirror.Horizontal)
    {
        if (!double.IsFinite(widthFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(widthFraction));
        }

        if (!double.IsFinite(insetFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(insetFraction));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(widthFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(widthFraction, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(insetFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(insetFraction, 0.5);
        if (widthFraction + 2 * insetFraction > 1)
        {
            throw new ArgumentException(
                "The picture-in-picture width and insets must fit inside the output.",
                nameof(widthFraction));
        }
        if (visible && (widthFraction < 0.10 || widthFraction > 0.40))
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthFraction),
                "A visible camera overlay must be between 10% and 40% of the output width.");
        }
        Visible = visible;
        WidthFraction = widthFraction;
        InsetFraction = insetFraction;
        Corner = corner;
        Mirror = mirror;
    }

    public bool Visible { get; }
    public double WidthFraction { get; }
    public double InsetFraction { get; }
    public PipCorner Corner { get; }
    public CameraMirror Mirror { get; }
}

public enum PipCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public sealed record CompositionSettings
{
    private PixelSize? cameraSourceSize;

    public CompositionSettings(
        PixelSize outputSize,
        PipLayout pip,
        int outputFramesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(pip);
        ValidatePixelSize(outputSize, nameof(outputSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputFramesPerSecond);
        OutputSize = outputSize;
        Pip = pip;
        OutputFramesPerSecond = outputFramesPerSecond;
    }

    public PixelSize OutputSize { get; }
    public PipLayout Pip { get; }
    public int OutputFramesPerSecond { get; }
    public CompositionMode Mode { get; set; } = CompositionMode.Auto;
    public PixelSize? CameraSourceSize
    {
        get => cameraSourceSize;
        init
        {
            if (value is PixelSize size)
            {
                ValidatePixelSize(size, nameof(CameraSourceSize));
            }

            cameraSourceSize = value;
        }
    }

    private static void ValidatePixelSize(PixelSize size, string parameterName)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Pixel dimensions must be positive.");
        }
    }
}

public sealed record TimerSettings
{
    public TimerSettings(bool enabled, double opacity = 0.9, bool includePausedTime = false)
    {
        if (!double.IsFinite(opacity))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(opacity, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opacity, 1);
        Enabled = enabled;
        Opacity = opacity;
        IncludePausedTime = includePausedTime;
    }

    public bool Enabled { get; }
    public double Opacity { get; }
    public bool IncludePausedTime { get; }
}

public sealed record RenderSettings
{
    public RenderSettings(
        CompositionSettings composition,
        TimerSettings? timer = null,
        bool watermarkEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (composition.OutputFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(composition),
                "Output frame rate must be positive.");
        }

        Composition = composition;
        Timer = timer ?? new TimerSettings(enabled: false);
        WatermarkEnabled = watermarkEnabled;
    }

    public CompositionSettings Composition { get; }
    public TimerSettings Timer { get; }
    public bool WatermarkEnabled { get; }
}

public sealed record CompositionFrame(
    int SlotIndex,
    PixelRect ScreenDestination,
    PixelRect? CameraDestination,
    CameraMirror CameraMirror)
{
    public bool ScreenVisible { get; set; } = true;
    public bool CameraIsFullFrame { get; init; }
}
