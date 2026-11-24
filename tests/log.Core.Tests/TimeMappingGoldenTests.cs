using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

// Golden-case time-mapping vectors called out in requirements-traceability.md
// (CAP-14, PP-03/11): dropped slots, equal-time highlight tie-break, and
// output-fps rational timing. These pin the slot model (doc 04 §3) so two
// engineers get frame-identical placement.
public sealed class TimeMappingGoldenTests
{
    private static readonly CompositionSettings Composition = new(
        new PixelSize(1920, 1080),
        new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
        2);

    [Fact]
    public void DroppedSlotsStayInPlaceAsGapFramesWithoutRenumbering()
    {
        var slots = TimelinePlanner.BuildSlots(0, 50, 10);
        var captures = slots
            .Select((slot, index) => new SlotCapture(
                slot,
                Frame: null,
                IsGap: index is 1 or 3, // two dropped/missed slots mid-session
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();

        var plan = RenderTimelinePlanner.Plan(captures, null, [], Composition);

        Assert.Equal(5, plan.Count);
        Assert.Equal([0, 1, 2, 3, 4], plan.Select(frame => frame.SlotIndex));
        Assert.Equal(
            [true, false, true, false, true],
            plan.Select(frame => frame.Composition.ScreenVisible));
    }

    [Fact]
    public void HighlightsAtTheSameInstantAreOrderedDeterministicallyById()
    {
        var slots = TimelinePlanner.BuildSlots(0, 50, 10);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();

        // Same EventTicks; supplied out of order so only the Id tie-break can order them.
        var highlights = new[]
        {
            new HighlightEvent("z-second", 25, HighlightSource.Screen, 15_000_000),
            new HighlightEvent("a-first", 25, HighlightSource.Screen, 15_000_000)
        };

        var plan = RenderTimelinePlanner.Plan(captures, null, highlights, Composition);

        var holdIds = plan
            .Where(frame => frame.IsHighlightHold)
            .Select(frame => frame.HighlightId)
            .ToArray();

        // Every "a-first" hold precedes every "z-second" hold, contiguously.
        Assert.NotEmpty(holdIds);
        var firstSecondIndex = Array.IndexOf(holdIds, "z-second");
        var lastFirstIndex = Array.LastIndexOf(holdIds, "a-first");
        Assert.True(firstSecondIndex > lastFirstIndex);
        Assert.All(holdIds.Take(firstSecondIndex), id => Assert.Equal("a-first", id));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void OutputFpsProducesRationalTimingThatSumsToExactlyOneSecond(int fps)
    {
        var timings = MediaFoundationFrameTimingPlanner.Plan(fps, fps);

        Assert.Equal(0, timings[0].PresentationTicks);

        // Presentation is strictly increasing.
        for (var index = 1; index < timings.Count; index++)
        {
            Assert.True(timings[index].PresentationTicks > timings[index - 1].PresentationTicks);
        }

        // Exactly `fps` frames span exactly one second — no cumulative drift.
        var span = timings[^1].PresentationTicks + timings[^1].DurationTicks;
        Assert.Equal(TimelineConstants.TicksPerSecond, span);

        // Rational distribution: any two frame durations differ by at most one tick.
        var min = timings.Min(timing => timing.DurationTicks);
        var max = timings.Max(timing => timing.DurationTicks);
        Assert.True(max - min <= 1);
    }
}
