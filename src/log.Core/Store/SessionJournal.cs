using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Log.Core.Store;

public sealed record SessionJournalRecord(
    long Sequence,
    string Kind,
    int SlotIndex,
    long SourceTicks,
    string AssetReference,
    string? Checksum = null,
    string? EventId = null,
    string? HighlightSource = null,
    long? HoldTicks = null,
    bool? Included = null,
    string? Track = null,
    long? TargetTicks = null,
    bool? IsDuplicate = null,
    long? AgeTicks = null,
    int? SegmentIndex = null,
    int? SegmentSlotCount = null,
    long? SegmentStartTicks = null,
    long? SegmentEndTicks = null,
    long? ByteLength = null,
    long? PauseStartTicks = null,
    long? PauseEndTicks = null,
    string? ScreenAssetReference = null,
    string? CameraAssetReference = null,
    long? ArrivalTicks = null,
    string? TimestampQuality = null,
    int? SourceEpoch = null,
    long? ActiveDurationTicks = null,
    long? SamplingIntervalTicks = null,
    // New nullable fields stay absent when unused so old journal checksums
    // remain canonical after the record gains random-sampling metadata.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RandomWindowStartTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RandomWindowEndTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RandomWindowIntervalTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RandomWindowTargetTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RandomSamplingSeed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RandomSamplingAlgorithmVersion = null);

public sealed record SessionJournalReadResult(
    IReadOnlyList<SessionJournalRecord> Records,
    int InvalidRecordCount,
    bool HasTruncatedFinalRecord);

public static class SessionJournalReader
{

    public static async Task<SessionJournalReadResult> ReadAsync(
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!File.Exists(journalPath))
        {
            return new SessionJournalReadResult([], 0, false);
        }

        var records = new List<SessionJournalRecord>();
        var invalidRecordCount = 0;
        var hasTruncatedFinalRecord = false;
        await using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        while (line is not null)
        {
            var nextLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var isFinalLine = nextLine is null;
            if (string.IsNullOrWhiteSpace(line))
            {
                line = nextLine;
                continue;
            }

            SessionJournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize(line, PlainJson.Default.SessionJournalRecord);
            }
            catch (JsonException)
            {
                if (isFinalLine)
                {
                    hasTruncatedFinalRecord = true;
                    break;
                }

                invalidRecordCount++;
                line = nextLine;
                continue;
            }

            if (record is null || !HasValidChecksum(record))
            {
                invalidRecordCount++;
                line = nextLine;
                continue;
            }

            records.Add(record);
            line = nextLine;
        }

        return new SessionJournalReadResult(records, invalidRecordCount, hasTruncatedFinalRecord);
    }

    private static bool HasValidChecksum(SessionJournalRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Checksum))
        {
            return false;
        }

        var unsigned = JsonSerializer.Serialize(record with { Checksum = null }, PlainJson.Default.SessionJournalRecord);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unsigned)));
        return string.Equals(expected, record.Checksum, StringComparison.OrdinalIgnoreCase);
    }
}
