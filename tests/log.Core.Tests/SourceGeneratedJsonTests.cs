using System.Text.Json;
using Xunit;
using Log.Core.Shell;
using Log.Core.Store;

namespace Log.Core.Tests;

// Source-generated JSON must keep initializer defaults when old files omit a property.
public sealed class SourceGeneratedJsonTests
{
    [Fact]
    public void ManifestKeepsDefaultsForAbsentProperties()
    {
        const string json = """
            {"id":"s","sourceMode":"screen","randomSampling":{"seed":7},
             "slots":[{"index":0,"targetTicks":1,"sourceTicks":1,"isDuplicate":false,"ageTicks":0,"assetReference":"screen/a.png"}]}
            """;

        var manifest = JsonSerializer.Deserialize(json, ManifestJson.Default.SessionManifest)!;

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Empty(manifest.PrunedTracks);
        Assert.Equal("unknown", Assert.Single(manifest.Slots).TimestampQuality);
        Assert.NotNull(manifest.RandomSampling!.AlgorithmVersion);
        Assert.Empty(manifest.RandomSampling.Windows);
    }

    [Fact]
    public void ReminderStateKeepsEmptyEventIdsWhenAbsent()
    {
        var envelope = JsonSerializer.Deserialize("{\"state\":{}}", PlainJson.Default.PersistedEnvelope)!;
        Assert.NotNull(envelope.State!.ProcessedEventIds);
    }
}
