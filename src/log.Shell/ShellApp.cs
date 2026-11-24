using System.Diagnostics;
using Log.Core.Capture;
using Log.Core.Shell;
using Log.Core.Store;
using Log.Engine;
using Log.Windows;
using static Log.Shell.Native;

namespace Log.Shell;

/// <summary>Capture-time encoder: PNG samples now, H.264 once at export.</summary>
internal sealed class SnapshotEncoderFactory : IEncoderFactory
{
    public Task<IEncoder> CreateAsync(EncoderContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IEncoder>(new SnapshotCaptureEncoder(context.Width, context.Height));
}

/// <summary>Keeps the engine seam in the shell while the platform renderer owns media work.</summary>
internal sealed class RendererAdapter(string root) : ISessionRenderer
{
    readonly MediaFoundationSessionRenderer renderer = new(root);

    public Task RenderAsync(Log.Core.Store.SessionExportContext context, CancellationToken cancellationToken = default) =>
        renderer.RenderAsync(context, cancellationToken);
}

/// <summary>Owns the main window, tray icon, controller and the message loop.</summary>
internal sealed class ShellApp(string[] args) : IDisposable
{
    const nint TickTimer = 1;
    SurfaceWindow window = null!;
    TrayIcon tray = null!;
    ShellController controller = null!;
    WakeReminders reminders = null!;
    SurfaceWindow? invite;
    SingleInstanceGuard? guard;
    ScreenId shownScreen;
    uint taskbarCreated;
    bool firstPaintLogged;

    ShellModel Model => controller.Model;

    public unsafe bool Start()
    {
        if (!SingleInstanceGuard.TryAcquire(LogPaths.SingleInstanceId, () => Ui.Post(OpenLog), out guard))
            return false; // the running instance was told to come forward

        window = new SurfaceWindow();
        Ui.Init(window.Handle);
        window.Hook = OnMessage;
        window.Command = OnCommand;
        window.Painted = OnPainted;
        tray = new TrayIcon(window.Handle);
        fixed (char* n = "TaskbarCreated") taskbarCreated = RegisterWindowMessageW(n);

        var root = LogPaths.DefaultRootDirectory;
        var broker = new ShellConsentBroker(
            window.Handle,
            showRecordingWindow: _ => Ui.Invoke(EnsureWindowShown),
            applyRecordingTrayState: _ => Ui.Invoke(() => tray.Update("recording", canFinish: false)));
        controller = new ShellController(
            createEngine: b => LogEngine.CreateDefault(new SnapshotEncoderFactory(), b, new RendererAdapter(root), root),
            broker, root,
            changed: () => Render(),
            showWindow: () => ShowMain(),
            hideWindow: HideToTray,
            updateTray: (state, canFinish) => tray.Update(state, canFinish),
            verifyWindowVisible: () => window.IsVisible && IsIconic(window.Handle) == 0,
            pickFolder: () => PickFolder(window.Handle, "Choose where to save your timelapse"));
        controller.RegisterSessionNotifications(window.Handle);
        reminders = new WakeReminders(() => controller.IsBusy, () => controller.IsLocked, () => Ui.Invoke(ShowInvitation));
        _ = reminders.StartAsync(launchedAtSignIn: args.Contains("--at-sign-in"));
        tray.Add();
        StartUpdates();
        if (args.Contains("--show-invitation")) Ui.Post(() => ShowInvitation());
        if (args.Contains("--show")) ShowMain(); else TrimWorkingSet(); // starts hidden in the tray
        // A recording left by an earlier run is offered now, except at sign-in or after a silent update,
        // where it waits for the window to be opened.
        controller.CheckUnfinished(show: !args.Contains("--at-sign-in") && !args.Contains("--after-update"));
        return true;
    }

    public unsafe int Run()
    {
        MSG m;
        while (GetMessageW(&m, 0, 0, 0) > 0)
        {
            if (m.message == WM_KEYDOWN && KeyShortcut(&m)) continue;
            if (window.IsVisible && IsDialogMessageW(window.Handle, &m) != 0) continue;
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }

        return 0;
    }

    // Escape and Enter are handled here so they behave the same on every screen.
    unsafe bool KeyShortcut(MSG* m)
    {
        var focus = GetFocus();
        if (focus == 0 || GetParent(focus) != window.Handle) return false;
        if (m->wParam == VK_ESCAPE) { PostMessageW(window.Handle, WM_COMMAND, IDCANCEL, 0); return true; }
        if (m->wParam == VK_RETURN) { SendMessageW(focus, BM_CLICK, 0, 0); return true; }
        return false;
    }

    bool OnMessage(uint msg, nint wp, nint lp, out nint result)
    {
        result = 0;
        if (msg == Ui.WmRun) { Ui.Drain(); return true; }
        if (msg == TrayIcon.CallbackMessage)
        {
            var mouse = (uint)(lp & 0xFFFF);
            if (mouse == 0x202) OpenLog();               // left click
            else if (mouse == WM_RBUTTONUP) OnTrayAction(tray.ShowMenu());
            return true;
        }

        if (msg == WM_TIMER && wp == TickTimer) { Tick(); return true; }
        if (msg == WM_TIMER && wp == UpdateTimer) { UpdateTick(); return true; }
        if (msg == taskbarCreated && taskbarCreated != 0) { tray.Add(); return true; }
        if (msg == WM_CLOSE) { OnCloseRequested(); return true; }
        if (msg is WM_WTSSESSION_CHANGE or WM_POWERBROADCAST)
        {
            controller.HandleSessionMessage(msg, wp, out var trigger);
            if (trigger is { } t) reminders.Observe(t);
        }

        return false;
    }

    // --- Wake invitation: a small corner window that never takes focus ---
    bool ShowInvitation()
    {
        if (invite is not null || window.IsVisible || controller.IsBusy) return false;
        var popup = new SurfaceWindow(noActivate: true);
        popup.Hook = (uint m, nint wp, nint lp, out nint r) =>
        {
            r = 0;
            if (m != WM_CLOSE) return false;
            Ui.Post(() => CloseInvitation(accepted: false));
            return true;
        };
        popup.Command = id =>
        {
            if (id == Cmd.LetsGo) { CloseInvitation(accepted: true); controller.LetsGo(); ShowMain(); }
            else if (id is Cmd.NotNow or Cmd.Close) CloseInvitation(accepted: false);
        };
        popup.Present(ScreenId.Invitation, new ViewData());
        popup.Place(bottomRight: true);
        popup.Show(activate: false);
        invite = popup;
        return true;
    }

    void CloseInvitation(bool accepted)
    {
        if (invite is null) return;
        invite.Dispose();
        invite = null;
        if (accepted) reminders.Accepted(); else reminders.Dismissed();
        if (!window.IsVisible) { Gfx.Shutdown(); TrimWorkingSet(); }
    }

    // --- Showing and hiding ---
    void OpenLog()
    {
        ShowMain();
        controller.CheckUnfinished(show: true);
    }

    void ShowMain()
    {
        var wasVisible = window.IsVisible;
        Render(force: true);
        if (!wasVisible) window.Place(bottomRight: false);
        else window.KeepOnScreen();
        window.Show(activate: true);
        window.FocusPrimary();
        SetTimer(window.Handle, TickTimer, 1000, 0);
    }

    bool EnsureWindowShown()
    {
        if (!window.IsVisible) ShowMain();
        return window.IsVisible && IsIconic(window.Handle) == 0;
    }

    void HideToTray()
    {
        KillTimer(window.Handle, TickTimer);
        window.Hide();
        Gfx.Shutdown(); // a hidden tray process keeps no graphics objects
        // After a recording the heap is full of frame buffers; give it back when nothing is in progress.
        if (!controller.IsBusy) GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        TrimWorkingSet();
    }

    /// <summary>Close (the × or Alt+F4): always hides to the tray. From the goodbye screen it means "keep going".</summary>
    void OnCloseRequested()
    {
        controller.KeepGoing(GoodbyeDecisionAction.WindowClosed);
        controller.KeepIt(); // closing never deletes
        HideToTray();
    }

    // Presents the current model state. A hidden window is refreshed the next time it is shown.
    void Render(bool force = false)
    {
        if (!window.IsVisible && !force) return;
        var changedScreen = Model.Screen != shownScreen;
        shownScreen = Model.Screen;
        Model.SetElapsed(Model.Screen is ScreenId.Recording or ScreenId.Goodbye ? controller.Elapsed : Model.Elapsed);
        window.PlayerPath = Model.Screen == ScreenId.Preview ? Model.PreviewPath : null;
        window.Present(Model.Screen, Model.ToView(), focusPrimary: changedScreen && window.IsVisible);
        if (window.IsVisible) window.KeepOnScreen();
        if (invite is not null && Model.CaptureStarted) CloseInvitation(accepted: true);
    }

    void Tick()
    {
        if (!window.IsVisible || Model.Screen is not (ScreenId.Recording or ScreenId.Goodbye)) return;
        var t = controller.Elapsed;
        Model.SetElapsed(t);
        window.SetClock(Format.Clock(t), Format.Spoken(t));
    }

    // --- Commands ---
    void OnTrayAction(TrayMenuAction? action)
    {
        switch (action)
        {
            case TrayMenuAction.OpenLog: OpenLog(); break;
            case TrayMenuAction.FinishRecording: controller.FinishFromTray(); break;
            case TrayMenuAction.Exit: Exit(); break;
            case TrayIcon.AutomaticUpdatesItem: Updater.Enabled = !Updater.Enabled; break;
        }
    }

    // Quitting never ends a recording behind the user's back.
    void Exit()
    {
        if (Model.IsCapturing) { controller.FinishFromTray(); return; }
        _ = ShutdownAsync();
    }

    async Task ShutdownAsync()
    {
        HideToTray();
        try { await reminders.DisposeAsync(); await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception e) { AppLog.Write("shutdown", e); }
        PostQuitMessage(0);
    }

    void OnCommand(int id)
    {
        switch (id)
        {
            case Cmd.Close: OnCloseRequested(); break;
            case IDCANCEL: controller.KeepGoing(GoodbyeDecisionAction.Escape); controller.KeepIt(); break; // Escape never stops capture or deletes
            case Cmd.NotNow: controller.Dismiss(); HideToTray(); break;
            case Cmd.Back: controller.Dismiss(); break;
            case Cmd.LetsGo: controller.LetsGo(); break;
            case Cmd.ChooseSource: controller.ChooseSource(); break;
            case Cmd.Timer: controller.TimerClicked(); break;
            case Cmd.KeepGoing: controller.KeepGoing(); break;
            case Cmd.FinishPreview or Cmd.FinishSession: controller.FinishConfirmed(); break;
            case Cmd.TryAgain: controller.TryAgain(); break;
            case Cmd.SaveTimelapse: controller.Save(); break;
            case Cmd.KeepLater: controller.KeepForLater(); break;
            case Cmd.Discard: controller.Discard(); break;
            case Cmd.KeepIt: controller.KeepIt(); break;
            case Cmd.DiscardConfirm: controller.DiscardConfirmed(); break;
            case Cmd.ChooseFolder: controller.ChooseFolder(); break;
            case Cmd.OpenFile: controller.OpenFile(); break;
            case Cmd.Done: controller.Done(); HideToTray(); break;
        }
    }

    void OnPainted()
    {
        if (firstPaintLogged || !Program.Timing) return;
        firstPaintLogged = true;
        using var self = Process.GetCurrentProcess();
        var sinceStart = (DateTime.Now - self.StartTime).TotalMilliseconds;
        var dir = Path.Combine(Path.GetTempPath(), "log-shell");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "paint.txt"),
            $"firstPaintMsSinceProcessStart={sinceStart:F0}\nmainToPaintMs={Program.Clock.ElapsedMilliseconds}\n");
    }

    // --- Automatic updates (installed copy only) ---
    // Every 10 minutes: apply a staged update if the app is idle and hidden, else check at most twice a day.
    const nint UpdateTimer = 2;
    DateTime lastUpdateCheck = DateTime.MinValue;
    int updateRunning;

    public bool RestartForUpdate { get; private set; }

    void StartUpdates()
    {
        if (!Updater.IsInstalledCopy) return;
        Updater.CleanUp(Installer.DefaultInstallDir);
        SetTimer(window.Handle, UpdateTimer, 10 * 60 * 1000, 0);
    }

    void UpdateTick()
    {
        if (!Updater.IsInstalledCopy || !Updater.Enabled || Volatile.Read(ref updateRunning) != 0) return;
        var dir = Installer.DefaultInstallDir;
        if (Updater.IsStaged(dir))
        {
            if (controller.IsBusy || window.IsVisible || invite is not null) return; // never interrupt the user
            if (Updater.Apply(dir)) { RestartForUpdate = true; _ = ShutdownAsync(); }
            return;
        }

        if (DateTime.UtcNow - lastUpdateCheck < TimeSpan.FromHours(12)) return;
        lastUpdateCheck = DateTime.UtcNow;
        Volatile.Write(ref updateRunning, 1);
        _ = Task.Run(() =>
        {
            try { Updater.Stage(dir); }
            catch (Exception e) { AppLog.Write("update check", e); }
            finally { Volatile.Write(ref updateRunning, 0); }
        });
    }

    public void Dispose()
    {
        invite?.Dispose();
        tray?.Dispose();
        window?.Dispose();
        guard?.Dispose();
    }
}
