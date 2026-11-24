using System.Security.AccessControl;
using System.Security.Principal;
using Log.Core.Render;
using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionExportTests
{
    [Fact]
    public async Task ExportsWithAuthoritativeInputAndPreservesEveryRawTrack()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen+camera", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(10);
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, "screen", "raw.bin"), [1, 2, 3]);
                await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, "camera", "raw.bin"), [4, 5]);
            }

            await using var library = await SessionLibrary.OpenAsync(root);
            var request = new SessionExportRequest(
                CreateRenderSettings(),
                outputDirectory,
                "session.mp4");
            string? rendererOutputPath = null;
            string? finalOutputPath = null;
            var result = await library.Export.ExportAsync(
                "session-1",
                request,
                async (context, cancellationToken) =>
                {
                    rendererOutputPath = context.OutputPath;
                    finalOutputPath = context.FinalOutputPath;
                    Assert.Equal("session-1", context.SessionId);
                    Assert.Equal("screen+camera", context.RenderInput.SourceMode);
                    Assert.Single(context.RenderInput.Slots);
                    await File.WriteAllBytesAsync(context.OutputPath, [1], cancellationToken);
                });

            Assert.Equal("completed", result.Job.Status);
            Assert.Equal(result.OutputPath, finalOutputPath);
            Assert.Equal(result.OutputPath, result.Job.OutputPath);
            Assert.NotEqual(result.OutputPath, rendererOutputPath);
            Assert.NotNull(rendererOutputPath);
            Assert.False(File.Exists(rendererOutputPath));
            Assert.True(File.Exists(result.OutputPath));
            var publishedBytes = await File.ReadAllBytesAsync(result.OutputPath);
            using (var parameters = System.Text.Json.JsonDocument.Parse(result.Job.ParametersJson))
            {
                var rootElement = parameters.RootElement;
                Assert.Equal(publishedBytes.LongLength, rootElement.GetProperty("publishedOutputLength").GetInt64());
                Assert.Equal(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(publishedBytes)),
                    rootElement.GetProperty("publishedOutputSha256").GetString());
            }
            Assert.True(File.Exists(Path.Combine(root, "sessions", "session-1", "screen", "raw.bin")));
            Assert.True(File.Exists(Path.Combine(root, "sessions", "session-1", "camera", "raw.bin")));
            Assert.Contains("outputPath", result.Job.ParametersJson, StringComparison.Ordinal);
            Assert.Equal("exported", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationLeavesTheSessionReadyAndDoesNotInvokeTheRenderer()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var invoked = false;

            var result = await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), Path.Combine(root, "exports")),
                (_, _) =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                },
                cancellation.Token);

            Assert.Equal("canceled", result.Job.Status);
            Assert.False(invoked);
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedRendererCleansItsOwnedPartialOutputAndLeavesTheSessionReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            var outputDirectory = Path.Combine(root, "exports");
            string? outputPath = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() => library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputDirectory, "failed.mp4"),
                async (context, cancellationToken) =>
                {
                    outputPath = context.OutputPath;
                    await File.WriteAllBytesAsync(context.OutputPath, [1], cancellationToken);
                    throw new InvalidOperationException("synthetic renderer failure");
                }));

            Assert.NotNull(outputPath);
            Assert.False(File.Exists(outputPath));
            var job = Assert.Single(await library.Catalog.ListRenderJobsAsync("session-1"));
            Assert.Equal("failed", job.Status);
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationMidRenderCleansTheOwnedPartialOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            using var cancellation = new CancellationTokenSource();
            string? outputPath = null;

            var result = await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), Path.Combine(root, "exports"), "canceled.mp4"),
                async (context, cancellationToken) =>
                {
                    // Write a partial output, then cancel as an encoder would mid-job.
                    outputPath = context.OutputPath;
                    await File.WriteAllBytesAsync(context.OutputPath, [1], cancellationToken);
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                },
                cancellation.Token);

            Assert.Equal("canceled", result.Job.Status);
            Assert.NotNull(outputPath);
            Assert.False(File.Exists(outputPath));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAfterRendererReturnsStillCancelsAndKeepsRawTracks()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateDualReadySessionWithRawAssetsAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            using var cancellation = new CancellationTokenSource();
            string? outputPath = null;

            var result = await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), Path.Combine(root, "exports"), "race.mp4"),
                async (context, cancellationToken) =>
                {
                    outputPath = context.OutputPath;
                    await File.WriteAllBytesAsync(context.OutputPath, [1, 2, 3], cancellationToken);
                    cancellation.Cancel();
                },
                cancellation.Token);

            Assert.Equal("canceled", result.Job.Status);
            Assert.NotNull(outputPath);
            Assert.False(File.Exists(outputPath));
            Assert.True(File.Exists(Path.Combine(root, "sessions", "session-1", "screen", "slot-000000.png")));
            Assert.True(File.Exists(Path.Combine(root, "sessions", "session-1", "camera", "slot-000000.png")));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedExportsUseFixedPresetAndKeepIndependentRawTracks()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateDualReadySessionWithRawAssetsAsync(root);
            var screenPath = Path.Combine(root, "sessions", "session-1", "screen", "slot-000000.png");
            var cameraPath = Path.Combine(root, "sessions", "session-1", "camera", "slot-000000.png");
            var screenBefore = await File.ReadAllBytesAsync(screenPath);
            var cameraBefore = await File.ReadAllBytesAsync(cameraPath);
            await using var library = await SessionLibrary.OpenAsync(root);
            var firstSettings = new RenderSettings(
                new CompositionSettings(
                    new PixelSize(640, 360),
                    new PipLayout(true, 0.20, 0.04, PipCorner.TopLeft),
                    24)
                {
                    Mode = CompositionMode.PictureInPicture,
                    CameraSourceSize = new PixelSize(320, 240)
                },
                new TimerSettings(enabled: true, opacity: 0.7),
                watermarkEnabled: true);
            var secondSettings = new RenderSettings(
                new CompositionSettings(
                    new PixelSize(640, 360),
                    new PipLayout(false, 0, 0, PipCorner.TopLeft),
                    24)
                {
                    Mode = CompositionMode.ScreenOnly,
                    CameraSourceSize = new PixelSize(320, 240)
                },
                new TimerSettings(enabled: false),
                watermarkEnabled: true);

            await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(firstSettings, Path.Combine(root, "exports"), "first.mp4"),
                async (context, cancellationToken) =>
                    await File.WriteAllBytesAsync(context.OutputPath, [1], cancellationToken));
            var persistedAfterFirst = await library.GetLastExportSettingsAsync("session-1");
            Assert.NotNull(persistedAfterFirst);
            Assert.Equal(30, persistedAfterFirst!.Composition.OutputFramesPerSecond);
            Assert.Equal(CompositionMode.PictureInPicture, persistedAfterFirst.Composition.Mode);
            Assert.True(persistedAfterFirst.Composition.Pip.Visible);
            Assert.Equal(RenderPreset.PictureInPictureWidthFraction, persistedAfterFirst.Composition.Pip.WidthFraction);
            Assert.Equal(RenderPreset.PictureInPictureInsetFraction, persistedAfterFirst.Composition.Pip.InsetFraction);
            Assert.Equal(PipCorner.BottomRight, persistedAfterFirst.Composition.Pip.Corner);
            Assert.True(persistedAfterFirst.Timer.Enabled);
            Assert.Equal(0.75, persistedAfterFirst.Timer.Opacity);
            Assert.False(persistedAfterFirst.WatermarkEnabled);

            await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(secondSettings, Path.Combine(root, "exports"), "second.mp4"),
                async (context, cancellationToken) =>
                    await File.WriteAllBytesAsync(context.OutputPath, [2], cancellationToken));
            var persistedAfterSecond = await library.GetLastExportSettingsAsync("session-1");
            Assert.NotNull(persistedAfterSecond);
            Assert.Equal(30, persistedAfterSecond!.Composition.OutputFramesPerSecond);
            Assert.Equal(CompositionMode.PictureInPicture, persistedAfterSecond.Composition.Mode);
            Assert.True(persistedAfterSecond.Composition.Pip.Visible);
            Assert.True(persistedAfterSecond.Timer.Enabled);
            Assert.False(persistedAfterSecond.WatermarkEnabled);
            Assert.Equal(screenBefore, await File.ReadAllBytesAsync(screenPath));
            Assert.Equal(cameraBefore, await File.ReadAllBytesAsync(cameraPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveredInterruptedDualSessionCanBeExportedWithoutLosingEitherTrack()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(root, "session-1", "screen+camera", 0, 10))
            {
                await store.StartRecordingAsync();
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 0, 0, 0, false, 0, "screen/slot-000000.png"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "camera", 0, 0, 0, false, 0, "camera/slot-000000.png"));
            }

            await using var library = await SessionLibrary.OpenAsync(root);
            Assert.Equal("interrupted", Assert.Single(await library.ListSessionsAsync()).Status);
            var result = await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), Path.Combine(root, "exports"), "recovered.mp4"),
                async (context, cancellationToken) =>
                {
                    Assert.Single(context.RenderInput.Slots);
                    Assert.Equal("screen/slot-000000.png", context.RenderInput.Slots[0].ScreenAssetReference);
                    Assert.Equal("camera/slot-000000.png", context.RenderInput.Slots[0].CameraAssetReference);
                    await File.WriteAllBytesAsync(context.OutputPath, [1], cancellationToken);
                });

            Assert.Equal("completed", result.Job.Status);
            Assert.Equal("exported", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportStoragePreflightBlocksAJobBeforeRendering()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            var invoked = false;

            await Assert.ThrowsAsync<InsufficientStorageException>(() => library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(
                    CreateRenderSettings(),
                    Path.Combine(root, "exports"),
                    estimatedOutputBytes: long.MaxValue),
                (_, _) =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                }));

            Assert.False(invoked);
            Assert.Empty(await library.Catalog.ListRenderJobsAsync("session-1"));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnusableOutputDirectoryLeavesTheSessionReadyAndDoesNotCreateAJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            var outputPathOccupiedByFile = Path.Combine(root, "exports");
            var originalBytes = new byte[] { 7, 8, 9 };
            await File.WriteAllBytesAsync(outputPathOccupiedByFile, originalBytes);
            var rendererInvoked = false;

            await Assert.ThrowsAsync<IOException>(() => library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputPathOccupiedByFile, "result.mp4"),
                (_, _) =>
                {
                    rendererInvoked = true;
                    return Task.CompletedTask;
                }));

            Assert.False(rendererInvoked);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(outputPathOccupiedByFile));
            Assert.Empty(await library.Catalog.ListRenderJobsAsync("session-1"));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AccessDeniedOutputDirectoryKeepsSessionReadyAndFailsRender()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var directoryInfo = new DirectoryInfo(outputDirectory);
        DirectorySecurity? originalAccess = null;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            await CreateReadySessionAsync(root);
            originalAccess = directoryInfo.GetAccessControl(AccessControlSections.Access);
            await using var library = await SessionLibrary.OpenAsync(root);
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
            var deniedAccess = directoryInfo.GetAccessControl(AccessControlSections.Access);
            deniedAccess.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.CreateFiles,
                AccessControlType.Deny));

            try
            {
                directoryInfo.SetAccessControl(deniedAccess);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.Export.ExportAsync(
                    "session-1",
                    new SessionExportRequest(CreateRenderSettings(), outputDirectory, "denied.mp4"),
                    (context, cancellationToken) => File.WriteAllBytesAsync(
                        context.OutputPath,
                        [1, 2, 3],
                        cancellationToken)));
            }
            finally
            {
                directoryInfo.SetAccessControl(originalAccess);
            }

            Assert.False(File.Exists(Path.Combine(outputDirectory, "denied.mp4")));
            var jobs = await library.Catalog.ListRenderJobsAsync("session-1");
            Assert.True(jobs.Count <= 1);
            if (jobs.Count == 1)
            {
                Assert.Equal("failed", jobs[0].Status);
            }

            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            try
            {
                if (originalAccess is not null && Directory.Exists(outputDirectory))
                {
                    directoryInfo.SetAccessControl(originalAccess);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExistingOutputCollisionGetsUniquePathWithoutOverwritingTheExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            Directory.CreateDirectory(outputDirectory);
            var existingOutput = Path.Combine(outputDirectory, "result.mp4");
            var originalBytes = new byte[] { 1, 3, 5 };
            var renderedBytes = new byte[] { 2, 4, 6 };
            await File.WriteAllBytesAsync(existingOutput, originalBytes);
            await using var library = await SessionLibrary.OpenAsync(root);

            var result = await library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputDirectory, "result.mp4"),
                (context, cancellationToken) => File.WriteAllBytesAsync(
                    context.OutputPath,
                    renderedBytes,
                    cancellationToken));

            Assert.Equal("completed", result.Job.Status);
            Assert.Equal(Path.Combine(outputDirectory, "result (2).mp4"), result.OutputPath);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(existingOutput));
            Assert.Equal(renderedBytes, await File.ReadAllBytesAsync(result.OutputPath));
            Assert.Equal("exported", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DestinationCreatedDuringRenderIsPreservedWhenPublicationCollides()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            var competingFileBytes = new byte[] { 7, 4, 1 };
            string? stagingPath = null;
            string? finalPath = null;

            await Assert.ThrowsAsync<IOException>(() => library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputDirectory, "raced.mp4"),
                async (context, cancellationToken) =>
                {
                    var rendererOutputPath = context.OutputPath;
                    var requestedOutputPath = context.FinalOutputPath;
                    stagingPath = rendererOutputPath;
                    finalPath = requestedOutputPath;
                    Assert.NotEqual(rendererOutputPath, requestedOutputPath);
                    await File.WriteAllBytesAsync(rendererOutputPath, [1, 2, 3], cancellationToken);
                    // Model another process creating the selected name after
                    // allocation but before the export can publish its output.
                    await File.WriteAllBytesAsync(requestedOutputPath, competingFileBytes, cancellationToken);
                }));

            Assert.NotNull(stagingPath);
            Assert.NotNull(finalPath);
            Assert.False(File.Exists(stagingPath));
            Assert.Equal(competingFileBytes, await File.ReadAllBytesAsync(finalPath));
            Assert.Equal("failed", Assert.Single(await library.Catalog.ListRenderJobsAsync("session-1")).Status);
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendererCompletionWithoutNonemptyOutputFailsAndPreservesSession(bool writeEmptyFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            string? outputPath = null;

            await Assert.ThrowsAsync<InvalidDataException>(() => library.Export.ExportAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), Path.Combine(root, "exports"), "missing.mp4"),
                async (context, cancellationToken) =>
                {
                    outputPath = context.OutputPath;
                    if (writeEmptyFile)
                    {
                        await File.WriteAllBytesAsync(context.OutputPath, [], cancellationToken);
                    }
                }));

            Assert.NotNull(outputPath);
            Assert.False(File.Exists(outputPath));
            Assert.Equal("failed", Assert.Single(await library.Catalog.ListRenderJobsAsync("session-1")).Status);
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewRemainsReadyAcrossRestartThenSavePublishesWithoutOverwritingAndPreservesSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateDualReadySessionWithRawAssetsAsync(root);
            string jobId;
            string previewPath;
            string suggestedPath;
            var renderedBytes = new byte[] { 2, 4, 6, 8 };
            await using (var library = await SessionLibrary.OpenAsync(root))
            {
                var nonPresetSettings = new RenderSettings(
                    new CompositionSettings(
                        new PixelSize(640, 360),
                        new PipLayout(false, 0, 0, PipCorner.TopLeft),
                        24)
                    {
                        Mode = CompositionMode.ScreenOnly,
                        CameraSourceSize = new PixelSize(320, 240)
                    },
                    new TimerSettings(enabled: false),
                    watermarkEnabled: true);
                var preview = await library.Export.RenderPreviewAsync(
                    "session-1",
                    new SessionExportRequest(nonPresetSettings, outputDirectory, "result.mp4"),
                    async (context, cancellationToken) =>
                    {
                        Assert.Equal(30, context.RenderSettings.Composition.OutputFramesPerSecond);
                        Assert.Equal(CompositionMode.PictureInPicture, context.RenderSettings.Composition.Mode);
                        Assert.True(context.RenderSettings.Composition.Pip.Visible);
                        Assert.Equal(RenderPreset.PictureInPictureWidthFraction,
                            context.RenderSettings.Composition.Pip.WidthFraction);
                        Assert.Equal(PipCorner.BottomRight, context.RenderSettings.Composition.Pip.Corner);
                        Assert.True(context.RenderSettings.Timer.Enabled);
                        Assert.Equal(0.75, context.RenderSettings.Timer.Opacity);
                        Assert.False(context.RenderSettings.WatermarkEnabled);
                        await File.WriteAllBytesAsync(context.OutputPath, renderedBytes, cancellationToken);
                    });

                Assert.Equal("completed", preview.Job.Status);
                Assert.NotNull(preview.PreviewPath);
                jobId = preview.Job.Id;
                previewPath = preview.PreviewPath!;
                suggestedPath = preview.SuggestedOutputPath;
                Assert.True(File.Exists(previewPath));
                Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
                Assert.Single(await library.ListPendingExportPreviewsAsync("session-1"));
            }

            await File.WriteAllBytesAsync(suggestedPath, [9, 9, 9]);
            await using var reopened = await SessionLibrary.OpenAsync(root);
            var pending = Assert.Single(await reopened.ListPendingExportPreviewsAsync("session-1"));
            Assert.Equal(jobId, pending.Job.Id);
            Assert.Equal(previewPath, pending.PreviewPath);
            Assert.Equal("ready", Assert.Single(await reopened.ListSessionsAsync()).Status);

            var saved = await reopened.Export.SavePreviewAsync(jobId, outputDirectory);
            Assert.Equal(Path.Combine(outputDirectory, "result (2).mp4"), saved.OutputPath);
            Assert.Equal(renderedBytes, await File.ReadAllBytesAsync(saved.OutputPath));
            Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(suggestedPath));
            Assert.False(File.Exists(previewPath));
            Assert.Empty(await reopened.ListPendingExportPreviewsAsync("session-1"));
            Assert.Equal("exported", Assert.Single(await reopened.ListSessionsAsync()).Status);
            Assert.True(File.Exists(Path.Combine(root, "sessions", "session-1", "screen", "slot-000000.png")));
            Assert.True(File.Exists(Path.Combine(root, "sessions", "session-1", "camera", "slot-000000.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledPreviewSaveKeepsReadyPreviewAndSourceSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            var preview = await library.Export.RenderPreviewAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputDirectory, "result.mp4"),
                (context, cancellationToken) => File.WriteAllBytesAsync(context.OutputPath, [1, 2, 3], cancellationToken));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.Export.SavePreviewAsync(
                preview.Job.Id,
                outputDirectory,
                cancellationToken: cancellation.Token));

            Assert.True(File.Exists(preview.PreviewPath));
            Assert.Single(await library.ListPendingExportPreviewsAsync("session-1"));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "result.mp4")));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(outputDirectory),
                path => Path.GetFileName(path).StartsWith(".log-save-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AccessDeniedPreviewSaveKeepsPreviewRetryableAndSourceReady()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var directoryInfo = new DirectoryInfo(outputDirectory);
        DirectorySecurity? originalAccess = null;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            await CreateReadySessionAsync(root);
            originalAccess = directoryInfo.GetAccessControl(AccessControlSections.Access);
            await using var library = await SessionLibrary.OpenAsync(root);
            var preview = await library.Export.RenderPreviewAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputDirectory, "denied.mp4"),
                (context, cancellationToken) => File.WriteAllBytesAsync(
                    context.OutputPath,
                    [1, 2, 3],
                    cancellationToken));
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
            var deniedAccess = directoryInfo.GetAccessControl(AccessControlSections.Access);
            deniedAccess.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.CreateFiles,
                AccessControlType.Deny));

            try
            {
                directoryInfo.SetAccessControl(deniedAccess);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.Export.SavePreviewAsync(
                    preview.Job.Id,
                    outputDirectory));
            }
            finally
            {
                directoryInfo.SetAccessControl(originalAccess);
            }

            Assert.True(File.Exists(preview.PreviewPath));
            Assert.Single(await library.ListPendingExportPreviewsAsync("session-1"));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "denied.mp4")));
        }
        finally
        {
            try
            {
                if (originalAccess is not null && Directory.Exists(outputDirectory))
                {
                    directoryInfo.SetAccessControl(originalAccess);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CanceledPreviewRenderRemovesPartialStageAndKeepsSourceReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            await using var library = await SessionLibrary.OpenAsync(root);
            using var cancellation = new CancellationTokenSource();
            var preview = await library.Export.RenderPreviewAsync(
                "session-1",
                new SessionExportRequest(CreateRenderSettings(), outputDirectory, "canceled.mp4"),
                async (context, cancellationToken) =>
                {
                    await File.WriteAllBytesAsync(context.OutputPath, [1, 2], cancellationToken);
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                },
                cancellation.Token);

            Assert.Equal("canceled", preview.Job.Status);
            Assert.Null(preview.PreviewPath);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "canceled.mp4")));
            Assert.Empty(await library.ListPendingExportPreviewsAsync("session-1"));
            Assert.Equal("ready", Assert.Single(await library.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupRecoversPreviewSavePublishedBeforeCatalogCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            string finalPath;
            string previewPath;
            string jobId;
            var renderedBytes = new byte[] { 5, 7, 9, 11 };
            await using (var library = await SessionLibrary.OpenAsync(root))
            {
                var preview = await library.Export.RenderPreviewAsync(
                    "session-1",
                    new SessionExportRequest(CreateRenderSettings(), outputDirectory, "saved.mp4"),
                    (context, cancellationToken) => File.WriteAllBytesAsync(
                        context.OutputPath,
                        renderedBytes,
                        cancellationToken));
                jobId = preview.Job.Id;
                previewPath = preview.PreviewPath!;
                finalPath = Path.Combine(outputDirectory, "saved.mp4");
                await File.WriteAllBytesAsync(finalPath, renderedBytes);

                var parameters = System.Text.Json.Nodes.JsonNode.Parse(preview.Job.ParametersJson)!.AsObject();
                var saveStagingPath = Path.Combine(outputDirectory, $".log-save-{Guid.NewGuid():N}.mp4");
                parameters["previewState"] = "publishing";
                parameters["pendingFinalOutputPath"] = finalPath;
                parameters["saveStagingPath"] = saveStagingPath;
                await library.Catalog.UpdateCompletedRenderJobParametersAsync(
                    jobId,
                    previewPath,
                    preview.Job.ParametersJson,
                    parameters.ToJsonString());
            }

            await using var reopened = await SessionLibrary.OpenAsync(root);
            var recovered = Assert.Single(await reopened.Catalog.ListRenderJobsAsync("session-1"));
            Assert.Equal("completed", recovered.Status);
            Assert.Equal(finalPath, recovered.OutputPath);
            Assert.Equal("saved", System.Text.Json.JsonDocument.Parse(recovered.ParametersJson)
                .RootElement.GetProperty("previewState").GetString());
            Assert.Equal("exported", Assert.Single(await reopened.ListSessionsAsync()).Status);
            Assert.False(File.Exists(previewPath));
            Assert.Equal(renderedBytes, await File.ReadAllBytesAsync(finalPath));

            var idempotentSave = await reopened.Export.SavePreviewAsync(jobId, outputDirectory);
            Assert.Equal(finalPath, idempotentSave.OutputPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupResetsInterruptedPreviewCopyAndKeepsPreviewRetryable()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        Directory.CreateDirectory(root);

        try
        {
            await CreateReadySessionAsync(root);
            string jobId;
            string previewPath;
            string saveStage;
            await using (var library = await SessionLibrary.OpenAsync(root))
            {
                var preview = await library.Export.RenderPreviewAsync(
                    "session-1",
                    new SessionExportRequest(CreateRenderSettings(), outputDirectory, "retry.mp4"),
                    (context, cancellationToken) => File.WriteAllBytesAsync(context.OutputPath, [3, 5, 7], cancellationToken));
                jobId = preview.Job.Id;
                previewPath = preview.PreviewPath!;
                saveStage = Path.Combine(outputDirectory, $".log-save-{Guid.NewGuid():N}.mp4");
                await File.WriteAllBytesAsync(saveStage, [3]);

                var parameters = System.Text.Json.Nodes.JsonNode.Parse(preview.Job.ParametersJson)!.AsObject();
                parameters["previewState"] = "publishing";
                parameters["pendingFinalOutputPath"] = Path.Combine(outputDirectory, "retry.mp4");
                parameters["saveStagingPath"] = saveStage;
                await library.Catalog.UpdateCompletedRenderJobParametersAsync(
                    jobId,
                    previewPath,
                    preview.Job.ParametersJson,
                    parameters.ToJsonString());
            }

            await using var reopened = await SessionLibrary.OpenAsync(root);
            var pending = Assert.Single(await reopened.ListPendingExportPreviewsAsync("session-1"));
            Assert.Equal(jobId, pending.Job.Id);
            Assert.Equal(previewPath, pending.PreviewPath);
            Assert.False(File.Exists(saveStage));
            Assert.False(File.Exists(Path.Combine(outputDirectory, "retry.mp4")));
            Assert.Equal("ready", Assert.Single(await reopened.ListSessionsAsync()).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CreateReadySessionAsync(string root)
    {
        await using var store = await SessionStore.CreateAsync(root, "session-1", "screen", 0, 10);
        await store.StartRecordingAsync();
        await store.CompleteAsync(10);
    }

    private static async Task CreateDualReadySessionWithRawAssetsAsync(string root)
    {
        await using var store = await SessionStore.CreateAsync(root, "session-1", "screen+camera", 0, 10);
        await store.StartRecordingAsync();
        await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, "screen", "slot-000000.png"), [10, 11]);
        await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, "camera", "slot-000000.png"), [20, 21]);
        await store.AppendTrackSlotAsync(new ManifestTrackSlot(
            "screen", 0, 0, 0, false, 0, "screen/slot-000000.png"));
        await store.AppendTrackSlotAsync(new ManifestTrackSlot(
            "camera", 0, 0, 0, false, 0, "camera/slot-000000.png"));
        await store.CompleteAsync(10);
    }

    private static RenderSettings CreateRenderSettings()
    {
        return new RenderSettings(
            new CompositionSettings(
                new PixelSize(4, 4),
                new PipLayout(false, 0.2, 0.05, PipCorner.BottomRight),
                30));
    }
}
