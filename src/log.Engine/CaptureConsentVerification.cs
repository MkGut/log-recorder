using Log.Core.Capture;

namespace Log.Engine;

public sealed record CaptureIndicatorVerification(bool WindowVisible, bool TrayApplied)
{
    public bool IndicatorsVerified => WindowVisible && TrayApplied;
}

/// <summary>Grants a capture source only after the visible window and tray indicators are applied.</summary>
public static class CaptureConsentVerification
{
    public static async Task<CaptureIndicatorVerification> CreateAsync(
        IFrameSource source,
        Func<CancellationToken, Task<bool>> showRecordingWindow,
        Func<CancellationToken, Task<bool>> applyRecordingTrayState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(showRecordingWindow);
        ArgumentNullException.ThrowIfNull(applyRecordingTrayState);

        var windowVisible = await showRecordingWindow(cancellationToken).ConfigureAwait(false);
        if (!windowVisible)
        {
            return new CaptureIndicatorVerification(WindowVisible: false, TrayApplied: false);
        }

        var trayApplied = await applyRecordingTrayState(cancellationToken).ConfigureAwait(false);
        return new CaptureIndicatorVerification(WindowVisible: true, TrayApplied: trayApplied);
    }
}
