using System.Text.Json;
using Log.Core.Capture;
using Log.Core.Store;
using Log.Core.Timeline;
using Log.Testing;
using Xunit;

namespace Log.Integration.Tests;

public sealed class HighlightCaptureTests
{
    [Fact]
    public async Task EnqueuedHighlightIsPersistedAsManifestJournalAndCatalogEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var inbox = new HighlightInbox();
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        Assert.True(inbox.TryEnqueue(
            new HighlightEvent("highlight-1", 10, HighlightSource.Screen, 15),
            safetyGate));
        var encoder = new RecordingEncoder();
        var source = new SyntheticFrameSource(
        [
            new FrameSample(0, new byte[] { 1 }, 2, 2),
            new FrameSample(10, new byte[] { 2 }, 2, 2),
            new FrameSample(20, new byte[] { 3 }, 2, 2)
        ]);

        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog.db")))
            {
                await CaptureCoordinator.RunAsync(
                    source,
                    new VirtualClock(),
                    encoder,
                    root,
                    "session-1",
                    new CaptureRunOptions(30, 10, 10),
                    safetyGate,
                    catalog,
                    highlightInbox: inbox);

                var stored = Assert.Single(await catalog.ListHighlightsAsync("session-1"));
                Assert.Equal("highlight-1", stored.Event.Id);
                Assert.Equal(10, stored.Event.EventTicks);
            }

            var sessionDirectory = Path.Combine(root, "sessions", "session-1");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "manifest.json")));
            Assert.Single(manifest.RootElement.GetProperty("highlights").EnumerateArray());
            var journal = await SessionJournalReader.ReadAsync(Path.Combine(sessionDirectory, "events.jsonl"));
            Assert.Contains(journal.Records, record => record.Kind == "highlight" && record.EventId == "highlight-1");
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
    public async Task ConflictingDuplicateHighlightIdDoesNotOverwriteTheDurableEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var inbox = new HighlightInbox();
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var first = new HighlightEvent("duplicate-id", 0, HighlightSource.Screen, 15_000_000);
        var conflicting = new HighlightEvent("duplicate-id", 0, HighlightSource.Screen, 30_000_000);
        Assert.True(inbox.TryEnqueue(first, safetyGate));
        Assert.True(inbox.TryEnqueue(conflicting, safetyGate));

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog.db"));
            await CaptureCoordinator.RunAsync(
                new SyntheticFrameSource([]),
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10),
                safetyGate,
                catalog,
                highlightInbox: inbox);

            var stored = Assert.Single(await catalog.ListHighlightsAsync("session-1"));
            Assert.Equal(first, stored.Event);
            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "sessions", "session-1", "manifest.json")));
            var manifestHighlight = Assert.Single(manifest.RootElement.GetProperty("highlights").EnumerateArray());
            Assert.Equal(15_000_000, manifestHighlight.GetProperty("holdTicks").GetInt64());
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
    public void PausedSafetyGateRejectsHighlightBeforeItReachesTheSessionJournal()
    {
        var inbox = new HighlightInbox();
        var safetyGate = new CaptureSafetyGate();
        var highlight = new HighlightEvent("paused", 100, HighlightSource.Screen, 10);

        Assert.False(inbox.TryEnqueue(highlight, safetyGate));
        safetyGate.Start(indicatorsVerified: true);
        Assert.True(inbox.TryEnqueue(highlight, safetyGate));
    }
}
