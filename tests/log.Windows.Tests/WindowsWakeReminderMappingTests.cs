using Log.Core.Shell;
using Xunit;

namespace Log.Windows.Tests;

public sealed class WindowsWakeReminderMappingTests
{
    [Fact]
    public void SessionUnlockMapsToWakeOrUnlock()
    {
        var recognized = WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmWtsSessionChange,
            WindowsCaptureSafetyMonitor.WtsSessionUnlock,
            out var trigger);

        Assert.True(recognized);
        Assert.Equal(ReminderTriggerKind.WakeOrUnlock, trigger);
    }

    [Fact]
    public void SessionLogonMapsToColdSignIn()
    {
        var recognized = WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmWtsSessionChange,
            WindowsCaptureSafetyMonitor.WtsSessionLogon,
            out var trigger);

        Assert.True(recognized);
        Assert.Equal(ReminderTriggerKind.ColdSignIn, trigger);
    }

    [Fact]
    public void UserActivityResumeMapsToWakeOrUnlock()
    {
        var recognized = WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmPowerBroadcast,
            WindowsCaptureSafetyMonitor.PbtApmResumeSuspend,
            out var trigger);

        Assert.True(recognized);
        Assert.Equal(ReminderTriggerKind.WakeOrUnlock, trigger);
    }

    [Fact]
    public void AutomaticAndCriticalResumeDoNotScheduleReminders()
    {
        Assert.False(WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmPowerBroadcast,
            WindowsCaptureSafetyMonitor.PbtApmResumeAutomatic,
            out _));
        Assert.False(WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmPowerBroadcast,
            WindowsCaptureSafetyMonitor.PbtApmResumeCritical,
            out _));
    }

    [Fact]
    public void LockAndUnrelatedMessagesDoNotScheduleReminders()
    {
        Assert.False(WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmWtsSessionChange,
            WindowsCaptureSafetyMonitor.WtsSessionLock,
            out _));
        Assert.False(WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            WindowsCaptureSafetyMonitor.WmPowerBroadcast,
            WindowsCaptureSafetyMonitor.PbtApmSuspend,
            out _));
        Assert.False(WindowsCaptureSafetyMonitor.TryGetReminderTrigger(
            0x0002,
            WindowsCaptureSafetyMonitor.WtsSessionUnlock,
            out _));
    }

    [Fact]
    public void MonitorSurfacesUnlockTriggerBeforeConsumingMessage()
    {
        var pauseRequests = 0;
        using var monitor = new WindowsCaptureSafetyMonitor(_ => pauseRequests++);

        var handled = monitor.TryHandleWindowMessage(
            WindowsCaptureSafetyMonitor.WmWtsSessionChange,
            WindowsCaptureSafetyMonitor.WtsSessionUnlock,
            out var reminderTrigger);

        Assert.True(handled);
        Assert.Equal<ReminderTriggerKind?>(ReminderTriggerKind.WakeOrUnlock, reminderTrigger);
        Assert.Equal(0, pauseRequests);
    }
    [Fact]
    public void ExistingHandlerStillConsumesUnlockWithoutPausing()
    {
        var pauseRequests = 0;
        using var monitor = new WindowsCaptureSafetyMonitor(_ => pauseRequests++);

        var handled = monitor.TryHandleWindowMessage(
            WindowsCaptureSafetyMonitor.WmWtsSessionChange,
            WindowsCaptureSafetyMonitor.WtsSessionUnlock);

        Assert.True(handled);
        Assert.Equal(0, pauseRequests);
    }

}
