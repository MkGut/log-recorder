using System.Collections.Concurrent;
using Log.Core.Shell;
using Log.Core.Store;
using static Log.Shell.Native;

namespace Log.Shell;

/// <summary>
/// Tiny log next to the data, for faults a tray app cannot show. It records only the exception type and
/// HRESULT (messages can hold paths or window titles) and keeps at most two files of 1 MB.
/// </summary>
internal static class AppLog
{
    const long MaxBytes = 1024 * 1024;

    public static string Line(DateTime at, string what, Exception? e) =>
        e is null ? $"{at:s} {what}" : $"{at:s} {what} {e.GetType().Name} 0x{e.HResult:X8}";

    public static void Write(string what, Exception? e = null)
    {
        try
        {
            Directory.CreateDirectory(LogPaths.DefaultRootDirectory);
            var path = Path.Combine(LogPaths.DefaultRootDirectory, "shell.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, Line(DateTime.Now, what, e) + Environment.NewLine);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

/// <summary>
/// Moves work onto the UI thread: engine events arrive on other threads and
/// are posted to the main window instead of being handled in place.
/// </summary>
internal static class Ui
{
    public const uint WmRun = WM_APP + 2;
    static readonly ConcurrentQueue<Action> queue = new();
    static nint hwnd;
    static int uiThread;

    public static void Init(nint mainWindow)
    {
        hwnd = mainWindow;
        uiThread = Environment.CurrentManagedThreadId;
    }

    public static bool OnUiThread => Environment.CurrentManagedThreadId == uiThread;

    public static void Post(Action action)
    {
        queue.Enqueue(action);
        if (hwnd != 0) PostMessageW(hwnd, WmRun, 0, 0);
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
    public static Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>Runs an async function on the UI thread (the picker needs the UI thread's apartment).</summary>
    public static Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { tcs.SetResult(await func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public static void Drain()
    {
        while (queue.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception e) { AppLog.Write("ui work", e); }
        }
    }
}

/// <summary>Notification-area icon and its menu. UI thread only.</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint CallbackMessage = WM_APP + 1;
    /// <summary>Shell-only menu item (not part of the core tray model): turns automatic updates on or off.</summary>
    public const TrayMenuAction AutomaticUpdatesItem = (TrayMenuAction)100;
    readonly nint hwnd;
    readonly Dictionary<string, nint> icons = [];
    TrayMenuPresentation presentation = TrayMenuModel.Create("idle", canFinish: false);
    bool added;

    public TrayIcon(nint hwnd) => this.hwnd = hwnd;

    public TrayMenuPresentation Presentation => presentation;

    NOTIFYICONDATA Data()
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = (uint)sizeof(NOTIFYICONDATA), hWnd = hwnd, uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = CallbackMessage, hIcon = Icon(IconFor(presentation.Status)),
        };
        var tip = presentation.ToolTip;
        for (var i = 0; i < Math.Min(tip.Length, 127); i++) d.szTip[i] = tip[i];
        return d;
    }

    /// <summary>
    /// Which icon the tray shows for a status: "tray-recording", "tray-paused", "tray-finishing",
    /// "tray-error" (Assets\tray-*.ico), or "" for the plain whale.
    /// </summary>
    internal static string IconFor(TrayStatus status) => status switch
    {
        TrayStatus.Recording => "tray-recording",
        TrayStatus.Paused => "tray-paused",
        TrayStatus.Finishing => "tray-finishing",
        TrayStatus.Error => "tray-error",
        _ => "",
    };

    /// <summary>Loads once and caches; anything that fails to load falls back to the plain app icon.</summary>
    nint Icon(string name)
    {
        if (icons.TryGetValue(name, out var h)) return h;
        h = name.Length > 0 ? LoadEmbeddedIcon(name) : 0;
        if (h == 0) h = name.Length > 0 ? Icon("") : LoadAppIcon();
        return icons[name] = h;
    }

    static nint LoadAppIcon()
    {
        var path = stackalloc char[520];
        GetModuleFileNameW(0, path, 520);
        nint small = 0;
        ExtractIconExW(path, 0, null, &small, 1);
        return small;
    }

    /// <summary>Picks the .ico image closest to the tray's icon size and hands its bytes to Windows.</summary>
    static nint LoadEmbeddedIcon(string name)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name);
        if (stream is null) return 0;
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);

        var want = GetSystemMetrics(SM_CXSMICON);
        var best = PickIconEntry(ico, want);
        if (best < 0) return 0;
        var size = BitConverter.ToUInt32(ico, 6 + best * 16 + 8);
        var offset = BitConverter.ToUInt32(ico, 6 + best * 16 + 12);
        if ((ulong)offset + size > (ulong)ico.Length) return 0;
        fixed (byte* bits = ico)
            return CreateIconFromResourceEx(bits + offset, size, 1, 0x00030000, want, want, 0);
    }

    /// <summary>
    /// Index of the .ico image to use at <paramref name="want"/> pixels: the smallest one at least
    /// that wide (scaling down stays sharp), else the widest. -1 when the file has no usable entries.
    /// </summary>
    internal static int PickIconEntry(ReadOnlySpan<byte> ico, int want)
    {
        // ICONDIR: 6-byte header, then 16-byte entries (width, ..., byte count at +8, offset at +12).
        if (ico.Length < 6) return -1;
        int count = ico[4] | ico[5] << 8, best = -1, bestWidth = 0;
        if (6 + count * 16 > ico.Length) return -1;
        for (var i = 0; i < count; i++)
        {
            var width = ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16];
            if (best < 0 || (width >= want ? bestWidth < want || width < bestWidth : width > bestWidth))
                (best, bestWidth) = (i, width);
        }

        return best;
    }

    /// <summary>Adds the icon; also used to restore it after Explorer restarts.</summary>
    public bool Add()
    {
        var d = Data();
        added = Shell_NotifyIconW(NIM_ADD, &d) != 0;
        return added;
    }

    /// <summary>Applies the engine state to icon and menu. True only when Windows accepted the change.</summary>
    public bool Update(string state, bool canFinish)
    {
        presentation = TrayMenuModel.Create(state, canFinish);
        if (!added) return false;
        var d = Data();
        return Shell_NotifyIconW(NIM_MODIFY, &d) != 0 || Shell_NotifyIconW(NIM_MODIFY, &d) != 0;
    }

    /// <summary>Shows the menu and returns the chosen action, if any.</summary>
    public TrayMenuAction? ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return null;
        try
        {
            // The lightweight shell has no settings or unfinished-saves pages.
            foreach (var item in presentation.Items)
            {
                if (item.Action is TrayMenuAction.Settings or TrayMenuAction.UnfinishedSaves) continue;
                if (item.Action == TrayMenuAction.Exit && Updater.IsInstalledCopy)
                    fixed (char* u = "Automatic updates")
                        AppendMenuW(menu, Updater.Enabled ? MF_STRING | MF_CHECKED : MF_STRING, (nuint)AutomaticUpdatesItem, u);
                fixed (char* t = item.Label)
                    AppendMenuW(menu, item.IsEnabled ? MF_STRING : MF_STRING | MF_GRAYED, (nuint)item.Action, t);
            }

            POINT pt;
            GetCursorPos(&pt);
            SetForegroundWindow(hwnd);
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD, pt.x, pt.y, hwnd, 0);
            return cmd == 0 ? null : (TrayMenuAction)cmd;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (added)
        {
            var d = Data();
            Shell_NotifyIconW(NIM_DELETE, &d);
            added = false;
        }

        foreach (var h in icons.Values.Distinct()) DestroyIcon(h);
        icons.Clear();
    }
}
