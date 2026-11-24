using System.Globalization;
using System.Runtime.CompilerServices;
using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Log.Engine;
using Log.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Log.Integration.Tests;

// End-to-end proof of the runnable product spine (docs/planning/09 §M1): the
// LogEngine composition root records synthetic frames through the real capture
// coordinator + store, stops, and exports.
//
// NOTE ON THE ENCODER: the native Media Foundation encoder cannot run on this
// host (no toolchain / Smart App Control), so this test uses a synthetic encoder
// and a synthetic renderer that composes the recorded session's slots into a
// deterministic, decode-and-assert artifact. Separate ABI probes
// prove the native encoder finalizes a valid, decodable MP4; this
// test proves the orchestration around it.
public sealed class EngineSpineTests
{
    private readonly ITestOutputHelper output;

    public EngineSpineTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void ActiveEngineContractsExcludeGroupAssignmentAndUnreasonedPause()
    {
        Assert.Null(typeof(CaptureRequest).GetProperty("GroupId"));
        Assert.DoesNotContain(
            typeof(CaptureRequest).GetConstructors().SelectMany(static constructor => constructor.GetParameters()),
            static parameter => parameter.Name == "groupId");
        Assert.Null(typeof(IAppEngine).GetMethod("PauseAsync", [typeof(CancellationToken)]));
        Assert.Null(typeof(LogEngine).GetMethod("PauseAsync", [typeof(CancellationToken)]));
    }

    [Fact]
    public async Task StartDoesNotPublishRecordingDuringConsentAndDisposeWaitsForStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var consent = new BlockingConsentBroker(
            SyntheticFrameSource.Create(3, 0, 10, 2, 2),
            blockRelease: true);
        var engine = new LogEngine(
            root,
            new VirtualClock(),
            clockTicksPerSecond: 1,
            new SingleEncoderFactory(new GatedRecordingEncoder()),
            consent,
            new SlotManifestRenderer());
        var observedStates = new System.Collections.Concurrent.ConcurrentQueue<EngineActivity>();
        engine.StateChanged += snapshot => observedStates.Enqueue(snapshot.Activity);

        try
        {
            var startTask = engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));

            await consent.RequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
            Assert.DoesNotContain(EngineActivity.Recording, observedStates);
            Assert.False(startTask.IsCompleted);

            var firstDisposal = engine.DisposeAsync().AsTask();
            await consent.ReleaseEntered.WaitAsync(TimeSpan.FromSeconds(10));
            var concurrentDisposal = engine.DisposeAsync().AsTask();
            Assert.False(concurrentDisposal.IsCompleted);
            consent.AllowRelease();
            await Task.WhenAll(firstDisposal, concurrentDisposal).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);

            Assert.Equal(1, consent.ReleaseCount);
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
        }
        finally
        {
            consent.AllowRelease();
            await engine.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GrantedConsentVerifiesIndicatorsBeforeStartAsyncAcceptsConsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var trace = new List<string>();
        var source = new BlockingFrameSource();
        trace.Add("Picker consent granted");
        var verifiedConsent = await CaptureConsentVerification.CreateAsync(
            source,
            _ =>
            {
                trace.Add("Cobalt window visible and not minimized");
                return Task.FromResult(true);
            },
            _ =>
            {
                trace.Add("Tray recording state applied");
                return Task.FromResult(true);
            });
        trace.Add(verifiedConsent.IndicatorsVerified ? "IndicatorsVerified=true" : "IndicatorsVerified=false");
        var consent = new CountingConsentBroker(source, () => trace.Add("Fake consent source returned grant"),
            indicatorsVerified: verifiedConsent.IndicatorsVerified);
        var engine = new LogEngine(
            root,
            new VirtualClock(),
            clockTicksPerSecond: 1,
            new TracingEncoderFactory(trace, new GatedRecordingEncoder()),
            consent,
            new SlotManifestRenderer());

        try
        {
            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));

            trace.Add("StartAsync passed the consent check and reached encoder setup");
            output.WriteLine(string.Join(Environment.NewLine, trace));
            Assert.Equal(
                [
                    "Picker consent granted",
                    "Cobalt window visible and not minimized",
                    "Tray recording state applied",
                    "IndicatorsVerified=true",
                    "Fake consent source returned grant",
                    "Engine passed the consent verification check",
                    "StartAsync passed the consent check and reached encoder setup"
                ],
                trace);
        }
        finally
        {
            await engine.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnverifiedWindowOrTrayNeverGrantsCaptureConsent()
    {
        var trayWasApplied = false;
        var hiddenWindow = await CaptureConsentVerification.CreateAsync(
            new BlockingFrameSource(),
            _ => Task.FromResult(false),
            _ =>
            {
                trayWasApplied = true;
                return Task.FromResult(true);
            });
        Assert.False(hiddenWindow.WindowVisible);
        Assert.False(hiddenWindow.TrayApplied);
        Assert.False(hiddenWindow.IndicatorsVerified);
        Assert.False(trayWasApplied);

        var unavailableTray = await CaptureConsentVerification.CreateAsync(
            new BlockingFrameSource(),
            _ => Task.FromResult(true),
            _ => Task.FromResult(false));
        Assert.True(unavailableTray.WindowVisible);
        Assert.False(unavailableTray.TrayApplied);
        Assert.False(unavailableTray.IndicatorsVerified);
    }

    [Fact]
    public async Task StartDoesNotPublishRecordingDuringEncoderSetupAndDisposeWaitsForStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var consent = new CountingConsentBroker(SyntheticFrameSource.Create(3, 0, 10, 2, 2));
        var encoderFactory = new BlockingEncoderFactory();
        var engine = new LogEngine(
            root,
            new VirtualClock(),
            clockTicksPerSecond: 1,
            encoderFactory,
            consent,
            new SlotManifestRenderer());
        var observedStates = new System.Collections.Concurrent.ConcurrentQueue<EngineActivity>();
        engine.StateChanged += snapshot => observedStates.Enqueue(snapshot.Activity);

        try
        {
            var startTask = engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));

            await encoderFactory.CreateEntered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
            Assert.DoesNotContain(EngineActivity.Recording, observedStates);
            Assert.False(startTask.IsCompleted);

            await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);

            Assert.True(encoderFactory.CancellationObserved);
            Assert.Equal(1, consent.ReleaseCount);
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
        }
        finally
        {
            await engine.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartDoesNotPublishRecordingWhenCoordinatorSetupFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var source = new ThrowingSamplingSource(SyntheticFrameSource.Create(3, 0, 10, 2, 2));
        var consent = new CountingConsentBroker(source);
        var engine = new LogEngine(
            root,
            new VirtualClock(),
            clockTicksPerSecond: 1,
            new SingleEncoderFactory(new GatedRecordingEncoder()),
            consent,
            new SlotManifestRenderer());
        var observedStates = new System.Collections.Concurrent.ConcurrentQueue<EngineActivity>();
        engine.StateChanged += snapshot => observedStates.Enqueue(snapshot.Activity);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10))));

            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
            Assert.DoesNotContain(EngineActivity.Recording, observedStates);
            Assert.Equal(1, consent.ReleaseCount);
        }
        finally
        {
            await engine.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineRecordsStopsAndExportsAssertableArtifact(bool throwingStateObserver)
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var source = SyntheticFrameSource.Create(frameCount: 3, startTicks: 0, intervalTicks: 10, width: 2, height: 2);
        var encoder = new GatedRecordingEncoder();
        var encoderFactory = new SingleEncoderFactory(encoder);
        var renderer = new SlotManifestRenderer();
        try
        {
            await using var engine = new LogEngine(
                root,
                new VirtualClock(),
                clockTicksPerSecond: 1,
                encoderFactory,
                new GrantingConsentBroker(source),
                renderer);

            var observedStates = new System.Collections.Concurrent.ConcurrentQueue<EngineActivity>();
            if (throwingStateObserver)
            {
                engine.StateChanged += _ => throw new InvalidOperationException("Faulty UI observer.");
            }

            engine.StateChanged += snapshot => observedStates.Enqueue(snapshot.Activity);

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));

            // The synthetic clock advances instantly; wait for the natural finalize
            // so the stop harvests a fully recorded session (deterministic, non-flaky).
            await encoder.Finalized.WaitAsync(TimeSpan.FromSeconds(30));

            var result = await engine.StopAsync();

            Assert.Equal(3, result.SlotCount);
            Assert.Equal(0, result.GapCount);
            Assert.True(encoder.IsFinalized);
            Assert.Equal(3, encoder.Frames.Count);
            Assert.Equal(
                [0, 1, 2],
                SyntheticFramePayload.DecodeEncodedFrames(encoder.Frames).Select(marker => marker.FrameIndex));
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);

            // The encoder factory received the live session store + track (the
            // store-aware composition a production segmented MF encoder relies on).
            Assert.NotNull(encoderFactory.LastContext);
            Assert.Equal("screen", encoderFactory.LastContext!.Track);
            Assert.Equal(result.SessionId, encoderFactory.LastContext.Store.Manifest.Id);

            var export = await engine.ExportAsync(
                result.SessionId,
                new EngineExportRequest(DefaultRenderSettings(), outputDirectory));

            Assert.True(export.Completed);
            Assert.True(File.Exists(export.OutputPath));
            Assert.Contains(EngineActivity.Exporting, observedStates);
            Assert.Equal(EngineActivity.Idle, observedStates.Last());

            // Decode the exported artifact and assert it carries every recorded slot in order.
            var exportedSlots = (await File.ReadAllLinesAsync(export.OutputPath))
                .Select(line => int.Parse(line, CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal([0, 1, 2], exportedSlots);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartAsyncFailsFastWhenStorageIsInsufficient()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var encoder = new GatedRecordingEncoder();
        var consent = new CountingConsentBroker(SyntheticFrameSource.Create(3, 0, 10, 2, 2));

        try
        {
            await using var engine = new LogEngine(
                root,
                new VirtualClock(),
                clockTicksPerSecond: 1,
                new SingleEncoderFactory(encoder),
                consent,
                new SlotManifestRenderer());

            // An impossible reserve makes the storage guard fail before any session
            // artifacts are created.
            await Assert.ThrowsAsync<InsufficientStorageException>(() => engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10),
                reserveBytes: long.MaxValue)));

            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
            // The guard runs before consent and before any catalog/session artifacts:
            // the privacy/consent flow is never invoked and nothing is written (M0-194).
            Assert.Equal(0, consent.RequestCount);
            Assert.Equal(0, consent.ReleaseCount);
            Assert.False(Directory.Exists(Path.Combine(root, "sessions")));
            Assert.False(File.Exists(LogPaths.CatalogPath(root)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EngineExportPreservesRawScreenTrack()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var source = SyntheticFrameSource.Create(frameCount: 3, startTicks: 0, intervalTicks: 10, width: 2, height: 2);
        var encoder = new GatedRecordingEncoder();

        try
        {
            await using var engine = new LogEngine(
                root,
                new VirtualClock(),
                clockTicksPerSecond: 1,
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(source),
                new SlotManifestRenderer());

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            await encoder.Finalized.WaitAsync(TimeSpan.FromSeconds(30));
            var result = await engine.StopAsync();
            var rawTrackDirectory = Path.Combine(root, "sessions", result.SessionId, "screen");
            var rawFiles = Directory.GetFiles(rawTrackDirectory, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(rawFiles);

            var export = await engine.ExportAsync(
                result.SessionId,
                new EngineExportRequest(DefaultRenderSettings(), outputDirectory));

            Assert.True(export.Completed);
            Assert.True(File.Exists(export.OutputPath));
            Assert.All(rawFiles, static path => Assert.True(File.Exists(path)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncFailsWhenOutputStorageIsInsufficient()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var source = SyntheticFrameSource.Create(frameCount: 3, startTicks: 0, intervalTicks: 10, width: 2, height: 2);
        var encoder = new GatedRecordingEncoder();

        try
        {
            await using var engine = new LogEngine(
                root,
                new VirtualClock(),
                clockTicksPerSecond: 1,
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(source),
                new SlotManifestRenderer());

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            await encoder.Finalized.WaitAsync(TimeSpan.FromSeconds(30));
            var result = await engine.StopAsync();

            // An impossible output reserve trips the export storage guard.
            await Assert.ThrowsAsync<InsufficientStorageException>(() => engine.ExportAsync(
                result.SessionId,
                new EngineExportRequest(
                    DefaultRenderSettings(),
                    outputDirectory,
                    ReserveBytes: long.MaxValue)));

            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WatchdogStopCleansEngineWithoutAStopCaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var idle = new TaskCompletionSource<EngineStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCompleted = new TaskCompletionSource<CaptureRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordingStarted = 0;
        var probeCount = 0;
        var encoder = new GatedRecordingEncoder();

        try
        {
            await using var engine = new LogEngine(
                root,
                new SystemClock(),
                SystemClock.Frequency,
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(new BlockingFrameSource()),
                new SlotManifestRenderer(),
                availableFreeSpace: _ =>
                {
                    Interlocked.Increment(ref probeCount);
                    return 0;
                });
            engine.StateChanged += snapshot =>
            {
                if (snapshot.Activity is EngineActivity.Recording or EngineActivity.Paused)
                {
                    Volatile.Write(ref recordingStarted, 1);
                }
                else if (snapshot.Activity == EngineActivity.Idle
                    && Volatile.Read(ref recordingStarted) != 0)
                {
                    idle.TrySetResult(snapshot);
                }
            };
            engine.CaptureCompleted += result => captureCompleted.TrySetResult(result);

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromMinutes(1),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10),
                reserveBytes: 1));

            Assert.True(Volatile.Read(ref probeCount) > 0);
            await encoder.Finalized.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await captureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completed = await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EngineActivity.Idle, completed.Activity);
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);
            Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EngineExportBuildsRenderPlanCoveringEveryRecordedSlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var source = SyntheticFrameSource.Create(frameCount: 3, startTicks: 0, intervalTicks: 10, width: 2, height: 2);
        var encoder = new GatedRecordingEncoder();
        var renderer = new PlanCapturingRenderer();

        try
        {
            await using var engine = new LogEngine(
                root,
                new VirtualClock(),
                clockTicksPerSecond: 1,
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(source),
                renderer);

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10),
                outputFramesPerSecond: 30));
            await encoder.Finalized.WaitAsync(TimeSpan.FromSeconds(30));
            var result = await engine.StopAsync();

            var export = await engine.ExportAsync(
                result.SessionId,
                new EngineExportRequest(DefaultRenderSettings(), outputDirectory));
            Assert.True(export.Completed);

            // The render plan built from the engine-recorded manifest covers every
            // recorded slot, in order, with strictly increasing MF presentation
            // timestamps and a real per-slot screen asset reference.
            var plan = renderer.Plan;
            Assert.NotNull(plan);
            Assert.Equal(3, plan.Count);
            for (var index = 0; index < plan.Count; index++)
            {
                Assert.Equal(index, plan[index].OutputIndex);
                Assert.Equal(index, plan[index].SlotIndex);
                Assert.False(plan[index].IsHighlightHold);
                Assert.False(plan[index].Sources.ScreenIsGap);
                Assert.False(string.IsNullOrEmpty(plan[index].Sources.ScreenAssetReference));
                if (index > 0)
                {
                    Assert.True(plan[index].PresentationTicks > plan[index - 1].PresentationTicks);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EngineHighlightAndPauseDriveTheActiveRecording()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var encoder = new GatedRecordingEncoder();
        var source = new BlockingFrameSource();

        try
        {
            await using var engine = new LogEngine(
                root,
                new SystemClock(),
                SystemClock.Frequency,
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(source),
                new SlotManifestRenderer());

            // Real clock + a source that never yields keeps the recording observably
            // active (waiting for the first 10 s slot) while we drive it.
            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromMinutes(1),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            // Wait until the coordinator is actually reading (past its startup gate
            // check) so driving pause/highlight can't race that check.
            await source.Started.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(EngineActivity.Recording, engine.State.Activity);
            Assert.True(await engine.HighlightAsync(new HighlightRequest(TimeSpan.FromSeconds(2))));

            Assert.False(await engine.PauseAsync(CapturePauseReason.UserRequested));
            Assert.Equal(EngineActivity.Recording, engine.State.Activity);
            Assert.True(await engine.PauseAsync(CapturePauseReason.WorkstationLocked));
            Assert.Equal(EngineActivity.Paused, engine.State.Activity);
            // Highlights are rejected while paused.
            Assert.False(await engine.HighlightAsync(new HighlightRequest(TimeSpan.FromSeconds(2))));

            Assert.True(await engine.ResumeAsync());
            Assert.Equal(EngineActivity.Recording, engine.State.Activity);

            var result = await engine.StopAsync();

            // The highlight accepted while recording was persisted with the session.
            // (Pause/resume correctness is proven above by the observable state
            // transitions; pause-range persistence is capture-loop timing covered
            // by the coordinator's own tests.)
            await using var store = await SessionStore.OpenAsync(root, result.SessionId);
            Assert.Null(store.Manifest.GroupId);
            var highlight = Assert.Single(store.Manifest.Highlights);
            Assert.Equal(2 * SystemClock.Frequency, highlight.HoldTicks);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateDefaultComposesTheNormalizedTimelineClock()
    {
        // CreateDefault must wire the normalized QpcClock (100ns timeline unit),
        // not the raw Stopwatch frequency (not guaranteed to be 10 MHz). A 2 s
        // hold therefore converts to exactly 2 * TicksPerSecond regardless of this
        // host's Stopwatch.Frequency — the assertion locks that unit in and would
        // fail if CreateDefault regressed to SystemClock/SystemClock.Frequency.
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var encoder = new GatedRecordingEncoder();
        var source = new BlockingFrameSource();

        try
        {
            await using var engine = LogEngine.CreateDefault(
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(source),
                new SlotManifestRenderer(),
                root);

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromMinutes(1),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            await source.Started.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(await engine.HighlightAsync(new HighlightRequest(TimeSpan.FromSeconds(2))));
            var result = await engine.StopAsync();

            await using var store = await SessionStore.OpenAsync(root, result.SessionId);
            var highlight = Assert.Single(store.Manifest.Highlights);
            Assert.Equal(2 * TimelineConstants.TicksPerSecond, highlight.HoldTicks);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StopAsyncCancellationLeavesEngineCleanlyReusable()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));

        try
        {
            await using var engine = new LogEngine(
                root,
                new SystemClock(),
                SystemClock.Frequency,
                new SingleEncoderFactory(new GatedRecordingEncoder()),
                new GrantingConsentBroker(new BlockingFrameSource()),
                new SlotManifestRenderer());

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromMinutes(1),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            Assert.Equal(EngineActivity.Recording, engine.State.Activity);

            // Cancel the stop's wait while the run is still live. Cleanup must wait
            // for the run to reach a terminal state before releasing shared state.
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => engine.StopAsync(cts.Token));
            }

            // The engine is fully cleaned up and reusable — a second capture cycle
            // completes, which it could not if teardown had raced the first run.
            Assert.Equal(EngineActivity.Idle, engine.State.Activity);

            var encoder2 = new GatedRecordingEncoder();
            await using var engine2 = new LogEngine(
                root,
                new VirtualClock(),
                clockTicksPerSecond: 1,
                new SingleEncoderFactory(encoder2),
                new GrantingConsentBroker(SyntheticFrameSource.Create(3, 0, 10, 2, 2)),
                new SlotManifestRenderer());
            await engine2.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromSeconds(30),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            await encoder2.Finalized.WaitAsync(TimeSpan.FromSeconds(30));
            var result = await engine2.StopAsync();
            Assert.Equal(3, result.SlotCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EngineSystemPauseSurfacesReasonAndHoldsUntilExplicitResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-engine-tests", Guid.NewGuid().ToString("N"));
        var encoder = new GatedRecordingEncoder();
        var source = new BlockingFrameSource();

        try
        {
            await using var engine = new LogEngine(
                root,
                new SystemClock(),
                SystemClock.Frequency,
                new SingleEncoderFactory(encoder),
                new GrantingConsentBroker(source),
                new SlotManifestRenderer());

            await engine.StartAsync(new CaptureRequest(
                maxDuration: TimeSpan.FromMinutes(1),
                interval: TimeSpan.FromSeconds(10),
                maxFrameAge: TimeSpan.FromSeconds(10)));
            // Wait until the coordinator is actually reading (past its startup gate
            // check) so the system pause can't race that check.
            await source.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(EngineActivity.Recording, engine.State.Activity);

            // A system transition (workstation lock) pauses with its truthful reason.
            Assert.True(await engine.PauseAsync(CapturePauseReason.WorkstationLocked));
            var paused = engine.State;
            Assert.Equal(EngineActivity.Paused, paused.Activity);
            Assert.Equal(CapturePauseReason.WorkstationLocked, paused.PauseReason);

            // The pause holds until an explicit, verified resume (overt-only invariant).
            Assert.True(await engine.ResumeAsync());
            Assert.Equal(EngineActivity.Recording, engine.State.Activity);

            await engine.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RenderSettings DefaultRenderSettings()
    {
        return new RenderSettings(
            new CompositionSettings(
                new PixelSize(2, 2),
                new PipLayout(visible: false, widthFraction: 0, insetFraction: 0, corner: PipCorner.BottomRight),
                outputFramesPerSecond: 30));
    }

    private sealed class GatedRecordingEncoder : IEncoder
    {
        private readonly List<EncodedFrame> frames = [];
        private readonly TaskCompletionSource finalized =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<EncodedFrame> Frames => frames;
        public bool IsFinalized { get; private set; }
        public Task Finalized => finalized.Task;

        public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsFinalized = true;
            finalized.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleEncoderFactory(IEncoder encoder) : IEncoderFactory
    {
        public EncoderContext? LastContext { get; private set; }

        public Task<IEncoder> CreateAsync(EncoderContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(encoder);
        }
    }

    private sealed class TracingEncoderFactory(IList<string> trace, IEncoder encoder) : IEncoderFactory
    {
        public Task<IEncoder> CreateAsync(EncoderContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("Engine passed the consent verification check");
            return Task.FromResult(encoder);
        }
    }

    // Never yields a frame; honors cancellation so cooperative stop tears it down.
    // Signals when the coordinator begins reading, which happens only after its
    // startup gate check passes — letting tests drive pause/highlight without
    // racing that check.
    private sealed class BlockingFrameSource : IFrameSource
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }

    private sealed class ThrowingSamplingSource(IFrameSource inner) : IFrameSource, IFrameSourceSamplingControl
    {
        public long SampleIntervalTicks => 0;

        public IAsyncEnumerable<FrameSample> ReadFramesAsync(CancellationToken cancellationToken = default) =>
            inner.ReadFramesAsync(cancellationToken);

        public void SetSampleInterval(long intervalTicks) =>
            throw new InvalidOperationException("The source failed while the coordinator was starting.");
    }

    private sealed class CountingConsentBroker(
        IFrameSource source,
        Action? beforeGrant = null,
        bool indicatorsVerified = true) : IConsentBroker
    {
        public int RequestCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<CaptureConsent> RequestCaptureConsentAsync(
            CaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            beforeGrant?.Invoke();
            return Task.FromResult(new CaptureConsent(source, indicatorsVerified));
        }

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingConsentBroker(IFrameSource source, bool blockRelease = false) : IConsentBroker
    {
        private readonly TaskCompletionSource requestEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseAllowed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestEntered => requestEntered.Task;
        public Task ReleaseEntered => releaseEntered.Task;
        public int ReleaseCount { get; private set; }

        public async Task<CaptureConsent> RequestCaptureConsentAsync(
            CaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            requestEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new CaptureConsent(source, IndicatorsVerified: true);
        }

        public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            releaseEntered.TrySetResult();
            if (blockRelease)
            {
                await releaseAllowed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void AllowRelease() => releaseAllowed.TrySetResult();
    }

    private sealed class BlockingEncoderFactory : IEncoderFactory
    {
        private readonly TaskCompletionSource createEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CreateEntered => createEntered.Task;
        public bool CancellationObserved { get; private set; }

        public async Task<IEncoder> CreateAsync(
            EncoderContext context,
            CancellationToken cancellationToken = default)
        {
            createEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The blocked encoder factory should only finish through cancellation.");
        }
    }

    private sealed class GrantingConsentBroker(IFrameSource source) : IConsentBroker
    {
        public Task<CaptureConsent> RequestCaptureConsentAsync(
            CaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CaptureConsent(source, IndicatorsVerified: true));
        }

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    // Stands in for the native MP4 renderer: composes the recorded session's
    // ordered slots into a deterministic artifact the test can decode and assert.
    private sealed class SlotManifestRenderer : ISessionRenderer
    {
        public async Task RenderAsync(SessionExportContext context, CancellationToken cancellationToken = default)
        {
            var lines = context.RenderInput.Slots
                .Select(slot => slot.Slot.Index.ToString(CultureInfo.InvariantCulture));
            await File.WriteAllLinesAsync(context.OutputPath, lines, cancellationToken);
        }
    }

    // Drives the canonical render plan the platform renderer is meant to consume:
    // it builds the plan from the exported session and records it for assertions.
    private sealed class PlanCapturingRenderer : ISessionRenderer
    {
        public IReadOnlyList<RenderFramePlan>? Plan { get; private set; }

        public Task RenderAsync(SessionExportContext context, CancellationToken cancellationToken = default)
        {
            var plan = RenderTimelinePlanner.Plan(context.RenderInput, context.RenderSettings);
            Plan = plan;
            return File.WriteAllTextAsync(
                context.OutputPath,
                $"frames={plan.Count}",
                cancellationToken);
        }
    }
}
