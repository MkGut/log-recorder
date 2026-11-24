namespace Log.Core.Capture;

public interface IClock
{
    long NowTicks { get; }

    ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default);
}
