using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Log.Shell.Native;

namespace Log.Shell;

/// <summary>
/// Plays the rendered preview with the in-box Media Engine (IMFMediaEngine), drawing into its own
/// child window. Click or Space pauses and resumes. Raw COM, no extra files.
/// </summary>
internal sealed unsafe class VideoPlayer : IDisposable
{
    const string ClassName = "log.Player";
    static bool classRegistered, mfStarted;
    static nint notify; // one process-wide IMFMediaEngineNotify, never freed
    static readonly Guid ClsidFactory = new("B44392DA-499B-446B-A4CB-005FEAD0E6D5");
    static readonly Guid IidFactory = new("4D645ACE-26AA-4688-9BE1-DF3516990B93");
    static readonly Guid IidNotify = new("fee7c112-e776-42b5-9bbf-0048524e2bd5");
    static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid AttrCallback = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    static readonly Guid AttrPlaybackHwnd = new("d988879b-67c9-4d92-baa7-6eadd446039d");

    [DllImport("mfplat", ExactSpelling = true)] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat", ExactSpelling = true)] static extern int MFCreateAttributes(nint* attrs, uint size);
    [DllImport("ole32", ExactSpelling = true)] static extern int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* obj);
    [DllImport("oleaut32", ExactSpelling = true)] static extern nint SysAllocString(char* s);
    [DllImport("oleaut32", ExactSpelling = true)] static extern void SysFreeString(nint s);

    readonly nint window;
    nint engine;
    bool playing = true;

    public nint Handle => window;
    public string? Error { get; private set; }

    static void* Slot(nint obj, int i) => (*(void***)obj)[i];

    /// <summary>Creates the player over the given pixel rectangle of <paramref name="parent"/>. Check <see cref="Error"/>.</summary>
    public VideoPlayer(nint parent, RECT r, string path, int cornerRadiusPx)
    {
        try
        {
            RegisterClass();
            if (!mfStarted) { Check(MFStartup(0x00020070, 0)); mfStarted = true; }
            fixed (char* cls = ClassName)
            fixed (char* title = "Preview video. Press Space to play or pause.")
                window = CreateWindowExW(0, cls, title, WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_CLIPSIBLINGS,
                    r.left, r.top, r.right - r.left, r.bottom - r.top, parent, Cmd.PlayPause, Hinstance, 0);
            if (window == 0) throw new InvalidOperationException("The player window could not be created.");
            SetWindowLongPtrW(window, GWLP_USERDATA, (nint)GCHandle.ToIntPtr(GCHandle.Alloc(this)));
            RoundCorners(r, cornerRadiusPx);
            Start(path);
        }
        catch (Exception e)
        {
            Error = e.Message;
            AppLog.Write("preview player", e);
        }
    }

    RECT placed;

    /// <summary>Moves and resizes the player window without touching playback.</summary>
    public void Move(RECT r, int cornerRadiusPx)
    {
        if (window == 0 || r.left == placed.left && r.top == placed.top && r.right == placed.right && r.bottom == placed.bottom) return;
        SetWindowPos(window, 0, r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_NOZORDER | SWP_NOACTIVATE);
        RoundCorners(r, cornerRadiusPx);
    }

    void RoundCorners(RECT r, int radius)
    {
        placed = r;
        var rgn = CreateRoundRectRgn(0, 0, r.right - r.left + 1, r.bottom - r.top + 1, radius * 2, radius * 2);
        if (rgn != 0 && SetWindowRgn(window, rgn, 1) == 0) DeleteObject(rgn);
    }

    void Start(string path)
    {
        notify = notify != 0 ? notify : CreateNotify();
        nint attrs;
        Check(MFCreateAttributes(&attrs, 2));
        try
        {
            var set64 = (delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attrs, 22);
            var setUnknown = (delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(attrs, 27);
            var cb = AttrCallback; var hw = AttrPlaybackHwnd;
            Check(setUnknown(attrs, &cb, notify));
            Check(set64(attrs, &hw, (ulong)window));

            nint factory;
            var clsid = ClsidFactory; var iid = IidFactory;
            Check(CoCreateInstance(&clsid, 0, 1, &iid, &factory));
            try
            {
                nint eng;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Slot(factory, 3))(factory, 0, attrs, &eng));
                engine = eng;
            }
            finally { Gfx.Release(factory); }
        }
        finally { Gfx.Release(attrs); }

        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(engine, 29))(engine, 1); // autoplay
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(engine, 31))(engine, 1); // loop
        fixed (char* p = path)
        {
            var bstr = SysAllocString(p);
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(engine, 6))(engine, bstr);
            SysFreeString(bstr);
            Check(hr);
        }
    }

    /// <summary>Pause or resume (click or Space).</summary>
    public void Toggle()
    {
        if (engine == 0) return;
        playing = !playing;
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, playing ? 32 : 33))(engine);
    }

    /// <summary>Diagnostics for the fixture window: playback position, pause state, video size.</summary>
    public string Status()
    {
        if (engine == 0) return "no engine";
        var time = ((delegate* unmanaged[Stdcall]<nint, double>)Slot(engine, 16))(engine);
        var paused = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 20))(engine) != 0;
        var hasVideo = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 38))(engine) != 0;
        uint cx = 0, cy = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(engine, 40))(engine, &cx, &cy);
        return $"time={time:F2}s paused={paused} hasVideo={hasVideo} size={cx}x{cy} {Distinct()}";
    }

    // Counts distinct colors in this window's own pixels (PrintWindow of the child only), to show video is being drawn.
    string Distinct()
    {
        RECT r;
        GetClientRect(window, &r);
        int w = r.right, h = r.bottom;
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bmp);
        PrintWindow(window, dc, 2);
        var buf = new byte[w * h * 4];
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        fixed (byte* pb = buf) GetDIBits(dc, bmp, 0, (uint)h, pb, &bi, 0);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(0, screen);
        var seen = new HashSet<uint>();
        for (var i = 0; i < buf.Length; i += 4 * 37) seen.Add(BitConverter.ToUInt32(buf, i) & 0xF8F8F8);
        return $"distinctColors={seen.Count}";
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // --- Window class ---
    static void RegisterClass()
    {
        if (classRegistered) return;
        var name = (char*)Marshal.StringToHGlobalUni(ClassName);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)sizeof(WNDCLASSEX), lpfnWndProc = &PlayerProc, hInstance = Hinstance,
            hCursor = LoadCursorW(0, 32512), lpszClassName = name,
        };
        RegisterClassExW(&wc);
        classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint PlayerProc(nint h, uint msg, nint wp, nint lp)
    {
        var ptr = GetWindowLongPtrW(h, GWLP_USERDATA);
        var player = ptr != 0 ? GCHandle.FromIntPtr(ptr).Target as VideoPlayer : null;
        switch (msg)
        {
            case WM_ERASEBKGND: return 1; // the engine paints
            case 0x202 /* WM_LBUTTONUP */: player?.Toggle(); return 0;
            case WM_KEYDOWN when wp == VK_SPACE: player?.Toggle(); return 0;
            case 0x201 /* WM_LBUTTONDOWN */: SetFocus(h); return 0;
        }

        return DefWindowProcW(h, msg, wp, lp);
    }

    // --- IMFMediaEngineNotify: a fixed, never-freed COM object ---
    static nint CreateNotify()
    {
        var vtbl = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        vtbl[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&NotifyQueryInterface;
        vtbl[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&NotifyRef;
        vtbl[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&NotifyRef;
        vtbl[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, uint, int>)&NotifyEvent;
        var obj = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        *obj = (nint)vtbl;
        return (nint)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int NotifyQueryInterface(nint self, Guid* iid, nint* result)
    {
        if (*iid == IidUnknown || *iid == IidNotify) { *result = self; return 0; }
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint NotifyRef(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int NotifyEvent(nint self, uint ev, nuint p1, uint p2)
    {
        if (ev == 5) AppLog.Write($"media engine error {p1} 0x{p2:X8}"); // MF_MEDIA_ENGINE_EVENT_ERROR
        else if (Program.Timing && ev is 10 or 13 or 19) AppLog.Write($"media engine event {ev}");
        return 0;
    }

    public void Dispose()
    {
        if (engine != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 42))(engine); // Shutdown
            Gfx.Release(engine);
            engine = 0;
        }

        if (window != 0)
        {
            var ptr = GetWindowLongPtrW(window, GWLP_USERDATA);
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
            if (ptr != 0) GCHandle.FromIntPtr(ptr).Free();
            DestroyWindow(window);
        }
    }
}
