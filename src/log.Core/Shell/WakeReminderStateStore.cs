using Log.Core.Store;
using System.Text.Json;
using Log.Core.Shell;

namespace Log.Core.Shell;

public sealed record WakeReminderStateLoadResult(
    WakeReminderState State,
    bool WasCorrupt);

/// <summary>
/// Atomically persists reminder policy state and serializes complete transitions
/// performed through the same store instance.
/// </summary>
public sealed class WakeReminderStateStore : IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const long MaximumStateFileBytes = 1_048_576;


    private readonly string filePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    public WakeReminderStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    public async Task<WakeReminderStateLoadResult> LoadAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(localDate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Serializes one complete load-transition-save operation with every other
    /// operation performed through this store instance.
    /// </summary>
    public async Task<WakeReminderStateLoadResult> UpdateAsync(
        DateOnly localDate,
        Func<WakeReminderState, WakeReminderState> transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(localDate, cancellationToken).ConfigureAwait(false);
            if (current.WasCorrupt)
            {
                return current;
            }

            var updated = transition(current.State);
            ArgumentNullException.ThrowIfNull(updated);
            var snapshot = CopyState(updated);
            if (!IsValidState(snapshot))
            {
                throw new ArgumentException(
                    "The reminder transition produced state outside the persisted schema bounds.",
                    nameof(transition));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await SaveCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return new WakeReminderStateLoadResult(snapshot, WasCorrupt: false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        WakeReminderState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = CopyState(state);
        if (!IsValidState(snapshot))
        {
            throw new ArgumentException(
                "The reminder state is outside the persisted schema bounds.",
                nameof(state));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WakeReminderStateLoadResult> LoadCoreAsync(
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return new WakeReminderStateLoadResult(
                new WakeReminderState { InvitationLocalDate = localDate },
                WasCorrupt: false);
        }
        catch (DirectoryNotFoundException)
        {
            return new WakeReminderStateLoadResult(
                new WakeReminderState { InvitationLocalDate = localDate },
                WasCorrupt: false);
        }

        await using var sourceStream = source;
        if (sourceStream.Length > MaximumStateFileBytes)
        {
            return CreateCorruptResult(localDate);
        }

        PersistedEnvelope? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync(
                sourceStream,
                PlainJson.Default.PersistedEnvelope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return CreateCorruptResult(localDate);
        }

        if (envelope is null
            || envelope.SchemaVersion != CurrentSchemaVersion
            || !IsValidState(envelope.State))
        {
            return CreateCorruptResult(localDate);
        }

        return new WakeReminderStateLoadResult(envelope.State!, WasCorrupt: false);
    }

    private async Task SaveCoreAsync(
        WakeReminderState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The reminder-state file has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    destination,
                    new PersistedEnvelope
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        State = state
                    },
                    PlainJson.Default.PersistedEnvelope,
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                if (destination.Length > MaximumStateFileBytes)
                {
                    throw new InvalidDataException("The reminder state exceeds the persisted size limit.");
                }
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed cleanup leaves only this uniquely named temporary file.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the primary save error if the temporary file cannot be removed.
            }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            gate.Dispose();
        }
    }

    private static WakeReminderStateLoadResult CreateCorruptResult(DateOnly localDate)
    {
        return new WakeReminderStateLoadResult(
            new WakeReminderState
            {
                InvitationLocalDate = localDate,
                ProcessedEventIds = [],
                EventIdentityLimitReached = true
            },
            WasCorrupt: true);
    }

    private static WakeReminderState CopyState(WakeReminderState state)
    {
        if (state.ProcessedEventIds is null
            || state.ProcessedEventIds.Length > WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay)
        {
            return state;
        }

        return state with
        {
            ProcessedEventIds = [.. state.ProcessedEventIds]
        };
    }

    private static bool IsValidState(WakeReminderState? state)
    {
        if (state is null
            || state.InvitationsShownOnLocalDate < 0
            || state.ProcessedEventIds is null
            || state.ProcessedEventIds.Length > WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay)
        {
            return false;
        }

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var eventId in state.ProcessedEventIds)
        {
            if (!IsValidEventId(eventId) || !eventIds.Add(eventId))
            {
                return false;
            }
        }

        if (state.LastEventId is { } lastEventId
            && (!IsValidEventId(lastEventId) || !eventIds.Contains(lastEventId)))
        {
            return false;
        }

        var hasPendingEvent = state.PendingEventId is not null;
        if (hasPendingEvent != state.PendingTrigger.HasValue
            || hasPendingEvent != state.DueAtUtc.HasValue)
        {
            return false;
        }

        if (state.PendingEventId is { } pendingEventId
            && (!IsValidEventId(pendingEventId)
                || !eventIds.Contains(pendingEventId)
                || state.InvitationLocalDate is null))
        {
            return false;
        }

        return state.PendingTrigger is not { } trigger
            || Enum.IsDefined(trigger);
    }

    private static bool IsValidEventId(string? eventId)
    {
        return !string.IsNullOrEmpty(eventId)
            && eventId.Length <= 128
            && eventId.All(static character => character is >= '!' and <= '~');
    }

    internal sealed class PersistedEnvelope
    {
        public PersistedEnvelope()
        {
        }

        public int SchemaVersion { get; init; }
        public WakeReminderState? State { get; init; }
    }
}
