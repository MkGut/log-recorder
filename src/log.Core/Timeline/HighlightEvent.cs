namespace Log.Core.Timeline;

public enum HighlightSource
{
    Screen,
    Camera,
    Both
}

public sealed record HighlightEvent
{
    public HighlightEvent(
        string id,
        long eventTicks,
        HighlightSource source,
        long holdTicks,
        bool included = true,
        string? screenAssetReference = null,
        string? cameraAssetReference = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(eventTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(holdTicks);
        Id = id;
        EventTicks = eventTicks;
        Source = source;
        HoldTicks = holdTicks;
        Included = included;
        ValidateAssetReference(screenAssetReference, nameof(screenAssetReference));
        ValidateAssetReference(cameraAssetReference, nameof(cameraAssetReference));
        ScreenAssetReference = screenAssetReference;
        CameraAssetReference = cameraAssetReference;
    }

    public string Id { get; }
    public long EventTicks { get; }
    public HighlightSource Source { get; }
    public long HoldTicks { get; }
    public bool Included { get; }
    public string? ScreenAssetReference { get; }
    public string? CameraAssetReference { get; }

    private static void ValidateAssetReference(string? assetReference, string parameterName)
    {
        if (assetReference is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference, parameterName);
        if (Path.IsPathRooted(assetReference)
            || assetReference.Contains(':')
            || assetReference.Contains('\0'))
        {
            throw new ArgumentException("Highlight asset references must be relative paths.", parameterName);
        }

        var normalized = assetReference.Replace('\\', '/');
        if (!normalized.StartsWith("highlights/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(static part => part is "" or "." or "..")
            || !string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Highlight asset references must be normalized relative .png paths under highlights/.",
                parameterName);
        }
    }
}
