using Log.Core.Shell;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

public sealed class ShellModelTests
{
    static ShellModel Recording()
    {
        var m = new ShellModel();
        Assert.True(m.LetsGo());
        Assert.True(m.BeginConsent());
        Assert.True(m.EngineStarted("Entire display"));
        return m;
    }

    static ShellModel Preview()
    {
        var m = Recording();
        m.TimerClicked();
        m.FinishConfirmed();
        m.CaptureStopped();
        m.PreviewReady("p.mp4", TimeSpan.FromMinutes(5));
        return m;
    }

    static string Screen(ShellModel m) => m.Screen.ToString();

    [Fact]
    public void LetsGoOpensSourceWithoutStartingCapture()
    {
        var m = new ShellModel();
        Assert.True(m.LetsGo());
        Assert.Equal("Source", Screen(m));
        Assert.False(m.CaptureStarted);
    }

    [Fact]
    public void RecordingScreenNeedsTheEngineToHaveStarted()
    {
        var m = new ShellModel();
        m.LetsGo();
        Assert.False(m.EngineStarted("Entire display")); // no consent in flight
        Assert.False(m.CaptureStarted);
        Assert.False(m.TimerClicked());
        Assert.Equal("Source", Screen(m));
    }

    [Fact]
    public void ConsentInFlightDisablesDuplicates()
    {
        var m = new ShellModel();
        m.LetsGo();
        Assert.True(m.BeginConsent());
        Assert.False(m.BeginConsent());
        Assert.False(m.Dismiss());
        Assert.True(m.ToView().Busy);
    }

    [Fact]
    public void CancelledConsentLeavesCaptureOffAndRecoverable()
    {
        var m = new ShellModel();
        m.LetsGo();
        m.BeginConsent();
        Assert.True(m.ConsentCancelled());
        Assert.Equal("ConsentCancelled", Screen(m));
        Assert.False(m.CaptureStarted);
        Assert.True(m.BeginConsent()); // choose source again
    }

    [Fact]
    public void FailedStartReturnsToSourceWithTheReason()
    {
        var m = new ShellModel();
        m.LetsGo();
        m.BeginConsent();
        Assert.True(m.ConsentFailed("Could not start recording. No space."));
        Assert.Equal("Source", Screen(m));
        Assert.Contains("No space", m.Notice);
        Assert.False(m.CaptureStarted);
    }

    [Fact]
    public void TimerClickOpensGoodbyeAndEscapeReturnsWithoutStopping()
    {
        var m = Recording();
        Assert.True(m.TimerClicked());
        Assert.Equal("Goodbye", Screen(m));
        Assert.True(m.KeepGoing(GoodbyeDecisionAction.Escape));
        Assert.Equal("Recording", Screen(m));
        Assert.True(m.CaptureStarted);
    }

    [Fact]
    public void ClosingTheGoodbyeWindowKeepsRecording()
    {
        var m = Recording();
        m.TimerClicked();
        Assert.True(m.KeepGoing(GoodbyeDecisionAction.WindowClosed));
        Assert.Equal("Recording", Screen(m));
    }

    [Fact]
    public void FinishRequestsAStopExactlyOnce()
    {
        var m = Recording();
        m.TimerClicked();
        Assert.True(m.FinishConfirmed());
        Assert.Equal("Finishing", Screen(m));
        Assert.False(m.FinishConfirmed());
    }

    [Fact]
    public void SecondCaptureInTheSameRunCanFinish()
    {
        var m = Preview();
        Assert.True(m.SaveStarted());
        Assert.True(m.SaveSucceeded("saved.mp4", fileVerified: true));
        Assert.True(m.Done());
        Assert.True(m.LetsGo());
        Assert.True(m.BeginConsent());
        Assert.True(m.EngineStarted("Entire display"));
        Assert.True(m.TimerClicked());
        Assert.True(m.FinishConfirmed());
        Assert.Equal("Finishing", Screen(m));
    }

    [Fact]
    public void FinishCannotBeRequestedFromTheTimerScreen()
    {
        var m = Recording();
        Assert.False(m.FinishConfirmed());
        Assert.Equal("Recording", Screen(m));
    }

    [Fact]
    public void PreservationWordingAppearsOnlyAfterTheEngineStopped()
    {
        var m = Recording();
        m.TimerClicked();
        m.FinishConfirmed();
        Assert.DoesNotContain("kept on this device", m.Notice);
        m.CaptureStopped();
        Assert.Equal("Recording has stopped. Your source is kept on this device.", m.Notice);
    }

    [Fact]
    public void PreviewShowsTheCapturedDuration()
    {
        var m = Recording();
        m.TimerClicked();
        m.FinishConfirmed();
        m.CaptureStopped();
        Assert.True(m.PreviewReady("p.mp4", new TimeSpan(0, 24, 18)));
        Assert.Equal("Preview", Screen(m));
        Assert.Contains("24 minutes", Format.Focus(m.Elapsed));
    }

    [Fact]
    public void SavedRequiresAVerifiedFile()
    {
        var m = Preview();
        Assert.True(m.SaveStarted());
        Assert.False(m.SaveSucceeded("a.mp4", fileVerified: false));
        Assert.Equal("Preview", Screen(m));
        Assert.True(m.SaveSucceeded("a.mp4", fileVerified: true));
        Assert.Equal("Saved", Screen(m));
        Assert.Equal("a.mp4", m.SavedPath);
    }

    [Fact]
    public void SavedScreenShowsVideosCrumbsNotTheFullPath()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var m = Preview();
        m.SaveStarted();
        m.SaveSucceeded(Path.Combine(videos, "log", "a.mp4"), true);
        Assert.Equal("Videos / log / a.mp4", m.ToView().SavedPath);
        Assert.Equal(Path.Combine(videos, "log", "a.mp4"), m.SavedPath);
    }

    [Fact]
    public void SaveCannotBeRepeatedWhileBusy()
    {
        var m = Preview();
        Assert.True(m.SaveStarted());
        Assert.False(m.SaveStarted());
        Assert.True(m.ToView().Busy);
    }

    [Fact]
    public void SaveFailureShowsTheErrorAndKeepsThePreview()
    {
        var m = Preview();
        m.SaveStarted();
        Assert.True(m.SaveFailed("The folder is not available."));
        Assert.Equal("SaveError", Screen(m));
        Assert.Equal("p.mp4", m.PreviewPath);
        Assert.True(m.SaveStarted()); // choose another folder and retry
    }

    [Fact]
    public void KeepForLaterNeverDropsThePreview()
    {
        var m = Preview();
        Assert.True(m.KeepForLater());
        Assert.Equal("Preview", Screen(m));
        Assert.Equal("p.mp4", m.PreviewPath);
    }

    [Fact]
    public void FailedPreviewReturnsToSourceAndSaysTheRecordingIsSafe()
    {
        var m = Recording();
        m.TimerClicked();
        m.FinishConfirmed();
        Assert.True(m.PreviewFailed("Could not prepare the preview. Your recording is still on this device."));
        Assert.Equal("Source", Screen(m));
    }

    [Fact]
    public void DoneResetsToTheInvitation()
    {
        var m = Preview();
        m.SaveStarted();
        m.SaveSucceeded("a.mp4", true);
        Assert.True(m.Done());
        Assert.Equal("Invitation", Screen(m));
        Assert.Null(m.SavedPath);
    }

    [Fact]
    public void SourceLossShowsAPausedScreenAndResumeReturns()
    {
        var m = Recording();
        Assert.True(m.SourceLost("source unavailable", "Restore the source, then choose Try again."));
        Assert.Equal("SourceLost", Screen(m));
        Assert.True(m.CaptureStarted);
        m.ResumeFailed("Capture could not resume yet.");
        Assert.Equal("Capture could not resume yet.", m.Notice);
        Assert.True(m.Resumed());
        Assert.Equal("Recording", Screen(m));
    }

    [Fact]
    public void FinishSessionWorksFromTheSourceLostScreen()
    {
        var m = Recording();
        m.SourceLost("source unavailable");
        Assert.True(m.FinishConfirmed());
        Assert.Equal("Finishing", Screen(m));
    }

    [Fact]
    public void UnexpectedCaptureEndGoesOnToFinishing()
    {
        var m = Recording();
        Assert.True(m.CaptureFailed("Recording stopped unexpectedly. Preparing what was captured."));
        Assert.Equal("Finishing", Screen(m));
        Assert.False(new ShellModel().CaptureFailed("x")); // nothing was recording
    }

    [Fact]
    public void ActiveClockCountsOnlyRecordingTime()
    {
        var now = TimeSpan.Zero;
        var clock = new ActiveClock(() => now);
        clock.Running();
        now = TimeSpan.FromSeconds(10);
        clock.Stopped();
        now = TimeSpan.FromSeconds(100); // paused for 90 s
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Elapsed);
        clock.Running();
        now = TimeSpan.FromSeconds(105);
        Assert.Equal(TimeSpan.FromSeconds(15), clock.Elapsed);
        clock.Reset();
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }
}
