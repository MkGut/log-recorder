using Log.Core.Render;
using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class WatermarkSettingsTests
{
    [Fact]
    public void LegacyWatermarkPreferenceSurvivesSettingsRoundTripButFixedPresetIgnoresIt()
    {
        var legacy = ShellSettings.FromValues(new Dictionary<string, object>
        {
            ["sourceMode"] = "screen+camera",
            ["defaultOutputFramesPerSecond"] = 60,
            ["elapsedTimer"] = false,
            ["watermark"] = true,
            ["compositionMode"] = 1,
            ["pipCorner"] = (int)PipCorner.TopLeft,
            ["pipWidthPercent"] = 38
        });

        var persisted = legacy.ToValues();
        Assert.Equal(60, persisted["defaultOutputFramesPerSecond"]);
        Assert.Equal(false, persisted["elapsedTimer"]);
        Assert.Equal(true, persisted["watermark"]);
        Assert.Equal(1, persisted["compositionMode"]);
        Assert.Equal((int)PipCorner.TopLeft, persisted["pipCorner"]);
        Assert.Equal(38, persisted["pipWidthPercent"]);

        var preset = RenderPreset.CreateSettings(
            legacy.SourceMode,
            new PixelSize(1920, 1080),
            new PixelSize(1280, 720));

        Assert.Equal(30, preset.Composition.OutputFramesPerSecond);
        Assert.True(preset.Timer.Enabled);
        Assert.False(preset.WatermarkEnabled);
        Assert.Equal(CompositionMode.PictureInPicture, preset.Composition.Mode);
        Assert.Equal(PipCorner.BottomRight, preset.Composition.Pip.Corner);
        Assert.Equal(0.28, preset.Composition.Pip.WidthFraction);
    }
}
