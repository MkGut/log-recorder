using Log.Core.Capture;
using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class CaptureSchedulingHelpersTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    [Fact]
    public void ResumesAtTheFirstWindowWithoutARecordedTarget()
    {
        var first = RandomAdaptiveSamplingPolicy.BuildWindow(0, 42);
        var second = RandomAdaptiveSamplingPolicy.BuildWindow(first.EndTicks, 42);
        var schedule = new RandomSamplingSchedule
        {
            Seed = 42,
            Windows = [first, second]
        };

        Assert.Equal(
            second.StartTicks,
            CaptureSchedulingHelpers.GetRandomWindowStartTicks(
                schedule,
                [first.TargetTicks]));
    }

    [Fact]
    public void ContinuesAfterAllRecordedWindowsAndHandlesMissingSchedule()
    {
        var first = RandomAdaptiveSamplingPolicy.BuildWindow(0, 42);
        var second = RandomAdaptiveSamplingPolicy.BuildWindow(first.EndTicks, 42);
        var schedule = new RandomSamplingSchedule
        {
            Seed = 42,
            Windows = [first, second]
        };

        Assert.Equal(
            second.EndTicks,
            CaptureSchedulingHelpers.GetRandomWindowStartTicks(
                schedule,
                [first.TargetTicks, second.TargetTicks]));
        Assert.Equal(
            0,
            CaptureSchedulingHelpers.GetRandomWindowStartTicks(null, []));
    }

    [Fact]
    public void NextSlotIndexWorksForSingleAndDualTrackIndexes()
    {
        Assert.Equal(4, CaptureSchedulingHelpers.NextSlotIndex([0, 3, 2]));
        Assert.Equal(0, CaptureSchedulingHelpers.NextSlotIndex(Array.Empty<int>()));
    }

    [Fact]
    public async Task ExistingRandomWindowIsReturnedWithoutRerolling()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await SessionStore.CreateAsync(
                root,
                "random-helper",
                "screen",
                0,
                Second,
                randomSampling: true,
                randomSeed: 42);

            var first = await CaptureSchedulingHelpers.EnsureRandomSamplingWindowAsync(
                store,
                0,
                CancellationToken.None);
            var second = await CaptureSchedulingHelpers.EnsureRandomSamplingWindowAsync(
                store,
                0,
                CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Single(store.Manifest.RandomSampling!.Windows);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
