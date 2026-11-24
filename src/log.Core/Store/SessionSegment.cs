namespace Log.Core.Store;

public sealed record SessionSegment
{
    public SessionSegment(
        string track,
        int index,
        int firstSlotIndex,
        int slotCount,
        long startedAtTicks,
        long endedAtTicks,
        string assetReference,
        long byteLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(track);
        if (track is not ("screen" or "camera"))
        {
            throw new ArgumentException("Segment track must be screen or camera.", nameof(track));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(firstSlotIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(startedAtTicks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endedAtTicks, startedAtTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        ValidateAssetReference(assetReference);
        var normalizedAssetReference = assetReference.Replace('\\', '/');
        if (!normalizedAssetReference.StartsWith($"{track}/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"A {track} segment must be stored under the {track}/ directory.",
                nameof(assetReference));
        }

        Track = track;
        Index = index;
        FirstSlotIndex = firstSlotIndex;
        SlotCount = slotCount;
        StartedAtTicks = startedAtTicks;
        EndedAtTicks = endedAtTicks;
        AssetReference = assetReference;
        ByteLength = byteLength;
    }

    public string Track { get; }
    public int Index { get; }
    public int FirstSlotIndex { get; }
    public int SlotCount { get; }
    public long StartedAtTicks { get; }
    public long EndedAtTicks { get; }
    public string AssetReference { get; }
    public long ByteLength { get; }

    internal static void ValidateAssetReference(string assetReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference);
        if (Path.IsPathRooted(assetReference)
            || assetReference.Contains(':')
            || assetReference.Contains('\0'))
        {
            throw new ArgumentException("Segment asset references must be relative paths.", nameof(assetReference));
        }

        var normalized = assetReference.Replace('\\', '/');
        if (normalized.Split('/').Any(static part => part is "" or "." or "..")
            || !string.Equals(Path.GetExtension(normalized), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Segment asset references must be normalized relative .mp4 paths.",
                nameof(assetReference));
        }
    }
}
