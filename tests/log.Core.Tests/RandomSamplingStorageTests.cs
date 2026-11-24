using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class RandomSamplingStorageTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    [Fact]
    public async Task RandomScheduleAndSparseSlotsRoundTripWithoutCompaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstWindow = RandomAdaptiveSamplingPolicy.BuildWindow(0, 42);
            var secondWindow = RandomAdaptiveSamplingPolicy.BuildWindow(firstWindow.EndTicks, 42);
            var firstReference = $"screen/slot-r-{firstWindow.TargetTicks:D19}";
            var secondReference = $"screen/slot-r-{secondWindow.TargetTicks:D19}";

            await using (var store = await SessionStore.CreateAsync(
                root,
                "random",
                "screen",
                0,
                Second,
                randomSampling: true,
                randomSeed: 42))
            {
                Assert.Equal(SessionManifest.RandomSamplingMode, store.Manifest.SamplingMode);
                Assert.Empty(store.Manifest.RandomSampling!.Windows);
                Assert.Equal(42, store.Manifest.RandomSampling.Seed);

                Assert.True(await store.AppendRandomSamplingWindowAsync(firstWindow));
                Assert.False(await store.AppendRandomSamplingWindowAsync(firstWindow));
                Assert.True(await store.AppendRandomSamplingWindowAsync(secondWindow));

                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, firstReference), [1]);
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, secondReference), [2]);
                await store.AppendSlotAsync(new ManifestSlot(
                    0,
                    firstWindow.TargetTicks,
                    firstWindow.TargetTicks,
                    false,
                    0,
                    firstReference));
                await store.AppendSlotAsync(new ManifestSlot(
                    1,
                    secondWindow.TargetTicks,
                    secondWindow.TargetTicks,
                    false,
                    0,
                    secondReference));
                await store.CompleteAsync(secondWindow.EndTicks);
            }

            await using var reopened = await SessionStore.OpenAsync(root, "random");
            Assert.Equal(SessionManifest.RandomSamplingMode, reopened.Manifest.SamplingMode);
            Assert.Equal(42, reopened.Manifest.RandomSampling!.Seed);
            Assert.Equal(
                [firstWindow, secondWindow],
                reopened.Manifest.RandomSampling.Windows);
            Assert.Equal(
                [firstWindow.TargetTicks, secondWindow.TargetTicks],
                reopened.Manifest.Slots.Select(static slot => slot.TargetTicks));
            Assert.All(
                reopened.Manifest.Slots,
                slot => Assert.StartsWith("screen/slot-r-", slot.AssetReference, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecoveryReplaysRandomWindowAndKeepsItsOriginalTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string oldManifest;
            var firstWindow = RandomAdaptiveSamplingPolicy.BuildWindow(0, 7);
            var secondWindow = RandomAdaptiveSamplingPolicy.BuildWindow(firstWindow.EndTicks, 7);
            await using (var store = await SessionStore.CreateAsync(
                root,
                "recover-random",
                "screen",
                0,
                Second,
                randomSampling: true,
                randomSeed: 7))
            {
                await store.AppendRandomSamplingWindowAsync(firstWindow);
                var firstReference = $"screen/slot-r-{firstWindow.TargetTicks:D19}";
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, firstReference), [1]);
                await store.AppendSlotAsync(new ManifestSlot(
                    0,
                    firstWindow.TargetTicks,
                    firstWindow.TargetTicks,
                    false,
                    0,
                    firstReference));
                oldManifest = await File.ReadAllTextAsync(
                    Path.Combine(store.DirectoryPath, "manifest.json"));

                await store.AppendRandomSamplingWindowAsync(secondWindow);
                var secondReference = $"screen/slot-r-{secondWindow.TargetTicks:D19}";
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, secondReference), [2]);
                await store.AppendSlotAsync(new ManifestSlot(
                    1,
                    secondWindow.TargetTicks,
                    secondWindow.TargetTicks,
                    false,
                    0,
                    secondReference));
            }

            // The journal contains the second decision, while the manifest
            // snapshot represents a process dying before that snapshot landed.
            await File.WriteAllTextAsync(
                Path.Combine(root, "sessions", "recover-random", "manifest.json"),
                oldManifest);

            var report = await SessionRecovery.RecoverWithReportAsync(root);
            Assert.Contains(report.Recovered, session => session.Id == "recover-random");

            await using var recovered = await SessionStore.OpenAsync(root, "recover-random");
            Assert.Equal(SessionStatus.Interrupted, recovered.Manifest.Status);
            Assert.Equal(
                secondWindow,
                recovered.Manifest.RandomSampling!.Windows.Single(window =>
                    window.StartTicks == secondWindow.StartTicks));
            Assert.Equal(secondWindow.TargetTicks, recovered.Manifest.Slots[1].TargetTicks);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RandomAdaptiveSessionsRejectFixedGridCompaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(
                root,
                "no-compact",
                "screen",
                0,
                Second,
                randomSampling: true,
                randomSeed: 42);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.CompactAdaptiveSamplesAsync(Second));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
