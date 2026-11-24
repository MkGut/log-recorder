using Log.Core.Capture;
using Log.Engine;
using Log.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Log.Shell;

/// <summary>What the controller needs from the consent side; the real one opens the Windows picker.</summary>
internal interface IShellConsent : IConsentBroker, IAsyncDisposable
{
    string? SelectedDisplayName { get; }
    (int Width, int Height)? SelectedFrameSize { get; }

    /// <summary>Asks the user what to share. False means they cancelled.</summary>
    Task<bool> SelectDisplayAsync(CancellationToken ct);
}

/// <summary>
/// Shell side of the OS consent flow: the Windows capture picker, plus granting the frame
/// source only after the visible window and tray indicators are verified.
/// </summary>
internal sealed class ShellConsentBroker(
    nint ownerWindow,
    Func<CancellationToken, Task<bool>> showRecordingWindow,
    Func<CancellationToken, Task<bool>> applyRecordingTrayState) : IShellConsent
{
    const int ServiceUnavailable = unchecked((int)0x80070424);
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object state = new();
    GraphicsCaptureItem? item;
    IDirect3DDevice? device;
    IAsyncDisposable? activeSource;
    int epoch;

    public string? SelectedDisplayName { get; private set; }

    public bool CanStart => Selected;

    bool Selected
    {
        get { lock (state) return item is not null && device is not null; }
    }

    public (int Width, int Height)? SelectedFrameSize
    {
        get
        {
            lock (state)
            {
                var size = item?.Size;
                return size is { Width: > 0, Height: > 0 } s ? (s.Width, s.Height) : null;
            }
        }
    }

    /// <summary>Opens the Windows picker. Must run on the UI thread. False means the user cancelled.</summary>
    public async Task<bool> SelectDisplayAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ClearSelectionAsync();
            var caps = WindowsCaptureCapabilities.Inspect();
            if (!caps.ApiAvailable || !caps.IsSupported)
            {
                var hr = caps.FailureHResult is int f ? $" HRESULT 0x{f:X8}." : "";
                var service = caps.FailureHResult == ServiceUnavailable
                    ? " Windows reports that the per-user capture service is unavailable."
                    : "";
                throw new InvalidOperationException($"Windows screen capture is unavailable here.{hr}{service}");
            }

            var picked = await WindowsGraphicsCapturePicker.PickAsync(ownerWindow, ct);
            if (picked is null) return false;
            var dev = WindowsGraphicsCaptureDevice.CreateHardware();
            lock (state)
            {
                item = picked;
                device = dev;
                SelectedDisplayName = SafeName(picked);
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CaptureConsent> RequestCaptureConsentAsync(CaptureRequest request, CancellationToken ct = default)
    {
        GraphicsCaptureItem? i;
        IDirect3DDevice? d;
        lock (state) { i = item; d = device; }
        if (i is null || d is null) throw new InvalidOperationException("Choose a source before starting capture.");

        var source = new WindowsGraphicsCaptureFrameSource(
            i, d,
            sampleIntervalTicks: request.Interval.Ticks,
            sourceEpoch: Interlocked.Increment(ref epoch),
            cursorCaptureEnabled: request.CaptureCursor);
        lock (state) activeSource = source;

        var v = await CaptureConsentVerification.CreateAsync(source, showRecordingWindow, applyRecordingTrayState, ct);
        if (!v.IndicatorsVerified)
        {
            await source.DisposeAsync();
            lock (state) activeSource = null;
            throw new InvalidOperationException(
                (v.WindowVisible ? "The recording indicator in the tray could not be applied." : "The log window could not be shown.")
                + " No capture was started.");
        }

        return new CaptureConsent(source, IndicatorsVerified: true);
    }

    public async ValueTask ReleaseAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await ClearSelectionAsync(); }
        finally { gate.Release(); }
    }

    async Task ClearSelectionAsync()
    {
        IAsyncDisposable? source;
        IDirect3DDevice? d;
        lock (state)
        {
            source = activeSource; d = device;
            activeSource = null; item = null; device = null; SelectedDisplayName = null;
        }

        if (source is not null)
        {
            try { await source.DisposeAsync(); }
            catch { /* teardown must not mask the capture result */ }
        }

        (d as IDisposable)?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { await ReleaseAsync(); }
        catch { /* shutting down */ }
        gate.Dispose();
    }

    // The display name is best effort, control characters stripped and capped.
    static string? SafeName(GraphicsCaptureItem i)
    {
        try
        {
            var name = new string((i.DisplayName ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }
}
