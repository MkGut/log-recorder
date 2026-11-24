using Log.Core.Shell;

namespace Log.Shell;

/// <summary>Counts only the time the engine was actually recording.</summary>
internal sealed class ActiveClock(Func<TimeSpan> now)
{
    TimeSpan accumulated;
    TimeSpan? startedAt;

    public void Running() => startedAt ??= now();

    public void Stopped()
    {
        if (startedAt is { } s) { accumulated += now() - s; startedAt = null; }
    }

    public void Reset() { accumulated = TimeSpan.Zero; startedAt = null; }

    public TimeSpan Elapsed => accumulated + (startedAt is { } s ? now() - s : TimeSpan.Zero);
}

/// <summary>
/// Which screen to show and why. Every transition names the fact that allows it
/// (consent given, engine started, file verified), so navigation can never
/// manufacture engine state. No window, no engine: plain logic.
/// </summary>
internal sealed class ShellModel
{
    GoodbyeDecisionModel goodbye = new(); // one per capture, see EngineStarted

    public ScreenId Screen { get; private set; } = ScreenId.Invitation;
    public bool ConsentPending { get; private set; }
    public bool Busy { get; private set; }
    public string Source { get; private set; } = "";
    public TimeSpan Elapsed { get; private set; }
    public string Notice { get; private set; } = "";
    public string PauseText { get; private set; } = "";
    public string ErrorDetail { get; private set; } = "";
    public string? PreviewPath { get; private set; }
    public string? SavedPath { get; private set; }

    /// <summary>The preview on screen was found on disk after a restart (kept for later, or a recording that never finished).</summary>
    public bool Recovered { get; private set; }

    /// <summary>True only after the engine reported that capture started.</summary>
    public bool CaptureStarted { get; private set; }

    public bool CanOpenFile { get; set; } = true;

    public void SetElapsed(TimeSpan elapsed) => Elapsed = elapsed;

    public bool LetsGo()
    {
        if (Screen != ScreenId.Invitation) return false;
        Screen = ScreenId.Source;
        Notice = "";
        return true;
    }

    /// <summary>Back / Not now. Never while a consent prompt is open.</summary>
    public bool Dismiss()
    {
        if (ConsentPending || Screen is not (ScreenId.Invitation or ScreenId.Source or ScreenId.ConsentCancelled)) return false;
        Screen = ScreenId.Invitation;
        Notice = "";
        return true;
    }

    public bool BeginConsent()
    {
        if (ConsentPending || Screen is not (ScreenId.Source or ScreenId.ConsentCancelled)) return false;
        ConsentPending = true;
        Screen = ScreenId.Source;
        Notice = "Waiting for Windows to confirm what to share…";
        return true;
    }

    public bool ConsentCancelled()
    {
        if (!ConsentPending) return false;
        ConsentPending = false;
        Notice = "";
        Screen = ScreenId.ConsentCancelled;
        return true;
    }

    /// <summary>Consent or start failed: capture is off and the source menu stays usable.</summary>
    public bool ConsentFailed(string message)
    {
        if (!ConsentPending) return false;
        ConsentPending = false;
        Notice = message;
        Screen = ScreenId.Source;
        return true;
    }

    /// <summary>The engine says capture is running. The only way into the Recording screen.</summary>
    public bool EngineStarted(string source)
    {
        if (!ConsentPending) return false;
        ConsentPending = false;
        CaptureStarted = true;
        goodbye = new(); // the last capture left it in Finishing
        Source = source;
        Elapsed = TimeSpan.Zero;
        Notice = "";
        Screen = ScreenId.Recording;
        return true;
    }

    public bool TimerClicked()
    {
        if (Screen != ScreenId.Recording || !CaptureStarted) return false;
        goodbye.Apply(GoodbyeDecisionAction.TimerClicked);
        Screen = ScreenId.Goodbye;
        return true;
    }

    /// <summary>Keep going, Escape or closing the window: back to the timer, capture untouched.</summary>
    public bool KeepGoing(GoodbyeDecisionAction how = GoodbyeDecisionAction.KeepGoing)
    {
        if (Screen != ScreenId.Goodbye) return false;
        goodbye.Apply(how);
        Screen = ScreenId.Recording;
        return true;
    }

    /// <summary>Returns true exactly once per capture: the caller must stop the engine now.</summary>
    public bool FinishConfirmed()
    {
        if (Screen == ScreenId.Goodbye)
        {
            if (!goodbye.Apply(GoodbyeDecisionAction.FinishAndPreview).ShouldStopCapture) return false;
        }
        else if (Screen != ScreenId.SourceLost)
        {
            return false;
        }

        Screen = ScreenId.Finishing;
        Notice = "";
        return true;
    }

    /// <summary>The capture ended without the user asking (writer or source failure); go on to finish what was captured.</summary>
    public bool CaptureFailed(string notice)
    {
        if (!IsCapturing) return false;
        Screen = ScreenId.Finishing;
        Notice = notice;
        return true;
    }

    /// <summary>The engine finished stopping; the source frames are durable on disk.</summary>
    public bool CaptureStopped()
    {
        if (Screen != ScreenId.Finishing) return false;
        CaptureStarted = false;
        Notice = "Recording has stopped. Your source is kept on this device.";
        return true;
    }

    public bool PreviewReady(string path, TimeSpan elapsed)
    {
        if (Screen != ScreenId.Finishing) return false;
        PreviewPath = path;
        Elapsed = elapsed;
        if (!Recovered) Notice = ""; // a found recording keeps its "from earlier" line
        Busy = false;
        Screen = ScreenId.Preview;
        return true;
    }

    public bool PreviewFailed(string message)
    {
        if (Screen != ScreenId.Finishing) return false;
        CaptureStarted = false;
        Recovered = false;
        Notice = message;
        Screen = ScreenId.Source;
        return true;
    }

    public bool SaveStarted()
    {
        if (Busy || Screen is not (ScreenId.Preview or ScreenId.SaveError) || PreviewPath is null) return false;
        Busy = true;
        Notice = "";
        return true;
    }

    /// <summary>Only a file that exists and is non-empty may reach the Saved screen.</summary>
    public bool SaveSucceeded(string path, bool fileVerified)
    {
        if (!Busy || !fileVerified) return false;
        Busy = false;
        SavedPath = path;
        PreviewPath = null;
        Screen = ScreenId.Saved;
        return true;
    }

    public bool SaveFailed(string detail)
    {
        if (!Busy) return false;
        Busy = false;
        ErrorDetail = detail;
        Screen = ScreenId.SaveError;
        return true;
    }

    /// <summary>Hide for now; the rendered preview and source recording stay on disk.</summary>
    public bool KeepForLater() => !Busy && Screen is ScreenId.Preview or ScreenId.SaveError;

    public bool Done()
    {
        if (Screen != ScreenId.Saved) return false;
        Screen = ScreenId.Invitation;
        Recovered = false;
        SavedPath = null;
        Elapsed = TimeSpan.Zero;
        return true;
    }

    // --- Recordings found on disk after a restart ---

    /// <summary>Nothing is recording, being chosen or being saved, so an old recording may be offered.</summary>
    public bool CanOfferUnfinished =>
        !Busy && !ConsentPending && !CaptureStarted && Screen is ScreenId.Invitation or ScreenId.Source or ScreenId.ConsentCancelled;

    /// <summary>Offer a recording from an earlier run: its preview if one exists, otherwise a render of what was captured.</summary>
    public bool UnfinishedFound(string? previewPath, TimeSpan elapsed, string notice)
    {
        if (!CanOfferUnfinished) return false;
        Recovered = true;
        Elapsed = elapsed;
        Notice = notice;
        PreviewPath = previewPath;
        Screen = previewPath is null ? ScreenId.Finishing : ScreenId.Preview;
        return true;
    }

    /// <summary>Discard asks first: it deletes the recording for good.</summary>
    public bool DiscardAsked()
    {
        if (Busy || !Recovered || Screen != ScreenId.Preview) return false;
        Screen = ScreenId.Discard;
        return true;
    }

    /// <summary>Not deleting after all (Keep it, Escape, closing the window).</summary>
    public bool KeepIt()
    {
        if (Busy || Screen != ScreenId.Discard) return false;
        Screen = ScreenId.Preview;
        return true;
    }

    public bool DiscardStarted()
    {
        if (Busy || Screen != ScreenId.Discard) return false;
        Busy = true;
        return true;
    }

    public bool Discarded()
    {
        if (!Busy || Screen != ScreenId.Discard) return false;
        Busy = false;
        Recovered = false;
        PreviewPath = null;
        Elapsed = TimeSpan.Zero;
        Notice = "";
        Screen = ScreenId.Invitation;
        return true;
    }

    public bool DiscardFailed(string detail)
    {
        if (!Busy || Screen != ScreenId.Discard) return false;
        Busy = false;
        Notice = detail;
        Screen = ScreenId.Preview;
        return true;
    }

    /// <summary>The engine paused for a reason the user must look at (source gone, lock, sleep...).</summary>
    public bool SourceLost(string pauseText, string notice = "")
    {
        if (Screen is not (ScreenId.Recording or ScreenId.Goodbye)) return false;
        PauseText = pauseText;
        Notice = notice;
        Screen = ScreenId.SourceLost;
        return true;
    }

    public bool Resumed()
    {
        if (Screen != ScreenId.SourceLost) return false;
        Notice = "";
        Screen = ScreenId.Recording;
        return true;
    }

    /// <summary>A resume attempt failed; stay on the paused screen and say so.</summary>
    public void ResumeFailed(string notice)
    {
        if (Screen == ScreenId.SourceLost) Notice = notice;
    }

    /// <summary>True while a capture exists that the user has not finished.</summary>
    public bool IsCapturing => CaptureStarted && Screen is ScreenId.Recording or ScreenId.Goodbye or ScreenId.SourceLost;

    public ViewData ToView() => new()
    {
        Source = Source,
        Elapsed = Elapsed,
        Notice = Notice,
        // The Saved screen shows "Videos / log / name.mp4"; the full path stays in the model.
        SavedPath = SavedPath is null ? "" : Format.Crumbs(SavedPath, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        PauseText = PauseText,
        CanOpenFile = CanOpenFile,
        ErrorDetail = ErrorDetail,
        Busy = Busy || ConsentPending,
        Recovered = Recovered,
    };
}
