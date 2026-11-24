namespace Log.Core.Shell;

public enum RecordingSurfaceStatus
{
    NotStarted,
    Recording,
    Paused,
    Error
}

/// <summary>
/// State for the recording surface. Source identity is supplied by the active
/// capture adapter; elapsed time is supplied by the engine clock.
/// </summary>
public sealed record RecordingSurfacePresentation(
    RecordingSurfaceStatus Status,
    bool HasTimer,
    bool TimerAdvancing,
    TimeSpan Elapsed,
    string? SourceIdentity,
    string? ErrorMessage);

/// <summary>Projects authoritative engine state into a small, UI-independent recording snapshot.</summary>
public static class RecordingSurfaceModel
{
    public static RecordingSurfacePresentation Create(
        RecordingSurfaceStatus status,
        TimeSpan engineElapsed,
        string? captureSourceIdentity,
        string? errorMessage = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(engineElapsed, TimeSpan.Zero);
        if (status == RecordingSurfaceStatus.NotStarted)
        {
            return new RecordingSurfacePresentation(
                status,
                HasTimer: false,
                TimerAdvancing: false,
                Elapsed: TimeSpan.Zero,
                SourceIdentity: null,
                ErrorMessage: null);
        }

        return new RecordingSurfacePresentation(
            status,
            HasTimer: true,
            TimerAdvancing: status == RecordingSurfaceStatus.Recording,
            Elapsed: engineElapsed,
            SourceIdentity: string.IsNullOrWhiteSpace(captureSourceIdentity)
                ? null
                : captureSourceIdentity,
            ErrorMessage: status == RecordingSurfaceStatus.Error
                ? errorMessage
                : null);
    }

    /// <summary>Announce meaningful state changes while ignoring per-tick elapsed updates.</summary>
    public static bool ShouldAnnounceTransition(
        RecordingSurfacePresentation previous,
        RecordingSurfacePresentation current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return previous.Status != current.Status
            || !string.Equals(previous.SourceIdentity, current.SourceIdentity, StringComparison.Ordinal)
            || !string.Equals(previous.ErrorMessage, current.ErrorMessage, StringComparison.Ordinal);
    }
}
