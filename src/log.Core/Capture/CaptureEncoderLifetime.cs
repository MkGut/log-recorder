namespace Log.Core.Capture;

/// <summary>Best-effort cleanup for platform encoders owned by a capture run.</summary>
internal sealed class CaptureEncoderLifetime : IDisposable
{
    private readonly IEncoder first;
    private readonly IEncoder? second;
    private int disposed;

    public CaptureEncoderLifetime(IEncoder first, IEncoder? second = null)
    {
        this.first = first ?? throw new ArgumentNullException(nameof(first));
        this.second = second;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        DisposeNoThrow(first);
        if (!ReferenceEquals(first, second))
        {
            DisposeNoThrow(second);
        }
    }

    private static void DisposeNoThrow(IEncoder? encoder)
    {
        try
        {
            (encoder as IDisposable)?.Dispose();
        }
        catch
        {
            // Preserve the capture result or failure; cleanup is still attempted once.
        }
    }
}
