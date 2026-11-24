using System.Collections.Concurrent;
using System.Text.Json;
using Log.Core.Timeline;

namespace Log.Core.Store;

/// <summary>
/// Index of recordings, highlights, assets and render jobs. Each recording is one
/// small JSON file (<c>sessions/&lt;id&gt;.json</c>) written atomically; settings and
/// legacy groups live in two more small files. Files are re-read on every call, so
/// several stores over the same folder always agree.
/// </summary>
public sealed class CatalogStore : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string sessionsDirectory;
    private readonly string settingsPath;
    private readonly string groupsPath;
    private readonly bool isReadOnly;
    private readonly SemaphoreSlim gate;
    private volatile bool disposed;

    private CatalogStore(string catalogDirectory, bool isReadOnly)
    {
        sessionsDirectory = Path.Combine(catalogDirectory, "sessions");
        settingsPath = Path.Combine(catalogDirectory, "settings.json");
        groupsPath = Path.Combine(catalogDirectory, "groups.json");
        this.isReadOnly = isReadOnly;
        gate = Gates.GetOrAdd(catalogDirectory, static _ => new SemaphoreSlim(1, 1));
    }

    public static Task<CatalogStore> OpenAsync(
        string catalogDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(catalogDirectory);
        Directory.CreateDirectory(Path.Combine(fullPath, "sessions"));
        var catalog = new CatalogStore(fullPath, isReadOnly: false);
        catalog.ValidateSharedFiles();
        return Task.FromResult(catalog);
    }

    public static Task<CatalogStore> OpenReadOnlyAsync(
        string catalogDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(catalogDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The catalog folder '{fullPath}' was not found.");
        }

        var catalog = new CatalogStore(fullPath, isReadOnly: true);
        catalog.ValidateSharedFiles();
        return Task.FromResult(catalog);
    }

    public Task UpsertSessionAsync(
        SessionManifest manifest,
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var name = string.IsNullOrWhiteSpace(manifest.Name)
            ? manifest.Id
            : SessionNamePolicy.Normalize(manifest.Name);
        var description = SessionNamePolicy.NormalizeDescription(manifest.Description);

        return LockedAsync(() =>
        {
            var record = Load(manifest.Id) ?? new SessionRecord { Id = manifest.Id };
            record.GroupId = manifest.GroupId;
            record.Name = name;
            record.Description = description;
            record.SourceMode = manifest.SourceMode;
            record.StartedAtTicks = manifest.StartedAtTicks;
            record.DirectoryPath = directoryPath;
            // A session marked for deletion stays that way until it is restored.
            if (record.Status != Deleting)
            {
                record.Status = StatusText(manifest.Status);
            }

            for (var index = 0; index < manifest.Highlights.Count; index++)
            {
                ArgumentNullException.ThrowIfNull(manifest.Highlights[index]);
                SessionIdPolicy.Validate(manifest.Highlights[index].Id);
                UpsertHighlight(record, manifest.Highlights[index], index, DateTimeOffset.UtcNow);
            }

            var manifestIds = manifest.Highlights.Select(static highlight => highlight.Id).ToHashSet(StringComparer.Ordinal);
            record.Highlights.RemoveAll(row => !manifestIds.Contains(row.Id));
            Save(record);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CatalogSession>> GetInterruptedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        return LockedAsync<IReadOnlyList<CatalogSession>>(() => LoadAll()
            .Where(static record => record.Status is "recording" or "finalizing" or "interrupted")
            .OrderByDescending(static record => record.StartedAtTicks)
            .ThenBy(static record => record.Id, StringComparer.Ordinal)
            .Select(ToSession)
            .ToList(), cancellationToken);
    }

    public Task<CatalogSession?> GetSessionAsync(
        string sessionId,
        bool includeDeleting = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync<CatalogSession?>(() =>
            Load(sessionId) is { } record && (includeDeleting || record.Status != Deleting)
                ? ToSession(record)
                : null, cancellationToken);
    }

    public Task UpsertHighlightAsync(
        string sessionId,
        HighlightEvent highlight,
        int ordering,
        DateTimeOffset wallTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        ArgumentNullException.ThrowIfNull(highlight);
        SessionIdPolicy.Validate(highlight.Id);
        ArgumentOutOfRangeException.ThrowIfNegative(ordering);

        return LockedAsync(() =>
        {
            var record = Load(sessionId) ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            UpsertHighlight(record, highlight, ordering, wallTime);
            Save(record);
        }, cancellationToken);
    }

    public Task UpdateHighlightAsync(
        string sessionId,
        HighlightEvent highlight,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        ArgumentNullException.ThrowIfNull(highlight);
        SessionIdPolicy.Validate(highlight.Id);

        return LockedAsync(() =>
        {
            var record = LoadActive(sessionId);
            var existing = record.Highlights.FirstOrDefault(row => row.Id == highlight.Id)
                ?? throw new KeyNotFoundException($"Highlight '{highlight.Id}' was not found in session '{sessionId}'.");
            UpsertHighlight(record, highlight, existing.Ordering, existing.WallTime);
            Save(record);
        }, cancellationToken);
    }

    public Task DeleteHighlightAsync(
        string sessionId,
        string highlightId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        SessionIdPolicy.Validate(highlightId);

        return LockedAsync(() =>
        {
            var record = LoadActive(sessionId);
            if (record.Highlights.RemoveAll(row => row.Id == highlightId) == 0)
            {
                throw new KeyNotFoundException($"Highlight '{highlightId}' was not found in session '{sessionId}'.");
            }

            Save(record);
        }, cancellationToken);
    }

    public Task UpsertAssetAsync(
        CatalogAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateAsset(asset);

        return LockedAsync(() =>
        {
            var record = LoadActive(asset.SessionId);
            var stored = asset with { RelativePath = asset.RelativePath.Replace('\\', '/') };
            var index = record.Assets.FindIndex(existing => existing.Id == asset.Id);
            if (index >= 0)
            {
                record.Assets[index] = stored;
            }
            else
            {
                record.Assets.Add(stored);
            }

            Save(record);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CatalogAsset>> ListAssetsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync<IReadOnlyList<CatalogAsset>>(() => (Load(sessionId)?.Assets ?? [])
            .OrderBy(static asset => asset.CreatedAt.UtcTicks)
            .ThenBy(static asset => asset.Id, StringComparer.Ordinal)
            .ToList(), cancellationToken);
    }

    public Task DeleteAssetAsync(
        string sessionId,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        SessionIdPolicy.Validate(sessionId);
        ValidateAssetId(assetId);

        return LockedAsync(() =>
        {
            var record = Load(sessionId);
            if (record is null || record.Assets.RemoveAll(asset => asset.Id == assetId) == 0)
            {
                throw new KeyNotFoundException($"Asset '{assetId}' was not found in session '{sessionId}'.");
            }

            Save(record);
        }, cancellationToken);
    }

    public Task DeleteAssetsByKindAsync(
        string sessionId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync(() =>
        {
            if (Load(sessionId) is { } record && record.Assets.RemoveAll(asset => asset.Kind == kind) > 0)
            {
                Save(record);
            }
        }, cancellationToken);
    }

    public Task<CatalogRenderJob> CreateRenderJobAsync(
        string sessionId,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJson);
        EnsureJson(parametersJson);

        return LockedAsync(() =>
        {
            var record = LoadActive(sessionId);
            if (record.Status is not ("ready" or "exported" or "interrupted"))
            {
                throw new InvalidOperationException(
                    $"Session '{sessionId}' is not stable enough to render in state '{record.Status}'.");
            }

            var job = new CatalogRenderJob(
                Guid.NewGuid().ToString("N"),
                sessionId,
                parametersJson,
                StatusText(RenderJobStatus.Queued),
                null,
                null,
                null);
            record.RenderJobs.Add(job);
            Save(record);
            return job;
        }, cancellationToken);
    }

    public Task<CatalogRenderJob?> GetRenderJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return LockedAsync<CatalogRenderJob?>(
            () => LoadAll().SelectMany(static record => record.RenderJobs).FirstOrDefault(job => job.Id == jobId),
            cancellationToken);
    }

    public Task<IReadOnlyList<CatalogRenderJob>> ListRenderJobsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        return LockedAsync<IReadOnlyList<CatalogRenderJob>>(() => (Load(sessionId)?.RenderJobs ?? [])
            .OrderBy(static job => job.StartedAt?.UtcTicks ?? long.MaxValue)
            .ThenBy(static job => job.Id, StringComparer.Ordinal)
            .ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<CatalogRenderJob>> ListRunningRenderJobsAsync(
        CancellationToken cancellationToken = default)
    {
        return LockedAsync<IReadOnlyList<CatalogRenderJob>>(() => LoadAll()
            .SelectMany(static record => record.RenderJobs)
            .Where(static job => job.Status == "running")
            .OrderBy(static job => job.StartedAt?.UtcTicks)
            .ThenBy(static job => job.Id, StringComparer.Ordinal)
            .ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<CatalogRenderJob>> ListCompletedRenderJobsAsync(
        CancellationToken cancellationToken = default)
    {
        return LockedAsync<IReadOnlyList<CatalogRenderJob>>(() => LoadAll()
            .SelectMany(static record => record.RenderJobs)
            .Where(static job => job.Status == "completed")
            .OrderBy(static job => job.FinishedAt?.UtcTicks)
            .ThenBy(static job => job.Id, StringComparer.Ordinal)
            .ToList(), cancellationToken);
    }

    public Task<int> RecoverRunningRenderJobsAsync(
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default)
    {
        return LockedAsync(() =>
        {
            var count = 0;
            foreach (var record in LoadAll())
            {
                var changed = false;
                for (var index = 0; index < record.RenderJobs.Count; index++)
                {
                    if (record.RenderJobs[index].Status == "running")
                    {
                        record.RenderJobs[index] = record.RenderJobs[index] with { Status = "failed", FinishedAt = finishedAt };
                        changed = true;
                        count++;
                    }
                }

                if (changed)
                {
                    Save(record);
                }
            }

            return count;
        }, cancellationToken);
    }

    public Task StartRenderJobAsync(
        string jobId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return UpdateJobAsync(
            jobId,
            (record, job) => job.Status == "queued" && record.Status is "ready" or "exported" or "interrupted"
                ? job with { Status = "running", StartedAt = startedAt }
                : null,
            $"Render job '{jobId}' is not queued.",
            cancellationToken);
    }

    public Task UpdateRunningRenderJobParametersAsync(
        string jobId,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJson);
        EnsureJson(parametersJson);
        return UpdateJobAsync(
            jobId,
            (_, job) => job.Status == "running" ? job with { ParametersJson = parametersJson } : null,
            $"Render job '{jobId}' is not running.",
            cancellationToken);
    }

    public Task UpdateCompletedRenderJobParametersAsync(
        string jobId,
        string expectedOutputPath,
        string expectedParametersJson,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedParametersJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJson);
        EnsureJson(expectedParametersJson);
        EnsureJson(parametersJson);
        return UpdateJobAsync(
            jobId,
            (_, job) => IsCompletedAs(job, expectedOutputPath, expectedParametersJson)
                ? job with { ParametersJson = parametersJson }
                : null,
            $"Completed render job '{jobId}' changed before its parameters were updated.",
            cancellationToken);
    }

    public Task SaveCompletedRenderJobAsync(
        string jobId,
        string expectedOutputPath,
        string expectedParametersJson,
        string savedOutputPath,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedParametersJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(savedOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJson);
        EnsureJson(expectedParametersJson);
        EnsureJson(parametersJson);

        var fullOutputPath = Path.GetFullPath(savedOutputPath);
        if (!IsUsableMp4(fullOutputPath))
        {
            throw new IOException("A saved render job must point to a non-empty, non-reparse MP4 file.");
        }

        return UpdateJobAsync(
            jobId,
            (_, job) => IsCompletedAs(job, expectedOutputPath, expectedParametersJson)
                ? job with { OutputPath = fullOutputPath, ParametersJson = parametersJson }
                : null,
            $"Completed render job '{jobId}' changed before its output was saved.",
            cancellationToken);
    }

    public Task CancelRenderJobAsync(
        string jobId,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return UpdateJobAsync(
            jobId,
            (_, job) => job.Status is "queued" or "running"
                ? job with { Status = "canceled", FinishedAt = finishedAt }
                : null,
            $"Render job '{jobId}' is not cancelable.",
            cancellationToken);
    }

    public Task FinishRenderJobAsync(
        string jobId,
        RenderJobStatus status,
        string? outputPath,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (status is not (RenderJobStatus.Completed or RenderJobStatus.Failed or RenderJobStatus.Canceled))
        {
            throw new ArgumentException("A render job can only finish in a terminal state.", nameof(status));
        }

        if (status == RenderJobStatus.Completed && string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("A completed render job must have an output path.", nameof(outputPath));
        }

        if (status == RenderJobStatus.Completed && !IsUsableMp4(Path.GetFullPath(outputPath!)))
        {
            throw new IOException("A completed render job must point to a non-empty, non-reparse MP4 file.");
        }

        return UpdateJobAsync(
            jobId,
            (_, job) => job.Status == "running"
                ? job with { Status = StatusText(status), OutputPath = outputPath, FinishedAt = finishedAt }
                : null,
            $"Render job '{jobId}' is not running.",
            cancellationToken);
    }

    public Task<IReadOnlyList<CatalogHighlight>> ListHighlightsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync<IReadOnlyList<CatalogHighlight>>(() => (Load(sessionId)?.Highlights ?? [])
            .OrderBy(static row => row.Ordering)
            .ThenBy(static row => row.QpcTicks)
            .ThenBy(static row => row.Id, StringComparer.Ordinal)
            .Select(static row => new CatalogHighlight(
                new HighlightEvent(
                    row.Id,
                    row.QpcTicks,
                    Enum.Parse<HighlightSource>(row.Source, ignoreCase: true),
                    MillisecondsToHoldTicks(row.HoldMs),
                    row.Included,
                    row.ScreenPath,
                    row.CameraPath),
                row.WallTime,
                row.Ordering))
            .ToList(), cancellationToken);
    }

    public Task RestoreSessionAsync(
        string sessionId,
        SessionStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        if (status is SessionStatus.Starting or SessionStatus.Recording or SessionStatus.Finalizing or SessionStatus.Deleting)
        {
            throw new ArgumentException("A deleted session can only be restored to a stable state.", nameof(status));
        }

        return LockedAsync(() =>
        {
            if (Load(sessionId) is not { Status: Deleting } record)
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found or is not deleted.");
            }

            record.Status = StatusText(status);
            Save(record);
        }, cancellationToken);
    }

    public Task RemoveDeletedSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync(() =>
        {
            if (Load(sessionId) is not { Status: Deleting })
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found or is not marked for deletion.");
            }

            RequireWritable();
            File.Delete(RecordPath(sessionId));
        }, cancellationToken);
    }

    public Task<string?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return LockedAsync<string?>(
            () => ReadSettings().TryGetValue(key.Trim(), out var value) ? value : null,
            cancellationToken);
    }

    public Task SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return LockedAsync(() =>
        {
            var settings = ReadSettings();
            settings[key.Trim()] = value;
            WriteAtomic(settingsPath, settings, CatalogJson.Default.DictionaryStringString);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CatalogGroup>> ListAllGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        return LockedAsync<IReadOnlyList<CatalogGroup>>(() =>
        {
            var counts = LoadAll()
                .Where(static record => record.Status != Deleting && record.GroupId is not null)
                .GroupBy(static record => record.GroupId!, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
            return ReadGroups()
                .OrderBy(static row => row.SortOrder)
                .ThenBy(static row => row.CreatedAtTicks)
                .ThenBy(static row => row.Id, StringComparer.Ordinal)
                .Select(row => new CatalogGroup(
                    row.Id,
                    row.Name,
                    GroupTypePolicy.FromStorageValue(row.Type),
                    row.CreatedAtTicks,
                    row.SortOrder)
                {
                    VideoCount = counts.GetValueOrDefault(row.Id)
                })
                .ToList();
        }, cancellationToken);
    }

    /// <summary>Seeds a legacy group; groups are read-only history in the product.</summary>
    internal Task SeedGroupAsync(CatalogGroup group, CancellationToken cancellationToken = default)
    {
        return LockedAsync(() =>
        {
            var groups = ReadGroups();
            groups.RemoveAll(row => row.Id == group.Id);
            groups.Add(new GroupRow
            {
                Id = group.Id,
                Name = group.Name,
                Type = group.Type switch
                {
                    GroupType.Class => "class",
                    GroupType.PersonalProject => "personal-project",
                    _ => "unassigned"
                },
                CreatedAtTicks = group.CreatedAtTicks,
                SortOrder = group.SortOrder
            });
            WriteAtomic(groupsPath, groups, CatalogJson.Default.ListGroupRow);
        }, cancellationToken);
    }

    public Task<IReadOnlySet<string>> ListActiveRenderSessionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        return LockedAsync<IReadOnlySet<string>>(() => LoadAll()
            .Where(static record => record.RenderJobs.Any(static job => job.Status is "queued" or "running"))
            .Select(static record => record.Id)
            .ToHashSet(StringComparer.Ordinal), cancellationToken);
    }

    public Task<IReadOnlyList<CatalogSession>> ListSessionsAsync(
        bool includeDeleting = false,
        CancellationToken cancellationToken = default)
    {
        return LockedAsync<IReadOnlyList<CatalogSession>>(() => LoadAll()
            .Where(record => includeDeleting || record.Status != Deleting)
            .OrderByDescending(static record => record.StartedAtTicks)
            .ThenBy(static record => record.Id, StringComparer.Ordinal)
            .Select(ToSession)
            .ToList(), cancellationToken);
    }

    public Task RenameSessionAsync(
        string sessionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        var normalizedName = SessionNamePolicy.Normalize(name);

        return LockedAsync(() =>
        {
            var record = LoadActive(sessionId);
            record.Name = normalizedName;
            Save(record);
        }, cancellationToken);
    }

    public Task UpdateSessionMetadataAsync(
        string sessionId,
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);
        var normalizedName = SessionNamePolicy.Normalize(name);
        var normalizedDescription = SessionNamePolicy.NormalizeDescription(description);

        return LockedAsync(() =>
        {
            var record = LoadActive(sessionId);
            record.Name = normalizedName;
            record.Description = normalizedDescription;
            Save(record);
        }, cancellationToken);
    }

    public Task MarkSessionExportedAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync(() =>
        {
            if (Load(sessionId) is not { Status: "ready" or "exported" or "interrupted" } record)
            {
                throw new InvalidOperationException($"Session '{sessionId}' is not ready to be marked exported.");
            }

            record.Status = "exported";
            Save(record);
        }, cancellationToken);
    }

    public Task SoftDeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionIdPolicy.Validate(sessionId);

        return LockedAsync(() =>
        {
            if (Load(sessionId) is not { Status: "ready" or "exported" or "interrupted" } record
                || record.RenderJobs.Any(static job => job.Status == "running"))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            record.Status = Deleting;
            Save(record);
        }, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    private const string Deleting = "deleting";

    private async Task<T> LockedAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task LockedAsync(Action action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            gate.Release();
        }
    }

    // Applies a compare-and-set style update to one render job; null means "not allowed now".
    private Task UpdateJobAsync(
        string jobId,
        Func<SessionRecord, CatalogRenderJob, CatalogRenderJob?> update,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        return LockedAsync(() =>
        {
            foreach (var record in LoadAll())
            {
                var index = record.RenderJobs.FindIndex(job => job.Id == jobId);
                if (index < 0)
                {
                    continue;
                }

                var updated = update(record, record.RenderJobs[index])
                    ?? throw new InvalidOperationException(failureMessage);
                record.RenderJobs[index] = updated;
                Save(record);
                return;
            }

            throw new InvalidOperationException(failureMessage);
        }, cancellationToken);
    }

    private static bool IsCompletedAs(CatalogRenderJob job, string outputPath, string parametersJson) =>
        job.Status == "completed" && job.OutputPath == outputPath && job.ParametersJson == parametersJson;

    private static bool IsUsableMp4(string fullPath) =>
        string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase)
        && File.Exists(fullPath)
        && !File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint)
        && new FileInfo(fullPath).Length != 0;

    private static void EnsureJson(string json)
    {
        using var _ = JsonDocument.Parse(json);
    }

    private static string StatusText(Enum status) => status.ToString().ToLowerInvariant();

    private static CatalogSession ToSession(SessionRecord record) => new(
        record.Id,
        record.Name,
        record.SourceMode,
        record.Status,
        record.DirectoryPath,
        record.StartedAtTicks,
        record.Description,
        record.GroupId);

    private string RecordPath(string sessionId)
    {
        SessionIdPolicy.Validate(sessionId);
        return Path.Combine(sessionsDirectory, sessionId + ".json");
    }

    private SessionRecord LoadActive(string sessionId) =>
        Load(sessionId) is { } record && record.Status != Deleting
            ? record
            : throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");

    // A missing or unreadable record counts as absent; the manifest is the source of truth
    // and recovery rewrites the record.
    private SessionRecord? Load(string sessionId) => TryReadRecord(RecordPath(sessionId));

    private List<SessionRecord> LoadAll()
    {
        var records = new List<SessionRecord>();
        if (!Directory.Exists(sessionsDirectory))
        {
            return records;
        }

        foreach (var path in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
        {
            if (TryReadRecord(path) is { } record)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static SessionRecord? TryReadRecord(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, CatalogJson.Default.SessionRecord);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Test seam: lets tests make a specific write fail.
    internal Action<SessionRecord>? BeforeSave { get; set; }

    private void Save(SessionRecord record)
    {
        BeforeSave?.Invoke(record);
        WriteAtomic(RecordPath(record.Id), record, CatalogJson.Default.SessionRecord);
    }

    private void RequireWritable()
    {
        if (isReadOnly)
        {
            throw new InvalidOperationException("The catalog was opened read-only.");
        }
    }

    // Temp file + move so a crash never leaves a half-written file.
    private void WriteAtomic<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        RequireWritable();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private Dictionary<string, string> ReadSettings() =>
        ReadShared(settingsPath, CatalogJson.Default.DictionaryStringString) ?? new(StringComparer.Ordinal);

    private List<GroupRow> ReadGroups() =>
        ReadShared(groupsPath, CatalogJson.Default.ListGroupRow) ?? [];

    // Corrupt shared files fail loudly: unlike a record they cannot be rebuilt from a manifest.
    private static T? ReadShared<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The catalog file '{path}' is corrupt.", exception);
        }
    }

    private void ValidateSharedFiles()
    {
        _ = ReadSettings();
        _ = ReadGroups();
    }

    private static void UpsertHighlight(
        SessionRecord record,
        HighlightEvent highlight,
        int ordering,
        DateTimeOffset wallTime)
    {
        var index = record.Highlights.FindIndex(row => row.Id == highlight.Id);
        var row = new HighlightRow
        {
            Id = highlight.Id,
            QpcTicks = highlight.EventTicks,
            // The first wall time sticks; later upserts only change the event itself.
            WallTime = index >= 0 ? record.Highlights[index].WallTime : wallTime,
            Source = highlight.Source.ToString().ToLowerInvariant(),
            ScreenPath = highlight.ScreenAssetReference,
            CameraPath = highlight.CameraAssetReference,
            Included = highlight.Included,
            HoldMs = HoldTicksToMilliseconds(highlight.HoldTicks),
            Ordering = ordering
        };
        if (index >= 0)
        {
            record.Highlights[index] = row;
        }
        else
        {
            record.Highlights.Add(row);
        }
    }

    private static long HoldTicksToMilliseconds(long holdTicks)
    {
        return checked((holdTicks * 1000L + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond);
    }

    private static long MillisecondsToHoldTicks(long milliseconds)
    {
        return checked(milliseconds * TimeSpan.TicksPerSecond / 1000L);
    }

    private static void ValidateAsset(CatalogAsset asset)
    {
        ValidateAssetId(asset.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.SessionId);
        SessionIdPolicy.Validate(asset.SessionId);
        if (asset.Kind is not ("screen_track" or "camera_track" or "thumbnail" or "highlight" or "export"))
        {
            throw new ArgumentException("Asset kind is not supported.", nameof(asset));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(asset.RelativePath);
        if (Path.IsPathRooted(asset.RelativePath)
            || asset.RelativePath.Contains(':')
            || asset.RelativePath.Contains('\0')
            || asset.RelativePath.Replace('\\', '/').Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Asset paths must be normalized relative paths.", nameof(asset));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(asset.SizeBytes);
        if (asset.Sha256 is not null
            && (asset.Sha256.Length != 64 || asset.Sha256.Any(static character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException("Asset SHA-256 values must be 64 hexadecimal characters.", nameof(asset));
        }
    }

    private static void ValidateAssetId(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        if (assetId.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Asset IDs cannot contain whitespace or control characters.", nameof(assetId));
        }
    }
}

/// <summary>On-disk shape of one recording's catalog file.</summary>
internal sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string? GroupId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string SourceMode { get; set; } = "";
    public long StartedAtTicks { get; set; }
    public string Status { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public List<HighlightRow> Highlights { get; set; } = [];
    public List<CatalogAsset> Assets { get; set; } = [];
    public List<CatalogRenderJob> RenderJobs { get; set; } = [];
}

internal sealed class HighlightRow
{
    public string Id { get; set; } = "";
    public long QpcTicks { get; set; }
    public DateTimeOffset WallTime { get; set; }
    public string Source { get; set; } = "";
    public string? ScreenPath { get; set; }
    public string? CameraPath { get; set; }
    public bool Included { get; set; } = true;
    public long HoldMs { get; set; } = 1500;
    public int Ordering { get; set; }
}

internal sealed class GroupRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "unassigned";
    public long CreatedAtTicks { get; set; }
    public int SortOrder { get; set; }
}

public sealed record CatalogSession(
    string Id,
    string Name,
    string SourceMode,
    string Status,
    string DirectoryPath,
    long StartedAtTicks,
    string? Description = null,
    string? GroupId = null);

public sealed record CatalogHighlight(
    HighlightEvent Event,
    DateTimeOffset WallTime,
    int Ordering);

public sealed record CatalogAsset(
    string Id,
    string SessionId,
    string Kind,
    string RelativePath,
    string? Sha256,
    long SizeBytes,
    DateTimeOffset CreatedAt);
