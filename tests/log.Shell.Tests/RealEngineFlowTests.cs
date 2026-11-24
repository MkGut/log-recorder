using System.Diagnostics;
using System.Runtime.CompilerServices;
using Log.Core.Capture;
using Log.Engine;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

/// <summary>
/// The shell controller on the real LogEngine, real session store, real Media Foundation render and
/// real save. Only the frame source is generated (a moving gradient) instead of the screen, so no picker
/// consent is involved and no screen content is captured. Runs in a temporary data root.
/// </summary>
[Collection("ui")]
public sealed class RealEngineFlowTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "log-shell-e2e-" + Guid.NewGuid().ToString("N"));
    readonly string? previousRoot = Environment.GetEnvironmentVariable("LOG_DATA_ROOT");

    public RealEngineFlowTests()
    {
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LOG_DATA_ROOT", root); // keeps the export folder out of Videos\log
        Ui.Init(0);
    }

    static async Task Until(Func<bool> condition, int seconds = 90)
    {
        var end = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < end)
        {
            Ui.Drain();
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("condition not reached");
    }

    [Fact]
    public async Task RecordFinishPreviewAndSaveProduceARealMp4()
    {
        var consent = new GeneratedConsent();
        await using var controller = new ShellController(
            b => LogEngine.CreateDefault(new SnapshotEncoderFactory(), b, new RendererAdapter(root), root),
            consent, root,
            changed: () => { }, showWindow: () => { }, hideWindow: () => { },
            updateTray: (_, _) => true, verifyWindowVisible: () => true, pickFolder: () => null);
        var model = controller.Model;

        controller.LetsGo();
        controller.ChooseSource();
        await Until(() => model.Screen == ScreenId.Recording);
        Assert.Equal(EngineActivity.Recording, controller.EngineState.Activity);
        Assert.True(model.CaptureStarted);

        await Task.Delay(TimeSpan.FromSeconds(5)); // let a few one-second samples land
        Ui.Drain();
        Assert.True(controller.Elapsed >= TimeSpan.FromSeconds(4), $"elapsed {controller.Elapsed}");

        controller.TimerClicked();
        controller.FinishConfirmed();
        await Until(() => model.Screen == ScreenId.Preview);
        Assert.Equal(EngineActivity.Idle, controller.EngineState.Activity);
        Assert.True(File.Exists(model.PreviewPath));
        Assert.True(new FileInfo(model.PreviewPath!).Length > 1000);

        controller.Save();
        await Until(() => model.Screen is ScreenId.Saved or ScreenId.SaveError);
        Assert.Equal(ScreenId.Saved, model.Screen);
        var saved = model.SavedPath!;
        Assert.StartsWith(root, saved, StringComparison.OrdinalIgnoreCase);
        Assert.True(new FileInfo(saved).Length > 1000);

        // An MP4 starts with a box header followed by "ftyp".
        var head = new byte[12];
        await using (var f = File.OpenRead(saved)) _ = await f.ReadAsync(head);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_DATA_ROOT", previousRoot);
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp folder */ }
        catch (UnauthorizedAccessException) { /* temp folder */ }
    }

    internal sealed class GeneratedConsent : IShellConsent
    {
        public string? SelectedDisplayName => "Generated frames";
        public (int Width, int Height)? SelectedFrameSize => (GeneratedSource.W, GeneratedSource.H);
        public Task<bool> SelectDisplayAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<CaptureConsent> RequestCaptureConsentAsync(CaptureRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CaptureConsent(new GeneratedSource(), IndicatorsVerified: true));

        public ValueTask ReleaseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>10 BGRA frames a second of a moving gradient, stamped on the same clock the engine uses.</summary>
    internal sealed class GeneratedSource : IFrameSource
    {
        public const int W = 640, H = 360;

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                var pixels = new byte[W * H * 4];
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var o = (y * W + x) * 4;
                        pixels[o] = (byte)(x + i * 4);
                        pixels[o + 1] = (byte)y;
                        pixels[o + 2] = (byte)(i * 8);
                        pixels[o + 3] = 255;
                    }

                var ticks = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
                yield return new FrameSample(ticks, pixels, W, H);
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }
}
