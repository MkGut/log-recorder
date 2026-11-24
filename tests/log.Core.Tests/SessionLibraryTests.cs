using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionLibraryTests
{
    [Fact]
    public async Task OpeningLibraryRebuildsCatalogAndExposesRecoveryReport()
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

            await using var library = await SessionLibrary.OpenAsync(root);
            Assert.Empty(library.RecoveryReport.Recovered);
            var session = Assert.Single(await library.ListSessionsAsync());
            Assert.Equal("ready", session.Id);
            Assert.Equal(Path.Combine(root, "sessions", "ready"), session.DirectoryPath);

            var card = Assert.Single(await library.ListSessionCardsAsync());
            Assert.Equal("ready", card.Session.Id);
            Assert.Equal(SessionStatus.Ready, card.Summary.Status);
            Assert.Equal(10, card.Summary.ActiveDurationTicks);

            await library.UpdateSessionMetadataAsync("ready", "Study block", "Focus notes");
            await using (var reopened = await SessionStore.OpenAsync(root, "ready"))
            {
                Assert.Equal("Study block", reopened.Manifest.Name);
            Assert.Equal("Focus notes", reopened.Manifest.Description);
            }

            var renderInput = await library.GetSessionRenderInputAsync("ready");
            Assert.Equal("screen", renderInput.SourceMode);
            Assert.Single(renderInput.Slots);

            await library.MarkSessionExportedAsync("ready");
            var exported = Assert.Single(await library.ListSessionsAsync());
            Assert.Equal("exported", exported.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ListsSessionsAndCardsNewestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // Created out of chronological order to prove the ordering is by start time.
            foreach (var (id, startTicks) in new[] { ("mid", 200L), ("early", 100L), ("late", 300L) })
            {
                await using var store = await SessionStore.CreateAsync(root, id, "screen", startTicks, 10);
                await store.StartRecordingAsync();
                await store.CompleteAsync(startTicks + 10);
            }

            await using var library = await SessionLibrary.OpenAsync(root);

            Assert.Equal(
                ["late", "mid", "early"],
                (await library.ListSessionsAsync()).Select(session => session.Id));
            Assert.Equal(
                ["late", "mid", "early"],
                (await library.ListSessionCardsAsync()).Select(card => card.Session.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningLibraryFailsAbandonedRenderJobsAndRemovesOnlyTheirStageFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var finalPath = Path.Combine(outputDirectory, "requested.mp4");
            var stagingPath = Path.Combine(outputDirectory, $".log-{Guid.NewGuid():N}.mp4");
            var competingFileBytes = new byte[] { 9, 6, 3 };
            await File.WriteAllBytesAsync(finalPath, competingFileBytes);
            await File.WriteAllBytesAsync(stagingPath, [1, 2, 3]);
            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog")))
            {
                var manifest = new SessionManifest
                {
                    Id = "ready",
                    SourceMode = "screen",
                    StartedAtTicks = 0,
                    StoppedAtTicks = 10,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Ready
                };
                manifest.Tracks["screen"] = "screen/";
                await catalog.UpsertSessionAsync(manifest, Path.Combine(root, "sessions", "ready"));
                var parametersJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    outputPath = finalPath,
                    stagingOutputPath = stagingPath
                });
                var job = await catalog.CreateRenderJobAsync("ready", parametersJson);
                await catalog.StartRenderJobAsync(job.Id, DateTimeOffset.UtcNow);
            }

            await using var library = await SessionLibrary.OpenAsync(root);
            var recoveredJob = Assert.Single(await library.Catalog.ListRenderJobsAsync("ready"));
            Assert.Equal("failed", recoveredJob.Status);
            Assert.NotNull(recoveredJob.FinishedAt);
            Assert.False(File.Exists(stagingPath));
            Assert.Equal(competingFileBytes, await File.ReadAllBytesAsync(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningLibraryRecoversPublishedOutputOnlyWhenFingerprintMatches(bool outputMatches)
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var expectedBytes = new byte[] { 1, 2, 3, 4 };
            var finalBytes = outputMatches ? expectedBytes : new byte[] { 9, 6, 3 };
            var outputPath = await CreateAbandonedPublishedJobAsync(root, expectedBytes, finalBytes);

            await using var library = await SessionLibrary.OpenAsync(root);
            var recoveredJob = Assert.Single(await library.Catalog.ListRenderJobsAsync("published"));
            var recoveredSession = Assert.Single(await library.ListSessionsAsync());

            Assert.Equal(outputMatches ? "completed" : "failed", recoveredJob.Status);
            Assert.Equal(outputMatches ? outputPath : null, recoveredJob.OutputPath);
            Assert.Equal(outputMatches ? "exported" : "ready", recoveredSession.Status);
            Assert.Equal(finalBytes, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveredSessionCanBeMarkedExportedAfterSuccessfulRender()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "interrupted", "screen", 100, 10))
            {
                await store.StartRecordingAsync();
            }

            await using var library = await SessionLibrary.OpenAsync(root);
            var recovered = Assert.Single(await library.ListSessionsAsync());
            Assert.Equal("interrupted", recovered.Status);

            await library.MarkSessionExportedAsync("interrupted");

            var exported = Assert.Single(await library.ListSessionsAsync());
            Assert.Equal("exported", exported.Status);
            await using var reopened = await SessionStore.OpenAsync(root, "interrupted");
            Assert.Equal(SessionStatus.Exported, reopened.Manifest.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncompleteCatalogRowsDoNotHideHealthyLibraryCards()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "healthy", "screen", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(10);
            }

            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog")))
            {
                var incomplete = new SessionManifest
                {
                    Id = "incomplete",
                    SourceMode = "screen",
                    StartedAtTicks = 0,
                    StoppedAtTicks = 10,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Ready
                };
                incomplete.Tracks["screen"] = "screen/";
                await catalog.UpsertSessionAsync(
                    incomplete,
                    Path.Combine(root, "sessions", "incomplete"));
                var malformedDirectory = Path.Combine(root, "sessions", "malformed");
                Directory.CreateDirectory(malformedDirectory);
                await File.WriteAllTextAsync(Path.Combine(malformedDirectory, "manifest.json"), "{not-json");
                var malformed = new SessionManifest
                {
                    Id = "malformed",
                    SourceMode = "screen",
                    StartedAtTicks = 0,
                    StoppedAtTicks = 10,
                    CaptureIntervalTicks = 10,
                    Status = SessionStatus.Ready
                };
                malformed.Tracks["screen"] = "screen/";
                await catalog.UpsertSessionAsync(malformed, malformedDirectory);
            }

            await using var library = await SessionLibrary.OpenAsync(root);
            var cards = await library.ListSessionCardsAsync();
            Assert.Equal(["healthy"], cards.Select(card => card.Session.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CreateAbandonedPublishedJobAsync(
        string root,
        byte[] expectedOutputBytes,
        byte[] finalOutputBytes)
    {
        const string sessionId = "published";
        await using (var store = await SessionStore.CreateAsync(root, sessionId, "screen", 0, 10))
        {
            await store.StartRecordingAsync();
            await store.CompleteAsync(10);
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await catalog.UpsertSessionAsync(
                store.Manifest,
                Path.Combine(root, "sessions", sessionId));

            var outputDirectory = Path.Combine(root, "exports");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "published.mp4");
            var stagingPath = Path.Combine(outputDirectory, $".log-{Guid.NewGuid():N}.mp4");
            await File.WriteAllBytesAsync(outputPath, finalOutputBytes);
            var parametersJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                outputPath,
                stagingOutputPath = stagingPath,
                publishedOutputSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(expectedOutputBytes)),
                publishedOutputLength = expectedOutputBytes.Length
            });
            var job = await catalog.CreateRenderJobAsync(sessionId, parametersJson);
            await catalog.StartRenderJobAsync(job.Id, DateTimeOffset.UtcNow);
            return outputPath;
        }
    }
}
