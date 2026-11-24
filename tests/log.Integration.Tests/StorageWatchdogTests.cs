using Log.Core.Capture;
using Log.Engine;
using Xunit;

namespace Log.Integration.Tests;

public sealed class StorageWatchdogTests
{
    [Fact]
    public async Task RequestsStopWhenFreeSpaceReachesReserve()
    {
        var breached = 0;
        // Free space falls to the reserve on the second poll.
        var readings = new Queue<long>([10_000, 1_000]);

        await StorageWatchdog.RunAsync(
            availableBytes: () => readings.Count > 0 ? readings.Dequeue() : 1_000,
            reserveBytes: 1_000,
            pollInterval: TimeSpan.FromMilliseconds(1),
            onReserveBreached: () => breached++,
            onProbeFailed: exception => throw new InvalidOperationException("Unexpected storage probe failure.", exception),
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, breached);
    }

    [Fact]
    public async Task DoesNotStopWhileAmpleSpaceRemainsThenCancels()
    {
        var breached = 0;
        using var cts = new CancellationTokenSource();

        var run = StorageWatchdog.RunAsync(
            availableBytes: () => 1_000_000,
            reserveBytes: 1_000,
            pollInterval: TimeSpan.FromMilliseconds(5),
            onReserveBreached: () =>
            {
                breached++;
                return;
            },
            onProbeFailed: exception => throw new InvalidOperationException("Unexpected storage probe failure.", exception),
            cancellationToken: cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await run;

        Assert.Equal(0, breached);
    }

    [Fact]
    public async Task TransientProbeFailureDoesNotStop()
    {
        var breached = 0;
        var calls = 0;
        var reportedFailures = 0;
        using var cts = new CancellationTokenSource();

        var run = StorageWatchdog.RunAsync(
            availableBytes: () =>
            {
                calls++;
                if (calls is 1 or 3 or 5)
                {
                    throw new IOException("transient probe failure");
                }

                if (calls == 6)
                {
                    cts.Cancel();
                }

                return 1_000_000;
            },
            reserveBytes: 1_000,
            pollInterval: TimeSpan.FromMilliseconds(1),
            onReserveBreached: () => breached++,
            onProbeFailed: _ => reportedFailures++,
            cancellationToken: cts.Token);

        await run;

        Assert.Equal(0, breached);
        Assert.Equal(6, calls);
        Assert.Equal(0, reportedFailures);
    }

    [Fact]
    public async Task PersistentProbeFailureRequestsStopAfterBoundedRetriesAndReportsFailure()
    {
        var failure = new IOException("persistent probe failure");
        var calls = 0;
        var reserveBreaches = 0;
        Exception? reportedFailure = null;
        using var stopController = new CaptureStopController();

        await StorageWatchdog.RunAsync(
            availableBytes: () =>
            {
                calls++;
                throw failure;
            },
            reserveBytes: 1_000,
            pollInterval: TimeSpan.FromMilliseconds(1),
            onReserveBreached: () => reserveBreaches++,
            onProbeFailed: exception =>
            {
                stopController.RequestStop();
                reportedFailure = exception;
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.True(stopController.IsStopRequested);
        Assert.Same(failure, reportedFailure);
        Assert.Equal(0, reserveBreaches);
    }
}
