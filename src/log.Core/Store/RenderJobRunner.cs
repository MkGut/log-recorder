namespace Log.Core.Store;

public sealed class RenderJobRunner
{
    private readonly CatalogStore catalog;

    public RenderJobRunner(CatalogStore catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.catalog = catalog;
    }

    public Task<CatalogRenderJob> RunAsync(
        string jobId,
        Func<CancellationToken, Task<string>> renderAsync,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(
            jobId,
            renderAsync,
            publishOutput: null,
            cancellationToken: cancellationToken);
    }

    public Task<CatalogRenderJob> RunAsync(
        string jobId,
        Func<CancellationToken, Task<string>> renderAsync,
        Func<string, string>? publishOutput,
        CancellationToken cancellationToken = default)
    {
        Func<string, Task<string>>? asyncPublishOutput = publishOutput is null
            ? null
            : outputPath => Task.FromResult(publishOutput(outputPath));
        return RunCoreAsync(jobId, renderAsync, asyncPublishOutput, cancellationToken);
    }

    public Task<CatalogRenderJob> RunWithAsyncPublicationAsync(
        string jobId,
        Func<CancellationToken, Task<string>> renderAsync,
        Func<string, Task<string>> publishOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishOutput);
        return RunCoreAsync(jobId, renderAsync, publishOutput, cancellationToken);
    }

    private async Task<CatalogRenderJob> RunCoreAsync(
        string jobId,
        Func<CancellationToken, Task<string>> renderAsync,
        Func<string, Task<string>>? publishOutput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(renderAsync);

        var queued = await catalog.GetRenderJobAsync(jobId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Render job '{jobId}' was not found.");
        if (cancellationToken.IsCancellationRequested)
        {
            await catalog.CancelRenderJobAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return await catalog.GetRenderJobAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? queued;
        }

        try
        {
            await catalog.StartRenderJobAsync(jobId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await catalog.CancelRenderJobAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            return await catalog.GetRenderJobAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? queued;
        }

        var publicationCompleted = false;
        try
        {
            var renderedOutputPath = await renderAsync(cancellationToken).ConfigureAwait(false);
            // A renderer may finish just as cancellation is requested. Do not
            // publish a completed job or let the caller retain that output.
            cancellationToken.ThrowIfCancellationRequested();
            // Once publication begins, treat it as winning cancellation races
            // so a published output is not reported as canceled afterward.
            var outputPath = publishOutput is null
                ? renderedOutputPath
                : await publishOutput(renderedOutputPath).ConfigureAwait(false);
            publicationCompleted = publishOutput is not null;
            await catalog.FinishRenderJobAsync(
                jobId,
                RenderJobStatus.Completed,
                outputPath,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !publicationCompleted)
        {
            if (!publicationCompleted)
            {
                await catalog.FinishRenderJobAsync(
                    jobId,
                    RenderJobStatus.Canceled,
                    outputPath: null,
                    finishedAt: DateTimeOffset.UtcNow,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            if (!publicationCompleted)
            {
                await catalog.FinishRenderJobAsync(
                    jobId,
                    RenderJobStatus.Failed,
                    outputPath: null,
                    finishedAt: DateTimeOffset.UtcNow,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return await catalog.GetRenderJobAsync(jobId, CancellationToken.None).ConfigureAwait(false)
            ?? queued;
    }
}
