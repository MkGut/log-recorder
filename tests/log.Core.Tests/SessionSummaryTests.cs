using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionSummaryTests
{
    [Fact]
    public async Task SummarizesSlotsGapsPausesHighlightsAndSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10);
            await store.StartRecordingAsync();
            await store.AppendSlotAsync(new ManifestSlot(0, 100, -1, false, 0, "screen/slot-000000"));
            await store.AppendSlotAsync(new ManifestSlot(1, 110, 110, false, 0, "screen/slot-000001"));
            await store.AppendPauseRangeAsync(new PauseRange(110, 120));
            await store.AppendHighlightAsync(new HighlightEvent("highlight-1", 110, HighlightSource.Screen, 15));
            var segmentPath = Path.Combine(store.DirectoryPath, "screen", "segment-000000.mp4");
            await File.WriteAllBytesAsync(segmentPath, [1, 2]);
            await store.AppendFinalizedSegmentAsync(new SessionSegment(
                "screen", 0, 0, 2, 100, 120, "screen/segment-000000.mp4", 2));
            await store.CompleteAsync(130);
            Assert.Single(store.Manifest.Pauses);

            var summary = await SessionSummaryReader.ReadAsync(store.DirectoryPath);

            Assert.Equal("session-1", summary.Id);
            Assert.Equal(store.Manifest.Name, summary.Name);
            Assert.Equal(SessionStatus.Ready, summary.Status);
            Assert.Equal(2, summary.SlotCount);
            Assert.Equal(1, summary.GapCount);
            Assert.Equal(1, summary.HighlightCount);
            Assert.Equal(2, summary.SegmentBytes);
            Assert.True(summary.StorageBytes > summary.SegmentBytes);
            Assert.Equal(20, summary.ActiveDurationTicks);
            Assert.NotNull(summary.StartedAtUtc);
            Assert.NotNull(summary.StoppedAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SummarizesDualSourceTrackSlotCountsAndPerTrackGaps()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen+camera", 0, 10);
            await store.StartRecordingAsync();
            await store.AppendTrackSlotAsync(new ManifestTrackSlot("screen", 0, 0, 0, false, 0, "screen/slot-000000"));
            // Camera missed the first slot (source unavailable -> gap).
            await store.AppendTrackSlotAsync(new ManifestTrackSlot("camera", 0, 0, -1, false, 0, "camera/slot-000000"));
            await store.AppendTrackSlotAsync(new ManifestTrackSlot("screen", 1, 10, 10, false, 0, "screen/slot-000001"));
            await store.AppendTrackSlotAsync(new ManifestTrackSlot("camera", 1, 10, 10, false, 0, "camera/slot-000001"));
            await store.CompleteAsync(20);

            var summary = await SessionSummaryReader.ReadAsync(store.DirectoryPath);

            Assert.Equal("screen+camera", summary.SourceMode);
            Assert.Equal(2, summary.SlotCount); // distinct slot indices 0 and 1
            Assert.Equal(2, summary.TrackSlotCounts["screen"]);
            Assert.Equal(2, summary.TrackSlotCounts["camera"]);
            Assert.Equal(1, summary.GapCount); // only the missing camera slot
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CountsEverySlotForAPrunedTrackAsAGap()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 0, 10);
            await store.StartRecordingAsync();
            await store.AppendSlotAsync(new ManifestSlot(0, 0, 0, false, 0, "screen/slot-000000"));
            await store.AppendSlotAsync(new ManifestSlot(1, 10, 10, false, 0, "screen/slot-000001"));
            await store.CompleteAsync(20);
            await store.MarkTrackPrunedAsync("screen");

            var summary = await SessionSummaryReader.ReadAsync(store.DirectoryPath);

            Assert.Equal(2, summary.SlotCount);
            Assert.Equal(2, summary.GapCount);
            Assert.Equal(["screen"], summary.PrunedTracks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataOnlySummaryDoesNotClaimAStorageMeasurement()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "metadata", "screen", 0, 10);
            await store.StartRecordingAsync();
            await store.CompleteAsync(10);
            await File.WriteAllBytesAsync(
                Path.Combine(store.DirectoryPath, "screen", "unreferenced.bin"),
                [1, 2, 3, 4]);

            var metadata = await SessionSummaryReader.ReadAsync(
                store.DirectoryPath,
                SessionSummaryReadOptions.MetadataOnly);
            var full = await SessionSummaryReader.ReadAsync(store.DirectoryPath);

            Assert.False(metadata.StorageMeasured);
            Assert.Equal(0, metadata.StorageBytes);
            Assert.True(full.StorageMeasured);
            Assert.True(full.StorageBytes > metadata.StorageBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SummaryReadHonorsCancellationBeforeOpeningTheManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var store = await SessionStore.CreateAsync(root, "cancelled", "screen", 0, 10);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionSummaryReader.ReadAsync(
                store.DirectoryPath,
                SessionSummaryReadOptions.MetadataOnly,
                cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
