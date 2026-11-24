using Log.Core.Capture;
using Log.Core.Timeline;
using Log.Testing;
using Xunit;

namespace Log.Integration.Tests;

public sealed class SyntheticPipelineTests
{
    [Fact]
    public async Task SyntheticFramesCanBeSelectedForEveryTimelineSlot()
    {
        var source = SyntheticFrameSource.Create(frameCount: 3, startTicks: 0, intervalTicks: 10);
        var frames = new List<FrameSample>();
        await foreach (var frame in source.ReadFramesAsync())
        {
            frames.Add(frame);
        }

        var slots = TimelinePlanner.BuildSlots(0, 30, 10);
        var captures = SlotCapturePlanner.Plan(slots, frames, maxAgeTicks: 10);
        var encoder = new RecordingEncoder();
        foreach (var capture in captures)
        {
            Assert.False(capture.IsGap);
            Assert.NotNull(capture.Frame);
            await encoder.WriteAsync(new EncodedFrame(
                capture.Slot.Index,
                capture.Slot.ActiveTargetTicks,
                capture.Frame,
                capture.IsDuplicate,
                capture.AgeTicks));
        }

        await encoder.FinalizeAsync();

        Assert.True(encoder.IsFinalized);
        Assert.Equal(3, encoder.Frames.Count);
        var markers = SyntheticFramePayload.DecodeEncodedFrames(encoder.Frames);
        Assert.Equal([0, 1, 2], markers.Select(marker => marker.FrameIndex));
        Assert.Equal([0L, 10L, 20L], markers.Select(marker => marker.SourceTicks));
    }

    [Fact]
    public async Task SparseSourcePreservesDroppedSlotsAsGapsThroughTheEncodedStream()
    {
        // Frames only at ticks 0 and 30; the age bound drops the two middle slots.
        var frames = new[]
        {
            new FrameSample(0, SyntheticFramePayload.Encode(0, 0), 2, 2),
            new FrameSample(30, SyntheticFramePayload.Encode(1, 30), 2, 2)
        };

        var slots = TimelinePlanner.BuildSlots(0, 40, 10);
        var captures = SlotCapturePlanner.Plan(slots, frames, maxAgeTicks: 5);
        var encoder = new RecordingEncoder();
        foreach (var capture in captures)
        {
            await encoder.WriteAsync(new EncodedFrame(
                capture.Slot.Index,
                capture.Slot.ActiveTargetTicks,
                capture.Frame,
                capture.IsDuplicate,
                capture.AgeTicks));
        }

        await encoder.FinalizeAsync();

        Assert.Equal(4, encoder.Frames.Count);
        Assert.Equal(
            [false, true, true, false],
            encoder.Frames.Select(frame => frame.Sample is null));

        // The two surviving frames decode to the original sparse source markers.
        Assert.True(SyntheticFramePayload.TryDecode(encoder.Frames[0].Sample!.Payload.Span, out var first));
        Assert.True(SyntheticFramePayload.TryDecode(encoder.Frames[3].Sample!.Payload.Span, out var last));
        Assert.Equal(new SyntheticFrameMarker(0, 0), first);
        Assert.Equal(new SyntheticFrameMarker(1, 30), last);
    }
}
