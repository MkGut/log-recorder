using Log.Core.Capture;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class TimelinePlannerTests
{
    [Fact]
    public void QpcClockProducesNonNegativeMonotonicTimelineTicks()
    {
        var clock = new QpcClock();

        var first = clock.NowTicks;
        var second = clock.NowTicks;

        Assert.True(first >= 0);
        Assert.True(second >= first);
    }

    [Fact]
    public void CreatesImmediateStartSlotAndCeilsTheFinalPartialInterval()
    {
        var slots = TimelinePlanner.BuildSlots(100, 100 + 25, 10);

        Assert.Equal(3, slots.Count);
        Assert.Equal([0, 10, 20], slots.Select(slot => slot.ActiveTargetTicks));
        Assert.Equal([100, 110, 120], slots.Select(slot => slot.WallTargetTicks));
    }

    [Fact]
    public void PauseIsElidedWithoutRestartingTheActiveLattice()
    {
        var slots = TimelinePlanner.BuildSlots(
            0,
            50,
            10,
            [new PauseRange(15, 25)]);

        Assert.Equal(4, slots.Count);
        Assert.Equal([0, 10, 20, 30], slots.Select(slot => slot.ActiveTargetTicks));
        Assert.Equal([0, 10, 30, 40], slots.Select(slot => slot.WallTargetTicks));
    }

    [Fact]
    public void PauseAtCaptureStartDoesNotDelayTheImmediateStartSlot()
    {
        var slots = TimelinePlanner.BuildSlots(
            0,
            30,
            10,
            [new PauseRange(0, 10)]);

        Assert.Equal([0, 20], slots.Select(slot => slot.WallTargetTicks));
    }

    [Fact]
    public void OverlappingPausesAreCountedOnce()
    {
        var active = TimelinePlanner.ActiveDuration(
            elapsedTicks: 40,
            startTicks: 0,
            stopTicks: 40,
            pauses: [new PauseRange(10, 25), new PauseRange(20, 30)]);

        Assert.Equal(20, active);
    }

    [Fact]
    public void SelectsLatestFrameAtOrBeforeSlotAndRecordsAge()
    {
        var frames = new[]
        {
            new FrameSample(10, new byte[] { 10 }, 1, 1),
            new FrameSample(20, new byte[] { 20 }, 1, 1),
            new FrameSample(30, new byte[] { 30 }, 1, 1)
        };

        var selected = FrameSelector.SelectLatestAtOrBefore(frames, 25, 10);

        Assert.NotNull(selected);
        Assert.Equal(20, selected.Value.Sample.SourceTimestampTicks);
        Assert.True(selected.Value.IsDuplicate);
        Assert.Equal(5, selected.Value.AgeTicks);
    }

    [Fact]
    public void ExactTimestampMatchIsAFreshFrameNotADuplicate()
    {
        var selected = FrameSelector.SelectLatestAtOrBefore(
            [new FrameSample(30, new byte[] { 30 }, 1, 1)],
            targetTicks: 30,
            maxAgeTicks: 10);

        Assert.NotNull(selected);
        Assert.False(selected.Value.IsDuplicate);
        Assert.Equal(0, selected.Value.AgeTicks);
    }

    [Fact]
    public void FrameExactlyAtTheAgeBoundIsKeptAsADuplicate()
    {
        var selected = FrameSelector.SelectLatestAtOrBefore(
            [new FrameSample(20, new byte[] { 20 }, 1, 1)],
            targetTicks: 30,
            maxAgeTicks: 10);

        Assert.NotNull(selected);
        Assert.True(selected.Value.IsDuplicate);
        Assert.Equal(10, selected.Value.AgeTicks);
    }

    [Fact]
    public void FrameJustPastTheAgeBoundIsDropped()
    {
        Assert.Null(FrameSelector.SelectLatestAtOrBefore(
            [new FrameSample(20, new byte[] { 20 }, 1, 1)],
            targetTicks: 30,
            maxAgeTicks: 9));
    }

    [Fact]
    public void SelectsTheLatestEligibleFrameRegardlessOfInputOrder()
    {
        var selected = FrameSelector.SelectLatestAtOrBefore(
            [
                new FrameSample(30, new byte[] { 30 }, 1, 1),
                new FrameSample(10, new byte[] { 10 }, 1, 1),
                new FrameSample(20, new byte[] { 20 }, 1, 1)
            ],
            targetTicks: 25,
            maxAgeTicks: 100);

        Assert.NotNull(selected);
        Assert.Equal(20, selected.Value.Sample.SourceTimestampTicks);
    }

    [Fact]
    public void HighlightHoldUsesDeterministicFrameRounding()
    {
        var placement = HighlightPlacementCalculator.Calculate(
            eventTicks: 25,
            startTicks: 0,
            stopTicks: 50,
            intervalTicks: 10,
            holdTicks: 15_000_000,
            outputFramesPerSecond: 2);

        Assert.Equal(3, placement.NormalFramesBefore);
        Assert.Equal(3, placement.HoldFrames);
        Assert.Equal(25, placement.ActiveEventTicks);
    }

    [Fact]
    public void HighlightBeforeStartClampsToTheOpeningFrame()
    {
        // A hotkey pressed just before capture "officially" starts must land at the open.
        var placement = HighlightPlacementCalculator.Calculate(
            eventTicks: 50,
            startTicks: 100,
            stopTicks: 150,
            intervalTicks: 10,
            holdTicks: 5_000_000,
            outputFramesPerSecond: 2);

        Assert.Equal(0, placement.ActiveEventTicks);
        Assert.Equal(1, placement.NormalFramesBefore);
    }

    [Fact]
    public void HighlightAfterStopClampsToTheClosingFrame()
    {
        // slots = BuildSlots(100, 150, 10) -> 5 slots; a late highlight lands at the end.
        var placement = HighlightPlacementCalculator.Calculate(
            eventTicks: 1_000,
            startTicks: 100,
            stopTicks: 150,
            intervalTicks: 10,
            holdTicks: 5_000_000,
            outputFramesPerSecond: 2);

        Assert.Equal(50, placement.ActiveEventTicks);
        Assert.Equal(5, placement.NormalFramesBefore);
    }

    [Fact]
    public void SlotPlannerKeepsAnInitialGapAndThenUsesTheFirstFrame()
    {
        var slots = TimelinePlanner.BuildSlots(0, 20, 10);
        var captures = SlotCapturePlanner.Plan(
            slots,
            [new FrameSample(5, new byte[] { 5 }, 1, 1)],
            maxAgeTicks: 10);

        Assert.True(captures[0].IsGap);
        Assert.False(captures[1].IsGap);
        Assert.True(captures[1].IsDuplicate);
        Assert.Equal(5, captures[1].AgeTicks);
    }

    [Fact]
    public void SlotPlannerGapsMidSessionThenRecoversWhenFramesResume()
    {
        // Frames stop after t=0 (e.g. a minimized window) and resume at t=35.
        var slots = TimelinePlanner.BuildSlots(0, 50, 10);
        var captures = SlotCapturePlanner.Plan(
            slots,
            [
                new FrameSample(0, new byte[] { 0 }, 1, 1),
                new FrameSample(35, new byte[] { 35 }, 1, 1)
            ],
            maxAgeTicks: 5);

        Assert.Equal([false, true, true, true, false], captures.Select(capture => capture.IsGap));
        Assert.False(captures[0].IsDuplicate);
        Assert.True(captures[4].IsDuplicate);
        Assert.Equal(35, captures[4].Frame!.SourceTimestampTicks);
        Assert.Equal(5, captures[4].AgeTicks);
    }

    [Fact]
    public void SlotPlannerDoesNotDuplicateAFramePastTheAgeBound()
    {
        var slots = TimelinePlanner.BuildSlots(0, 30, 10);
        var captures = SlotCapturePlanner.Plan(
            slots,
            [new FrameSample(0, new byte[] { 0 }, 1, 1)],
            maxAgeTicks: 5);

        Assert.False(captures[0].IsGap);
        Assert.True(captures[1].IsGap);
        Assert.True(captures[2].IsGap);
    }

    [Fact]
    public void CameraSelectionAppliesCalibrationOffsetWithoutChangingRawFrameTimestamp()
    {
        var frame = new FrameSample(90, new byte[] { 1 }, 1, 1);

        var selected = FrameSelector.SelectLatestAtOrBefore(
            [frame],
            targetTicks: 100,
            maxAgeTicks: 10,
            timestampOffsetTicks: 10);

        Assert.NotNull(selected);
        Assert.Equal(90, selected.Value.Sample.SourceTimestampTicks);
        Assert.Equal(0, selected.Value.AgeTicks);
    }

    [Fact]
    public void SourceEpochBoundaryPreventsUsingAFrameFromBeforeReconnect()
    {
        var selected = FrameSelector.SelectLatestAtOrBefore(
            [new FrameSample(90, new byte[] { 1 }, 1, 1, sourceEpoch: 0)],
            targetTicks: 100,
            maxAgeTicks: 20,
            timestampOffsetTicks: 0,
            sourceEpoch: 1);

        Assert.Null(selected);
    }

    [Fact]
    public void CalibrationRequiresAtLeastThreePairedSamples()
    {
        Assert.Throws<ArgumentException>(() => DualSourceOffsetCalibrator.EstimateOffset(
        [
            new OffsetCalibrationSample(1_010, 1_000),
            new OffsetCalibrationSample(2_010, 2_000)
        ]));
    }

    [Fact]
    public void CalibrationUsesTheMiddleOffsetForOddSampleCounts()
    {
        var offset = DualSourceOffsetCalibrator.EstimateOffset(
        [
            new OffsetCalibrationSample(1_005, 1_000), // 5
            new OffsetCalibrationSample(2_010, 2_000), // 10
            new OffsetCalibrationSample(3_030, 3_000)  // 30
        ]);

        Assert.Equal(10, offset);
    }

    [Fact]
    public void CalibrationMedianIsRobustToASingleOutlier()
    {
        // A single wildly-off pairing (e.g. a stalled camera frame) must not skew the offset.
        var offset = DualSourceOffsetCalibrator.EstimateOffset(
        [
            new OffsetCalibrationSample(1_008, 1_000),  // 8
            new OffsetCalibrationSample(2_009, 2_000),  // 9
            new OffsetCalibrationSample(3_010, 3_000),  // 10
            new OffsetCalibrationSample(4_011, 4_000),  // 11
            new OffsetCalibrationSample(10_000, 5_000)  // 5000 outlier
        ]);

        Assert.Equal(10, offset);
    }

    [Fact]
    public void DualSourceCalibrationUsesMedianOffset()
    {
        var offset = DualSourceOffsetCalibrator.EstimateOffset(
        [
            new OffsetCalibrationSample(1_010, 1_000),
            new OffsetCalibrationSample(2_009, 2_000),
            new OffsetCalibrationSample(3_011, 3_000),
            new OffsetCalibrationSample(4_010, 4_000)
        ]);

        Assert.Equal(10, offset);
        Assert.Equal(1_010, DualSourceOffsetCalibrator.AlignCameraTicks(1_000, offset));
    }
}
