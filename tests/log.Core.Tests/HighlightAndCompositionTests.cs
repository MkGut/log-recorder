using Log.Core.Render;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class HighlightAndCompositionTests
{
    [Fact]
    public void CompositionKeepsScreenFullFrameAndPlacesEvenSizedCameraPip()
    {
        var frame = CompositionPlanner.PlanFrame(
            4,
            new CompositionSettings(
                new PixelSize(1920, 1080),
                new PipLayout(true, 0.25, 0.02, PipCorner.BottomRight),
                30),
            hasScreenSource: true,
            hasCameraSource: true);

        Assert.Equal(new PixelRect(0, 0, 1920, 1080), frame.ScreenDestination);
        Assert.Equal(new PixelRect(1402, 772, 480, 270), frame.CameraDestination);
        Assert.Equal(CameraMirror.Horizontal, frame.CameraMirror);
    }

    [Fact]
    public void CameraOnlyCompositionUsesCameraAsTheFullFrame()
    {
        var frame = CompositionPlanner.PlanFrame(
            0,
            new CompositionSettings(
                new PixelSize(1280, 720),
                new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                30),
            hasScreenSource: false,
            hasCameraSource: true);

        Assert.False(frame.ScreenVisible);
        Assert.True(frame.CameraIsFullFrame);
        Assert.Equal(new PixelRect(0, 0, 1280, 720), frame.CameraDestination);
    }

    [Fact]
    public void ExplicitDualCameraOnlyModeUsesFullCameraFrame()
    {
        var frame = CompositionPlanner.PlanFrame(
            0,
            new CompositionSettings(
                new PixelSize(1280, 720),
                new PipLayout(false, 0, 0, PipCorner.BottomRight),
                30)
            {
                Mode = CompositionMode.CameraOnly
            },
            hasScreenSource: true,
            hasCameraSource: true);

        Assert.False(frame.ScreenVisible);
        Assert.True(frame.CameraIsFullFrame);
        Assert.Equal(new PixelRect(0, 0, 1280, 720), frame.CameraDestination);
    }

    [Fact]
    public void CameraOnlyChoiceFallsBackToScreenWhenCameraSampleIsMissing()
    {
        var frame = CompositionPlanner.PlanFrame(
            0,
            new CompositionSettings(
                new PixelSize(1280, 720),
                new PipLayout(false, 0, 0, PipCorner.BottomRight),
                30)
            {
                Mode = CompositionMode.CameraOnly
            },
            hasScreenSource: true,
            hasCameraSource: false);

        Assert.True(frame.ScreenVisible);
        Assert.Null(frame.CameraDestination);
        Assert.False(frame.CameraIsFullFrame);
    }

    [Fact]
    public void CameraOverlaySizeIsBoundedByTheProductControlRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipLayout(
            visible: true,
            widthFraction: 0.09,
            insetFraction: 0.02,
            corner: PipCorner.BottomRight));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipLayout(
            visible: true,
            widthFraction: 0.41,
            insetFraction: 0.02,
            corner: PipCorner.BottomRight));
    }

    [Fact]
    public void PipGeometryStaysInsideVerySmallEvenAlignedOutput()
    {
        var frame = CompositionPlanner.PlanFrame(
            0,
            new CompositionSettings(
                new PixelSize(1, 1),
                new PipLayout(true, 0.10, 0.04, PipCorner.BottomRight),
                30),
            hasScreenSource: true,
            hasCameraSource: true);

        Assert.NotNull(frame.CameraDestination);
        var camera = frame.CameraDestination.Value;
        Assert.True(camera.X >= 0);
        Assert.True(camera.Y >= 0);
        Assert.True(camera.X + camera.Width <= 2);
        Assert.True(camera.Y + camera.Height <= 2);
    }

    [Fact]
    public void MissingSourcesProduceAnExplicitBlackGapFrameContract()
    {
        var frame = CompositionPlanner.PlanFrame(
            2,
            new CompositionSettings(
                new PixelSize(1280, 720),
                new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                30),
            hasScreenSource: false,
            hasCameraSource: false);

        Assert.False(frame.ScreenVisible);
        Assert.Null(frame.CameraDestination);
        Assert.False(frame.CameraIsFullFrame);
        Assert.Equal(new PixelRect(0, 0, 1280, 720), frame.ScreenDestination);
    }

    [Fact]
    public void EmptySessionProducesAnEmptyPlanEvenWithAPendingHighlight()
    {
        // An immediate stop captures zero frames; a queued highlight must not crash the render.
        var plan = RenderTimelinePlanner.Plan(
            [],
            cameraCaptures: null,
            [new HighlightEvent("hl-1", 5, HighlightSource.Screen, 15_000_000)],
            new CompositionSettings(
                new PixelSize(1280, 720),
                new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                30));

        Assert.Empty(plan);
    }

    [Fact]
    public void RenderPlanKeepsAnAllGapSlotInsteadOfThrowing()
    {
        var slot = TimelinePlanner.BuildSlots(0, 10, 10).Single();
        var plan = RenderTimelinePlanner.Plan(
            [new SlotCapture(slot, null, IsGap: true, IsDuplicate: false, AgeTicks: 0)],
            cameraCaptures: null,
            [],
            new CompositionSettings(
                new PixelSize(1280, 720),
                new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                30));

        var frame = Assert.Single(plan);
        Assert.False(frame.Composition.ScreenVisible);
        Assert.Null(frame.Composition.CameraDestination);
    }

    [Fact]
    public void CompositionPadsOddOutputDimensionsForNv12Geometry()
    {
        var frame = CompositionPlanner.PlanFrame(
            0,
            new CompositionSettings(
                new PixelSize(1279, 719),
                new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                30),
            hasScreenSource: true,
            hasCameraSource: false);

        Assert.Equal(new PixelRect(0, 0, 1280, 720), frame.ScreenDestination);
    }

    [Fact]
    public void PipUsesTheCapturedCameraAspectRatioWhenItIsKnown()
    {
        var frame = CompositionPlanner.PlanFrame(
            0,
            new CompositionSettings(
                new PixelSize(1920, 1080),
                new PipLayout(true, 0.25, 0.02, PipCorner.BottomRight),
                30)
            {
                CameraSourceSize = new PixelSize(1280, 960)
            },
            hasScreenSource: true,
            hasCameraSource: true);

        Assert.Equal(new PixelRect(1402, 682, 480, 360), frame.CameraDestination);
    }

    [Theory]
    [InlineData(PipCorner.TopLeft, 38, 38)]
    [InlineData(PipCorner.TopRight, 1402, 38)]
    [InlineData(PipCorner.BottomLeft, 38, 772)]
    [InlineData(PipCorner.BottomRight, 1402, 772)]
    public void PipIsPlacedInEachSelectedCorner(PipCorner corner, int expectedX, int expectedY)
    {
        // 1920x1080, width 0.25 -> 480, inset 0.02 -> 38, 16:9 -> height 270.
        var frame = CompositionPlanner.PlanFrame(
            slotIndex: 0,
            new CompositionSettings(
                new PixelSize(1920, 1080),
                new PipLayout(true, 0.25, 0.02, corner),
                30),
            hasScreenSource: true,
            hasCameraSource: true);

        Assert.Equal(new PixelRect(expectedX, expectedY, 480, 270), frame.CameraDestination);
    }

    [Fact]
    public void TallCameraPipFitsInsideTheOutputInset()
    {
        var settings = new CompositionSettings(
            new PixelSize(1920, 1080),
            new PipLayout(true, 0.4, 0.02, PipCorner.BottomRight),
            30)
        {
            CameraSourceSize = new PixelSize(9, 16)
        };

        var frame = CompositionPlanner.PlanFrame(
            slotIndex: 0,
            settings,
            hasScreenSource: true,
            hasCameraSource: true);

        Assert.NotNull(frame.CameraDestination);
        Assert.True(frame.CameraDestination.Value.X >= 0);
        Assert.True(frame.CameraDestination.Value.Y >= 0);
        Assert.True(frame.CameraDestination.Value.X + frame.CameraDestination.Value.Width <= 1920);
        Assert.True(frame.CameraDestination.Value.Y + frame.CameraDestination.Value.Height <= 1080);
    }

    [Fact]
    public void PipLayoutRejectsGeometryThatCannotFitInsideTheOutput()
    {
        Assert.Throws<ArgumentException>(() => new PipLayout(
            visible: true,
            widthFraction: 0.8,
            insetFraction: 0.2,
            corner: PipCorner.BottomRight));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PipLayoutRejectsNonFiniteFractions(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipLayout(
            visible: true,
            widthFraction: fraction,
            insetFraction: 0.02,
            corner: PipCorner.BottomRight));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipLayout(
            visible: true,
            widthFraction: 0.25,
            insetFraction: fraction,
            corner: PipCorner.BottomRight));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TimerSettingsRejectsNonFiniteOpacity(double opacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimerSettings(
            enabled: true,
            opacity: opacity));
    }

    [Fact]
    public void CompositionSettingsRejectsAZeroOutputFrameRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompositionSettings(
            new PixelSize(1280, 720),
            new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
            0));
    }

    [Fact]
    public void CompositionSettingsRejectsDefaultOutputSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompositionSettings(
            default,
            new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
            30));
    }

    [Fact]
    public void CompositionSettingsRejectsDefaultCameraSourceSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompositionSettings(
            new PixelSize(1280, 720),
            new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
            30)
        {
            CameraSourceSize = (PixelSize?)default(PixelSize)
        });
    }

    [Fact]
    public void ExcludedHighlightRemainsAnEventButHasNoHoldByPolicy()
    {
        var highlight = new HighlightEvent("hl-1", 100, HighlightSource.Both, 15_000_000, included: false);

        var placement = HighlightPlacementCalculator.Calculate(
            highlight.EventTicks,
            startTicks: 0,
            stopTicks: 200,
            intervalTicks: 10,
            holdTicks: highlight.Included ? highlight.HoldTicks : 0,
            outputFramesPerSecond: 2);

        Assert.Equal(0, placement.HoldFrames);
        Assert.False(highlight.Included);
    }

    [Fact]
    public void HighlightRejectsUnsafeAssetReferences()
    {
        Assert.Throws<ArgumentException>(() => new HighlightEvent(
            "hl-1",
            100,
            HighlightSource.Screen,
            1,
            screenAssetReference: "..\\outside.png"));
        Assert.Throws<ArgumentException>(() => new HighlightEvent(
            "hl-2",
            100,
            HighlightSource.Screen,
            1,
            screenAssetReference: "highlights/asset.jpg"));
        Assert.Throws<ArgumentException>(() => new HighlightEvent(
            "hl-3",
            100,
            HighlightSource.Screen,
            1,
            screenAssetReference: "screen/asset.png"));
    }

    [Fact]
    public void RenderPlanInsertsIncludedHighlightsAfterTheirNormalFrames()
    {
        var slots = Log.Core.Timeline.TimelinePlanner.BuildSlots(0, 50, 10);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();
        var settings = new CompositionSettings(
            new PixelSize(1920, 1080),
            new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
            2);

        var plan = RenderTimelinePlanner.Plan(
            captures,
            cameraCaptures: null,
            [new HighlightEvent("hl-1", 25, HighlightSource.Screen, 15_000_000)],
            settings);

        Assert.Equal(8, plan.Count);
        Assert.Equal([0, 1, 2], plan.Take(3).Select(frame => frame.SlotIndex));
        Assert.Equal(["hl-1", "hl-1", "hl-1"], plan.Skip(3).Take(3).Select(frame => frame.HighlightId));
        Assert.Equal([3, 4], plan.Skip(6).Select(frame => frame.SlotIndex));
    }

    [Fact]
    public void AdaptiveHighlightUsesActualTargetsAfterCadenceChangesAndPause()
    {
        var second = TimelineConstants.TicksPerSecond;
        var startTicks = 100 * second;
        var pauses = new[] { new PauseRange(startTicks + 65 * second, startTicks + 85 * second) };
        var activeTargets = new long[] { 0, 10, 20, 40, 60, 90, 120 }
            .Select(target => target * second)
            .ToArray();
        var captures = activeTargets
            .Select((activeTarget, index) =>
            {
                var slot = new TimelineSlot(
                    index,
                    activeTarget,
                    TimelinePlanner.WallTargetForActiveTarget(startTicks, activeTarget, pauses));
                return new SlotCapture(
                    slot,
                    new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                    IsGap: false,
                    IsDuplicate: false,
                    AgeTicks: 0);
            })
            .ToArray();
        var settings = new CompositionSettings(
            new PixelSize(1920, 1080),
            new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
            2);
        var eventTicks = TimelinePlanner.WallTargetForActiveTarget(startTicks, 90 * second, pauses);

        var plan = RenderTimelinePlanner.Plan(
            captures,
            cameraCaptures: null,
            [new HighlightEvent("adaptive-hold", eventTicks, HighlightSource.Screen, second)],
            settings,
            pauses,
            sessionStopTicks: startTicks + 160 * second);

        var firstHoldIndex = Array.FindIndex(plan.ToArray(), static frame => frame.IsHighlightHold);

        Assert.True(firstHoldIndex >= 0);
        Assert.Equal(2, plan.Count(static frame => frame.IsHighlightHold));
        Assert.Equal(90 * second, plan[firstHoldIndex].TimelineTicks);
        Assert.Equal(5, plan[firstHoldIndex - 1].SlotIndex);
        Assert.Equal(6, plan[firstHoldIndex + 2].SlotIndex);
    }

    [Fact]
    public void TimerMetadataUsesCaptureTimelineAndDoesNotChangeFramePlacement()
    {
        var slots = TimelinePlanner.BuildSlots(0, 3 * TimelineConstants.TicksPerSecond, TimelineConstants.TicksPerSecond);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();
        var settings = new RenderSettings(
            new CompositionSettings(
                new PixelSize(1920, 1080),
                new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                30),
            new TimerSettings(enabled: true));

        var plan = RenderTimelinePlanner.Plan(captures, null, [], settings);

        Assert.Equal(3, plan.Count);
        Assert.Equal(["00:00", "00:01", "00:02"], plan.Select(frame => frame.ElapsedTimerText));
        Assert.Equal([0, TimelineConstants.TicksPerSecond, 2 * TimelineConstants.TicksPerSecond], plan.Select(frame => frame.TimelineTicks));
    }

    [Fact]
    public void TimerUsesRelativeActiveTimeWhenSessionStartsAtNonzeroTicks()
    {
        var start = 500 * TimelineConstants.TicksPerSecond;
        var slots = TimelinePlanner.BuildSlots(
            start,
            start + 3 * TimelineConstants.TicksPerSecond,
            TimelineConstants.TicksPerSecond);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();

        var plan = RenderTimelinePlanner.Plan(
            captures,
            null,
            [],
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1920, 1080),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    30),
                new TimerSettings(enabled: true)));

        Assert.Equal(["00:00", "00:01", "00:02"], plan.Select(frame => frame.ElapsedTimerText));
    }

    [Fact]
    public void HighlightHoldTimerUsesActiveTimeAcrossAnElidedPause()
    {
        var slots = TimelinePlanner.BuildSlots(
            0,
            4 * TimelineConstants.TicksPerSecond,
            TimelineConstants.TicksPerSecond,
            [new PauseRange(
                TimelineConstants.TicksPerSecond + TimelineConstants.TicksPerSecond / 2,
                2 * TimelineConstants.TicksPerSecond + TimelineConstants.TicksPerSecond / 2)]);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();

        var plan = RenderTimelinePlanner.Plan(
            captures,
            null,
            [new HighlightEvent("paused-hold", 3 * TimelineConstants.TicksPerSecond, HighlightSource.Screen, TimelineConstants.TicksPerSecond / 2)],
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1920, 1080),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    30),
                new TimerSettings(enabled: true)),
            [new PauseRange(
                TimelineConstants.TicksPerSecond + TimelineConstants.TicksPerSecond / 2,
                2 * TimelineConstants.TicksPerSecond + TimelineConstants.TicksPerSecond / 2)]);

        var holds = plan.Where(frame => frame.IsHighlightHold).ToArray();
        Assert.Equal(15, holds.Length);
        Assert.All(holds, hold =>
        {
            Assert.Equal(2 * TimelineConstants.TicksPerSecond, hold.TimelineTicks);
            Assert.Equal("00:02", hold.ElapsedTimerText);
        });
    }

    [Fact]
    public void HighlightHoldTimerIncludesPausedWallTimeWhenEnabled()
    {
        // Mirror of the active-time hold test, but with includePausedTime: the hold
        // timer should show wall time (2s active + 1s pause = 3s).
        var second = TimelineConstants.TicksPerSecond;
        var pause = new PauseRange(second + second / 2, 2 * second + second / 2);
        var slots = TimelinePlanner.BuildSlots(0, 4 * second, second, [pause]);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();

        var plan = RenderTimelinePlanner.Plan(
            captures,
            null,
            [new HighlightEvent("paused-hold", 3 * second, HighlightSource.Screen, second / 2)],
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1920, 1080),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    30),
                new TimerSettings(enabled: true, includePausedTime: true)),
            [pause]);

        var holds = plan.Where(frame => frame.IsHighlightHold).ToArray();
        Assert.NotEmpty(holds);
        Assert.All(holds, hold => Assert.Equal("00:03", hold.ElapsedTimerText));
    }

    [Fact]
    public void TimerCanIncludePausedWallTimeWhenExplicitlyEnabled()
    {
        var second = TimelineConstants.TicksPerSecond;
        var pause = new PauseRange(second + second / 2, 2 * second + second / 2);
        var slots = TimelinePlanner.BuildSlots(0, 5 * second, second, [pause]);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();

        var plan = RenderTimelinePlanner.Plan(
            captures,
            null,
            [],
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1920, 1080),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    30),
                new TimerSettings(enabled: true, includePausedTime: true)),
            [pause]);

        Assert.Equal(["00:00", "00:01", "00:03", "00:04"], plan.Select(frame => frame.ElapsedTimerText));
    }

    [Fact]
    public void TimerAddsHoursOnlyAfterTheOneHourBoundary()
    {
        var slots = TimelinePlanner.BuildSlots(
            0,
            3_601 * TimelineConstants.TicksPerSecond,
            TimelineConstants.TicksPerSecond);
        var captures = slots
            .Select(slot => new SlotCapture(
                slot,
                new Log.Core.Capture.FrameSample(slot.WallTargetTicks, new byte[] { 1 }, 2, 2),
                IsGap: false,
                IsDuplicate: false,
                AgeTicks: 0))
            .ToArray();
        var plan = RenderTimelinePlanner.Plan(
            captures,
            null,
            [],
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1920, 1080),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    30),
                new TimerSettings(enabled: true)));

        Assert.Equal("59:59", plan[3_599].ElapsedTimerText);
        Assert.Equal("1:00:00", plan[3_600].ElapsedTimerText);
    }

    [Fact]
    public void RenderPlanUsesRationalMediaFoundationFrameTiming()
    {
        var timings = MediaFoundationFrameTimingPlanner.Plan(frameCount: 30, framesPerSecond: 30);

        Assert.Equal(0, timings[0].PresentationTicks);
        Assert.Equal(333_333, timings[0].DurationTicks);
        Assert.Equal(333_333, timings[1].PresentationTicks);
        Assert.Equal(333_333, timings[1].DurationTicks);
        Assert.Equal(333_334, timings[2].DurationTicks);
        Assert.Equal(TimelineConstants.TicksPerSecond, timings[^1].PresentationTicks + timings[^1].DurationTicks);
    }
}
