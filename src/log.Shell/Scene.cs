using System.Globalization;

namespace Log.Shell;

internal enum ScreenId { Invitation, Source, Recording, Goodbye, Finishing, Preview, Saved, ConsentCancelled, SourceLost, SaveError, Discard }

/// <summary>Control ids. Buttons send WM_COMMAND with these.</summary>
internal static class Cmd
{
    public const int Close = 1001, NotNow = 1002, LetsGo = 1003, Back = 1004, ChooseSource = 1005,
        Timer = 1007, KeepGoing = 1008, FinishPreview = 1009, KeepLater = 1010, SaveTimelapse = 1011,
        OpenFile = 1012, Done = 1013, TryAgain = 1014, FinishSession = 1015, ChooseFolder = 1016, PlayPause = 1017,
        Discard = 1018, KeepIt = 1019, DiscardConfirm = 1020;
    public const int TextBase = 2000;
}

internal enum ItemKind { Text, Button, Progress, Player, Rule }

internal sealed record Item(ItemKind Kind, int Id, string Text, RectF Rect)
{
    public TextRole Role { get; init; } = TextRole.Body;
    public ButtonStyle Style { get; init; } = ButtonStyle.Secondary;
    public HAlign Align { get; init; } = HAlign.Left;
    /// <summary>Accessible name when it differs from the visible text.</summary>
    public string? Name { get; init; }
    public bool Primary { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>"clock" marks the elapsed readout, which is updated in place every second.</summary>
    public string? Tag { get; init; }
    /// <summary>Draws the green recording dot before the text.</summary>
    public bool Dot { get; init; }
}

/// <summary>One laid-out screen, in DIPs. Pure data, so layout is testable without a window.</summary>
internal sealed class Scene
{
    public ScreenId Screen;
    public float Width, Height;
    public string Title = "";
    public readonly List<Item> Items = [];
}

/// <summary>Facts the screens show. Everything here comes from the engine or the saved file, never invented.</summary>
internal sealed record ViewData
{
    public string Source { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public string Notice { get; init; } = "";
    public string SavedPath { get; init; } = "";
    public string PauseText { get; init; } = "source unavailable";
    public bool CanOpenFile { get; init; } = true;
    public string ErrorDetail { get; init; } = "";
    /// <summary>A consent prompt, preview or save is in progress: actions are disabled so they cannot be repeated.</summary>
    public bool Busy { get; init; }
    /// <summary>False when the in-window player is unavailable; the preview then opens in the default player.</summary>
    public bool PreviewInPlayer { get; init; } = true;
    /// <summary>The preview was found after a restart, so it can also be discarded.</summary>
    public bool Recovered { get; init; }
}

internal static class Format
{
    /// <summary>Always HH:MM:SS, like 00:24:18.</summary>
    public static string Clock(TimeSpan t)
    {
        var total = (long)Math.Max(0, t.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}");
    }

    /// <summary>"Videos / log / file.mp4" for a saved path; other places show every folder.</summary>
    public static string Crumbs(string fullPath, string videosDir)
    {
        var path = fullPath.Replace('/', '\\');
        var videos = videosDir.TrimEnd('\\');
        if (videos.Length > 0 && path.StartsWith(videos + "\\", StringComparison.OrdinalIgnoreCase))
            path = "Videos\\" + path[(videos.Length + 1)..];
        return string.Join(" / ", path.Split('\\', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"24 minutes of focus. Ready to take with you."</summary>
    public static string Focus(TimeSpan t)
    {
        var spoken = Spoken(t);
        return char.ToUpperInvariant(spoken[0]) + spoken[1..] + " of focus. Ready to take with you.";
    }

    /// <summary>Spoken duration at minute granularity, so a screen reader is not told every second.</summary>
    public static string Spoken(TimeSpan t)
    {
        var total = (long)Math.Max(0, t.TotalSeconds);
        var h = total / 3600; var m = total / 60 % 60;
        if (h == 0 && m == 0) return "less than a minute";
        var parts = new List<string>();
        if (h > 0) parts.Add(h == 1 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{h} hours"));
        if (m > 0) parts.Add(m == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{m} minutes"));
        return string.Join(" ", parts);
    }
}

/// <summary>Turns a screen id plus facts into positioned items. Copy is exactly as in DESIGN.md.</summary>
internal static class Layout
{
    public static float WidthFor(ScreenId id, float available) =>
        Math.Clamp(id == ScreenId.Invitation ? Skin.InvitationWidth : Skin.PreferredWidth, Skin.MinWidth, Math.Max(Skin.MinWidth, available));

    public delegate (float W, float H) Measure(TextRole role, string text, float width);

    public static Scene Build(ScreenId id, ViewData v, float width, Measure measure, string? timerName = null)
    {
        var s = new Scene { Screen = id, Width = width };
        var pad = Skin.BodyInset(width);
        var x = Skin.CasingLeft + pad;
        var cw = width - 2 * Skin.CasingLeft - 2 * pad;
        var y = Skin.InsetTop + pad;
        var textId = Cmd.TextBase;

        float Text(TextRole role, string text, HAlign align = HAlign.Left, bool dot = false, float maxWidth = 0, bool advance = true, string? tag = null, string? name = null)
        {
            var w = maxWidth > 0 ? Math.Min(maxWidth, cw) : cw;
            var h = (float)Math.Ceiling(measure(role, text, w).H) + 2;
            var left = align == HAlign.Right ? x + cw - w : x;
            s.Items.Add(new Item(ItemKind.Text, textId++, text, RectF.Xywh(left, y, w, h)) { Role = role, Align = align, Dot = dot, Tag = tag, Name = name });
            if (advance) y += h + Skin.BodyGap;
            return h;
        }

        void Rule()
        {
            s.Items.Add(new Item(ItemKind.Rule, textId++, "", RectF.Xywh(x, y, cw, 1)));
            y += 1 + Skin.BodyGap;
        }

        // Preserve label widths first. Stack the actions when even compact padding cannot fit.
        void Buttons(params (int Id, string Text, bool Primary)[] defs)
        {
            const float gap = 12;
            var widths = defs.Select(d => Math.Max(Skin.ButtonMinWidth, measure(TextRole.Button, d.Text, 1000).W + 44)).ToArray();
            var gaps = gap * (defs.Length - 1);
            if (widths.Sum() + gaps > cw)
                widths = defs.Select(d => MathF.Ceiling(measure(TextRole.Button, d.Text, 1000).W) + 20).ToArray();
            var stacked = widths.Sum() + gaps > cw;
            var left = stacked ? x : x + cw - widths.Sum() - gaps;
            for (var i = 0; i < defs.Length; i++)
            {
                var w = stacked ? cw : widths[i];
                var h = Math.Max(Skin.ButtonHeight, MathF.Ceiling(measure(TextRole.Button, defs[i].Text, w - 16).H) + 16);
                s.Items.Add(new Item(ItemKind.Button, defs[i].Id, defs[i].Text, RectF.Xywh(left, y, w, h))
                { Style = defs[i].Primary ? ButtonStyle.Primary : ButtonStyle.Secondary, Primary = defs[i].Primary, Enabled = !v.Busy });
                if (stacked) y += h + (i + 1 == defs.Length ? Skin.BodyGap : gap);
                else left += w + gap;
            }

            if (!stacked) y = s.Items.TakeLast(defs.Length).Max(item => item.Rect.B) + Skin.BodyGap;
        }

        switch (id)
        {
            case ScreenId.Invitation:
                s.Title = "Wanna lock in?";
                Text(TextRole.Heading, "Wanna lock in?");
                Text(TextRole.Body, "A little time for what matters.");
                Buttons((Cmd.NotNow, "Not now", false), (Cmd.LetsGo, "Let's go", true));
                break;

            case ScreenId.Source:
                {
                    s.Title = "Your time starts here.";
                    Text(TextRole.Heading, "Your time starts here.");
                    Text(TextRole.Body, "Windows will ask which screen or window to share. Recording starts after you confirm.");
                    if (v.Notice.Length > 0) Text(TextRole.Detail, v.Notice);
                    Buttons((Cmd.Back, "Back", false), (Cmd.ChooseSource, "Choose source…", true));
                    break;
                }

            case ScreenId.Recording:
                {
                    s.Title = "Recording";
                    Text(TextRole.Detail, "Recording · " + v.Source, HAlign.Right, dot: true);
                    var th = (float)Math.Round(Skin.TimerSize(width) * 1.9);
                    s.Items.Add(new Item(ItemKind.Button, Cmd.Timer, Format.Clock(v.Elapsed), RectF.Xywh(x, y, cw, th))
                    { Style = ButtonStyle.Timer, Tag = "clock", Name = timerName ?? $"Finish recording, elapsed {Format.Spoken(v.Elapsed)}" });
                    y += th + Skin.BodyGap;
                    Text(TextRole.Body, "Click the timer to finish", HAlign.Center);
                    Rule();
                    Text(TextRole.Detail, "Saved on this device. Close this window to keep going.");
                    break;
                }

            case ScreenId.Goodbye:
                {
                    s.Title = "Sure you wanna say goodbye?";
                    var clock = Format.Clock(v.Elapsed);
                    var clockW = (float)Math.Ceiling(measure(TextRole.Live, clock, 1000).W) + 4;
                    Text(TextRole.Live, clock, HAlign.Right, maxWidth: clockW, advance: false, tag: "clock", name: $"Elapsed {Format.Spoken(v.Elapsed)}");
                    Text(TextRole.Live, $"Still recording · {v.Source}", maxWidth: cw - clockW - 12);
                    Text(TextRole.Goodbye, "Sure you wanna say goodbye?");
                    Text(TextRole.Body, "Keep this session as a timelapse.");
                    Buttons((Cmd.KeepGoing, "Keep going", false), (Cmd.FinishPreview, "Finish & preview", true));
                    break;
                }

            case ScreenId.Finishing:
                s.Title = "Finishing your session…";
                Text(TextRole.Heading, "Finishing your session…");
                Text(TextRole.Body, v.Notice.Length > 0 ? v.Notice : "Finishing the recording…");
                s.Items.Add(new Item(ItemKind.Progress, textId++, "Finishing", RectF.Xywh(x, y, cw, 8)));
                y += 8 + Skin.BodyGap;
                break;

            case ScreenId.Preview:
                {
                    s.Title = "A little time, kept.";
                    Text(TextRole.Heading, "A little time, kept.");
                    Text(TextRole.Detail, Format.Focus(v.Elapsed));
                    var ph = (float)Math.Round(cw * 9 / 16);
                    s.Items.Add(new Item(ItemKind.Player, Cmd.PlayPause, "Preview", RectF.Xywh(x, y, cw, ph)));
                    if (!v.PreviewInPlayer)
                    {
                        // Fallback: no in-window player, so offer the default player instead.
                        var bw = Math.Max(Skin.ButtonMinWidth, measure(TextRole.Button, "Open file", 1000).W + 44);
                        s.Items.Add(new Item(ItemKind.Button, Cmd.OpenFile, "Open file", RectF.Xywh(x + (cw - bw) / 2, y + (ph - Skin.ButtonHeight) / 2, bw, Skin.ButtonHeight))
                        { Style = ButtonStyle.Secondary });
                    }

                    y += ph + Skin.BodyGap;
                    if (v.Notice.Length > 0) Text(TextRole.Detail, v.Notice);
                    if (v.Recovered)
                        Buttons((Cmd.Discard, "Discard", false), (Cmd.KeepLater, "Keep for later", false), (Cmd.SaveTimelapse, "Save timelapse", true));
                    else
                        Buttons((Cmd.KeepLater, "Keep for later", false), (Cmd.SaveTimelapse, "Save timelapse", true));
                    break;
                }

            case ScreenId.Saved:
                s.Title = "All yours.";
                Text(TextRole.Heading, "All yours.");
                Text(TextRole.Body, "Your timelapse is saved.");
                Rule();
                Text(TextRole.Body, v.SavedPath);
                if (v.CanOpenFile) Buttons((Cmd.OpenFile, "Open file", false), (Cmd.Done, "Done", true));
                else Buttons((Cmd.Done, "Done", true));
                break;

            case ScreenId.ConsentCancelled:
                s.Title = "No rush.";
                Text(TextRole.Heading, "No rush.");
                Text(TextRole.Body, "Nothing was recorded. Choose a source whenever you're ready.");
                Buttons((Cmd.NotNow, "Not now", false), (Cmd.ChooseSource, "Choose source…", true));
                break;

            case ScreenId.SourceLost:
                s.Title = "Paused · " + v.PauseText;
                Text(TextRole.Heading, "Paused · " + v.PauseText);
                Text(TextRole.Body, "Your session is safe.");
                if (v.Notice.Length > 0) Text(TextRole.Detail, v.Notice);
                Buttons((Cmd.FinishSession, "Finish session", false), (Cmd.TryAgain, "Try again", true));
                break;

            case ScreenId.SaveError:
                s.Title = "Couldn't save there.";
                Text(TextRole.Heading, "Couldn't save there.");
                Text(TextRole.Body, "Your recording is still on this device.");
                if (v.ErrorDetail.Length > 0) Text(TextRole.Detail, v.ErrorDetail);
                Buttons((Cmd.KeepLater, "Keep for later", false), (Cmd.ChooseFolder, "Choose folder…", true));
                break;

            case ScreenId.Discard:
                s.Title = "Delete this recording?";
                Text(TextRole.Heading, "Delete this recording?");
                Text(TextRole.Body, "It will be removed from this device.");
                Text(TextRole.Detail, "This can't be undone.");
                Buttons((Cmd.DiscardConfirm, "Delete", false), (Cmd.KeepIt, "Keep it", true));
                break;
        }

        // Header close control is on every screen.
        s.Items.Add(new Item(ItemKind.Button, Cmd.Close, "×", Skin.CloseRect(width)) { Style = ButtonStyle.Close, Name = "Close" });

        // y includes the trailing body gap; the inset ends one inset padding below the last item.
        s.Height = (float)Math.Ceiling(y - Skin.BodyGap + pad + Skin.CasingBottom);
        return s;
    }
}
