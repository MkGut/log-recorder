using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Log.Shell.Native;

namespace Log.Shell;

/// <summary>
/// One cobalt popup window. The window paints the backdrop with Direct2D; each button and text
/// line is a real child control (owner-draw BUTTON / STATIC) that paints itself with <see cref="Skin"/>,
/// so Tab order, Enter/Space and screen readers work as for any Win32 dialog.
/// </summary>
internal sealed unsafe class SurfaceWindow : IDisposable
{
    public delegate bool MessageHook(uint msg, nint wParam, nint lParam, out nint result);

    const string ClassName = "log.Surface";
    static bool classRegistered;
    static nint staticProc;
    static readonly Skin skin = Skin.Current;

    readonly nint hwnd;
    readonly bool noActivate;
    readonly Dictionary<int, nint> controls = [];
    readonly Dictionary<nint, Item> items = [];
    readonly Dictionary<int, RECT> pixelRects = [];
    GCHandle self;
    Painter? controlPainter;
    float dpiScale = 1f;
    int pxW, pxH;
    Scene scene = new();
    ViewData data = new();
    readonly Dictionary<int, string> spokenNames = [];

    /// <summary>Fixtures only: draw at this scale instead of the window's DPI, and skip the rounded region.</summary>
    public float? ScaleOverride { get; init; }
    public bool Fixture { get; init; }
    public float? WidthOverride { get; set; }

    /// <summary>Preview file to play when the current screen has a player area.</summary>
    public string? PlayerPath
    {
        get => playerPath;
        set { if (playerPath != value) { playerPath = value; playerUnavailable = false; } }
    }

    string? playerPath;
    bool playerUnavailable;
    VideoPlayer? player;
    string? playingPath;

    public MessageHook? Hook;
    public Action<int>? Command;
    public Action? Painted;

    public nint Handle => hwnd;
    public string? PlayerStatus() => player?.Status();
    public Scene CurrentScene => scene;
    public float Scale => ScaleOverride ?? dpiScale;
    public bool IsVisible => IsWindowVisible(hwnd) != 0;

    public SurfaceWindow(bool noActivate = false, bool fixture = false, float? scaleOverride = null)
    {
        this.noActivate = noActivate;
        Fixture = fixture;
        ScaleOverride = scaleOverride;
        RegisterClass();
        self = GCHandle.Alloc(this);
        var ex = noActivate ? WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST : (fixture ? WS_EX_TOOLWINDOW : WS_EX_APPWINDOW);
        fixed (char* cls = ClassName)
        fixed (char* title = "log")
            hwnd = CreateWindowExW(ex, cls, title, WS_POPUP | WS_CLIPCHILDREN, 0, 0, 100, 100, 0, 0, Hinstance, GCHandle.ToIntPtr(self));
        if (hwnd == 0) throw new InvalidOperationException("The log window could not be created.");
        dpiScale = Math.Max(1f, GetDpiForWindow(hwnd) / 96f);
    }

    static void RegisterClass()
    {
        if (classRegistered) return;
        var name = (char*)Marshal.StringToHGlobalUni(ClassName);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)sizeof(WNDCLASSEX), lpfnWndProc = &WndProc, hInstance = Hinstance,
            hCursor = LoadCursorW(0, 32512), lpszClassName = name,
        };
        RegisterClassExW(&wc);
        classRegistered = true;
    }

    // --- Presenting a screen ---
    /// <summary>Lays out and shows a screen; the window keeps its top-left corner.</summary>
    public void Present(ScreenId id, ViewData view, bool focusPrimary = false)
    {
        view = view with { PreviewInPlayer = !playerUnavailable };
        data = view;
        var focused = FocusedId();
        Gfx.EnsureFactories();
        var work = WorkArea();
        var availableDip = (work.right - work.left) / Scale - 2 * 24;
        var s = Layout.Build(id, view, WidthOverride ?? Layout.WidthFor(id, availableDip), skin.Measure);
        Apply(s);
        if (focusPrimary) FocusPrimary();
        else if (focused != 0 && controls.TryGetValue(focused, out var h)) SetFocus(h);
    }

    int FocusedId()
    {
        var f = GetFocus();
        foreach (var (id, h) in controls) if (h == f) return id;
        return 0;
    }

    void Apply(Scene s)
    {
        scene = s;
        var scale = Scale;
        pxW = (int)Math.Round(s.Width * scale);
        pxH = (int)Math.Round(s.Height * scale);
        RECT r;
        GetWindowRect(hwnd, &r);
        SetWindowPos(hwnd, 0, r.left, r.top, pxW, pxH, SWP_NOZORDER | SWP_NOACTIVATE);
        if (!Fixture) ApplyShape();
        Native.SetText(hwnd, s.Title.Length > 0 ? "log — " + s.Title : "log");
        RebuildControls();
        DisposeGraphics();
        InvalidateRect(hwnd, null, 0);
        SyncPlayer();
        SyncProgressTimer();
    }

    // Indeterminate progress animates only while its screen is up (~30 fps, one sweep every 1.8 s).
    const nint ProgressTimer = 77;
    bool progressTimerOn;

    static float ProgressPhase() => (float)(Environment.TickCount64 % 1800 / 1800.0);

    void SyncProgressTimer()
    {
        var want = !Fixture && scene.Items.Any(i => i.Kind == ItemKind.Progress);
        if (want == progressTimerOn) return;
        progressTimerOn = want;
        if (want) SetTimer(hwnd, ProgressTimer, 33, 0);
        else KillTimer(hwnd, ProgressTimer);
    }

    void AnimateProgress()
    {
        var it = scene.Items.FirstOrDefault(i => i.Kind == ItemKind.Progress);
        if (it is null) return;
        var s = Scale;
        var r = new RECT
        {
            left = (int)Math.Floor(it.Rect.L * s) - 1, top = (int)Math.Floor(it.Rect.T * s) - 1,
            right = (int)Math.Ceiling(it.Rect.R * s) + 1, bottom = (int)Math.Ceiling(it.Rect.B * s) + 1,
        };
        InvalidateRect(hwnd, &r, 0);
    }

    void DetachPlayer()
    {
        player?.Dispose();
        player = null;
        playingPath = null;
    }

    // Shows the preview player over the scene's player area, or falls back to "Open file".
    // A redraw must not restart playback, so the same file keeps its player and only moves.
    void SyncPlayer()
    {
        var area = scene.Items.FirstOrDefault(i => i.Kind == ItemKind.Player);
        if (area is null || playerPath is null || !data.PreviewInPlayer || Fixture) { DetachPlayer(); return; }
        var scale = Scale;
        var inset = 10 * scale;
        var r = new RECT
        {
            left = (int)Math.Round(area.Rect.L * scale + inset), top = (int)Math.Round(area.Rect.T * scale + inset),
            right = (int)Math.Round(area.Rect.R * scale - inset), bottom = (int)Math.Round(area.Rect.B * scale - inset),
        };
        var radius = (int)Math.Round(14 * scale);
        if (player is not null && playingPath == playerPath) { player.Move(r, radius); return; }
        DetachPlayer();
        player = new VideoPlayer(hwnd, r, playerPath, radius);
        playingPath = playerPath;
        if (player.Error is null) return;
        DetachPlayer();
        playerUnavailable = true;
        Present(scene.Screen, data);
    }

    void RebuildControls()
    {
        foreach (var h in controls.Values) DestroyWindow(h);
        controls.Clear(); items.Clear(); pixelRects.Clear(); spokenNames.Clear();
        var scale = Scale;
        foreach (var it in scene.Items)
        {
            if (it.Kind is ItemKind.Rule or ItemKind.Progress or ItemKind.Player) continue;
            var l = (int)Math.Round(it.Rect.L * scale); var t = (int)Math.Round(it.Rect.T * scale);
            var w = (int)Math.Round(it.Rect.W * scale); var h = (int)Math.Round(it.Rect.H * scale);
            var button = it.Kind == ItemKind.Button;
            // Window text is the accessible name; it is not what gets painted. Buttons treat '&' as a
            // mnemonic, so it is doubled to keep names like "Finish & preview" intact.
            var name = it.Name ?? it.Text;
            if (button) name = name.Replace("&", "&&");
            var ctl = CreateChild(button ? "BUTTON" : "STATIC", name,
                (button ? BS_OWNERDRAW | WS_TABSTOP : SS_NOPREFIX) | (it.Enabled ? 0 : WS_DISABLED), hwnd, it.Id, l, t, w, h);
            controls[it.Id] = ctl;
            items[ctl] = it;
            pixelRects[it.Id] = new RECT { left = l, top = t, right = l + w, bottom = t + h };
            // Buttons are owner-draw (WM_DRAWITEM); static text is a plain STATIC painted by its subclass.
            if (!button)
                staticProc = SetWindowLongPtrW(ctl, GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&ChildProc);
        }
    }

    public void FocusPrimary()
    {
        var target = scene.Items.FirstOrDefault(i => i.Kind == ItemKind.Button && i.Primary)
                     ?? scene.Items.FirstOrDefault(i => i.Kind == ItemKind.Button && i.Id != Cmd.Close);
        if (target is not null && controls.TryGetValue(target.Id, out var h)) SetFocus(h);
    }

    /// <summary>
    /// Updates the elapsed readout in place. The accessible name changes only when
    /// <paramref name="spoken"/> does (about once a minute), so a screen reader is not told every second.
    /// </summary>
    public void SetClock(string clock, string spoken)
    {
        foreach (var (hCtl, it) in items.ToArray())
        {
            if (it.Tag != "clock") continue;
            if (it.Text != clock)
            {
                items[hCtl] = it with { Text = clock };
                InvalidateRect(hCtl, null, 0);
            }

            var name = it.Kind == ItemKind.Button ? "Finish recording, elapsed " + spoken : "Elapsed " + spoken;
            if (!spokenNames.TryGetValue(it.Id, out var last) || last != name)
            {
                spokenNames[it.Id] = name;
                Native.SetText(hCtl, name);
            }
        }
    }

    /// <summary>Position for the next show: bottom-right of the work area (invitation) or centered.</summary>
    public void Place(bool bottomRight)
    {
        var work = WorkArea();
        RECT r;
        GetWindowRect(hwnd, &r);
        var w = r.right - r.left; var h = r.bottom - r.top;
        var margin = (int)Math.Round(24 * Scale);
        var x = bottomRight ? work.right - w - margin : work.left + (work.right - work.left - w) / 2;
        var y = bottomRight ? work.bottom - h - margin : work.top + (work.bottom - work.top - h) / 2;
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Keeps the window on the screen after its size changed.</summary>
    public void KeepOnScreen()
    {
        var work = WorkArea();
        RECT r;
        GetWindowRect(hwnd, &r);
        var x = Math.Clamp(r.left, work.left, Math.Max(work.left, work.right - (r.right - r.left)));
        var y = Math.Clamp(r.top, work.top, Math.Max(work.top, work.bottom - (r.bottom - r.top)));
        if (x != r.left || y != r.top) SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    RECT WorkArea()
    {
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);
        var mon = MonitorFromWindow(hwnd, 1);
        if (mon != 0 && GetMonitorInfoW(mon, &mi) != 0) return mi.rcWork;
        return new RECT { left = 0, top = 0, right = GetSystemMetrics(SM_CXSCREEN), bottom = GetSystemMetrics(SM_CYSCREEN) };
    }

    public void Show(bool activate)
    {
        ShowWindow(hwnd, activate && !noActivate ? SW_SHOW : SW_SHOWNOACTIVATE);
        if (activate && !noActivate)
        {
            if (IsIconic(hwnd) != 0) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }

    public void Hide()
    {
        ShowWindow(hwnd, SW_HIDE);
        DetachPlayer();
        DisposeGraphics();
    }

    void DisposeGraphics()
    {
        controlPainter?.Dispose(); controlPainter = null;
    }

    /// <summary>Copies the window (children included) to a top-down 32-bit BGRA buffer via PrintWindow.</summary>
    public byte[]? Capture(out int width, out int height)
    {
        width = pxW; height = pxH;
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, pxW, pxH);
        var old = SelectObject(dc, bmp);
        var ok = PrintWindow(hwnd, dc, 0) != 0; // WM_PRINT into our own bitmap; never reads the screen
        var buf = new byte[pxW * pxH * 4];
        if (ok)
        {
            var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = pxW, biHeight = -pxH, biPlanes = 1, biBitCount = 32 };
            fixed (byte* p = buf) GetDIBits(dc, bmp, 0, (uint)pxH, p, &bi, 0);
        }

        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(0, screen);
        return ok ? buf : null;
    }

    // --- Rounded region (curve math from CobaltWindowShape) ---
    void ApplyShape()
    {
        var rgn = CreateShapeRegion(pxW, pxH, Scale, scene.Width);
        if (rgn == 0) return;
        var off = 1;
        DwmSetWindowAttribute(hwnd, 2, &off, 4);        // no non-client rendering
        var noRound = 1;
        DwmSetWindowAttribute(hwnd, 33, &noRound, 4);   // no system corner rounding
        var none = -2;
        DwmSetWindowAttribute(hwnd, 34, &none, 4);      // no border color
        if (SetWindowRgn(hwnd, rgn, 1) == 0) DeleteObject(rgn);
    }

    /// <summary>Casing outline plus the "log" badge, which overhangs the casing's top-left curve.</summary>
    internal static nint CreateShapeRegion(int pxW, int pxH, double scale, float widthDip)
    {
        var casing = Outline(pxW, pxH, scale, Skin.Casing(widthDip));
        var lens = Skin.LensRect();
        // A little larger than the badge, so its 2-DIP rim and anti-aliased edge are not clipped.
        var pad = (int)Math.Ceiling(2 * scale);
        var lensPoly = Outline((int)Math.Round(lens.W * scale) + 2 * pad, (int)Math.Round(lens.H * scale) + 2 * pad, scale, Skin.LensShape,
            (int)Math.Round(lens.L * scale) - pad, (int)Math.Round(lens.T * scale) - pad);
        nint rgn, lensRgn;
        fixed (POINT* c = casing) rgn = CreatePolygonRgn(c, casing.Length, 2);
        fixed (POINT* l = lensPoly) lensRgn = CreatePolygonRgn(l, lensPoly.Length, 2);
        if (rgn != 0 && lensRgn != 0) CombineRgn(rgn, rgn, lensRgn, 2); // RGN_OR
        if (lensRgn != 0) DeleteObject(lensRgn);
        return rgn;
    }

    internal static POINT[] Outline(int width, int height, double scale, Radii r, int offsetX = 0, int offsetY = 0)
    {
        const double k = 0.55228475;
        double left = offsetX, top = offsetY, right = offsetX + width - 1d, bottom = offsetY + height - 1d;
        double w2 = (right - left) / 2, h2 = (bottom - top) / 2, half = Math.Min(w2, h2);
        double tl = Math.Min(r.TL * scale, half), tr = Math.Min(r.TR * scale, half);
        double br = Math.Min(r.BR * scale, half), bl = Math.Min(r.BL * scale, half);
        // About one point every two pixels of the largest corner, so big curves are not visibly faceted.
        var steps = Math.Max(8, (int)Math.Ceiling(Math.Max(Math.Max(tl, tr), Math.Max(br, bl)) * .8));
        var pts = new List<POINT>(4 * steps + 4);
        void Curve(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            for (var i = 0; i <= steps; i++)
            {
                double t = i / (double)steps, u = 1 - t;
                var x = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
                var y = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
                pts.Add(new POINT { x = (int)Math.Round(x), y = (int)Math.Round(y) });
            }
        }

        Curve(left, top + tl, left, top + tl * (1 - k), left + tl * (1 - k), top, left + tl, top);
        Curve(right - tr, top, right - tr * (1 - k), top, right, top + tr * (1 - k), right, top + tr);
        Curve(right, bottom - br, right, bottom - br * (1 - k), right - br * (1 - k), bottom, right - br, bottom);
        Curve(left + bl, bottom, left + bl * (1 - k), bottom, left, bottom - bl * (1 - k), left, bottom - bl);
        return pts.ToArray();
    }

    // --- Painting ---
    // Backdrop and controls share one GDI-bound Direct2D target, so on-screen paint and
    // PrintWindow see the same pixels.
    void PaintClient(nint dc)
    {
        if (pxW <= 0 || pxH <= 0) return;
        controlPainter ??= new Painter(Gfx.CreateDcTarget(96f * Scale));
        var p = controlPainter;
        Gfx.BindDc(p.Rt, dc, new RECT { left = 0, top = 0, right = pxW, bottom = pxH });
        Gfx.BeginDraw(p.Rt);
        skin.PaintBackdrop(p, scene.Width, scene.Height);
        foreach (var it in scene.Items)
        {
            if (it.Kind == ItemKind.Rule) skin.PaintRule(p, it.Rect);
            else if (it.Kind == ItemKind.Progress) skin.PaintIndeterminate(p, it.Rect, Fixture ? 0 : ProgressPhase());
            else if (it.Kind == ItemKind.Player) skin.PaintPlayerPanel(p, it.Rect);
        }

        if (!Gfx.EndDraw(p.Rt)) { controlPainter?.Dispose(); controlPainter = null; InvalidateRect(hwnd, null, 0); }
    }

    void OnPaint()
    {
        PAINTSTRUCT ps;
        var dc = BeginPaint(hwnd, &ps);
        try { PaintClient(dc); }
        finally { EndPaint(hwnd, &ps); }
        Painted?.Invoke();
    }

    // Controls paint themselves here: buttons from WM_DRAWITEM, static text from its subclass.
    void PaintControl(nint ctl, nint dc, uint state = 0)
    {
        if (!items.TryGetValue(ctl, out var it)) return;
        controlPainter ??= new Painter(Gfx.CreateDcTarget(96f * Scale));
        var p = controlPainter;
        var scale = Scale;
        RECT rc;
        GetClientRect(ctl, &rc);
        var px = pixelRects[it.Id];
        float w = rc.right / scale, h = rc.bottom / scale;
        if (it.Kind == ItemKind.Button && !it.Enabled) state |= ODS_DISABLED;
        Gfx.BindDc(p.Rt, dc, rc);
        Gfx.BeginDraw(p.Rt);
        skin.PaintBackdrop(p, scene.Width, scene.Height, px.left / scale, px.top / scale);
        var local = new RectF(0, 0, w, h);
        if (it.Kind == ItemKind.Text) skin.PaintText(p, it.Role, it.Text, local, it.Align, it.Dot);
        else skin.PaintButton(p, it.Style, local, it.Text, state, Skin.TimerSize(scene.Width));
        if (!Gfx.EndDraw(p.Rt)) { controlPainter?.Dispose(); controlPainter = null; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint ChildProc(nint h, uint msg, nint wp, nint lp)
    {
        var parent = GetParent(h);
        var ptr = parent == 0 ? 0 : GetWindowLongPtrW(parent, GWLP_USERDATA);
        var owner = ptr != 0 ? GCHandle.FromIntPtr(ptr).Target as SurfaceWindow : null;
        if (owner is not null)
        {
            try
            {
                switch (msg)
                {
                    case WM_ERASEBKGND: return 1;
                    case WM_PAINT:
                        {
                            PAINTSTRUCT ps;
                            var dc = BeginPaint(h, &ps);
                            owner.PaintControl(h, dc);
                            EndPaint(h, &ps);
                            return 0;
                        }
                    case WM_PRINTCLIENT: owner.PaintControl(h, wp); return 0;
                }
            }
            catch (Exception e) { AppLog.Write("control paint", e); }
        }

        return CallWindowProcW(staticProc, h, msg, wp, lp);
    }

    // --- Window procedure ---
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WndProc(nint h, uint msg, nint wp, nint lp)
    {
        if (msg == WM_NCCREATE)
            SetWindowLongPtrW(h, GWLP_USERDATA, ((nint*)lp)[0]);
        var ptr = GetWindowLongPtrW(h, GWLP_USERDATA);
        if (ptr != 0 && GCHandle.FromIntPtr(ptr).Target is SurfaceWindow w)
        {
            try { return w.Handle0(h, msg, wp, lp); }
            catch (Exception e) { AppLog.Write("window message " + msg, e); }
        }

        return DefWindowProcW(h, msg, wp, lp);
    }

    nint Handle0(nint h, uint msg, nint wp, nint lp)
    {
        if (Hook is not null && Hook(msg, wp, lp, out var result)) return result;
        switch (msg)
        {
            case WM_PAINT: OnPaint(); return 0;
            case WM_TIMER when wp == ProgressTimer: AnimateProgress(); return 0;
            case WM_DRAWITEM:
                {
                    var di = (DRAWITEMSTRUCT*)lp;
                    PaintControl(di->hwndItem, di->hDC, di->itemState);
                    return 1;
                }
            case WM_PRINTCLIENT: PaintClient(wp); return 0;
            case WM_ERASEBKGND: return 1;
            case WM_COMMAND:
                {
                    var id = (int)(wp & 0xFFFF);
                    var code = (int)((wp >> 16) & 0xFFFF);
                    // 0 = BN_CLICKED. Handled after the click returns, because navigating rebuilds (destroys) the buttons.
                    if (id == IDCANCEL || code == 0) Ui.Post(() => Command?.Invoke(id));
                    return 0;
                }
            case WM_NCHITTEST:
                {
                    // The header drags the window; everything below is client area.
                    var x = (short)(lp & 0xFFFF); var y = (short)((lp >> 16) & 0xFFFF);
                    POINT pt = new() { x = x, y = y };
                    var origin = new POINT();
                    ClientToScreen(h, &origin);
                    return pt.y - origin.y < (Skin.CasingTop + Skin.HeaderHeight) * Scale ? HTCAPTION : HTCLIENT;
                }
            case WM_MOUSEACTIVATE when noActivate: return MA_NOACTIVATE;
            case WM_DPICHANGED:
                {
                    dpiScale = (wp & 0xFFFF) / 96f;
                    var r = (RECT*)lp;
                    SetWindowPos(h, 0, r->left, r->top, r->right - r->left, r->bottom - r->top, SWP_NOZORDER | SWP_NOACTIVATE);
                    Present(scene.Screen, data);
                    return 0;
                }
            case WM_SETTINGCHANGE or WM_THEMECHANGED or WM_SYSCOLORCHANGE:
                // High contrast or text size may have changed: re-read, then lay the screen out again.
                skin.Refresh();
                if (scene.Items.Count > 0) Present(scene.Screen, data);
                return 0;
            case WM_NCDESTROY:
                SetWindowLongPtrW(h, GWLP_USERDATA, 0);
                return DefWindowProcW(h, msg, wp, lp);
        }

        return DefWindowProcW(h, msg, wp, lp);
    }

    public void Dispose()
    {
        DetachPlayer();
        DisposeGraphics();
        if (progressTimerOn) KillTimer(hwnd, ProgressTimer);
        if (hwnd != 0) DestroyWindow(hwnd);
        if (self.IsAllocated) self.Free();
    }
}
