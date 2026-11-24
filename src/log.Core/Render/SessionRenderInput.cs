using Log.Core.Store;
using Log.Core.Timeline;

namespace Log.Core.Render;

public sealed record RenderAssetSlot(
    TimelineSlot Slot,
    string? ScreenAssetReference,
    string? CameraAssetReference,
    bool ScreenIsGap,
    bool CameraIsGap,
    bool ScreenIsDuplicate,
    bool CameraIsDuplicate)
{
    public long ScreenAgeTicks { get; init; }
    public long CameraAgeTicks { get; init; }
}

public sealed record SessionRenderInput(
    string SessionId,
    string SourceMode,
    IReadOnlyList<RenderAssetSlot> Slots,
    IReadOnlyList<HighlightEvent> Highlights,
    IReadOnlyList<PauseRange> Pauses)
{
    public long SessionStopTicks { get; init; }
}

public static class SessionRenderInputBuilder
{
    public static SessionRenderInput Build(SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SourceMode is not ("screen" or "camera" or "screen+camera"))
        {
            throw new InvalidDataException("The session manifest has an unsupported source mode.");
        }

        if (manifest.Pauses is null
            || manifest.Tracks is null
            || manifest.Slots is null
            || manifest.TrackSlots is null
            || manifest.Highlights is null
            || manifest.PrunedTracks is null)
        {
            throw new InvalidDataException("The session manifest has incomplete render collections.");
        }

        if (manifest.Slots.Any(static slot => slot is null)
            || manifest.TrackSlots.Any(static slot => slot is null))
        {
            throw new InvalidDataException("The session manifest has incomplete render slots.");
        }

        if (manifest.PrunedTracks.Any(track => !manifest.Tracks.ContainsKey(track))
            || manifest.PrunedTracks.Distinct(StringComparer.Ordinal).Count() != manifest.PrunedTracks.Count)
        {
            throw new InvalidDataException("The session manifest has invalid pruned-track metadata.");
        }

        if (manifest.SamplingMode is not (SessionManifest.FixedSamplingMode
            or SessionManifest.AdaptiveSamplingMode
            or SessionManifest.RandomSamplingMode))
        {
            throw new InvalidDataException("The session manifest has an invalid sampling mode.");
        }

        if (manifest.SamplingMode == SessionManifest.RandomSamplingMode)
        {
            if (manifest.RandomSampling is null)
            {
                throw new InvalidDataException(
                    "A random sampling manifest must include its durable schedule.");
            }

            manifest.RandomSampling.Validate();
        }
        else if (manifest.RandomSampling is not null)
        {
            throw new InvalidDataException(
                "Only random sampling manifests may include a random schedule.");
        }

        ValidateTrackShape(manifest);

        if (manifest.StoppedAtTicks is not long stoppedAtTicks)
        {
            throw new InvalidOperationException("A render input requires a committed session stop time.");
        }

        var targetBasedSampling = manifest.UsesAdaptiveSampling || manifest.UsesRandomSampling;
        IReadOnlyList<TimelineSlot> slots;
        try
        {
            slots = targetBasedSampling
                ? BuildRecordedTimelineSlots(manifest, stoppedAtTicks)
                : TimelinePlanner.BuildSlots(
                    manifest.StartedAtTicks,
                    stoppedAtTicks,
                    manifest.CaptureIntervalTicks,
                    manifest.Pauses);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The session manifest has invalid timeline settings.", exception);
        }

        RenderAssetSlot[] renderSlots;
        try
        {
            renderSlots = manifest.SourceMode == "screen+camera"
                ? BuildDualSlots(manifest, slots, targetBasedSampling)
                : BuildSingleSlots(manifest, slots, targetBasedSampling);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The session manifest contains duplicate render slots.", exception);
        }

        if (manifest.UsesRandomSampling
            && (manifest.SourceMode == "screen+camera"
                ? manifest.TrackSlots.Count == 0
                : manifest.Slots.Count == 0))
        {
            throw new InvalidOperationException(
                "Random adaptive capture ended before its first scheduled sample; export is unavailable.");
        }

        return new SessionRenderInput(
            manifest.Id,
            manifest.SourceMode,
            renderSlots,
            manifest.Highlights.ToArray(),
            manifest.Pauses.ToArray())
        {
            SessionStopTicks = stoppedAtTicks
        };
    }

    private static TimelineSlot[] BuildRecordedTimelineSlots(
        SessionManifest manifest,
        long stoppedAtTicks)
    {
        var activeDurationTicks = TimelinePlanner.ActiveDuration(
            checked(stoppedAtTicks - manifest.StartedAtTicks),
            manifest.StartedAtTicks,
            stoppedAtTicks,
            manifest.Pauses);
        var storedTargets = manifest.SourceMode == "screen+camera"
            ? manifest.TrackSlots.Select(static slot => slot.TargetTicks)
            : manifest.Slots.Select(static slot => slot.TargetTicks);
        var targets = manifest.UsesRandomSampling
            ? BuildRandomTimelineTargets(manifest, activeDurationTicks, storedTargets)
            : storedTargets
                .Distinct()
                .OrderBy(static target => target)
                .ToArray();
        if (targets.Any(target => target < 0 || target >= activeDurationTicks))
        {
            throw new InvalidDataException("The recorded sampling manifest contains a target outside the active timeline.");
        }

        return targets
            .Select((target, index) => new TimelineSlot(
                index,
                target,
                TimelinePlanner.WallTargetForActiveTarget(
                    manifest.StartedAtTicks,
                    target,
                    manifest.Pauses)))
            .ToArray();
    }

    private static long[] BuildRandomTimelineTargets(
        SessionManifest manifest,
        long activeDurationTicks,
        IEnumerable<long> storedTargets)
    {
        var schedule = manifest.RandomSampling
            ?? throw new InvalidDataException("The random sampling schedule is missing.");
        var scheduledTargets = schedule.Windows
            .Where(window => window.TargetTicks < activeDurationTicks)
            .Select(static window => window.TargetTicks)
            .ToHashSet();
        foreach (var storedTarget in storedTargets)
        {
            if (!scheduledTargets.Contains(storedTarget))
            {
                throw new InvalidDataException(
                    $"The random manifest contains a target that was not scheduled: {storedTarget}.");
            }
        }

        return scheduledTargets.OrderBy(static target => target).ToArray();
    }

    private static RenderAssetSlot[] BuildSingleSlots(
        SessionManifest manifest,
        IReadOnlyList<TimelineSlot> slots,
        bool targetBasedSampling)
    {
        var sourceMode = manifest.SourceMode;
        var sourcePruned = manifest.PrunedTracks.Contains(sourceMode, StringComparer.Ordinal);
        var byIndex = targetBasedSampling
            ? null
            : BuildUniqueSingleSlots(manifest.Slots, slots.Count);
        var byTarget = targetBasedSampling
            ? BuildUniqueSingleSlotsByTarget(manifest.Slots, slots)
            : null;
        var renderSlots = new RenderAssetSlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            ManifestSlot? storedSlot = null;
            if (targetBasedSampling)
            {
                byTarget!.TryGetValue(slots[index].ActiveTargetTicks, out storedSlot);
            }
            else
            {
                byIndex!.TryGetValue(index, out storedSlot);
            }

            if (storedSlot is not null)
            {
                ValidateTargetTicks(storedSlot.TargetTicks, slots[index]);
                ValidateStoredSlot(storedSlot.SourceTicks, storedSlot.AgeTicks, storedSlot.IsDuplicate);
            }

            var isGap = sourcePruned || storedSlot is null || storedSlot.SourceTicks < 0;
            var assetReference = isGap
                ? null
                : ValidateAssetReference(storedSlot!.AssetReference, sourceMode);
            renderSlots[index] = sourceMode == "screen"
                ? new RenderAssetSlot(
                    slots[index],
                    assetReference,
                    null,
                    isGap,
                    CameraIsGap: true,
                    !sourcePruned && storedSlot?.IsDuplicate == true,
                    CameraIsDuplicate: false)
                {
                    ScreenAgeTicks = storedSlot?.AgeTicks ?? 0
                }
                : new RenderAssetSlot(
                    slots[index],
                    null,
                    assetReference,
                    ScreenIsGap: true,
                    isGap,
                    ScreenIsDuplicate: false,
                    !sourcePruned && storedSlot?.IsDuplicate == true)
                {
                    CameraAgeTicks = storedSlot?.AgeTicks ?? 0
                };
        }

        return renderSlots;
    }

    private static RenderAssetSlot[] BuildDualSlots(
        SessionManifest manifest,
        IReadOnlyList<TimelineSlot> slots,
        bool targetBasedSampling)
    {
        var byTrackAndIndex = targetBasedSampling
            ? null
            : BuildUniqueTrackSlots(manifest.TrackSlots, slots.Count);
        var byTrackAndTarget = targetBasedSampling
            ? BuildUniqueTrackSlotsByTarget(manifest.TrackSlots, slots)
            : null;
        var renderSlots = new RenderAssetSlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            ManifestTrackSlot? screenSlot = null;
            ManifestTrackSlot? cameraSlot = null;
            if (targetBasedSampling)
            {
                byTrackAndTarget!.TryGetValue(("screen", slots[index].ActiveTargetTicks), out screenSlot);
                byTrackAndTarget.TryGetValue(("camera", slots[index].ActiveTargetTicks), out cameraSlot);
            }
            else
            {
                byTrackAndIndex!.TryGetValue(("screen", index), out screenSlot);
                byTrackAndIndex.TryGetValue(("camera", index), out cameraSlot);
            }

            if (screenSlot is not null)
            {
                ValidateTargetTicks(screenSlot.TargetTicks, slots[index]);
                ValidateStoredSlot(screenSlot.SourceTicks, screenSlot.AgeTicks, screenSlot.IsDuplicate);
            }

            if (cameraSlot is not null)
            {
                ValidateTargetTicks(cameraSlot.TargetTicks, slots[index]);
                ValidateStoredSlot(cameraSlot.SourceTicks, cameraSlot.AgeTicks, cameraSlot.IsDuplicate);
            }

            var screenIsPruned = manifest.PrunedTracks.Contains("screen", StringComparer.Ordinal);
            var cameraIsPruned = manifest.PrunedTracks.Contains("camera", StringComparer.Ordinal);
            var screenIsGap = screenIsPruned || screenSlot is null || screenSlot.SourceTicks < 0;
            var cameraIsGap = cameraIsPruned || cameraSlot is null || cameraSlot.SourceTicks < 0;
            renderSlots[index] = new RenderAssetSlot(
                slots[index],
                screenIsGap ? null : ValidateAssetReference(screenSlot!.AssetReference, "screen"),
                cameraIsGap ? null : ValidateAssetReference(cameraSlot!.AssetReference, "camera"),
                screenIsGap,
                cameraIsGap,
                !screenIsPruned && screenSlot?.IsDuplicate == true,
                !cameraIsPruned && cameraSlot?.IsDuplicate == true)
            {
                ScreenAgeTicks = screenIsPruned ? 0 : screenSlot?.AgeTicks ?? 0,
                CameraAgeTicks = cameraIsPruned ? 0 : cameraSlot?.AgeTicks ?? 0
            };
        }

        return renderSlots;
    }

    private static void ValidateTrackShape(SessionManifest manifest)
    {
        var expectedTracks = manifest.SourceMode switch
        {
            "screen" => new[] { "screen" },
            "camera" => new[] { "camera" },
            _ => new[] { "screen", "camera" }
        };
        if (manifest.Tracks.Count != expectedTracks.Length
            || expectedTracks.Any(track => !manifest.Tracks.TryGetValue(track, out var path)
                || !string.Equals(path, $"{track}/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The session manifest has invalid track declarations.");
        }

        if (manifest.SourceMode == "screen+camera")
        {
            if (manifest.Slots.Count != 0
                || manifest.TrackSlots.Any(slot => slot.Track is not ("screen" or "camera")))
            {
                throw new InvalidDataException("A dual-source session must use screen and camera track slots.");
            }
        }
        else if (manifest.TrackSlots.Count != 0)
        {
            throw new InvalidDataException("A single-source session cannot contain dual track slots.");
        }
    }

    private static void ValidateTargetTicks(long storedTargetTicks, TimelineSlot expectedSlot)
    {
        if (storedTargetTicks != expectedSlot.ActiveTargetTicks)
        {
            throw new InvalidDataException("The session manifest slot target does not match the semantic timeline.");
        }
    }

    private static void ValidateStoredSlot(long sourceTicks, long ageTicks, bool isDuplicate)
    {
        if (sourceTicks < -1 || ageTicks < 0 || (sourceTicks < 0 && isDuplicate))
        {
            throw new InvalidDataException("The session manifest contains invalid slot timing metadata.");
        }
    }

    private static int ValidateSlotIndex(int index, int slotCount)
    {
        if (index < 0 || index >= slotCount)
        {
            throw new InvalidDataException("The session manifest contains a slot outside the committed timeline.");
        }

        return index;
    }

    private static Dictionary<int, ManifestSlot> BuildUniqueSingleSlots(
        IEnumerable<ManifestSlot> storedSlots,
        int slotCount)
    {
        var result = new Dictionary<int, ManifestSlot>();
        foreach (var slot in storedSlots)
        {
            var index = ValidateSlotIndex(slot.Index, slotCount);
            if (result.TryGetValue(index, out var existing))
            {
                if (existing != slot)
                {
                    throw new InvalidDataException(
                        $"The session manifest contains conflicting render slots for index {index}.");
                }

                // Older adaptive manifests could repeat an identical record
                // during a compaction boundary. It is safe to read once.
                continue;
            }

            result.Add(index, slot);
        }

        return result;
    }

    private static Dictionary<long, ManifestSlot> BuildUniqueSingleSlotsByTarget(
        IEnumerable<ManifestSlot> storedSlots,
        IReadOnlyList<TimelineSlot> slots)
    {
        var validTargets = slots
            .Select(static slot => slot.ActiveTargetTicks)
            .ToHashSet();
        var result = new Dictionary<long, ManifestSlot>();
        foreach (var slot in storedSlots)
        {
            if (!validTargets.Contains(slot.TargetTicks))
            {
                throw new InvalidDataException(
                    $"The adaptive manifest contains a slot outside the committed timeline at target {slot.TargetTicks}.");
            }

            if (result.TryGetValue(slot.TargetTicks, out var existing))
            {
                if (existing with { Index = 0 } != slot with { Index = 0 })
                {
                    throw new InvalidDataException(
                        $"The adaptive manifest contains conflicting render slots for target {slot.TargetTicks}.");
                }

                continue;
            }

            result.Add(slot.TargetTicks, slot);
        }

        return result;
    }

    private static Dictionary<(string Track, int Index), ManifestTrackSlot> BuildUniqueTrackSlots(
        IEnumerable<ManifestTrackSlot> storedSlots,
        int slotCount)
    {
        var result = new Dictionary<(string Track, int Index), ManifestTrackSlot>();
        foreach (var slot in storedSlots)
        {
            var key = (slot.Track, ValidateSlotIndex(slot.Index, slotCount));
            if (result.TryGetValue(key, out var existing))
            {
                if (existing != slot)
                {
                    throw new InvalidDataException(
                        $"The session manifest contains conflicting render slots for {slot.Track}/{slot.Index}.");
                }

                continue;
            }

            result.Add(key, slot);
        }

        return result;
    }

    private static Dictionary<(string Track, long Target), ManifestTrackSlot> BuildUniqueTrackSlotsByTarget(
        IEnumerable<ManifestTrackSlot> storedSlots,
        IReadOnlyList<TimelineSlot> slots)
    {
        var validTargets = slots
            .Select(static slot => slot.ActiveTargetTicks)
            .ToHashSet();
        var result = new Dictionary<(string Track, long Target), ManifestTrackSlot>();
        foreach (var slot in storedSlots)
        {
            if (!validTargets.Contains(slot.TargetTicks))
            {
                throw new InvalidDataException(
                    $"The adaptive manifest contains a slot outside the committed timeline at target {slot.TargetTicks}.");
            }

            var key = (slot.Track, slot.TargetTicks);
            if (result.TryGetValue(key, out var existing))
            {
                if (existing with { Index = 0 } != slot with { Index = 0 })
                {
                    throw new InvalidDataException(
                        $"The adaptive manifest contains conflicting render slots for {slot.Track} target {slot.TargetTicks}.");
                }

                continue;
            }

            result.Add(key, slot);
        }

        return result;
    }

    private static string ValidateAssetReference(string assetReference, string track)
    {
        try
        {
            SessionStore.ValidateSlotAssetReference(assetReference, track);
            return assetReference.Replace('\\', '/');
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The session manifest contains an invalid slot asset reference.", exception);
        }
    }
}
