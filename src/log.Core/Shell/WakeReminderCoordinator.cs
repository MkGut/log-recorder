using Log.Core.Shell;

namespace Log.Core.Shell;

/// <summary>
/// Applies reminder lifecycle transitions through one durable state-store instance.
/// Windows event registration, timer scheduling, eligibility queries, settings, and
/// presentation remain responsibilities of the app adapter.
/// </summary>
public sealed class WakeReminderCoordinator
{
    private readonly WakeReminderPolicy policy;
    private readonly WakeReminderStateStore stateStore;

    public WakeReminderCoordinator(
        WakeReminderPolicy policy,
        WakeReminderStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(stateStore);
        this.policy = policy;
        this.stateStore = stateStore;
    }

    /// <summary>
    /// Persists startup recovery before the adapter subscribes to wake or session events.
    /// An overdue invitation is discarded instead of becoming a catch-up notification.
    /// </summary>
    public Task<WakeReminderStateLoadResult> RecoverOnStartupAsync(
        DateOnly localDate,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        return stateStore.UpdateAsync(
            localDate,
            state => WakeReminderPolicy.ExpireOverdueOnStartup(state, nowUtc, localDate),
            cancellationToken);
    }

    /// <summary>
    /// Persists one wake, unlock, or sign-in event and its sampled due time.
    /// </summary>
    public Task<WakeReminderStateLoadResult> ObserveAsync(
        DateOnly localDate,
        string eventId,
        ReminderTriggerKind trigger,
        DateTimeOffset eventOccurredAtUtc,
        DateTimeOffset nowUtc,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return stateStore.UpdateAsync(
            localDate,
            state => policy.Schedule(
                state,
                eventId,
                trigger,
                eventOccurredAtUtc,
                nowUtc,
                localDate,
                enabled),
            cancellationToken);
    }

    /// <summary>
    /// Evaluates a due invitation and durably consumes or suppresses it exactly once.
    /// A corrupt state returns no evaluation, which gives the adapter no Present result
    /// to act on.
    /// </summary>
    public async Task<WakeReminderEvaluationLoadResult> EvaluateAsync(
        DateOnly localDate,
        DateTimeOffset nowUtc,
        ReminderEligibility eligibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligibility);

        var loaded = await stateStore.LoadAsync(localDate, cancellationToken).ConfigureAwait(false);
        if (loaded.WasCorrupt)
        {
            return new WakeReminderEvaluationLoadResult(
                loaded.State,
                Evaluation: null,
                WasCorrupt: true);
        }

        var preview = WakeReminderPolicy.Evaluate(
            loaded.State,
            nowUtc,
            localDate,
            eligibility);
        if (preview.Disposition == ReminderDisposition.NotDue)
        {
            // Do not flush the state file on a timer tick that has nothing to consume.
            return new WakeReminderEvaluationLoadResult(
                preview.State,
                preview,
                WasCorrupt: false);
        }

        ReminderEvaluation? committedEvaluation = null;
        var committed = await stateStore.UpdateAsync(
            localDate,
            state =>
            {
                committedEvaluation = WakeReminderPolicy.Evaluate(
                    state,
                    nowUtc,
                    localDate,
                    eligibility);
                return committedEvaluation.State;
            },
            cancellationToken).ConfigureAwait(false);

        return new WakeReminderEvaluationLoadResult(
            committed.State,
            committed.WasCorrupt ? null : committedEvaluation,
            committed.WasCorrupt);
    }

    /// <summary>
    /// Persists dismissal and its two-hour cooldown.
    /// </summary>
    public Task<WakeReminderStateLoadResult> DismissAsync(
        DateOnly localDate,
        DateTimeOffset dismissedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return stateStore.UpdateAsync(
            localDate,
            state => WakeReminderPolicy.Dismiss(state, dismissedAtUtc),
            cancellationToken);
    }

    /// <summary>Persists cancellation of any scheduled invitation after opt-out.</summary>
    public Task<WakeReminderStateLoadResult> DisableAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        return stateStore.UpdateAsync(
            localDate,
            WakeReminderPolicy.Disable,
            cancellationToken);
    }
}

/// <summary>
/// A null evaluation with WasCorrupt set is fail-closed and must never be treated as
/// permission to present an invitation.
/// </summary>
public sealed record WakeReminderEvaluationLoadResult(
    WakeReminderState State,
    ReminderEvaluation? Evaluation,
    bool WasCorrupt);
