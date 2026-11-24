using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class RenderJobRunnerTests
{
    [Fact]
    public async Task CompletesAJobOnlyAfterTheOutputFileIsDurable()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await OpenCatalogWithSessionAsync(root);
            var job = await catalog.CreateRenderJobAsync("session-1", "{\"fps\":30}");
            var outputPath = Path.Combine(root, "export.mp4");
            var runner = new RenderJobRunner(catalog);

            var result = await runner.RunAsync(
                job.Id,
                async cancellationToken =>
                {
                    await File.WriteAllBytesAsync(outputPath, [1], cancellationToken);
                    return outputPath;
                });

            Assert.Equal("completed", result.Status);
            Assert.Equal(outputPath, result.OutputPath);
            Assert.NotNull(result.StartedAt);
            Assert.NotNull(result.FinishedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordsAFailedRenderAndPreservesTheOriginalException()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await OpenCatalogWithSessionAsync(root);
            var job = await catalog.CreateRenderJobAsync("session-1", "{}");
            var runner = new RenderJobRunner(catalog);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
                job.Id,
                _ => Task.FromException<string>(new InvalidOperationException("synthetic render failure"))));

            var failed = await catalog.GetRenderJobAsync(job.Id);
            Assert.Equal("failed", failed!.Status);
            Assert.NotNull(failed.FinishedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertsCancellationIntoACanceledTerminalJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await OpenCatalogWithSessionAsync(root);
            var job = await catalog.CreateRenderJobAsync("session-1", "{}");
            var runner = new RenderJobRunner(catalog);
            using var cancellation = new CancellationTokenSource();

            var result = await runner.RunAsync(
                job.Id,
                token =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<string>(token);
                },
                cancellation.Token);

            Assert.Equal("canceled", result.Status);
            Assert.Null(result.OutputPath);
            Assert.NotNull(result.FinishedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringPublicationDoesNotCancelThePublishedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await OpenCatalogWithSessionAsync(root);
            var job = await catalog.CreateRenderJobAsync("session-1", "{}");
            var runner = new RenderJobRunner(catalog);
            using var cancellation = new CancellationTokenSource();
            var stagingPath = Path.Combine(root, ".log-stage.mp4");
            var outputPath = Path.Combine(root, "export.mp4");

            var result = await runner.RunWithAsyncPublicationAsync(
                job.Id,
                async token =>
                {
                    await File.WriteAllBytesAsync(stagingPath, [1, 2, 3], token);
                    return stagingPath;
                },
                stagingOutputPath =>
                {
                    File.Move(stagingOutputPath, outputPath);
                    cancellation.Cancel();
                    return Task.FromResult(outputPath);
                },
                cancellation.Token);

            Assert.Equal("completed", result.Status);
            Assert.Equal(outputPath, result.OutputPath);
            Assert.False(File.Exists(stagingPath));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelsAQueuedJobWithoutInvokingTheRenderer()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await OpenCatalogWithSessionAsync(root);
            var job = await catalog.CreateRenderJobAsync("session-1", "{}");
            var runner = new RenderJobRunner(catalog);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var invoked = false;

            var result = await runner.RunAsync(
                job.Id,
                _ =>
                {
                    invoked = true;
                    return Task.FromResult(Path.Combine(root, "never.mp4"));
                },
                cancellation.Token);

            Assert.Equal("canceled", result.Status);
            Assert.False(invoked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CatalogStore> OpenCatalogWithSessionAsync(string root)
    {
        var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
        await catalog.UpsertSessionAsync(new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 100,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        }, Path.Combine(root, "sessions", "session-1"));
        return catalog;
    }
}
