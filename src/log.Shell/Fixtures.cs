using System.IO.Compression;
using static Log.Shell.Native;

namespace Log.Shell;

/// <summary>
/// Hidden <c>--render-fixtures &lt;folder&gt;</c>: draws every screen with fake data into its own
/// window and saves it with PrintWindow. Never reachable from normal use.
/// </summary>
internal static unsafe class Fixtures
{
    static readonly (string Name, ScreenId Id)[] Screens =
    [
        ("invitation", ScreenId.Invitation), ("source", ScreenId.Source), ("recording", ScreenId.Recording),
        ("goodbye", ScreenId.Goodbye), ("finishing", ScreenId.Finishing), ("preview", ScreenId.Preview),
        ("saved", ScreenId.Saved), ("consent-cancelled", ScreenId.ConsentCancelled),
        ("source-lost", ScreenId.SourceLost), ("save-error", ScreenId.SaveError), ("discard", ScreenId.Discard),
    ];

    static readonly ViewData Fake = new()
    {
        Source = "Entire display",
        Elapsed = new TimeSpan(0, 24, 18),
        SavedPath = "Videos / log / session-2026-11-05.mp4",
        Notice = "",
        PauseText = "source unavailable",
        ErrorDetail = "The folder is not available.",
    };

    // The finishing screen shows its post-stop line, as it does once the engine has stopped.
    static ViewData FakeFor(ScreenId id) =>
        id == ScreenId.Finishing ? Fake with { Notice = "Recording has stopped. Your source is kept on this device." } : Fake;

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        var skin = Skin.Current;
        skin.FixtureSurround = true;
        skin.FixTextScale(1f);
        var complete = true;
        foreach (var hc in new[] { false, true })
        {
            skin.ForceHighContrast(hc);
            foreach (var scale in hc ? new[] { 1f } : new[] { 1f, 1.5f, 2f })
                foreach (var narrow in new[] { false, true })
                    foreach (var (name, id) in Screens)
                    {
                        var width = narrow ? Skin.MinWidth : (id == ScreenId.Invitation ? Skin.InvitationWidth : Skin.PreferredWidth);
                        var file = $"{name}{(hc ? "-hc" : "")}-{(int)(scale * 100)}-{(int)width}.png";
                        complete &= RenderOne(Path.Combine(folder, file), id, scale, width);
                    }
        }

        skin.ForceHighContrast(false);
        return complete ? 0 : 1;
    }

    /// <summary>Hidden <c>--fixture-window &lt;screen&gt; [video]</c>: one fake-data screen as a live window, for UI Automation and player checks.</summary>
    public static int Hold(string screenName, string? video)
    {
        var id = Screens.FirstOrDefault(s => s.Name == screenName).Id;
        using var w = new SurfaceWindow { PlayerPath = video };
        w.Hook = (uint m, nint wp, nint lp, out nint r) =>
        {
            r = 0;
            if (m == Ui.WmRun) { Ui.Drain(); return true; }
            if (m == WM_TIMER && wp == 7) { AppLog.Write("player " + w.PlayerStatus()); return true; }
            if (m != WM_CLOSE) return false;
            PostQuitMessage(0);
            return true;
        };
        w.Command = c => { if (c == Cmd.Close) PostQuitMessage(0); };
        Ui.Init(w.Handle);
        w.Present(id, FakeFor(id), focusPrimary: true);
        w.Place(bottomRight: false);
        w.Show(activate: true);
        w.FocusPrimary();
        SetTimer(w.Handle, 7, 2000, 0);
        MSG m;
        while (GetMessageW(&m, 0, 0, 0) > 0)
        {
            if (m.message == WM_KEYDOWN && GetParent(GetFocus()) == w.Handle && m.wParam == VK_RETURN) { SendMessageW(GetFocus(), BM_CLICK, 0, 0); continue; }
            if (IsDialogMessageW(w.Handle, &m) != 0) continue;
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }

        return 0;
    }

    static bool RenderOne(string path, ScreenId id, float scale, float width)
    {
        using var w = new SurfaceWindow(fixture: true, scaleOverride: scale) { WidthOverride = width };
        w.Present(id, FakeFor(id));
        SetWindowPos(w.Handle, 0, 60, 60, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        w.Show(activate: false);
        Pump(250);
        RedrawWindow(w.Handle, null, 0, 0x1 | 0x4 | 0x80 | 0x100); // invalidate, now, all children
        Pump(150);
        var pixels = w.Capture(out var pw, out var ph);
        if (pixels is null) return false;
        File.WriteAllBytes(path, Png(pixels, pw, ph));
        return true;
    }

    static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        MSG m;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessageW(&m, 0, 0, 0, 1) != 0) { TranslateMessage(&m); DispatchMessageW(&m); }
            Thread.Sleep(10);
        }
    }

    // --- Minimal PNG writer (BGRA in, RGB out) ---
    static byte[] Png(byte[] bgra, int w, int h)
    {
        var raw = new byte[(w * 3 + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var o = y * (w * 3 + 1);
            raw[o] = 0;
            for (var x = 0; x < w; x++)
            {
                var s = (y * w + x) * 4;
                raw[o + 1 + x * 3] = bgra[s + 2];
                raw[o + 2 + x * 3] = bgra[s + 1];
                raw[o + 3 + x * 3] = bgra[s];
            }
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", idat.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) td[i] = (byte)type[i];
        data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4]; Be(crc, 0, (int)Crc32(td));
        s.Write(crc);
    }

    static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c ^ 0xFFFFFFFFu;
    }
}
