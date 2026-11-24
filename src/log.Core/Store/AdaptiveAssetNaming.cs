namespace Log.Core.Store;

/// <summary>Names assets written after adaptive slot compaction.</summary>
internal static class AdaptiveAssetNaming
{
    public static string For(string source, long targetTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);
        return $"{source}/slot-a-{targetTicks:D19}";
    }
}
