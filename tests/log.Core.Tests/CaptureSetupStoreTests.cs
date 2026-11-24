using Log.Core.Capture;
using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class CaptureSetupStoreTests
{
    [Fact]
    public async Task RoundTripsTheLastCaptureSetupThroughCatalogSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var options = new CaptureRunOptions(
                durationTicks: 120,
                intervalTicks: 10,
                maxFrameAgeTicks: 20,
                sourceMode: "screen",
                estimatedBytesPerHour: 1234,
                reserveBytes: 5678);

            await CaptureSetupStore.SaveAsync(catalog, options);
            var loaded = await CaptureSetupStore.LoadAsync(catalog);

            Assert.Equal(options, loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTripsRandomSamplingModeAndSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var options = new CaptureRunOptions(
                durationTicks: 0,
                intervalTicks: 10,
                maxFrameAgeTicks: 20,
                samplingMode: CaptureSamplingMode.Random,
                randomSeed: 9876);

            await CaptureSetupStore.SaveAsync(catalog, options);
            var loaded = await CaptureSetupStore.LoadAsync(catalog);

            Assert.Equal(options, loaded);
            Assert.Equal(CaptureSamplingMode.Random, loaded!.SamplingMode);
            Assert.Equal(9876, loaded.RandomSeed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IgnoresCorruptOrOutdatedLastSetupSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var catalog = await CatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await catalog.SetSettingAsync("capture.last-setup", "{not-json}");
            Assert.Null(await CaptureSetupStore.LoadAsync(catalog));

            await catalog.SetSettingAsync(
                "capture.last-setup",
                "{\"DurationTicks\":1,\"IntervalTicks\":1,\"MaxFrameAgeTicks\":0,\"SourceMode\":\"future-mode\",\"EstimatedBytesPerHour\":0,\"ReserveBytes\":0,\"CameraOffsetTicks\":0}");
            Assert.Null(await CaptureSetupStore.LoadAsync(catalog));

            await catalog.SetSettingAsync(
                "capture.last-setup",
                "{\"DurationTicks\":1,\"IntervalTicks\":1,\"MaxFrameAgeTicks\":0,\"SourceMode\":\"camera\",\"EstimatedBytesPerHour\":0,\"ReserveBytes\":0,\"CameraOffsetTicks\":0}");
            Assert.Null(await CaptureSetupStore.LoadAsync(catalog));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsUnsupportedSourceModesBeforeASetupCanBeSaved()
    {
        Assert.Throws<ArgumentException>(() => new CaptureRunOptions(
            durationTicks: 10,
            intervalTicks: 1,
            maxFrameAgeTicks: 0,
            sourceMode: "microphone"));
    }
}
