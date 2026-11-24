using System.Globalization;
using Log.Core.Render;

namespace Log.Core.Store;

public enum ShellSamplingMode
{
    Adaptive,
    Random
}

/// <summary>
/// Capture defaults and serialized compatibility fields. Export rendering now
/// uses <see cref="RenderPreset"/>; the old export-choice fields remain here so
/// existing user settings survive upgrades without being applied to new exports.
/// </summary>
public sealed record ShellSettings(
    string SourceMode,
    string? CameraDeviceId,
    bool CaptureCursor,
    ShellSamplingMode SamplingMode,
    int OutputFramesPerSecond,
    bool IncludeElapsedTimer,
    CompositionMode CompositionMode,
    PipCorner PipCorner,
    int PipWidthPercent,
    bool ShowWatermark,
    bool AppearInCaptures)
{
    // Defaults are deliberately opt-out; the app settings adapter persists these
    // fields through the same LocalSettings/fallback-file mapping as capture options.
    public bool StartAtSignInEnabled { get; init; }
    public bool WakeRemindersEnabled { get; init; }
    public bool WakeReminderActiveHoursEnabled { get; init; }
    public TimeOnly? WakeReminderActiveHoursStart { get; init; }
    public TimeOnly? WakeReminderActiveHoursEnd { get; init; }
    public static ShellSettings Defaults { get; } = new(
        "screen",
        null,
        CaptureCursor: false,
        ShellSamplingMode.Adaptive,
        OutputFramesPerSecond: 30,
        IncludeElapsedTimer: true,
        CompositionMode.PictureInPicture,
        PipCorner.BottomRight,
        PipWidthPercent: 28,
        ShowWatermark: false,
        AppearInCaptures: false);

    public ShellSettings Validate()
    {
        var sourceMode = SourceMode is "screen" or "camera" or "screen+camera"
            ? SourceMode
            : Defaults.SourceMode;
        var cameraDeviceId = string.IsNullOrWhiteSpace(CameraDeviceId)
            ? null
            : CameraDeviceId.Trim();
        var samplingMode = Enum.IsDefined(SamplingMode)
            ? SamplingMode
            : Defaults.SamplingMode;
        var compositionMode = CompositionMode is CompositionMode.ScreenOnly
            or CompositionMode.CameraOnly
            or CompositionMode.PictureInPicture
            ? CompositionMode
            : Defaults.CompositionMode;
        var pipCorner = Enum.IsDefined(PipCorner)
            ? PipCorner
            : Defaults.PipCorner;
        var outputFramesPerSecond = OutputFramesPerSecond is 24 or 30 or 60
            ? OutputFramesPerSecond
            : Defaults.OutputFramesPerSecond;

        return this with
        {
            SourceMode = sourceMode,
            CameraDeviceId = cameraDeviceId,
            SamplingMode = samplingMode,
            OutputFramesPerSecond = outputFramesPerSecond,
            CompositionMode = compositionMode,
            PipCorner = pipCorner,
            PipWidthPercent = Math.Clamp(PipWidthPercent, 10, 40)
        };
    }

    public static ShellSettings FromValues(IEnumerable<KeyValuePair<string, object>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var values = entries.ToDictionary(static entry => entry.Key, static entry => entry.Value,
            StringComparer.Ordinal);
        var settings = Defaults with
        {
            SourceMode = ReadString(values, "sourceMode") ?? Defaults.SourceMode,
            CameraDeviceId = ReadString(values, "cameraDeviceId"),
            CaptureCursor = ReadBool(values, "captureCursor") ?? Defaults.CaptureCursor,
            SamplingMode = ReadSamplingMode(values, "samplingMode") ?? Defaults.SamplingMode,
            OutputFramesPerSecond = ReadInt(values, "defaultOutputFramesPerSecond")
                ?? Defaults.OutputFramesPerSecond,
            IncludeElapsedTimer = ReadBool(values, "elapsedTimer") ?? Defaults.IncludeElapsedTimer,
            ShowWatermark = ReadBool(values, "watermark") ?? Defaults.ShowWatermark,
            AppearInCaptures = ReadBool(values, "appearInCaptures") ?? Defaults.AppearInCaptures,
            StartAtSignInEnabled = ReadBool(values, "startAtSignInEnabled") ?? Defaults.StartAtSignInEnabled,
            WakeRemindersEnabled = ReadBool(values, "wakeRemindersEnabled") ?? Defaults.WakeRemindersEnabled,
            WakeReminderActiveHoursEnabled = ReadBool(values, "wakeReminderActiveHoursEnabled")
                ?? Defaults.WakeReminderActiveHoursEnabled,
            WakeReminderActiveHoursStart = ReadTimeOnly(values, "wakeReminderActiveHoursStart"),
            WakeReminderActiveHoursEnd = ReadTimeOnly(values, "wakeReminderActiveHoursEnd"),
            CompositionMode = ReadCompositionMode(values),
            PipCorner = ReadPipCorner(values),
            PipWidthPercent = ReadInt(values, "pipWidthPercent") ?? Defaults.PipWidthPercent
        };
        return settings.Validate();
    }

    public IReadOnlyDictionary<string, object> ToValues()
    {
        var settings = Validate();
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sourceMode"] = settings.SourceMode,
            ["cameraDeviceId"] = settings.CameraDeviceId ?? string.Empty,
            ["captureCursor"] = settings.CaptureCursor,
            ["samplingMode"] = settings.SamplingMode.ToString().ToLowerInvariant(),
            ["defaultOutputFramesPerSecond"] = settings.OutputFramesPerSecond,
            ["elapsedTimer"] = settings.IncludeElapsedTimer,
            ["watermark"] = settings.ShowWatermark,
            ["appearInCaptures"] = settings.AppearInCaptures,
            ["startAtSignInEnabled"] = settings.StartAtSignInEnabled,
            ["wakeRemindersEnabled"] = settings.WakeRemindersEnabled,
            ["wakeReminderActiveHoursEnabled"] = settings.WakeReminderActiveHoursEnabled,
            ["wakeReminderActiveHoursStart"] = FormatTimeOnly(settings.WakeReminderActiveHoursStart),
            ["wakeReminderActiveHoursEnd"] = FormatTimeOnly(settings.WakeReminderActiveHoursEnd),
            ["compositionMode"] = settings.CompositionMode switch
            {
                CompositionMode.CameraOnly => 1,
                CompositionMode.PictureInPicture => 2,
                _ => 0
            },
            ["pictureInPicture"] = settings.CompositionMode == CompositionMode.PictureInPicture,
            ["pipCorner"] = (int)settings.PipCorner,
            ["pipWidthPercent"] = settings.PipWidthPercent
        };
    }

    /// <summary>
    /// Returns whether a local wall-clock time is inside the optional reminder
    /// window. The start is inclusive and the end exclusive. Invalid configured
    /// windows fail closed; an end earlier than the start spans midnight.
    /// </summary>
    public bool IsWithinWakeReminderActiveHours(TimeOnly localTime)
    {
        if (!WakeReminderActiveHoursEnabled)
        {
            return true;
        }

        if (WakeReminderActiveHoursStart is not { } start
            || WakeReminderActiveHoursEnd is not { } end
            || start == end)
        {
            return false;
        }

        return start < end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }

    private static TimeOnly? ReadTimeOnly(IReadOnlyDictionary<string, object> values, string key)
    {
        var value = ReadString(values, key);
        return TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var result)
            ? result
            : null;
    }

    private static string FormatTimeOnly(TimeOnly? time)
    {
        return time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
    }
    private static string? ReadString(IReadOnlyDictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var value) && value is bool boolean ? boolean : null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var value) && value is int integer ? integer : null;
    }

    private static ShellSamplingMode? ReadSamplingMode(
        IReadOnlyDictionary<string, object> values,
        string key)
    {
        var value = ReadString(values, key);
        return value?.ToLowerInvariant() switch
        {
            "adaptive" => ShellSamplingMode.Adaptive,
            "random" => ShellSamplingMode.Random,
            _ => null
        };
    }

    private static CompositionMode ReadCompositionMode(IReadOnlyDictionary<string, object> values)
    {
        if (ReadInt(values, "compositionMode") is int mode)
        {
            return mode switch
            {
                1 => CompositionMode.CameraOnly,
                2 => CompositionMode.PictureInPicture,
                _ => CompositionMode.ScreenOnly
            };
        }

        return ReadBool(values, "pictureInPicture") == true
            ? CompositionMode.PictureInPicture
            : Defaults.CompositionMode;
    }

    private static PipCorner ReadPipCorner(IReadOnlyDictionary<string, object> values)
    {
        return ReadInt(values, "pipCorner") is int corner
            && corner is >= 0 and <= 3
            ? (PipCorner)corner
            : Defaults.PipCorner;
    }
}
