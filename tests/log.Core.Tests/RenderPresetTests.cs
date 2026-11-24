using Log.Core.Render;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class RenderPresetTests
{
    [Fact]
    public void EmptySessionPlansTheSingleBlackFrameWrittenByTheRenderer()
    {
        var estimate = RenderPreset.EstimateDuration(CreateInput("screen", []));

        Assert.Equal(1, estimate.OutputFrameCount);
        Assert.Equal(30, estimate.OutputFramesPerSecond);
        Assert.Equal(333_333, estimate.DurationTicks);
        Assert.Equal(1d / 30, estimate.DurationSeconds);
    }

    [Fact]
    public void OneRetainedSlotProducesOneThirtyFpsFrame()
    {
        var estimate = RenderPreset.EstimateDuration(
            CreateInput("screen", [Slot(0, screenReference: "screen/one.png")]));

        Assert.Equal(1, estimate.OutputFrameCount);
        Assert.Equal(333_333, estimate.DurationTicks);
        Assert.Equal(1d / 30, estimate.DurationSeconds);
    }

    [Fact]
    public void LongSessionDurationCountsEverySlotAndRoundedIncludedHighlightHold()
    {
        const int seconds = 3_600;
        var slots = Enumerable.Range(0, seconds)
            .Select(index => Slot(index, $"screen/{index}.png"))
            .ToArray();
        var input = CreateInput(
            "screen",
            slots,
            [new HighlightEvent(
                "long-hold",
                eventTicks: 3_595L * TimeSpan.TicksPerSecond,
                HighlightSource.Screen,
                holdTicks: 2L * TimeSpan.TicksPerSecond)]) with
        {
            SessionStopTicks = seconds * TimeSpan.TicksPerSecond
        };

        var estimate = RenderPreset.EstimateDuration(input);

        Assert.Equal(3_660, estimate.OutputFrameCount);
        Assert.Equal(122, estimate.DurationSeconds);
        Assert.Equal(122L * TimeSpan.TicksPerSecond, estimate.DurationTicks);
    }

    [Fact]
    public void LegacyDualSourceUsesTheFixedBottomRightPictureInPicturePreset()
    {
        var settings = RenderPreset.CreateSettings(
            "screen+camera",
            new PixelSize(1920, 1080),
            new PixelSize(1280, 720));

        Assert.Equal(CompositionMode.PictureInPicture, settings.Composition.Mode);
        Assert.True(settings.Composition.Pip.Visible);
        Assert.Equal(0.28, settings.Composition.Pip.WidthFraction);
        Assert.Equal(0.04, settings.Composition.Pip.InsetFraction);
        Assert.Equal(PipCorner.BottomRight, settings.Composition.Pip.Corner);
        Assert.Equal(30, settings.Composition.OutputFramesPerSecond);
        Assert.False(settings.WatermarkEnabled);
    }

    [Fact]
    public void UnsupportedSourceModeIsRejectedInsteadOfSilentlyChangingComposition()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderPreset.CreateSettings(
            "unknown",
            new PixelSize(1920, 1080)));
    }

    private static SessionRenderInput CreateInput(
        string sourceMode,
        RenderAssetSlot[] slots,
        IReadOnlyList<HighlightEvent>? highlights = null)
    {
        return new SessionRenderInput(
            "synthetic-session",
            sourceMode,
            slots,
            highlights ?? [],
            [])
        {
            SessionStopTicks = slots.Length * TimeSpan.TicksPerSecond
        };
    }

    private static RenderAssetSlot Slot(int index, string? screenReference)
    {
        var targetTicks = index * TimeSpan.TicksPerSecond;
        return new RenderAssetSlot(
            new TimelineSlot(index, targetTicks, targetTicks),
            screenReference,
            null,
            ScreenIsGap: screenReference is null,
            CameraIsGap: true,
            ScreenIsDuplicate: false,
            CameraIsDuplicate: false);
    }
}
