using System.Text.Json;
using Log.Core.Capture;
using Log.Core.Store;
using Log.Core.Timeline;
using Log.Testing;
using Xunit;

namespace Log.Integration.Tests;

public sealed class CaptureCoordinatorTests
{
    [Fact]
    public async Task RefusesToReadFramesUntilThePersistentIndicatorIsVerified()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var source = new TrackingFrameSource();

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureCoordinator.RunAsync(
                source,
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10),
                new CaptureSafetyGate()));

            Assert.False(source.ReadStarted);
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
    public async Task ExplicitUnavailableCompletionPausesSingleSourceCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var source = new ExplicitlyUnavailableFrameSource();

        try
        {
            var captureTask = CaptureCoordinator.RunAsync(
                source,
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(20, 10, 10),
                safetyGate);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (safetyGate.Snapshot().State != CaptureSafetyState.Paused
                && !captureTask.IsCompleted
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Equal(CaptureSafetyState.Paused, safetyGate.Snapshot().State);
            Assert.False(captureTask.IsCompleted);
            Assert.Equal(FrameSourceCompletion.Unavailable, source.Completion);
            Assert.True(safetyGate.TryResume(indicatorsVerified: true));
            await captureTask;
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
    public async Task StaleHighlightInboxFailureStopsTheNewSafetyGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var inbox = new HighlightInbox();
        var previousGate = CaptureTestSupport.StartedSafetyGate();
        Assert.True(inbox.TryEnqueue(
            new HighlightEvent("stale", 0, HighlightSource.Screen, 10),
            previousGate));
        inbox.StopAccepting();
        var safetyGate = CaptureTestSupport.StartedSafetyGate();

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureCoordinator.RunAsync(
                new SyntheticFrameSource([]),
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10),
                safetyGate,
                highlightInbox: inbox));

            Assert.Equal(CaptureSafetyState.Stopping, safetyGate.Snapshot().State);
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
    public async Task RunProducesRecoverableReadySessionAndFinalizesEncoder()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var encoder = new RecordingEncoder();
        var metrics = new CaptureMetrics();
        var source = new SyntheticFrameSource(
        [
            new FrameSample(0, new byte[] { 0 }, 2, 2),
            new FrameSample(10, new byte[] { 1 }, 2, 2),
            new FrameSample(20, new byte[] { 2 }, 2, 2)
        ]);

        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog")))
            {
                var result = await CaptureCoordinator.RunAsync(
                    source,
                    new VirtualClock(),
                    encoder,
                    root,
                    "session-1",
                    new CaptureRunOptions(durationTicks: 30, intervalTicks: 10, maxFrameAgeTicks: 10),
                    CaptureTestSupport.StartedSafetyGate(),
                    catalog,
                    metrics: metrics);

                Assert.Equal(3, result.SlotCount);
                Assert.Equal(0, result.GapCount);
                Assert.Equal(3, encoder.Frames.Count);
                Assert.True(encoder.IsFinalized);
                Assert.Empty(await catalog.GetInterruptedSessionsAsync());
            }

            var manifestPath = Path.Combine(root, "sessions", "session-1", "manifest.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.Equal("Ready", manifest.RootElement.GetProperty("status").GetString());
            Assert.Equal(3, manifest.RootElement.GetProperty("slots").GetArrayLength());
            Assert.Equal(3, manifest.RootElement.GetProperty("metrics").GetProperty("scheduledSlotCount").GetInt32());
            Assert.Equal(3, manifest.RootElement.GetProperty("metrics").GetProperty("filledSlotCount").GetInt32());
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
    public async Task FinalizeFailureMarksSessionInterruptedAndCatalogRecoverable()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var source = new SyntheticFrameSource(
        [
            new FrameSample(0, new byte[] { 1 }, 2, 2)
        ]);

        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog")))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureCoordinator.RunAsync(
                    source,
                    new VirtualClock(),
                    new FailingFinalizeEncoder(),
                    root,
                    "session-1",
                    new CaptureRunOptions(10, 10, 10),
                    CaptureTestSupport.StartedSafetyGate(),
                    catalog));

                var session = Assert.Single(await catalog.GetInterruptedSessionsAsync());
                Assert.Equal("interrupted", session.Status);
            }

            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "sessions", "session-1", "manifest.json")));
            Assert.Equal("Interrupted", manifest.RootElement.GetProperty("status").GetString());
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
    public async Task OversizeFrameIsDroppedAndRecordedWithoutBreakingTheTimeline()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var metrics = new CaptureMetrics();
        var source = new SyntheticFrameSource(
        [
            new FrameSample(0, new byte[33 * 1024 * 1024], 1, 1),
            new FrameSample(10, new byte[] { 2 }, 1, 1)
        ]);

        try
        {
            await CaptureCoordinator.RunAsync(
                source,
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(20, 10, 10),
                CaptureTestSupport.StartedSafetyGate(),
                metrics: metrics);

            var snapshot = metrics.Snapshot();
            Assert.Equal(1, snapshot.DroppedFrameCount);
            Assert.Equal(1, snapshot.DropCounts["oversizeframe"]);
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
    public async Task UserPauseIsPersistedAndElidedFromTheActiveTimeline()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var clock = new PausingClock(safetyGate);

        try
        {
            var captureTask = CaptureCoordinator.RunAsync(
                new SyntheticFrameSource(
                [
                    new FrameSample(0, new byte[] { 0 }, 2, 2),
                    new FrameSample(10, new byte[] { 1 }, 2, 2),
                    new FrameSample(20, new byte[] { 2 }, 2, 2)
                ]),
                clock,
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(durationTicks: 20, intervalTicks: 10, maxFrameAgeTicks: 10),
                safetyGate);

            await clock.Paused.WaitAsync(TimeSpan.FromSeconds(5));
            clock.AdvanceTo(15);
            Assert.True(safetyGate.TryResume(indicatorsVerified: true));
            var result = await captureTask;

            Assert.Equal(30, result.StopTicks);
            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "sessions", "session-1", "manifest.json")));
            var pause = Assert.Single(manifest.RootElement.GetProperty("pauses").EnumerateArray());
            Assert.Equal(5, pause.GetProperty("startTicks").GetInt64());
            Assert.Equal(15, pause.GetProperty("endTicks").GetInt64());
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
    public async Task PauseRecomputesTheWallTargetBeforeSelectingTheNextSlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var resumeDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ResumedFrameSource(resumeDelivery.Task);
        var clock = new PausingClock(safetyGate, source.Delivered);
        var encoder = new RecordingEncoder();

        try
        {
            var captureTask = CaptureCoordinator.RunAsync(
                source,
                clock,
                encoder,
                root,
                "session-1",
                new CaptureRunOptions(durationTicks: 20, intervalTicks: 10, maxFrameAgeTicks: 10),
                safetyGate);

            await clock.Paused.WaitAsync(TimeSpan.FromSeconds(5));
            clock.AdvanceTo(15);
            Assert.True(safetyGate.TryResume(indicatorsVerified: true));
            resumeDelivery.SetResult();
            await captureTask;

            Assert.Equal(2, encoder.Frames.Count);
            Assert.Equal(20, encoder.Frames[1].Sample!.SourceTimestampTicks);
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
    public async Task StoragePreflightRejectsAReservationBeforeCreatingTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var source = new SyntheticFrameSource([]);
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var inbox = new HighlightInbox();

        try
        {
            await Assert.ThrowsAsync<InsufficientStorageException>(() => CaptureCoordinator.RunAsync(
                source,
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10, reserveBytes: long.MaxValue),
                safetyGate,
                highlightInbox: inbox));

            Assert.False(Directory.Exists(Path.Combine(root, "sessions", "session-1")));
            Assert.False(inbox.TryEnqueue(
                new HighlightEvent("after-failure", 0, HighlightSource.Screen, 1),
                safetyGate));
            Assert.Equal(CaptureSafetyState.Stopping, safetyGate.Snapshot().State);
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
    public async Task CatalogFailureKeepsAlreadyAcceptedHighlightAvailableForRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var inbox = new HighlightInbox();
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var highlight = new HighlightEvent("catalog-retry", 0, HighlightSource.Screen, 10);
        Assert.True(inbox.TryEnqueue(highlight, safetyGate));
        CatalogStore? catalog = null;
        var catalogDisposed = false;

        try
        {
            catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await Assert.ThrowsAnyAsync<Exception>(() => CaptureCoordinator.RunAsync(
                new SyntheticFrameSource([]),
                new VirtualClock(),
                new DisposingCatalogEncoder(catalog, () => catalogDisposed = true),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10),
                safetyGate,
                catalog,
                highlightInbox: inbox));

            Assert.Same(highlight, Assert.Single(inbox.Drain()));
        }
        finally
        {
            if (catalog is not null && !catalogDisposed)
            {
                await catalog.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CatalogFailureAfterCompletionDoesNotDemoteReadyManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var catalogPath = Path.Combine(root, "catalog");
        CatalogStore? catalog = null;

        try
        {
            catalog = await CatalogStore.OpenAsync(catalogPath);
            // Fail only the final "ready" write, like a catalog that dies after capture completes.
            catalog.BeforeSave = static record =>
            {
                if (record.Status == "ready")
                {
                    throw new IOException("synthetic catalog failure");
                }
            };

            await Assert.ThrowsAnyAsync<Exception>(() => CaptureCoordinator.RunAsync(
                new SyntheticFrameSource([]),
                new VirtualClock(),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10),
                CaptureTestSupport.StartedSafetyGate(),
                catalog));

            using (var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "sessions", "session-1", "manifest.json"))))
            {
                Assert.Equal("Ready", manifest.RootElement.GetProperty("status").GetString());
            }

            Assert.Equal("finalizing", (await catalog.GetSessionAsync("session-1"))!.Status);
        }
        finally
        {
            if (catalog is not null)
            {
                await catalog.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CooperativeStopCatalogFailureDoesNotDemoteReadyManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        var catalogPath = Path.Combine(root, "catalog");
        CatalogStore? catalog = null;
        using var stopController = new CaptureStopController();

        try
        {
            catalog = await CatalogStore.OpenAsync(catalogPath);
            // Fail only the final "ready" write, like a catalog that dies after capture completes.
            catalog.BeforeSave = static record =>
            {
                if (record.Status == "ready")
                {
                    throw new IOException("synthetic catalog failure");
                }
            };

            await Assert.ThrowsAnyAsync<Exception>(() => CaptureCoordinator.RunAsync(
                new SyntheticFrameSource([]),
                new StopAtBoundaryClock(stopController),
                new RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(10, 10, 10),
                CaptureTestSupport.StartedSafetyGate(),
                catalog,
                stopController: stopController));

            using (var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "sessions", "session-1", "manifest.json"))))
            {
                Assert.Equal("Ready", manifest.RootElement.GetProperty("status").GetString());
            }

            Assert.Equal("finalizing", (await catalog.GetSessionAsync("session-1"))!.Status);
        }
        finally
        {
            if (catalog is not null)
            {
                await catalog.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FailingFinalizeEncoder : IEncoder
    {
        public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Synthetic finalization failure.");
        }
    }

    private sealed class DisposingCatalogEncoder(CatalogStore catalog, Action markDisposed) : IEncoder
    {
        private int disposed;

        public async ValueTask WriteAsync(
            EncodedFrame frame,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await catalog.DisposeAsync();
                markDisposed();
            }
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingFrameSource : IFrameSource
    {
        public bool ReadStarted { get; private set; }

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ExplicitlyUnavailableFrameSource : IFrameSource, IFrameSourceLifecycle
    {
        public FrameSourceCompletion Completion { get; private set; }

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completion = FrameSourceCompletion.Unavailable;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StopAtBoundaryClock(CaptureStopController stopController) : IClock
    {
        private int stopRequested;

        public long NowTicks { get; private set; }

        public async ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetTicks == 10 && Interlocked.Exchange(ref stopRequested, 1) == 0)
            {
                stopController.RequestStop();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            NowTicks = Math.Max(NowTicks, targetTicks);
        }
    }

    private sealed class ResumedFrameSource(Task resumeDelivery) : IFrameSource
    {
        private readonly TaskCompletionSource delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Completes once the consumer has taken (and moved past) the last frame.
        public Task Delivered => delivered.Task;

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await resumeDelivery.WaitAsync(cancellationToken);
            yield return new FrameSample(20, new byte[] { 2 }, 2, 2);
            delivered.TrySetResult();
            await Task.Yield();
        }
    }

    private sealed class PausingClock(CaptureSafetyGate safetyGate, Task? resumedFrameDelivered = null) : IClock
    {
        private int pauseRequested;
        private readonly TaskCompletionSource<object?> paused = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public long NowTicks { get; private set; }

        public Task Paused => paused.Task;

        private readonly TaskCompletionSource<object?> advanced = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Once paused, time stands still until the test advances it and resumes;
            // otherwise the capture loop can race the clock past the resume point.
            if (paused.Task.IsCompleted)
            {
                await advanced.Task.WaitAsync(cancellationToken);
                while (safetyGate.Snapshot().State == CaptureSafetyState.Paused)
                {
                    await Task.Delay(1, cancellationToken);
                }
            }

            if (targetTicks > NowTicks)
            {
                if (targetTicks == 10 && Interlocked.Exchange(ref pauseRequested, 1) == 0)
                {
                    NowTicks = 5;
                    safetyGate.Pause(CapturePauseReason.UserRequested, NowTicks);
                    paused.TrySetResult(null);
                }
                else
                {
                    if (targetTicks == 20 && resumedFrameDelivered is not null)
                        await resumedFrameDelivered.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    NowTicks = targetTicks;
                }
            }
        }

        public void AdvanceTo(long ticks)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(ticks, NowTicks);
            NowTicks = ticks;
            advanced.TrySetResult(null);
        }
    }
}
