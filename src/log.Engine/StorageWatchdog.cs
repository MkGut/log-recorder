namespace Log.Engine;

/// <summary>
/// Polls available free space during a capture and signals once when continuing
/// would consume the reserved headroom, so the engine can request a cooperative
/// stop and finalize gracefully before the disk is exhausted (M0-186).
/// Fail-closed: it stops as soon as free space is at or below the reserve, with
/// no optimistic projection of remaining capacity.
/// </summary>
internal static class StorageWatchdog
{
    private const int MaxConsecutiveProbeFailures = 3;

    public static async Task RunAsync(
        Func<long> availableBytes,
        long reserveBytes,
        TimeSpan pollInterval,
        Action onReserveBreached,
        Action<Exception> onProbeFailed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(availableBytes);
        ArgumentNullException.ThrowIfNull(onReserveBreached);
        ArgumentNullException.ThrowIfNull(onProbeFailed);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);

        var consecutiveProbeFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var probeSucceeded = false;
            long available = 0;
            try
            {
                available = availableBytes();
                consecutiveProbeFailures = 0;
                probeSucceeded = true;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                consecutiveProbeFailures++;
                if (consecutiveProbeFailures >= MaxConsecutiveProbeFailures)
                {
                    onProbeFailed(exception);
                    return;
                }
            }

            if (probeSucceeded && available <= reserveBytes)
            {
                onReserveBreached();
                return;
            }

            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
