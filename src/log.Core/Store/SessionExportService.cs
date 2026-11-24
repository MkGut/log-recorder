using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Log.Core.Diagnostics;
using Log.Core.Render;

namespace Log.Core.Store;

public sealed record SessionExportRequest
{
    public SessionExportRequest(
        RenderSettings renderSettings,
        string outputDirectory,
        string? fileName = null,
        long estimatedOutputBytes = 0,
        long reserveBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(renderSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        // Callers provide geometry; the service replaces render choices with RenderPreset.
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);

        RenderSettings = renderSettings;
        OutputDirectory = Path.GetFullPath(outputDirectory);
        FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        EstimatedOutputBytes = estimatedOutputBytes;
        ReserveBytes = reserveBytes;
    }

    public RenderSettings RenderSettings { get; }
    public string OutputDirectory { get; }
    public string? FileName { get; }
    public long EstimatedOutputBytes { get; }
    public long ReserveBytes { get; }
}

public sealed record SessionExportContext(
    string SessionId,
    // Renderer-owned staging MP4. The export service publishes it afterward.
    string OutputPath,
    string FinalOutputPath,
    SessionRenderInput RenderInput,
    RenderSettings RenderSettings);

public sealed record SessionExportResult(
    CatalogRenderJob Job,
    string OutputPath);

public sealed class SessionExportService
{

    private readonly CatalogStore catalog;
    private readonly string rootDirectory;

    public SessionExportService(CatalogStore catalog, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.catalog = catalog;
        this.rootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>
    /// Renders a durable preview while leaving the source session ready. The
    /// preview remains in the completed render job until SavePreviewAsync commits it.
    /// </summary>
    public async Task<SessionExportPreview> RenderPreviewAsync(
        string sessionId,
        SessionExportRequest request,
        Func<SessionExportContext, CancellationToken, Task> renderAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(renderAsync);

        _ = await catalog.GetSessionAsync(
            sessionId,
            includeDeleting: false,
            CancellationToken.None).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");

        string? stagingOutputPath = null;
        var jobCreated = false;
        try
        {
            await using var store = await SessionStore.OpenAsync(
                rootDirectory,
                sessionId,
                CancellationToken.None).ConfigureAwait(false);
            var renderInput = SessionRenderInputBuilder.Build(store.Manifest);
            var renderSettings = CreatePresetSettings(request.RenderSettings, renderInput);
            EnsureExportStorageAvailable(request);
            var fileName = request.FileName
                ?? ExportPathPolicy.CreateFileName(
                    store.Manifest.Name,
                    store.Manifest.StoppedAtUtc ?? DateTimeOffset.UtcNow);
            var suggestedOutputPath = ExportPathPolicy.AllocateUniquePath(request.OutputDirectory, fileName);
            if (HasReparsePointInPath(Path.GetDirectoryName(suggestedOutputPath)!))
            {
                throw new IOException("Preview output cannot be stored through a reparse-point path.");
            }

            stagingOutputPath = CreateStagingPath(suggestedOutputPath);
            var parameters = new PersistedExportParameters(
                suggestedOutputPath,
                stagingOutputPath,
                renderSettings,
                IsPreview: true,
                PreviewState: "ready",
                ReserveBytes: request.ReserveBytes);
            var job = await catalog.CreateRenderJobAsync(
                sessionId,
                JsonSerializer.Serialize(parameters, CompactJson.Default.PersistedExportParameters),
                CancellationToken.None).ConfigureAwait(false);
            jobCreated = true;
            CaptureEventSource.Instance.ExportStarted(
                sessionId,
                renderSettings.Composition.OutputFramesPerSecond,
                renderSettings.Composition.Mode.ToString());

            var context = new SessionExportContext(
                sessionId,
                stagingOutputPath,
                suggestedOutputPath,
                renderInput,
                renderSettings);
            var completed = await new RenderJobRunner(catalog).RunAsync(
                job.Id,
                async token =>
                {
                    await renderAsync(context, token).ConfigureAwait(false);
                    var renderedPath = await RenderAndReturnPathAsync(
                        Task.CompletedTask,
                        stagingOutputPath).ConfigureAwait(false);
                    var fingerprint = await ReadOutputFingerprintAsync(renderedPath).ConfigureAwait(false);
                    var readyParameters = parameters with
                    {
                        PublishedOutputSha256 = fingerprint.Sha256,
                        PublishedOutputLength = fingerprint.Length
                    };
                    await catalog.UpdateRunningRenderJobParametersAsync(
                        job.Id,
                        JsonSerializer.Serialize(readyParameters, CompactJson.Default.PersistedExportParameters),
                        CancellationToken.None).ConfigureAwait(false);
                    return renderedPath;
                },
                cancellationToken).ConfigureAwait(false);

            if (!string.Equals(completed.Status, "completed", StringComparison.Ordinal))
            {
                DeleteStagingOutput(stagingOutputPath);
            }

            CaptureEventSource.Instance.ExportFinished(sessionId, completed.Status, 0);
            return new SessionExportPreview(
                completed,
                string.Equals(completed.Status, "completed", StringComparison.Ordinal)
                    ? stagingOutputPath
                    : null,
                suggestedOutputPath);
        }
        catch
        {
            if (stagingOutputPath is not null)
            {
                DeleteStagingOutput(stagingOutputPath);
            }

            if (jobCreated)
            {
                CaptureEventSource.Instance.ExportFinished(sessionId, "failed", 0);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<SessionExportPreview>> ListPendingPreviewsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CatalogRenderJob> jobs;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            jobs = await catalog.ListCompletedRenderJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            SessionIdPolicy.Validate(sessionId);
            jobs = await catalog.ListRenderJobsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        var previews = new List<SessionExportPreview>();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(job.Status, "completed", StringComparison.Ordinal)
                || !TryReadPreviewParameters(job, out var parameters, out var previewPath)
                || !string.Equals(parameters.PreviewState, "ready", StringComparison.Ordinal)
                || !string.Equals(job.OutputPath, previewPath, PathComparison)
                || !IsValidFingerprint(parameters)
                || !await MatchesPublishedOutputAsync(
                    previewPath,
                    parameters.PublishedOutputLength!.Value,
                    parameters.PublishedOutputSha256!,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            previews.Add(new SessionExportPreview(job, previewPath, parameters.OutputPath));
        }

        return previews;
    }

    /// <summary>
    /// Saves a completed preview by copying it to a same-directory temporary
    /// file and publishing that copy without overwrite. The preview and source
    /// remain intact until the saved job state is committed.
    /// </summary>
    public async Task<SessionExportResult> SavePreviewAsync(
        string jobId,
        string outputDirectory,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var job = await catalog.GetRenderJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Render job '{jobId}' was not found.");
        if (!string.Equals(job.Status, "completed", StringComparison.Ordinal)
            || !TryReadPreviewParameters(job, out var parameters, out var previewPath)
            || !IsValidFingerprint(parameters))
        {
            throw new InvalidOperationException($"Render job '{jobId}' is not a valid preview-before-save job.");
        }

        if (string.Equals(parameters.PreviewState, "saved", StringComparison.Ordinal))
        {
            var savedPath = parameters.PendingFinalOutputPath
                ?? throw new InvalidDataException("The saved preview job has no final output path.");
            if (!string.Equals(job.OutputPath, savedPath, PathComparison)
                || !await MatchesPublishedOutputAsync(
                    savedPath,
                    parameters.PublishedOutputLength!.Value,
                    parameters.PublishedOutputSha256!,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The saved preview output no longer matches its recorded fingerprint.");
            }

            await MarkPreviewSessionExportedAsync(
                catalog,
                job,
                parameters,
                rootDirectory,
                CancellationToken.None).ConfigureAwait(false);
            return new SessionExportResult(job, savedPath);
        }

        if (!string.Equals(parameters.PreviewState, "ready", StringComparison.Ordinal)
            || !string.Equals(job.Status, "completed", StringComparison.Ordinal)
            || !string.Equals(job.OutputPath, previewPath, PathComparison))
        {
            throw new InvalidOperationException($"Render job '{jobId}' is not ready to save.");
        }

        if (!await MatchesPublishedOutputAsync(
            previewPath,
            parameters.PublishedOutputLength!.Value,
            parameters.PublishedOutputSha256!,
            cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The preview file no longer matches its recorded fingerprint.");
        }

        var requestedFileName = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(parameters.OutputPath)
            : fileName.Trim();
        var finalOutputPath = ExportPathPolicy.AllocateUniquePath(outputDirectory, requestedFileName);
        if (HasReparsePointInPath(Path.GetDirectoryName(finalOutputPath)!))
        {
            throw new IOException("Saved output cannot be stored through a reparse-point path.");
        }

        var saveStagingPath = CreateSaveStagingPath(finalOutputPath);
        var requiredBytes = checked(parameters.PublishedOutputLength.Value + parameters.ReserveBytes);
        StorageGuard.EnsureSufficient(StorageGuard.CheckRequiredBytes(outputDirectory, requiredBytes));

        var publishingParameters = parameters with
        {
            PreviewState = "publishing",
            PendingFinalOutputPath = finalOutputPath,
            SaveStagingPath = saveStagingPath
        };
        var publishingJson = JsonSerializer.Serialize(publishingParameters, CompactJson.Default.PersistedExportParameters);
        await catalog.UpdateCompletedRenderJobParametersAsync(
            job.Id,
            previewPath,
            job.ParametersJson,
            publishingJson,
            CancellationToken.None).ConfigureAwait(false);

        var publicationCompleted = false;
        var saveStagingOwned = false;
        try
        {
            await CopyPreviewToStagingAsync(
                previewPath,
                saveStagingPath,
                cancellationToken).ConfigureAwait(false);
            saveStagingOwned = true;
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(saveStagingPath, finalOutputPath);
            publicationCompleted = true;
            saveStagingOwned = false;

            if (!await MatchesPublishedOutputAsync(
                finalOutputPath,
                parameters.PublishedOutputLength.Value,
                parameters.PublishedOutputSha256!,
                CancellationToken.None).ConfigureAwait(false))
            {
                throw new IOException("The published output does not match the rendered preview.");
            }

            var savedParameters = publishingParameters with
            {
                PreviewState = "saved",
                SaveStagingPath = null
            };
            await catalog.SaveCompletedRenderJobAsync(
                job.Id,
                previewPath,
                publishingJson,
                finalOutputPath,
                JsonSerializer.Serialize(savedParameters, CompactJson.Default.PersistedExportParameters),
                CancellationToken.None).ConfigureAwait(false);
            var savedJob = await catalog.GetRenderJobAsync(job.Id, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Render job '{job.Id}' disappeared after save.");
            await MarkPreviewSessionExportedAsync(
                catalog,
                savedJob,
                savedParameters,
                rootDirectory,
                CancellationToken.None).ConfigureAwait(false);
            TryDeleteStagingOutput(previewPath);
            return new SessionExportResult(savedJob, finalOutputPath);
        }
        catch
        {
            if (!publicationCompleted)
            {
                if (saveStagingOwned)
                {
                    TryDeleteStagingOutput(saveStagingPath);
                }

                var readyParameters = parameters with
                {
                    PendingFinalOutputPath = null,
                    SaveStagingPath = null
                };
                try
                {
                    await catalog.UpdateCompletedRenderJobParametersAsync(
                        job.Id,
                        previewPath,
                        publishingJson,
                        JsonSerializer.Serialize(readyParameters, CompactJson.Default.PersistedExportParameters),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // A concurrent recovery/save transition owns the durable state now.
                }
            }

            throw;
        }
    }

    public async Task<SessionExportResult> ExportAsync(
        string sessionId,
        SessionExportRequest request,
        Func<SessionExportContext, CancellationToken, Task> renderAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(renderAsync);

        var session = await catalog.GetSessionAsync(
            sessionId,
            includeDeleting: false,
            CancellationToken.None).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");

        string? outputPath = null;
        string? stagingOutputPath = null;
        CatalogRenderJob completed;
        var renderCompleted = false;
        var exportStarted = false;
        try
        {
            await using (var store = await SessionStore.OpenAsync(
                rootDirectory,
                sessionId,
                CancellationToken.None).ConfigureAwait(false))
            {
                var renderInput = SessionRenderInputBuilder.Build(store.Manifest);
                var renderSettings = CreatePresetSettings(request.RenderSettings, renderInput);
                EnsureExportStorageAvailable(request);
                var fileName = request.FileName
                    ?? ExportPathPolicy.CreateFileName(
                        store.Manifest.Name,
                        store.Manifest.StoppedAtUtc ?? DateTimeOffset.UtcNow);
                var allocatedOutputPath = ExportPathPolicy.AllocateUniquePath(request.OutputDirectory, fileName);
                outputPath = allocatedOutputPath;
                var allocatedStagingPath = CreateStagingPath(allocatedOutputPath);
                stagingOutputPath = allocatedStagingPath;
                var parameters = new PersistedExportParameters(
                    allocatedOutputPath,
                    allocatedStagingPath,
                    renderSettings);
                var parametersJson = JsonSerializer.Serialize(parameters, CompactJson.Default.PersistedExportParameters);
                var job = await catalog.CreateRenderJobAsync(
                    sessionId,
                    parametersJson,
                    CancellationToken.None).ConfigureAwait(false);
                exportStarted = true;
                CaptureEventSource.Instance.ExportStarted(
                    sessionId,
                    renderSettings.Composition.OutputFramesPerSecond,
                    renderSettings.Composition.Mode.ToString());

                var context = new SessionExportContext(
                    sessionId,
                    allocatedStagingPath,
                    allocatedOutputPath,
                    renderInput,
                    renderSettings);
                completed = await new RenderJobRunner(catalog).RunWithAsyncPublicationAsync(
                    job.Id,
                    token => RenderAndReturnPathAsync(renderAsync(context, token), allocatedStagingPath),
                    async renderedPath =>
                    {
                        var fingerprint = await ReadOutputFingerprintAsync(renderedPath).ConfigureAwait(false);
                        var publishingParameters = parameters with
                        {
                            PublishedOutputSha256 = fingerprint.Sha256,
                            PublishedOutputLength = fingerprint.Length
                        };
                        await catalog.UpdateRunningRenderJobParametersAsync(
                            job.Id,
                            JsonSerializer.Serialize(publishingParameters, CompactJson.Default.PersistedExportParameters),
                            CancellationToken.None).ConfigureAwait(false);
                        return PublishOutput(renderedPath, allocatedOutputPath);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (string.Equals(completed.Status, "completed", StringComparison.Ordinal))
                {
                    renderCompleted = true;
                    await store.SaveLastExportSettingsAsync(
                        renderSettings,
                        CancellationToken.None).ConfigureAwait(false);
                    await store.MarkExportedAsync(CancellationToken.None).ConfigureAwait(false);
                    await catalog.MarkSessionExportedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            if (!renderCompleted && stagingOutputPath is not null)
            {
                DeleteStagingOutput(stagingOutputPath);
            }

            if (exportStarted)
            {
                CaptureEventSource.Instance.ExportFinished(sessionId, "failed", 0);
            }

            throw;
        }

        if (!string.Equals(completed.Status, "completed", StringComparison.Ordinal))
        {
            DeleteStagingOutput(stagingOutputPath!);
            CaptureEventSource.Instance.ExportFinished(sessionId, completed.Status, 0);
            return new SessionExportResult(completed, outputPath!);
        }

        CaptureEventSource.Instance.ExportFinished(sessionId, "completed", 0);
        return new SessionExportResult(completed, outputPath!);
    }

    private static async Task<string> RenderAndReturnPathAsync(
        Task renderTask,
        string stagingOutputPath)
    {
        await renderTask.ConfigureAwait(false);
        var output = new FileInfo(stagingOutputPath);
        if (!output.Exists || output.Length == 0)
        {
            throw new InvalidDataException(
                "The renderer completed without producing a non-empty output file.");
        }

        if (File.GetAttributes(stagingOutputPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The renderer output cannot be a reparse point.");
        }

        return stagingOutputPath;
    }

    private static string CreateStagingPath(string finalOutputPath)
    {
        var outputDirectory = Path.GetDirectoryName(finalOutputPath)
            ?? throw new InvalidOperationException("The export output has no parent directory.");
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(outputDirectory, $".log-{Guid.NewGuid():N}.mp4");
            if (!string.Equals(candidate, finalOutputPath, PathComparison)
                && !File.Exists(candidate)
                && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique temporary export filename.");
    }

    private static string PublishOutput(string stagingOutputPath, string finalOutputPath)
    {
        // Both paths are in the selected output directory. File.Move without
        // overwrite refuses a target created while rendering was in progress.
        File.Move(stagingOutputPath, finalOutputPath);
        return finalOutputPath;
    }

    private static async Task<OutputFingerprint> ReadOutputFingerprintAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            131072,
            useAsync: true);
        var length = stream.Length;
        if (length <= 0)
        {
            throw new InvalidDataException("The renderer output cannot be empty.");
        }

        var hash = await SHA256.HashDataAsync(stream, CancellationToken.None).ConfigureAwait(false);
        return new OutputFingerprint(length, Convert.ToHexString(hash));
    }

    private static void DeleteStagingOutput(string stagingOutputPath)
    {
        if (!File.Exists(stagingOutputPath))
        {
            return;
        }

        if (HasReparsePointInPath(stagingOutputPath))
        {
            return;
        }

        File.Delete(stagingOutputPath);
    }

    private static void TryDeleteStagingOutput(string stagingOutputPath)
    {
        try
        {
            DeleteStagingOutput(stagingOutputPath);
        }
        catch (IOException)
        {
            // The output is already saved; startup can retry stage cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave an inaccessible stage untouched without undoing a completed Save.
        }
    }

    internal static async Task RecoverAbandonedRenderJobsAsync(
        CatalogStore catalog,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRootDirectory = Path.GetFullPath(rootDirectory);
        var runningJobs = await catalog.ListRunningRenderJobsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in runningJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPreviewJob(job.ParametersJson))
            {
                // Preview jobs never publish to their suggested final path.
                // The ordinary abandoned-job pass below will fail them and
                // clean only their renderer-owned stage.
                if (TryReadStagingPaths(
                        job.ParametersJson,
                        out _,
                        out var previewStage,
                        out _,
                        out _))
                {
                    try
                    {
                        DeleteStagingOutput(previewStage);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                continue;
            }

            if (!TryReadStagingPaths(
                    job.ParametersJson,
                    out var finalOutputPath,
                    out var stagingOutputPath,
                    out var publishedOutputSha256,
                    out var publishedOutputLength))
            {
                continue;
            }

            if (!AreSiblingMp4Paths(finalOutputPath, stagingOutputPath))
            {
                continue;
            }

            if (publishedOutputLength > 0
                && IsSha256(publishedOutputSha256)
                && !File.Exists(stagingOutputPath)
                && !Directory.Exists(stagingOutputPath)
                && !HasReparsePointInPath(stagingOutputPath)
                && await MatchesPublishedOutputAsync(
                    finalOutputPath,
                    publishedOutputLength,
                    publishedOutputSha256,
                    cancellationToken).ConfigureAwait(false))
            {
                await using (var store = await SessionStore.OpenAsync(
                    fullRootDirectory,
                    job.SessionId,
                    cancellationToken).ConfigureAwait(false))
                {
                    await store.MarkExportedAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await catalog.MarkSessionExportedAsync(
                    job.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                await catalog.FinishRenderJobAsync(
                    job.Id,
                    RenderJobStatus.Completed,
                    finalOutputPath,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            try
            {
                DeleteStagingOutput(stagingOutputPath);
            }
            catch (IOException)
            {
                // Keep startup recovery available when an abandoned stage is inaccessible.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave an inaccessible stage untouched rather than fail startup.
            }
        }
    }

    internal static async Task RecoverCompletedPreviewJobsAsync(
        CatalogStore catalog,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRootDirectory = Path.GetFullPath(rootDirectory);
        var jobs = await catalog.ListCompletedRenderJobsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPreviewParameters(job, out var parameters, out var previewPath)
                || !IsValidFingerprint(parameters))
            {
                continue;
            }

            if (string.Equals(parameters.PreviewState, "saved", StringComparison.Ordinal))
            {
                var savedPath = parameters.PendingFinalOutputPath!;
                if (string.Equals(job.OutputPath, savedPath, PathComparison)
                    && await MatchesPublishedOutputAsync(
                        savedPath,
                        parameters.PublishedOutputLength!.Value,
                        parameters.PublishedOutputSha256!,
                        cancellationToken).ConfigureAwait(false))
                {
                    await MarkPreviewSessionExportedAsync(
                        catalog,
                        job,
                        parameters,
                        fullRootDirectory,
                        CancellationToken.None).ConfigureAwait(false);
                    TryDeleteStagingOutput(previewPath);
                }

                continue;
            }

            if (!string.Equals(parameters.PreviewState, "publishing", StringComparison.Ordinal))
            {
                continue;
            }

            var finalPath = parameters.PendingFinalOutputPath!;
            var saveStage = parameters.SaveStagingPath!;
            var previewMatches = await MatchesPublishedOutputAsync(
                previewPath,
                parameters.PublishedOutputLength!.Value,
                parameters.PublishedOutputSha256!,
                cancellationToken).ConfigureAwait(false);
            var saveStageExists = File.Exists(saveStage) || Directory.Exists(saveStage);
            var finalMatches = !saveStageExists
                && await MatchesPublishedOutputAsync(
                    finalPath,
                    parameters.PublishedOutputLength.Value,
                    parameters.PublishedOutputSha256!,
                    cancellationToken).ConfigureAwait(false);

            if (finalMatches)
            {
                var savedParameters = parameters with
                {
                    PreviewState = "saved",
                    SaveStagingPath = null
                };
                try
                {
                    await catalog.SaveCompletedRenderJobAsync(
                        job.Id,
                        previewPath,
                        job.ParametersJson,
                        finalPath,
                        JsonSerializer.Serialize(savedParameters, CompactJson.Default.PersistedExportParameters),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Another save/recovery transition won the compare-and-swap.
                    continue;
                }

                var savedJob = await catalog.GetRenderJobAsync(job.Id, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Render job '{job.Id}' disappeared during recovery.");
                await MarkPreviewSessionExportedAsync(
                    catalog,
                    savedJob,
                    savedParameters,
                    fullRootDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                TryDeleteStagingOutput(previewPath);
                continue;
            }

            if (!previewMatches)
            {
                // Keep the durable publishing marker when neither a valid
                // preview nor a verified final output is available.
                continue;
            }

            var readyParameters = parameters with
            {
                PreviewState = "ready",
                PendingFinalOutputPath = null,
                SaveStagingPath = null
            };
            try
            {
                await catalog.UpdateCompletedRenderJobParametersAsync(
                    job.Id,
                    previewPath,
                    job.ParametersJson,
                    JsonSerializer.Serialize(readyParameters, CompactJson.Default.PersistedExportParameters),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            try
            {
                TryDeleteStagingOutput(saveStage);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task MarkPreviewSessionExportedAsync(
        CatalogStore catalog,
        CatalogRenderJob job,
        PersistedExportParameters parameters,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        await using var store = await SessionStore.OpenAsync(
            rootDirectory,
            job.SessionId,
            cancellationToken).ConfigureAwait(false);
        await store.SaveLastExportSettingsAsync(parameters.RenderSettings, cancellationToken).ConfigureAwait(false);
        await store.MarkExportedAsync(cancellationToken).ConfigureAwait(false);
        await catalog.MarkSessionExportedAsync(job.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateSaveStagingPath(string finalOutputPath)
    {
        var outputDirectory = Path.GetDirectoryName(finalOutputPath)
            ?? throw new InvalidOperationException("The export output has no parent directory.");
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(outputDirectory, $".log-save-{Guid.NewGuid():N}.mp4");
            if (!string.Equals(candidate, finalOutputPath, PathComparison)
                && !File.Exists(candidate)
                && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique temporary save filename.");
    }

    private static async Task CopyPreviewToStagingAsync(
        string previewPath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var destinationCreated = false;
        try
        {
            await using var source = new FileStream(
                previewPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131072,
                useAsync: true);
            await using var destination = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131072,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            destinationCreated = true;
            await source.CopyToAsync(destination, 131072, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        catch
        {
            if (destinationCreated)
            {
                TryDeleteStagingOutput(stagingPath);
            }

            throw;
        }
    }

    private static bool TryReadPreviewParameters(
        CatalogRenderJob job,
        out PersistedExportParameters parameters,
        out string previewPath)
    {
        parameters = null!;
        previewPath = string.Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize(job.ParametersJson, CompactJson.Default.PersistedExportParameters);
            if (parsed is null
                || !parsed.IsPreview
                || parsed.RenderSettings is null
                || parsed.ReserveBytes < 0
                || !Path.IsPathFullyQualified(parsed.OutputPath)
                || !Path.IsPathFullyQualified(parsed.StagingOutputPath))
            {
                return false;
            }

            var outputPath = Path.GetFullPath(parsed.OutputPath);
            previewPath = Path.GetFullPath(parsed.StagingOutputPath);
            if (!AreSiblingMp4Paths(outputPath, previewPath)
                || !IsOwnedTemporaryPath(previewPath, ".log-")
                || HasReparsePointInPath(Path.GetDirectoryName(previewPath)!))
            {
                return false;
            }

            parsed = parsed with
            {
                OutputPath = outputPath,
                StagingOutputPath = previewPath
            };
            switch (parsed.PreviewState)
            {
                case "ready":
                    if (parsed.PendingFinalOutputPath is not null || parsed.SaveStagingPath is not null)
                    {
                        return false;
                    }
                    break;
                case "publishing":
                    if (!TryNormalizeSavePaths(parsed, out parsed))
                    {
                        return false;
                    }
                    break;
                case "saved":
                    if (!TryNormalizeSavedPath(parsed, out parsed))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }

            if (parsed.PendingFinalOutputPath is not null
                && string.Equals(parsed.PendingFinalOutputPath, previewPath, PathComparison))
            {
                return false;
            }

            parameters = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryNormalizeSavePaths(
        PersistedExportParameters parameters,
        out PersistedExportParameters normalized)
    {
        normalized = parameters;
        if (parameters.PendingFinalOutputPath is null
            || parameters.SaveStagingPath is null
            || !Path.IsPathFullyQualified(parameters.PendingFinalOutputPath)
            || !Path.IsPathFullyQualified(parameters.SaveStagingPath))
        {
            return false;
        }

        var finalPath = Path.GetFullPath(parameters.PendingFinalOutputPath);
        var stagePath = Path.GetFullPath(parameters.SaveStagingPath);
        if (!AreSiblingMp4Paths(finalPath, stagePath)
            || !IsOwnedTemporaryPath(stagePath, ".log-save-")
            || HasReparsePointInPath(Path.GetDirectoryName(finalPath)!)
            || HasReparsePointInPath(Path.GetDirectoryName(stagePath)!))
        {
            return false;
        }

        normalized = parameters with
        {
            PendingFinalOutputPath = finalPath,
            SaveStagingPath = stagePath
        };
        return true;
    }

    private static bool TryNormalizeSavedPath(
        PersistedExportParameters parameters,
        out PersistedExportParameters normalized)
    {
        normalized = parameters;
        if (parameters.PendingFinalOutputPath is null
            || parameters.SaveStagingPath is not null
            || !Path.IsPathFullyQualified(parameters.PendingFinalOutputPath))
        {
            return false;
        }

        var finalPath = Path.GetFullPath(parameters.PendingFinalOutputPath);
        if (!string.Equals(Path.GetExtension(finalPath), ".mp4", StringComparison.OrdinalIgnoreCase)
            || HasReparsePointInPath(Path.GetDirectoryName(finalPath)!))
        {
            return false;
        }

        normalized = parameters with { PendingFinalOutputPath = finalPath };
        return true;
    }

    private static bool IsOwnedTemporaryPath(string path, string prefix)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Length == prefix.Length + 32 + 4
            && fileName.StartsWith(prefix, StringComparison.Ordinal)
            && fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(fileName.AsSpan(prefix.Length, 32), "N", out _);
    }

    private static bool IsValidFingerprint(PersistedExportParameters parameters)
    {
        return parameters.PublishedOutputLength is > 0
            && parameters.PublishedOutputSha256 is not null
            && IsSha256(parameters.PublishedOutputSha256);
    }

    private static bool IsPreviewJob(string parametersJson)
    {
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("isPreview", out var isPreview)
                && isPreview.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> MatchesPublishedOutputAsync(
        string outputPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath)
            || Directory.Exists(outputPath)
            || HasReparsePointInPath(outputPath))
        {
            return false;
        }

        try
        {
            await using var stream = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131072,
                useAsync: true);
            if (stream.Length != expectedLength)
            {
                return false;
            }

            var actualSha256 = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(
                actualSha256,
                Convert.FromHexString(expectedSha256));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryReadStagingPaths(
        string parametersJson,
        out string finalOutputPath,
        out string stagingOutputPath,
        out string publishedOutputSha256,
        out long publishedOutputLength)
    {
        finalOutputPath = string.Empty;
        stagingOutputPath = string.Empty;
        publishedOutputSha256 = string.Empty;
        publishedOutputLength = 0;
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            if (!TryGetString(document.RootElement, "outputPath", out var finalPath)
                || !TryGetString(document.RootElement, "stagingOutputPath", out var stagePath))
            {
                return false;
            }

            if (!Path.IsPathFullyQualified(finalPath)
                || !Path.IsPathFullyQualified(stagePath))
            {
                return false;
            }

            finalOutputPath = Path.GetFullPath(finalPath);
            stagingOutputPath = Path.GetFullPath(stagePath);
            TryGetString(document.RootElement, "publishedOutputSha256", out publishedOutputSha256);
            if (document.RootElement.TryGetProperty("publishedOutputLength", out var outputLength)
                && outputLength.ValueKind == JsonValueKind.Number)
            {
                outputLength.TryGetInt64(out publishedOutputLength);
            }

            var stageFileName = Path.GetFileName(stagingOutputPath);
            if (stageFileName.Length != 41
                || !stageFileName.StartsWith(".log-", StringComparison.Ordinal)
                || !stageFileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(stageFileName.AsSpan(5, 32), "N", out _))
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private sealed record OutputFingerprint(long Length, string Sha256);

    private static bool AreSiblingMp4Paths(string finalOutputPath, string stagingOutputPath)
    {
        var finalDirectory = Path.GetDirectoryName(finalOutputPath);
        var stagingDirectory = Path.GetDirectoryName(stagingOutputPath);
        return string.Equals(finalDirectory, stagingDirectory, PathComparison)
            && string.Equals(Path.GetExtension(finalOutputPath), ".mp4", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(stagingOutputPath), ".mp4", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finalOutputPath, stagingOutputPath, PathComparison);
    }

    private static bool HasReparsePointInPath(string path)
    {
        var currentPath = Path.GetFullPath(path);
        while (currentPath is not null)
        {
            try
            {
                if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }

        return false;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static RenderSettings CreatePresetSettings(
        RenderSettings requestedSettings,
        SessionRenderInput renderInput)
    {
        return RenderPreset.CreateSettings(
            renderInput.SourceMode,
            requestedSettings.Composition.OutputSize,
            requestedSettings.Composition.CameraSourceSize);
    }

    private static void EnsureExportStorageAvailable(SessionExportRequest request)
    {
        var requiredBytes = checked(request.EstimatedOutputBytes + request.ReserveBytes);
        if (requiredBytes == 0)
        {
            return;
        }

        StorageGuard.EnsureSufficient(
            StorageGuard.CheckRequiredBytes(request.OutputDirectory, requiredBytes));
    }

    internal sealed record PersistedExportParameters(
        string OutputPath,
        string StagingOutputPath,
        RenderSettings RenderSettings,
        string? PublishedOutputSha256 = null,
        long? PublishedOutputLength = null,
        bool IsPreview = false,
        string? PreviewState = null,
        long ReserveBytes = 0,
        string? PendingFinalOutputPath = null,
        string? SaveStagingPath = null);
}
