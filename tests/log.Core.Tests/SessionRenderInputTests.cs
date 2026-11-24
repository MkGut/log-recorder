using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionRenderInputTests
{
    [Fact]
    public void ReconstructsAuthoritativeWallTargetsAndPreservesGaps()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 30,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Pauses.Add(new PauseRange(5, 15));
        manifest.Slots.Add(new ManifestSlot(0, 0, 0, false, 0, "screen/slot-000000"));
        manifest.Slots.Add(new ManifestSlot(1, 10, -1, false, 0, "screen/slot-000001"));

        var input = SessionRenderInputBuilder.Build(manifest);

        Assert.Equal(2, input.Slots.Count);
        Assert.Equal(0, input.Slots[0].Slot.WallTargetTicks);
        Assert.Equal(20, input.Slots[1].Slot.WallTargetTicks);
        Assert.Equal("screen/slot-000000", input.Slots[0].ScreenAssetReference);
        Assert.True(input.Slots[1].ScreenIsGap);
        Assert.Null(input.Slots[1].ScreenAssetReference);
    }

    [Fact]
    public void KeepsDualTrackReferencesIndependentWhenOneSourceIsAGap()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen+camera",
            StartedAtTicks = 0,
            StoppedAtTicks = 10,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Tracks["camera"] = "camera/";
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "screen", 0, 0, 0, false, 0, "screen/slot-000000"));
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "camera", 0, 0, -1, false, 0, "camera/slot-000000"));

        var input = SessionRenderInputBuilder.Build(manifest);

        var slot = Assert.Single(input.Slots);
        Assert.Equal("screen/slot-000000", slot.ScreenAssetReference);
        Assert.False(slot.ScreenIsGap);
        Assert.Null(slot.CameraAssetReference);
        Assert.True(slot.CameraIsGap);
    }

    [Fact]
    public void RejectsUnsafeStoredSlotReferences()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 10,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Slots.Add(new ManifestSlot(0, 0, 0, false, 0, "screen/../outside"));

        Assert.Throws<InvalidDataException>(() => SessionRenderInputBuilder.Build(manifest));
    }

    [Fact]
    public void RejectsImpossibleGapAndDuplicateMetadata()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 10,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Slots.Add(new ManifestSlot(0, 0, -2, false, 0, "screen/slot-000000"));

        Assert.Throws<InvalidDataException>(() => SessionRenderInputBuilder.Build(manifest));

        manifest.Slots[0] = new ManifestSlot(0, 0, -1, true, 0, "screen/slot-000000");
        Assert.Throws<InvalidDataException>(() => SessionRenderInputBuilder.Build(manifest));
    }

    [Fact]
    public void AcceptsIdenticalDuplicateSingleSourceSlotsFromLegacyAdaptiveManifest()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 20,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        var slot = new ManifestSlot(1, 10, 10, false, 0, "screen/slot-000001");
        manifest.Slots.Add(new ManifestSlot(0, 0, 0, false, 0, "screen/slot-000000"));
        manifest.Slots.Add(slot);
        manifest.Slots.Add(slot);

        var input = SessionRenderInputBuilder.Build(manifest);

        Assert.Equal(2, input.Slots.Count);
        Assert.Equal([0, 1], input.Slots.Select(static value => value.Slot.Index));
    }

    [Fact]
    public void LegacyAdaptiveInputUsesPersistedTargetsInsteadOfFabricatingBlackGapSlots()
    {
        var minute = 60 * TimelineConstants.TicksPerSecond;
        var manifest = new SessionManifest
        {
            Id = "session-adaptive",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 9 * minute,
            CaptureIntervalTicks = 8 * minute,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Slots.Add(new ManifestSlot(0, 0, 0, false, 0, "screen/slot-a-000000"));
        manifest.Slots.Add(new ManifestSlot(1, 8 * minute, 8 * minute, false, 0, "screen/slot-a-000001"));

        Assert.True(manifest.UsesAdaptiveSampling);
        var input = SessionRenderInputBuilder.Build(manifest);
        var plans = RenderTimelinePlanner.Plan(
            input,
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1280, 720),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    30)));

        Assert.Equal(2, input.Slots.Count);
        Assert.Equal([0, 8 * minute], input.Slots.Select(static slot => slot.Slot.ActiveTargetTicks));
        Assert.All(input.Slots, static slot => Assert.False(slot.ScreenIsGap));
        Assert.Equal(2, plans.Count);
        Assert.All(plans, static plan => Assert.False(plan.Sources.ScreenIsGap));
    }

    [Fact]
    public void RejectsConflictingDuplicateSingleSourceSlots()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 20,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Slots.Add(new ManifestSlot(1, 10, 10, false, 0, "screen/slot-a"));
        manifest.Slots.Add(new ManifestSlot(1, 10, 11, false, 0, "screen/slot-b"));

        Assert.Throws<InvalidDataException>(() => SessionRenderInputBuilder.Build(manifest));
    }

    [Fact]
    public void RejectsTrackShapeThatDoesNotMatchTheDeclaredSourceMode()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 10,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Tracks["camera"] = "camera/";

        Assert.Throws<InvalidDataException>(() => SessionRenderInputBuilder.Build(manifest));
    }

    [Fact]
    public void RejectsSingleSourceManifestsWithDualTrackSlots()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 10,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "screen", 0, 0, 0, false, 0, "screen/slot-000000"));

        Assert.Throws<InvalidDataException>(() => SessionRenderInputBuilder.Build(manifest));
    }

    [Fact]
    public void HighlightWithoutItsOwnAssetFallsBackToTheNearestPriorSlot()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 30,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Slots.Add(new ManifestSlot(0, 0, 0, false, 0, "screen/slot-000000"));
        manifest.Slots.Add(new ManifestSlot(1, 10, 10, false, 0, "screen/slot-000001"));
        manifest.Slots.Add(new ManifestSlot(2, 20, 20, false, 0, "screen/slot-000002"));
        // Highlight at t=15 whose source frame was unavailable (no frozen asset).
        manifest.Highlights.Add(new HighlightEvent("hl-2", 15, HighlightSource.Screen, 5_000_000));

        var input = SessionRenderInputBuilder.Build(manifest);
        var plans = RenderTimelinePlanner.Plan(
            input,
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1280, 720),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    2)));

        var hold = Assert.Single(plans, static plan => plan.IsHighlightHold);
        Assert.False(hold.Sources.ScreenUsesHighlightAsset);
        Assert.Equal("screen/slot-000001", hold.Sources.ScreenAssetReference);
        Assert.False(hold.Sources.ScreenIsGap);
    }

    [Fact]
    public void RenderPlanCarriesIndependentManifestAssetsAndHighlightAssets()
    {
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen+camera",
            StartedAtTicks = 0,
            StoppedAtTicks = 20,
            CaptureIntervalTicks = 10,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Tracks["camera"] = "camera/";
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "screen", 0, 0, 0, false, 0, "screen/slot-000000"));
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "camera", 0, 0, 0, false, 0, "camera/slot-000000"));
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "screen", 1, 10, -1, false, 0, "screen/slot-000001"));
        manifest.TrackSlots.Add(new ManifestTrackSlot(
            "camera", 1, 10, 0, true, 10, "camera/slot-000001"));
        manifest.Highlights.Add(new HighlightEvent(
            "hl-1",
            5,
            HighlightSource.Both,
            5_000_000,
            screenAssetReference: "highlights/hl-1.png",
            cameraAssetReference: "highlights/hl-1.cam.png"));

        var input = SessionRenderInputBuilder.Build(manifest);
        var plans = RenderTimelinePlanner.Plan(
            input,
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1280, 720),
                    new PipLayout(true, 0.25, 0.02, PipCorner.BottomRight),
                    2)));

        Assert.Equal("screen/slot-000000", plans[0].Sources.ScreenAssetReference);
        Assert.Equal("camera/slot-000000", plans[0].Sources.CameraAssetReference);
        var hold = Assert.Single(plans, static plan => plan.IsHighlightHold);
        Assert.Equal("highlights/hl-1.png", hold.Sources.ScreenAssetReference);
        Assert.Equal("highlights/hl-1.cam.png", hold.Sources.CameraAssetReference);
        Assert.True(hold.Sources.ScreenUsesHighlightAsset);
        Assert.True(hold.Sources.CameraUsesHighlightAsset);
        Assert.True(plans[^1].Sources.CameraIsDuplicate);
        Assert.Equal(10, plans[^1].Sources.CameraAgeTicks);
        Assert.True(plans[^1].Sources.ScreenIsGap);
    }

    [Fact]
    public void RenderPlanUsesTheCommittedStopForAHighlightInTheFinalPartialInterval()
    {
        var second = TimelineConstants.TicksPerSecond;
        var manifest = new SessionManifest
        {
            Id = "session-1",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = 35 * second,
            CaptureIntervalTicks = 10 * second,
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        for (var index = 0; index < 4; index++)
        {
            manifest.Slots.Add(new ManifestSlot(
                index,
                index * 10 * second,
                index * 10 * second,
                false,
                0,
                $"screen/slot-{index:D6}"));
        }
        manifest.Highlights.Add(new HighlightEvent(
            "tail-highlight",
            34 * second,
            HighlightSource.Screen,
            second,
            screenAssetReference: "highlights/tail.png"));

        var plans = RenderTimelinePlanner.Plan(
            SessionRenderInputBuilder.Build(manifest),
            new RenderSettings(
                new CompositionSettings(
                    new PixelSize(1280, 720),
                    new PipLayout(false, 0.25, 0.02, PipCorner.BottomRight),
                    1),
                new TimerSettings(enabled: true)));

        var hold = Assert.Single(plans, static plan => plan.IsHighlightHold);
        Assert.Equal(34 * second, hold.TimelineTicks);
        Assert.Equal("00:34", hold.ElapsedTimerText);
    }

    [Fact]
    public void RandomAdaptiveRenderUsesDurableTargetsAndRepresentsMissingTargetsAsGaps()
    {
        var seed = 42L;
        var firstWindow = RandomAdaptiveSamplingPolicy.BuildWindow(0, seed);
        var secondWindow = RandomAdaptiveSamplingPolicy.BuildWindow(firstWindow.EndTicks, seed);
        var manifest = new SessionManifest
        {
            Id = "session-random",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = secondWindow.EndTicks,
            CaptureIntervalTicks = TimelineConstants.TicksPerSecond,
            SamplingMode = SessionManifest.RandomSamplingMode,
            RandomSampling = new RandomSamplingSchedule
            {
                Seed = seed,
                Windows = [firstWindow, secondWindow]
            },
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";
        manifest.Slots.Add(new ManifestSlot(
            0,
            firstWindow.TargetTicks,
            firstWindow.TargetTicks,
            false,
            0,
            "screen/slot-r-0000000000000000000"));

        var input = SessionRenderInputBuilder.Build(manifest);

        Assert.True(manifest.UsesRandomSampling);
        Assert.Equal(
            [firstWindow.TargetTicks, secondWindow.TargetTicks],
            input.Slots.Select(static slot => slot.Slot.ActiveTargetTicks));
        Assert.False(input.Slots[0].ScreenIsGap);
        Assert.True(input.Slots[1].ScreenIsGap);
        Assert.Null(input.Slots[1].ScreenAssetReference);
    }

    [Fact]
    public void RandomAdaptiveCaptureWithoutARecordedTargetCannotBeExported()
    {
        var window = RandomAdaptiveSamplingPolicy.BuildWindow(0, 42);
        var manifest = new SessionManifest
        {
            Id = "session-random-empty",
            SourceMode = "screen",
            StartedAtTicks = 0,
            StoppedAtTicks = window.TargetTicks,
            CaptureIntervalTicks = TimelineConstants.TicksPerSecond,
            SamplingMode = SessionManifest.RandomSamplingMode,
            RandomSampling = new RandomSamplingSchedule
            {
                Seed = 42,
                Windows = [window]
            },
            Status = SessionStatus.Ready
        };
        manifest.Tracks["screen"] = "screen/";

        var exception = Assert.Throws<InvalidOperationException>(
            () => SessionRenderInputBuilder.Build(manifest));
        Assert.Contains("first scheduled sample", exception.Message, StringComparison.Ordinal);
    }
}
