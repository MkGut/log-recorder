namespace Log.Core.Shell;

public enum ReminderTriggerKind
{
    WakeOrUnlock,
    ColdSignIn
}

public enum ReminderDisposition
{
    NotDue,
    Present,
    Suppressed
}

public enum ReminderSuppressionReason
{
    Disabled,
    Locked,
    CaptureOrFinalizationActive,
    OutsideActiveHours,
    PresentationMode,
    WindowsNotificationsSuppressed,
    CooldownActive,
    LocalDateMovedBackwards
}

public sealed record WakeReminderState
{
    public string? PendingEventId { get; init; }
    public ReminderTriggerKind? PendingTrigger { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public string? LastEventId { get; init; }
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    // High-water date; the policy never moves it backward after a clock/time-zone change.
    public DateOnly? InvitationLocalDate { get; init; }
    public int InvitationsShownOnLocalDate { get; init; }
    public string[] ProcessedEventIds { get; set; } = [];
    public bool EventIdentityLimitReached { get; init; }
}

public sealed record ReminderEligibility
{
    // Defaults are deliberately fail-closed. The adapter must confirm opt-in
    // and every safety/notification condition before showing a reminder.
    public bool Enabled { get; init; }
    public bool IsLocked { get; init; } = true;
    public bool IsCaptureOrFinalizationActive { get; init; } = true;
    public bool IsWithinActiveHours { get; init; }
    public bool IsPresentationMode { get; init; } = true;
    public bool AreWindowsNotificationsSuppressed { get; init; } = true;
}

public sealed record ReminderEvaluation(
    WakeReminderState State,
    ReminderDisposition Disposition,
    ReminderSuppressionReason? SuppressionReason = null);

/// <summary>
/// Deterministic reminder timing and suppression policy. The app adapter owns
/// persistence, Windows signals, and notification presentation.
/// </summary>
public sealed class WakeReminderPolicy
{
    public const int MaximumRememberedEventIdsPerLocalDay = 1024;
    public static readonly TimeSpan DismissalCooldown = TimeSpan.FromHours(2);

    private readonly Func<int, int, int> sampleInteger;

    /// <param name="sampleInteger">Injected inclusive-minimum/exclusive-maximum sampler.</param>
    public WakeReminderPolicy(Func<int, int, int> sampleInteger)
    {
        ArgumentNullException.ThrowIfNull(sampleInteger);
        this.sampleInteger = sampleInteger;
    }

    public WakeReminderState Schedule(
        WakeReminderState state,
        string eventId,
        ReminderTriggerKind trigger,
        DateTimeOffset eventOccurredAtUtc,
        DateTimeOffset nowUtc,
        DateOnly localDate,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (eventId.Length > 128
            || eventId.Any(static character => character < '!' || character > '~'))
        {
            throw new ArgumentException(
                "Reminder event IDs must be at most 128 printable ASCII characters.",
                nameof(eventId));
        }

        var now = nowUtc.ToUniversalTime();
        var occurred = eventOccurredAtUtc.ToUniversalTime();
        if (occurred > now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventOccurredAtUtc),
                "A reminder trigger cannot occur in the future.");
        }

        if (IsLocalDateMovedBackwards(state, localDate))
        {
            return ClearPending(state);
        }
        state = ResetDailyCount(state, localDate);
        if (state.ProcessedEventIds.Contains(eventId, StringComparer.Ordinal)
            || string.Equals(state.PendingEventId, eventId, StringComparison.Ordinal))
        {
            return state;
        }

        // Persist a bounded daily identity window so a later duplicate signal
        // cannot become a second prompt after an overlapping event was coalesced.
        if (state.EventIdentityLimitReached
            || state.ProcessedEventIds.Length >= MaximumRememberedEventIdsPerLocalDay)
        {
            return state with { EventIdentityLimitReached = true };
        }

        state = state with
        {
            LastEventId = eventId,
            ProcessedEventIds = [.. state.ProcessedEventIds, eventId]
        };
        if (!enabled)
        {
            return ClearPending(state);
        }

        if (state.PendingEventId is not null)
        {
            return state;
        }

        if (state.CooldownUntilUtc is { } cooldown && now < cooldown.ToUniversalTime())
        {
            return ClearPending(state);
        }

        var (minimumSeconds, maximumSeconds) = trigger switch
        {
            ReminderTriggerKind.WakeOrUnlock => (3, 7),
            ReminderTriggerKind.ColdSignIn => (8, 15),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger))
        };
        var delaySeconds = sampleInteger(minimumSeconds, maximumSeconds + 1);
        if (delaySeconds < minimumSeconds || delaySeconds > maximumSeconds)
        {
            throw new InvalidOperationException("The injected reminder-delay sampler returned a value outside the requested range.");
        }

        var dueAtUtc = occurred.AddSeconds(delaySeconds);
        if (dueAtUtc <= now)
        {
            // A delayed/replayed signal is expired instead of becoming a catch-up prompt.
            return ClearPending(state);
        }

        return state with
        {
            PendingEventId = eventId,
            PendingTrigger = trigger,
            DueAtUtc = dueAtUtc
        };
    }

    public static ReminderEvaluation Evaluate(
        WakeReminderState state,
        DateTimeOffset nowUtc,
        DateOnly localDate,
        ReminderEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(eligibility);

        if (IsLocalDateMovedBackwards(state, localDate) && state.PendingEventId is not null)
        {
            return new ReminderEvaluation(
                ClearPending(state),
                ReminderDisposition.Suppressed,
                ReminderSuppressionReason.LocalDateMovedBackwards);
        }
        state = ResetDailyCount(state, localDate);
        if (state.PendingEventId is null
            || state.DueAtUtc is not { } dueAtUtc
            || nowUtc.ToUniversalTime() < dueAtUtc.ToUniversalTime())
        {
            return new ReminderEvaluation(state, ReminderDisposition.NotDue);
        }

        var suppression = GetSuppressionReason(state, nowUtc.ToUniversalTime(), eligibility);
        if (suppression is not null)
        {
            return new ReminderEvaluation(
                ClearPending(state),
                ReminderDisposition.Suppressed,
                suppression);
        }

        return new ReminderEvaluation(
            ClearPending(state) with
            {
                InvitationLocalDate = localDate,
                InvitationsShownOnLocalDate = checked(state.InvitationsShownOnLocalDate + 1)
            },
            ReminderDisposition.Present);
    }

    public static WakeReminderState Dismiss(
        WakeReminderState state,
        DateTimeOffset dismissedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ClearPending(state) with
        {
            CooldownUntilUtc = dismissedAtUtc.ToUniversalTime() + DismissalCooldown
        };
    }

    /// <summary>Clear a scheduled invitation as soon as the opt-in is disabled.</summary>
    public static WakeReminderState Disable(WakeReminderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ClearPending(state);
    }

    /// <summary>
    /// Call on process startup. Any reminder whose due time elapsed while the
    /// app was unavailable is discarded, never shown as a catch-up prompt.
    /// </summary>
    public static WakeReminderState ExpireOverdueOnStartup(
        WakeReminderState state,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.DueAtUtc is { } dueAtUtc && dueAtUtc.ToUniversalTime() <= nowUtc.ToUniversalTime()
            ? ClearPending(state)
            : state;
    }

    public static WakeReminderState ExpireOverdueOnStartup(
        WakeReminderState state,
        DateTimeOffset nowUtc,
        DateOnly localDate)
    {
        ArgumentNullException.ThrowIfNull(state);
        return IsLocalDateMovedBackwards(state, localDate)
            ? ClearPending(state)
            : ExpireOverdueOnStartup(state, nowUtc);
    }

    private static bool IsLocalDateMovedBackwards(WakeReminderState state, DateOnly localDate)
    {
        return state.InvitationLocalDate is { } lastLocalDate && localDate < lastLocalDate;
    }

    private static ReminderSuppressionReason? GetSuppressionReason(
        WakeReminderState state,
        DateTimeOffset nowUtc,
        ReminderEligibility eligibility)
    {
        if (!eligibility.Enabled)
        {
            return ReminderSuppressionReason.Disabled;
        }

        if (eligibility.IsLocked)
        {
            return ReminderSuppressionReason.Locked;
        }

        if (eligibility.IsCaptureOrFinalizationActive)
        {
            return ReminderSuppressionReason.CaptureOrFinalizationActive;
        }

        if (!eligibility.IsWithinActiveHours)
        {
            return ReminderSuppressionReason.OutsideActiveHours;
        }

        if (eligibility.IsPresentationMode)
        {
            return ReminderSuppressionReason.PresentationMode;
        }

        if (eligibility.AreWindowsNotificationsSuppressed)
        {
            return ReminderSuppressionReason.WindowsNotificationsSuppressed;
        }

        if (state.CooldownUntilUtc is { } cooldown && nowUtc < cooldown.ToUniversalTime())
        {
            return ReminderSuppressionReason.CooldownActive;
        }

        return null;
    }

    private static WakeReminderState ResetDailyCount(WakeReminderState state, DateOnly localDate)
    {
        if (IsLocalDateMovedBackwards(state, localDate))
        {
            return state;
        }

        if (state.InvitationLocalDate == localDate)
        {
            // The count is informational only; there is no daily cap.
            if (state.InvitationsShownOnLocalDate < 0)
            {
                state = state with { InvitationsShownOnLocalDate = 0 };
            }

            var processedEventIds = state.ProcessedEventIds;
            if (processedEventIds is null
                || processedEventIds.Length > MaximumRememberedEventIdsPerLocalDay
                || processedEventIds.Any(static eventId =>
                    eventId is null
                    || eventId.Length == 0
                    || eventId.Length > 128
                    || eventId.Any(static character => character < '!' || character > '~')))
            {
                return state with
                {
                    ProcessedEventIds = [],
                    EventIdentityLimitReached = true,
                    LastEventId = null,
                    PendingEventId = null,
                    PendingTrigger = null,
                    DueAtUtc = null
                };
            }

            return state;
        }

        return state with
        {
            InvitationLocalDate = localDate,
            InvitationsShownOnLocalDate = 0,
            ProcessedEventIds = [],
            EventIdentityLimitReached = false,
            LastEventId = null
        };
    }

    private static WakeReminderState ClearPending(WakeReminderState state)
    {
        return state with
        {
            PendingEventId = null,
            PendingTrigger = null,
            DueAtUtc = null
        };
    }
}
