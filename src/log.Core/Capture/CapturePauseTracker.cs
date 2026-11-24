using Log.Core.Timeline;

namespace Log.Core.Capture;

public sealed class CapturePauseTracker : IDisposable
{
    private readonly CaptureSafetyGate safetyGate;
    private readonly IClock clock;
    private readonly object gate = new();
    private readonly List<PauseRange> completedPauses = [];
    private long? openPauseStartTicks;
    private int persistedPauseCount;
    private bool disposed;

    public CapturePauseTracker(CaptureSafetyGate safetyGate, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(safetyGate);
        ArgumentNullException.ThrowIfNull(clock);
        this.safetyGate = safetyGate;
        this.clock = clock;
        safetyGate.StateChanged += OnStateChanged;
    }

    public IReadOnlyList<PauseRange> Pauses
    {
        get
        {
            lock (gate)
            {
                return completedPauses.ToArray();
            }
        }
    }

    public async ValueTask WaitForRecordingAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var snapshot = safetyGate.Snapshot();
            if (snapshot.State == CaptureSafetyState.Recording)
            {
                lock (gate)
                {
                    ClosePauseIfNeeded();
                }
                return;
            }

            if (snapshot.State == CaptureSafetyState.AwaitingStart)
            {
                throw new InvalidOperationException("Capture has not started.");
            }

            if (snapshot.State == CaptureSafetyState.Stopping)
            {
                throw new InvalidOperationException("Capture is stopping.");
            }

            lock (gate)
            {
                openPauseStartTicks ??= snapshot.PauseStartedAtTicks ?? clock.NowTicks;
            }
            await safetyGate.WaitForStateChangeAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask WaitUntilAsync(
        long targetTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);

        while (true)
        {
            await WaitForRecordingAsync(cancellationToken).ConfigureAwait(false);
            if (clock.NowTicks >= targetTicks)
            {
                return;
            }

            var observedSnapshot = safetyGate.Snapshot();
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = clock.DelayUntilAsync(targetTicks, waitCancellation.Token).AsTask();
            var stateChangeTask = safetyGate.WaitForStateChangeAsync(
                observedSnapshot,
                waitCancellation.Token);
            var completed = await Task.WhenAny(delayTask, stateChangeTask).ConfigureAwait(false);
            if (completed == stateChangeTask)
            {
                waitCancellation.Cancel();
                await ObserveCanceledWaitAsync(delayTask).ConfigureAwait(false);
                await stateChangeTask.ConfigureAwait(false);
                continue;
            }

            waitCancellation.Cancel();
            await ObserveCanceledWaitAsync(stateChangeTask).ConfigureAwait(false);
            await delayTask.ConfigureAwait(false);

            // A pause can complete in the same scheduling turn as the timer. Recheck
            // the gate so the next capture slot cannot cross that safety boundary.
            if (safetyGate.Snapshot().State != CaptureSafetyState.Recording)
            {
                continue;
            }

            return;
        }
    }

    private static async ValueTask ObserveCanceledWaitAsync(Task waitTask)
    {
        try
        {
            await waitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void CloseAtCurrentTime()
    {
        var snapshot = safetyGate.Snapshot();
        lock (gate)
        {
            if (snapshot.State == CaptureSafetyState.Paused)
            {
                openPauseStartTicks ??= snapshot.PauseStartedAtTicks ?? clock.NowTicks;
            }

            ClosePauseIfNeeded();
        }
    }

    public IReadOnlyList<PauseRange> DrainUnpersistedPauses()
    {
        lock (gate)
        {
            if (persistedPauseCount == completedPauses.Count)
            {
                return [];
            }

            var pending = completedPauses.Skip(persistedPauseCount).ToArray();
            persistedPauseCount = completedPauses.Count;
            return pending;
        }
    }

    internal void RequeuePauses(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, persistedPauseCount);
            persistedPauseCount -= count;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        safetyGate.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(CaptureSafetySnapshot snapshot)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (snapshot.State == CaptureSafetyState.Paused)
            {
                openPauseStartTicks ??= snapshot.PauseStartedAtTicks ?? clock.NowTicks;
            }
            else if (snapshot.State is CaptureSafetyState.Recording or CaptureSafetyState.Stopping)
            {
                ClosePauseIfNeeded();
            }
        }
    }

    private void ClosePauseIfNeeded()
    {
        if (openPauseStartTicks is not long startTicks)
        {
            return;
        }

        var endTicks = Math.Max(startTicks, clock.NowTicks);
        if (endTicks > startTicks)
        {
            completedPauses.Add(new PauseRange(startTicks, endTicks));
        }

        openPauseStartTicks = null;
    }
}
