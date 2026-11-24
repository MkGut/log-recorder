using System.Text.Json;
using System.Text.Json.Serialization;
using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class AdaptiveStorageTests
{
    private const long Second = TimelineConstants.TicksPerSecond;
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task CompactionReindexesSavedSlotsAndDeletesOnlySupersededSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(root, "adaptive", "screen", 0, Second);
            for (var index = 0; index < 60; index++)
            {
                var reference = $"screen/slot-{index:D6}";
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, reference), [1]);
                await store.AppendSlotAsync(new ManifestSlot(index, index * Second, index * Second, false, 0, reference));
            }

            Assert.True(await store.CompactAdaptiveSamplesAsync(60 * Second));
            Assert.Equal(2 * Second, store.Manifest.CaptureIntervalTicks);
            Assert.Equal(30, store.Manifest.Slots.Count);
            Assert.Equal(Enumerable.Range(0, 30), store.Manifest.Slots.Select(static slot => slot.Index));
            Assert.Equal(Enumerable.Range(0, 30).Select(index => index * 2 * Second), store.Manifest.Slots.Select(static slot => slot.TargetTicks));
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "screen", "slot-000001")));
            Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "screen", "slot-000002")));
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "screen", "slot-000059")));
            Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "screen", "slot-000058")));

            await store.CompleteAsync(60 * Second);
            var renderInput = Log.Core.Render.SessionRenderInputBuilder.Build(store.Manifest);
            Assert.Equal(30, renderInput.Slots.Count);
            Assert.Equal(2 * Second, renderInput.Slots[1].Slot.ActiveTargetTicks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompactionPreservesHighlightsAndTheirSeparateAssets()
    {
        // Root cause #6: adaptive thinning must delete only superseded sampled
        // SCREENSHOT slots (screen/slot-*) and never touch a highlight event or
        // its own asset under highlights/, even when the highlight's timestamp
        // lands on a slot that compaction is about to thin away.
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var highlightAssetReference = "highlights/hl-test-screen.png";
            HighlightEvent highlight;
            await using (var store = await SessionStore.CreateAsync(root, "adaptive-highlight", "screen", 0, Second))
            {
                for (var index = 0; index < 60; index++)
                {
                    var reference = $"screen/slot-{index:D6}";
                    await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, reference), [1]);
                    await store.AppendSlotAsync(new ManifestSlot(index, index * Second, index * Second, false, 0, reference));
                }

                // The highlight sits at 15s -- a target that the 60s-boundary
                // compaction below thins away from the sampled timeline (only
                // even-second targets survive at the new 2s cadence).
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, highlightAssetReference), [42]);
                highlight = new HighlightEvent(
                    "hl-1",
                    eventTicks: 15 * Second,
                    HighlightSource.Screen,
                    holdTicks: 2 * Second,
                    included: true,
                    screenAssetReference: highlightAssetReference);
                await store.AppendHighlightAsync(highlight);

                Assert.True(await store.CompactAdaptiveSamplesAsync(60 * Second));
                Assert.Equal(2 * Second, store.Manifest.CaptureIntervalTicks);
                Assert.Equal(30, store.Manifest.Slots.Count);

                // The superseded sampled slot at the highlight's own timestamp
                // is gone, proving compaction actually ran...
                Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "screen", "slot-000015")));

                // ...yet the highlight event is byte-for-byte unchanged (same
                // id, timestamp, hold, and asset reference) and its asset file
                // is untouched.
                var survivingHighlight = Assert.Single(store.Manifest.Highlights);
                Assert.Equal(highlight, survivingHighlight);
                Assert.True(File.Exists(Path.Combine(store.DirectoryPath, highlightAssetReference)));
                Assert.Equal(42, (await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, highlightAssetReference)))[0]);
            }

            // Reopen from disk to confirm the highlight and its asset survive
            // the manifest round-trip too, not just the in-memory compaction.
            await using var reopened = await SessionStore.OpenAsync(root, "adaptive-highlight");
            var reloadedHighlight = Assert.Single(reopened.Manifest.Highlights);
            Assert.Equal(highlight, reloadedHighlight);
            Assert.True(File.Exists(Path.Combine(reopened.DirectoryPath, highlightAssetReference)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryReplaysCompactionJournalAfterManifestWasInterrupted()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string oldManifest;
            await using (var store = await SessionStore.CreateAsync(root, "recover", "screen", 0, Second))
            {
                for (var index = 0; index < 60; index++)
                {
                    var reference = $"screen/slot-{index:D6}";
                    await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, reference), [1]);
                    await store.AppendSlotAsync(new ManifestSlot(index, index * Second, index * Second, false, 0, reference));
                }

                oldManifest = await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath, "manifest.json"));
                await store.CompactAdaptiveSamplesAsync(60 * Second);
            }

            // Simulate a process dying after the compaction journal was made
            // durable but before the atomic manifest replacement completed.
            var manifestPath = Path.Combine(root, "sessions", "recover", "manifest.json");
            await File.WriteAllTextAsync(manifestPath, oldManifest);
            var orphanPath = Path.Combine(root, "sessions", "recover", "screen", "slot-recovery-orphan.png");
            await File.WriteAllBytesAsync(orphanPath, [2]);

            var report = await SessionRecovery.RecoverWithReportAsync(root);
            Assert.Contains(report.Recovered, session => session.Id == "recover");
            var repaired = JsonSerializer.Deserialize<SessionManifest>(
                await File.ReadAllTextAsync(manifestPath),
                ManifestOptions);
            Assert.NotNull(repaired);
            Assert.Equal(2 * Second, repaired.CaptureIntervalTicks);
            Assert.Equal(30, repaired.Slots.Count);
            Assert.False(File.Exists(Path.Combine(root, "sessions", "recover", "screen", "slot-000001")));
            Assert.False(File.Exists(orphanPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdaptiveAssetNamesDoNotOverwriteRetainedAssetsAfterReindex()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(root, "collision", "screen", 0, Second);
            for (var index = 0; index < 60; index++)
            {
                var reference = $"screen/slot-{index:D6}";
                var path = Path.Combine(store.DirectoryPath, reference);
                await File.WriteAllBytesAsync(path, [(byte)index]);
                await store.AppendSlotAsync(new ManifestSlot(index, index * Second, index * Second, false, 0, reference));
            }

            await store.CompactAdaptiveSamplesAsync(60 * Second);
            var retainedReference = store.Manifest.Slots.Single(slot => slot.TargetTicks == 30 * Second).AssetReference;
            var retainedPath = Path.Combine(store.DirectoryPath, retainedReference);
            Assert.Equal(30, (await File.ReadAllBytesAsync(retainedPath))[0]);

            var newReference = AdaptiveAssetNaming.For("screen", 60 * Second);
            Assert.DoesNotContain(newReference, store.Manifest.Slots.Select(static slot => slot.AssetReference));
            await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, newReference), [99]);

            Assert.Equal(30, (await File.ReadAllBytesAsync(retainedPath))[0]);
            Assert.Equal(99, (await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, newReference)))[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdaptiveAssetNamesDoNotOverwriteRetainedAssetsAtThe180SecondBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(root, "collision-180", "screen", 0, Second);
            for (var index = 0; index < 60; index++)
            {
                var reference = $"screen/slot-{index:D6}";
                var path = Path.Combine(store.DirectoryPath, reference);
                await File.WriteAllBytesAsync(path, [(byte)index]);
                await store.AppendSlotAsync(new ManifestSlot(index, index * Second, index * Second, false, 0, reference));
            }

            await store.CompactAdaptiveSamplesAsync(60 * Second);

            // Keep appending through the 2-second stage (targets 60..178),
            // exactly as a live run would after the first transition, so the
            // second compaction has a realistic pre-180s timeline to reindex.
            for (var index = 30; index < 90; index++)
            {
                var targetTicks = index * 2 * Second;
                var reference = $"screen/slot-{index:D6}";
                var path = Path.Combine(store.DirectoryPath, reference);
                await File.WriteAllBytesAsync(path, [(byte)index]);
                await store.AppendSlotAsync(new ManifestSlot(index, targetTicks, targetTicks, false, 0, reference));
            }

            await store.CompactAdaptiveSamplesAsync(180 * Second);
            Assert.Equal(4 * Second, store.Manifest.CaptureIntervalTicks);
            Assert.Equal(45, store.Manifest.Slots.Count);

            var retainedReference = store.Manifest.Slots.Single(slot => slot.TargetTicks == 92 * Second).AssetReference;
            var retainedPath = Path.Combine(store.DirectoryPath, retainedReference);
            Assert.Equal(46, (await File.ReadAllBytesAsync(retainedPath))[0]);

            var newReference = AdaptiveAssetNaming.For("screen", 180 * Second);
            Assert.DoesNotContain(newReference, store.Manifest.Slots.Select(static slot => slot.AssetReference));
            await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, newReference), [99]);

            Assert.Equal(46, (await File.ReadAllBytesAsync(retainedPath))[0]);
            Assert.Equal(99, (await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, newReference)))[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdaptiveAssetReferencesAreGloballyDistinctAcrossAFullAdaptiveRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(root, "distinct", "screen", 0, Second);
            var intervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(0);
            var intervalsSeen = new List<long> { intervalTicks };
            var targetTicks = 0L;
            var allReferences = new List<string>();

            // Walk the same progressive stages a live run would (1s, then 2s
            // past 60s, then 4s past 180s), naming each write the way the
            // coordinator does. Every reference ever produced -- not just the
            // ones still retained after compaction -- must stay unique.
            while (targetTicks <= 185 * Second)
            {
                var nextIntervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(targetTicks);
                if (nextIntervalTicks != intervalTicks)
                {
                    await store.CompactAdaptiveSamplesAsync(targetTicks);
                    intervalTicks = nextIntervalTicks;
                    intervalsSeen.Add(intervalTicks);
                }

                var index = checked((int)(targetTicks / intervalTicks));
                var reference = AdaptiveAssetNaming.For("screen", targetTicks);
                allReferences.Add(reference);
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, reference), [1]);
                await store.AppendSlotAsync(new ManifestSlot(index, targetTicks, targetTicks, false, 0, reference));
                targetTicks = checked(targetTicks + intervalTicks);
            }

            Assert.Equal([Second, 2 * Second, 4 * Second], intervalsSeen);
            Assert.Equal(allReferences.Count, allReferences.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdaptiveIndexesNeverCollideAcrossUnalignedStageBoundaries()
    {
        // Root cause: 420s, 900s, and 1860s are stage boundaries where the
        // interval that activates there does not evenly divide the boundary
        // itself. A live run must keep the actual schedule target independent
        // of the manifest index; deriving index from target / interval can
        // collide with an index assigned by the compaction immediately before
        // an unaligned boundary. This test walks the real
        // SessionStore/AdaptiveSlotCompactor/AdaptiveAssetNaming past all
        // three unaligned boundaries -- unlike
        // AdaptiveAssetReferencesAreGloballyDistinctAcrossAFullAdaptiveRun
        // above, which only reaches 185s and never hits one.
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(root, "long-run", "screen", 0, Second);
            var intervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(0);
            var intervalsSeen = new List<long> { intervalTicks };
            var targetTicks = 0L;
            var nextSlotIndex = 0;
            var allReferences = new List<string>();

            while (targetTicks <= 1900 * Second)
            {
                var nextIntervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(targetTicks);
                if (nextIntervalTicks != intervalTicks)
                {
                    // Compaction always reindexes against the true elapsed
                    // (pre-realignment) duration, exactly as CaptureCoordinator
                    // calls it.
                    await store.CompactAdaptiveSamplesAsync(targetTicks);
                    intervalTicks = nextIntervalTicks;
                    intervalsSeen.Add(intervalTicks);
                    nextSlotIndex = checked(
                        store.Manifest.Slots
                            .Select(static slot => slot.Index)
                            .DefaultIfEmpty(-1)
                            .Max() + 1);
                }

                var reference = AdaptiveAssetNaming.For("screen", targetTicks);
                allReferences.Add(reference);
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, reference), [1]);

                await store.AppendSlotAsync(new ManifestSlot(
                    nextSlotIndex,
                    targetTicks,
                    targetTicks,
                    false,
                    0,
                    reference));
                nextSlotIndex = checked(nextSlotIndex + 1);
                targetTicks = checked(targetTicks + intervalTicks);
            }

            Assert.Equal(
                [Second, 2 * Second, 4 * Second, 8 * Second, 16 * Second, 32 * Second],
                intervalsSeen);
            Assert.Equal(allReferences.Count, allReferences.Distinct(StringComparer.Ordinal).Count());

            // A final compaction (as FinalizeAfterStopAsync performs on stop)
            // must leave a dense, collision-free 0..N-1 index range that
            // matches the documented retention policy exactly.
            await store.CompactAdaptiveSamplesAsync(targetTicks);
            var expectedTargets = AdaptiveSamplingPolicy.BuildRetainedTargets(targetTicks);
            Assert.Equal(expectedTargets.Count, store.Manifest.Slots.Count);
            Assert.Equal(
                Enumerable.Range(0, expectedTargets.Count),
                store.Manifest.Slots.Select(static slot => slot.Index).OrderBy(static index => index));
            Assert.Equal(
                expectedTargets,
                store.Manifest.Slots.OrderBy(static slot => slot.Index).Select(static slot => slot.TargetTicks));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
