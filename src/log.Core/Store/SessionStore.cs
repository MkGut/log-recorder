using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Timeline;

namespace Log.Core.Store;

public sealed class SessionStore : IAsyncDisposable
{

    private readonly FileStream journal;
    private readonly string manifestPath;
    private long nextSequence;

    private SessionStore(string directory, FileStream journal, SessionManifest manifest)
    {
        DirectoryPath = directory;
        this.journal = journal;
        manifestPath = Path.Combine(directory, "manifest.json");
        Manifest = manifest;
    }

    public string DirectoryPath { get; }
    public SessionManifest Manifest { get; }

    public static async Task<SessionStore> CreateAsync(
        string rootDirectory,
        string id,
        string sourceMode,
        long startedAtTicks,
        long captureIntervalTicks,
        long cameraOffsetTicks = 0,
        bool adaptiveSampling = false,
        bool randomSampling = false,
        long? randomSeed = null,
        string? groupId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        SessionIdPolicy.Validate(id);
        if (groupId is not null)
        {
            SessionIdPolicy.Validate(groupId);
        }
        if (sourceMode is not ("screen" or "camera" or "screen+camera"))
        {
            throw new ArgumentException("Source mode must be screen, camera, or screen+camera.", nameof(sourceMode));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(startedAtTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureIntervalTicks);
        if (adaptiveSampling && randomSampling)
        {
            throw new ArgumentException("A session cannot use both adaptive sampling modes.");
        }

        if (!randomSampling && randomSeed is not null)
        {
            throw new ArgumentException(
                "A random seed is only valid for random adaptive sampling.",
                nameof(randomSeed));
        }

        var fullRootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(fullRootDirectory);
        EnsureNotReparsePoint(fullRootDirectory, "The session root cannot be a reparse point.");
        var sessionsDirectory = Path.Combine(fullRootDirectory, "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        EnsureNotReparsePoint(sessionsDirectory, "The sessions directory cannot be a reparse point.");
        var directory = Path.Combine(sessionsDirectory, id);
        if (Directory.Exists(directory))
        {
            EnsureNotReparsePoint(directory, "The session directory cannot be a reparse point.");
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new IOException($"The session directory for '{id}' already contains data.");
            }
        }

        Directory.CreateDirectory(directory);
        var temporaryDirectory = Path.Combine(directory, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        EnsureNotReparsePoint(temporaryDirectory, "The session temporary directory cannot be a reparse point.");
        Directory.CreateDirectory(Path.Combine(directory, "highlights"));
        Directory.CreateDirectory(Path.Combine(directory, "thumbs"));
        if (sourceMode is "screen" or "screen+camera")
        {
            Directory.CreateDirectory(Path.Combine(directory, "screen"));
        }

        if (sourceMode is "camera" or "screen+camera")
        {
            Directory.CreateDirectory(Path.Combine(directory, "camera"));
        }

        var manifest = new SessionManifest
        {
            Id = id,
            GroupId = groupId,
            Name = id,
            SourceMode = sourceMode,
            StartedAtTicks = startedAtTicks,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CaptureIntervalTicks = captureIntervalTicks,
            SamplingMode = randomSampling
                ? SessionManifest.RandomSamplingMode
                : adaptiveSampling
                    ? SessionManifest.AdaptiveSamplingMode
                    : SessionManifest.FixedSamplingMode,
            RandomSampling = randomSampling
                ? new RandomSamplingSchedule
                {
                    Seed = randomSeed ?? RandomAdaptiveSamplingPolicy.CreateSeed(),
                    AlgorithmVersion = RandomAdaptiveSamplingPolicy.AlgorithmVersion
                }
                : null,
            CameraOffsetTicks = cameraOffsetTicks,
            Status = SessionStatus.Starting
        };
        if (sourceMode is "screen" or "screen+camera")
        {
            manifest.Tracks["screen"] = "screen/";
        }

        if (sourceMode is "camera" or "screen+camera")
        {
            manifest.Tracks["camera"] = "camera/";
        }

        var journalPath = Path.Combine(directory, "events.jsonl");
        var journal = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        try
        {
            var store = new SessionStore(directory, journal, manifest);
            await store.SaveManifestAsync(cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch
        {
            await journal.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<SessionStore> OpenAsync(
        string rootDirectory,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        SessionIdPolicy.Validate(id);

        var fullRootDirectory = Path.GetFullPath(rootDirectory);
        if (Directory.Exists(fullRootDirectory)
            && File.GetAttributes(fullRootDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The session root cannot be a reparse point.");
        }

        var sessionsDirectory = Path.Combine(fullRootDirectory, "sessions");
        if (Directory.Exists(sessionsDirectory)
            && File.GetAttributes(sessionsDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The sessions directory cannot be a reparse point.");
        }

        var directory = Path.Combine(sessionsDirectory, id);
        if (Directory.Exists(directory)
            && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The session directory cannot be a reparse point.");
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The session manifest was not found.", manifestPath);
        }
        if (File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The session manifest cannot be a reparse point.");
        }

        await using var manifestStream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);
        var manifest = await JsonSerializer.DeserializeAsync(
            manifestStream,
            ManifestJson.Default.SessionManifest,
            cancellationToken).ConfigureAwait(false);
        if (manifest is null
            || !string.Equals(manifest.Id, id, StringComparison.Ordinal)
            || manifest.Pauses is null
            || manifest.Tracks is null
            || manifest.Slots is null
            || manifest.TrackSlots is null
            || manifest.Segments is null
            || manifest.Highlights is null
            || manifest.PrunedTracks is null)
        {
            throw new InvalidDataException("The session manifest is invalid or belongs to another session.");
        }

        if (manifest.Highlights.Any(static highlight => highlight is null)
            || manifest.Highlights
                .Select(static highlight => highlight.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != manifest.Highlights.Count)
        {
            throw new InvalidDataException("The session manifest has null or duplicate highlights.");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(manifest.Name))
            {
                SessionNamePolicy.Normalize(manifest.Name);
            }

            SessionNamePolicy.NormalizeDescription(manifest.Description);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The session manifest has invalid metadata.", exception);
        }

        var expectedTracks = manifest.SourceMode switch
        {
            "screen" => new[] { "screen" },
            "camera" => new[] { "camera" },
            "screen+camera" => new[] { "screen", "camera" },
            _ => []
        };
        if (expectedTracks.Length == 0
            || manifest.Tracks.Count != expectedTracks.Length
            || expectedTracks.Any(track => !manifest.Tracks.TryGetValue(track, out var path)
                || !string.Equals(path, $"{track}/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The session manifest has an invalid track map.");
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
            try
            {
                manifest.RandomSampling?.Validate();
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    "The session manifest has invalid random sampling metadata.",
                    exception);
            }

            if (manifest.RandomSampling is null)
            {
                throw new InvalidDataException(
                    "A random sampling manifest must include its durable schedule.");
            }
        }
        else if (manifest.RandomSampling is not null)
        {
            throw new InvalidDataException(
                "Only random sampling manifests may include a random schedule.");
        }

        if (manifest.LastExportSettings is not null)
        {
            try
            {
                _ = manifest.LastExportSettings.ToRenderSettings();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException(
                    "The session manifest has invalid saved export settings.",
                    exception);
            }
        }

        var temporaryDirectory = Path.Combine(directory, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        EnsureNotReparsePoint(temporaryDirectory, "The session temporary directory cannot be a reparse point.");
        var journalPath = Path.Combine(directory, "events.jsonl");
        if (File.Exists(journalPath)
            && File.GetAttributes(journalPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The session journal cannot be a reparse point.");
        }

        var journal = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        try
        {
            var store = new SessionStore(directory, journal, manifest);
            var journalResult = await SessionJournalReader.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false);
            store.nextSequence = journalResult.Records.Count == 0
                ? 0
                : checked(journalResult.Records.Max(static record => record.Sequence) + 1);
            return store;
        }
        catch
        {
            await journal.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask AppendSlotAsync(
        ManifestSlot slot,
        FrameSample? sourceFrame = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        EnsureCaptureDataMutable();
        var track = Manifest.SourceMode;
        if (track is not ("screen" or "camera"))
        {
            throw new InvalidOperationException("A dual-source session must append independent track slots.");
        }

        ValidateSlotAssetReference(slot.AssetReference, track);
        ValidateSlot(slot.Index, slot.TargetTicks, slot.SourceTicks, slot.AgeTicks);
        ValidateSlotMetadata(slot.ArrivalTicks, slot.TimestampQuality, slot.SourceEpoch);
        if (Manifest.Slots.Any(existing => existing.Index == slot.Index))
        {
            throw new InvalidOperationException($"Slot {slot.Index} already exists in session '{Manifest.Id}'.");
        }

        var durableSlot = AttachFrameMetadata(slot, sourceFrame);
        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "slot",
            SlotIndex: durableSlot.Index,
            SourceTicks: durableSlot.SourceTicks,
            AssetReference: durableSlot.AssetReference,
            TargetTicks: durableSlot.TargetTicks,
            IsDuplicate: durableSlot.IsDuplicate,
            AgeTicks: durableSlot.AgeTicks,
            ArrivalTicks: durableSlot.ArrivalTicks,
            TimestampQuality: durableSlot.TimestampQuality,
            SourceEpoch: durableSlot.SourceEpoch), cancellationToken).ConfigureAwait(false);
        Manifest.Slots.Add(durableSlot);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendTrackSlotAsync(
        ManifestTrackSlot slot,
        FrameSample? sourceFrame = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        EnsureCaptureDataMutable();
        if (slot.Track is not ("screen" or "camera"))
        {
            throw new ArgumentException("Track must be screen or camera.", nameof(slot));
        }
        if (!Manifest.Tracks.ContainsKey(slot.Track))
        {
            throw new ArgumentException(
                $"Track '{slot.Track}' is not configured for session '{Manifest.Id}'.",
                nameof(slot));
        }

        ValidateSlotAssetReference(slot.AssetReference, slot.Track);
        ValidateSlot(slot.Index, slot.TargetTicks, slot.SourceTicks, slot.AgeTicks);
        ValidateSlotMetadata(slot.ArrivalTicks, slot.TimestampQuality, slot.SourceEpoch);
        if (Manifest.TrackSlots.Any(existing =>
                existing.Track == slot.Track && existing.Index == slot.Index))
        {
            throw new InvalidOperationException(
                $"Track slot {slot.Track}/{slot.Index} already exists in session '{Manifest.Id}'.");
        }

        var durableSlot = AttachFrameMetadata(slot, sourceFrame);
        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "track-slot",
            SlotIndex: durableSlot.Index,
            SourceTicks: durableSlot.SourceTicks,
            AssetReference: durableSlot.AssetReference,
            Track: durableSlot.Track,
            TargetTicks: durableSlot.TargetTicks,
            IsDuplicate: durableSlot.IsDuplicate,
            AgeTicks: durableSlot.AgeTicks,
            ArrivalTicks: durableSlot.ArrivalTicks,
            TimestampQuality: durableSlot.TimestampQuality,
            SourceEpoch: durableSlot.SourceEpoch), cancellationToken).ConfigureAwait(false);
        Manifest.TrackSlots.Add(durableSlot);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits the target chosen for one random-adaptive window before capture
    /// waits on it. Replaying this event restores the decision without rerolling.
    /// </summary>
    public async ValueTask<bool> AppendRandomSamplingWindowAsync(
        RandomSamplingWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        EnsureCaptureDataMutable();
        if (Manifest.SamplingMode != SessionManifest.RandomSamplingMode
            || Manifest.RandomSampling is null)
        {
            throw new InvalidOperationException(
                "Random sampling windows require a random-adaptive session.");
        }

        if (window.IntervalTicks != AdaptiveSamplingPolicy.GetIntervalTicks(window.StartTicks))
        {
            throw new ArgumentException(
                "The random sampling window does not match the adaptive cadence.",
                nameof(window));
        }

        Manifest.RandomSampling.Validate();
        var existing = Manifest.RandomSampling.FindWindow(window.StartTicks);
        if (existing is not null)
        {
            if (existing != window)
            {
                throw new InvalidOperationException(
                    $"Random sampling window {window.StartTicks} already has a different target.");
            }

            return false;
        }

        var expectedStartTicks = Manifest.RandomSampling.Windows
            .Select(static existingWindow => existingWindow.EndTicks)
            .DefaultIfEmpty(0)
            .Max();
        if (window.StartTicks != expectedStartTicks)
        {
            throw new InvalidOperationException(
                "Random sampling windows must be appended in active-time order.");
        }

        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "random-window",
            SlotIndex: -1,
            SourceTicks: 0,
            AssetReference: string.Empty,
            TargetTicks: window.TargetTicks,
            RandomWindowStartTicks: window.StartTicks,
            RandomWindowEndTicks: window.EndTicks,
            RandomWindowIntervalTicks: window.IntervalTicks,
            RandomWindowTargetTicks: window.TargetTicks,
            RandomSamplingSeed: Manifest.RandomSampling.Seed,
            RandomSamplingAlgorithmVersion: Manifest.RandomSampling.AlgorithmVersion),
            cancellationToken).ConfigureAwait(false);
        Manifest.RandomSampling.Windows.Add(window);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Coarsens the screenshot timeline at an adaptive stage boundary. The
    /// journal and manifest are durable before superseded files are removed;
    /// recovery can therefore finish the cleanup after an interrupted delete.
    /// </summary>
    public async ValueTask<bool> CompactAdaptiveSamplesAsync(
        long activeDurationTicks,
        CancellationToken cancellationToken = default)
    {
        EnsureCaptureDataMutable();
        if (Manifest.SamplingMode == SessionManifest.RandomSamplingMode)
        {
            throw new InvalidOperationException(
                "Random adaptive sessions retain sparse scheduled targets and cannot be compacted.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(activeDurationTicks);
        var intervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(
            activeDurationTicks);
        var changed = AdaptiveSlotCompactor.Apply(Manifest, activeDurationTicks, intervalTicks);
        if (!changed)
        {
            return false;
        }

        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "adaptive-compaction",
            SlotIndex: -1,
            SourceTicks: 0,
            AssetReference: string.Empty,
            ActiveDurationTicks: activeDurationTicks,
            SamplingIntervalTicks: intervalTicks), cancellationToken).ConfigureAwait(false);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
        DeleteUnreferencedSnapshotAssets(DirectoryPath, Manifest);
        return true;
    }

    public async ValueTask AppendFinalizedSegmentAsync(
        SessionSegment segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        EnsureCaptureDataMutable();
        if (!Manifest.Tracks.ContainsKey(segment.Track))
        {
            throw new ArgumentException(
                $"Track '{segment.Track}' is not configured for session '{Manifest.Id}'.",
                nameof(segment));
        }

        if (Manifest.Segments.Any(existing =>
                existing.Track == segment.Track && existing.Index == segment.Index))
        {
            throw new InvalidOperationException(
                $"Segment {segment.Track}/{segment.Index} already exists in session '{Manifest.Id}'.");
        }

        var segmentPath = ResolveSegmentPath(segment.AssetReference);
        if (!File.Exists(segmentPath))
        {
            throw new FileNotFoundException("The finalized segment file was not found.", segmentPath);
        }

        EnsurePathNotReparsePoint(
            DirectoryPath,
            segmentPath,
            "Reparse-point segment paths cannot be committed.");

        var actualLength = new FileInfo(segmentPath).Length;
        if (actualLength != segment.ByteLength)
        {
            throw new IOException(
                $"The finalized segment length was {actualLength} bytes, expected {segment.ByteLength}.");
        }

        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "segment",
            SlotIndex: segment.FirstSlotIndex,
            SourceTicks: segment.StartedAtTicks,
            AssetReference: segment.AssetReference,
            Track: segment.Track,
            SegmentIndex: segment.Index,
            SegmentSlotCount: segment.SlotCount,
            SegmentStartTicks: segment.StartedAtTicks,
            SegmentEndTicks: segment.EndedAtTicks,
            ByteLength: segment.ByteLength), cancellationToken).ConfigureAwait(false);
        Manifest.Segments.Add(segment);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (Manifest.Status != SessionStatus.Starting)
        {
            throw new InvalidOperationException("Only a starting session can enter recording state.");
        }

        Manifest.Status = SessionStatus.Recording;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendPauseRangeAsync(
        PauseRange pause,
        CancellationToken cancellationToken = default)
    {
        EnsureCaptureDataMutable();
        if (pause.StartTicks < Manifest.StartedAtTicks)
        {
            throw new ArgumentException("A pause cannot start before the session.", nameof(pause));
        }

        if (Manifest.Pauses.Any(existing =>
                pause.StartTicks < existing.EndTicks && existing.StartTicks < pause.EndTicks))
        {
            throw new InvalidOperationException("Pause ranges cannot overlap.");
        }

        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "pause",
            SlotIndex: -1,
            SourceTicks: pause.StartTicks,
            AssetReference: string.Empty,
            PauseStartTicks: pause.StartTicks,
            PauseEndTicks: pause.EndTicks), cancellationToken).ConfigureAwait(false);
        Manifest.Pauses.Add(pause);
        Manifest.Pauses.Sort(static (left, right) => left.StartTicks.CompareTo(right.StartTicks));
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask BeginFinalizingAsync(CancellationToken cancellationToken = default)
    {
        if (Manifest.Status != SessionStatus.Recording)
        {
            throw new InvalidOperationException("Only a recording session can enter finalizing state.");
        }

        Manifest.Status = SessionStatus.Finalizing;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateMetricsAsync(
        CaptureMetricsSnapshot metrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        Manifest.Metrics = metrics;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveLastExportSettingsAsync(
        RenderSettings renderSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderSettings);
        if (Manifest.Status is not (SessionStatus.Ready or SessionStatus.Exported or SessionStatus.Interrupted))
        {
            throw new InvalidOperationException(
                $"Only a stable session can save export settings; current state is '{Manifest.Status}'.");
        }

        Manifest.LastExportSettings = ExportSettingsSnapshot.FromRenderSettings(renderSettings);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(long stoppedAtTicks, CancellationToken cancellationToken = default)
    {
        EnsureCaptureDataMutable();
        ArgumentOutOfRangeException.ThrowIfLessThan(stoppedAtTicks, Manifest.StartedAtTicks);
        if (Manifest.Pauses.Any(pause => pause.EndTicks > stoppedAtTicks))
        {
            throw new ArgumentException("Pause ranges must end before the session stops.", nameof(stoppedAtTicks));
        }

        Manifest.StoppedAtTicks = stoppedAtTicks;
        Manifest.StoppedAtUtc = DateTimeOffset.UtcNow;
        Manifest.Status = SessionStatus.Ready;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MarkExportedAsync(CancellationToken cancellationToken = default)
    {
        if (Manifest.Status is not (SessionStatus.Ready or SessionStatus.Exported or SessionStatus.Interrupted))
        {
            throw new InvalidOperationException(
                $"Only a stable session can be marked exported; current state is '{Manifest.Status}'.");
        }

        Manifest.Status = SessionStatus.Exported;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MarkTrackPrunedAsync(
        string track,
        CancellationToken cancellationToken = default)
    {
        if (Manifest.Status is not (SessionStatus.Ready or SessionStatus.Exported or SessionStatus.Interrupted))
        {
            throw new InvalidOperationException(
                $"Only a stable session can prune raw tracks; current state is '{Manifest.Status}'.");
        }

        if (!Manifest.Tracks.ContainsKey(track))
        {
            throw new ArgumentException(
                $"Track '{track}' is not configured for session '{Manifest.Id}'.",
                nameof(track));
        }

        if (Manifest.PrunedTracks.Contains(track, StringComparer.Ordinal))
        {
            return;
        }

        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "track-pruned",
            SlotIndex: -1,
            SourceTicks: 0,
            AssetReference: string.Empty,
            Track: track), cancellationToken).ConfigureAwait(false);
        Manifest.PrunedTracks.Add(track);
        Manifest.PrunedTracks.Sort(StringComparer.Ordinal);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateMetadataAsync(
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (Manifest.Status == SessionStatus.Deleting)
        {
            throw new InvalidOperationException("A deleted session cannot be edited.");
        }

        Manifest.Name = SessionNamePolicy.Normalize(name);
        Manifest.Description = SessionNamePolicy.NormalizeDescription(description);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendHighlightAsync(
        HighlightEvent highlight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        if (Manifest.Highlights.Any(existing => string.Equals(existing.Id, highlight.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Highlight '{highlight.Id}' already exists in session '{Manifest.Id}'.");
        }

        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "highlight",
            SlotIndex: -1,
            SourceTicks: highlight.EventTicks,
            AssetReference: highlight.ScreenAssetReference ?? highlight.CameraAssetReference ?? string.Empty,
            EventId: highlight.Id,
            HighlightSource: highlight.Source.ToString().ToLowerInvariant(),
            HoldTicks: highlight.HoldTicks,
            Included: highlight.Included,
            ScreenAssetReference: highlight.ScreenAssetReference,
            CameraAssetReference: highlight.CameraAssetReference), cancellationToken).ConfigureAwait(false);
        Manifest.Highlights.Add(highlight);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateHighlightAsync(
        string highlightId,
        bool? included = null,
        long? holdTicks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highlightId);
        SessionIdPolicy.Validate(highlightId);
        if (holdTicks is long requestedHoldTicks)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(requestedHoldTicks);
        }

        var index = Manifest.Highlights.FindIndex(highlight => highlight.Id == highlightId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Highlight '{highlightId}' was not found in session '{Manifest.Id}'.");
        }

        var current = Manifest.Highlights[index];
        var updated = new HighlightEvent(
            current.Id,
            current.EventTicks,
            current.Source,
            holdTicks ?? current.HoldTicks,
            included ?? current.Included,
            current.ScreenAssetReference,
            current.CameraAssetReference);
        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "highlight-update",
            SlotIndex: -1,
            SourceTicks: updated.EventTicks,
            AssetReference: updated.ScreenAssetReference ?? updated.CameraAssetReference ?? string.Empty,
            EventId: updated.Id,
            HighlightSource: updated.Source.ToString().ToLowerInvariant(),
            HoldTicks: updated.HoldTicks,
            Included: updated.Included,
            ScreenAssetReference: updated.ScreenAssetReference,
            CameraAssetReference: updated.CameraAssetReference), cancellationToken).ConfigureAwait(false);
        Manifest.Highlights[index] = updated;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteHighlightAsync(
        string highlightId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(highlightId);
        SessionIdPolicy.Validate(highlightId);
        var index = Manifest.Highlights.FindIndex(highlight => highlight.Id == highlightId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Highlight '{highlightId}' was not found in session '{Manifest.Id}'.");
        }

        var deleted = Manifest.Highlights[index];
        await AppendJournalEventAsync(new SessionJournalRecord(
            Sequence: nextSequence++,
            Kind: "highlight-delete",
            SlotIndex: -1,
            SourceTicks: deleted.EventTicks,
            AssetReference: string.Empty,
            EventId: deleted.Id), cancellationToken).ConfigureAwait(false);
        Manifest.Highlights.RemoveAt(index);
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        Manifest.Status = SessionStatus.Interrupted;
        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await journal.DisposeAsync().ConfigureAwait(false);
    }

    private async Task SaveManifestAsync(CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(DirectoryPath, ".tmp", "manifest.json.tmp");
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, Manifest, ManifestJson.Default.SessionManifest, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private async Task AppendJournalEventAsync(SessionJournalRecord journalEvent, CancellationToken cancellationToken)
    {
        var unsigned = JsonSerializer.Serialize(journalEvent, PlainJson.Default.SessionJournalRecord);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unsigned)));
        var line = JsonSerializer.Serialize(journalEvent with { Checksum = checksum }, PlainJson.Default.SessionJournalRecord) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        await journal.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await journal.FlushAsync(cancellationToken).ConfigureAwait(false);
        journal.Flush(flushToDisk: true);
    }

    private string ResolveSegmentPath(string assetReference)
    {
        SessionSegment.ValidateAssetReference(assetReference);
        var sessionRoot = Path.GetFullPath(DirectoryPath);
        var candidate = Path.GetFullPath(Path.Combine(sessionRoot, assetReference));
        var allowedPrefix = sessionRoot.EndsWith(Path.DirectorySeparatorChar)
            ? sessionRoot
            : sessionRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The segment path escapes the session directory.");
        }

        return candidate;
    }

    private static void ValidateSlot(int index, long targetTicks, long sourceTicks, long ageTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceTicks, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(ageTicks);
    }

    private void EnsureCaptureDataMutable()
    {
        if (Manifest.Status is not (SessionStatus.Starting or SessionStatus.Recording or SessionStatus.Finalizing))
        {
            throw new InvalidOperationException(
                $"Session '{Manifest.Id}' is not accepting capture data in state '{Manifest.Status}'.");
        }
    }

    private static void ValidateSlotMetadata(
        long? arrivalTicks,
        string? timestampQuality,
        int sourceEpoch)
    {
        if (arrivalTicks is long recordedArrivalTicks)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(recordedArrivalTicks);
        }

        if (string.IsNullOrWhiteSpace(timestampQuality)
            || !Enum.TryParse<FrameTimestampQuality>(timestampQuality, ignoreCase: true, out var quality)
            || !Enum.IsDefined(quality))
        {
            throw new ArgumentException("Slot timestamp quality is invalid.", nameof(timestampQuality));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sourceEpoch);
    }

    private static ManifestSlot AttachFrameMetadata(ManifestSlot slot, FrameSample? sourceFrame)
    {
        return sourceFrame is null
            ? slot
            : slot with
            {
                ArrivalTicks = sourceFrame.ArrivalTimestampTicks,
                TimestampQuality = sourceFrame.TimestampQuality.ToString().ToLowerInvariant(),
                SourceEpoch = sourceFrame.SourceEpoch
            };
    }

    private static ManifestTrackSlot AttachFrameMetadata(ManifestTrackSlot slot, FrameSample? sourceFrame)
    {
        return sourceFrame is null
            ? slot
            : slot with
            {
                ArrivalTicks = sourceFrame.ArrivalTimestampTicks,
                TimestampQuality = sourceFrame.TimestampQuality.ToString().ToLowerInvariant(),
                SourceEpoch = sourceFrame.SourceEpoch
            };
    }

    private static void EnsureNotReparsePoint(string path, string message)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(message);
        }
    }

    internal static void EnsurePathNotReparsePoint(
        string sessionRoot,
        string path,
        string message)
    {
        if (HasReparsePointInPath(sessionRoot, path))
        {
            throw new IOException(message);
        }
    }

    internal static bool HasReparsePointInPath(string sessionRoot, string path)
    {
        var rootAttributes = File.GetAttributes(sessionRoot);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(sessionRoot, path);
        if (relativePath == ".")
        {
            return false;
        }

        var currentPath = sessionRoot;
        foreach (var component in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            if (File.Exists(currentPath) || Directory.Exists(currentPath))
            {
                if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static void DeleteUnreferencedSnapshotAssets(
        string sessionDirectory,
        SessionManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        var references = AdaptiveSlotCompactor.ReferencedSnapshotAssets(manifest);
        foreach (var track in new[] { "screen", "camera" })
        {
            var trackDirectory = Path.Combine(sessionDirectory, track);
            if (!Directory.Exists(trackDirectory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(trackDirectory, "slot-*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(sessionDirectory, path).Replace('\\', '/');
                var extension = Path.GetExtension(path);
                if (references.Contains(relative)
                    || extension.Length > 0 && !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    EnsurePathNotReparsePoint(
                        Path.GetFullPath(sessionDirectory),
                        path,
                        "Snapshot assets cannot be deleted through a reparse point.");
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A failed cleanup is safe: the manifest no longer points
                    // at the file, and the next recovery scan can retry it.
                }
                catch (UnauthorizedAccessException)
                {
                    // See the IOException case above.
                }
            }
        }
    }

    internal static void ValidateSlotAssetReference(string assetReference, string track)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference);
        if (Path.IsPathRooted(assetReference)
            || assetReference.Contains(':')
            || assetReference.Contains('\0'))
        {
            throw new ArgumentException("Slot asset references must be relative paths.", nameof(assetReference));
        }

        var normalized = assetReference.Replace('\\', '/');
        if (!normalized.StartsWith($"{track}/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new ArgumentException(
                $"Slot asset references must stay under the {track}/ directory.",
                nameof(assetReference));
        }
    }

}
