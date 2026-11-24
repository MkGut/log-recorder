using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Log.Testing;
using Xunit;

namespace Log.Integration.Tests;

public sealed class AdaptiveSingleCaptureTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    [Fact]
    public async Task IndefiniteCaptureRunsPastThreeMinutesAndCompactsOnStop()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var stopController = new CaptureStopController();
        var source = new AdaptiveFrameSource();
        var clock = new StopAtClock(stopController, 182 * Second);
        try
        {
            var result = await CaptureCoordinator.RunAsync(
                source,
                clock,
                new Log.Testing.RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(0, Second, 10 * Second, sourceMode: "screen"),
                CaptureTestSupport.StartedSafetyGate(),
                stopController: stopController);

            Assert.True(result.StopTicks >= 182 * Second);
            Assert.Equal([Second, 2 * Second, 4 * Second], source.Intervals);
            await using var store = await SessionStore.OpenAsync(root, "session-1");
            Assert.Equal(4 * Second, store.Manifest.CaptureIntervalTicks);
            Assert.Equal(46, store.Manifest.Slots.Count);
            Assert.Equal(0, store.Manifest.Slots[0].TargetTicks);
            Assert.Equal(180 * Second, store.Manifest.Slots[^1].TargetTicks);
            var retainedBeforeBoundary = store.Manifest.Slots.Single(slot => slot.TargetTicks == 32 * Second);
            Assert.StartsWith("screen/slot-a-", retainedBeforeBoundary.AssetReference);
            Assert.StartsWith("screen/slot-a-", store.Manifest.Slots.Single(slot => slot.TargetTicks == 60 * Second).AssetReference);
            Assert.Equal(SessionStatus.Ready, store.Manifest.Status);

            // A clean adaptive stop must record a completion timestamp, not
            // just flip the status to Ready.
            Assert.NotNull(store.Manifest.StoppedAtUtc);
            Assert.True(store.Manifest.StoppedAtUtc >= store.Manifest.StartedAtUtc);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Root cause: 420s, 900s, and 1860s are stage boundaries where the
    // interval that activates there does not evenly divide the boundary
    // itself (420/8, 900/16, and 1860/32 all have a remainder). A live run
    // that advanced its schedule pointer with the raw (unaligned) boundary
    // value truncated its slot index onto one the compaction just above had
    // already assigned to a retained target, throwing "Slot N already
    // exists" the first time this happened, at the 420s mark. This test
    // drives the real CaptureCoordinator well past all three such boundaries
    // and proves the fix holds for the whole run, not just the first one.
    [Fact]
    public async Task IndefiniteCaptureCrossesUnalignedStageBoundariesWithoutCollidingSlotIndexes()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var stopController = new CaptureStopController();
        var source = new AdaptiveFrameSource();
        const long StopAtTicks = 1900 * Second;
        var clock = new StopAtClock(stopController, StopAtTicks);
        try
        {
            var result = await CaptureCoordinator.RunAsync(
                source,
                clock,
                new Log.Testing.RecordingEncoder(),
                root,
                "session-long",
                new CaptureRunOptions(0, Second, 10 * Second, sourceMode: "screen"),
                CaptureTestSupport.StartedSafetyGate(),
                stopController: stopController);

            Assert.True(result.StopTicks >= StopAtTicks);

            // Every cadence up to and including the 32s stage (activated at
            // the unaligned 1860s boundary) must have been visited, in order,
            // with no crash along the way.
            Assert.Equal(
                [Second, 2 * Second, 4 * Second, 8 * Second, 16 * Second, 32 * Second],
                source.Intervals);

            await using var store = await SessionStore.OpenAsync(root, "session-long");
            Assert.Equal(SessionStatus.Ready, store.Manifest.Status);
            Assert.Equal(32 * Second, store.Manifest.CaptureIntervalTicks);
            Assert.NotNull(store.Manifest.StoppedAtUtc);
            Assert.True(store.Manifest.StoppedAtUtc >= store.Manifest.StartedAtUtc);

            // No two retained slots may share an index or a target -- this is
            // precisely the invariant "Slot N already exists" protects, and
            // the one the unaligned-boundary bug used to violate.
            var indexes = store.Manifest.Slots.Select(static slot => slot.Index).ToList();
            Assert.Equal(indexes.Count, indexes.Distinct().Count());
            var targets = store.Manifest.Slots.Select(static slot => slot.TargetTicks).ToList();
            Assert.Equal(targets.Count, targets.Distinct().Count());

            // The retained set must match the documented policy exactly for
            // the final cadence: dense 0..N-1 indexes over the canonical
            // target grid AdaptiveSamplingPolicy defines for this duration.
            Assert.NotNull(store.Manifest.StoppedAtTicks);
            var activeDurationAtStop = store.Manifest.StoppedAtTicks!.Value - store.Manifest.StartedAtTicks;
            var expectedTargets = AdaptiveSamplingPolicy.BuildRetainedTargets(activeDurationAtStop);
            Assert.Equal(expectedTargets.Count, store.Manifest.Slots.Count);
            Assert.Equal(Enumerable.Range(0, expectedTargets.Count), indexes.OrderBy(static index => index));
            Assert.Equal(
                expectedTargets,
                store.Manifest.Slots.OrderBy(static slot => slot.Index).Select(static slot => slot.TargetTicks));

            // Reprojected targets may intentionally share an asset when no fresh
            // source sample existed at the new target: the last frame is held
            // forward as an explicit duplicate (ADR-0009) rather than blacked out.
            // Such reuse is legitimate, but a reference must still back at most one
            // freshly sampled (non-duplicate) slot -- two originals sharing one PNG
            // would mean a real overwrite collision.
            foreach (var group in store.Manifest.Slots.GroupBy(static slot => slot.AssetReference, StringComparer.Ordinal))
            {
                Assert.True(group.Count(static slot => !slot.IsDuplicate) <= 1);
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

    // Regression for the "starts fine, then goes black" failure. The shell starts
    // an indefinite capture with a 10s max-frame-age, but the adaptive schedule
    // grows the sample interval to 60s. A still screen (one frame, then no change)
    // must be held forward onto every retained target as a duplicate; the fixed
    // 10s ceiling used to age it out into gaps that render black. Drive the real
    // CaptureCoordinator across every stage to the 60s cap and prove the built
    // render timeline carries a real screen asset on every slot -- no black gaps.
    [Fact]
    public async Task IndefiniteCaptureHoldsAStillScreenAcrossCoarseStagesWithoutBlackGaps()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var stopController = new CaptureStopController();
        var source = new StaticScreenFrameSource();
        const long StopAtTicks = 3901 * Second;
        var clock = new StopAtClock(stopController, StopAtTicks);
        try
        {
            var result = await CaptureCoordinator.RunAsync(
                source,
                clock,
                new Log.Testing.RecordingEncoder(),
                root,
                "session-still",
                // Exactly the shell's indefinite request: a 10s freshness ceiling
                // that is smaller than the adaptive schedule's own interval.
                new CaptureRunOptions(0, Second, 10 * Second, sourceMode: "screen"),
                CaptureTestSupport.StartedSafetyGate(),
                stopController: stopController);

            Assert.True(result.StopTicks >= StopAtTicks);
            // The run climbed the whole adaptive ladder to the 60s cap.
            Assert.Equal(
                [Second, 2 * Second, 4 * Second, 8 * Second, 16 * Second, 32 * Second,
                    AdaptiveSamplingPolicy.MaximumIntervalTicks],
                source.Intervals);

            await using var store = await SessionStore.OpenAsync(root, "session-still");
            Assert.Equal(SessionStatus.Ready, store.Manifest.Status);
            Assert.Equal(AdaptiveSamplingPolicy.MaximumIntervalTicks, store.Manifest.CaptureIntervalTicks);

            // The rendered timeline covers the full retained target lattice, and
            // every slot resolves to a real held screen asset -- never a gap. A 10s
            // ceiling would have blacked out every target beyond the first 10s.
            var renderInput = SessionRenderInputBuilder.Build(store.Manifest);
            Assert.NotNull(store.Manifest.StoppedAtTicks);
            var activeDurationAtStop = store.Manifest.StoppedAtTicks!.Value - store.Manifest.StartedAtTicks;
            var expectedTargets = AdaptiveSamplingPolicy.BuildRetainedTargets(activeDurationAtStop);
            Assert.Equal(expectedTargets.Count, renderInput.Slots.Count);
            Assert.All(renderInput.Slots, slot =>
            {
                Assert.False(slot.ScreenIsGap);
                Assert.False(string.IsNullOrEmpty(slot.ScreenAssetReference));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // A still screen: one frame at t=0, then no further changes (like WGC on a
    // static desktop). Honors the sampling-interval control so the run records the
    // adaptive ladder, and reports Completed -- which keeps the capture running and
    // holding the last frame rather than pausing on an unavailable source.
    private sealed class StaticScreenFrameSource :
        IFrameSource,
        IFrameSourceLifecycle,
        IFrameSourceSamplingControl
    {
        private readonly object sync = new();
        private readonly List<long> intervals = [];

        public IReadOnlyList<long> Intervals
        {
            get
            {
                lock (sync)
                {
                    return intervals.ToArray();
                }
            }
        }

        public FrameSourceCompletion Completion { get; private set; }

        public long SampleIntervalTicks { get; private set; }

        public void SetSampleInterval(long intervalTicks)
        {
            lock (sync)
            {
                SampleIntervalTicks = intervalTicks;
                if (intervals.Count == 0 || intervals[^1] != intervalTicks)
                {
                    intervals.Add(intervalTicks);
                }
            }
        }

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FrameSample(0, new byte[16], 2, 2);
            await Task.Yield();
            Completion = FrameSourceCompletion.Completed;
        }
    }

    // Regression: stopping while paused closed the open pause at a clock reading
    // later than the stop time already captured, so CompleteAsync threw
    // "Pause ranges must end before the session stops" and the save failed.
    [Fact]
    public async Task StoppingWhilePausedStillSavesTheSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var stopController = new CaptureStopController();
        var safetyGate = CaptureTestSupport.StartedSafetyGate();
        var clock = new PauseOnceClock(safetyGate, pauseAtTicks: 3 * Second);
        try
        {
            var captureTask = CaptureCoordinator.RunAsync(
                new AdaptiveFrameSource(),
                clock,
                new Log.Testing.RecordingEncoder(),
                root,
                "session-1",
                new CaptureRunOptions(0, Second, 10 * Second, sourceMode: "screen"),
                safetyGate,
                stopController: stopController);

            await clock.Paused.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CaptureSafetyState.Paused, safetyGate.Snapshot().State);
            stopController.RequestStop();

            var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(10));

            await using var store = await SessionStore.OpenAsync(root, "session-1");
            Assert.Equal(SessionStatus.Ready, store.Manifest.Status);
            var pause = Assert.Single(store.Manifest.Pauses);
            Assert.True(result.StopTicks >= pause.EndTicks);
            Assert.Equal(result.StopTicks, store.Manifest.StoppedAtTicks);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Every read moves time forward, like a real clock, so a pause closed at stop
    // time ends later than any reading taken before it.
    private sealed class PauseOnceClock(CaptureSafetyGate safetyGate, long pauseAtTicks) : IClock
    {
        private readonly TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long ticks;
        private int pauseRequested;

        public long NowTicks => Interlocked.Increment(ref ticks);

        public Task Paused => paused.Task;

        public ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Math.Max(Interlocked.Read(ref ticks), targetTicks);
            Interlocked.Exchange(ref ticks, now);
            if (now >= pauseAtTicks && Interlocked.Exchange(ref pauseRequested, 1) == 0)
            {
                safetyGate.Pause(CapturePauseReason.UserRequested, now);
                paused.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class StopAtClock(CaptureStopController stopController, long stopAtTicks) : IClock
    {
        public long NowTicks { get; private set; }

        public ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NowTicks = Math.Max(NowTicks, targetTicks);
            if (NowTicks >= stopAtTicks)
            {
                stopController.RequestStop();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdaptiveFrameSource : IFrameSource, IFrameSourceSamplingControl
    {
        private readonly object sync = new();
        private readonly List<long> intervals = [];
        private long nextSourceTicks;

        public IReadOnlyList<long> Intervals
        {
            get
            {
                lock (sync)
                {
                    return intervals.ToArray();
                }
            }
        }

        public long SampleIntervalTicks { get; private set; }

        public void SetSampleInterval(long intervalTicks)
        {
            lock (sync)
            {
                SampleIntervalTicks = intervalTicks;
                if (intervals.Count == 0 || intervals[^1] != intervalTicks)
                {
                    intervals.Add(intervalTicks);
                }
            }
        }

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceTicks = Interlocked.Add(ref nextSourceTicks, Second) - Second;
                yield return new FrameSample(sourceTicks, new byte[] { 0, 0, 0, 255 }, 1, 1);
                await Task.Delay(1, cancellationToken);
            }
        }
    }
}
