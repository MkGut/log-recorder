using System.Text.Json;
using System.Text.Json.Serialization;

namespace Log.Core.Store;

/// <summary>
/// Read-only compatibility access for the pre-journal group layout. The active
/// product never creates, repairs, deletes, or assigns group data.
/// </summary>
public sealed record GroupManifest(
    int SchemaVersion,
    string Id,
    string Name,
    GroupType Type,
    long CreatedAtTicks);

public static class GroupStorage
{

    internal static async Task<IReadOnlyList<RecoveryIssue>> ReconcileAsync(
        string rootDirectory,
        CatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(catalog);

        var issues = new List<RecoveryIssue>();
        var groupsDirectory = LogPaths.GroupsDirectory(Path.GetFullPath(rootDirectory));
        if (!Directory.Exists(groupsDirectory))
        {
            return issues;
        }

        try
        {
            if (File.GetAttributes(groupsDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                return [new RecoveryIssue(groupsDirectory, "groups-reparse-point")];
            }

            var catalogGroups = await catalog.ListAllGroupsAsync(cancellationToken)
                .ConfigureAwait(false);
            var knownGroups = catalogGroups
                .Select(static group => group.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var directory in Directory.EnumerateDirectories(groupsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
                var manifestPath = Path.Combine(directory, "group.json");
                if (!File.Exists(manifestPath))
                {
                    issues.Add(new RecoveryIssue(directory, "group-manifest-missing"));
                    continue;
                }

                try
                {
                    var manifest = await ReadManifestAsync(manifestPath, cancellationToken)
                        .ConfigureAwait(false);
                    ValidateManifest(directoryName, manifest);
                    if (!knownGroups.Contains(manifest.Id))
                    {
                        // Preserve orphaned historical metadata; it is not an
                        // instruction to recreate a group row.
                        issues.Add(new RecoveryIssue(directory, "group-unindexed"));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UnauthorizedAccessException)
                {
                    issues.Add(new RecoveryIssue(directory, "group-reconcile-access-denied"));
                }
                catch (IOException)
                {
                    issues.Add(new RecoveryIssue(directory, "group-reconcile-failed"));
                }
                catch (InvalidDataException)
                {
                    issues.Add(new RecoveryIssue(directory, "group-manifest-invalid"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new RecoveryIssue(groupsDirectory, "groups-access-denied"));
        }
        catch (IOException)
        {
            issues.Add(new RecoveryIssue(groupsDirectory, "groups-unreadable"));
        }

        return issues;
    }

    private static async Task<GroupManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);
        try
        {
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    ManifestJson.Default.GroupManifest,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The group manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The group manifest is not valid JSON.", exception);
        }
    }

    private static void ValidateManifest(string directoryName, GroupManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(manifest.Id)
            || !string.Equals(manifest.Id, directoryName, StringComparison.Ordinal)
            || manifest.CreatedAtTicks < 0
            || !Enum.IsDefined(manifest.Type))
        {
            throw new InvalidDataException("The group manifest has invalid identity or schema metadata.");
        }

        try
        {
            SessionIdPolicy.Validate(manifest.Id);
            _ = SessionNamePolicy.Normalize(manifest.Name);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The group manifest has invalid identity or name metadata.", exception);
        }
    }
}
