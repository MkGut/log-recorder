using System.Text.Json;
using Log.Core.Store;
using Xunit;

namespace Log.Integration.Tests;

// S8 durability proof for the product spine (docs/planning/09 §M1): a forced
// process termination (power loss) during recording must not lose already-
// captured work. The store journals every slot durably before snapshotting, so
// a crash loses at most the in-flight capture interval.
//
// This stages a faithful power-loss state: a Recording session with K slots
// journaled to events.jsonl, then the store is dropped WITHOUT any graceful
// finalize (status stays "Recording" on disk, exactly as a killed process would
// leave it). Recovery then replays the journal, preserves every persisted slot,
// and marks the session Interrupted/recoverable.
public sealed class ForcedKillRecoveryTests
{
    [Fact]
    public async Task ForcedKillDuringRecordingRecoversEveryPersistedSlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-s8-tests", Guid.NewGuid().ToString("N"));
        const long captureIntervalTicks = 5; // ticks == seconds here: a 5 s capture interval.
        const int persistedSlots = 3;         // 3 slots durably captured (t = 0 s, 5 s, 10 s).

        try
        {
            // --- Recording, then a forced kill ---
            var store = await SessionStore.CreateAsync(
                root,
                "s8-crash",
                sourceMode: "screen",
                startedAtTicks: 0,
                captureIntervalTicks: captureIntervalTicks);
            await store.StartRecordingAsync();
            for (var index = 0; index < persistedSlots; index++)
            {
                var targetTicks = index * captureIntervalTicks;
                await store.AppendSlotAsync(new ManifestSlot(
                    Index: index,
                    TargetTicks: targetTicks,
                    SourceTicks: targetTicks,
                    IsDuplicate: false,
                    AgeTicks: 0,
                    AssetReference: $"screen/slot-{index:D6}"));
            }

            // Simulate power loss: drop the store with no BeginFinalizing/Complete.
            // The on-disk status stays "Recording" and the journal holds all slots.
            await store.DisposeAsync();

            var manifestPath = Path.Combine(root, "sessions", "s8-crash", "manifest.json");
            using (var crashed = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                Assert.Equal("Recording", crashed.RootElement.GetProperty("status").GetString());
            }

            // --- Recovery ---
            var recovered = await SessionRecovery.RecoverAsync(root);

            var session = Assert.Single(recovered);
            Assert.Equal("s8-crash", session.Id);
            Assert.Equal(SessionStatus.Recording, session.PreviousStatus);

            using var repaired = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal("Interrupted", repaired.RootElement.GetProperty("status").GetString());
            // Every slot persisted before the kill survives; loss is bounded to the
            // single in-flight interval (<= 5 s).
            Assert.Equal(persistedSlots, repaired.RootElement.GetProperty("slots").GetArrayLength());
            Assert.Equal(
                captureIntervalTicks,
                repaired.RootElement.GetProperty("captureIntervalTicks").GetInt64());
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
