namespace Log.Core.Shell;

public enum TrayStatus
{
    Idle,
    Recording,
    Paused,
    Finishing,
    Error
}

public enum TrayMenuAction
{
    OpenLog = 1,
    FinishRecording = 2,
    Settings = 3,
    UnfinishedSaves = 4,
    Exit = 5
}

public sealed record TrayMenuItem(
    TrayMenuAction Action,
    string Label,
    bool IsEnabled);

public sealed record TrayMenuPresentation(
    TrayStatus Status,
    string ToolTip,
    IReadOnlyList<TrayMenuItem> Items);

/// <summary>Pure mapping from the shell's published state to its tray actions and status text.</summary>
public static class TrayMenuModel
{
    public static TrayMenuPresentation Create(string engineState, bool canFinish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineState);
        var status = engineState.Trim().ToLowerInvariant() switch
        {
            "recording" => TrayStatus.Recording,
            "paused" => TrayStatus.Paused,
            "stopping" or "saving" or "exporting" or "finishing" => TrayStatus.Finishing,
            "error" => TrayStatus.Error,
            "ready" or "idle" or "awaitingconsent" or "reviewing" => TrayStatus.Idle,
            _ => TrayStatus.Error
        };
        var finishEnabled = canFinish && status is (TrayStatus.Recording or TrayStatus.Paused);
        var label = status switch
        {
            TrayStatus.Recording => "Recording",
            TrayStatus.Paused => "Paused",
            TrayStatus.Finishing => "Finishing",
            TrayStatus.Error => "Error",
            _ => "Idle"
        };

        var items = new List<TrayMenuItem>
        {
            new(TrayMenuAction.OpenLog, "Open log", IsEnabled: true)
        };
        if (finishEnabled)
            items.Add(new TrayMenuItem(TrayMenuAction.FinishRecording, "Finish recording", IsEnabled: true));
        items.Add(new TrayMenuItem(TrayMenuAction.Settings, "Settings", IsEnabled: true));
        items.Add(new TrayMenuItem(TrayMenuAction.UnfinishedSaves, "Unfinished saves", IsEnabled: true));
        items.Add(new TrayMenuItem(TrayMenuAction.Exit, "Exit", IsEnabled: true));
        return new TrayMenuPresentation(status, $"log · {label}", items);
    }
}
