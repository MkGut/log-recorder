using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Shell;
using Log.Engine;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

/// <summary>
/// Runs the real controller against a fake engine and a fake picker: call order, "timer only after the
/// engine started", "Saved only after the file exists" and failure handling. The Windows picker itself
/// needs a person, so it is faked here and still open in the results.
/// </summary>
[Collection("ui")]
public sealed class ShellControllerTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "log-shell-tests-" + Guid.NewGuid().ToString("N"));
    readonly FakeEngine engine;
    readonly FakeConsent consent = new();
    readonly ShellController controller;
    string? folder;

    public ShellControllerTests()
    {
        Directory.CreateDirectory(dir);
        engine = new FakeEngine(dir);
        Ui.Init(0);
        controller = new ShellController(
            _ => engine, consent, dir,
            changed: () => { }, showWindow: () => { }, hideWindow: () => { },
            updateTray: (_, _) => true, verifyWindowVisible: () => true,
            pickFolder: () => folder,
            estimateOutputBytes: (_, _, _) => Task.FromResult(0L));
    }

    ShellModel Model => controller.Model;
    string Screen => Model.Screen.ToString();

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            Ui.Drain();
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("condition not reached");
    }

    async Task StartRecording()
    {
        controller.LetsGo();
        controller.ChooseSource();
        await Until(() => Screen == "Recording");
    }

    async Task FinishToPreview()
    {
        controller.TimerClicked();
        controller.FinishConfirmed();
        await Until(() => Screen == "Preview");
    }

    [Fact]
    public async Task WholeFlowAsksTheEngineInOrderAndEndsSaved()
    {
        await StartRecording();
        Assert.Equal(1, consent.Picked);
        Assert.Equal(1, engine.StartCalls);
        Assert.True(Model.CaptureStarted);
        Assert.Equal("Test display", Model.Source);

        await FinishToPreview();
        Assert.Equal(1, engine.StopCalls);
        Assert.Equal(1, engine.RenderCalls);
        Assert.True(File.Exists(Model.PreviewPath));

        controller.Save();
        Assert.True(Model.Busy); // duplicate saves are blocked while one runs
        await Until(() => Screen == "Saved");
        Assert.True(File.Exists(Model.SavedPath));
        Assert.Equal(1, engine.SaveCalls);
    }

    [Fact]
    public async Task TimerAppearsOnlyAfterTheEngineHasStarted()
    {
        engine.HoldStart = new TaskCompletionSource();
        controller.LetsGo();
        controller.ChooseSource();
        await Until(() => engine.StartCalls == 1);
        Assert.False(Model.CaptureStarted);
        Assert.Equal("Source", Screen); // still waiting; no timer yet
        Assert.True(Model.ToView().Busy);

        engine.HoldStart.SetResult();
        await Until(() => Screen == "Recording");
        Assert.True(Model.CaptureStarted);
    }

    [Fact]
    public async Task CancelledPickerNeverStartsTheEngine()
    {
        consent.Picks = false;
        controller.LetsGo();
        controller.ChooseSource();
        await Until(() => Screen == "ConsentCancelled");
        Assert.Equal(0, engine.StartCalls);
        Assert.False(Model.CaptureStarted);
    }

    [Fact]
    public async Task FailedStartReturnsToSourceWithAReason()
    {
        engine.StartFails = new InvalidOperationException("The recording indicator could not be applied.");
        controller.LetsGo();
        controller.ChooseSource();
        await Until(() => Model.Notice.StartsWith("Couldn't start recording.", StringComparison.Ordinal));
        Assert.Equal("Source", Screen);
        Assert.Contains("indicator", Model.Notice);
        Assert.False(Model.CaptureStarted);
        Assert.False(Model.ToView().Busy); // the source menu is usable again
    }

    [Fact]
    public async Task EscapeOnGoodbyeKeepsRecordingAndNeverStops()
    {
        await StartRecording();
        controller.TimerClicked();
        Assert.Equal("Goodbye", Screen);
        controller.KeepGoing(GoodbyeDecisionAction.Escape);
        Assert.Equal("Recording", Screen);
        Assert.Equal(0, engine.StopCalls);
    }

    [Fact]
    public async Task FinishStopsTheEngineExactlyOnce()
    {
        await StartRecording();
        controller.TimerClicked();
        controller.FinishConfirmed();
        controller.FinishConfirmed(); // a second click while finishing is ignored
        await Until(() => Screen == "Preview");
        Assert.Equal(1, engine.StopCalls);
    }

    [Fact]
    public async Task SavedIsNeverShownWhenTheFileIsMissing()
    {
        engine.SaveWritesFile = false;
        await StartRecording();
        await FinishToPreview();
        controller.Save();
        await Until(() => Screen == "SaveError");
        Assert.Null(Model.SavedPath);
        Assert.NotNull(Model.PreviewPath); // the preview is kept for another try
    }

    [Fact]
    public async Task SaveErrorCanBeRetriedInAnotherFolder()
    {
        engine.SaveFails = new IOException("The folder is not available.");
        await StartRecording();
        await FinishToPreview();
        controller.Save();
        await Until(() => Screen == "SaveError");
        Assert.Contains("not available", Model.ErrorDetail);

        engine.SaveFails = null;
        folder = Path.Combine(dir, "elsewhere");
        Directory.CreateDirectory(folder);
        controller.ChooseFolder();
        await Until(() => Screen == "Saved");
        Assert.StartsWith(folder, Model.SavedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LosingTheSourceShowsPausedAndTryAgainResumes()
    {
        await StartRecording();
        engine.Raise(new EngineStateSnapshot(EngineActivity.Paused, null, CapturePauseReason.SourceUnavailable, "s1"));
        await Until(() => Screen == "SourceLost");
        Assert.Equal("source unavailable", Model.PauseText);
        Assert.True(Model.CaptureStarted);

        controller.TryAgain();
        await Until(() => Screen == "Recording");
        Assert.Equal(1, engine.ResumeCalls);
    }

    [Fact]
    public async Task AnUnexpectedCaptureFailureStillEndsInAPreview()
    {
        await StartRecording();
        engine.RaiseFailure(new IOException("writer failed"));
        await Until(() => Screen == "Preview");
        Assert.Equal(0, engine.StopCalls); // the capture was already over
        Assert.Equal(1, engine.RenderCalls);
    }

    public void Dispose()
    {
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* temp folder; the OS cleans it up */ }
    }

    sealed class FakeConsent : IShellConsent
    {
        public bool Picks = true;
        public int Picked;
        public string? SelectedDisplayName => "Test display";
        public (int Width, int Height)? SelectedFrameSize => (1920, 1080);
        public Task<bool> SelectDisplayAsync(CancellationToken ct) { Picked++; return Task.FromResult(Picks); }
        public Task<CaptureConsent> RequestCaptureConsentAsync(CaptureRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask ReleaseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeEngine(string dir) : IAppEngine
    {
        public int StartCalls, StopCalls, RenderCalls, SaveCalls, ResumeCalls;
        public TaskCompletionSource? HoldStart;
        public Exception? StartFails, SaveFails;
        public bool SaveWritesFile = true;
        public EngineStateSnapshot State { get; private set; } = new(EngineActivity.Idle, null, null, null);
        public event Action<EngineStateSnapshot>? StateChanged;
        public event Action<Exception>? CaptureFailed;
        public event Action<CaptureRunResult>? CaptureCompleted { add { } remove { } }

        public void Raise(EngineStateSnapshot s) { State = s; StateChanged?.Invoke(s); }

        public void RaiseFailure(Exception e)
        {
            State = new EngineStateSnapshot(EngineActivity.Idle, null, null, null);
            CaptureFailed?.Invoke(e);
        }

        public async Task StartAsync(CaptureRequest request, CancellationToken ct = default)
        {
            StartCalls++;
            if (HoldStart is not null) await HoldStart.Task;
            if (StartFails is not null) throw StartFails;
            Raise(new EngineStateSnapshot(EngineActivity.Recording, null, null, "s1"));
        }

        public Task<CaptureRunResult> StopAsync(CancellationToken ct = default)
        {
            StopCalls++;
            Raise(new EngineStateSnapshot(EngineActivity.Idle, null, null, null));
            return Task.FromResult(new CaptureRunResult("s1", 0, 10, 5, 0, 0));
        }

        public Task<EngineExportPreview> RenderPreviewAsync(string sessionId, EngineExportRequest request, CancellationToken ct = default)
        {
            RenderCalls++;
            var preview = Path.Combine(dir, "preview.mp4");
            File.WriteAllText(preview, "preview");
            return Task.FromResult(new EngineExportPreview(sessionId, "job1", preview, Path.Combine(dir, "out.mp4"), Completed: true));
        }

        public Task<EngineExportResult> SavePreviewAsync(string jobId, string outputDirectory, string? fileName = null, CancellationToken ct = default)
        {
            SaveCalls++;
            if (SaveFails is not null) throw SaveFails;
            var path = Path.Combine(outputDirectory, fileName ?? "out.mp4");
            if (SaveWritesFile) File.WriteAllText(path, "video");
            return Task.FromResult(new EngineExportResult("s1", path, Completed: true));
        }

        public Task<bool> ResumeAsync(CancellationToken ct = default)
        {
            ResumeCalls++;
            Raise(new EngineStateSnapshot(EngineActivity.Recording, null, null, "s1"));
            return Task.FromResult(true);
        }

        public Task<bool> PauseAsync(CapturePauseReason reason, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HighlightAsync(HighlightRequest request, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<EngineExportPreview>> ListPendingExportPreviewsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EngineUnfinishedSave>> ListUnfinishedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EngineUnfinishedSave>>([]);
        public Task DiscardUnfinishedAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<EngineExportResult> ExportAsync(string sessionId, EngineExportRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
