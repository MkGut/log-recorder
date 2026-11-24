namespace Log.Core.Store;

public enum RenderJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record CatalogRenderJob(
    string Id,
    string SessionId,
    string ParametersJson,
    string Status,
    string? OutputPath,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record SessionExportPreview(
    CatalogRenderJob Job,
    string? PreviewPath,
    string SuggestedOutputPath);
