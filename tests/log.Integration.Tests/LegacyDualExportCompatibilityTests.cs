using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Integration.Tests;

public sealed class LegacyDualExportCompatibilityTests
{
    [Fact]
    public async Task LegacyAdaptiveDualSessionOpensAndExportsWithFixedPipWhilePreservingSources()
    {
        const long minute = 60 * TimelineConstants.TicksPerSecond;
        const string sessionId = "legacy-dual";
        var root = Path.Combine(Path.GetTempPath(), "log-integration-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(root, "exports");
        var screenBytes = new byte[] { 0x10, 0x11, 0x12 };
        var cameraBytes = new byte[] { 0x20, 0x21, 0x22 };
        Directory.CreateDirectory(root);

        try
        {
            await using (var store = await SessionStore.CreateAsync(
                root,
                sessionId,
                "screen+camera",
                startedAtTicks: 0,
                captureIntervalTicks: 8 * minute,
                adaptiveSampling: true))
            {
                await store.StartRecordingAsync();
                var screenPath = Path.Combine(store.DirectoryPath, "screen", "slot-a-000000.png");
                var cameraPath = Path.Combine(store.DirectoryPath, "camera", "slot-a-000000.png");
                await File.WriteAllBytesAsync(screenPath, screenBytes);
                await File.WriteAllBytesAsync(cameraPath, cameraBytes);
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 0, 0, 0, false, 0, "screen/slot-a-000000.png"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "camera", 0, 0, 0, false, 0, "camera/slot-a-000000.png"));

                var lateTarget = 8 * minute;
                var lateScreenPath = Path.Combine(store.DirectoryPath, "screen", "slot-a-000001.png");
                var lateCameraPath = Path.Combine(store.DirectoryPath, "camera", "slot-a-000001.png");
                await File.WriteAllBytesAsync(lateScreenPath, screenBytes);
                await File.WriteAllBytesAsync(lateCameraPath, cameraBytes);
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "screen", 1, lateTarget, lateTarget, false, 0, "screen/slot-a-000001.png"));
                await store.AppendTrackSlotAsync(new ManifestTrackSlot(
                    "camera", 1, lateTarget, lateTarget, false, 0, "camera/slot-a-000001.png"));
                await store.CompleteAsync(9 * minute);
            }

            var manifestPath = Path.Combine(root, "sessions", sessionId, "manifest.json");
            var manifestJson = System.Text.Json.Nodes.JsonNode
                .Parse(await File.ReadAllTextAsync(manifestPath))!
                .AsObject();
            manifestJson.Remove("schemaVersion");
            manifestJson.Remove("samplingMode");
            await File.WriteAllTextAsync(manifestPath, manifestJson.ToJsonString());

            await using var library = await SessionLibrary.OpenAsync(root);
            var listedSession = Assert.Single(await library.ListSessionsAsync());
            Assert.Equal(sessionId, listedSession.Id);
            Assert.Equal("ready", listedSession.Status);

            var input = await library.GetSessionRenderInputAsync(sessionId);
            Assert.Equal("screen+camera", input.SourceMode);
            Assert.Equal(2, input.Slots.Count);
            Assert.All(input.Slots, static slot =>
            {
                Assert.False(slot.ScreenIsGap);
                Assert.False(slot.CameraIsGap);
            });

            var settings = RenderPreset.CreateSettings(
                input.SourceMode,
                new PixelSize(1920, 1080),
                new PixelSize(1280, 720));
            var plans = Array.Empty<RenderFramePlan>();
            var result = await library.Export.ExportAsync(
                sessionId,
                new SessionExportRequest(settings, outputDirectory, "legacy-dual.mp4"),
                async (context, cancellationToken) =>
                {
                    plans = RenderTimelinePlanner.Plan(context.RenderInput, context.RenderSettings).ToArray();
                    await File.WriteAllTextAsync(
                        context.OutputPath,
                        $"planned-frames={plans.Length}",
                        cancellationToken);
                });

            Assert.Equal("completed", result.Job.Status);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(2, plans.Length);
            Assert.Equal("screen/slot-a-000000.png", plans[0].Sources.ScreenAssetReference);
            Assert.Equal("camera/slot-a-000000.png", plans[0].Sources.CameraAssetReference);
            Assert.Equal(30, settings.Composition.OutputFramesPerSecond);
            Assert.Equal(CompositionMode.PictureInPicture, settings.Composition.Mode);
            Assert.Equal(PipCorner.BottomRight, settings.Composition.Pip.Corner);

            Assert.Equal(screenBytes, await File.ReadAllBytesAsync(
                Path.Combine(root, "sessions", sessionId, "screen", "slot-a-000000.png")));
            Assert.Equal(cameraBytes, await File.ReadAllBytesAsync(
                Path.Combine(root, "sessions", sessionId, "camera", "slot-a-000000.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
