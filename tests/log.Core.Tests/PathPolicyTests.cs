using Log.Core.Store;
using Log.Core.Render;
using Xunit;

namespace Log.Core.Tests;

public sealed class PathPolicyTests
{
    [Fact]
    public void GeneratedSessionIdsAreDatePrefixedAndPathSafe()
    {
        var id = SessionIdGenerator.Create(new DateTimeOffset(2026, 9, 3, 12, 34, 56, TimeSpan.Zero));

        Assert.StartsWith("2026-09-03_s_", id, StringComparison.Ordinal);
        Assert.Equal(23, id.Length);
        SessionIdPolicy.Validate(id);
        Assert.Equal(Path.Combine("root", "sessions", id), LogPaths.SessionDirectory("root", id));
    }

    [Fact]
    public void DataRootOverrideRedirectsApplicationPathsAndUnsetPreservesDefaults()
    {
        var originalRoot = Environment.GetEnvironmentVariable(LogPaths.DataRootEnvironmentVariable);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        try
        {
            Environment.SetEnvironmentVariable(LogPaths.DataRootEnvironmentVariable, null);
            var defaultRoot = Path.Combine(localApplicationData, "log");
            Assert.Equal(defaultRoot, LogPaths.DefaultRootDirectory);
            Assert.Equal("log", LogPaths.SingleInstanceId);
            Assert.Equal(Path.Combine(defaultRoot, "settings.json"), LogPaths.SettingsPath);
            Assert.Equal(Path.Combine(defaultRoot, "hotkey.json"), LogPaths.HotkeySettingsPath);
            Assert.Equal(Path.Combine(videos, "log"), ExportPathPolicy.GetDefaultExportDirectory());

            var configuredRoot = Path.Combine(Path.GetTempPath(), "log-data-root-tests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(LogPaths.DataRootEnvironmentVariable, configuredRoot);
            var fullRoot = Path.GetFullPath(configuredRoot);

            Assert.Equal(fullRoot, LogPaths.DefaultRootDirectory);
            Assert.StartsWith("log-", LogPaths.SingleInstanceId, StringComparison.Ordinal);
            Assert.Equal(Path.Combine(fullRoot, "settings.json"), LogPaths.SettingsPath);
            Assert.Equal(Path.Combine(fullRoot, "hotkey.json"), LogPaths.HotkeySettingsPath);
            Assert.Equal(Path.Combine(fullRoot, "catalog"), LogPaths.CatalogPath(LogPaths.DefaultRootDirectory));
            Assert.Equal(Path.Combine(fullRoot, "sessions"), LogPaths.SessionsDirectory(LogPaths.DefaultRootDirectory));
            Assert.Equal(Path.Combine(fullRoot, "exports"), ExportPathPolicy.GetDefaultExportDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LogPaths.DataRootEnvironmentVariable, originalRoot);
        }
    }
}
