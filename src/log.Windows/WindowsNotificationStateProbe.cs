using System.Runtime.InteropServices;

namespace Log.Windows;

/// <summary>
/// Current-user Shell notification states from QUERY_USER_NOTIFICATION_STATE.
/// </summary>
public enum WindowsUserNotificationState
{
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7
}

/// <summary>
/// Reads the current-user Shell notification state. Call immediately before a
/// non-critical custom invitation is shown; the result must not be cached.
/// </summary>
public static class WindowsNotificationStateProbe
{
    private const int HResultNotImplemented = unchecked((int)0x80004001);

    public static WindowsNotificationStateResult Query()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsNotificationStateResult(
                HResultNotImplemented,
                StateValue: 0);
        }

        try
        {
            var hresult = SHQueryUserNotificationState(out var state);
            return new WindowsNotificationStateResult(hresult, (int)state);
        }
        catch (DllNotFoundException exception)
        {
            return FailedQuery(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            return FailedQuery(exception);
        }
        catch (BadImageFormatException exception)
        {
            return FailedQuery(exception);
        }
    }

    private static WindowsNotificationStateResult FailedQuery(Exception exception)
    {
        return new WindowsNotificationStateResult(
            Marshal.GetHRForException(exception),
            StateValue: 0);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(
        out WindowsUserNotificationState state);
}

/// <summary>
/// Raw Shell API result. Only S_OK paired with QUNS_ACCEPTS_NOTIFICATIONS
/// allows a non-critical invitation; errors and unknown values suppress it.
/// </summary>
public readonly record struct WindowsNotificationStateResult(
    int HResult,
    int StateValue)
{
    public bool AllowsNonCriticalNotifications =>
        HResult == 0
        && StateValue == (int)WindowsUserNotificationState.AcceptsNotifications;

    public bool IsSuppressed => !AllowsNonCriticalNotifications;
}