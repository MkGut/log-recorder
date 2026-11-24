using Log.Core.Shell;
using Xunit;
using Xunit.Abstractions;

namespace Log.Core.Tests;

public sealed class WakeReminderStateStoreTests(ITestOutputHelper output)
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
    public async Task MissingStateReturnsAnEmptyStateForTheCurrentLocalDate()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));

            var result = await store.LoadAsync(LocalDate);

            Assert.False(result.WasCorrupt);
            Assert.Equal(LocalDate, result.State.InvitationLocalDate);
            Assert.Equal(0, result.State.InvitationsShownOnLocalDate);
            Assert.Null(result.State.PendingEventId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledDueTimeCooldownAndIdentityWindowSurviveSaveAndLoad()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var policy = new WakeReminderPolicy((minimum, _) => minimum);
            var scheduled = policy.Schedule(
                new WakeReminderState(),
                "wake-1",
                ReminderTriggerKind.WakeOrUnlock,
                EventTime,
                EventTime,
                LocalDate,
                enabled: true);
            var shown = WakeReminderPolicy.Evaluate(
                scheduled,
                scheduled.DueAtUtc!.Value,
                LocalDate,
                EligibleReminder());
            var dismissedAt = scheduled.DueAtUtc.Value;
            var dismissed = WakeReminderPolicy.Dismiss(shown.State, dismissedAt);
            var afterCooldown = dismissedAt.Add(WakeReminderPolicy.DismissalCooldown).AddSeconds(1);
            var pending = policy.Schedule(
                dismissed,
                "wake-2",
                ReminderTriggerKind.WakeOrUnlock,
                afterCooldown,
                afterCooldown,
                LocalDate,
                enabled: true);

            await store.SaveAsync(pending);
            var loaded = await store.LoadAsync(LocalDate);

            Assert.False(loaded.WasCorrupt);
            Assert.Equal(pending.PendingEventId, loaded.State.PendingEventId);
            Assert.Equal(pending.PendingTrigger, loaded.State.PendingTrigger);
            Assert.Equal(pending.DueAtUtc, loaded.State.DueAtUtc);
            Assert.Equal(pending.CooldownUntilUtc, loaded.State.CooldownUntilUtc);
            Assert.Equal(pending.InvitationLocalDate, loaded.State.InvitationLocalDate);
            Assert.Equal(pending.InvitationsShownOnLocalDate, loaded.State.InvitationsShownOnLocalDate);
            Assert.Equal(pending.ProcessedEventIds, loaded.State.ProcessedEventIds);
            Assert.Equal(pending.LastEventId, loaded.State.LastEventId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptJsonFailsClosedAndPreservesTheOriginalFile()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var original = "{ invalid json";
            await File.WriteAllTextAsync(path, original);
            using var store = new WakeReminderStateStore(path);

            var result = await store.LoadAsync(LocalDate);

            Assert.True(result.WasCorrupt);
            Assert.True(result.State.EventIdentityLimitReached);
            Assert.Null(result.State.PendingEventId);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(50)]
    public async Task InvitationCountAboveTheOldDailyCapLoadsAndAllowsMoreInvitations(int count)
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Count 2 is today's real file shape; larger counts come from the removed cap.
            await File.WriteAllTextAsync(path, $$$"""
                {"schemaVersion":1,"state":{"invitationLocalDate":"2026-11-06","invitationsShownOnLocalDate":{{{count}}},"processedEventIds":[],"eventIdentityLimitReached":false}}
                """);
            using var store = new WakeReminderStateStore(path);
            var policy = new WakeReminderPolicy((minimum, _) => minimum);

            var loaded = await store.LoadAsync(LocalDate);
            Assert.False(loaded.WasCorrupt);
            Assert.Equal(count, loaded.State.InvitationsShownOnLocalDate);

            var scheduled = await store.UpdateAsync(LocalDate, state => policy.Schedule(
                state, "wake-more", ReminderTriggerKind.WakeOrUnlock, EventTime, EventTime, LocalDate, enabled: true));
            Assert.False(scheduled.WasCorrupt);
            Assert.Equal("wake-more", scheduled.State.PendingEventId);

            var shown = await store.UpdateAsync(LocalDate, state => WakeReminderPolicy.Evaluate(
                state, scheduled.State.DueAtUtc!.Value, LocalDate, EligibleReminder()).State);
            Assert.False(shown.WasCorrupt);
            Assert.Equal(count + 1, shown.State.InvitationsShownOnLocalDate);
            Assert.False((await store.LoadAsync(LocalDate)).WasCorrupt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownSchemaVersionFailsClosed()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, """{"schemaVersion":2,"state":{}}""");
            using var store = new WakeReminderStateStore(path);

            var result = await store.LoadAsync(LocalDate);

            Assert.True(result.WasCorrupt);
            Assert.True(result.State.EventIdentityLimitReached);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedStateFailsClosedWithoutDeletingTheFile()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(WakeReminderStateStore.MaximumStateFileBytes + 1);
            }

            using var store = new WakeReminderStateStore(path);
            var result = await store.LoadAsync(LocalDate);

            Assert.True(result.WasCorrupt);
            Assert.True(result.State.EventIdentityLimitReached);
            Assert.Equal(WakeReminderStateStore.MaximumStateFileBytes + 1, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MaximumValidEventIdentitySetFitsTheDailyMetadataLimit()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            using var store = new WakeReminderStateStore(path);
            var eventIds = Enumerable.Range(0, WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay)
                .Select(static index => $"event-{index:D4}" + new string('x', 118))
                .ToArray();
            Assert.All(eventIds, static eventId => Assert.Equal(128, eventId.Length));

            await store.SaveAsync(new WakeReminderState
            {
                InvitationLocalDate = LocalDate,
                ProcessedEventIds = eventIds,
                LastEventId = eventIds[^1]
            });

            var serializedBytes = new FileInfo(path).Length;
            output.WriteLine($"Maximum valid daily reminder state: {serializedBytes} bytes.");
            Assert.True(
                serializedBytes < WakeReminderStateStore.MaximumStateFileBytes,
                $"Maximum valid daily reminder state serialized to {serializedBytes} bytes.");
            var loaded = await store.LoadAsync(LocalDate);

            Assert.False(loaded.WasCorrupt);
            Assert.Equal(WakeReminderPolicy.MaximumRememberedEventIdsPerLocalDay, loaded.State.ProcessedEventIds.Length);
            Assert.Equal(eventIds, loaded.State.ProcessedEventIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAtomicallyReplacesStateAndLeavesNoOwnedTemporaryFile()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var directory = Path.Combine(root, "settings");
            using var store = new WakeReminderStateStore(Path.Combine(directory, "wake-state.json"));
            var first = new WakeReminderState
            {
                InvitationLocalDate = LocalDate,
                ProcessedEventIds = ["event-1"],
                LastEventId = "event-1"
            };
            var second = first with
            {
                ProcessedEventIds = ["event-1", "event-2"],
                LastEventId = "event-2"
            };

            await store.SaveAsync(first);
            await store.SaveAsync(second);
            var loaded = await store.LoadAsync(LocalDate);

            Assert.False(loaded.WasCorrupt);
            Assert.Equal(second.ProcessedEventIds, loaded.State.ProcessedEventIds);
            Assert.Equal(second.LastEventId, loaded.State.LastEventId);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentUpdatesSerializeCompletePolicyTransitions()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var sampleCount = 0;
            var policy = new WakeReminderPolicy((minimum, _) =>
            {
                Interlocked.Increment(ref sampleCount);
                return minimum;
            });
            var eventIds = Enumerable.Range(0, 20).Select(static index => $"wake-{index}").ToArray();

            await Task.WhenAll(eventIds.Select(eventId => store.UpdateAsync(
                LocalDate,
                state => policy.Schedule(
                    state,
                    eventId,
                    ReminderTriggerKind.WakeOrUnlock,
                    EventTime,
                    EventTime,
                    LocalDate,
                    enabled: true))));

            var loaded = await store.LoadAsync(LocalDate);

            Assert.False(loaded.WasCorrupt);
            Assert.Equal(eventIds.Length, loaded.State.ProcessedEventIds.Length);
            Assert.Equal(eventIds.Order(StringComparer.Ordinal), loaded.State.ProcessedEventIds.Order(StringComparer.Ordinal));
            Assert.Contains(loaded.State.PendingEventId, eventIds);
            Assert.Equal(1, sampleCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSnapshotsMutableEventIdsBeforeWaitingForStoreGate()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            using var transitionEntered = new ManualResetEventSlim();
            using var releaseTransition = new ManualResetEventSlim();
            var transition = Task.Run(() => store.UpdateAsync(LocalDate, state =>
            {
                transitionEntered.Set();
                if (!releaseTransition.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The state-store test did not release its held transition.");
                }

                return state;
            }));

            try
            {
                Assert.True(transitionEntered.Wait(TimeSpan.FromSeconds(10)));
                var mutableEventIds = new[] { "event-before-queue" };
                var save = store.SaveAsync(new WakeReminderState
                {
                    InvitationLocalDate = LocalDate,
                    ProcessedEventIds = mutableEventIds,
                    LastEventId = "event-before-queue"
                });

                mutableEventIds[0] = "event-after-queue";
                releaseTransition.Set();
                await Task.WhenAll(transition, save);

                var loaded = await store.LoadAsync(LocalDate);
                Assert.False(loaded.WasCorrupt);
                Assert.Equal("event-before-queue", Assert.Single(loaded.State.ProcessedEventIds));
                Assert.Equal("event-before-queue", loaded.State.LastEventId);
            }
            finally
            {
                releaseTransition.Set();
                await transition;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateOnCorruptStateDoesNotRunTransitionOrReplaceSourceFile()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings", "wake-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string original = "{ invalid json";
            await File.WriteAllTextAsync(path, original);
            using var store = new WakeReminderStateStore(path);
            var transitionCalled = false;

            var result = await store.UpdateAsync(LocalDate, state =>
            {
                transitionCalled = true;
                return state with { InvitationsShownOnLocalDate = 0 };
            });

            Assert.True(result.WasCorrupt);
            Assert.False(transitionCalled);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidStateCannotReplaceAnExistingGoodFile()
    {
        var root = CreateTemporaryRoot();

        try
        {
            using var store = new WakeReminderStateStore(Path.Combine(root, "settings", "wake-state.json"));
            var valid = new WakeReminderState
            {
                InvitationLocalDate = LocalDate,
                ProcessedEventIds = ["event-1"],
                LastEventId = "event-1"
            };
            await store.SaveAsync(valid);

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
                valid with { ProcessedEventIds = null! }));

            var loaded = await store.LoadAsync(LocalDate);
            Assert.False(loaded.WasCorrupt);
            Assert.Equal(valid.ProcessedEventIds, loaded.State.ProcessedEventIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
