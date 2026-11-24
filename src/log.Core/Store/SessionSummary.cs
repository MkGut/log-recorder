using System.Text.Json;
using System.Text.Json.Serialization;
using Log.Core.Timeline;

namespace Log.Core.Store;

public sealed record SessionSummary(
    string Id,
    string SourceMode,
    SessionStatus Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    long StartedAtTicks,
    long? StoppedAtTicks,
    long ActiveDurationTicks,
    int SlotCount,
    IReadOnlyDictionary<string, int> TrackSlotCounts,
    long SegmentBytes,
    long StorageBytes,
    int HighlightCount,
    int GapCount)
{
    public IReadOnlyList<string> PrunedTracks { get; init; } = [];
    public string SamplingMode { get; init; } = SessionManifest.FixedSamplingMode;
    public string Name { get; init; } = string.Empty;
    public bool StorageMeasured { get; init; } = true;
}

public sealed record SessionLibraryCard(
    CatalogSession Session,
    SessionSummary Summary);

public sealed record SessionSummaryReadOptions(bool MeasureStorage)
{
    public static SessionSummaryReadOptions Full { get; } = new(true);
    public static SessionSummaryReadOptions MetadataOnly { get; } = new(false);
}

public static class SessionSummaryReader
{

    public static async Task<SessionSummary> ReadAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        return await ReadAsync(
            sessionDirectory,
            SessionSummaryReadOptions.Full,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SessionSummary> ReadAsync(
        string sessionDirectory,
        SessionSummaryReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(sessionDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The session directory was not found: '{directory}'.");
        }

        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The session directory cannot be a reparse point.");
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The session manifest was not found.", manifestPath);
        }

        if (File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The session manifest cannot be a reparse point.");
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            useAsync: true);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream,
            ManifestJson.Default.SessionManifest,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The session manifest is empty.");

        if (manifest.Pauses is null
            || manifest.Tracks is null
            || manifest.TrackSlots is null
            || manifest.Slots is null
            || manifest.Segments is null
            || manifest.Highlights is null
            || manifest.PrunedTracks is null)
        {
            throw new InvalidDataException("The session manifest has incomplete summary collections.");
        }
        if (manifest.SourceMode is not ("screen" or "camera" or "screen+camera")
            || manifest.PrunedTracks.Any(track => !manifest.Tracks.ContainsKey(track))
            || manifest.PrunedTracks.Distinct(StringComparer.Ordinal).Count() != manifest.PrunedTracks.Count)
        {
            throw new InvalidDataException("The session manifest has invalid summary track metadata.");
        }

        if (manifest.SamplingMode is not (SessionManifest.FixedSamplingMode
            or SessionManifest.AdaptiveSamplingMode
            or SessionManifest.RandomSamplingMode))
        {
            throw new InvalidDataException("The session manifest has an invalid summary sampling mode.");
        }

        if (manifest.SamplingMode == SessionManifest.RandomSamplingMode)
        {
            if (manifest.RandomSampling is null)
            {
                throw new InvalidDataException("The random sampling schedule is missing.");
            }

            manifest.RandomSampling.Validate();
        }
        else if (manifest.RandomSampling is not null)
        {
            throw new InvalidDataException("The session manifest has unexpected random sampling metadata.");
        }

        if (manifest.LastExportSettings is not null)
        {
            try
            {
                _ = manifest.LastExportSettings.ToRenderSettings();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException(
                    "The session manifest has invalid saved export settings.",
                    exception);
            }
        }

        var prunedTracks = manifest.PrunedTracks.ToHashSet(StringComparer.Ordinal);
        var storage = options.MeasureStorage
            ? SessionStorageReader.Measure(directory, cancellationToken)
            : null;

        var isDual = manifest.SourceMode == "screen+camera";
        var trackSlotCounts = manifest.TrackSlots
            .GroupBy(static slot => slot.Track, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var gapCount = isDual
            ? manifest.TrackSlots.Count(slot => slot.SourceTicks < 0 || prunedTracks.Contains(slot.Track))
            : manifest.Slots.Count(slot => slot.SourceTicks < 0 || prunedTracks.Contains(manifest.SourceMode));
        var slotCount = isDual
            ? manifest.TrackSlots.Select(static slot => slot.Index).Distinct().Count()
            : manifest.Slots.Count;
        var activeDuration = manifest.StoppedAtTicks is long stoppedAt
            ? TimelinePlanner.ActiveDuration(
                checked(stoppedAt - manifest.StartedAtTicks),
                manifest.StartedAtTicks,
                stoppedAt,
                manifest.Pauses)
            : 0;

        return new SessionSummary(
            manifest.Id,
            manifest.SourceMode,
            manifest.Status,
            manifest.StartedAtUtc,
            manifest.StoppedAtUtc,
            manifest.StartedAtTicks,
            manifest.StoppedAtTicks,
            activeDuration,
            slotCount,
            trackSlotCounts,
            manifest.Segments.Sum(static segment => segment.ByteLength),
            storage?.TotalBytes ?? 0,
            manifest.Highlights.Count,
            gapCount)
        {
            PrunedTracks = manifest.PrunedTracks.ToArray(),
            SamplingMode = manifest.SamplingMode,
            Name = manifest.Name,
            StorageMeasured = storage is not null
        };
    }
}
