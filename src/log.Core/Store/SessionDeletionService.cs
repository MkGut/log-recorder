using System.Text.Json;
using System.Text.Json.Serialization;

namespace Log.Core.Store;

public sealed class SessionDeletionService
{

    private readonly CatalogStore catalog;
    private readonly string sessionsDirectory;

    public SessionDeletionService(CatalogStore catalog, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.catalog = catalog;
        sessionsDirectory = Path.GetFullPath(Path.Combine(rootDirectory, "sessions"));
    }

    public Task SoftDeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return catalog.SoftDeleteSessionAsync(sessionId, cancellationToken);
    }

    public async Task RestoreAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(session.Status, "deleting", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Session '{sessionId}' is not marked for deletion.");
        }

        var directory = ValidateSessionDirectory(session.DirectoryPath, sessionId);
        var status = await ReadRestorableStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        await catalog.RestoreSessionAsync(sessionId, status, cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(session.Status, "deleting", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Session '{sessionId}' must be soft-deleted before purge.");
        }

        var directory = ValidateSessionDirectory(session.DirectoryPath, sessionId);
        if (Directory.Exists(directory))
        {
            EnsureNoReparsePoints(directory);
            Directory.Delete(directory, recursive: true);
        }

        await catalog.RemoveDeletedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogSession> RequireSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await catalog.GetSessionAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return session ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private static async Task<SessionStatus> ReadRestorableStatusAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directoryPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return SessionStatus.Interrupted;
        }

        if (File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
        {
            return SessionStatus.Interrupted;
        }

        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            var manifest = await JsonSerializer.DeserializeAsync(stream, ManifestJson.Default.SessionManifest, cancellationToken)
                .ConfigureAwait(false);
            return manifest?.Status is SessionStatus.Ready or SessionStatus.Exported
                ? manifest.Status
                : SessionStatus.Interrupted;
        }
        catch (JsonException)
        {
            return SessionStatus.Interrupted;
        }
        catch (IOException)
        {
            return SessionStatus.Interrupted;
        }
    }

    private string ValidateSessionDirectory(string directoryPath, string sessionId)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var prefix = sessionsDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? sessionsDirectory
            : sessionsDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(fullPath), sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The catalog path is outside the session store and cannot be purged.");
        }

        if (Directory.Exists(fullPath)
            && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Reparse-point session directories cannot be purged automatically.");
        }

        return fullPath;
    }

    private static void EnsureNoReparsePoints(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "Reparse-point entries cannot be purged automatically.");
            }

            if (!Directory.Exists(current))
            {
                continue;
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                pending.Push(child);
            }
        }
    }
}
