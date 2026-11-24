using System.Text.Json;
using System.Text.Json.Nodes;
using Log.Core.Capture;
using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task RejectsUnsafeSessionIdsBeforeCreatingDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ArgumentException>(() => SessionStore.CreateAsync(
            root,
            "..\\outside",
            "screen",
            100,
            10));
        Assert.False(Directory.Exists(Path.Combine(root, "outside")));
    }

    [Fact]
    public async Task ClosesTheJournalWhenInitialManifestPersistenceFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionStore.CreateAsync(
                root,
                "session-1",
                "screen",
                100,
                10,
                cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsDuplicateHighlightIdsWhenOpeningAManifest()
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

            await Assert.ThrowsAsync<InvalidDataException>(() => SessionStore.OpenAsync(root, "session-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WritesAtomicManifestAndChecksummedJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.AppendSlotAsync(new ManifestSlot(0, 100, 100, false, 0, "screen/slot-0000"));
                await store.CompleteAsync(110);
            }

            var sessionDirectory = Path.Combine(root, "sessions", "session-1");
            var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "manifest.json")));
            Assert.Equal("Ready", manifest.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, manifest.RootElement.GetProperty("slots").GetArrayLength());
            Assert.Equal("screen/", manifest.RootElement.GetProperty("tracks").GetProperty("screen").GetString());
            var startedAtUtc = manifest.RootElement.GetProperty("startedAtUtc").GetDateTimeOffset();
            var stoppedAtUtc = manifest.RootElement.GetProperty("stoppedAtUtc").GetDateTimeOffset();
            Assert.True(stoppedAtUtc >= startedAtUtc);

            var journalLine = (await File.ReadAllLinesAsync(Path.Combine(sessionDirectory, "events.jsonl"))).Single();
            Assert.True(JsonDocument.Parse(journalLine).RootElement.TryGetProperty("checksum", out var checksum));
            Assert.Equal(64, checksum.GetString()!.Length);

            var journalPath = Path.Combine(sessionDirectory, "events.jsonl");
            var journal = await SessionJournalReader.ReadAsync(journalPath);
            Assert.Single(journal.Records);
            Assert.Equal(0, journal.InvalidRecordCount);
            Assert.False(journal.HasTruncatedFinalRecord);

            await File.AppendAllTextAsync(journalPath, "{\"sequence\":");
            var truncatedJournal = await SessionJournalReader.ReadAsync(journalPath);
            Assert.Single(truncatedJournal.Records);
            Assert.True(truncatedJournal.HasTruncatedFinalRecord);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsSourceArrivalQualityAndEpochMetadataWithASlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            var sample = new FrameSample(
                sourceTimestampTicks: 100,
                payload: new byte[] { 1 },
                width: 2,
                height: 2,
                arrivalTimestampTicks: 105,
                timestampQuality: FrameTimestampQuality.SourceProvided,
                sourceEpoch: 2);

            await store.AppendSlotAsync(
                new ManifestSlot(0, 0, 100, false, 0, "screen/slot-000000"),
                sourceFrame: sample);

            var slot = Assert.Single(store.Manifest.Slots);
            Assert.Equal(105, slot.ArrivalTicks);
            Assert.Equal("sourceprovided", slot.TimestampQuality);
            Assert.Equal(2, slot.SourceEpoch);
            var journal = await SessionJournalReader.ReadAsync(
                Path.Combine(store.DirectoryPath, "events.jsonl"));
            var record = Assert.Single(journal.Records);
            Assert.Equal(105, record.ArrivalTicks);
            Assert.Equal("sourceprovided", record.TimestampQuality);
            Assert.Equal(2, record.SourceEpoch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotMutateJournalBackedManifestDataWhenTheJournalIsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            var originalHighlight = new HighlightEvent("original", 110, HighlightSource.Screen, 15);
            await store.AppendHighlightAsync(originalHighlight);
            var segmentPath = Path.Combine(store.DirectoryPath, "screen", "segment-000000.mp4");
            await File.WriteAllBytesAsync(segmentPath, [1]);
            await store.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.AppendSlotAsync(
                new ManifestSlot(0, 100, 100, false, 0, "screen/slot-000000")).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.AppendPauseRangeAsync(
                new PauseRange(120, 130)).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.AppendHighlightAsync(
                new HighlightEvent("new", 120, HighlightSource.Screen, 15)).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.UpdateHighlightAsync(
                "original",
                included: false,
                holdTicks: 25).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.DeleteHighlightAsync("original").AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.AppendFinalizedSegmentAsync(
                new SessionSegment("screen", 0, 0, 1, 100, 110, "screen/segment-000000.mp4", 1)).AsTask());

            Assert.Empty(store.Manifest.Slots);
            Assert.Empty(store.Manifest.Pauses);
            Assert.Equal([originalHighlight], store.Manifest.Highlights);
            Assert.Empty(store.Manifest.Segments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsCaptureDataMutationAfterTheSessionIsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 0, 10);
            await store.CompleteAsync(10);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendSlotAsync(
                new ManifestSlot(0, 0, -1, false, 0, "screen/slot-000000")).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendPauseRangeAsync(
                new PauseRange(1, 2)).AsTask());

            await store.MarkExportedAsync();
            Assert.Equal(SessionStatus.Exported, store.Manifest.Status);
            await store.MarkExportedAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefusesToReuseANonEmptySessionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
            }

            await Assert.ThrowsAsync<IOException>(() => SessionStore.CreateAsync(root, "session-1", "screen", 200, 10));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsFinalizedSegmentsAndChecksumsTheirJournalRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            var segmentPath = Path.Combine(store.DirectoryPath, "screen", "segment-000001.mp4");
            await File.WriteAllBytesAsync(segmentPath, [1, 2, 3]);
            await store.AppendFinalizedSegmentAsync(new SessionSegment(
                "screen",
                index: 1,
                firstSlotIndex: 0,
                slotCount: 3,
                startedAtTicks: 100,
                endedAtTicks: 130,
                assetReference: "screen/segment-000001.mp4",
                byteLength: 3));

            Assert.Single(store.Manifest.Segments);
            var journal = await SessionJournalReader.ReadAsync(Path.Combine(store.DirectoryPath, "events.jsonl"));
            var record = Assert.Single(journal.Records);
            Assert.Equal("segment", record.Kind);
            Assert.Equal(1, record.SegmentIndex);
            Assert.Equal(3, record.SegmentSlotCount);
            Assert.Equal(3, record.ByteLength);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsUnsafeOrNonMp4SegmentReferences()
    {
        Assert.Throws<ArgumentException>(() => new SessionSegment(
            "screen", 0, 0, 1, 100, 110, "..\\outside.mp4", 1));
        Assert.Throws<ArgumentException>(() => new SessionSegment(
            "screen", 0, 0, 1, 100, 110, "screen/frame.png", 1));
        Assert.Throws<ArgumentException>(() => new SessionSegment(
            "screen", 0, 0, 1, 100, 110, "camera/segment.mp4", 1));
    }

    [Fact]
    public async Task DoesNotCommitMissingOrPartiallyWrittenSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            var segment = new SessionSegment("screen", 0, 0, 1, 100, 110, "screen/segment.mp4", 3);

            await Assert.ThrowsAsync<FileNotFoundException>(() => store.AppendFinalizedSegmentAsync(segment).AsTask());

            await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, "screen", "segment.mp4"), [1, 2]);
            await Assert.ThrowsAsync<IOException>(() => store.AppendFinalizedSegmentAsync(segment).AsTask());
            Assert.Empty(store.Manifest.Segments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsNonOverlappingPauseRangesAndBoundsStopTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            await store.AppendPauseRangeAsync(new PauseRange(110, 120));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AppendPauseRangeAsync(new PauseRange(115, 125)).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(115).AsTask());

            await store.CompleteAsync(130);
            Assert.Single(store.Manifest.Pauses);
            var journal = await SessionJournalReader.ReadAsync(Path.Combine(store.DirectoryPath, "events.jsonl"));
            var record = Assert.Single(journal.Records);
            Assert.Equal("pause", record.Kind);
            Assert.Equal(110, record.PauseStartTicks);
            Assert.Equal(120, record.PauseEndTicks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsDuplicateAndUnconfiguredTrackSlots()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            var slot = new ManifestSlot(0, 100, 100, false, 0, "screen/slot-000000");
            await store.AppendSlotAsync(slot);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendSlotAsync(slot).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendTrackSlotAsync(
                new ManifestTrackSlot("camera", 0, 100, 100, false, 0, "camera/slot-000000")).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendSlotAsync(
                new ManifestSlot(1, 110, 110, false, 0, "camera/slot-000001")).AsTask());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DualSessionsRequireIndependentTrackSlots()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen+camera", 100, 10);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendSlotAsync(
                new ManifestSlot(0, 100, 100, false, 0, "screen/slot-000000")).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendTrackSlotAsync(
                new ManifestTrackSlot("camera", 0, 100, 100, false, 0, "screen/slot-000000")).AsTask());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReopensAReadySessionAndJournalsHighlightEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.AppendHighlightAsync(new HighlightEvent(
                    "highlight-1",
                    110,
                    HighlightSource.Screen,
                    15));
                await store.CompleteAsync(120);
            }

            await using (var store = await SessionStore.OpenAsync(root, "session-1"))
            {
                await store.UpdateHighlightAsync("highlight-1", included: false, holdTicks: 25);
                Assert.False(store.Manifest.Highlights[0].Included);
                Assert.Equal(25, store.Manifest.Highlights[0].HoldTicks);

                await store.DeleteHighlightAsync("highlight-1");
                Assert.Empty(store.Manifest.Highlights);
            }

            var journal = await SessionJournalReader.ReadAsync(
                Path.Combine(root, "sessions", "session-1", "events.jsonl"));
            Assert.Equal(
                ["highlight", "highlight-update", "highlight-delete"],
                journal.Records.Select(record => record.Kind));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
