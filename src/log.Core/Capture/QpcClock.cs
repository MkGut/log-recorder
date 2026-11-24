using System.Diagnostics;

namespace Log.Core.Capture;

public sealed class QpcClock : IClock
{
    public long NowTicks => ToTimelineTicks(Stopwatch.GetTimestamp());

    public async ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = targetTicks - NowTicks;
            if (remaining <= 0)
            {
                return;
            }

            var delayMilliseconds = Math.Clamp(remaining / (Log.Core.Timeline.TimelineConstants.TicksPerSecond / 1000), 1, 50);
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private static long ToTimelineTicks(long timestamp)
    {
        var frequency = Stopwatch.Frequency;
        var seconds = timestamp / frequency;
        var remainder = timestamp % frequency;
        return checked(seconds * Log.Core.Timeline.TimelineConstants.TicksPerSecond +
            remainder * Log.Core.Timeline.TimelineConstants.TicksPerSecond / frequency);
    }
}
