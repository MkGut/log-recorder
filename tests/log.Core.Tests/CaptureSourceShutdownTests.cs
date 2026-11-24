using Log.Core.Capture;
using Xunit;

namespace Log.Core.Tests;

// The registry is process-wide, so tests that hold or leave a lease run serially.
[Collection("CaptureSessionRegistry")]
public sealed class CaptureSourceShutdownTests
{
    [Fact]
    public async Task ReportsWhenAReaderIgnoresCancellationPastTheBound()
    {
        var sourceCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var stopped = await CaptureSourceShutdown.StopAsync(sourceCompletion.Task, cancellation);

        Assert.False(stopped);
        Assert.True(cancellation.IsCancellationRequested);

        // A timed-out reader stays registered until it exits; let it exit so it
        // cannot block later captures in this process.
        sourceCompletion.SetResult();
    }

    [Fact]
    public async Task BlocksASecondCaptureUntilATimedOutReaderExits()
    {
        var sourceCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var stopped = await CaptureSourceShutdown.StopAsync(sourceCompletion.Task, cancellation);

        Assert.False(stopped);
        Assert.Throws<InvalidOperationException>(() => CaptureSessionRegistry.Acquire(
            Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"))));

        sourceCompletion.SetResult();
        IDisposable? reacquired = null;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (reacquired is null && DateTime.UtcNow < deadline)
            {
                try
                {
                    reacquired = CaptureSessionRegistry.Acquire(
                        Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N")));
                }
                catch (InvalidOperationException)
                {
                    await Task.Delay(10);
                }
            }

            Assert.NotNull(reacquired);
        }
        finally
        {
            reacquired?.Dispose();
        }
    }

    [Fact]
    public async Task TreatsAnAlreadyFaultedExpectedReaderShutdownAsStopped()
    {
        var sourceTask = Task.FromException(new IOException("Synthetic reader shutdown."));
        using var cancellation = new CancellationTokenSource();

        var stopped = await CaptureSourceShutdown.StopAsync(sourceTask, cancellation);

        Assert.True(stopped);
    }

    [Fact]
    public async Task PropagatesAnUnexpectedReaderFaultBeforeSchedulingTheNextSlot()
    {
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        using var tracker = new CapturePauseTracker(gate, new Log.Testing.VirtualClock());
        using var sourceHealthCancellation = new CancellationTokenSource();
        var sourceTask = Task.FromException(new InvalidOperationException("Synthetic reader bug."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaptureSourceHealth.WaitUntilAsync(
                tracker,
                targetTicks: 100,
                sourceTask,
                sourceHealthCancellation).AsTask());

        Assert.Equal("Synthetic reader bug.", exception.Message);
    }
}
