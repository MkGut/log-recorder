namespace Log.Core.Render;

public sealed record RenderFrameSources(
    string? ScreenAssetReference,
    string? CameraAssetReference,
    bool ScreenIsGap,
    bool CameraIsGap,
    bool ScreenIsDuplicate,
    bool CameraIsDuplicate,
    bool ScreenUsesHighlightAsset,
    bool CameraUsesHighlightAsset,
    long ScreenAgeTicks,
    long CameraAgeTicks)
{
    public static RenderFrameSources Empty { get; } = new(
        ScreenAssetReference: null,
        CameraAssetReference: null,
        ScreenIsGap: true,
        CameraIsGap: true,
        ScreenIsDuplicate: false,
        CameraIsDuplicate: false,
        ScreenUsesHighlightAsset: false,
        CameraUsesHighlightAsset: false,
        ScreenAgeTicks: 0,
        CameraAgeTicks: 0);
}
