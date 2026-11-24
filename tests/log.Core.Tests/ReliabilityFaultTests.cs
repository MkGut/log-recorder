using System.Text;
using System.Diagnostics;
using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class ReliabilityFaultTests
{
    [Fact]
    public async Task ReadOnlyListingExcludesActiveCaptureAndRenderAndReturnsIssues()
    {
        var root = CreateRoot();
        try
        {
            await using var catalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root));
            await catalog.UpsertSessionAsync(
                new SessionManifest
                {
                    Id = "session-active",
                    SourceMode = "screen",
                    StartedAtTicks = 1,
                    CaptureIntervalTicks = 1,
                    Status = SessionStatus.Recording
                },
                LogPaths.SessionDirectory(root, "session-active"));

            await using (var store = await SessionStore.CreateAsync(root, "session-render", "screen", 2, 1))
            {
                await store.StartRecordingAsync();
                await store.CompleteAsync(3);
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath);
            }

            _ = await catalog.CreateRenderJobAsync("session-render", "{}");

            var listing = await SessionLibrary.ListSessionCardsCoreAsync(
                catalog,
                CancellationToken.None);

            Assert.Empty(listing.Cards);
            Assert.Contains(listing.Issues, issue => issue is
                { SessionId: "session-active", Reason: "active-capture" });
            Assert.Contains(listing.Issues, issue => issue is
                { SessionId: "session-render", Reason: "active-render" });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadOnlyOpenDoesNotReplayStartupRecovery()
    {
        var root = CreateRoot();
        try
        {
            await using (var catalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root)))
            {
                await catalog.UpsertSessionAsync(
                    new SessionManifest
                    {
                        Id = "session-still-recording",
                        SourceMode = "screen",
                        StartedAtTicks = 1,
                        CaptureIntervalTicks = 1,
                        Status = SessionStatus.Recording
                    },
                    LogPaths.SessionDirectory(root, "session-still-recording"));
            }

            await using var library = await SessionLibrary.OpenReadOnlyAsync(root);
            var listing = await library.ListSessionCardsDetailedAsync();

            Assert.Contains(listing.Issues, issue => issue is
                { SessionId: "session-still-recording", Reason: "active-capture" });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadOnlyCatalogReadsButNeverWrites()
    {
        var root = CreateRoot();
        try
        {
            var catalogPath = LogPaths.CatalogPath(root);
            await using var writer = await CatalogStore.OpenAsync(catalogPath);
            await writer.UpsertSessionAsync(
                new SessionManifest
                {
                    Id = "session-ro",
                    SourceMode = "screen",
                    StartedAtTicks = 1,
                    CaptureIntervalTicks = 1,
                    Status = SessionStatus.Ready
                },
                LogPaths.SessionDirectory(root, "session-ro"));

            await using var reader = await CatalogStore.OpenReadOnlyAsync(catalogPath);
            Assert.NotNull(await reader.GetSessionAsync("session-ro"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SetSettingAsync("k", "v"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRetainsGroupIssuesWhenSessionsDirectoryIsReparsePoint()
    {
        var root = CreateRoot();
        var sessionsTarget = Path.Combine(root, "sessions-target");
        var sessionsDirectory = LogPaths.SessionsDirectory(root);
        try
        {
            Directory.CreateDirectory(LogPaths.GroupsDirectory(root));
            Directory.CreateDirectory(LogPaths.GroupDirectory(root, "legacy-group"));
            Directory.CreateDirectory(sessionsTarget);
            CreateDirectoryReparsePoint(sessionsDirectory, sessionsTarget);

            await using var catalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root));
            var report = await SessionRecovery.RecoverWithReportAsync(root, catalog);

            Assert.Contains(report.Issues, issue => issue.Reason == "group-manifest-missing");
            Assert.Contains(report.Issues, issue => issue.Reason == "sessions-reparse-point");
        }
        finally
        {
            if (Directory.Exists(sessionsDirectory))
            {
                Directory.Delete(sessionsDirectory, recursive: false);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataOnlyListingSkipsAssetMeasurementAndPreservesIssues()
    {
        var root = CreateRoot();
        try
        {
            const int assetLength = 256 * 1024;
            await using (var catalog = await CatalogStore.OpenAsync(LogPaths.CatalogPath(root)))
            {
                await using (var store = await SessionStore.CreateAsync(root, "session-metadata", "screen", 1, 1))
                {
                    await store.StartRecordingAsync();
                    await store.CompleteAsync(2);
                    await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath);
                }

                await File.WriteAllBytesAsync(
                    Path.Combine(LogPaths.SessionDirectory(root, "session-metadata"), "asset.bin"),
                    new byte[assetLength]);
                await catalog.UpsertSessionAsync(
                    new SessionManifest
                    {
                        Id = "session-missing",
                        SourceMode = "screen",
                        StartedAtTicks = 3,
                        CaptureIntervalTicks = 1,
                        Status = SessionStatus.Ready
                    },
                    LogPaths.SessionDirectory(root, "session-missing"));
            }

            await using var library = await SessionLibrary.OpenReadOnlyAsync(root);
            var full = await library.ListSessionCardsDetailedAsync(
                SessionSummaryReadOptions.Full);
            var metadata = await library.ListSessionCardsDetailedAsync(
                SessionSummaryReadOptions.MetadataOnly);

            var fullCard = Assert.Single(full.Cards);
            Assert.True(fullCard.Summary.StorageMeasured);
            Assert.True(fullCard.Summary.StorageBytes >= assetLength);
            var metadataCard = Assert.Single(metadata.Cards);
            Assert.False(metadataCard.Summary.StorageMeasured);
            Assert.Equal(0, metadataCard.Summary.StorageBytes);
            Assert.Contains(full.Issues, issue => issue is
                { SessionId: "session-missing", Reason: "directory-missing" });
            Assert.Contains(metadata.Issues, issue => issue is
                { SessionId: "session-missing", Reason: "directory-missing" });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptFallbackBytesSurviveUntilAnExplicitRecoveryRetry()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var originalBytes = Encoding.UTF8.GetBytes("null");
            await File.WriteAllBytesAsync(path, originalBytes);

            var load = ShellSettingsFileStore.Load(path);
            Assert.True(load.RequiresExplicitOverwrite);
            Assert.Contains("original bytes were preserved", load.Error, StringComparison.OrdinalIgnoreCase);

            Assert.False(ShellSettingsFileStore.TrySave(
                path,
                ShellSettings.Defaults,
                allowReplacingPreservedBytes: false,
                out var blockedError));
            Assert.Contains("preserved", blockedError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));

            Assert.True(ShellSettingsFileStore.TrySave(
                path,
                ShellSettings.Defaults with { SourceMode = "camera" },
                allowReplacingPreservedBytes: true,
                out var retryError),
                retryError);
            var recovered = ShellSettingsFileStore.Load(path);
            Assert.False(recovered.RequiresExplicitOverwrite);
            Assert.Equal("camera", recovered.Settings.SourceMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyFallbackSettingsRemainReadable()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(
                path,
                "{\"SourceMode\":\"camera\",\"CaptureCursor\":true}");

            var load = ShellSettingsFileStore.Load(path);

            Assert.False(load.RequiresExplicitOverwrite);
            Assert.Null(load.Error);
            Assert.Equal("camera", load.Settings.SourceMode);
            Assert.True(load.Settings.CaptureCursor);
            Assert.Equal(30, load.Settings.OutputFramesPerSecond);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateDirectoryReparsePoint(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c mklink /J \"%LOG_TEST_JUNCTION_LINK%\" \"%LOG_TEST_JUNCTION_TARGET%\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Keep temp paths out of cmd.exe text so percent signs cannot trigger variable expansion.
        startInfo.Environment["LOG_TEST_JUNCTION_LINK"] = linkPath;
        startInfo.Environment["LOG_TEST_JUNCTION_TARGET"] = targetPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not create the sessions-directory junction.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
