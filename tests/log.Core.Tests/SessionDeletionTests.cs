using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionDeletionTests
{
    [Fact]
    public async Task SoftDeleteCanBeRestoredAndPurgeRemovesFilesAndRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(110);
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath);
            }

            var deletion = new SessionDeletionService(catalog, root);
            await deletion.SoftDeleteAsync("session-1");
            Assert.Equal("deleting", (await catalog.GetSessionAsync("session-1"))!.Status);
            Assert.True(Directory.Exists(Path.Combine(root, "sessions", "session-1")));

            await deletion.RestoreAsync("session-1");
            Assert.Equal("ready", (await catalog.GetSessionAsync("session-1"))!.Status);

            await deletion.SoftDeleteAsync("session-1");
            await deletion.PurgeAsync("session-1");
            Assert.Null(await catalog.GetSessionAsync("session-1"));
            Assert.False(Directory.Exists(Path.Combine(root, "sessions", "session-1")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PurgeRemovesChildRowsBeforeDeletingTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(110);
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath);
            }

            await catalog.UpsertHighlightAsync(
                "session-1",
                new HighlightEvent("highlight-1", 105, HighlightSource.Screen, 10),
                ordering: 0,
                wallTime: DateTimeOffset.UtcNow);
            await catalog.CreateRenderJobAsync("session-1", "{}");

            var deletion = new SessionDeletionService(catalog, root);
            await deletion.SoftDeleteAsync("session-1");
            await deletion.PurgeAsync("session-1");

            Assert.Null(await catalog.GetSessionAsync("session-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SoftDeleteRejectsActiveCaptureAndRunningRenderSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await using (var activeStore = await SessionStore.CreateAsync(root, "active", "screen", 100, 10))
            {
                await activeStore.StartRecordingAsync();
                await catalog.UpsertSessionAsync(activeStore.Manifest, activeStore.DirectoryPath);
            }

            var deletion = new SessionDeletionService(catalog, root);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => deletion.SoftDeleteAsync("active"));

            await using (var readyStore = await SessionStore.CreateAsync(root, "rendering", "screen", 100, 10))
            {
                await readyStore.StartRecordingAsync();
                await readyStore.CompleteAsync(110);
                await catalog.UpsertSessionAsync(readyStore.Manifest, readyStore.DirectoryPath);
            }

            var job = await catalog.CreateRenderJobAsync("rendering", "{}");
            await catalog.StartRenderJobAsync(job.Id, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => deletion.SoftDeleteAsync("rendering"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PurgeRefusesASessionThatWasNotSoftDeletedAndLeavesItIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(110);
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath);
            }

            var deletion = new SessionDeletionService(catalog, root);

            // Purging a still-ready session would be irreversible data loss; it must refuse.
            await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.PurgeAsync("session-1"));

            Assert.Equal("ready", (await catalog.GetSessionAsync("session-1"))!.Status);
            Assert.True(Directory.Exists(Path.Combine(root, "sessions", "session-1")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsACatalogPathOutsideTheManagedSessionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
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
                    Status = SessionStatus.Deleting
                },
                outside);

            var deletion = new SessionDeletionService(catalog, root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.RestoreAsync("session-1"));
            Assert.Equal("deleting", (await catalog.GetSessionAsync("session-1"))!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SoftDeleteSurvivesARecoveryScanUntilUndo()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog")))
            {
                await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 0, 10);
                await store.StartRecordingAsync();
                await store.CompleteAsync(10);
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath);
                await new SessionDeletionService(catalog, root).SoftDeleteAsync("session-1");
            }

            await using var library = await SessionLibrary.OpenAsync(root);
            Assert.Empty(await library.ListSessionsAsync());
            await library.Deletion.RestoreAsync("session-1");
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
