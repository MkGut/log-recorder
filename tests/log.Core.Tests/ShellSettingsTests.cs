using Log.Core.Render;
using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class ShellSettingsTests
{
    [Fact]
    public void DefaultsAreStableAndUseTheSupportedExportChoices()
    {
        var settings = ShellSettings.Defaults;

        Assert.Equal("screen", settings.SourceMode);
        Assert.False(settings.CaptureCursor);
        Assert.Equal(ShellSamplingMode.Adaptive, settings.SamplingMode);
        Assert.Equal(30, settings.OutputFramesPerSecond);
        Assert.True(settings.IncludeElapsedTimer);
        Assert.False(settings.ShowWatermark);
        // A dual-source recording should be useful without an extra review
        // change; screen-only and camera-only sessions override this naturally.
        Assert.Equal(CompositionMode.PictureInPicture, settings.CompositionMode);
        Assert.Equal(PipCorner.BottomRight, settings.PipCorner);
        Assert.Equal(28, settings.PipWidthPercent);
        Assert.False(settings.AppearInCaptures);
    }

    [Fact]
    public void AppearInCapturesRoundTripsAndDefaultsToHidden()
    {
        Assert.False(ShellSettings.Defaults.AppearInCaptures);
        Assert.Equal(false, ShellSettings.Defaults.ToValues()["appearInCaptures"]);

        var visible = ShellSettings.Defaults with { AppearInCaptures = true };
        Assert.Equal(true, visible.ToValues()["appearInCaptures"]);
        Assert.True(ShellSettings.FromValues(visible.ToValues()).AppearInCaptures);

        // Legacy settings written before the option existed stay hidden.
        var legacy = new Dictionary<string, object> { ["sourceMode"] = "screen" };
        Assert.False(ShellSettings.FromValues(legacy).AppearInCaptures);
    }

    [Fact]
    public void LegacyValuesImportWithoutLettingMalformedValuesBlockTheRest()
    {
        var values = new Dictionary<string, object>
        {
            ["sourceMode"] = "screen+camera",
            ["cameraDeviceId"] = "  camera-1  ",
            ["captureCursor"] = true,
            ["samplingMode"] = "random",
            ["defaultOutputFramesPerSecond"] = 120,
            ["elapsedTimer"] = false,
            ["pictureInPicture"] = true,
            ["pipCorner"] = 99,
            ["pipWidthPercent"] = 80
        };

        var settings = ShellSettings.FromValues(values);

        Assert.Equal("screen+camera", settings.SourceMode);
        Assert.Equal("camera-1", settings.CameraDeviceId);
        Assert.True(settings.CaptureCursor);
        Assert.Equal(ShellSamplingMode.Random, settings.SamplingMode);
        Assert.Equal(30, settings.OutputFramesPerSecond);
        Assert.False(settings.IncludeElapsedTimer);
        Assert.Equal(CompositionMode.PictureInPicture, settings.CompositionMode);
        Assert.Equal(PipCorner.BottomRight, settings.PipCorner);
        Assert.Equal(40, settings.PipWidthPercent);
    }

    [Fact]
    public void NewCompositionIndexRemainsCompatibleWithTheExistingSettingsKeys()
    {
        var camera = ShellSettings.Defaults with { CompositionMode = CompositionMode.CameraOnly };
        var pictureInPicture = ShellSettings.Defaults with
        {
            CompositionMode = CompositionMode.PictureInPicture,
            PipCorner = PipCorner.TopLeft,
            PipWidthPercent = 35
        };

        Assert.Equal(1, camera.ToValues()["compositionMode"]);
        Assert.Equal(2, pictureInPicture.ToValues()["compositionMode"]);
        Assert.Equal(2, ShellSettings.Defaults.ToValues()["compositionMode"]);
        Assert.Equal(35, pictureInPicture.ToValues()["pipWidthPercent"]);
        Assert.Equal(0, pictureInPicture.ToValues()["pipCorner"]);
    }

    [Fact]
    public void RoundTripPreservesValidatedDefaults()
    {
        var original = ShellSettings.Defaults with
        {
            SourceMode = "camera",
            CameraDeviceId = "camera-2",
            CaptureCursor = true,
            SamplingMode = ShellSamplingMode.Random,
            OutputFramesPerSecond = 60,
            IncludeElapsedTimer = false,
            ShowWatermark = true,
            CompositionMode = CompositionMode.PictureInPicture,
            PipCorner = PipCorner.TopRight,
            PipWidthPercent = 16
        };

        var roundTrip = ShellSettings.FromValues(original.ToValues());

        Assert.Equal(original, roundTrip);
    }
}
