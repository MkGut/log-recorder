using Log.Core.Diagnostics;

namespace Log.Core.Capture;

public enum CaptureSafetyState
{
    AwaitingStart,
    Recording,
    Paused,
    Stopping
}

public enum CapturePauseReason
{
    UserRequested,
    WorkstationLocked,
    SecureDesktop,
    SystemSuspended,
    SourceUnavailable,
    FrameStale,
    TrayUnavailable
}

public sealed record CaptureSafetySnapshot(
    CaptureSafetyState State,
    CapturePauseReason? PauseReason,
    long? PauseStartedAtTicks = null)
{
    public long TransitionVersion { get; init; }

    public bool CanPersistFrames => State == CaptureSafetyState.Recording;
    public bool CanAcceptHighlights => State == CaptureSafetyState.Recording;
}

public sealed class CaptureSafetyGate
{
    private readonly object gate = new();
    private readonly object notificationGate = new();
    private TaskCompletionSource stateChanged = CreateStateChangedSource();
    private CaptureSafetyState state = CaptureSafetyState.AwaitingStart;
    private CapturePauseReason? pauseReason;
    private long? pauseStartedAtTicks;
    private long stateVersion;

    public event Action<CaptureSafetySnapshot>? StateChanged;

    public CaptureSafetySnapshot Snapshot()
    {
        lock (gate)
        {
            return CreateSnapshot();
        }
    }

    public void Start(bool indicatorsVerified)
    {
        if (!indicatorsVerified)
        {
            throw new InvalidOperationException(
                "Capture cannot start until its persistent indicators are verified.");
        }

        CaptureSafetySnapshot snapshot;
        lock (notificationGate)
        {
            lock (gate)
            {
                if (state != CaptureSafetyState.AwaitingStart)
                {
                    throw new InvalidOperationException("Capture has already started.");
                }

                state = CaptureSafetyState.Recording;
                pauseReason = null;
                pauseStartedAtTicks = null;
                snapshot = SignalStateChanged();
            }

            NotifyStateChanged(snapshot);
        }
    }

    public CaptureSafetySnapshot Pause(CapturePauseReason reason, long? atTicks = null)
    {
        if (atTicks is long pauseTicks)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(pauseTicks);
        }

        CaptureSafetySnapshot snapshot;
        var notify = false;
        lock (notificationGate)
        {
            lock (gate)
            {
                if (state == CaptureSafetyState.AwaitingStart)
                {
                    throw new InvalidOperationException("Capture has not started.");
                }

                // Finalization is terminal; late platform callbacks must not reopen a pause.
                if (state == CaptureSafetyState.Stopping)
                {
                    return CreateSnapshot();
                }

                var wasPaused = state == CaptureSafetyState.Paused;
                state = CaptureSafetyState.Paused;
                pauseReason = reason;
                if (!wasPaused)
                {
                    pauseStartedAtTicks = atTicks;
                    snapshot = SignalStateChanged();
                    notify = true;
                }
                else if (pauseReason != reason)
                {
                    snapshot = SignalStateChanged();
                    notify = true;
                }
                else
                {
                    snapshot = CreateSnapshot();
                }
            }

            if (notify)
            {
                NotifyStateChanged(snapshot);
            }
        }

        return snapshot;
    }

    public bool TryResume(bool indicatorsVerified)
    {
        if (!indicatorsVerified)
        {
            return false;
        }

        CaptureSafetySnapshot snapshot;
        lock (notificationGate)
        {
            lock (gate)
            {
                if (state != CaptureSafetyState.Paused)
                {
                    return false;
                }

                state = CaptureSafetyState.Recording;
                pauseReason = null;
                pauseStartedAtTicks = null;
                snapshot = SignalStateChanged();
            }

            NotifyStateChanged(snapshot);
        }
        return true;
    }

    public CaptureSafetySnapshot Stop()
    {
        CaptureSafetySnapshot snapshot;
        lock (notificationGate)
        {
            lock (gate)
            {
                if (state == CaptureSafetyState.AwaitingStart)
                {
                    throw new InvalidOperationException("Capture has not started.");
                }

                if (state == CaptureSafetyState.Stopping)
                {
                    return CreateSnapshot();
                }

                state = CaptureSafetyState.Stopping;
                pauseReason = null;
                pauseStartedAtTicks = null;
                snapshot = SignalStateChanged();
            }

            NotifyStateChanged(snapshot);
        }
        return snapshot;
    }

    public Task WaitForStateChangeAsync(
        CaptureSafetySnapshot observedSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observedSnapshot);
        lock (gate)
        {
            if (state != observedSnapshot.State
                || stateVersion != observedSnapshot.TransitionVersion)
            {
                return Task.CompletedTask;
            }

            return stateChanged.Task.WaitAsync(cancellationToken);
        }
    }

    internal bool TryRunWhileRecording(Func<bool> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (notificationGate)
        {
            lock (gate)
            {
                if (state != CaptureSafetyState.Recording)
                {
                    return false;
                }

                return operation();
            }
        }
    }

    private static TaskCompletionSource CreateStateChangedSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void NotifyStateChanged(CaptureSafetySnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<CaptureSafetySnapshot>>())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                CaptureEventSource.Instance.StateObserverFailed();
            }
        }
    }

    private CaptureSafetySnapshot SignalStateChanged()
    {
        stateVersion = checked(stateVersion + 1);
        var snapshot = CreateSnapshot();
        stateChanged.TrySetResult();
        stateChanged = CreateStateChangedSource();
        return snapshot;
    }

    private CaptureSafetySnapshot CreateSnapshot()
    {
        return new CaptureSafetySnapshot(state, pauseReason, pauseStartedAtTicks)
        {
            TransitionVersion = stateVersion
        };
    }
}
