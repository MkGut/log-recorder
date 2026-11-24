using System.Text.Json;
using Log.Core.Shell;
using Xunit;

namespace Log.Core.Tests;

public sealed class WakeReminderPolicyTests
{
    private static readonly DateOnly Day = new(2026, 11, 6);
    private static readonly DateTimeOffset EventTime = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static ReminderEligibility EligibleReminder() => new()
    {
        Enabled = true,
        IsLocked = false,
        IsCaptureOrFinalizationActive = false,
        IsWithinActiveHours = true,
        IsPresentationMode = false,
        AreWindowsNotificationsSuppressed = false
    };

    [Fact]
    public void DefaultEligibilityFailsClosedUntilOptInAndSafeConditionsAreExplicit()
    {
        var defaults = new ReminderEligibility();

        Assert.False(defaults.Enabled);
        Assert.True(defaults.IsLocked);
        Assert.True(defaults.IsCaptureOrFinalizationActive);
        Assert.False(defaults.IsWithinActiveHours);
        Assert.True(defaults.IsPresentationMode);
        Assert.True(defaults.AreWindowsNotificationsSuppressed);

        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var scheduled = policy.Schedule(
            new WakeReminderState(), "event-defaults", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);
        var optedInButUnknown = WakeReminderPolicy.Evaluate(
            scheduled,
            scheduled.DueAtUtc!.Value,
            Day,
            defaults with { Enabled = true });

        Assert.Equal(ReminderDisposition.Suppressed, optedInButUnknown.Disposition);
        Assert.Equal(ReminderSuppressionReason.Locked, optedInButUnknown.SuppressionReason);
        Assert.Null(optedInButUnknown.State.PendingEventId);
    }

    [Theory]
    [InlineData(ReminderTriggerKind.WakeOrUnlock, 3, 7, 7)]
    [InlineData(ReminderTriggerKind.ColdSignIn, 8, 15, 15)]
    public void SamplesAndPersistsAnInclusiveDelayRange(
        ReminderTriggerKind trigger,
        int minimumSeconds,
        int maximumSeconds,
        int sampledSeconds)
    {
        var samplerCalled = false;
        var policy = new WakeReminderPolicy((minimum, maximumExclusive) =>
        {
            samplerCalled = true;
            Assert.Equal(minimumSeconds, minimum);
            Assert.Equal(maximumSeconds + 1, maximumExclusive);
            return sampledSeconds;
        });

        var state = policy.Schedule(
            new WakeReminderState(),
            "event-1",
            trigger,
            EventTime,
            EventTime,
            Day,
            enabled: true);

        Assert.True(samplerCalled);
        Assert.Equal("event-1", state.PendingEventId);
        Assert.Equal(trigger, state.PendingTrigger);
        Assert.Equal(EventTime.AddSeconds(sampledSeconds), state.DueAtUtc);
    }

    [Fact]
    public void DuplicateAndOverlappingSignalsDoNotRerollThePendingDueTime()
    {
        var samples = 0;
        var policy = new WakeReminderPolicy((minimum, _) =>
        {
            samples++;
            return minimum;
        });
        var state = policy.Schedule(
            new WakeReminderState(), "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);
        var dueAt = state.DueAtUtc;

        state = policy.Schedule(
            state, "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime.AddSeconds(1), EventTime.AddSeconds(1), Day, enabled: true);
        state = policy.Schedule(
            state, "unlock-duplicate", ReminderTriggerKind.WakeOrUnlock,
            EventTime.AddSeconds(2), EventTime.AddSeconds(2), Day, enabled: true);

        Assert.Equal(1, samples);
        Assert.Equal(dueAt, state.DueAtUtc);
        Assert.Equal("event-1", state.PendingEventId);

        var presented = WakeReminderPolicy.Evaluate(
            state, dueAt!.Value, Day, EligibleReminder());
        var replayed = policy.Schedule(
            presented.State, "event-1", ReminderTriggerKind.WakeOrUnlock,
            dueAt.Value, dueAt.Value, Day, enabled: true);
        Assert.Equal(1, samples);
        Assert.Null(replayed.PendingEventId);
    }

    [Fact]
    public void DueReminderIsPresentedOnlyWhenEligibleAndConsumesItsPendingEvent()
    {
        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var state = policy.Schedule(
            new WakeReminderState(), "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);

        var beforeDue = WakeReminderPolicy.Evaluate(
            state, state.DueAtUtc!.Value.AddSeconds(-1), Day, EligibleReminder());
        Assert.Equal(ReminderDisposition.NotDue, beforeDue.Disposition);
        Assert.Equal("event-1", beforeDue.State.PendingEventId);

        var due = WakeReminderPolicy.Evaluate(
            beforeDue.State, state.DueAtUtc.Value, Day, EligibleReminder());

        Assert.Equal(ReminderDisposition.Present, due.Disposition);
        Assert.Null(due.State.PendingEventId);
        Assert.Equal(1, due.State.InvitationsShownOnLocalDate);
        Assert.Equal(Day, due.State.InvitationLocalDate);
    }

    [Theory]
    [InlineData(true, false, true, false, false, true, ReminderSuppressionReason.Locked)]
    [InlineData(false, true, true, false, false, true, ReminderSuppressionReason.CaptureOrFinalizationActive)]
    [InlineData(false, false, false, false, false, true, ReminderSuppressionReason.OutsideActiveHours)]
    [InlineData(false, false, true, true, false, true, ReminderSuppressionReason.PresentationMode)]
    [InlineData(false, false, true, false, true, true, ReminderSuppressionReason.WindowsNotificationsSuppressed)]
    [InlineData(false, false, true, false, false, false, ReminderSuppressionReason.Disabled)]
    public void SuppressedDueReminderIsDiscardedWithoutCreatingCatchUpWork(
        bool isLocked,
        bool isCaptureActive,
        bool withinActiveHours,
        bool isPresentationMode,
        bool windowsSuppressed,
        bool enabled,
        ReminderSuppressionReason expectedReason)
    {
        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var state = policy.Schedule(
            new WakeReminderState(), "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);

        var result = WakeReminderPolicy.Evaluate(
            state,
            state.DueAtUtc!.Value,
            Day,
            new ReminderEligibility
            {
                Enabled = enabled,
                IsLocked = isLocked,
                IsCaptureOrFinalizationActive = isCaptureActive,
                IsWithinActiveHours = withinActiveHours,
                IsPresentationMode = isPresentationMode,
                AreWindowsNotificationsSuppressed = windowsSuppressed
            });

        Assert.Equal(ReminderDisposition.Suppressed, result.Disposition);
        Assert.Equal(expectedReason, result.SuppressionReason);
        Assert.Null(result.State.PendingEventId);
        Assert.Equal(0, result.State.InvitationsShownOnLocalDate);
    }

    [Fact]
    public void NoDailyCapButCooldownStillAppliesAfterNotNow()
    {
        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var state = new WakeReminderState();
        var time = EventTime;

        // Five opens in a row, each shown, none dismissed: no cap.
        for (var i = 1; i <= 5; i++)
        {
            state = policy.Schedule(
                state, $"event-{i}", ReminderTriggerKind.WakeOrUnlock, time, time, Day, enabled: true);
            Assert.Equal($"event-{i}", state.PendingEventId);
            var shown = WakeReminderPolicy.Evaluate(state, state.DueAtUtc!.Value, Day, EligibleReminder());
            Assert.Equal(ReminderDisposition.Present, shown.Disposition);
            Assert.Equal(i, shown.State.InvitationsShownOnLocalDate);
            state = shown.State;
            time = time.AddMinutes(10);
        }

        // "Not now" starts the two-hour quiet period.
        var dismissedAt = time;
        state = WakeReminderPolicy.Dismiss(state, dismissedAt);
        var duringCooldown = policy.Schedule(
            state, "event-6", ReminderTriggerKind.WakeOrUnlock,
            dismissedAt.AddMinutes(30), dismissedAt.AddMinutes(30), Day, enabled: true);
        Assert.Null(duringCooldown.PendingEventId);

        var after = dismissedAt.Add(WakeReminderPolicy.DismissalCooldown).AddSeconds(1);
        var afterCooldown = policy.Schedule(
            duringCooldown, "event-7", ReminderTriggerKind.WakeOrUnlock, after, after, Day, enabled: true);
        Assert.Equal("event-7", afterCooldown.PendingEventId);

        // The count is informational and resets on a new local date.
        var nextDay = Day.AddDays(1);
        var nextDaySchedule = policy.Schedule(
            afterCooldown, "event-8", ReminderTriggerKind.WakeOrUnlock, after, after, nextDay, enabled: true);
        Assert.Equal(0, nextDaySchedule.InvitationsShownOnLocalDate);
        Assert.Equal(nextDay, nextDaySchedule.InvitationLocalDate);
    }

    [Fact]
    public void NoPendingEvaluationDoesNotMoveInvitationDateBackwards()
    {
        var highWaterDate = Day.AddDays(1);
        var state = new WakeReminderState
        {
            InvitationLocalDate = highWaterDate,
            InvitationsShownOnLocalDate = 1,
            ProcessedEventIds = ["shown-event"],
            LastEventId = "shown-event"
        };

        var result = WakeReminderPolicy.Evaluate(
            state,
            EventTime.AddDays(1),
            Day,
            EligibleReminder());

        Assert.Equal(ReminderDisposition.NotDue, result.Disposition);
        Assert.Equal(highWaterDate, result.State.InvitationLocalDate);
        Assert.Equal(1, result.State.InvitationsShownOnLocalDate);
        Assert.Single(result.State.ProcessedEventIds);
        Assert.Equal("shown-event", result.State.ProcessedEventIds[0]);
    }

    [Fact]
    public void LocalDateRollbackCancelsPendingWithoutResettingTheInvitationCount()
    {
        var samples = 0;
        var policy = new WakeReminderPolicy((minimum, _) =>
        {
            samples++;
            return minimum;
        });
        var highWaterDate = Day.AddDays(1);
        var now = EventTime.AddDays(1);
        var pending = new WakeReminderState
        {
            InvitationLocalDate = highWaterDate,
            InvitationsShownOnLocalDate = 1,
            ProcessedEventIds = ["pending-event"],
            LastEventId = "pending-event",
            PendingEventId = "pending-event",
            PendingTrigger = ReminderTriggerKind.WakeOrUnlock,
            DueAtUtc = now.AddMinutes(1)
        };

        var duringRollback = policy.Schedule(
            pending,
            "rollback-event",
            ReminderTriggerKind.WakeOrUnlock,
            now,
            now,
            Day,
            enabled: true);

        Assert.Null(duringRollback.PendingEventId);
        Assert.Equal(highWaterDate, duringRollback.InvitationLocalDate);
        Assert.Equal(1, duringRollback.InvitationsShownOnLocalDate);
        Assert.Equal(0, samples);

        var afterClockCatchesUp = policy.Schedule(
            duringRollback,
            "later-event",
            ReminderTriggerKind.WakeOrUnlock,
            now,
            now,
            highWaterDate,
            enabled: true);

        Assert.Equal("later-event", afterClockCatchesUp.PendingEventId);
        Assert.Equal(1, samples);

        var shown = WakeReminderPolicy.Evaluate(
            afterClockCatchesUp,
            afterClockCatchesUp.DueAtUtc!.Value,
            highWaterDate,
            EligibleReminder());

        Assert.Equal(ReminderDisposition.Present, shown.Disposition);
        Assert.Equal(2, shown.State.InvitationsShownOnLocalDate);
    }

    [Fact]
    public void DueInvitationIsSuppressedAndConsumedWhenLocalDateMovesBackwards()
    {
        var highWaterDate = Day.AddDays(1);
        var now = EventTime.AddDays(1);
        var pending = new WakeReminderState
        {
            InvitationLocalDate = highWaterDate,
            InvitationsShownOnLocalDate = 1,
            ProcessedEventIds = ["pending-event"],
            LastEventId = "pending-event",
            PendingEventId = "pending-event",
            PendingTrigger = ReminderTriggerKind.WakeOrUnlock,
            DueAtUtc = now.AddSeconds(5)
        };

        var result = WakeReminderPolicy.Evaluate(
            pending,
            now.AddSeconds(5),
            Day,
            EligibleReminder());

        Assert.Equal(ReminderDisposition.Suppressed, result.Disposition);
        Assert.Equal(ReminderSuppressionReason.LocalDateMovedBackwards, result.SuppressionReason);
        Assert.Null(result.State.PendingEventId);
        Assert.Equal(highWaterDate, result.State.InvitationLocalDate);
        Assert.Equal(1, result.State.InvitationsShownOnLocalDate);
    }

    [Fact]
    public void ProcessStartupExpiresAnOverdueReminderInsteadOfShowingItAsCatchUp()
    {
        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var state = policy.Schedule(
            new WakeReminderState(), "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);

        var restored = WakeReminderPolicy.ExpireOverdueOnStartup(state, state.DueAtUtc!.Value.AddSeconds(1));

        Assert.Null(restored.PendingEventId);
        Assert.Null(restored.DueAtUtc);
        Assert.Equal("event-1", restored.LastEventId);
    }

    [Fact]
    public void NegativeInvitationCountIsRepairedAndDoesNotBlockReminders()
    {
        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var invalidState = new WakeReminderState
        {
            InvitationLocalDate = Day,
            InvitationsShownOnLocalDate = -1
        };

        var scheduled = policy.Schedule(
            invalidState, "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);

        Assert.Equal(0, scheduled.InvitationsShownOnLocalDate);
        Assert.Equal("event-1", scheduled.PendingEventId);
    }

    [Fact]
    public void CountAboveTheOldCapStillPresents()
    {
        var policy = new WakeReminderPolicy((minimum, _) => minimum);
        var state = new WakeReminderState
        {
            InvitationLocalDate = Day,
            InvitationsShownOnLocalDate = 7
        };

        var scheduled = policy.Schedule(
            state, "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);
        var shown = WakeReminderPolicy.Evaluate(
            scheduled, scheduled.DueAtUtc!.Value, Day, EligibleReminder());

        Assert.Equal(ReminderDisposition.Present, shown.Disposition);
        Assert.Equal(8, shown.State.InvitationsShownOnLocalDate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedProcessedEventIdsFailClosedForTheCurrentLocalDate(int corruptionKind)
    {
        string[]? processedEventIds = corruptionKind switch
        {
            0 => null,
            1 => [new string('x', 129)],
            2 => Enumerable.Range(0, WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay + 1)
                .Select(static index => $"event-{index}")
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(corruptionKind))
        };
        var samples = 0;
        var policy = new WakeReminderPolicy((minimum, _) =>
        {
            samples++;
            return minimum;
        });
        var malformedState = new WakeReminderState
        {
            InvitationLocalDate = Day,
            LastEventId = "pending-event",
            PendingEventId = "pending-event",
            PendingTrigger = ReminderTriggerKind.WakeOrUnlock,
            DueAtUtc = EventTime,
            ProcessedEventIds = processedEventIds!
        };

        var result = policy.Schedule(
            malformedState, "new-event", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);

        Assert.True(result.EventIdentityLimitReached);
        Assert.Empty(result.ProcessedEventIds);
        Assert.Null(result.PendingEventId);
        Assert.Null(result.PendingTrigger);
        Assert.Null(result.DueAtUtc);
        Assert.Null(result.LastEventId);
        Assert.Equal(0, samples);
    }

    [Fact]
    public void EventIdentityBoundFailsClosedAndResetsOnTheNextLocalDate()
    {
        var samples = 0;
        var policy = new WakeReminderPolicy((minimum, _) =>
        {
            samples++;
            return minimum;
        });
        var fullDay = new WakeReminderState
        {
            InvitationLocalDate = Day,
            ProcessedEventIds = Enumerable.Range(0, WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay)
                .Select(static index => $"event-{index}")
                .ToArray()
        };

        var saturated = policy.Schedule(
            fullDay, "overflow-event", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);

        Assert.True(saturated.EventIdentityLimitReached);
        Assert.Equal(WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay, saturated.ProcessedEventIds.Length);
        Assert.Null(saturated.PendingEventId);
        Assert.Equal(0, samples);

        var nextDay = Day.AddDays(1);
        var reset = policy.Schedule(
            saturated, "next-day-event", ReminderTriggerKind.WakeOrUnlock,
            EventTime.AddDays(1), EventTime.AddDays(1), nextDay, enabled: true);

        Assert.False(reset.EventIdentityLimitReached);
        Assert.Equal(["next-day-event"], reset.ProcessedEventIds);
        Assert.Equal("next-day-event", reset.PendingEventId);
        Assert.Equal(1, samples);
    }

    [Fact]
    public void ScheduledDueTimeAndDismissalCooldownSurviveJsonStatePersistence()
    {
        var samples = 0;
        var policy = new WakeReminderPolicy((minimum, _) =>
        {
            samples++;
            return minimum;
        });
        var scheduled = policy.Schedule(
            new WakeReminderState(), "event-1", ReminderTriggerKind.WakeOrUnlock,
            EventTime, EventTime, Day, enabled: true);
        var restoredSchedule = JsonSerializer.Deserialize<WakeReminderState>(
            JsonSerializer.Serialize(scheduled));

        Assert.NotNull(restoredSchedule);
        Assert.Equal(scheduled.DueAtUtc, restoredSchedule!.DueAtUtc);
        Assert.Equal(scheduled.ProcessedEventIds, restoredSchedule.ProcessedEventIds);
        var shown = WakeReminderPolicy.Evaluate(
            restoredSchedule, restoredSchedule.DueAtUtc!.Value, Day, EligibleReminder());
        Assert.Equal(Day, shown.State.InvitationLocalDate);
        var dismissedAt = restoredSchedule.DueAtUtc.Value;

        var dismissed = WakeReminderPolicy.Dismiss(shown.State, dismissedAt);
        var restoredDismissal = JsonSerializer.Deserialize<WakeReminderState>(
            JsonSerializer.Serialize(dismissed));
        Assert.NotNull(restoredDismissal);

        var duringCooldown = policy.Schedule(
            restoredDismissal!, "event-2", ReminderTriggerKind.WakeOrUnlock,
            dismissedAt.AddMinutes(30), dismissedAt.AddMinutes(30), Day, enabled: true);

        Assert.Null(duringCooldown.PendingEventId);
        Assert.Equal(dismissedAt.Add(WakeReminderPolicy.DismissalCooldown), duringCooldown.CooldownUntilUtc);
        Assert.Equal(1, samples);
    }
}
