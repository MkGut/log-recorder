using System.Text.Json;
using System.Text.Json.Serialization;
using Log.Core.Diagnostics;
using Log.Core.Timeline;

namespace Log.Core.Store;

public sealed record RecoveredSession(string Id, string DirectoryPath, SessionStatus PreviousStatus);
public sealed record RecoveryIssue(string DirectoryPath, string Reason);
public sealed record RecoveryReport(
    IReadOnlyList<RecoveredSession> Recovered,
    IReadOnlyList<RecoveryIssue> Issues);

public static class SessionRecovery
{

    public static async Task<IReadOnlyList<RecoveredSession>> RecoverAsync(
        string rootDirectory,
        CatalogStore? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var report = await RecoverWithReportAsync(rootDirectory, catalog, cancellationToken).ConfigureAwait(false);
        return report.Recovered;
    }

    public static async Task<RecoveryReport> RecoverWithReportAsync(
        string rootDirectory,
        CatalogStore? catalog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRootDirectory = Path.GetFullPath(rootDirectory);
        try
        {
            if (File.GetAttributes(fullRootDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                return new RecoveryReport(
                    [],
                    [new RecoveryIssue(fullRootDirectory, "root-reparse-point")]);
            }
        }
        catch (FileNotFoundException)
        {
            return new RecoveryReport([], []);
        }
        catch (DirectoryNotFoundException)
        {
            return new RecoveryReport([], []);
        }
        catch (UnauthorizedAccessException)
        {
            return new RecoveryReport(
                [],
                [new RecoveryIssue(fullRootDirectory, "root-access-denied")]);
        }
        catch (IOException)
        {
            return new RecoveryReport(
                [],
                [new RecoveryIssue(fullRootDirectory, "root-unreadable")]);
        }

        var recovered = new List<RecoveredSession>();
        var issues = new List<RecoveryIssue>();
        if (catalog is not null)
        {
            issues.AddRange(await GroupStorage.ReconcileAsync(
                fullRootDirectory,
                catalog,
                cancellationToken).ConfigureAwait(false));
        }

        var sessionsDirectory = Path.Combine(fullRootDirectory, "sessions");
        if (!Directory.Exists(sessionsDirectory))
        {
            return new RecoveryReport(recovered, issues);
        }

        try
        {
            if (File.GetAttributes(sessionsDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                issues.Add(new RecoveryIssue(sessionsDirectory, "sessions-reparse-point"));
                return new RecoveryReport(recovered, issues);
            }
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new RecoveryIssue(sessionsDirectory, "sessions-access-denied"));
            return new RecoveryReport(recovered, issues);
        }
        catch (IOException)
        {
            issues.Add(new RecoveryIssue(sessionsDirectory, "sessions-unreadable"));
            return new RecoveryReport(recovered, issues);
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(sessionsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes directoryAttributes;
                try
                {
                    directoryAttributes = File.GetAttributes(directory);
                }
                catch (UnauthorizedAccessException)
                {
                    issues.Add(new RecoveryIssue(directory, "session-access-denied"));
                    continue;
                }
                catch (IOException)
                {
                    issues.Add(new RecoveryIssue(directory, "session-unreadable"));
                    continue;
                }

                if (directoryAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    issues.Add(new RecoveryIssue(directory, "session-reparse-point"));
                    continue;
                }

                var manifestPath = Path.Combine(directory, "manifest.json");
                FileAttributes manifestAttributes;
                try
                {
                    manifestAttributes = File.GetAttributes(manifestPath);
                }
                catch (FileNotFoundException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-missing"));
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-missing"));
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-access-denied"));
                    continue;
                }
                catch (IOException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-unreadable"));
                    continue;
                }

                if (manifestAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-reparse-point"));
                    continue;
                }

                SessionManifest? manifest;
                try
                {
                    await using var stream = new FileStream(
                        manifestPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        4096,
                        useAsync: true);
                    manifest = await JsonSerializer.DeserializeAsync(stream, ManifestJson.Default.SessionManifest, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-invalid-json"));
                    continue;
                }
                catch (IOException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-unreadable"));
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-access-denied"));
                    continue;
                }

                if (manifest is null)
                {
                    issues.Add(new RecoveryIssue(directory, "manifest-empty"));
                    continue;
                }

                if (!IsValidManifest(directory, manifest, out var invalidReason))
                {
                    issues.Add(new RecoveryIssue(directory, invalidReason));
                    continue;
                }

                var journalReplay = await ReplayJournalAsync(
                    directory,
                    manifest,
                    issues,
                    cancellationToken).ConfigureAwait(false);
                var manifestChanged = journalReplay.Changed;
                var snapshotCleanupRequired = journalReplay.HasDurableCompaction;
                ValidateSegmentsOnDisk(directory, manifest, issues);
                if (manifest.UsesAdaptiveSampling
                    && manifest.SamplingMode != SessionManifest.AdaptiveSamplingMode)
                {
                    manifest.SamplingMode = SessionManifest.AdaptiveSamplingMode;
                    manifestChanged = true;
                }

                var needsRecovery = NeedsRecovery(manifest);
                var previousStatus = manifest.Status;
                if (needsRecovery)
                {
                    if (manifest.StoppedAtTicks is null)
                    {
                        manifest.StoppedAtTicks = InferRecoveryStopTicks(manifest);
                        if (manifest.StartedAtUtc is DateTimeOffset startedAtUtc)
                        {
                            try
                            {
                                manifest.StoppedAtUtc = startedAtUtc.AddTicks(
                                    manifest.StoppedAtTicks.Value - manifest.StartedAtTicks);
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                issues.Add(new RecoveryIssue(directory, "recovery-stop-utc-unavailable"));
                            }
                        }
                    }

                    if (manifest.UsesAdaptiveSampling && manifest.StoppedAtTicks is long recoveredStopTicks)
                    {
                        try
                        {
                            var activeDurationTicks = TimelinePlanner.ActiveDuration(
                                checked(recoveredStopTicks - manifest.StartedAtTicks),
                                manifest.StartedAtTicks,
                                recoveredStopTicks,
                                manifest.Pauses);
                            var compacted = AdaptiveSlotCompactor.Apply(
                                manifest,
                                activeDurationTicks,
                                AdaptiveSamplingPolicy.GetIntervalTicks(activeDurationTicks));
                            manifestChanged |= compacted;
                            snapshotCleanupRequired |= compacted;
                        }
                        catch (ArgumentException)
                        {
                            issues.Add(new RecoveryIssue(directory, "adaptive-recovery-compaction-invalid"));
                        }
                        catch (InvalidDataException)
                        {
                            issues.Add(new RecoveryIssue(directory, "adaptive-recovery-compaction-invalid"));
                        }
                    }

                    manifest.Status = SessionStatus.Interrupted;
                    manifestChanged = true;
                }

                if (manifestChanged)
                {
                    try
                    {
                        await WriteManifestAsync(manifestPath, directory, manifest, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        issues.Add(new RecoveryIssue(directory, "manifest-rewrite-failed"));
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        issues.Add(new RecoveryIssue(directory, "manifest-rewrite-access-denied"));
                        continue;
                    }
                }

                if (snapshotCleanupRequired)
                {
                    try
                    {
                        // The manifest is committed before this cleanup. If the
                        // process dies while deleting, the durable compaction retries it.
                        SessionStore.DeleteUnreferencedSnapshotAssets(directory, manifest);
                    }
                    catch (IOException)
                    {
                        issues.Add(new RecoveryIssue(directory, "snapshot-cleanup-failed"));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        issues.Add(new RecoveryIssue(directory, "snapshot-cleanup-access-denied"));
                    }
                }

                if (catalog is not null)
                {
                    try
                    {
                        await catalog.UpsertSessionAsync(manifest, directory, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException)
                    {
                        issues.Add(new RecoveryIssue(directory, "catalog-update-failed"));
                    }
                }

                // The catalog upsert above already ran for every session, recovered or not.
                if (needsRecovery)
                {
                    CaptureEventSource.Instance.RecoveryFinished(
                        manifest.Id,
                        manifest.Status.ToString());
                    recovered.Add(new RecoveredSession(manifest.Id, directory, previousStatus));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new RecoveryIssue(sessionsDirectory, "sessions-access-denied"));
        }
        catch (IOException)
        {
            issues.Add(new RecoveryIssue(sessionsDirectory, "sessions-unreadable"));
        }

        return new RecoveryReport(recovered, issues);
    }

    private static async Task<(bool Changed, bool HasDurableCompaction)> ReplayJournalAsync(
        string directory,
        SessionManifest manifest,
        List<RecoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        var journalPath = Path.Combine(directory, "events.jsonl");
        if (!File.Exists(journalPath))
        {
            return (false, false);
        }

        SessionJournalReadResult journal;
        try
        {
            if (File.GetAttributes(journalPath).HasFlag(FileAttributes.ReparsePoint))
            {
                issues.Add(new RecoveryIssue(directory, "journal-reparse-point"));
                return (false, false);
            }

            journal = await SessionJournalReader.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            issues.Add(new RecoveryIssue(directory, "journal-unreadable"));
            return (false, false);
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new RecoveryIssue(directory, "journal-access-denied"));
            return (false, false);
        }

        if (journal.InvalidRecordCount > 0)
        {
            issues.Add(new RecoveryIssue(directory, "journal-checksum-failed"));
        }

        if (journal.HasTruncatedFinalRecord)
        {
            issues.Add(new RecoveryIssue(directory, "journal-truncated-final-record"));
        }

        var changed = false;
        var hasDurableCompaction = false;
        foreach (var record in journal.Records.OrderBy(static record => record.Sequence))
        {
            if (record.Kind == "slot")
            {
                if (record.TargetTicks is not long targetTicks
                    || record.IsDuplicate is not bool isDuplicate
                    || record.AgeTicks is not long ageTicks
                    || record.SlotIndex < 0
                    || targetTicks < 0
                    || targetTicks > long.MaxValue - manifest.CaptureIntervalTicks
                    || record.SourceTicks < -1
                    || ageTicks < 0
                    || manifest.SourceMode is not ("screen" or "camera"))
                {
                    continue;
                }

                if (!IsValidSlotMetadata(
                        record.ArrivalTicks,
                        record.TimestampQuality ?? "unknown",
                        record.SourceEpoch ?? 0))
                {
                    continue;
                }

                try
                {
                    SessionStore.ValidateSlotAssetReference(record.AssetReference, manifest.SourceMode);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var candidate = new ManifestSlot(
                    record.SlotIndex,
                    targetTicks,
                    record.SourceTicks,
                    isDuplicate,
                    ageTicks,
                    record.AssetReference)
                {
                    ArrivalTicks = record.ArrivalTicks,
                    TimestampQuality = record.TimestampQuality ?? "unknown",
                    SourceEpoch = record.SourceEpoch ?? 0
                };
                var existingByTarget = manifest.Slots.FirstOrDefault(slot => slot.TargetTicks == targetTicks);
                if (existingByTarget is not null)
                {
                    if (!EquivalentExceptIndex(existingByTarget, candidate))
                    {
                        issues.Add(new RecoveryIssue(directory, $"conflicting-slot-target-{targetTicks}"));
                    }
                }
                else if (manifest.Slots.All(slot => slot.Index != record.SlotIndex))
                {
                    manifest.Slots.Add(candidate);
                    changed = true;
                }
            }
            else if (record.Kind == "track-slot")
            {
                if (record.Track is not ("screen" or "camera")
                    || record.TargetTicks is not long targetTicks
                    || record.IsDuplicate is not bool isDuplicate
                    || record.AgeTicks is not long ageTicks
                    || record.SlotIndex < 0
                    || targetTicks < 0
                    || targetTicks > long.MaxValue - manifest.CaptureIntervalTicks
                    || record.SourceTicks < -1
                    || ageTicks < 0
                    || !manifest.Tracks.ContainsKey(record.Track))
                {
                    continue;
                }

                if (!IsValidSlotMetadata(
                        record.ArrivalTicks,
                        record.TimestampQuality ?? "unknown",
                        record.SourceEpoch ?? 0))
                {
                    continue;
                }

                try
                {
                    SessionStore.ValidateSlotAssetReference(record.AssetReference, record.Track);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var candidate = new ManifestTrackSlot(
                    record.Track,
                    record.SlotIndex,
                    targetTicks,
                    record.SourceTicks,
                    isDuplicate,
                    ageTicks,
                    record.AssetReference)
                {
                    ArrivalTicks = record.ArrivalTicks,
                    TimestampQuality = record.TimestampQuality ?? "unknown",
                    SourceEpoch = record.SourceEpoch ?? 0
                };
                var existingByTarget = manifest.TrackSlots.FirstOrDefault(slot =>
                    slot.Track == record.Track && slot.TargetTicks == targetTicks);
                if (existingByTarget is not null)
                {
                    if (!EquivalentExceptIndex(existingByTarget, candidate))
                    {
                        issues.Add(new RecoveryIssue(directory, $"conflicting-track-slot-target-{record.Track}-{targetTicks}"));
                    }
                }
                else if (manifest.TrackSlots.All(slot => slot.Track != record.Track || slot.Index != record.SlotIndex))
                {
                    manifest.TrackSlots.Add(candidate);
                    changed = true;
                }
            }
            else if (record.Kind == "random-window"
                && manifest.SamplingMode == SessionManifest.RandomSamplingMode
                && manifest.RandomSampling is not null
                && record.RandomWindowStartTicks is long randomWindowStartTicks
                && record.RandomWindowEndTicks is long randomWindowEndTicks
                && record.RandomWindowIntervalTicks is long randomWindowIntervalTicks
                && record.RandomWindowTargetTicks is long randomWindowTargetTicks
                && record.RandomSamplingSeed is long randomSamplingSeed
                && record.RandomSamplingAlgorithmVersion is string randomAlgorithmVersion
                && record.TargetTicks == randomWindowTargetTicks)
            {
                try
                {
                    if (randomSamplingSeed != manifest.RandomSampling.Seed
                        || !string.Equals(
                            randomAlgorithmVersion,
                            manifest.RandomSampling.AlgorithmVersion,
                            StringComparison.Ordinal))
                    {
                        issues.Add(new RecoveryIssue(directory, "random-window-seed-mismatch"));
                        continue;
                    }

                    var window = new RandomSamplingWindow(
                        randomWindowStartTicks,
                        randomWindowEndTicks,
                        randomWindowIntervalTicks,
                        randomWindowTargetTicks);
                    var existing = manifest.RandomSampling.FindWindow(window.StartTicks);
                    if (existing is not null)
                    {
                        if (existing != window)
                        {
                            issues.Add(new RecoveryIssue(
                                directory,
                                $"conflicting-random-window-{window.StartTicks}"));
                        }
                    }
                    else
                    {
                        var expectedStartTicks = manifest.RandomSampling.Windows
                            .Select(static existingWindow => existingWindow.EndTicks)
                            .DefaultIfEmpty(0)
                            .Max();
                        if (window.StartTicks == expectedStartTicks
                            && window.IntervalTicks == AdaptiveSamplingPolicy.GetIntervalTicks(window.StartTicks))
                        {
                            manifest.RandomSampling.Windows.Add(window);
                            changed = true;
                        }
                        else
                        {
                            issues.Add(new RecoveryIssue(directory, "random-window-order-invalid"));
                        }
                    }
                }
                catch (ArgumentException)
                {
                    issues.Add(new RecoveryIssue(directory, "random-window-invalid"));
                }
                catch (OverflowException)
                {
                    issues.Add(new RecoveryIssue(directory, "random-window-invalid"));
                }
            }
            else if (record.Kind == "highlight")
            {
                try
                {
                    var highlight = CreateHighlightFromJournal(record);
                    if (highlight is not null
                        && manifest.Highlights.All(existing => existing.Id != highlight.Id))
                    {
                        manifest.Highlights.Add(highlight);
                        changed = true;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            else if (record.Kind == "highlight-update")
            {
                try
                {
                    var updated = CreateHighlightFromJournal(record);
                    var index = updated is null
                        ? -1
                        : manifest.Highlights.FindIndex(highlight => highlight.Id == updated.Id);
                    if (updated is not null && index >= 0 && manifest.Highlights[index] != updated)
                    {
                        manifest.Highlights[index] = updated;
                        changed = true;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            else if (record.Kind == "highlight-delete"
                && !string.IsNullOrWhiteSpace(record.EventId))
            {
                var index = manifest.Highlights.FindIndex(highlight => highlight.Id == record.EventId);
                if (index >= 0)
                {
                    manifest.Highlights.RemoveAt(index);
                    changed = true;
                }
            }
            else if (record.Kind == "segment"
                && record.Track is ("screen" or "camera")
                && record.SegmentIndex is int segmentIndex
                && record.SegmentSlotCount is int slotCount
                && record.SegmentStartTicks is long segmentStartTicks
                && record.SegmentEndTicks is long segmentEndTicks
                && record.ByteLength is long byteLength
                && record.SlotIndex >= 0
                && slotCount > 0
                && segmentStartTicks >= 0
                && segmentEndTicks > segmentStartTicks
                && byteLength >= 0
                && manifest.Segments.All(segment =>
                    segment.Track != record.Track || segment.Index != segmentIndex))
            {
                try
                {
                    manifest.Segments.Add(new SessionSegment(
                        record.Track,
                        segmentIndex,
                        record.SlotIndex,
                        slotCount,
                        segmentStartTicks,
                        segmentEndTicks,
                        record.AssetReference,
                        byteLength));
                    changed = true;
                }
                catch (ArgumentException)
                {
                }
            }
            else if (record.Kind == "pause"
                && record.PauseStartTicks is long pauseStartTicks
                && record.PauseEndTicks is long pauseEndTicks
                && pauseStartTicks >= manifest.StartedAtTicks
                && pauseEndTicks >= pauseStartTicks
                && manifest.Pauses.All(pause =>
                    pause.StartTicks != pauseStartTicks || pause.EndTicks != pauseEndTicks))
            {
                var pause = new Log.Core.Timeline.PauseRange(pauseStartTicks, pauseEndTicks);
                if (manifest.Pauses.All(existing =>
                        pause.StartTicks >= existing.EndTicks || existing.StartTicks >= pause.EndTicks))
                {
                    manifest.Pauses.Add(pause);
                    changed = true;
                }
            }
            else if (record.Kind == "track-pruned"
                && record.Track is "screen" or "camera"
                && manifest.Tracks.ContainsKey(record.Track)
                && !manifest.PrunedTracks.Contains(record.Track, StringComparer.Ordinal))
            {
                manifest.PrunedTracks.Add(record.Track);
                manifest.PrunedTracks.Sort(StringComparer.Ordinal);
                changed = true;
            }
            else if (record.Kind == "adaptive-compaction"
                && manifest.SamplingMode != SessionManifest.RandomSamplingMode
                && record.ActiveDurationTicks is long activeDurationTicks
                && record.SamplingIntervalTicks is long samplingIntervalTicks
                && activeDurationTicks >= 0
                && samplingIntervalTicks > 0)
            {
                try
                {
                    changed |= AdaptiveSlotCompactor.Apply(
                        manifest,
                        activeDurationTicks,
                        samplingIntervalTicks);
                    hasDurableCompaction = true;
                }
                catch (ArgumentException)
                {
                    issues.Add(new RecoveryIssue(directory, "adaptive-compaction-invalid"));
                }
                catch (InvalidDataException)
                {
                    issues.Add(new RecoveryIssue(directory, "adaptive-compaction-invalid"));
                }
            }
        }

        if (changed)
        {
            manifest.Slots.Sort(static (left, right) => left.Index.CompareTo(right.Index));
            manifest.TrackSlots.Sort(static (left, right) =>
            {
                var indexComparison = left.Index.CompareTo(right.Index);
                return indexComparison != 0
                    ? indexComparison
                    : string.CompareOrdinal(left.Track, right.Track);
            });
            manifest.Segments.Sort(static (left, right) =>
            {
                var trackComparison = string.CompareOrdinal(left.Track, right.Track);
                return trackComparison != 0
                    ? trackComparison
                    : left.Index.CompareTo(right.Index);
            });
            manifest.Pauses.Sort(static (left, right) => left.StartTicks.CompareTo(right.StartTicks));
        }

        return (changed, hasDurableCompaction);
    }

    // Compaction changes slot indexes while journal replay still contains the
    // pre-compaction records. Target and payload identify the durable sample.
    private static bool EquivalentExceptIndex(ManifestSlot left, ManifestSlot right)
    {
        return left == (right with { Index = left.Index });
    }

    private static bool EquivalentExceptIndex(ManifestTrackSlot left, ManifestTrackSlot right)
    {
        return left == (right with { Index = left.Index });
    }

    private static HighlightEvent? CreateHighlightFromJournal(SessionJournalRecord record)
    {
        if (record.EventId is null
            || record.HighlightSource is null
            || record.HoldTicks is not long holdTicks
            || holdTicks < 0
            || record.SourceTicks < 0
            || !Enum.TryParse<HighlightSource>(record.HighlightSource, true, out var source))
        {
            return null;
        }

        var screenAsset = record.ScreenAssetReference;
        var cameraAsset = record.CameraAssetReference;
        if (string.IsNullOrWhiteSpace(screenAsset)
            && source is HighlightSource.Screen or HighlightSource.Both
            && record.AssetReference is not null
            && record.AssetReference.StartsWith("highlights/", StringComparison.OrdinalIgnoreCase))
        {
            screenAsset = record.AssetReference;
        }

        if (string.IsNullOrWhiteSpace(cameraAsset)
            && source == HighlightSource.Camera
            && record.AssetReference is not null
            && record.AssetReference.StartsWith("highlights/", StringComparison.OrdinalIgnoreCase))
        {
            cameraAsset = record.AssetReference;
        }

        return new HighlightEvent(
            record.EventId,
            record.SourceTicks,
            source,
            holdTicks,
            record.Included ?? true,
            screenAsset,
            cameraAsset);
    }

    private static void ValidateSegmentsOnDisk(
        string directory,
        SessionManifest manifest,
        List<RecoveryIssue> issues)
    {
        var sessionRoot = Path.GetFullPath(directory);
        foreach (var segment in manifest.Segments)
        {
            try
            {
                SessionSegment.ValidateAssetReference(segment.AssetReference);
                var candidate = Path.GetFullPath(Path.Combine(sessionRoot, segment.AssetReference));
                var allowedPrefix = sessionRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? sessionRoot
                    : sessionRoot + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new RecoveryIssue(directory, "segment-path-invalid"));
                }
                else if (!File.Exists(candidate))
                {
                    issues.Add(new RecoveryIssue(directory, "segment-missing"));
                }
                else if (SessionStore.HasReparsePointInPath(sessionRoot, candidate))
                {
                    issues.Add(new RecoveryIssue(directory, "segment-reparse-point"));
                }
                else if (new FileInfo(candidate).Length != segment.ByteLength)
                {
                    issues.Add(new RecoveryIssue(directory, "segment-length-mismatch"));
                }
            }
            catch (ArgumentException)
            {
                issues.Add(new RecoveryIssue(directory, "segment-path-invalid"));
            }
            catch (IOException)
            {
                issues.Add(new RecoveryIssue(directory, "segment-path-unreadable"));
            }
            catch (UnauthorizedAccessException)
            {
                issues.Add(new RecoveryIssue(directory, "segment-access-denied"));
            }
        }
    }

    private static bool IsValidManifest(
        string directory,
        SessionManifest manifest,
        out string reason)
    {
        var directoryId = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (!string.Equals(manifest.Id, directoryId, StringComparison.Ordinal))
        {
            reason = "manifest-id-mismatch";
            return false;
        }

        if (manifest.Pauses is null
            || manifest.Tracks is null
            || manifest.Slots is null
            || manifest.TrackSlots is null
            || manifest.Segments is null
            || manifest.Highlights is null
            || manifest.PrunedTracks is null
            || manifest.Slots.Any(static slot => slot is null)
            || manifest.TrackSlots.Any(static slot => slot is null))
        {
            reason = "manifest-collections-invalid";
            return false;
        }

        try
        {
            SessionIdPolicy.Validate(manifest.Id);
        }
        catch (ArgumentException)
        {
            reason = "manifest-id-invalid";
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(manifest.Name))
            {
                SessionNamePolicy.Normalize(manifest.Name);
            }

            SessionNamePolicy.NormalizeDescription(manifest.Description);
        }
        catch (ArgumentException)
        {
            reason = "manifest-metadata-invalid";
            return false;
        }

        if (manifest.SourceMode is not ("screen" or "camera" or "screen+camera"))
        {
            reason = "manifest-source-invalid";
            return false;
        }

        if (manifest.SamplingMode is not (SessionManifest.FixedSamplingMode
            or SessionManifest.AdaptiveSamplingMode
            or SessionManifest.RandomSamplingMode))
        {
            reason = "manifest-sampling-mode-invalid";
            return false;
        }

        if (manifest.SamplingMode == SessionManifest.RandomSamplingMode)
        {
            if (manifest.RandomSampling is null)
            {
                reason = "manifest-random-schedule-missing";
                return false;
            }

            try
            {
                manifest.RandomSampling.Validate();
            }
            catch (InvalidDataException)
            {
                reason = "manifest-random-schedule-invalid";
                return false;
            }
        }
        else if (manifest.RandomSampling is not null)
        {
            reason = "manifest-random-schedule-unexpected";
            return false;
        }

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
            reason = "manifest-tracks-invalid";
            return false;
        }

        if (manifest.PrunedTracks.Any(track => !manifest.Tracks.ContainsKey(track))
            || manifest.PrunedTracks.Distinct(StringComparer.Ordinal).Count() != manifest.PrunedTracks.Count)
        {
            reason = "manifest-pruned-tracks-invalid";
            return false;
        }

        if (manifest.LastExportSettings is not null)
        {
            try
            {
                _ = manifest.LastExportSettings.ToRenderSettings();
            }
            catch (ArgumentException)
            {
                reason = "manifest-export-settings-invalid";
                return false;
            }
            catch (InvalidDataException)
            {
                reason = "manifest-export-settings-invalid";
                return false;
            }
        }

        if (manifest.Highlights.Any(static highlight => highlight is null)
            || manifest.Highlights
                .Select(static highlight => highlight.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != manifest.Highlights.Count)
        {
            reason = "manifest-highlights-invalid";
            return false;
        }

        if (!string.Equals(manifest.Clock, "qpc-100ns", StringComparison.Ordinal)
            || manifest.StartedAtTicks < 0
            || manifest.CaptureIntervalTicks <= 0
            || (manifest.StoppedAtTicks is long stoppedAt && stoppedAt < manifest.StartedAtTicks)
            || (manifest.StartedAtUtc is DateTimeOffset startedAtUtc
                && manifest.StoppedAtUtc is DateTimeOffset stoppedAtUtc
                && stoppedAtUtc < startedAtUtc))
        {
            reason = "manifest-timing-invalid";
            return false;
        }

        try
        {
            if (manifest.SourceMode is "screen" or "camera")
            {
                foreach (var slot in manifest.Slots)
                {
                    if (slot.Index < 0
                        || slot.TargetTicks < 0
                        || slot.TargetTicks > long.MaxValue - manifest.CaptureIntervalTicks
                        || slot.SourceTicks < -1
                        || slot.AgeTicks < 0
                        || slot.SourceTicks < 0 && slot.IsDuplicate)
                    {
                        reason = "manifest-slot-timing-invalid";
                        return false;
                    }

                    SessionStore.ValidateSlotAssetReference(slot.AssetReference, manifest.SourceMode);
                    if (!IsValidSlotMetadata(slot.ArrivalTicks, slot.TimestampQuality, slot.SourceEpoch))
                    {
                        reason = "manifest-slot-metadata-invalid";
                        return false;
                    }
                }
            }

            foreach (var slot in manifest.TrackSlots)
            {
                if (!manifest.Tracks.ContainsKey(slot.Track))
                {
                    reason = "manifest-track-slot-invalid";
                    return false;
                }

                if (slot.Index < 0
                    || slot.TargetTicks < 0
                    || slot.TargetTicks > long.MaxValue - manifest.CaptureIntervalTicks
                    || slot.SourceTicks < -1
                    || slot.AgeTicks < 0
                    || slot.SourceTicks < 0 && slot.IsDuplicate)
                {
                    reason = "manifest-slot-timing-invalid";
                    return false;
                }

                SessionStore.ValidateSlotAssetReference(slot.AssetReference, slot.Track);
                if (!IsValidSlotMetadata(slot.ArrivalTicks, slot.TimestampQuality, slot.SourceEpoch))
                {
                    reason = "manifest-slot-metadata-invalid";
                    return false;
                }
            }
        }
        catch (ArgumentException)
        {
            reason = "manifest-slot-reference-invalid";
            return false;
        }

        var orderedPauses = manifest.Pauses.OrderBy(static pause => pause.StartTicks).ToArray();
        for (var index = 0; index < orderedPauses.Length; index++)
        {
            var pause = orderedPauses[index];
            if (pause.StartTicks < manifest.StartedAtTicks
                || (manifest.StoppedAtTicks is long pauseStoppedAt && pause.EndTicks > pauseStoppedAt)
                || (index > 0 && pause.StartTicks < orderedPauses[index - 1].EndTicks))
            {
                reason = "manifest-pauses-invalid";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsValidSlotMetadata(
        long? arrivalTicks,
        string? timestampQuality,
        int sourceEpoch)
    {
        return (arrivalTicks is null or >= 0)
            && sourceEpoch >= 0
            && !string.IsNullOrWhiteSpace(timestampQuality)
            && Enum.TryParse<Log.Core.Capture.FrameTimestampQuality>(
                timestampQuality,
                ignoreCase: true,
                out var quality)
            && Enum.IsDefined(quality);
    }

    private static bool NeedsRecovery(SessionManifest manifest)
    {
        return manifest.Status is SessionStatus.Starting or SessionStatus.Recording or SessionStatus.Finalizing
            || manifest.Status == SessionStatus.Interrupted && manifest.StoppedAtTicks is null;
    }

    private static long InferRecoveryStopTicks(SessionManifest manifest)
    {
        // A forced termination has no committed stop event. Use the end of the
        // last durable sample, then include any completed pause so the recovered
        // timeline remains renderable and stable on later scans.
        var latestActiveTarget = manifest.Slots
            .Select(static slot => slot.TargetTicks)
            .Concat(manifest.TrackSlots.Select(static slot => slot.TargetTicks))
            .DefaultIfEmpty(0)
            .Max();
        var hasSlots = manifest.Slots.Count > 0 || manifest.TrackSlots.Count > 0;
        var activeStop = !hasSlots
            ? 0
            : checked(latestActiveTarget + (manifest.UsesRandomSampling
                ? 1
                : manifest.CaptureIntervalTicks));
        var wallStop = TimelinePlanner.WallTargetForActiveTarget(
            manifest.StartedAtTicks,
            activeStop,
            manifest.Pauses);
        return Math.Max(
            wallStop,
            manifest.Pauses.Select(static pause => pause.EndTicks).DefaultIfEmpty(manifest.StartedAtTicks).Max());
    }

    private static async Task WriteManifestAsync(
        string manifestPath,
        string directory,
        SessionManifest manifest,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(directory, ".tmp");
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, "manifest.recovery.tmp");
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, ManifestJson.Default.SessionManifest, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, manifestPath, overwrite: true);
    }
}
