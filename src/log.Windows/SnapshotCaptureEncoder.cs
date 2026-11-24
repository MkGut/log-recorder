using Log.Core.Capture;

namespace Log.Windows;

/// <summary>
/// Capture-time encoder for the sampled-frame pipeline.
/// </summary>
/// <remarks>
/// CaptureCoordinator persists the authoritative samples as PNG assets. Encoding
/// those sparse samples into H.264 while the user is working duplicates the work
/// and makes stop depend on a synchronous SinkWriter finalization. Export owns
/// the only H.264 pass, so capture only validates the frame contract and returns.
/// </remarks>
public sealed class SnapshotCaptureEncoder : IEncoder
{
    private readonly int width;
    private readonly int height;
    private readonly object gate = new();
    private bool finalized;

    public SnapshotCaptureEncoder(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.width = width;
        this.height = height;
    }

    public ValueTask WriteAsync(
        EncodedFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (finalized)
            {
                throw new InvalidOperationException("The sampled capture encoder has already been finalized.");
            }
            if (frame.Sample is { } sample
                && (sample.Width != width || sample.Height != height))
            {
                throw new InvalidDataException("The sampled frame dimensions do not match the capture source.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            finalized = true;
        }

        return ValueTask.CompletedTask;
    }
}
