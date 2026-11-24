namespace Log.Shell;

internal enum ButtonStyle { Primary, Secondary, Close, Timer }

internal enum TextRole { Heading, Goodbye, Body, Detail, Live, Button }

/// <summary>A render target plus its cached solid brushes. Dispose frees both.</summary>
internal sealed class Painter(nint rt) : IDisposable
{
    public readonly nint Rt = rt;
    readonly Dictionary<uint, nint> solids = [];

    public nint Solid(Rgba c)
    {
        if (!solids.TryGetValue(c.Key, out var b)) solids[c.Key] = b = Gfx.SolidBrush(Rt, c);
        return b;
    }

    public void Dispose()
    {
        foreach (var b in solids.Values) Gfx.Release(b);
        solids.Clear();
        Gfx.Release(Rt);
    }
}

/// <summary>
/// Every color, radius, size and font from docs/ui-handoff/DESIGN.md lives here,
/// together with the Direct2D drawing of the shared shell and its controls.
/// The extra gloss (rim light, sheen, inner shadow) follows the approved reference renders.
/// </summary>
internal sealed class Skin
{
    // --- Sizes (DIPs) ---
    public const float PreferredWidth = 510, InvitationWidth = 395, MinWidth = 320, NarrowBelow = 400;
    public const float HeaderHeight = 76, LensWidth = 74, LensHeight = 64, CloseSize = 44, ButtonHeight = 44, ButtonMinWidth = 94;
    public const float CasingLeft = 16, CasingTop = 10, CasingRight = 16, CasingBottom = 27, RowGap = 6;
    public const float HeaderInset = 8, HeaderGap = 14, BodyGap = 16;
    public const float InsetTop = CasingTop + HeaderHeight + RowGap + 4 + 4 + RowGap; // header, strip, gaps = 106

    // Nov 9 DESIGN.md: casing 106/34/94/36 (72/27/62/30 compact), inset 48/19/55/20, action lenses 24/10/24/10, timer 64/27/64/27.
    public static Radii Casing(float width) => width < NarrowBelow ? new(72, 27, 62, 30) : new(106, 34, 94, 36);
    public static readonly Radii Inset = new(48, 19, 55, 20);
    public static readonly Radii ButtonShape = new(24, 10, 24, 10);
    public static readonly Radii LensShape = new(37, 37, 30, 30);
    public static readonly Radii TimerShape = new(64, 27, 64, 27);
    public static readonly Radii PlayerShape = new(24, 12, 28, 12);

    // --- Text sizes ---
    public const float TaglineSize = 12, BodySize = 14, DetailSize = 13, HeadingSize = 24, GoodbyeSize = 28, ButtonSize = 14;
    public const int Light = 300, Regular = 400, SemiBold = 600;

    /// <summary>Timer size: 70 at preferred width, 42–56 when narrow.</summary>
    public static float TimerSize(float width) => width >= 480 ? 70 : width >= 400 ? 56 : 42;
    public static float BodyInset(float width) => width < NarrowBelow ? 18 : 24;

    // --- Colors ---
    static readonly Rgba LensA = Rgba.Hex(0xD6F6FF), LensB = Rgba.Hex(0x65CEFA), LensC = Rgba.Hex(0x38A9EE), LensD = Rgba.Hex(0x6FC8FA);
    static readonly Rgba BtnA = Rgba.Hex(0x5279CB), BtnB = Rgba.Hex(0x1E3F8C), BtnC = Rgba.Hex(0x142860);
    static readonly Rgba InsetA = Rgba.Hex(0x132F7A), InsetB = Rgba.Hex(0x060E32);
    static readonly Rgba CasingBorder = Rgba.Hex(0x395DE6), InsetBorder = Rgba.Hex(0x31569B), Edge = Rgba.Hex(0x739EFF);
    static readonly Rgba MainText = Rgba.Hex(0xEDF7FF), Secondary = Rgba.Hex(0xBDD7FF), LensText = Rgba.Hex(0x06245E);
    static readonly Rgba SecondaryButtonText = Rgba.Hex(0xEDF7FF), LiveText = Rgba.Hex(0xBDF29B), LiveDot = Rgba.Hex(0xA9E34F);
    static readonly Rgba White = Rgba.Hex(0xFFFFFF), RuleColor = new(.30f, .42f, .78f, .75f);

    // High contrast palette (system colors, or the HC Black theme for fixture images).
    Rgba hcWindow, hcText, hcFace, hcBtnText, hcHighlight, hcHighlightText, hcGray;
    bool forcedHc, fixedTextScale;

    static unsafe float ReadTextScale()
    {
        uint percent = 0, size = 4;
        fixed (char* key = @"Software\Microsoft\Accessibility")
        fixed (char* value = "TextScaleFactor")
        {
            // HKEY_CURRENT_USER, REG_DWORD only; missing means 100 %.
            var rc = Native.RegGetValueW(unchecked((nint)0x80000001), key, value, 0x10, null, &percent, &size);
            return rc == 0 ? Math.Clamp((int)percent, 100, 225) / 100f : 1f;
        }
    }

    /// <summary>Fixture images only: ignore the PC's text-size setting so the images are reproducible.</summary>
    public void FixTextScale(float scale)
    {
        fixedTextScale = true;
        TextScale = scale;
    }

    public bool HighContrast { get; private set; }
    public float TextScale { get; set; } = 1f;

    /// <summary>Fixture images only: dark dotted surround instead of the plain casing color outside the shape.</summary>
    public bool FixtureSurround { get; set; }

    public static Skin Current { get; } = new();

    /// <summary>Re-reads the system high-contrast state and the Windows "Text size" setting (100–225 %).</summary>
    public unsafe void Refresh()
    {
        if (!fixedTextScale) TextScale = ReadTextScale();
        var hc = new Native.HIGHCONTRAST { cbSize = (uint)sizeof(Native.HIGHCONTRAST) };
        var on = Native.SystemParametersInfoW(Native.SPI_GETHIGHCONTRAST, hc.cbSize, &hc, 0) != 0
                 && (hc.dwFlags & Native.HCF_HIGHCONTRASTON) != 0;
        if (on) { HighContrast = true; LoadSystemColors(); }
        else if (!forcedHc) HighContrast = false;
    }

    /// <summary>Fixture images only: draw the high-contrast path even when Windows is not in high contrast.</summary>
    public void ForceHighContrast(bool on)
    {
        forcedHc = on;
        HighContrast = on;
        if (!on) { Refresh(); return; }
        // HC Black theme colors.
        hcWindow = Rgba.Hex(0x000000); hcText = Rgba.Hex(0xFFFFFF); hcFace = Rgba.Hex(0x000000); hcBtnText = Rgba.Hex(0xFFFFFF);
        hcHighlight = Rgba.Hex(0x1AEBFF); hcHighlightText = Rgba.Hex(0x000000); hcGray = Rgba.Hex(0x3FF23F);
    }

    void LoadSystemColors()
    {
        static Rgba C(int i) => Rgba.FromColorRef(Native.GetSysColor(i));
        hcWindow = C(Native.COLOR_WINDOW); hcText = C(Native.COLOR_WINDOWTEXT); hcFace = C(Native.COLOR_BTNFACE);
        hcBtnText = C(Native.COLOR_BTNTEXT); hcHighlight = C(Native.COLOR_HIGHLIGHT);
        hcHighlightText = C(Native.COLOR_HIGHLIGHTTEXT); hcGray = C(Native.COLOR_GRAYTEXT);
    }

    // --- Text ---
    public (float Size, int Weight, bool Italic) Font(TextRole role) => role switch
    {
        TextRole.Heading => (HeadingSize * TextScale, Regular, false),
        TextRole.Goodbye => (GoodbyeSize * TextScale, Regular, false),
        TextRole.Detail or TextRole.Live => (DetailSize * TextScale, Regular, false),
        TextRole.Button => (ButtonSize * TextScale, SemiBold, false),
        _ => (BodySize * TextScale, Regular, false),
    };

    Rgba TextColor(TextRole role) => HighContrast ? hcText : role switch
    {
        TextRole.Detail => Secondary,
        TextRole.Live => LiveText,
        _ => MainText,
    };

    public (float W, float H) Measure(TextRole role, string text, float width)
    {
        var (size, weight, italic) = Font(role);
        return Gfx.Measure(text, size, weight, italic, width);
    }

    public void PaintText(Painter p, TextRole role, string text, RectF r, HAlign align = HAlign.Left, bool dot = false)
    {
        var (size, weight, italic) = Font(role);
        if (dot && !HighContrast)
        {
            // Green recording dot with a soft glow, just before the text.
            var tw = Gfx.Measure(text, size, weight, italic, r.W).W;
            var textLeft = align == HAlign.Right ? r.R - tw : r.L;
            var cx = textLeft - 14; var cy = r.T + Math.Min(r.H, size * 1.4f) / 2;
            using (var glow = Gfx.RoundedPath(new RectF(cx - 8, cy - 8, cx + 8, cy + 8), Radii.All(8)))
                Gfx.FillPath(p.Rt, glow.P, p.Solid(new Rgba(LiveDot.R, LiveDot.G, LiveDot.B, .28f)));
            using var d = Gfx.RoundedPath(new RectF(cx - 4, cy - 4, cx + 4, cy + 4), Radii.All(4));
            Gfx.FillPath(p.Rt, d.P, p.Solid(LiveDot));
        }

        Gfx.Text(p.Rt, text, r, size, weight, italic, p.Solid(TextColor(role)), align, VAlign.Top, wrap: true);
    }

    public void PaintRule(Painter p, RectF r)
    {
        using var line = Gfx.RoundedPath(new RectF(r.L, r.T, r.R, r.T + 1), Radii.All(0));
        Gfx.FillPath(p.Rt, line.P, p.Solid(HighContrast ? hcText : RuleColor));
    }

    /// <summary>Indeterminate progress: a chunk glides back and forth along the track. <paramref name="phase"/> is 0..1 per sweep cycle.</summary>
    public void PaintIndeterminate(Painter p, RectF r, float phase = 0)
    {
        using var track = Gfx.RoundedPath(r, Radii.All(r.H / 2));
        Gfx.FillPath(p.Rt, track.P, p.Solid(HighContrast ? hcGray : new Rgba(.1f, .2f, .45f, .55f)));
        var t = phase < .5f ? phase * 2 : (1 - phase) * 2;   // 0→1→0
        t = t * t * (3 - 2 * t);                              // ease in/out at both ends
        var cw = r.W * .38f;
        var left = r.L + (r.W - cw) * t;
        var chunk = new RectF(left, r.T, left + cw, r.B);
        using var fill = Gfx.RoundedPath(chunk, Radii.All(r.H / 2));
        if (HighContrast) { Gfx.FillPath(p.Rt, fill.P, p.Solid(hcHighlight)); return; }
        using var g = Gfx.LinearBrush(p.Rt, chunk, 0, 0, 0, 1, [(0f, Rgba.Hex(0xBFE8FF)), (1f, Rgba.Hex(0x5BB0F0))]);
        Gfx.FillPath(p.Rt, fill.P, g.P);
    }

    // --- Shared shell ---
    /// <summary>
    /// Draws casing, header, strip, inset and grip for a window of w×h DIPs. Controls call this
    /// with their own origin so they blend into the backdrop exactly.
    /// </summary>
    public void PaintBackdrop(Painter p, float w, float h, float ox = 0, float oy = 0)
    {
        var rt = p.Rt;
        Gfx.SetOrigin(rt, -ox, -oy);
        var all = new RectF(0, 0, w, h);
        var insetRect = new RectF(CasingLeft, InsetTop, w - CasingRight, h - CasingBottom);

        PaintSurround(p, w, h);
        var casingRadii = Casing(w);
        using (var casing = Gfx.RoundedPath(all, casingRadii))
        {
            if (HighContrast)
            {
                Gfx.FillPath(rt, casing.P, p.Solid(hcWindow));
                Gfx.StrokePath(rt, casing.P, p.Solid(hcText), 3);
            }
            else
            {
                using (var g = Gfx.LinearBrush(rt, all, 0, 0, 1, .35f,
                    [(0f, Rgba.Hex(0x6A86FF)), (.28f, Rgba.Hex(0x3447E8)), (.6f, Rgba.Hex(0x2A28CC)), (.9f, Rgba.Hex(0x2020C4)), (1f, Rgba.Hex(0x2F45E0))]))
                    Gfx.FillPath(rt, casing.P, g.P);
                // Bowed shoulder reflection across the top of the casing.
                using (var sg = Gfx.RadialBrush(rt, w * .5f, 0, w * .62f, 54, [(0f, new Rgba(1, 1, 1, .30f)), (1f, new Rgba(1, 1, 1, 0f))]))
                    Gfx.FillPath(rt, casing.P, sg.P);
                // The window is cut out along this path and the cut cannot be anti-aliased, so the
                // outermost line is a dark contour (hides the stair-steps); the bright bevel sits inside it.
                Gfx.StrokePath(rt, casing.P, p.Solid(Rgba.Hex(0x12148C)), 3);
                using (var bevel = Gfx.RoundedPath(all.Inset(2.25f), Shrink(casingRadii, 2.25f)))
                using (var rim = Gfx.LinearBrush(rt, all, 0, 0, 1, 1,
                    [(0f, new Rgba(.86f, .93f, 1f, .95f)), (.3f, new Rgba(.50f, .62f, 1f, .55f)), (.62f, new Rgba(.23f, .27f, .88f, .25f)), (1f, new Rgba(.55f, .70f, 1f, .85f))]))
                    Gfx.StrokePath(rt, bevel.P, rim.P, 1.5f);
                using var inner = Gfx.RoundedPath(all.Inset(4), Shrink(casingRadii, 4));
                Gfx.StrokePath(rt, inner.P, p.Solid(new Rgba(.07f, .09f, .62f, .35f)), 1);
            }
        }

        PaintHeader(p, w);

        // Inset face: base, sheen, inner shadow along the top, rim light along the bottom.
        using (var inset = Gfx.RoundedPath(insetRect, Inset))
        {
            if (HighContrast)
            {
                Gfx.FillPath(rt, inset.P, p.Solid(hcWindow));
                Gfx.StrokePath(rt, inset.P, p.Solid(hcText), 1);
            }
            else
            {
                using (var g = Gfx.LinearBrush(rt, insetRect, 0, 0, 1, 1, [(0f, InsetA), (1f, InsetB)]))
                    Gfx.FillPath(rt, inset.P, g.P);
                using (var sheen = Gfx.LinearBrush(rt, insetRect, 0, 0, .65f, .75f, [(0f, new Rgba(.40f, .62f, 1f, .30f)), (.55f, new Rgba(.4f, .6f, 1f, .07f)), (1f, new Rgba(.4f, .6f, 1f, 0f))]))
                    Gfx.FillPath(rt, inset.P, sheen.P);
                using (var shade = Gfx.LinearBrush(rt, insetRect, 0, 0, 0, 1, [(0f, new Rgba(0, 0, .1f, .55f)), (.06f, new Rgba(0, 0, .1f, 0f)), (1f, new Rgba(0, 0, .1f, 0f))]))
                    Gfx.StrokePath(rt, inset.P, shade.P, 4);
                // Soft aqua glow rising from the bottom of the display.
                using (var glow = Gfx.RadialBrush(rt, insetRect.L + insetRect.W * .5f, insetRect.B + 12, insetRect.W * .62f, insetRect.H * .5f,
                    [(0f, new Rgba(.30f, .72f, 1f, .26f)), (1f, new Rgba(.30f, .72f, 1f, 0f))]))
                    Gfx.FillPath(rt, inset.P, glow.P);
                using var rim = Gfx.LinearBrush(rt, insetRect, 0, 0, 1, 1,
                    [(0f, new Rgba(.48f, .6f, 1f, .45f)), (.5f, new Rgba(.18f, .25f, .66f, .4f)), (1f, new Rgba(.40f, .52f, 1f, .9f))]);
                Gfx.StrokePath(rt, inset.P, rim.P, 1.5f);
            }
        }

        PaintGrip(p, w, h);
        Gfx.SetOrigin(rt, 0, 0);
    }

    void PaintSurround(Painter p, float w, float h)
    {
        var rt = p.Rt;
        if (!FixtureSurround || HighContrast)
        {
            Gfx.Clear(rt, HighContrast ? hcWindow : Rgba.Hex(0x2A28CC));
            return;
        }

        Gfx.Clear(rt, Rgba.Hex(0x181818));
        var dot = p.Solid(Rgba.Hex(0x2B2B2B));
        for (float y = 10; y < h; y += 20)
            for (float x = 10; x < w; x += 20)
            {
                using var d = Gfx.RoundedPath(new RectF(x - 1, y - 1, x + 1, y + 1), Radii.All(1));
                Gfx.FillPath(rt, d.P, dot);
            }
    }

    void PaintHeader(Painter p, float w)
    {
        var rt = p.Rt;
        var lens = LensRect();
        PaintLens(p, lens);

        var tagX = lens.R + HeaderGap;
        var tagW = w - CasingRight - HeaderInset - CloseSize - HeaderGap - tagX;
        if (tagW > 60)
            Gfx.Text(rt, "a little time, well spent.", new RectF(tagX, CasingTop, tagX + tagW, CasingTop + HeaderHeight),
                TaglineSize * TextScale, Regular, true, p.Solid(HighContrast ? hcText : MainText), HAlign.Left, VAlign.Center, true);

        // Tapered highlight swoosh under the header.
        var strip = RectF.Xywh(CasingLeft + 72, CasingTop + HeaderHeight + RowGap, w - CasingLeft - CasingRight - 72 - 24, 5);
        using var s = Gfx.RoundedPath(strip, Radii.All(2.5f));
        if (HighContrast) { Gfx.FillPath(rt, s.P, p.Solid(hcText)); return; }
        using var g = Gfx.LinearBrush(rt, strip, 0, 0, 1, 0,
            [(0f, new Rgba(.85f, .91f, 1f, .95f)), (.5f, new Rgba(.5f, .64f, 1f, .6f)), (1f, new Rgba(.35f, .43f, .94f, 0f))]);
        Gfx.FillPath(rt, s.P, g.P);
    }

    // Sky-blue glass bubble with the whale inside. It overhangs the casing's top-left curve, so it
    // sits in a dark socket ring that doubles as the window's cut edge there.
    void PaintLens(Painter p, RectF r)
    {
        var rt = p.Rt;
        using var shape = Gfx.RoundedPath(r.Inset(1), LensShape);
        const float whale = .23f;
        float wx = r.L + r.W / 2 - 136 * whale, wy = r.T + r.H / 2 - 113 * whale;
        if (HighContrast)
        {
            Gfx.FillPath(rt, shape.P, p.Solid(hcFace));
            Gfx.StrokePath(rt, shape.P, p.Solid(hcText), 2);
            PaintWhale(p, wx, wy, whale);
            return;
        }

        using (var socket = Gfx.RoundedPath(r.Inset(-2), new Radii(LensShape.TL + 3, LensShape.TR + 3, LensShape.BR + 3, LensShape.BL + 3)))
            Gfx.FillPath(rt, socket.P, p.Solid(Rgba.Hex(0x12148C)));
        using (var body = Gfx.LinearBrush(rt, r, 0, 0, 0, 1,
            [(0f, Rgba.Hex(0xD4F4FF)), (.4f, Rgba.Hex(0x7CD0FB)), (.78f, Rgba.Hex(0x3C9FEC)), (1f, Rgba.Hex(0x4DB4F4))]))
            Gfx.FillPath(rt, shape.P, body.P);
        using (var glow = Gfx.RadialBrush(rt, r.L + r.W / 2, r.B - 4, r.W * .5f, r.H * .42f,
            [(0f, new Rgba(.78f, 1f, .98f, .75f)), (1f, new Rgba(.78f, 1f, .98f, 0f))]))
            Gfx.FillPath(rt, shape.P, glow.P);

        PaintWhale(p, wx, wy, whale);

        // Glass reflection: a crescent that follows the bubble's top edge, over the whale.
        using (var face = Gfx.RoundedPath(r.Inset(4), LensShape))
        using (var cap = Gfx.Ellipse(r.L + r.W / 2, r.T + r.H * .06f, r.W * .5f, r.H * .36f))
        using (var crescent = Gfx.Intersect(face.P, cap.P))
        using (var sg = Gfx.LinearBrush(rt, new RectF(r.L, r.T, r.R, r.T + r.H * .42f), 0, 0, 0, 1, [(0f, new Rgba(1, 1, 1, .78f)), (1f, new Rgba(1, 1, 1, .04f))]))
            Gfx.FillPath(rt, crescent.P, sg.P);
        using var rim = Gfx.LinearBrush(rt, r, 0, 0, 0, 1,
            [(0f, new Rgba(1f, 1f, 1f, .98f)), (.5f, new Rgba(.62f, .86f, 1f, .9f)), (1f, new Rgba(.88f, .98f, 1f, .98f))]);
        Gfx.StrokePath(rt, shape.P, rim.P, 2);
    }

    // Mascot outline in a 256-unit box; the same curve as tools/icons/build_icons.py.
    static readonly float[] WhaleCurve =
    [
        22, 150,
        22, 96, 66, 70, 120, 70,
        160, 70, 186, 90, 200, 114,
        207, 104, 210, 94, 209, 84,
        198, 78, 184, 70, 176, 52,
        193, 50, 206, 56, 213, 67,
        221, 56, 235, 49, 250, 51,
        248, 73, 238, 90, 225, 99,
        232, 150, 204, 220, 122, 222,
        62, 222, 22, 198, 22, 150,
    ];

    /// <summary>The whale, with its 256-unit box placed at (ox,oy) and scaled by s.</summary>
    void PaintWhale(Painter p, float ox, float oy, float s)
    {
        var rt = p.Rt;
        ComRef Dot(float cx, float cy, float r) => Gfx.Ellipse(ox + cx * s, oy + cy * s, r * s, r * s);
        using var body = Gfx.CurvePath(WhaleCurve, ox, oy, s);
        using var eye = Dot(74, 150, 15);
        if (HighContrast)
        {
            Gfx.FillPath(rt, body.P, p.Solid(hcBtnText));
            Gfx.FillPath(rt, eye.P, p.Solid(hcFace));
            return;
        }

        var box = new RectF(ox + 22 * s, oy + 50 * s, ox + 250 * s, oy + 222 * s);
        using (var g = Gfx.LinearBrush(rt, box, 0, 0, .25f, 1,
            [(0f, Rgba.Hex(0x7FD8FF)), (.3f, Rgba.Hex(0x3690F2)), (.68f, Rgba.Hex(0x2A46DC)), (1f, Rgba.Hex(0x2420B4))]))
            Gfx.FillPath(rt, body.P, g.P);
        using (var under = Gfx.Ellipse(ox + 100 * s, oy + 226 * s, 104 * s, 48 * s))
        using (var belly = Gfx.Intersect(body.P, under.P))
        using (var g = Gfx.LinearBrush(rt, box, 0, .7f, 0, 1, [(0f, Rgba.Hex(0xF4FDFF)), (1f, Rgba.Hex(0x9FDCFF))]))
            Gfx.FillPath(rt, belly.P, g.P);
        using (var top = Gfx.Ellipse(ox + 112 * s, oy + 98 * s, 74 * s, 24 * s))
        using (var gloss = Gfx.Intersect(body.P, top.P))
        using (var g = Gfx.LinearBrush(rt, box, 0, .1f, 0, .45f, [(0f, new Rgba(1, 1, 1, .85f)), (1f, new Rgba(1, 1, 1, .05f))]))
            Gfx.FillPath(rt, gloss.P, g.P);
        Gfx.StrokePath(rt, body.P, p.Solid(Rgba.Hex(0x1A2AA8)), 1.25f);

        Gfx.FillPath(rt, eye.P, p.Solid(Rgba.Hex(0x050B3C)));
        using (var glint = Dot(69, 144, 5.5f)) Gfx.FillPath(rt, glint.P, p.Solid(White));

        // Spout bubbles.
        foreach (var (cx, cy, r) in new[] { (104f, 38f, 15f), (133f, 20f, 9f), (78f, 20f, 7f) })
        {
            using var bubble = Dot(cx, cy, r);
            Gfx.FillPath(rt, bubble.P, p.Solid(new Rgba(1, 1, 1, .5f)));
            Gfx.StrokePath(rt, bubble.P, p.Solid(new Rgba(1, 1, 1, .95f)), 1);
        }
    }

    // Row of small grip ticks in the casing's bottom lip.
    void PaintGrip(Painter p, float w, float h)
    {
        if (HighContrast) return;
        const int n = 14;
        var cx = w * .54f;
        for (var i = 0; i < n; i++)
        {
            var x = cx + (i - n / 2f) * 8;
            var a = .25f + .7f * i / (n - 1);
            using var t = Gfx.RoundedPath(RectF.Xywh(x, h - 19, 2, 7), Radii.All(1));
            Gfx.FillPath(p.Rt, t.P, p.Solid(new Rgba(.62f, .74f, 1f, a)));
        }
    }

    public static RectF LensRect() =>
        RectF.Xywh(CasingLeft + HeaderInset, CasingTop + (HeaderHeight - LensHeight) / 2, LensWidth, LensHeight);

    public static RectF CloseRect(float w) =>
        RectF.Xywh(w - CasingRight - HeaderInset - CloseSize, CasingTop + (HeaderHeight - CloseSize) / 2, CloseSize, CloseSize);

    static Radii Shrink(Radii r, float d) => new(Math.Max(0, r.TL - d), Math.Max(0, r.TR - d), Math.Max(0, r.BR - d), Math.Max(0, r.BL - d));

    // --- Controls ---
    /// <summary>Paints one owner-draw button into an already-backdropped DC target. Rect is the control's own (0,0,w,h) in DIPs.</summary>
    public void PaintButton(Painter p, ButtonStyle style, RectF r, string label, uint state, float timerSize = 70)
    {
        var rt = p.Rt;
        bool pressed = (state & Native.ODS_SELECTED) != 0, focus = (state & Native.ODS_FOCUS) != 0, disabled = (state & Native.ODS_DISABLED) != 0;
        var radii = style switch
        {
            ButtonStyle.Close => Radii.All(r.H / 2),
            ButtonStyle.Timer => TimerShape,
            _ => ButtonShape,
        };
        var body = r.Inset(.75f);
        using (var shape = Gfx.RoundedPath(body, radii))
        {
            if (HighContrast) PaintHcButton(p, shape.P, style, pressed, disabled);
            else PaintGlossButton(p, shape.P, style, r, pressed, disabled, (state & Native.ODS_HOTLIGHT) != 0);
        }

        var fg = ForeColor(style, pressed, disabled);
        switch (style)
        {
            case ButtonStyle.Close:
                Gfx.Text(rt, "×", r, 22 * TextScale, SemiBold, false, p.Solid(HighContrast ? fg : White), HAlign.Center, VAlign.Center, false);
                break;
            case ButtonStyle.Timer:
                // Thin digits with a soft drop shadow.
                var tr = new RectF(r.L, r.T, r.R, r.B);
                if (!HighContrast)
                    Gfx.Text(rt, label, new RectF(tr.L, tr.T + 2, tr.R, tr.B + 2), timerSize * TextScale, Light, false, p.Solid(new Rgba(.01f, .04f, .2f, .6f)), HAlign.Center, VAlign.Center, false);
                Gfx.Text(rt, label, tr, timerSize * TextScale, Light, false, p.Solid(HighContrast ? fg : Rgba.Hex(0xDDF4FF)), HAlign.Center, VAlign.Center, false);
                break;
            default:
                Gfx.Text(rt, label, r.Inset(8), ButtonSize * TextScale, style == ButtonStyle.Primary ? SemiBold : Regular, false,
                    p.Solid(fg), HAlign.Center, VAlign.Center, true);
                break;
        }

        if (focus) PaintFocus(p, r, radii);
    }

    Rgba ForeColor(ButtonStyle style, bool pressed, bool disabled)
    {
        if (HighContrast)
            return disabled ? hcGray : style == ButtonStyle.Primary || pressed ? hcHighlightText : hcBtnText;
        return style switch
        {
            ButtonStyle.Primary => LensText,
            ButtonStyle.Timer => MainText,
            _ => SecondaryButtonText,
        };
    }

    void PaintHcButton(Painter p, nint shape, ButtonStyle style, bool pressed, bool disabled)
    {
        Gfx.FillPath(p.Rt, shape, p.Solid(style == ButtonStyle.Primary || pressed ? hcHighlight : hcFace));
        Gfx.StrokePath(p.Rt, shape, p.Solid(disabled ? hcGray : hcText), 2);
    }

    static void PaintGlossButton(Painter p, nint shape, ButtonStyle style, RectF r, bool pressed, bool disabled, bool hot)
    {
        var rt = p.Rt;
        switch (style)
        {
            case ButtonStyle.Primary:
                using (var g = Gfx.LinearBrush(rt, r, 0, 0, .3f, 1, [(0f, LensA), (.42f, LensB), (.46f, LensC), (1f, LensD)]))
                    Gfx.FillPath(rt, shape, g.P);
                break;
            case ButtonStyle.Timer:
                using (var g = Gfx.LinearBrush(rt, r, 0, 0, 0, 1, [(0f, Rgba.Hex(0x1B3B80)), (1f, Rgba.Hex(0x0B1A56))]))
                    Gfx.FillPath(rt, shape, g.P);
                using (var glow = Gfx.LinearBrush(rt, r, 0, 0, .75f, .9f, [(0f, new Rgba(.40f, .62f, .95f, .55f)), (.5f, new Rgba(.3f, .5f, .9f, .08f)), (1f, new Rgba(.3f, .5f, .9f, 0f))]))
                    Gfx.FillPath(rt, shape, glow.P);
                using (var shade = Gfx.LinearBrush(rt, r, 0, 0, 0, 1, [(0f, new Rgba(0, 0, .1f, .5f)), (.08f, new Rgba(0, 0, .1f, 0f)), (1f, new Rgba(0, 0, .1f, 0f))]))
                    Gfx.StrokePath(rt, shape, shade.P, 4);
                break;
            case ButtonStyle.Close:
                using (var g = Gfx.LinearBrush(rt, r, 0, 0, 0, 1, [(0f, Rgba.Hex(0x7AA0FF)), (.5f, Rgba.Hex(0x2A4FE0)), (1f, Rgba.Hex(0x1B33C0))]))
                    Gfx.FillPath(rt, shape, g.P);
                break;
            default:
                using (var g = Gfx.LinearBrush(rt, r, 0, 0, 0, 1, [(0f, BtnA), (.48f, new Rgba(.17f, .31f, .62f)), (.52f, BtnB), (1f, BtnC)]))
                    Gfx.FillPath(rt, shape, g.P);
                break;
        }

        // Bowed gloss cap on the lens-style buttons.
        if (style is ButtonStyle.Primary or ButtonStyle.Secondary or ButtonStyle.Close)
        {
            var strength = style == ButtonStyle.Primary ? .34f : style == ButtonStyle.Close ? .42f : .24f;
            using var cap = Gfx.Ellipse(r.L + r.W / 2, r.T, r.W * .56f, r.H * .5f);
            using var gloss = Gfx.Intersect(shape, cap.P);
            using var g = Gfx.LinearBrush(rt, new RectF(r.L, r.T, r.R, r.T + r.H * .5f), 0, 0, 0, 1, [(0f, new Rgba(1, 1, 1, strength)), (1f, new Rgba(1, 1, 1, .02f))]);
            Gfx.FillPath(rt, gloss.P, g.P);
        }

        if (pressed) Gfx.FillPath(rt, shape, p.Solid(new Rgba(0, 0, .1f, .25f)));
        else if (hot) Gfx.FillPath(rt, shape, p.Solid(new Rgba(1, 1, 1, .12f)));
        if (disabled) Gfx.FillPath(rt, shape, p.Solid(new Rgba(InsetB.R, InsetB.G, InsetB.B, .55f)));
        var edge = style == ButtonStyle.Close ? new Rgba(.7f, .8f, 1f, .85f)
            : style == ButtonStyle.Timer ? Rgba.Hex(0x3F68B8) : Edge;
        Gfx.StrokePath(rt, shape, p.Solid(edge), style == ButtonStyle.Timer ? 1.5f : 1);
    }

    /// <summary>Two-tone ring, visible on light and dark faces and in every theme.</summary>
    void PaintFocus(Painter p, RectF r, Radii radii)
    {
        var outer = HighContrast ? hcHighlight : White;
        var inner = HighContrast ? hcText : LensText;
        using (var a = Gfx.RoundedPath(r.Inset(1), radii)) Gfx.StrokePath(p.Rt, a.P, p.Solid(outer), 2);
        using (var b = Gfx.RoundedPath(r.Inset(3), Shrink(radii, 3))) Gfx.StrokePath(p.Rt, b.P, p.Solid(inner), 1);
    }

    /// <summary>Panel behind the preview player (its content is the player child window).</summary>
    public void PaintPlayerPanel(Painter p, RectF r)
    {
        using var shape = Gfx.RoundedPath(r.Inset(.75f), PlayerShape);
        if (HighContrast)
        {
            Gfx.FillPath(p.Rt, shape.P, p.Solid(hcWindow));
            Gfx.StrokePath(p.Rt, shape.P, p.Solid(hcText), 2);
            return;
        }

        using (var g = Gfx.LinearBrush(p.Rt, r, 0, 0, 1, 1, [(0f, Rgba.Hex(0x2C5C9E)), (.5f, Rgba.Hex(0x13307A)), (1f, Rgba.Hex(0x0B1850))]))
            Gfx.FillPath(p.Rt, shape.P, g.P);
        Gfx.StrokePath(p.Rt, shape.P, p.Solid(Rgba.Hex(0x3F68B8)), 1);
    }
}
