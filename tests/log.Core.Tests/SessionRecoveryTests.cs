using Log.Core.Store;
using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Timeline;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionRecoveryTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task DeserializesTheStoreManifestTrackMap()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (await SessionStore.CreateAsync(root, "roundtrip", "screen", 100, 10))
            {
            }

            var json = await File.ReadAllTextAsync(Path.Combine(root, "sessions", "roundtrip", "manifest.json"));
            var manifest = JsonSerializer.Deserialize<SessionManifest>(json, ManifestJsonOptions);

            Assert.NotNull(manifest);
            Assert.Equal("screen", manifest.SourceMode);
            Assert.Equal("screen/", manifest.Tracks["screen"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplayingACompactedJournalIsIdempotent()
    {
        const long second = TimelineConstants.TicksPerSecond;
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "adaptive", "screen", 0, second))
            {
                await store.StartRecordingAsync();
                for (var index = 0; index < 61; index++)
                {
                    await store.AppendSlotAsync(new ManifestSlot(
                        index,
                        index * second,
                        index * second,
                        false,
                        0,
                        $"screen/slot-{index:D6}"));
                }

                await store.CompactAdaptiveSamplesAsync(60 * second);
                await store.CompleteAsync(61 * second);
            }

            // Reproduce the old failure state: the compacted snapshot already
            // contains a repeated identical record, while the journal still
            // contains all pre-compaction indexes.
            var manifestPath = Path.Combine(root, "sessions", "adaptive", "manifest.json");
            var manifestJson = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var slotsJson = manifestJson["slots"]!.AsArray();
            slotsJson.Add(slotsJson[0]!.DeepClone());
            await File.WriteAllTextAsync(manifestPath, manifestJson.ToJsonString());

            for (var pass = 0; pass < 3; pass++)
            {
                var report = await SessionRecovery.RecoverWithReportAsync(root);
                Assert.DoesNotContain(report.Issues, issue => issue.Reason.StartsWith("conflicting-slot", StringComparison.Ordinal));

                await using var store = await SessionStore.OpenAsync(root, "adaptive");
                var slots = store.Manifest.Slots;
                Assert.Equal(slots.Count, slots.Select(static slot => slot.Index).Distinct().Count());
                Assert.Equal(slots.Count, slots.Select(static slot => slot.TargetTicks).Distinct().Count());
                Assert.Equal(30, slots.Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MarksUnfinishedManifestInterruptedAndLeavesReadySessionsAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "recording", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
            }

            await using (var readyStore = await SessionStore.CreateAsync(root, "ready", "screen", 100, 10))
            {
                await readyStore.StartRecordingAsync();
                await readyStore.CompleteAsync(110);
            }

            var recovered = await SessionRecovery.RecoverAsync(root);

            var item = Assert.Single(recovered);
            Assert.Equal("recording", item.Id);
            Assert.Equal(SessionStatus.Recording, item.PreviousStatus);

            var manifestJson = await File.ReadAllTextAsync(Path.Combine(root, "sessions", "recording", "manifest.json"));
            Assert.Contains("\"status\": \"Interrupted\"", manifestJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FixedSessionRecoveryKeepsUnreferencedSlotPngs()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "fixed", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                var referencedPath = Path.Combine(store.DirectoryPath, "screen", "slot-000000.png");
                Directory.CreateDirectory(Path.GetDirectoryName(referencedPath)!);
                await File.WriteAllBytesAsync(referencedPath, [1]);
                await store.AppendSlotAsync(new ManifestSlot(0, 0, 100, false, 0, "screen/slot-000000.png"));
                await store.CompleteAsync(110);
            }

            var orphanPath = Path.Combine(root, "sessions", "fixed", "screen", "slot-orphan.png");
            await File.WriteAllBytesAsync(orphanPath, [2]);

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Empty(report.Issues);
            Assert.True(File.Exists(orphanPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoversInterruptedDualSessionWithStableStopAndRepeatsSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "dual", "screen+camera", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 0, 0, 0, false, 0, "screen/slot-000000.png"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "camera", 0, 0, 0, false, 0, "camera/slot-000000.png"));
            }

            var first = await SessionRecovery.RecoverWithReportAsync(root);
            Assert.Contains(first.Recovered, session => session.Id == "dual");
            Assert.Empty(first.Issues);

            await using (var recovered = await SessionStore.OpenAsync(root, "dual"))
            {
                Assert.Equal(SessionStatus.Interrupted, recovered.Manifest.Status);
                Assert.Equal(10, recovered.Manifest.StoppedAtTicks);
                var input = SessionRenderInputBuilder.Build(recovered.Manifest);
                var slot = Assert.Single(input.Slots);
                Assert.Equal("screen/slot-000000.png", slot.ScreenAssetReference);
                Assert.Equal("camera/slot-000000.png", slot.CameraAssetReference);
            }

            var second = await SessionRecovery.RecoverWithReportAsync(root);
            Assert.Empty(second.Recovered);
            Assert.Empty(second.Issues);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdaptiveRecoveryReprojectsLongDualTimelineToTheOneMinuteCap()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        const long second = TimelineConstants.TicksPerSecond;
        const long latestTarget = 3840 * second;
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(
                root,
                "adaptive-dual",
                "screen+camera",
                0,
                second,
                adaptiveSampling: true))
            {
                await store.StartRecordingAsync();
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 0, 0, 0, false, 0, "screen/slot-a-000000"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "camera", 0, 0, 0, false, 0, "camera/slot-a-000000"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 1, latestTarget, latestTarget, false, 0, "screen/slot-a-latest"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "camera", 1, latestTarget, latestTarget, false, 0, "camera/slot-a-latest"));
            }

            var first = await SessionRecovery.RecoverWithReportAsync(root);
            var recovered = Assert.Single(first.Recovered);
            Assert.Equal("adaptive-dual", recovered.Id);
            Assert.Empty(first.Issues);

            await using (var store = await SessionStore.OpenAsync(root, "adaptive-dual"))
            {
                Assert.Equal(SessionStatus.Interrupted, store.Manifest.Status);
                Assert.Equal(SessionManifest.AdaptiveSamplingMode, store.Manifest.SamplingMode);
                Assert.Equal(AdaptiveSamplingPolicy.MaximumIntervalTicks,
                    store.Manifest.CaptureIntervalTicks);
                var input = SessionRenderInputBuilder.Build(store.Manifest);
                Assert.Equal(65, input.Slots.Count);
                Assert.All(input.Slots, static slot =>
                {
                    Assert.False(slot.ScreenIsGap);
                    Assert.False(slot.CameraIsGap);
                });
                Assert.Equal(
                    AdaptiveSamplingPolicy.MaximumIntervalTicks,
                    input.Slots.Zip(input.Slots.Skip(1))
                        .Select(pair => pair.Second.Slot.ActiveTargetTicks - pair.First.Slot.ActiveTargetTicks)
                        .Max());
            }

            var secondRecovery = await SessionRecovery.RecoverWithReportAsync(root);
            Assert.Empty(secondRecovery.Recovered);
            Assert.Empty(secondRecovery.Issues);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingCameraDataRemainsAnExplicitGapDuringDualRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "missing-camera", "screen+camera", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 0, 0, 0, false, 0, "screen/slot-000000.png"));
            }

            await SessionRecovery.RecoverAsync(root);
            await using var recovered = await SessionStore.OpenAsync(root, "missing-camera");
            var input = SessionRenderInputBuilder.Build(recovered.Manifest);
            var frame = Assert.Single(input.Slots);
            Assert.False(frame.ScreenIsGap);
            Assert.True(frame.CameraIsGap);
            var composition = CompositionPlanner.PlanFrame(
                0,
                new CompositionSettings(
                    new PixelSize(640, 360),
                    new PipLayout(true, 0.25, 0.04, PipCorner.BottomRight),
                    30)
                {
                    Mode = CompositionMode.PictureInPicture
                },
                hasScreenSource: !frame.ScreenIsGap,
                hasCameraSource: !frame.CameraIsGap);
            Assert.True(composition.ScreenVisible);
            Assert.Null(composition.CameraDestination);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsAnInvalidSavedExportSettingsSnapshotWithoutBreakingTheScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "valid", "screen", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(10);
            }

            var manifestPath = Path.Combine(root, "sessions", "valid", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["lastExportSettings"] = new JsonObject
            {
                ["outputFramesPerSecond"] = 25
            };
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(ManifestJsonOptions));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Empty(report.Recovered);
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-export-settings-invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsDuplicateHighlightIdsAsAnInvalidManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.AppendHighlightAsync(new HighlightEvent(
                    "highlight-1",
                    105,
                    HighlightSource.Screen,
                    10));
            }

            var manifestPath = Path.Combine(root, "sessions", "session-1", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var highlights = manifest["highlights"]!.AsArray();
            highlights.Add(highlights[0]!.DeepClone());
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Empty(report.Recovered);
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-highlights-invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsCorruptDirectoriesAndContinuesScanning()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(Path.Combine(sessions, "broken"));
        Directory.CreateDirectory(Path.Combine(sessions, "missing"));
        Directory.CreateDirectory(Path.Combine(sessions, "mismatch"));
        Directory.CreateDirectory(Path.Combine(sessions, "valid"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sessions, "broken", "manifest.json"), "{ not json");
            await File.WriteAllTextAsync(
                Path.Combine(sessions, "mismatch", "manifest.json"),
                "{\"id\":\"other\",\"sourceMode\":\"screen\",\"startedAtTicks\":1,\"captureIntervalTicks\":1,\"status\":\"Recording\"}");
            await using (var store = await SessionStore.CreateAsync(root, "valid", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
            }

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            var recovered = Assert.Single(report.Recovered);
            Assert.Equal("valid", recovered.Id);
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-invalid-json");
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-missing");
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-id-mismatch");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RebuildsMissingCatalogRowsFromReadyManifests()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "ready", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(110);
            }

            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var report = await SessionRecovery.RecoverWithReportAsync(root, catalog);

            Assert.Empty(report.Recovered);
            var session = Assert.Single(await catalog.ListSessionsAsync());
            Assert.Equal("ready", session.Id);
            Assert.Equal("ready", session.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysDurableSlotJournalWhenManifestSnapshotLags()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "recording", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendSlotAsync(new ManifestSlot(0, 100, 100, false, 0, "screen/slot-000000"));
            }

            var manifestPath = Path.Combine(root, "sessions", "recording", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["slots"]!.AsArray().Clear();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(ManifestJsonOptions));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Single(report.Recovered);
            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal(1, recoveredManifest.RootElement.GetProperty("slots").GetArrayLength());
            Assert.Equal("Interrupted", recoveredManifest.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysSourceTimingMetadataAlongsideADurableSlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "recording", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendSlotAsync(
                    new ManifestSlot(0, 0, 100, false, 0, "screen/slot-000000"),
                    sourceFrame: new FrameSample(
                        100,
                        new byte[] { 1 },
                        2,
                        2,
                        arrivalTimestampTicks: 105,
                        timestampQuality: FrameTimestampQuality.SourceProvided,
                        sourceEpoch: 3));
            }

            var manifestPath = Path.Combine(root, "sessions", "recording", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["slots"]!.AsArray().Clear();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(ManifestJsonOptions));

            await SessionRecovery.RecoverAsync(root);

            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var slot = Assert.Single(recoveredManifest.RootElement.GetProperty("slots").EnumerateArray());
            Assert.Equal(105, slot.GetProperty("arrivalTicks").GetInt64());
            Assert.Equal("sourceprovided", slot.GetProperty("timestampQuality").GetString());
            Assert.Equal(3, slot.GetProperty("sourceEpoch").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysDurableSegmentJournalWhenManifestSnapshotLags()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "recording", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                var segmentPath = Path.Combine(store.DirectoryPath, "screen", "segment-000000.mp4");
                await File.WriteAllBytesAsync(segmentPath, [1]);
                await store.AppendFinalizedSegmentAsync(new SessionSegment(
                    "screen", 0, 0, 1, 100, 110, "screen/segment-000000.mp4", 1));
            }

            var manifestPath = Path.Combine(root, "sessions", "recording", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["segments"]!.AsArray().Clear();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Single(report.Recovered);
            Assert.DoesNotContain(report.Issues, issue => issue.Reason == "segment-missing");
            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal(1, recoveredManifest.RootElement.GetProperty("segments").GetArrayLength());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysDurablePauseJournalWhenManifestSnapshotLags()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "recording", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendPauseRangeAsync(new PauseRange(110, 120));
            }

            var manifestPath = Path.Combine(root, "sessions", "recording", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["pauses"]!.AsArray().Clear();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Single(report.Recovered);
            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var pause = Assert.Single(recoveredManifest.RootElement.GetProperty("pauses").EnumerateArray());
            Assert.Equal(110, pause.GetProperty("startTicks").GetInt64());
            Assert.Equal(120, pause.GetProperty("endTicks").GetInt64());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysDurableTrackPruneJournalWhenManifestSnapshotLags()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(110);
                await store.MarkTrackPrunedAsync("screen");
            }

            var manifestPath = Path.Combine(root, "sessions", "session-1", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest.Remove("prunedTracks");
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(ManifestJsonOptions));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Empty(report.Recovered);
            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var prunedTracks = recoveredManifest.RootElement.GetProperty("prunedTracks").EnumerateArray();
            Assert.Equal("screen", Assert.Single(prunedTracks).GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsACommittedSegmentWhoseLengthChangedOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string segmentPath;
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                segmentPath = Path.Combine(store.DirectoryPath, "screen", "segment-000000.mp4");
                await File.WriteAllBytesAsync(segmentPath, [1]);
                await store.AppendFinalizedSegmentAsync(new SessionSegment(
                    "screen", 0, 0, 1, 100, 110, "screen/segment-000000.mp4", 1));
                await store.CompleteAsync(110);
            }

            await File.WriteAllBytesAsync(segmentPath, [1, 2]);
            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Contains(report.Issues, issue => issue.Reason == "segment-length-mismatch");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAValidJsonManifestWithAnInconsistentTrackMap()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var sessionDirectory = Path.Combine(sessions, "session-1");
        Directory.CreateDirectory(sessionDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sessionDirectory, "manifest.json"),
                "{\"id\":\"session-1\",\"sourceMode\":\"screen\",\"startedAtTicks\":1,\"captureIntervalTicks\":1,\"status\":\"Recording\",\"tracks\":{\"camera\":\"camera/\"}} ");

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Empty(report.Recovered);
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-tracks-invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAValidJsonManifestWithAnUnsafeSlotReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var sessionDirectory = Path.Combine(root, "sessions", "session-1");
        Directory.CreateDirectory(sessionDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sessionDirectory, "manifest.json"),
                "{\"id\":\"session-1\",\"sourceMode\":\"screen\",\"startedAtTicks\":1,\"captureIntervalTicks\":1,\"status\":\"Recording\",\"tracks\":{\"screen\":\"screen/\"},\"slots\":[{\"index\":0,\"targetTicks\":1,\"sourceTicks\":1,\"isDuplicate\":false,\"ageTicks\":0,\"assetReference\":\"..\\\\outside\"}]} ");

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Empty(report.Recovered);
            Assert.Contains(report.Issues, issue => issue.Reason == "manifest-slot-reference-invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysValidJournalRecordsAndSkipsATornFinalRecord()
    {
        // S8 crash-safety proof: a power-loss torn final journal line must not
        // corrupt recovery — earlier valid records still replay, the torn tail
        // is reported and dropped.
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "recording", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendSlotAsync(new ManifestSlot(0, 100, 100, false, 0, "screen/slot-000000"));
            }

            // Simulate a crash mid-write: append a partially flushed final record
            // (invalid JSON, no trailing newline) after the durable slot record.
            var journalPath = Path.Combine(root, "sessions", "recording", "events.jsonl");
            await File.AppendAllTextAsync(journalPath, "{\"sequence\":999,\"kind\":\"slot\",\"slotIndex\":1");

            // Force recovery to depend on the journal by clearing the manifest snapshot.
            var manifestPath = Path.Combine(root, "sessions", "recording", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["slots"]!.AsArray().Clear();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(ManifestJsonOptions));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Single(report.Recovered);
            Assert.Contains(report.Issues, issue => issue.Reason == "journal-truncated-final-record");
            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal(1, recoveredManifest.RootElement.GetProperty("slots").GetArrayLength());
            Assert.Equal("Interrupted", recoveredManifest.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysHighlightUpdatesWhenTheManifestSnapshotLags()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendHighlightAsync(new HighlightEvent(
                    "highlight-1",
                    110,
                    HighlightSource.Screen,
                    15));
                await store.UpdateHighlightAsync("highlight-1", included: false, holdTicks: 25);
            }

            var manifestPath = Path.Combine(root, "sessions", "session-1", "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["highlights"]!.AsArray().Clear();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(ManifestJsonOptions));

            var report = await SessionRecovery.RecoverWithReportAsync(root);

            Assert.Single(report.Recovered);
            using var recoveredManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var highlight = Assert.Single(recoveredManifest.RootElement.GetProperty("highlights").EnumerateArray());
            Assert.False(highlight.GetProperty("included").GetBoolean());
            Assert.Equal(25, highlight.GetProperty("holdTicks").GetInt64());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
