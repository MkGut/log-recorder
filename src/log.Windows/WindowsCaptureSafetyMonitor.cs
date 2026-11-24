using System.ComponentModel;
using System.Runtime.InteropServices;
using Log.Core.Capture;
using Log.Core.Shell;

namespace Log.Windows;

/// <summary>
/// Bridges shell window messages into the capture safety gate.
/// </summary>
/// <remarks>
/// Register this with the shell's top-level window and pass its WTS and power messages
/// from the window procedure. Unlock and resume messages are intentionally consumed
/// without resuming capture; the user must explicitly verify indicators and resume.
/// </remarks>
public sealed class WindowsCaptureSafetyMonitor : IDisposable
{
    public const uint WmPowerBroadcast = 0x0218;
    public const uint WmWtsSessionChange = 0x02B1;

    public const uint WtsConsoleConnect = 0x0001;
    public const uint WtsConsoleDisconnect = 0x0002;
    public const uint WtsRemoteConnect = 0x0003;
    public const uint WtsRemoteDisconnect = 0x0004;
    public const uint WtsSessionLogon = 0x0005;
    public const uint WtsSessionLogoff = 0x0006;
    public const uint WtsSessionLock = 0x0007;
    public const uint WtsSessionUnlock = 0x0008;
    public const uint WtsSessionRemoteControl = 0x0009;
    public const uint WtsSessionCreate = 0x000A;
    public const uint WtsSessionTerminate = 0x000B;

    public const uint PbtApmQuerySuspend = 0x0000;
    public const uint PbtApmQueryStandby = 0x0001;
    public const uint PbtApmSuspend = 0x0004;
    public const uint PbtApmStandby = 0x0005;
    public const uint PbtApmResumeCritical = 0x0006;
    public const uint PbtApmResumeSuspend = 0x0007;
    public const uint PbtApmResumeAutomatic = 0x0012;

    private const uint NotifyForThisSession = 0;

    private readonly object gate = new();
    private readonly CaptureSafetyGate? safetyGate;
    private readonly IClock? clock;
    private readonly Action<CapturePauseReason>? pauseRequested;
    private nint registeredWindowHandle;
    private int disposed;

    public WindowsCaptureSafetyMonitor(CaptureSafetyGate safetyGate, IClock clock)
    {
        this.safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Creates a shell-facing monitor. The callback is invoked only for a
    /// recognized transition that requires a pause; resume remains explicit.
    /// </summary>
    public WindowsCaptureSafetyMonitor(Action<CapturePauseReason> pauseRequested)
    {
        this.pauseRequested = pauseRequested ?? throw new ArgumentNullException(nameof(pauseRequested));
    }

    public void RegisterSessionNotifications(nint windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WTS session notifications require Windows.");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (registeredWindowHandle != 0)
            {
                throw new InvalidOperationException("WTS session notifications are already registered.");
            }

            if (!WTSRegisterSessionNotification(windowHandle, NotifyForThisSession))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not register capture safety session notifications.");
            }

            registeredWindowHandle = windowHandle;
        }
    }

    public bool TryHandleWindowMessage(uint message, nuint wParam)
    {
        return TryHandleWindowMessage(message, wParam, out _);
    }

    /// <summary>
    /// Handles a message and returns any associated reminder trigger before the message is consumed.
    /// </summary>
    /// <remarks>
    /// A reminder trigger is only an observation; callers must still check persisted opt-in and live eligibility.
    /// </remarks>
    public bool TryHandleWindowMessage(
        uint message,
        nuint wParam,
        out ReminderTriggerKind? reminderTrigger)
    {
        reminderTrigger = null;
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        if (TryGetReminderTrigger(message, wParam, out var trigger))
        {
            reminderTrigger = trigger;
        }

        if (!TryGetPauseReason(message, wParam, out var reason))
        {
            return false;
        }

        if (reason is CapturePauseReason pauseReason)
        {
            PauseIfActive(pauseReason);
        }

        // Returning zero for a suspend query vetoes the system power transition.
        // Pause capture, then let the host procedure return the platform's normal
        // allow-suspend result.
        return !IsPowerSuspendQuery(message, unchecked((uint)wParam));
    }

    /// <summary>Maps a native window message without mutating capture state.</summary>
    public static bool TryGetPauseReason(
        uint message,
        nuint wParam,
        out CapturePauseReason? reason)
    {
        reason = null;
        var eventCode = unchecked((uint)wParam);
        if (message == WmWtsSessionChange)
        {
            reason = eventCode switch
            {
                WtsSessionLock or WtsSessionLogoff => CapturePauseReason.WorkstationLocked,
                WtsConsoleConnect
                    or WtsConsoleDisconnect
                    or WtsRemoteConnect
                    or WtsRemoteDisconnect
                    or WtsSessionLogon
                    or WtsSessionRemoteControl
                    or WtsSessionCreate
                    or WtsSessionTerminate => CapturePauseReason.SecureDesktop,
                _ => null
            };

            return eventCode is WtsConsoleConnect
                or WtsConsoleDisconnect
                or WtsRemoteConnect
                or WtsRemoteDisconnect
                or WtsSessionLogon
                or WtsSessionLogoff
                or WtsSessionLock
                or WtsSessionUnlock
                or WtsSessionRemoteControl
                or WtsSessionCreate
                or WtsSessionTerminate;
        }

        if (message != WmPowerBroadcast)
        {
            return false;
        }

        var isRecognized = eventCode is PbtApmQuerySuspend
            or PbtApmQueryStandby
            or PbtApmSuspend
            or PbtApmStandby
            or PbtApmResumeCritical
            or PbtApmResumeSuspend
            or PbtApmResumeAutomatic;
        if (isRecognized)
        {
            reason = CapturePauseReason.SystemSuspended;
        }

        return isRecognized;
    }

    /// <summary>
    /// Maps only session events that provide evidence of user presence to reminder timing.
    /// </summary>
    /// <remarks>
    /// Automatic and critical resume messages can occur without user activity. This method
    /// does not infer lid-open state.
    /// </remarks>
    public static bool TryGetReminderTrigger(
        uint message,
        nuint wParam,
        out ReminderTriggerKind trigger)
    {
        trigger = default;
        var eventCode = unchecked((uint)wParam);
        if (message == WmWtsSessionChange)
        {
            if (eventCode == WtsSessionLogon)
            {
                trigger = ReminderTriggerKind.ColdSignIn;
                return true;
            }

            if (eventCode == WtsSessionUnlock)
            {
                trigger = ReminderTriggerKind.WakeOrUnlock;
                return true;
            }

            return false;
        }

        if (message == WmPowerBroadcast && eventCode == PbtApmResumeSuspend)
        {
            trigger = ReminderTriggerKind.WakeOrUnlock;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        nint windowHandle;
        lock (gate)
        {
            if (disposed != 0)
            {
                return;
            }

            disposed = 1;
            windowHandle = registeredWindowHandle;
            registeredWindowHandle = 0;
        }

        if (windowHandle == 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            WTSUnRegisterSessionNotification(windowHandle);
        }
        catch
        {
            // Teardown must not mask the capture result or shell shutdown.
        }
    }

    private void PauseIfActive(CapturePauseReason reason)
    {
        if (safetyGate is not null && clock is not null)
        {
            var snapshot = safetyGate.Snapshot();
            if (snapshot.State is CaptureSafetyState.AwaitingStart or CaptureSafetyState.Stopping)
            {
                return;
            }

            safetyGate.Pause(reason, clock.NowTicks);
            return;
        }

        pauseRequested?.Invoke(reason);
    }

    private static bool IsPowerSuspendQuery(uint message, uint eventCode)
    {
        return message == WmPowerBroadcast
            && eventCode is PbtApmQuerySuspend or PbtApmQueryStandby;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint windowHandle, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint windowHandle);
}
