using System.Diagnostics;
using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Log.Engine;
using Log.Windows;

namespace Log.Shell;

/// <summary>
/// Connects the shell model to the engine. No capture, store or render work lives here: it
/// asks the engine, then records what the engine actually did in the model. Model changes
/// happen on the UI thread only (engine events are posted there).
/// </summary>
internal sealed class ShellController : IAsyncDisposable
{
    const long StorageReserveBytes = 2L * 1024 * 1024 * 1024;
    const long ExportReserveBytes = 512L * 1024 * 1024;
    const long PngFrameOverheadBytes = 64L * 1024;
    const long ExportBitrate = 8_000_000;
    const long ExportOverheadBytes = 1024 * 1024;
    static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(1);

    readonly IAppEngine engine;
    readonly IShellConsent broker;
    readonly WindowsCaptureSafetyMonitor safety;
    readonly Action changed;
    readonly Action showWindow;
    readonly Action hideWindow;
    readonly Func<string, bool, bool> updateTray;
    readonly Func<bool> verifyWindowVisible;
    readonly Func<string?> pickFolder;
    readonly Func<string, RenderSettings, CancellationToken, Task<long>> estimateOutputBytes;
    readonly string root;
    readonly ActiveClock clock = new(() => Stopwatch.GetElapsedTime(0));
    readonly CancellationTokenSource shutdown = new();
    readonly object opGate = new();
    CaptureRequest? lastRequest;
    string? sessionId;
    EngineExportPreview? preview;
    Task? activeOp;
    bool locked;
    string? safetyFailure;
    int disposed;

    public ShellModel Model { get; } = new();

    public ShellController(
        Func<IConsentBroker, IAppEngine> createEngine,
        IShellConsent broker,
        string root,
        Action changed,
        Action showWindow,
        Action hideWindow,
        Func<string, bool, bool> updateTray,
        Func<bool> verifyWindowVisible,
        Func<string?> pickFolder,
        Func<string, RenderSettings, CancellationToken, Task<long>>? estimateOutputBytes = null)
    {
        this.estimateOutputBytes = estimateOutputBytes ?? EstimateFromSession;
        this.broker = broker;
        this.root = root;
        this.changed = changed;
        this.showWindow = showWindow;
        this.hideWindow = hideWindow;
        this.updateTray = updateTray;
        this.verifyWindowVisible = verifyWindowVisible;
        this.pickFolder = pickFolder;
        safety = new WindowsCaptureSafetyMonitor(reason => _ = PauseForSystemAsync(reason));
        engine = createEngine(broker);
        engine.StateChanged += s => Ui.Post(() => OnEngineState(s));
        engine.CaptureFailed += e => Ui.Post(() => OnCaptureFailed(e));
    }

    public EngineStateSnapshot EngineState => engine.State;

    // --- Windows session notifications (lock, sleep) ---
    public void RegisterSessionNotifications(nint hwnd)
    {
        try { safety.RegisterSessionNotifications(hwnd); }
        catch (Exception e) { safetyFailure = e.Message; AppLog.Write("session notifications", e); }
    }

    /// <summary>Feeds Windows session/power messages to the safety monitor. Returns the reminder trigger, if any.</summary>
    public bool HandleSessionMessage(uint msg, nint wParam, out Log.Core.Shell.ReminderTriggerKind? trigger)
    {
        var handled = safety.TryHandleWindowMessage(msg, (nuint)wParam, out trigger);
        if (msg == WindowsCaptureSafetyMonitor.WmWtsSessionChange)
        {
            var code = unchecked((uint)wParam);
            if (code is WindowsCaptureSafetyMonitor.WtsSessionLock or WindowsCaptureSafetyMonitor.WtsSessionLogoff) locked = true;
            else if (code is WindowsCaptureSafetyMonitor.WtsSessionUnlock or WindowsCaptureSafetyMonitor.WtsSessionLogon) locked = false;
        }

        return handled;
    }

    /// <summary>True while a capture, finish or save is in flight (the wake invitation must not appear).</summary>
    public bool IsBusy => engine.State.Activity != EngineActivity.Idle || Model.IsCapturing || Model.Busy
        || Model.Screen is ScreenId.Finishing or ScreenId.Preview or ScreenId.Saved;

    public bool IsLocked => locked;

    void EnsureSafetyReady()
    {
        if (safetyFailure is not null)
            throw new InvalidOperationException("Windows session notifications could not be registered, so capture cannot start safely.");
        if (locked) throw new InvalidOperationException("Windows is locked. Unlock it and try again.");
    }

    // --- Screen commands (UI thread) ---
    public void LetsGo() { if (Model.LetsGo()) changed(); }

    /// <summary>Back / Not now. Returns true when the screen changed.</summary>
    public bool Dismiss()
    {
        if (!Model.Dismiss()) return false;
        changed();
        return true;
    }

    public void ChooseSource()
    {
        if (!Model.BeginConsent()) return;
        changed();
        Run(ChooseAndStartAsync);
    }

    public void TimerClicked() { if (Model.TimerClicked()) changed(); }

    /// <summary>Keep going / Escape / closing the window from the goodbye screen. Capture is never touched.</summary>
    public void KeepGoing(Log.Core.Shell.GoodbyeDecisionAction how = Log.Core.Shell.GoodbyeDecisionAction.KeepGoing)
    {
        if (Model.KeepGoing(how)) changed();
    }

    /// <summary>Tray "Finish recording": show the goodbye decision; finishing still needs confirmation.</summary>
    public void FinishFromTray()
    {
        if (Model.Screen == ScreenId.Recording) Model.TimerClicked();
        showWindow();
        changed();
    }

    public void FinishConfirmed()
    {
        if (!Model.FinishConfirmed()) return;
        changed();
        Run(StopAndPreviewAsync);
    }

    public void TryAgain()
    {
        if (Model.Screen != ScreenId.SourceLost) return;
        Run(ResumeAsync);
    }

    public void Save()
    {
        if (!Model.SaveStarted()) return;
        changed();
        Run(() => SaveAsync(null));
    }

    public void ChooseFolder()
    {
        if (Model.Screen != ScreenId.SaveError || Model.Busy) return;
        var folder = pickFolder();
        if (folder is null || !Model.SaveStarted()) return;
        changed();
        Run(() => SaveAsync(folder));
    }

    public void KeepForLater()
    {
        if (Model.KeepForLater()) hideWindow();
    }

    public void OpenFile()
    {
        // On the saved screen this opens the saved file; on the preview it is the no-player fallback.
        var path = Model.Screen == ScreenId.Preview ? Model.PreviewPath : Model.SavedPath;
        if (path is not null) Native.OpenWithDefault(path);
    }

    public void Done()
    {
        if (!Model.Done()) return;
        preview = null;
        sessionId = null;
        changed();
        Run(async () => await broker.ReleaseAsync());
        CheckUnfinished(show: true);
    }

    // --- Recordings left on disk by an earlier run ---
    int scanning;

    /// <summary>
    /// Looks for a recording that was never saved (kept for later, or cut short by a crash or power loss)
    /// and puts it on screen. Does nothing while anything is recording, being chosen or being saved.
    /// </summary>
    public void CheckUnfinished(bool show)
    {
        if (Interlocked.Exchange(ref scanning, 1) != 0) return;
        Run(async () =>
        {
            try { await FindUnfinishedAsync(show); }
            finally { Volatile.Write(ref scanning, 0); }
        });
    }

    async Task FindUnfinishedAsync(bool show)
    {
        var ct = shutdown.Token;
        // The engine also refuses to scan during a capture; this just avoids a pointless error.
        if (engine.State.Activity != EngineActivity.Idle || !await Ui.Invoke(() => Model.CanOfferUnfinished)) return;

        IReadOnlyList<EngineUnfinishedSave> found;
        try { found = await engine.ListUnfinishedAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("unfinished scan", e);
            return;
        }

        if (found.Count == 0) return;
        var item = found[0];
        var kept = item.PreviewPath is { } kp && item.JobId is not null && item.SuggestedOutputPath is not null && File.Exists(kp);
        var notice = kept ? "Kept from earlier." : item.WasInterrupted ? "log closed before this finished. This is what was captured." : "Not saved yet.";
        var accepted = await Ui.Invoke(() => Model.UnfinishedFound(kept ? item.PreviewPath : null, item.Elapsed, notice));
        if (!accepted) return; // the user started something else while the scan ran

        sessionId = item.SessionId;
        if (kept)
        {
            preview = new EngineExportPreview(item.SessionId, item.JobId!, item.PreviewPath, item.SuggestedOutputPath!, Completed: true);
            Ui.Post(() => { if (show) showWindow(); changed(); });
            return;
        }

        Ui.Post(() => { if (show) showWindow(); changed(); });
        try
        {
            var name = item.Name.Length > 0 ? item.Name : SessionNamePolicy.CreateAutomaticName(DateTimeOffset.Now);
            await RenderPreviewAsync(item.SessionId, item.FrameWidth, item.FrameHeight, name, item.Elapsed, show, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("recovered preview", e);
            Post(() => Model.PreviewFailed("Couldn't prepare the preview. Your recording is still on this device."));
        }
    }

    public void Discard()
    {
        if (Model.DiscardAsked()) changed();
    }

    /// <summary>Keep it / Escape / closing the window on the delete question.</summary>
    public void KeepIt()
    {
        if (Model.KeepIt()) changed();
    }

    public void DiscardConfirmed()
    {
        if (!Model.DiscardStarted()) return;
        changed();
        Run(DiscardAsync);
    }

    async Task DiscardAsync()
    {
        var id = sessionId;
        try
        {
            if (id is null) throw new InvalidOperationException("There is no recording to delete.");
            await engine.DiscardUnfinishedAsync(id, shutdown.Token);
            preview = null;
            sessionId = null;
            Post(() => { if (Model.Discarded()) hideWindow(); });
            CheckUnfinished(show: true); // another one may be waiting
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("discard", e);
            Post(() => Model.DiscardFailed("Couldn't delete it. Your recording is still on this device."));
        }
    }

    /// <summary>Elapsed active time; the tick timer reads this.</summary>
    public TimeSpan Elapsed => clock.Elapsed;

    // --- Operations (one at a time) ---
    void Run(Func<Task> work)
    {
        lock (opGate)
        {
            var previous = activeOp ?? Task.CompletedTask;
            activeOp = Chain(previous, work);
        }
    }

    async Task Chain(Task previous, Func<Task> work)
    {
        try { await previous; }
        catch { /* reported by its own operation */ }
        if (Volatile.Read(ref disposed) != 0) return;
        try { await work(); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception e) { AppLog.Write("operation", e); }
    }

    void Post(Action a) => Ui.Post(() => { if (Volatile.Read(ref disposed) == 0) { a(); changed(); } });

    async Task ChooseAndStartAsync()
    {
        var ct = shutdown.Token;
        try
        {
            await broker.ReleaseAsync(ct);
            EnsureSafetyReady();
            // The picker needs the UI thread's apartment and owner window.
            var picked = await Ui.InvokeAsync(() => broker.SelectDisplayAsync(ct));
            if (!picked) { Post(() => Model.ConsentCancelled()); return; }

            var size = broker.SelectedFrameSize;
            var source = broker.SelectedDisplayName ?? "Entire display";
            var request = new CaptureRequest(
                maxDuration: TimeSpan.Zero,
                interval: SamplingInterval,
                maxFrameAge: TimeSpan.FromSeconds(10),
                outputWidth: size?.Width ?? 1920,
                outputHeight: size?.Height ?? 1080,
                outputFramesPerSecond: RenderPreset.OutputFramesPerSecond,
                sessionName: SessionNamePolicy.CreateAutomaticName(DateTimeOffset.Now),
                estimatedBytesPerHour: EstimateCaptureBytesPerHour(size, SamplingInterval.Ticks),
                reserveBytes: StorageReserveBytes,
                captureCursor: true,
                samplingMode: CaptureSamplingMode.Adaptive);
            lastRequest = request;
            await engine.StartAsync(request, ct);

            // Only the engine's own state counts as "capture started".
            var state = engine.State;
            if (state.Activity != EngineActivity.Recording)
                throw new InvalidOperationException("The engine did not start recording.");
            sessionId = state.SessionId;
            Post(() => { clock.Reset(); clock.Running(); Model.EngineStarted(source); });
        }
        catch (OperationCanceledException)
        {
            await ReleaseQuietly();
            Post(() => Model.ConsentCancelled());
        }
        catch (Exception e)
        {
            AppLog.Write("start capture", e);
            await ReleaseQuietly();
            var message = e is InsufficientStorageException ? "There isn't enough free space to start recording." : e.Message;
            Post(() => Model.ConsentFailed("Couldn't start recording. " + message));
        }
    }

    async Task ReleaseQuietly()
    {
        try { await broker.ReleaseAsync(); }
        catch { /* the source picker stays reachable */ }
    }

    async Task StopAndPreviewAsync()
    {
        var ct = shutdown.Token;
        var elapsed = clock.Elapsed;
        try
        {
            if (engine.State.Activity is EngineActivity.Recording or EngineActivity.Paused)
            {
                var result = await engine.StopAsync(ct);
                sessionId = result.SessionId;
            }

            Post(() => { clock.Stopped(); Model.CaptureStopped(); });
            await RenderPreviewAsync(elapsed, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("finish", e);
            Post(() => Model.PreviewFailed("Couldn't prepare the preview. Your recording is still on this device."));
        }
    }

    Task RenderPreviewAsync(TimeSpan elapsed, CancellationToken ct)
    {
        var id = sessionId ?? throw new InvalidOperationException("No recorded session is available.");
        var request = lastRequest ?? throw new InvalidOperationException("No capture request is available.");
        var name = request.SessionName ?? SessionNamePolicy.CreateAutomaticName(DateTimeOffset.Now);
        return RenderPreviewAsync(id, request.OutputWidth, request.OutputHeight, name, elapsed, showWhenReady: true, ct);
    }

    async Task RenderPreviewAsync(string id, int width, int height, string name, TimeSpan elapsed, bool showWhenReady, CancellationToken ct)
    {
        var settings = RenderPreset.CreateSettings("screen", new PixelSize(width, height));
        var estimate = await estimateOutputBytes(id, settings, ct);
        var export = new EngineExportRequest(
            settings,
            ExportPathPolicy.GetDefaultExportDirectory(),
            FileName: ExportPathPolicy.CreateFileNameFromInput(name),
            EstimatedOutputBytes: estimate,
            ReserveBytes: ExportReserveBytes);
        var rendered = await engine.RenderPreviewAsync(id, export, ct);
        if (!rendered.Completed || rendered.PreviewPath is not { } path || !File.Exists(path))
            throw new InvalidOperationException("The preview render did not complete.");

        preview = rendered;
        Ui.Post(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Model.PreviewReady(path, elapsed);
            if (showWhenReady) showWindow();
            changed();
        });
    }

    // Output size from the stored frames, so the engine can check free space before rendering.
    async Task<long> EstimateFromSession(string id, RenderSettings settings, CancellationToken ct)
    {
        await using var library = await SessionLibrary.OpenAsync(root, ct);
        return EstimateEncodedOutputBytes(await library.GetSessionRenderInputAsync(id, ct), settings);
    }

    async Task SaveAsync(string? folder)
    {
        var current = preview;
        if (current is null) { Post(() => Model.SaveFailed("There is no preview to save.")); return; }
        try
        {
            var dir = folder ?? Path.GetDirectoryName(current.SuggestedOutputPath)
                ?? throw new InvalidDataException("The preview has no output folder.");
            var result = await engine.SavePreviewAsync(current.JobId, dir, Path.GetFileName(current.SuggestedOutputPath), shutdown.Token);
            // "Saved" only after the file is really there.
            var verified = result.Completed && File.Exists(result.OutputPath) && new FileInfo(result.OutputPath).Length > 0;
            if (verified)
            {
                var path = result.OutputPath;
                Post(() => Model.SaveSucceeded(path, fileVerified: true));
            }
            else
            {
                Post(() => Model.SaveFailed("The saved file could not be verified."));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("save", e);
            Post(() => Model.SaveFailed(e.Message));
        }
    }

    async Task ResumeAsync()
    {
        var ct = shutdown.Token;
        try
        {
            EnsureSafetyReady();
            // Indicators must be verifiable before capture resumes.
            if (!await Ui.Invoke(verifyWindowVisible) || !await Ui.Invoke(() => updateTray("paused", true)))
                throw new InvalidOperationException("The log window or tray indicator could not be verified.");
            if (!await engine.ResumeAsync(ct))
                throw new InvalidOperationException("Capture could not resume yet.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Post(() => Model.ResumeFailed(e.Message));
        }
    }

    async Task PauseForSystemAsync(CapturePauseReason reason)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { await engine.PauseAsync(reason, shutdown.Token); }
        catch (Exception e) { AppLog.Write("automatic pause", e); }
    }

    // --- Engine events (already on the UI thread) ---
    void OnEngineState(EngineStateSnapshot s)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (s.Activity == EngineActivity.Recording) clock.Running(); else clock.Stopped();
        var tray = s.Activity switch
        {
            EngineActivity.Recording => "recording",
            EngineActivity.Paused => "paused",
            EngineActivity.Stopping or EngineActivity.Exporting => "saving",
            _ => "ready",
        };
        updateTray(tray, s.Activity is EngineActivity.Recording or EngineActivity.Paused);

        if (s.Activity == EngineActivity.Paused && s.PauseReason is { } reason)
            Model.SourceLost(PauseText(reason), PauseDetail(reason));
        else if (s.Activity == EngineActivity.Recording)
            Model.Resumed();
        changed();
    }

    void OnCaptureFailed(Exception e)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        AppLog.Write("capture failed", e);
        if (!Model.CaptureFailed("Recording stopped unexpectedly. Preparing what was captured.")) return;
        clock.Stopped();
        changed();
        var elapsed = clock.Elapsed;
        Run(async () =>
        {
            try
            {
                Post(() => Model.CaptureStopped());
                await RenderPreviewAsync(elapsed, shutdown.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Post(() => Model.PreviewFailed("Couldn't prepare the preview. Your recording is still on this device."));
            }
        });
    }

    static string PauseText(CapturePauseReason r) => r switch
    {
        CapturePauseReason.SourceUnavailable => "source unavailable",
        CapturePauseReason.FrameStale => "screen frames stopped",
        CapturePauseReason.WorkstationLocked => "Windows is locked",
        CapturePauseReason.SecureDesktop => "secure screen",
        CapturePauseReason.SystemSuspended => "PC was asleep",
        CapturePauseReason.TrayUnavailable => "indicator unavailable",
        _ => "paused",
    };

    static string PauseDetail(CapturePauseReason r) => r switch
    {
        CapturePauseReason.SourceUnavailable => "Restore the source, then choose Try again.",
        CapturePauseReason.FrameStale => "Check the display, then choose Try again.",
        CapturePauseReason.WorkstationLocked => "Unlock Windows, then choose Try again.",
        CapturePauseReason.SecureDesktop => "Return to your desktop, then choose Try again.",
        CapturePauseReason.SystemSuspended => "Choose Try again to continue.",
        CapturePauseReason.TrayUnavailable => "The tray indicator could not be verified. Restore it, then choose Try again.",
        _ => "",
    };

    // --- Storage estimates (as in the WinUI shell) ---
    static long EstimateCaptureBytesPerHour((int Width, int Height)? size, long intervalTicks)
    {
        var samples = (TimeSpan.TicksPerHour + intervalTicks - 1) / intervalTicks;
        var (w, h) = size ?? (1920, 1080);
        // PNG stores BGRA with one filter byte per row; estimate the uncompressed payload plus overhead.
        return ((long)w * h * 4 + h + PngFrameOverheadBytes) * samples;
    }

    static long EstimateEncodedOutputBytes(SessionRenderInput input, RenderSettings settings)
    {
        var frames = Math.Max(1, RenderTimelinePlanner.Plan(input, settings).Count);
        var bytes = ((long)frames * ExportBitrate * 2 + 8L * settings.Composition.OutputFramesPerSecond - 1)
                    / (8L * settings.Composition.OutputFramesPerSecond);
        return bytes + ExportOverheadBytes;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await shutdown.CancelAsync();
        safety.Dispose();
        Task? op;
        lock (opGate) op = activeOp;
        if (op is not null)
        {
            try { await op; }
            catch { /* disposal owns the cancellation */ }
        }

        try { await engine.DisposeAsync(); }
        finally
        {
            shutdown.Dispose();
            await broker.DisposeAsync();
        }
    }
}
