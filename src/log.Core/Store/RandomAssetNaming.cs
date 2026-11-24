namespace Log.Core.Store;

/// <summary>Names assets from the immutable target chosen for a random window.</summary>
internal static class RandomAssetNaming
{
    public static string For(string source, long targetTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);
        return $"{source}/slot-r-{targetTicks:D19}";
    }
}
