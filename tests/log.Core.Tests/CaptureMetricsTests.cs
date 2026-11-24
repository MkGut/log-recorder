using Log.Core.Capture;
using Log.Core.Timeline;
using Log.Testing;
using Xunit;

namespace Log.Core.Tests;

public sealed class CaptureMetricsTests
{
    [Fact]
    public void SnapshotReportsSlotsFramesAndMaximumJitter()
    {
        var metrics = new CaptureMetrics();
        metrics.RecordFrameArrived();
        metrics.RecordFrameArrived();
        metrics.RecordScheduledSlot(100, 103);
        metrics.RecordSlot(isGap: false, isDuplicate: true);
        metrics.RecordScheduledSlot(110, 110);
        metrics.RecordSlot(isGap: true, isDuplicate: false);

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.ScheduledSlotCount);
        Assert.Equal(1, snapshot.FilledSlotCount);
        Assert.Equal(1, snapshot.DuplicateSlotCount);
        Assert.Equal(1, snapshot.GapSlotCount);
        Assert.Equal(2, snapshot.FrameArrivalCount);
        Assert.Equal(3, snapshot.MaxJitterTicks);
    }

    [Fact]
    public void SnapshotIncludesDropReasons()
    {
        var metrics = new CaptureMetrics();
        metrics.RecordDropped(CaptureDropReason.OversizeFrame);
        metrics.RecordDropped(CaptureDropReason.OversizeFrame);
        metrics.RecordDropped(CaptureDropReason.Scheduler);

        var snapshot = metrics.Snapshot();

        Assert.Equal(3, snapshot.DroppedFrameCount);
        Assert.Equal(2, snapshot.DropCounts["oversizeframe"]);
        Assert.Equal(1, snapshot.DropCounts["scheduler"]);
    }

    [Fact]
    public void SafetyGateRequiresVerifiedIndicatorsAndNeverAutoResumes()
    {
        var gate = new CaptureSafetyGate();

        Assert.False(gate.Snapshot().CanPersistFrames);
        Assert.Throws<InvalidOperationException>(() => gate.Start(indicatorsVerified: false));

        gate.Start(indicatorsVerified: true);
        Assert.True(gate.Snapshot().CanAcceptHighlights);

        var paused = gate.Pause(CapturePauseReason.WorkstationLocked);
        Assert.Equal(CaptureSafetyState.Paused, paused.State);
        Assert.Equal(CapturePauseReason.WorkstationLocked, paused.PauseReason);
        Assert.False(paused.CanPersistFrames);
        Assert.False(paused.CanAcceptHighlights);
        Assert.False(gate.TryResume(indicatorsVerified: false));
        Assert.Equal(CaptureSafetyState.Paused, gate.Snapshot().State);
    }

    [Fact]
    public void SafetyGateResumesOnlyAfterExplicitVerifiedResume()
    {
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        gate.Pause(CapturePauseReason.SourceUnavailable);

        Assert.True(gate.TryResume(indicatorsVerified: true));
        var resumed = gate.Snapshot();
        Assert.Equal(CaptureSafetyState.Recording, resumed.State);
        Assert.Null(resumed.PauseReason);
        Assert.False(gate.TryResume(indicatorsVerified: true));
    }

    [Fact]
    public void SafetyGatePublishesStateTransitionsForShellObservers()
    {
        var gate = new CaptureSafetyGate();
        var states = new List<CaptureSafetyState>();
        gate.StateChanged += snapshot => states.Add(snapshot.State);

        gate.Start(indicatorsVerified: true);
        gate.Pause(CapturePauseReason.UserRequested, atTicks: 10);
        gate.Pause(CapturePauseReason.FrameStale, atTicks: 20);
        Assert.True(gate.TryResume(indicatorsVerified: true));
        gate.Stop();

        Assert.Equal(
            [
                CaptureSafetyState.Recording,
                CaptureSafetyState.Paused,
                CaptureSafetyState.Recording,
                CaptureSafetyState.Stopping
            ],
            states);
    }

    [Fact]
    public void SafetyGateVersionsEveryPersistingStateTransition()
    {
        var gate = new CaptureSafetyGate();

        var initial = gate.Snapshot();
        gate.Start(indicatorsVerified: true);
        var recording = gate.Snapshot();
        gate.Pause(CapturePauseReason.UserRequested, atTicks: 10);
        var paused = gate.Snapshot();
        Assert.True(gate.TryResume(indicatorsVerified: true));
        var resumed = gate.Snapshot();
        gate.Stop();
        var stopping = gate.Snapshot();

        Assert.Equal(0, initial.TransitionVersion);
        Assert.True(recording.TransitionVersion > initial.TransitionVersion);
        Assert.True(paused.TransitionVersion > recording.TransitionVersion);
        Assert.True(resumed.TransitionVersion > paused.TransitionVersion);
        Assert.True(stopping.TransitionVersion > resumed.TransitionVersion);
    }

    [Fact]
    public void SafetyGateObserversCanReadStateWithoutBlockingTheTransition()
    {
        var gate = new CaptureSafetyGate();
        gate.StateChanged += _ =>
        {
            var snapshotTask = Task.Run(gate.Snapshot);
            Assert.True(snapshotTask.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(CaptureSafetyState.Recording, snapshotTask.Result.State);
        };

        gate.Start(indicatorsVerified: true);

        Assert.Equal(CaptureSafetyState.Recording, gate.Snapshot().State);
    }

    [Fact]
    public void SafetyGateCannotPauseBeforeStartOrMoveTheOriginalPauseBoundary()
    {
        var gate = new CaptureSafetyGate();
        Assert.Throws<InvalidOperationException>(() => gate.Pause(CapturePauseReason.UserRequested, 10));

        gate.Start(indicatorsVerified: true);
        gate.Pause(CapturePauseReason.UserRequested, 10);
        var repeated = gate.Pause(CapturePauseReason.FrameStale, 20);

        Assert.Equal(CapturePauseReason.FrameStale, repeated.PauseReason);
        Assert.Equal(10, repeated.PauseStartedAtTicks);
    }

    [Fact]
    public async Task PauseTrackerRecordsTheElapsedRangeAfterExplicitResume()
    {
        var clock = new VirtualClock(100);
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        clock.AdvanceBy(10);
        var tracker = new CapturePauseTracker(gate, clock);

        gate.Pause(CapturePauseReason.UserRequested, clock.NowTicks);
        var waiting = tracker.WaitForRecordingAsync().AsTask();

        Assert.False(waiting.IsCompleted);
        clock.AdvanceBy(10);
        Assert.True(gate.TryResume(indicatorsVerified: true));
        await waiting;

        Assert.Equal([new PauseRange(110, 120)], tracker.DrainUnpersistedPauses());
    }

    [Fact]
    public async Task PauseTrackerCanRequeueAFailedPersistenceBatch()
    {
        var clock = new VirtualClock(100);
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        var tracker = new CapturePauseTracker(gate, clock);

        gate.Pause(CapturePauseReason.UserRequested, clock.NowTicks);
        clock.AdvanceBy(10);
        Assert.True(gate.TryResume(indicatorsVerified: true));

        var pending = tracker.DrainUnpersistedPauses();
        tracker.RequeuePauses(pending.Count);

        Assert.Equal(pending, tracker.DrainUnpersistedPauses());
    }

    [Fact]
    public async Task PauseTrackerCancelsTheClockDelayWhenSafetyStateChanges()
    {
        var clock = new CancellationProbeClock();
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        using var tracker = new CapturePauseTracker(gate, clock);

        var waiting = tracker.WaitUntilAsync(100).AsTask();
        await clock.DelayStarted.WaitAsync(TimeSpan.FromSeconds(2));

        gate.Pause(CapturePauseReason.WorkstationLocked, atTicks: 10);

        await clock.DelayCanceled.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);

        Assert.True(gate.TryResume(indicatorsVerified: true));
        await waiting;
    }

    private sealed class CancellationProbeClock : IClock
    {
        private readonly TaskCompletionSource delayStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource delayCanceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int delayCount;

        public long NowTicks { get; private set; }
        public Task DelayStarted => delayStarted.Task;
        public Task DelayCanceled => delayCanceled.Task;

        public async ValueTask DelayUntilAsync(
            long targetTicks,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref delayCount) == 1)
            {
                delayStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    delayCanceled.TrySetResult();
                    throw;
                }

                return;
            }

            NowTicks = targetTicks;
        }
    }
}
