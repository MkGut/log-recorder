using System.Text.Json.Nodes;
using Log.Core.Render;
using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class LegacyManifestCompatibilityTests
{
    [Fact]
    public async Task VersionlessSessionManifestKeepsGroupMetadataAndCanBePlannedForRender()
    {
        const string groupId = "group-legacy01";
        const string legacyGroupManifest = "{\"schemaVersion\":1,\"id\":\"group-legacy01\",\"name\":\"Old class\",\"type\":\"Class\",\"createdAtTicks\":1}";
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (await CatalogStore.OpenAsync(LogPaths.CatalogPath(root)))
            {
            }

            await using (var seedCatalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root)))
            {
                await seedCatalog.SeedGroupAsync(new CatalogGroup("group-legacy01", "Old class", GroupType.Class, 1, 0));
            }

            var groupDirectory = LogPaths.GroupDirectory(root, groupId);
            Directory.CreateDirectory(groupDirectory);
            var groupManifestPath = Path.Combine(groupDirectory, "group.json");
            await File.WriteAllTextAsync(groupManifestPath, legacyGroupManifest);

            await using (var store = await SessionStore.CreateAsync(
                root,
                "legacy-session",
                "screen",
                startedAtTicks: 0,
                captureIntervalTicks: 10,
                groupId: groupId))
            {
                await store.StartRecordingAsync();
                await store.AppendSlotAsync(new ManifestSlot(
                    0,
                    0,
                    0,
                    IsDuplicate: false,
                    AgeTicks: 0,
                    "screen/slot-000000.png"));
                await store.CompleteAsync(10);
            }

            var manifestPath = Path.Combine(root, "sessions", "legacy-session", "manifest.json");
            var manifestJson = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifestJson.Remove("schemaVersion");
            manifestJson.Remove("samplingMode");
            manifestJson.Remove("trackSlots");
            manifestJson.Remove("randomSampling");
            await File.WriteAllTextAsync(manifestPath, manifestJson.ToJsonString());

            await using var library = await SessionLibrary.OpenAsync(root);
            var card = Assert.Single(await library.ListSessionCardsAsync());
            Assert.Equal(groupId, card.Session.GroupId);
            Assert.Equal(groupId, Assert.Single(await library.Catalog.ListAllGroupsAsync()).Id);

            var input = await library.GetSessionRenderInputAsync("legacy-session");
            Assert.Equal("screen", input.SourceMode);
            var settings = RenderPreset.CreateSettings(input.SourceMode, new PixelSize(4, 4));
            var plan = Assert.Single(RenderTimelinePlanner.Plan(input, settings));
            Assert.Equal("screen/slot-000000.png", plan.Sources.ScreenAssetReference);

            await using var reopened = await SessionStore.OpenAsync(root, "legacy-session");
            Assert.Equal(1, reopened.Manifest.SchemaVersion);
            Assert.Equal(SessionManifest.FixedSamplingMode, reopened.Manifest.SamplingMode);
            Assert.Equal(groupId, reopened.Manifest.GroupId);
            Assert.Equal(legacyGroupManifest, await File.ReadAllTextAsync(groupManifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
