using Log.Core.Timeline;
using Log.Core.Capture;
using Log.Core.Render;

namespace Log.Core.Store;

public sealed record SessionLibraryCardIssue(
    string SessionId,
    string DirectoryPath,
    string Reason);

public sealed record SessionLibraryListing(
    IReadOnlyList<SessionLibraryCard> Cards,
    IReadOnlyList<SessionLibraryCardIssue> Issues);

/// <summary>
/// Catalog-backed browsing that never runs startup recovery or exposes mutating
/// library services. The normal <see cref="SessionLibrary.OpenAsync"/> path
/// remains the sole startup reconciliation boundary.
/// </summary>
public sealed class SessionLibraryReadOnly : IAsyncDisposable
{
    private readonly CatalogStore catalog;

    internal SessionLibraryReadOnly(string rootDirectory, CatalogStore catalog)
    {
        RootDirectory = rootDirectory;
        this.catalog = catalog;
    }

    public string RootDirectory { get; }

    public Task<SessionLibraryListing> ListSessionCardsDetailedAsync(
        CancellationToken cancellationToken = default)
    {
        return SessionLibrary.ListSessionCardsCoreAsync(
            catalog,
            SessionSummaryReadOptions.Full,
            cancellationToken);
    }

    /// <summary>
    /// Lists cards with an explicit summary read mode. Metadata-only mode reads
    /// the manifest and skips recursive asset measurement; full mode preserves
    /// the existing storage-size behavior.
    /// </summary>
    public Task<SessionLibraryListing> ListSessionCardsDetailedAsync(
        SessionSummaryReadOptions summaryOptions,
        CancellationToken cancellationToken = default)
    {
        return SessionLibrary.ListSessionCardsCoreAsync(
            catalog,
            summaryOptions,
            cancellationToken);
    }

    public Task<IReadOnlyList<SessionExportPreview>> ListPendingExportPreviewsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return new SessionExportService(catalog, RootDirectory)
            .ListPendingPreviewsAsync(sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionLibraryCard>> ListSessionCardsAsync(
        CancellationToken cancellationToken = default)
    {
        var listing = await ListSessionCardsDetailedAsync(cancellationToken).ConfigureAwait(false);
        return listing.Cards;
    }

    public async Task<IReadOnlyList<SessionLibraryCard>> ListSessionCardsAsync(
        SessionSummaryReadOptions summaryOptions,
        CancellationToken cancellationToken = default)
    {
        var listing = await ListSessionCardsDetailedAsync(
            summaryOptions,
            cancellationToken).ConfigureAwait(false);
        return listing.Cards;
    }

    public ValueTask DisposeAsync() => catalog.DisposeAsync();
}

public sealed class SessionLibrary : IAsyncDisposable
{
    private SessionLibrary(
        string rootDirectory,
        CatalogStore catalog,
        RecoveryReport recoveryReport)
    {
        RootDirectory = rootDirectory;
        Catalog = catalog;
        RecoveryReport = recoveryReport;
        Deletion = new SessionDeletionService(catalog, rootDirectory);
        Export = new SessionExportService(catalog, rootDirectory);
    }

    public string RootDirectory { get; }
    public CatalogStore Catalog { get; }
    public RecoveryReport RecoveryReport { get; }
    public SessionDeletionService Deletion { get; }
    public SessionExportService Export { get; }

    public Task SaveLastCaptureSetupAsync(
        CaptureRunOptions options,
        CancellationToken cancellationToken = default)
    {
        return CaptureSetupStore.SaveAsync(Catalog, options, cancellationToken);
    }

    public Task<CaptureRunOptions?> LoadLastCaptureSetupAsync(
        CancellationToken cancellationToken = default)
    {
        return CaptureSetupStore.LoadAsync(Catalog, cancellationToken);
    }

    public static async Task<SessionLibrary> OpenAsync(
        string? rootDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
            ? LogPaths.DefaultRootDirectory
            : Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(resolvedRoot);
        if (File.GetAttributes(resolvedRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The log library root cannot be a reparse point.");
        }

        var catalog = await CatalogStore.OpenAsync(
            LogPaths.CatalogPath(resolvedRoot),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await SessionExportService.RecoverAbandonedRenderJobsAsync(
                catalog,
                resolvedRoot,
                cancellationToken).ConfigureAwait(false);
            await catalog.RecoverRunningRenderJobsAsync(
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await SessionExportService.RecoverCompletedPreviewJobsAsync(
                catalog,
                resolvedRoot,
                cancellationToken).ConfigureAwait(false);
            var recoveryReport = await SessionRecovery.RecoverWithReportAsync(
                resolvedRoot,
                catalog,
                cancellationToken).ConfigureAwait(false);
            return new SessionLibrary(resolvedRoot, catalog, recoveryReport);
        }
        catch
        {
            await catalog.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<SessionLibraryReadOnly> OpenReadOnlyAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var resolvedRoot = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(resolvedRoot))
        {
            throw new DirectoryNotFoundException($"The log library root was not found: '{resolvedRoot}'.");
        }

        if (File.GetAttributes(resolvedRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The log library root cannot be a reparse point.");
        }

        var catalogPath = LogPaths.CatalogPath(resolvedRoot);
        if (!Directory.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                "The read-only library path requires a catalog created by startup reconciliation.",
                catalogPath);
        }

        var catalog = await CatalogStore.OpenReadOnlyAsync(
            catalogPath,
            cancellationToken).ConfigureAwait(false);
        return new SessionLibraryReadOnly(resolvedRoot, catalog);
    }

    public Task<IReadOnlyList<CatalogSession>> ListSessionsAsync(
        bool includeDeleting = false,
        CancellationToken cancellationToken = default)
    {
        return Catalog.ListSessionsAsync(includeDeleting, cancellationToken);
    }

    public Task<IReadOnlyList<SessionExportPreview>> ListPendingExportPreviewsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return Export.ListPendingPreviewsAsync(sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionLibraryCard>> ListSessionCardsAsync(
        CancellationToken cancellationToken = default)
    {
        var listing = await ListSessionCardsDetailedAsync(cancellationToken).ConfigureAwait(false);
        return listing.Cards;
    }

    public Task<SessionLibraryListing> ListSessionCardsDetailedAsync(
        CancellationToken cancellationToken = default)
    {
        return ListSessionCardsCoreAsync(
            Catalog,
            SessionSummaryReadOptions.Full,
            cancellationToken);
    }

    public Task<SessionLibraryListing> ListSessionCardsDetailedAsync(
        SessionSummaryReadOptions summaryOptions,
        CancellationToken cancellationToken = default)
    {
        return ListSessionCardsCoreAsync(
            Catalog,
            summaryOptions,
            cancellationToken);
    }

    internal static async Task<SessionLibraryListing> ListSessionCardsCoreAsync(
        CatalogStore catalog,
        CancellationToken cancellationToken)
    {
        return await ListSessionCardsCoreAsync(
            catalog,
            SessionSummaryReadOptions.Full,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SessionLibraryListing> ListSessionCardsCoreAsync(
        CatalogStore catalog,
        SessionSummaryReadOptions summaryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(summaryOptions);
        var sessions = await catalog.ListSessionsAsync(
            includeDeleting: false,
            cancellationToken).ConfigureAwait(false);

        var activeRenderSessionIds = await catalog.ListActiveRenderSessionIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        var cards = new List<SessionLibraryCard>(sessions.Count);
        var issues = new List<SessionLibraryCardIssue>();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Status is "starting" or "recording" or "finalizing")
            {
                issues.Add(new SessionLibraryCardIssue(
                    session.Id,
                    session.DirectoryPath,
                    "active-capture"));
                continue;
            }

            if (activeRenderSessionIds.Contains(session.Id))
            {
                issues.Add(new SessionLibraryCardIssue(
                    session.Id,
                    session.DirectoryPath,
                    "active-render"));
                continue;
            }

            try
            {
                var summary = await SessionSummaryReader.ReadAsync(
                    session.DirectoryPath,
                    summaryOptions,
                    cancellationToken).ConfigureAwait(false);
                cards.Add(new SessionLibraryCard(session, summary));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "manifest-missing"));
            }
            catch (DirectoryNotFoundException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "directory-missing"));
            }
            catch (InvalidDataException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "manifest-invalid"));
            }
            catch (System.Text.Json.JsonException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "manifest-invalid-json"));
            }
            catch (IOException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "session-unreadable"));
            }
            catch (UnauthorizedAccessException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "session-access-denied"));
            }
            catch (ArgumentException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "manifest-invalid"));
            }
            catch (NotSupportedException)
            {
                issues.Add(new SessionLibraryCardIssue(session.Id, session.DirectoryPath, "session-unsupported"));
            }
        }

        return new SessionLibraryListing(cards, issues);
    }

    public async Task<SessionSummary> GetSessionSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await Catalog.GetSessionAsync(
            sessionId,
            includeDeleting: false,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        return await SessionSummaryReader.ReadAsync(session.DirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRenderInput> GetSessionRenderInputAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await Catalog.GetSessionAsync(
            sessionId,
            includeDeleting: false,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        return SessionRenderInputBuilder.Build(store.Manifest);
    }

    public async Task<RenderSettings?> GetLastExportSettingsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await Catalog.GetSessionAsync(
            sessionId,
            includeDeleting: false,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        return store.Manifest.LastExportSettings?.ToRenderSettings();
    }

    public async Task RenameSessionAsync(
        string sessionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (await Catalog.GetSessionAsync(
            sessionId,
            includeDeleting: false,
            cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        }

        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await store.UpdateMetadataAsync(name, store.Manifest.Description, cancellationToken)
            .ConfigureAwait(false);
        await Catalog.UpdateSessionMetadataAsync(
            sessionId,
            store.Manifest.Name,
            store.Manifest.Description,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSessionMetadataAsync(
        string sessionId,
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (await Catalog.GetSessionAsync(
                sessionId,
                includeDeleting: false,
                cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        }

        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await store.UpdateMetadataAsync(name, description, cancellationToken).ConfigureAwait(false);
        await Catalog.UpdateSessionMetadataAsync(
            sessionId,
            store.Manifest.Name,
            store.Manifest.Description,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSessionExportedAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (await Catalog.GetSessionAsync(
                sessionId,
                includeDeleting: false,
                cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        }

        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await store.MarkExportedAsync(cancellationToken).ConfigureAwait(false);
        await Catalog.MarkSessionExportedAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateHighlightAsync(
        string sessionId,
        string highlightId,
        bool? included = null,
        long? holdTicks = null,
        CancellationToken cancellationToken = default)
    {
        if (await Catalog.GetSessionAsync(sessionId, includeDeleting: false, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        }

        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await store.UpdateHighlightAsync(
            highlightId,
            included,
            holdTicks,
            cancellationToken).ConfigureAwait(false);
        var updated = store.Manifest.Highlights.Single(highlight => highlight.Id == highlightId);
        await Catalog.UpdateHighlightAsync(sessionId, updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteHighlightAsync(
        string sessionId,
        string highlightId,
        CancellationToken cancellationToken = default)
    {
        if (await Catalog.GetSessionAsync(sessionId, includeDeleting: false, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found or is being deleted.");
        }

        await using var store = await SessionStore.OpenAsync(
            RootDirectory,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        await store.DeleteHighlightAsync(highlightId, cancellationToken).ConfigureAwait(false);
        await Catalog.DeleteHighlightAsync(sessionId, highlightId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return Catalog.DisposeAsync();
    }
}
