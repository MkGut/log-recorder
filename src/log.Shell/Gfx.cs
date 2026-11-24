using System.Runtime.InteropServices;

namespace Log.Shell;

internal struct Rgba(float r, float g, float b, float a = 1f)
{
    public float R = r, G = g, B = b, A = a;

    public static Rgba Hex(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    // GDI COLORREF is 0x00BBGGRR.
    public static Rgba FromColorRef(uint c) => new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f);
    public uint Key => ((uint)(R * 255) << 24) | ((uint)(G * 255) << 16) | ((uint)(B * 255) << 8) | (uint)(A * 255);
}

internal struct RectF(float l, float t, float r, float b)
{
    public float L = l, T = t, R = r, B = b;
    public readonly float W => R - L;
    public readonly float H => B - T;
    public static RectF Xywh(float x, float y, float w, float h) => new(x, y, x + w, y + h);
    public readonly RectF Inset(float d) => new(L + d, T + d, R - d, B - d);
}

/// <summary>Corner radii, clockwise from top-left.</summary>
internal readonly record struct Radii(float TL, float TR, float BR, float BL)
{
    public static Radii All(float r) => new(r, r, r, r);
}

internal enum HAlign { Left = 0, Right = 1, Center = 2 }
internal enum VAlign { Top = 0, Bottom = 1, Center = 2 }

/// <summary>Owned COM pointer; Dispose releases it.</summary>
internal readonly struct ComRef(nint p) : IDisposable
{
    public readonly nint P = p;
    public void Dispose() => Gfx.Release(P);
}

/// <summary>Direct2D + DirectWrite through raw vtable calls (in-box d2d1.dll, dwrite.dll).</summary>
internal static unsafe class Gfx
{
    [StructLayout(LayoutKind.Sequential)]
    struct RtProps { public int Type, Format, Alpha; public float DpiX, DpiY; public int Usage, MinLevel; }

    [StructLayout(LayoutKind.Sequential)]
    struct Stop { public float Pos; public Rgba Color; }

    [StructLayout(LayoutKind.Sequential)]
    struct LinearProps { public float X0, Y0, X1, Y1; }

    [StructLayout(LayoutKind.Sequential)]
    struct Bezier { public float X1, Y1, X2, Y2, X3, Y3; }

    [StructLayout(LayoutKind.Sequential)]
    struct RoundedRect { public RectF Rect; public float Rx, Ry; }

    [StructLayout(LayoutKind.Sequential)]
    struct TextMetrics { public float Left, Top, Width, WidthTrailing, Height, LayoutWidth, LayoutHeight; public uint Depth, Lines; }

    [DllImport("d2d1", ExactSpelling = true)] static extern int D2D1CreateFactory(int type, Guid* iid, void* options, nint* factory);
    [DllImport("dwrite", ExactSpelling = true)] static extern int DWriteCreateFactory(int type, Guid* iid, nint* factory);

    static readonly Guid IidD2DFactory = new("06152247-6f50-465a-9245-118bfd3b6007");
    static readonly Guid IidDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    const double Kappa = 0.5522847498;

    static nint d2d, dwrite;
    static readonly Dictionary<(int, int, bool), nint> formats = [];

    static void* Slot(nint obj, int i) => (*(void***)obj)[i];

    public static uint Release(nint p) => p == 0 ? 0 : ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p);

    /// <summary>True when the factories exist (they are created on first use and freed with <see cref="Shutdown"/>).</summary>
    public static void EnsureFactories()
    {
        if (d2d != 0) return;
        nint f;
        var iid = IidD2DFactory;
        Check(D2D1CreateFactory(0, &iid, null, &f));
        d2d = f;
        var iid2 = IidDWriteFactory;
        Check(DWriteCreateFactory(0, &iid2, &f));
        dwrite = f;
    }

    /// <summary>Frees the factories and cached text formats, so a hidden tray process stays small.</summary>
    public static void Shutdown()
    {
        foreach (var f in formats.Values) Release(f);
        formats.Clear();
        Release(d2d); Release(dwrite);
        d2d = dwrite = 0;
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    static RtProps Props(float dpi, int type, int format, int alpha, int usage) =>
        new() { Type = type, Format = format, Alpha = alpha, DpiX = dpi, DpiY = dpi, Usage = usage };

    /// <summary>Render target bound to a GDI DC per paint (window and owner-draw controls).</summary>
    public static nint CreateDcTarget(float dpi)
    {
        EnsureFactories();
        var props = Props(dpi, 1, 87, 1, 2);
        nint rt;
        Check(((delegate* unmanaged[Stdcall]<nint, RtProps*, nint*, int>)Slot(d2d, 16))(d2d, &props, &rt));
        return rt;
    }

    public static void BindDc(nint rt, nint dc, Native.RECT r) =>
        Check(((delegate* unmanaged[Stdcall]<nint, nint, Native.RECT*, int>)Slot(rt, 57))(rt, dc, &r));

    public static void SetDpi(nint rt, float dpi) => ((delegate* unmanaged[Stdcall]<nint, float, float, void>)Slot(rt, 51))(rt, dpi, dpi);

    public static nint TextFormat(float size, int weight, bool italic)
    {
        EnsureFactories();
        var key = ((int)(size * 100), weight, italic);
        if (formats.TryGetValue(key, out var existing)) return existing;
        nint fmt;
        fixed (char* family = "Segoe UI")
        fixed (char* locale = "en-us")
            Check(((delegate* unmanaged[Stdcall]<nint, char*, nint, int, int, int, float, char*, nint*, int>)Slot(dwrite, 15))(
                dwrite, family, 0, weight, italic ? 2 : 0, 5, size, locale, &fmt));
        formats[key] = fmt;
        return fmt;
    }

    /// <summary>Wrapped text size in DIPs for a given max width.</summary>
    public static (float W, float H) Measure(string text, float size, int weight, bool italic, float maxWidth)
    {
        var fmt = TextFormat(size, weight, italic);
        nint layout;
        fixed (char* t = text)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Slot(dwrite, 18))(
                dwrite, t, (uint)text.Length, fmt, maxWidth, 10000f, &layout));
        TextMetrics m;
        ((delegate* unmanaged[Stdcall]<nint, TextMetrics*, int>)Slot(layout, 60))(layout, &m);
        Release(layout);
        return (m.Width, m.Height);
    }

    static ulong Pt(float x, float y) => BitConverter.SingleToUInt32Bits(x) | ((ulong)BitConverter.SingleToUInt32Bits(y) << 32);

    /// <summary>Closed path of a rectangle with four independent (circular) corner radii.</summary>
    public static ComRef RoundedPath(RectF r, Radii rad)
    {
        EnsureFactories();
        var max = Math.Min(r.W, r.H) / 2;
        float tl = Math.Min(rad.TL, max), tr = Math.Min(rad.TR, max), br = Math.Min(rad.BR, max), bl = Math.Min(rad.BL, max);
        float k = (float)(1 - Kappa);
        nint geo, sink;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(d2d, 10))(d2d, &geo));
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(geo, 17))(geo, &sink));
        var begin = (delegate* unmanaged[Stdcall]<nint, ulong, int, void>)Slot(sink, 5);
        var line = (delegate* unmanaged[Stdcall]<nint, ulong, void>)Slot(sink, 10);
        var bez = (delegate* unmanaged[Stdcall]<nint, Bezier*, void>)Slot(sink, 11);
        begin(sink, Pt(r.L, r.T + tl), 0);
        var b = new Bezier { X1 = r.L, Y1 = r.T + tl * k, X2 = r.L + tl * k, Y2 = r.T, X3 = r.L + tl, Y3 = r.T };
        bez(sink, &b);
        line(sink, Pt(r.R - tr, r.T));
        b = new Bezier { X1 = r.R - tr * k, Y1 = r.T, X2 = r.R, Y2 = r.T + tr * k, X3 = r.R, Y3 = r.T + tr };
        bez(sink, &b);
        line(sink, Pt(r.R, r.B - br));
        b = new Bezier { X1 = r.R, Y1 = r.B - br * k, X2 = r.R - br * k, Y2 = r.B, X3 = r.R - br, Y3 = r.B };
        bez(sink, &b);
        line(sink, Pt(r.L + bl, r.B));
        b = new Bezier { X1 = r.L + bl * k, Y1 = r.B, X2 = r.L, Y2 = r.B - bl * k, X3 = r.L, Y3 = r.B - bl };
        bez(sink, &b);
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(sink, 8))(sink, 1);
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(sink, 9))(sink);
        Release(sink);
        return new ComRef(geo);
    }

    /// <summary>
    /// Closed path of cubic Béziers: start x,y then six numbers per curve, in the caller's own
    /// units. Every point lands at (ox + x·s, oy + y·s).
    /// </summary>
    public static ComRef CurvePath(ReadOnlySpan<float> pts, float ox = 0, float oy = 0, float s = 1)
    {
        EnsureFactories();
        nint geo, sink;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(d2d, 10))(d2d, &geo));
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(geo, 17))(geo, &sink));
        ((delegate* unmanaged[Stdcall]<nint, ulong, int, void>)Slot(sink, 5))(sink, Pt(ox + pts[0] * s, oy + pts[1] * s), 0);
        var bez = (delegate* unmanaged[Stdcall]<nint, Bezier*, void>)Slot(sink, 11);
        for (var i = 2; i + 5 < pts.Length; i += 6)
        {
            var b = new Bezier
            {
                X1 = ox + pts[i] * s, Y1 = oy + pts[i + 1] * s, X2 = ox + pts[i + 2] * s, Y2 = oy + pts[i + 3] * s,
                X3 = ox + pts[i + 4] * s, Y3 = oy + pts[i + 5] * s,
            };
            bez(sink, &b);
        }

        ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(sink, 8))(sink, 1);
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(sink, 9))(sink);
        Release(sink);
        return new ComRef(geo);
    }

    public static ComRef Ellipse(float cx, float cy, float rx, float ry)
    {
        float kx = rx * (float)Kappa, ky = ry * (float)Kappa;
        return CurvePath([
            cx - rx, cy,
            cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry,
            cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy,
            cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry,
            cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy,
        ]);
    }

    /// <summary>The area two paths share; used to keep a highlight inside its shape.</summary>
    public static ComRef Intersect(nint a, nint b)
    {
        nint geo, sink;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(d2d, 10))(d2d, &geo));
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(geo, 17))(geo, &sink));
        // CombineWithGeometry, D2D1_COMBINE_MODE_INTERSECT, fine flattening so curves stay smooth at 200 %.
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int, void*, float, nint, int>)Slot(a, 11))(a, b, 1, null, .05f, sink));
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(sink, 9))(sink);
        Release(sink);
        return new ComRef(geo);
    }

    // --- Drawing on a render target ---
    public static void BeginDraw(nint rt) => ((delegate* unmanaged[Stdcall]<nint, void>)Slot(rt, 48))(rt);

    /// <summary>Returns false when the target must be recreated.</summary>
    public static bool EndDraw(nint rt)
    {
        var hr = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)Slot(rt, 49))(rt, null, null);
        if (hr != 0) AppLog.Write($"EndDraw hr=0x{hr:X8}");
        return hr != unchecked((int)0x8899000C);
    }

    public static void Clear(nint rt, Rgba c) => ((delegate* unmanaged[Stdcall]<nint, Rgba*, void>)Slot(rt, 47))(rt, &c);

    public static void SetOrigin(nint rt, float dx, float dy)
    {
        var m = stackalloc float[6] { 1, 0, 0, 1, dx, dy };
        ((delegate* unmanaged[Stdcall]<nint, float*, void>)Slot(rt, 30))(rt, m);
    }

    public static nint SolidBrush(nint rt, Rgba c)
    {
        nint b;
        Check(((delegate* unmanaged[Stdcall]<nint, Rgba*, nint, nint*, int>)Slot(rt, 8))(rt, &c, 0, &b));
        return b;
    }

    /// <summary>Linear gradient over <paramref name="bounds"/> from (x0,y0) to (x1,y1), both as fractions of the bounds.</summary>
    public static ComRef LinearBrush(nint rt, RectF bounds, float x0, float y0, float x1, float y1, ReadOnlySpan<(float Pos, Rgba Color)> stops)
    {
        var s = stackalloc Stop[stops.Length];
        for (var i = 0; i < stops.Length; i++) s[i] = new Stop { Pos = stops[i].Pos, Color = stops[i].Color };
        nint coll, brush;
        Check(((delegate* unmanaged[Stdcall]<nint, Stop*, uint, int, int, nint*, int>)Slot(rt, 9))(rt, s, (uint)stops.Length, 0, 0, &coll));
        var lp = new LinearProps
        {
            X0 = bounds.L + x0 * bounds.W, Y0 = bounds.T + y0 * bounds.H,
            X1 = bounds.L + x1 * bounds.W, Y1 = bounds.T + y1 * bounds.H,
        };
        Check(((delegate* unmanaged[Stdcall]<nint, LinearProps*, nint, nint, nint*, int>)Slot(rt, 10))(rt, &lp, 0, coll, &brush));
        Release(coll);
        return new ComRef(brush);
    }

    /// <summary>Radial gradient centred on (cx,cy); the last stop sits on the rx/ry ellipse.</summary>
    public static ComRef RadialBrush(nint rt, float cx, float cy, float rx, float ry, ReadOnlySpan<(float Pos, Rgba Color)> stops)
    {
        var s = stackalloc Stop[stops.Length];
        for (var i = 0; i < stops.Length; i++) s[i] = new Stop { Pos = stops[i].Pos, Color = stops[i].Color };
        nint coll, brush;
        Check(((delegate* unmanaged[Stdcall]<nint, Stop*, uint, int, int, nint*, int>)Slot(rt, 9))(rt, s, (uint)stops.Length, 0, 0, &coll));
        var rp = stackalloc float[6] { cx, cy, 0, 0, rx, ry };
        Check(((delegate* unmanaged[Stdcall]<nint, float*, nint, nint, nint*, int>)Slot(rt, 11))(rt, rp, 0, coll, &brush));
        Release(coll);
        return new ComRef(brush);
    }

    public static void FillPath(nint rt, nint geo, nint brush) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, void>)Slot(rt, 23))(rt, geo, brush, 0);

    public static void StrokePath(nint rt, nint geo, nint brush, float width) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, float, nint, void>)Slot(rt, 22))(rt, geo, brush, width, 0);

    public static void PushClip(nint rt, RectF r) =>
        ((delegate* unmanaged[Stdcall]<nint, RectF*, int, void>)Slot(rt, 45))(rt, &r, 0);

    public static void PopClip(nint rt) => ((delegate* unmanaged[Stdcall]<nint, void>)Slot(rt, 46))(rt);

    public static void Text(nint rt, string text, RectF r, float size, int weight, bool italic, nint brush, HAlign h, VAlign v, bool wrap)
    {
        var fmt = TextFormat(size, weight, italic);
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(fmt, 3))(fmt, (int)h);
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(fmt, 4))(fmt, (int)v);
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(fmt, 5))(fmt, wrap ? 0 : 1);
        fixed (char* t = text)
            ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, RectF*, nint, int, int, void>)Slot(rt, 27))(
                rt, t, (uint)text.Length, fmt, &r, brush, 0, 0);
    }
}
