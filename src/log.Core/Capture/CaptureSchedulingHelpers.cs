using Log.Core.Store;
using Log.Core.Timeline;

namespace Log.Core.Capture;

internal static class CaptureSchedulingHelpers
{
    public static async ValueTask<RandomSamplingWindow> EnsureRandomSamplingWindowAsync(
        SessionStore store,
        long startTicks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        var schedule = store.Manifest.RandomSampling
            ?? throw new InvalidDataException("The random sampling schedule is missing.");
        var existing = schedule.FindWindow(startTicks);
        if (existing is not null)
        {
            return existing;
        }

        var window = RandomAdaptiveSamplingPolicy.BuildWindow(startTicks, schedule.Seed);
        await store.AppendRandomSamplingWindowAsync(window, cancellationToken).ConfigureAwait(false);
        return window;
    }

    public static long GetRandomWindowStartTicks(
        RandomSamplingSchedule? schedule,
        IEnumerable<long> recordedTargetTicks)
    {
        ArgumentNullException.ThrowIfNull(recordedTargetTicks);
        if (schedule is null)
        {
            return 0;
        }

        var recordedTargets = recordedTargetTicks.ToHashSet();
        var unfinishedWindow = schedule.Windows.FirstOrDefault(
            window => !recordedTargets.Contains(window.TargetTicks));
        return unfinishedWindow?.StartTicks
            ?? schedule.Windows.Select(static window => window.EndTicks).DefaultIfEmpty(0).Max();
    }

    public static int NextSlotIndex(IEnumerable<int> slotIndexes)
    {
        ArgumentNullException.ThrowIfNull(slotIndexes);
        return checked(slotIndexes.DefaultIfEmpty(-1).Max() + 1);
    }
}
