using Log.Core.Shell;
using Xunit;

namespace Log.Core.Tests;

public sealed class WakeReminderCoordinatorTests
{
    private static readonly DateOnly LocalDate = new(2026, 11, 6);
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
    public async Task NotDueEvaluationDoesNotWriteTheStateFile()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            using var store = new WakeReminderStateStore(path);
            var coordinator = new WakeReminderCoordinator(
                new WakeReminderPolicy((minimum, _) => minimum),
                store);

            var result = await coordinator.EvaluateAsync(LocalDate, EventTime, EligibleReminder());

            Assert.False(result.WasCorrupt);
            Assert.Equal(ReminderDisposition.NotDue, result.Evaluation?.Disposition);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentDueEvaluationsConsumeOneInvitationAndPersistTheCountOnce()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var coordinator = new WakeReminderCoordinator(
                new WakeReminderPolicy((minimum, _) => minimum),
                store);
            var scheduled = await coordinator.ObserveAsync(
                LocalDate,
                "wake-1",
                ReminderTriggerKind.WakeOrUnlock,
                EventTime,
                EventTime,
                enabled: true);
            var dueAt = scheduled.State.DueAtUtc!.Value;

            var results = await Task.WhenAll(
                coordinator.EvaluateAsync(LocalDate, dueAt, EligibleReminder()),
                coordinator.EvaluateAsync(LocalDate, dueAt, EligibleReminder()));

            Assert.All(results, result => Assert.False(result.WasCorrupt));
            Assert.Single(results, result =>
                result.Evaluation?.Disposition == ReminderDisposition.Present);

            var persisted = await store.LoadAsync(LocalDate);
            Assert.False(persisted.WasCorrupt);
            Assert.Equal(1, persisted.State.InvitationsShownOnLocalDate);
            Assert.Null(persisted.State.PendingEventId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupRecoveryExpiresAnOverdueInvitationBeforeEvaluation()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var coordinator = new WakeReminderCoordinator(
                new WakeReminderPolicy((minimum, _) => minimum),
                store);
            var scheduled = await coordinator.ObserveAsync(
                LocalDate,
                "wake-1",
                ReminderTriggerKind.WakeOrUnlock,
                EventTime,
                EventTime,
                enabled: true);
            var dueAt = scheduled.State.DueAtUtc!.Value;

            var recovered = await coordinator.RecoverOnStartupAsync(LocalDate, dueAt.AddSeconds(1));
            var evaluation = await coordinator.EvaluateAsync(
                LocalDate,
                dueAt.AddSeconds(1),
                EligibleReminder());

            Assert.False(recovered.WasCorrupt);
            Assert.Null(recovered.State.PendingEventId);
            Assert.False(evaluation.WasCorrupt);
            Assert.Equal(ReminderDisposition.NotDue, evaluation.Evaluation?.Disposition);
            Assert.Null(evaluation.State.PendingEventId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisablingRemindersImmediatelyClearsThePendingInvitation()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var coordinator = new WakeReminderCoordinator(
                new WakeReminderPolicy((minimum, _) => minimum),
                store);
            var scheduled = await coordinator.ObserveAsync(
                LocalDate,
                "wake-1",
                ReminderTriggerKind.WakeOrUnlock,
                EventTime,
                EventTime,
                enabled: true);
            Assert.NotNull(scheduled.State.PendingEventId);

            var disabled = await coordinator.DisableAsync(LocalDate);

            Assert.False(disabled.WasCorrupt);
            Assert.Null(disabled.State.PendingEventId);
            Assert.Null(disabled.State.PendingTrigger);
            Assert.Null(disabled.State.DueAtUtc);
            Assert.Equal(0, disabled.State.InvitationsShownOnLocalDate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupRecoveryCancelsPendingWhenLocalDateMovesBackwards()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var coordinator = new WakeReminderCoordinator(
                new WakeReminderPolicy((minimum, _) => minimum),
                store);
            var nextDay = LocalDate.AddDays(1);
            var nextDayTime = EventTime.AddDays(1);
            var scheduled = await coordinator.ObserveAsync(
                nextDay,
                "wake-next-day",
                ReminderTriggerKind.WakeOrUnlock,
                nextDayTime,
                nextDayTime,
                enabled: true);
            Assert.NotNull(scheduled.State.PendingEventId);

            var recovered = await coordinator.RecoverOnStartupAsync(LocalDate, nextDayTime);

            Assert.False(recovered.WasCorrupt);
            Assert.Null(recovered.State.PendingEventId);
            Assert.Equal(nextDay, recovered.State.InvitationLocalDate);
            Assert.Equal(0, recovered.State.InvitationsShownOnLocalDate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptStateReturnsNoPresentableEvaluationAndPreservesItsBytes()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string original = "{ invalid json";
            await File.WriteAllTextAsync(path, original);
            using var store = new WakeReminderStateStore(path);
            var coordinator = new WakeReminderCoordinator(
                new WakeReminderPolicy((minimum, _) => minimum),
                store);

            var result = await coordinator.EvaluateAsync(LocalDate, EventTime, EligibleReminder());

            Assert.True(result.WasCorrupt);
            Assert.Null(result.Evaluation);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "log-reminder-coordinator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
