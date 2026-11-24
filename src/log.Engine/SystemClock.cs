using System.Diagnostics;
using Log.Core.Capture;

namespace Log.Engine;

/// <summary>
/// Monotonic wall clock backed by the high-resolution QPC timer. Ticks are
/// <see cref="Stopwatch"/> ticks; convert wall durations with <see cref="Frequency"/>.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>Ticks per second for this clock (QPC frequency).</summary>
    public static long Frequency => Stopwatch.Frequency;

    public long NowTicks => Stopwatch.GetTimestamp();

    public async ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = targetTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            // Cap each wait so a distant target still observes cancellation promptly.
            var delay = (int)Math.Clamp(milliseconds, 1, 250);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Convert a wall duration into this clock's tick unit.</summary>
    public static long TicksFromDuration(TimeSpan duration)
    {
        return checked((long)(duration.TotalSeconds * Frequency));
    }
}
