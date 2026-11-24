namespace Log.Core.Capture;

/// <summary>Signals a cooperative user stop without treating it as a failed capture.</summary>
public sealed class CaptureStopController : IDisposable
{
    private readonly CancellationTokenSource stopCancellation = new();
    private int disposed;
    private int stopRequested;

    public CancellationToken Token => stopCancellation.Token;

    public bool IsStopRequested => Volatile.Read(ref stopRequested) != 0;

    public void RequestStop()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref stopRequested, 1);
        try
        {
            stopCancellation.Cancel();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
            // Disposal may win the race after the stop signal is recorded.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stopCancellation.Dispose();
        }
    }
}
