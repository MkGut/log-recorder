using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class GroupStoreTests
{
    [Fact]
    public async Task LegacyGroupsAndAssignedSessionsRemainListedAndOpenable()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
            const string groupId = "group-legacy01";
            const string legacyManifest = "{\"schemaVersion\":1,\"id\":\"group-legacy01\",\"name\":\"Old class\",\"type\":\"Class\",\"createdAtTicks\":1}";

        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root)))
            {
                // Simulate the old on-disk catalog without exercising a removed
                // group creation API.
            }

            await using (var seedCatalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root)))
            {
                await seedCatalog.SeedGroupAsync(new CatalogGroup("group-legacy01", "Old class", GroupType.Class, 1, 0));
            }

            var groupDirectory = LogPaths.GroupDirectory(root, groupId);
            Directory.CreateDirectory(groupDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(groupDirectory, "group.json"),
                legacyManifest);

            await using (var store = await SessionStore.CreateAsync(
                root,
                "legacy-session",
                "screen",
                100,
                10,
                groupId: groupId))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(110);
            }

            await using (var library = await SessionLibrary.OpenAsync(root))
            {
                var card = Assert.Single(await library.ListSessionCardsAsync());
                Assert.Equal("legacy-session", card.Session.Id);
                Assert.Equal(groupId, card.Session.GroupId);
                Assert.Equal(groupId, Assert.Single(await library.Catalog.ListAllGroupsAsync()).Id);

                var summary = await library.GetSessionSummaryAsync("legacy-session");
                Assert.Equal(SessionStatus.Ready, summary.Status);
                await using var reopened = await SessionStore.OpenAsync(root, "legacy-session");
                Assert.Equal(groupId, reopened.Manifest.GroupId);
            }
            Assert.Equal(
                legacyManifest,
                await File.ReadAllTextAsync(Path.Combine(groupDirectory, "group.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
