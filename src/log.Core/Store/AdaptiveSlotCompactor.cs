using Log.Core.Timeline;

namespace Log.Core.Store;

/// <summary>
/// Applies the retained adaptive timeline to a screenshot manifest. When a
/// new cadence has targets that were not sampled exactly, the latest prior
/// asset is carried forward as an explicit duplicate.
/// </summary>
internal static class AdaptiveSlotCompactor
{
    public static bool Apply(
        SessionManifest manifest,
        long activeDurationTicks,
        long intervalTicks)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SamplingMode == SessionManifest.RandomSamplingMode)
        {
            throw new InvalidOperationException(
                "Random adaptive sessions retain sparse scheduled targets and cannot be compacted.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(activeDurationTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalTicks);
        if (activeDurationTicks > 0
            && intervalTicks != AdaptiveSamplingPolicy.GetIntervalTicks(activeDurationTicks))
        {
            throw new ArgumentException(
                "The compaction interval does not match the adaptive sampling policy.",
                nameof(intervalTicks));
        }

        var changed = manifest.SamplingMode != SessionManifest.AdaptiveSamplingMode
            || manifest.CaptureIntervalTicks != intervalTicks;
        manifest.SamplingMode = SessionManifest.AdaptiveSamplingMode;
        var targets = AdaptiveSamplingPolicy.BuildRetainedTargets(activeDurationTicks);

        if (manifest.SourceMode is "screen" or "camera")
        {
            var retained = ReprojectSlots(
                manifest.Slots,
                targets,
                track: manifest.SourceMode,
                manifest.CameraOffsetTicks,
                manifest.StartedAtTicks,
                manifest.Pauses);
            changed |= retained.Count != manifest.Slots.Count
                || retained.Where((slot, index) => slot != manifest.Slots[index]).Any();
            manifest.Slots.Clear();
            manifest.Slots.AddRange(retained);
        }
        else if (manifest.SourceMode == "screen+camera")
        {
            var retained = ReprojectTrackSlots(
                manifest.TrackSlots,
                targets,
                manifest.CameraOffsetTicks,
                manifest.StartedAtTicks,
                manifest.Pauses);
            changed |= retained.Count != manifest.TrackSlots.Count
                || retained.Where((slot, index) => slot != manifest.TrackSlots[index]).Any();
            manifest.TrackSlots.Clear();
            manifest.TrackSlots.AddRange(retained);
        }
        else
        {
            throw new InvalidDataException("The session manifest has an unsupported source mode.");
        }

        manifest.CaptureIntervalTicks = intervalTicks;
        return changed;
    }

    private static List<ManifestSlot> ReprojectSlots(
        IEnumerable<ManifestSlot> storedSlots,
        IReadOnlyList<long> targets,
        string track,
        long cameraOffsetTicks,
        long startedAtTicks,
        IReadOnlyList<PauseRange> pauses)
    {
        var byTarget = storedSlots
            .GroupBy(static slot => slot.TargetTicks)
            .ToDictionary(
                static group => group.Key,
                group => RequireIdenticalDuplicateFree(group, $"target {group.Key}"));
        var ordered = byTarget.OrderBy(static pair => pair.Key).ToArray();
        var retained = new List<ManifestSlot>(targets.Count);
        var sourceIndex = 0;
        ManifestSlot? latest = null;
        foreach (var (target, index) in targets.Select((target, index) => (target, index)))
        {
            while (sourceIndex < ordered.Length && ordered[sourceIndex].Key <= target)
            {
                latest = ordered[sourceIndex].Value;
                sourceIndex++;
            }

            if (latest is not null)
            {
                retained.Add(ReprojectSlot(
                    latest,
                    index,
                    target,
                    track == "camera" ? cameraOffsetTicks : 0,
                    TimelinePlanner.WallTargetForActiveTarget(startedAtTicks, target, pauses)));
            }
        }

        return retained;
    }

    private static List<ManifestTrackSlot> ReprojectTrackSlots(
        IEnumerable<ManifestTrackSlot> storedSlots,
        IReadOnlyList<long> targets,
        long cameraOffsetTicks,
        long startedAtTicks,
        IReadOnlyList<PauseRange> pauses)
    {
        var tracks = storedSlots
            .GroupBy(static slot => slot.Track, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => ReprojectTrack(
                    group,
                    targets,
                    group.Key == "camera" ? cameraOffsetTicks : 0,
                    startedAtTicks,
                    pauses),
                StringComparer.Ordinal);
        return tracks
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(static pair => pair.Value)
            .ToList();
    }

    private static List<ManifestTrackSlot> ReprojectTrack(
        IEnumerable<ManifestTrackSlot> storedSlots,
        IReadOnlyList<long> targets,
        long cameraOffsetTicks,
        long startedAtTicks,
        IReadOnlyList<PauseRange> pauses)
    {
        var byTarget = storedSlots
            .GroupBy(static slot => slot.TargetTicks)
            .ToDictionary(
                static group => group.Key,
                group => RequireIdenticalDuplicateFree(group, $"{group.First().Track} target {group.Key}"));
        var ordered = byTarget.OrderBy(static pair => pair.Key).ToArray();
        var retained = new List<ManifestTrackSlot>(targets.Count);
        var sourceIndex = 0;
        ManifestTrackSlot? latest = null;
        foreach (var (target, index) in targets.Select((target, index) => (target, index)))
        {
            while (sourceIndex < ordered.Length && ordered[sourceIndex].Key <= target)
            {
                latest = ordered[sourceIndex].Value;
                sourceIndex++;
            }

            if (latest is not null)
            {
                retained.Add(ReprojectTrackSlot(
                    latest,
                    index,
                    target,
                    cameraOffsetTicks,
                    TimelinePlanner.WallTargetForActiveTarget(startedAtTicks, target, pauses)));
            }
        }

        return retained;
    }

    private static ManifestSlot ReprojectSlot(
        ManifestSlot source,
        int index,
        long targetTicks,
        long cameraOffsetTicks,
        long wallTargetTicks)
    {
        if (source.TargetTicks == targetTicks)
        {
            return source with { Index = index };
        }

        return source with
        {
            Index = index,
            TargetTicks = targetTicks,
            IsDuplicate = source.SourceTicks >= 0,
            AgeTicks = CalculateAgeTicks(source.SourceTicks, wallTargetTicks, cameraOffsetTicks)
        };
    }

    private static ManifestTrackSlot ReprojectTrackSlot(
        ManifestTrackSlot source,
        int index,
        long targetTicks,
        long cameraOffsetTicks,
        long wallTargetTicks)
    {
        if (source.TargetTicks == targetTicks)
        {
            return source with { Index = index };
        }

        return source with
        {
            Index = index,
            TargetTicks = targetTicks,
            IsDuplicate = source.SourceTicks >= 0,
            AgeTicks = CalculateAgeTicks(source.SourceTicks, wallTargetTicks, cameraOffsetTicks)
        };
    }

    private static long CalculateAgeTicks(
        long sourceTicks,
        long targetTicks,
        long cameraOffsetTicks)
    {
        if (sourceTicks < 0)
        {
            return 0;
        }

        var alignedSourceTicks = checked(sourceTicks + cameraOffsetTicks);
        return alignedSourceTicks >= targetTicks
            ? 0
            : targetTicks - alignedSourceTicks;
    }

    private static ManifestSlot RequireIdenticalDuplicateFree(
        IEnumerable<ManifestSlot> group,
        string key)
    {
        var slots = group.ToArray();
        var first = slots[0];
        if (slots.Any(slot => slot != first))
        {
            throw new InvalidDataException($"The session manifest contains conflicting slots for {key}.");
        }

        return first;
    }

    private static ManifestTrackSlot RequireIdenticalDuplicateFree(
        IEnumerable<ManifestTrackSlot> group,
        string key)
    {
        var slots = group.ToArray();
        var first = slots[0];
        if (slots.Any(slot => slot != first))
        {
            throw new InvalidDataException($"The session manifest contains conflicting slots for {key}.");
        }

        return first;
    }

    public static IReadOnlySet<string> ReferencedSnapshotAssets(SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.SourceMode is "screen" or "camera")
        {
            foreach (var slot in manifest.Slots)
            {
                references.Add(slot.AssetReference.Replace('\\', '/'));
            }
        }
        else
        {
            foreach (var slot in manifest.TrackSlots)
            {
                references.Add(slot.AssetReference.Replace('\\', '/'));
            }
        }

        return references;
    }
}
