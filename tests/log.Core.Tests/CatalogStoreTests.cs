using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class CatalogStoreTests
{
    [Fact]
    public async Task RejectsACorruptSharedCatalogFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "catalog");
        Directory.CreateDirectory(databasePath);
        await File.WriteAllTextAsync(Path.Combine(databasePath, "settings.json"), "not json");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => CatalogStore.OpenAsync(databasePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializesWalSchemaAndIndexesInterruptedSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog")))
            {
                var manifest = new SessionManifest
                {
                    Id = "session-1",
                    SourceMode = "screen",
                    StartedAtTicks = 100,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Interrupted
                };

                await catalog.UpsertSessionAsync(manifest, Path.Combine(root, "session-1"));
                var interrupted = await catalog.GetInterruptedSessionsAsync();

                var session = Assert.Single(interrupted);
                Assert.Equal("session-1", session.Id);
                Assert.Equal("session-1", session.Name);
                Assert.Equal(100, session.StartedAtTicks);
                Assert.Equal("interrupted", session.Status);

                var highlight = new HighlightEvent(
                    "highlight-1",
                    125,
                    HighlightSource.Both,
                    TimeSpan.FromMilliseconds(1500).Ticks,
                    screenAssetReference: "highlights/hl_0001.png",
                    cameraAssetReference: "highlights/hl_0001.cam.png");
                var wallTime = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
                await catalog.UpsertHighlightAsync("session-1", highlight, 2, wallTime);
                var storedHighlight = Assert.Single(await catalog.ListHighlightsAsync("session-1"));
                Assert.Equal(highlight, storedHighlight.Event);
                Assert.Equal(wallTime, storedHighlight.WallTime);
                Assert.Equal(2, storedHighlight.Ordering);

                var asset = new CatalogAsset(
                    "highlight-asset-1",
                    "session-1",
                    "highlight",
                    "highlights/hl_0001.png",
                    new string('a', 64),
                    123,
                    wallTime);
                await catalog.UpsertAssetAsync(asset);
                Assert.Equal(asset, Assert.Single(await catalog.ListAssetsAsync("session-1")));
                await catalog.DeleteAssetAsync("session-1", asset.Id);
                Assert.Empty(await catalog.ListAssetsAsync("session-1"));
                await Assert.ThrowsAsync<ArgumentException>(() => catalog.UpsertAssetAsync(
                    asset with { RelativePath = "../outside.png" }));

                var editedHighlight = new HighlightEvent(
                    highlight.Id,
                    highlight.EventTicks,
                    highlight.Source,
                    TimeSpan.FromMilliseconds(2500).Ticks,
                    included: false,
                    highlight.ScreenAssetReference,
                    highlight.CameraAssetReference);
                await catalog.UpdateHighlightAsync("session-1", editedHighlight);
                var edited = Assert.Single(await catalog.ListHighlightsAsync("session-1"));
                Assert.Equal(editedHighlight, edited.Event);
                await catalog.DeleteHighlightAsync("session-1", "highlight-1");
                Assert.Empty(await catalog.ListHighlightsAsync("session-1"));

                await catalog.RenameSessionAsync("session-1", "Study session");
                await catalog.UpdateSessionMetadataAsync("session-1", "Study session", "A short description.");
                var listed = await catalog.ListSessionsAsync();
                Assert.Equal("Study session", Assert.Single(listed).Name);
                Assert.Equal("A short description.", listed[0].Description);

                await catalog.SoftDeleteSessionAsync("session-1");
                Assert.Empty(await catalog.ListSessionsAsync());
                Assert.Single(await catalog.ListSessionsAsync(includeDeleting: true));

                await catalog.RestoreSessionAsync("session-1", SessionStatus.Interrupted);
                Assert.Equal("interrupted", (await catalog.GetSessionAsync("session-1"))!.Status);

                await catalog.SetSettingAsync("hotkey", "Ctrl+Shift+H");
                Assert.Equal("Ctrl+Shift+H", await catalog.GetSettingAsync("hotkey"));
                await catalog.SetSettingAsync("hotkey", "Ctrl+Alt+H");
                Assert.Equal("Ctrl+Alt+H", await catalog.GetSettingAsync("hotkey"));
                Assert.Null(await catalog.GetSettingAsync("missing"));
                await Assert.ThrowsAsync<ArgumentException>(() => catalog.RenameSessionAsync("session-1", new string('x', 201)));

                var activeManifest = new SessionManifest
                {
                    Id = "active-session",
                    SourceMode = "screen",
                    StartedAtTicks = 100,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Recording
                };
                await catalog.UpsertSessionAsync(activeManifest, Path.Combine(root, "active-session"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CreateRenderJobAsync(
                    "active-session",
                    "{}"));

                var job = await catalog.CreateRenderJobAsync("session-1", "{\"fps\":30}");
                Assert.Equal("queued", job.Status);
                await catalog.StartRenderJobAsync(job.Id, wallTime);
                var running = await catalog.GetRenderJobAsync(job.Id);
                Assert.Equal("running", running!.Status);
                Assert.Equal(wallTime, running.StartedAt);

                var finishedAt = wallTime.AddMinutes(2);
                var outputPath = Path.Combine(root, "export.mp4");
                await File.WriteAllBytesAsync(outputPath, [1]);
                await catalog.FinishRenderJobAsync(
                    job.Id,
                    RenderJobStatus.Completed,
                    outputPath,
                    finishedAt);
                var completed = await catalog.GetRenderJobAsync(job.Id);
                Assert.Equal("completed", completed!.Status);
                Assert.Equal(finishedAt, completed.FinishedAt);
                Assert.Equal(outputPath, completed.OutputPath);
                await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.StartRenderJobAsync(job.Id, wallTime));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ListsHighlightsByOrderingThenEventTicks()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await catalog.UpsertSessionAsync(
                new SessionManifest
                {
                    Id = "session-1",
                    SourceMode = "screen",
                    StartedAtTicks = 0,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Ready
                },
                Path.Combine(root, "sessions", "session-1"));

            var wall = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            // Inserted out of final order; ordering wins, then event (qpc) ticks.
            await catalog.UpsertHighlightAsync("session-1", new HighlightEvent("hl-a", 100, HighlightSource.Screen, 15_000_000), 0, wall);
            await catalog.UpsertHighlightAsync("session-1", new HighlightEvent("hl-c", 10, HighlightSource.Screen, 15_000_000), 1, wall);
            await catalog.UpsertHighlightAsync("session-1", new HighlightEvent("hl-b", 50, HighlightSource.Screen, 15_000_000), 0, wall);

            var listed = await catalog.ListHighlightsAsync("session-1");

            Assert.Equal(["hl-b", "hl-a", "hl-c"], listed.Select(highlight => highlight.Event.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoversOnlyRunningRenderJobsAfterACrash()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await catalog.UpsertSessionAsync(
                new SessionManifest
                {
                    Id = "session-1",
                    SourceMode = "screen",
                    StartedAtTicks = 100,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Ready
                },
                Path.Combine(root, "sessions", "session-1"));

            var wallTime = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

            // One job left mid-render ("running") when the app crashed.
            var running = await catalog.CreateRenderJobAsync("session-1", "{}");
            await catalog.StartRenderJobAsync(running.Id, wallTime);

            // One job that never started, and one that finished before the crash.
            var queued = await catalog.CreateRenderJobAsync("session-1", "{}");
            var completed = await catalog.CreateRenderJobAsync("session-1", "{}");
            await catalog.StartRenderJobAsync(completed.Id, wallTime);
            await File.WriteAllBytesAsync(Path.Combine(root, "done.mp4"), [1]);
            await catalog.FinishRenderJobAsync(
                completed.Id,
                RenderJobStatus.Completed,
                Path.Combine(root, "done.mp4"),
                wallTime.AddMinutes(1));

            var recoveredAt = wallTime.AddMinutes(5);
            var count = await catalog.RecoverRunningRenderJobsAsync(recoveredAt);

            Assert.Equal(1, count);
            var recoveredJob = await catalog.GetRenderJobAsync(running.Id);
            Assert.Equal("failed", recoveredJob!.Status);
            Assert.Equal(recoveredAt, recoveredJob.FinishedAt);
            Assert.Equal("queued", (await catalog.GetRenderJobAsync(queued.Id))!.Status);
            Assert.Equal("completed", (await catalog.GetRenderJobAsync(completed.Id))!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionUpsertRemovesCatalogHighlightsMissingFromTheAuthoritativeManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            SessionManifest manifest;
            string directoryPath;
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendHighlightAsync(new HighlightEvent("highlight-1", 5, HighlightSource.Screen, 10));
                await store.CompleteAsync(10);
                manifest = store.Manifest;
                directoryPath = store.DirectoryPath;
            }

            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await catalog.UpsertSessionAsync(manifest, directoryPath);
            Assert.Single(await catalog.ListHighlightsAsync("session-1"));

            manifest.Highlights.Clear();
            await catalog.UpsertSessionAsync(manifest, directoryPath);

            Assert.Empty(await catalog.ListHighlightsAsync("session-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionUpsertSupportsManyHighlights()
    {
        const int highlightCount = 33_000;
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var manifest = new SessionManifest
            {
                Id = "session-many-highlights",
                SourceMode = "screen",
                StartedAtTicks = 0,
                CaptureIntervalTicks = 10,
                Status = SessionStatus.Ready
            };
            for (var index = 0; index < highlightCount; index++)
            {
                manifest.Highlights.Add(new HighlightEvent(
                    $"highlight-{index:D5}",
                    index,
                    HighlightSource.Screen,
                    1));
            }

            await catalog.UpsertSessionAsync(manifest, Path.Combine(root, "session-many-highlights"));

            Assert.Equal(highlightCount, (await catalog.ListHighlightsAsync(manifest.Id)).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionUpsertPreservesExistingHighlightWallTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var manifest = new SessionManifest
            {
                Id = "session-wall-time",
                SourceMode = "screen",
                StartedAtTicks = 0,
                CaptureIntervalTicks = 10,
                Status = SessionStatus.Ready
            };
            var directoryPath = Path.Combine(root, "session-wall-time");
            await catalog.UpsertSessionAsync(manifest, directoryPath);

            var highlight = new HighlightEvent("highlight-wall-time", 5, HighlightSource.Screen, 10);
            var originalWallTime = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            await catalog.UpsertHighlightAsync(manifest.Id, highlight, 0, originalWallTime);
            manifest.Highlights.Add(highlight);

            await catalog.UpsertSessionAsync(manifest, directoryPath);

            Assert.Equal(originalWallTime, Assert.Single(await catalog.ListHighlightsAsync(manifest.Id)).WallTime);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionUpsertRollsBackWhenAHighlightCannotBeMirrored()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var manifest = new SessionManifest
            {
                Id = "session-1",
                SourceMode = "screen",
                StartedAtTicks = 100,
                CaptureIntervalTicks = 10,
                Status = SessionStatus.Ready
            };
            manifest.Highlights.Add(new HighlightEvent("highlight-1", 105, HighlightSource.Screen, 10));
            manifest.Highlights.Add(null!);

            await Assert.ThrowsAsync<ArgumentNullException>(() => catalog.UpsertSessionAsync(
                manifest,
                Path.Combine(root, "sessions", "session-1")));

            Assert.Null(await catalog.GetSessionAsync("session-1"));
            Assert.Empty(await catalog.ListHighlightsAsync("session-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
