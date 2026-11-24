using Log.Core.Timeline;

namespace Log.Core.Render;

public sealed record RenderFramePlan(
    int OutputIndex,
    int? SlotIndex,
    bool IsHighlightHold,
    string? HighlightId,
    CompositionFrame Composition)
{
    public RenderFrameSources Sources { get; init; } = RenderFrameSources.Empty;
    public long TimelineTicks { get; init; }
    public long PresentationTicks { get; init; }
    public long DurationTicks { get; init; }
    public string? ElapsedTimerText { get; init; }
}

public static class RenderTimelinePlanner
{
    public static IReadOnlyList<RenderFramePlan> Plan(
        SessionRenderInput input,
        RenderSettings renderSettings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(renderSettings);

        var screenCaptures = input.Slots
            .Select(static slot => new SlotCapture(
                slot.Slot,
                Frame: null,
                IsGap: slot.ScreenIsGap,
                slot.ScreenIsDuplicate,
                AgeTicks: 0))
            .ToArray();
        var cameraCaptures = input.SourceMode is "camera" or "screen+camera"
            ? input.Slots
                .Select(static slot => new SlotCapture(
                    slot.Slot,
                    Frame: null,
                    IsGap: slot.CameraIsGap,
                    slot.CameraIsDuplicate,
                    AgeTicks: 0))
                .ToArray()
            : null;

        var plans = Plan(
            screenCaptures,
            cameraCaptures,
            input.Highlights,
            renderSettings,
            input.Pauses,
            input.SessionStopTicks);
        return AttachSources(plans, input);
    }

    public static IReadOnlyList<RenderFramePlan> Plan(
        IReadOnlyList<SlotCapture> screenCaptures,
        IReadOnlyList<SlotCapture>? cameraCaptures,
        IEnumerable<HighlightEvent> highlights,
        CompositionSettings settings,
        IReadOnlyList<PauseRange>? pauses = null,
        long? sessionStopTicks = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Plan(
            screenCaptures,
            cameraCaptures,
            highlights,
            new RenderSettings(settings),
            pauses,
            sessionStopTicks);
    }

    public static IReadOnlyList<RenderFramePlan> Plan(
        IReadOnlyList<SlotCapture> screenCaptures,
        IReadOnlyList<SlotCapture>? cameraCaptures,
        IEnumerable<HighlightEvent> highlights,
        RenderSettings renderSettings,
        IReadOnlyList<PauseRange>? pauses = null,
        long? sessionStopTicks = null)
    {
        ArgumentNullException.ThrowIfNull(screenCaptures);
        ArgumentNullException.ThrowIfNull(highlights);
        ArgumentNullException.ThrowIfNull(renderSettings);
        var settings = renderSettings.Composition;
        if (cameraCaptures is not null && cameraCaptures.Count != screenCaptures.Count)
        {
            throw new ArgumentException("Source tracks must contain the same number of slots.", nameof(cameraCaptures));
        }

        var orderedHighlights = highlights
            .Where(static highlight => highlight.Included && highlight.HoldTicks > 0)
            .OrderBy(static highlight => highlight.EventTicks)
            .ThenBy(static highlight => highlight.Id, StringComparer.Ordinal)
            .ToArray();
        var holdsByNormalFrame = BuildHoldMap(
            orderedHighlights,
            screenCaptures,
            settings.OutputFramesPerSecond,
            pauses,
            sessionStopTicks);
        var output = new List<RenderFramePlan>(
            screenCaptures.Count + holdsByNormalFrame.Values.SelectMany(static value => value).Sum(static hold => hold.HoldFrames));
        var outputIndex = 0;
        var firstWallTargetTicks = screenCaptures.Count == 0
            ? 0
            : screenCaptures[0].Slot.WallTargetTicks;

        for (var normalFrameIndex = 0; normalFrameIndex <= screenCaptures.Count; normalFrameIndex++)
        {
            if (holdsByNormalFrame.TryGetValue(normalFrameIndex, out var holds))
            {
                foreach (var hold in holds)
                {
                    var composition = CompositionPlanner.PlanFrame(
                        slotIndex: Math.Min(normalFrameIndex, screenCaptures.Count - 1),
                        settings,
                        hasScreenSource: screenCaptures.Any(static capture => !capture.IsGap),
                        hasCameraSource: cameraCaptures?.Any(static capture => !capture.IsGap) == true);
                    for (var holdIndex = 0; holdIndex < hold.HoldFrames; holdIndex++)
                    {
                        output.Add(new RenderFramePlan(outputIndex++, null, true, hold.Event.Id, composition)
                        {
                            TimelineTicks = hold.ActiveEventTicks,
                            ElapsedTimerText = renderSettings.Timer.Enabled
                                ? ElapsedTimerFormatter.Format(
                                    renderSettings.Timer.IncludePausedTime
                                        ? checked(hold.WallEventTicks - firstWallTargetTicks)
                                        : hold.ActiveEventTicks)
                                : null
                        });
                    }
                }
            }

            if (normalFrameIndex == screenCaptures.Count)
            {
                break;
            }

            var normalComposition = CompositionPlanner.PlanFrame(
                screenCaptures[normalFrameIndex].Slot.Index,
                settings,
                hasScreenSource: !screenCaptures[normalFrameIndex].IsGap,
                hasCameraSource: cameraCaptures is not null && !cameraCaptures[normalFrameIndex].IsGap);
            var normalTicks = screenCaptures[normalFrameIndex].Slot.ActiveTargetTicks;
            var timerTicks = renderSettings.Timer.IncludePausedTime
                ? checked(screenCaptures[normalFrameIndex].Slot.WallTargetTicks - firstWallTargetTicks)
                : normalTicks;
            output.Add(new RenderFramePlan(outputIndex++, screenCaptures[normalFrameIndex].Slot.Index, false, null, normalComposition)
            {
                TimelineTicks = normalTicks,
                ElapsedTimerText = renderSettings.Timer.Enabled
                    ? ElapsedTimerFormatter.Format(timerTicks)
                    : null
            });
        }

        var timings = MediaFoundationFrameTimingPlanner.Plan(
            output.Count,
            settings.OutputFramesPerSecond);
        for (var index = 0; index < output.Count; index++)
        {
            output[index] = output[index] with
            {
                PresentationTicks = timings[index].PresentationTicks,
                DurationTicks = timings[index].DurationTicks
            };
        }

        return output;
    }

    private static RenderFramePlan[] AttachSources(
        IReadOnlyList<RenderFramePlan> plans,
        SessionRenderInput input)
    {
        var slotsByIndex = input.Slots.ToDictionary(static slot => slot.Slot.Index);
        var highlightsById = input.Highlights.ToDictionary(static highlight => highlight.Id, StringComparer.Ordinal);
        var output = new RenderFramePlan[plans.Count];

        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            if (!plan.IsHighlightHold)
            {
                if (plan.SlotIndex is not int slotIndex
                    || !slotsByIndex.TryGetValue(slotIndex, out var slot))
                {
                    throw new InvalidDataException("A normal render frame does not reference a known timeline slot.");
                }

                output[index] = plan with { Sources = FromSlot(slot) };
                continue;
            }

            if (plan.HighlightId is null
                || !highlightsById.TryGetValue(plan.HighlightId, out var highlight))
            {
                throw new InvalidDataException("A highlight hold does not reference a known highlight event.");
            }

            var fallbackSlot = input.Slots
                .Where(slot => slot.Slot.ActiveTargetTicks <= plan.TimelineTicks)
                .LastOrDefault();
            output[index] = plan with
            {
                Sources = FromHighlight(highlight, fallbackSlot)
            };
        }

        return output;
    }

    private static RenderFrameSources FromSlot(RenderAssetSlot slot)
    {
        return new RenderFrameSources(
            slot.ScreenAssetReference,
            slot.CameraAssetReference,
            slot.ScreenIsGap,
            slot.CameraIsGap,
            slot.ScreenIsDuplicate,
            slot.CameraIsDuplicate,
            ScreenUsesHighlightAsset: false,
            CameraUsesHighlightAsset: false,
            slot.ScreenAgeTicks,
            slot.CameraAgeTicks);
    }

    private static RenderFrameSources FromHighlight(
        HighlightEvent highlight,
        RenderAssetSlot? fallbackSlot)
    {
        var fallback = fallbackSlot is null
            ? RenderFrameSources.Empty
            : FromSlot(fallbackSlot);
        var usesScreenAsset = highlight.Source is HighlightSource.Screen or HighlightSource.Both
            && highlight.ScreenAssetReference is not null;
        var usesCameraAsset = highlight.Source is HighlightSource.Camera or HighlightSource.Both
            && highlight.CameraAssetReference is not null;
        var screenReference = usesScreenAsset
            ? highlight.ScreenAssetReference
            : fallback.ScreenAssetReference;
        var cameraReference = usesCameraAsset
            ? highlight.CameraAssetReference
            : fallback.CameraAssetReference;

        return new RenderFrameSources(
            screenReference,
            cameraReference,
            screenReference is null,
            cameraReference is null,
            usesScreenAsset ? false : fallback.ScreenIsDuplicate,
            usesCameraAsset ? false : fallback.CameraIsDuplicate,
            usesScreenAsset,
            usesCameraAsset,
            usesScreenAsset ? 0 : fallback.ScreenAgeTicks,
            usesCameraAsset ? 0 : fallback.CameraAgeTicks);
    }

    private static Dictionary<int, IReadOnlyList<HighlightHold>> BuildHoldMap(
        IReadOnlyList<HighlightEvent> highlights,
        IReadOnlyList<SlotCapture> captures,
        int outputFramesPerSecond,
        IReadOnlyList<PauseRange>? pauses,
        long? sessionStopTicks)
    {
        var map = new Dictionary<int, IReadOnlyList<HighlightHold>>();
        if (captures.Count == 0)
        {
            return map;
        }

        var startTicks = captures[0].Slot.WallTargetTicks;
        var stopTicks = sessionStopTicks ?? checked(captures[^1].Slot.WallTargetTicks + 1);
        var intervalTicks = captures.Count > 1
            ? captures[1].Slot.ActiveTargetTicks - captures[0].Slot.ActiveTargetTicks
            : TimelineConstants.TicksPerSecond;
        foreach (var highlight in highlights)
        {
            var placement = HighlightPlacementCalculator.Calculate(
                highlight.EventTicks,
                startTicks,
                stopTicks,
                intervalTicks,
                highlight.HoldTicks,
                outputFramesPerSecond,
                pauses);
            var normalFramesBefore = captures
                .TakeWhile(capture => capture.Slot.ActiveTargetTicks <= placement.ActiveEventTicks)
                .Count();
            if (placement.HoldFrames > 0)
            {
                if (!map.TryGetValue(normalFramesBefore, out var existing))
                {
                    existing = [];
                }

                map[normalFramesBefore] = existing
                    .Append(new HighlightHold(
                        highlight,
                        placement.HoldFrames,
                        placement.ActiveEventTicks,
                        TimelinePlanner.WallTargetForActiveTarget(
                            startTicks,
                            placement.ActiveEventTicks,
                            pauses)))
                    .ToArray();
            }
        }

        return map;
    }

    private sealed record HighlightHold(
        HighlightEvent Event,
        int HoldFrames,
        long ActiveEventTicks,
        long WallEventTicks);
}

internal static class ElapsedTimerFormatter
{
    public static string Format(long elapsedTicks)
    {
        elapsedTicks = Math.Max(0, elapsedTicks);
        var totalSeconds = elapsedTicks / TimelineConstants.TicksPerSecond;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        return hours == 0
            ? $"{minutes:00}:{seconds:00}"
            : $"{hours}:{minutes:00}:{seconds:00}";
    }
}
