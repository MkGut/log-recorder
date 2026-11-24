using System.Text.Json;
using Log.Core.Capture;

namespace Log.Core.Store;

public sealed record LastCaptureSetup(
    long DurationTicks,
    long IntervalTicks,
    long MaxFrameAgeTicks,
    string SourceMode,
    long EstimatedBytesPerHour,
    long ReserveBytes,
    long CameraOffsetTicks)
{
    public CaptureSamplingMode? SamplingMode { get; init; }
    public long? RandomSeed { get; init; }

    public static LastCaptureSetup From(CaptureRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LastCaptureSetup(
            options.DurationTicks,
            options.IntervalTicks,
            options.MaxFrameAgeTicks,
            options.SourceMode,
            options.EstimatedBytesPerHour,
            options.ReserveBytes,
            0)
        {
            SamplingMode = options.SamplingMode,
            RandomSeed = options.RandomSeed
        };
    }

    public CaptureRunOptions ToOptions()
    {
        return new CaptureRunOptions(
            DurationTicks,
            IntervalTicks,
            MaxFrameAgeTicks,
            SourceMode,
            EstimatedBytesPerHour,
            ReserveBytes,
            samplingMode: SamplingMode,
            randomSeed: RandomSeed);
    }
}

public static class CaptureSetupStore
{
    private const string LastSetupKey = "capture.last-setup";

    public static async Task SaveAsync(
        CatalogStore catalog,
        CaptureRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        var json = JsonSerializer.Serialize(LastCaptureSetup.From(options), PlainJson.Default.LastCaptureSetup);
        await catalog.SetSettingAsync(LastSetupKey, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CaptureRunOptions?> LoadAsync(
        CatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var json = await catalog.GetSettingAsync(LastSetupKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var setup = JsonSerializer.Deserialize(json, PlainJson.Default.LastCaptureSetup);
            return setup?.ToOptions();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
