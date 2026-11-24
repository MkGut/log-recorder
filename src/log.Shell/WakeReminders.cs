using System.Globalization;
using System.Security.Cryptography;
using Log.Core.Shell;
using Log.Core.Store;
using Log.Windows;

namespace Log.Shell;

/// <summary>
/// Schedules the wake invitation. Policy, durable state and eligibility rules come from Log.Core;
/// this class only owns the timer, the Windows notification-state check and "show it now".
/// There is no settings page in the lightweight shell, so reminders are on and not limited to hours.
/// </summary>
internal sealed class WakeReminders : IAsyncDisposable
{
    readonly WakeReminderStateStore store;
    readonly WakeReminderCoordinator coordinator;
    readonly Func<bool> isBusy;
    readonly Func<bool> isLocked;
    readonly Func<Task<bool>> showInvitation;
    readonly CancellationTokenSource shutdown = new();
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object timerGate = new();
    Timer? timer;
    int stateAvailable, evaluating, disposed;
    bool invitationPending;

    public WakeReminders(Func<bool> isBusy, Func<bool> isLocked, Func<Task<bool>> showInvitation)
    {
        this.isBusy = isBusy;
        this.isLocked = isLocked;
        this.showInvitation = showInvitation;
        store = new WakeReminderStateStore(Path.Combine(LogPaths.DefaultRootDirectory, "wake-reminder-state.json"));
        coordinator = new WakeReminderCoordinator(new WakeReminderPolicy(RandomNumberGenerator.GetInt32), store);
    }

    /// <summary>Recovers saved state (an overdue invitation is dropped, never shown late), then arms any pending one.</summary>
    public async Task StartAsync(bool launchedAtSignIn)
    {
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Now);
            var recovered = await coordinator.RecoverOnStartupAsync(date, DateTimeOffset.UtcNow, shutdown.Token);
            Volatile.Write(ref stateAvailable, recovered.WasCorrupt ? 0 : 1);
            if (recovered.WasCorrupt) { AppLog.Write("wake reminder state unreadable; reminders paused"); return; }
            if (launchedAtSignIn) await ObserveAsync(ReminderTriggerKind.ColdSignIn);
        }
        catch (Exception e) when (Volatile.Read(ref disposed) == 0)
        {
            Volatile.Write(ref stateAvailable, 0);
            AppLog.Write("wake reminders unavailable", e);
        }
    }

    /// <summary>A wake, unlock or sign-in happened: sample a due time and arm the timer.</summary>
    public void Observe(ReminderTriggerKind trigger) => _ = ObserveAsync(trigger);

    async Task ObserveAsync(ReminderTriggerKind trigger)
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref stateAvailable) == 0) return;
        try
        {
            await gate.WaitAsync(shutdown.Token);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var date = DateOnly.FromDateTime(DateTime.Now);
                var observed = await coordinator.ObserveAsync(
                    date, EventId(trigger, date, now), trigger, now, DateTimeOffset.UtcNow, enabled: true, shutdown.Token);
                if (observed.WasCorrupt) { Volatile.Write(ref stateAvailable, 0); StopTimer(); return; }
                if (observed.State.DueAtUtc is { } due) Arm(due); else StopTimer();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (Volatile.Read(ref disposed) == 0) { AppLog.Write("wake reminder schedule", e); }
    }

    // Wakes and unlocks within five minutes share one event, so they never stack.
    static string EventId(ReminderTriggerKind trigger, DateOnly date, DateTimeOffset at)
    {
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return trigger == ReminderTriggerKind.ColdSignIn
            ? $"signin-{day}"
            : $"wake-{day}-{(at.ToUnixTimeSeconds() / 300).ToString(CultureInfo.InvariantCulture)}";
    }

    void Arm(DateTimeOffset due)
    {
        var delay = due.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        lock (timerGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            timer?.Dispose();
            timer = new Timer(static s => _ = ((WakeReminders)s!).EvaluateAsync(), this, delay, Timeout.InfiniteTimeSpan);
        }
    }

    void StopTimer()
    {
        lock (timerGate) { timer?.Dispose(); timer = null; }
    }

    async Task EvaluateAsync()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref evaluating, 1) != 0) return;
        try
        {
            await gate.WaitAsync(shutdown.Token);
            try
            {
                var probe = WindowsNotificationStateProbe.Query();
                var eligibility = new ReminderEligibility
                {
                    Enabled = true,
                    IsLocked = isLocked(),
                    IsCaptureOrFinalizationActive = isBusy(),
                    IsWithinActiveHours = true,
                    IsPresentationMode = probe.StateValue is (int)WindowsUserNotificationState.RunningD3DFullScreen
                        or (int)WindowsUserNotificationState.PresentationMode,
                    AreWindowsNotificationsSuppressed = probe.IsSuppressed,
                };
                var date = DateOnly.FromDateTime(DateTime.Now);
                var evaluated = await coordinator.EvaluateAsync(date, DateTimeOffset.UtcNow, eligibility, shutdown.Token);
                if (evaluated.WasCorrupt) { Volatile.Write(ref stateAvailable, 0); return; }
                if (evaluated.Evaluation?.Disposition == ReminderDisposition.NotDue && evaluated.State.DueAtUtc is { } due)
                {
                    Arm(due);
                    return;
                }

                if (evaluated.Evaluation?.Disposition == ReminderDisposition.Present)
                {
                    // The policy has already consumed this invitation; a failed show is not retried as a catch-up.
                    invitationPending = await showInvitation();
                }
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (Volatile.Read(ref disposed) == 0) { AppLog.Write("wake reminder show", e); }
        finally { Volatile.Write(ref evaluating, 0); }
    }

    /// <summary>"Not now" or the close control on a reminder: start the two-hour cooldown.</summary>
    public void Dismissed()
    {
        if (!invitationPending) return;
        invitationPending = false;
        _ = DismissAsync();
    }

    /// <summary>The user accepted (or a recording began), so there is nothing to cool down.</summary>
    public void Accepted() => invitationPending = false;

    async Task DismissAsync()
    {
        try
        {
            await gate.WaitAsync(shutdown.Token);
            try { await coordinator.DismissAsync(DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.UtcNow, shutdown.Token); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (Volatile.Read(ref disposed) == 0) { AppLog.Write("wake reminder dismissal", e); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await shutdown.CancelAsync();
        StopTimer();
        store.Dispose();
        gate.Dispose();
        shutdown.Dispose();
    }
}
