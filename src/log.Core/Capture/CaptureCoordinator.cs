using Log.Core.Store;
using Log.Core.Timeline;
using Log.Core.Diagnostics;

namespace Log.Core.Capture;

public sealed class CaptureCoordinator
{
    public static Task<CaptureRunResult> RunAsync(
        IFrameSource source,
        IClock clock,
        IEncoder encoder,
        string rootDirectory,
        string sessionId,
        CaptureRunOptions options,
        CaptureSafetyGate safetyGate,
        CatalogStore? catalog,
        HighlightInbox? highlightInbox,
        CaptureMetrics? metrics,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            source,
            clock,
            encoder,
            rootDirectory,
            sessionId,
            options,
            safetyGate,
            catalog,
            highlightInbox,
            metrics,
            stopController: null,
            cancellationToken);
    }

    public static Task<CaptureRunResult> RunAsync(
        IFrameSource source,
        IClock clock,
        IEncoder encoder,
        string rootDirectory,
        string sessionId,
        CaptureRunOptions options,
        CaptureSafetyGate safetyGate,
        CatalogStore? catalog = null,
        HighlightInbox? highlightInbox = null,
        CaptureMetrics? metrics = null,
        CaptureStopController? stopController = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsyncCore(
            source,
            clock,
            encoder,
            rootDirectory,
            sessionId,
            options,
            safetyGate,
            providedStore: null,
            catalog,
            highlightInbox,
            metrics,
            stopController,
            startupReady: null,
            cancellationToken: cancellationToken);
    }

    public static Task<CaptureRunResult> RunAsync(
        SessionStore store,
        IFrameSource source,
        IClock clock,
        IEncoder encoder,
        string rootDirectory,
        string sessionId,
        CaptureRunOptions options,
        CaptureSafetyGate safetyGate,
        CatalogStore? catalog = null,
        HighlightInbox? highlightInbox = null,
        CaptureMetrics? metrics = null,
        CaptureStopController? stopController = null,
        Action? startupReady = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return RunAsyncCore(
            source,
            clock,
            encoder,
            rootDirectory,
            sessionId,
            options,
            safetyGate,
            store,
            catalog,
            highlightInbox,
            metrics,
            stopController,
            startupReady,
            cancellationToken);
    }

    private static async Task<CaptureRunResult> RunAsyncCore(
        IFrameSource source,
        IClock clock,
        IEncoder encoder,
        string rootDirectory,
        string sessionId,
        CaptureRunOptions options,
        CaptureSafetyGate safetyGate,
        SessionStore? providedStore,
        CatalogStore? catalog = null,
        HighlightInbox? highlightInbox = null,
        CaptureMetrics? metrics = null,
        CaptureStopController? stopController = null,
        Action? startupReady = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(encoder);
        using var encoderLifetime = new CaptureEncoderLifetime(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(safetyGate);
        if (options.SourceMode != "screen")
        {
            throw new ArgumentException(
                "New capture sessions require source mode screen.",
                nameof(options));
        }

        await using var storeLifetime = new SessionStoreLifetime(providedStore);
        using var captureLease = CaptureSessionRegistry.Acquire(rootDirectory);
        long startTicks;
        long stopTicks;
        int slotCount;
        var isAdaptive = options.IsAdaptiveSampling;
        var isRandom = options.IsRandomSampling;
        var usesOpenEndedSampling = isAdaptive || isRandom;
        try
        {
            highlightInbox?.BeginAccepting();
            startTicks = clock.NowTicks;
            stopTicks = usesOpenEndedSampling
                ? startTicks
                : checked(startTicks + options.DurationTicks);
            slotCount = usesOpenEndedSampling ? 0 : CountSlots(options.DurationTicks, options.IntervalTicks);
            EnsureStorageAvailable(rootDirectory, options);
        }
        catch
        {
            highlightInbox?.StopAccepting();
            StopSafetyGateIfStarted(safetyGate);
            throw;
        }

        SessionStore store;
        if (providedStore is null)
        {
            try
            {
                store = await SessionStore.CreateAsync(
                    rootDirectory,
                    sessionId,
                    options.SourceMode,
                    startTicks,
                    usesOpenEndedSampling
                        ? AdaptiveSamplingPolicy.GetIntervalTicks(0)
                        : options.IntervalTicks,
                    0,
                    adaptiveSampling: isAdaptive,
                    randomSampling: isRandom,
                    randomSeed: options.RandomSeed,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                storeLifetime.Adopt(store);
            }
            catch
            {
                highlightInbox?.StopAccepting();
                StopSafetyGateIfStarted(safetyGate);
                throw;
            }
        }
        else
        {
            store = providedStore;
        }
        if (catalog is not null)
        {
            try
            {
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                highlightInbox?.StopAccepting();
                StopSafetyGateIfStarted(safetyGate);
                throw;
            }
        }

        var cache = new FrameCache();
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var controlCancellation = stopController is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopController.Token);
        var controlToken = controlCancellation?.Token ?? cancellationToken;
        using var sourceHealthCancellation = CancellationTokenSource.CreateLinkedTokenSource(controlToken);
        var sourceTask = Task.CompletedTask;
        using var pauseTracker = new CapturePauseTracker(safetyGate, clock);
        var gapCount = 0;
        var duplicateCount = 0;
        var writtenSlotCount = 0;
        FrameSample? lastAssetFrame = null;
        string? lastAssetReference = null;

        try
        {
            if (!safetyGate.Snapshot().CanPersistFrames)
            {
                throw new InvalidOperationException(
                    "Capture cannot begin until the persistent recording indicator is verified.");
            }

            await store.StartRecordingAsync(cancellationToken).ConfigureAwait(false);
            if (catalog is not null)
            {
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, cancellationToken).ConfigureAwait(false);
            }
            sourceTask = ReadFramesAsync(
                source,
                cache,
                metrics,
                options.SourceMode,
                safetyGate,
                clock,
                sourceCancellation.Token);
            var adaptiveTargetTicks = 0L;
            var adaptiveSlotIndex = 0;
            var adaptiveIntervalTicks = usesOpenEndedSampling
                ? AdaptiveSamplingPolicy.GetIntervalTicks(0)
                : options.IntervalTicks;
            var randomWindowStartTicks = CaptureSchedulingHelpers.GetRandomWindowStartTicks(
                store.Manifest.RandomSampling,
                store.Manifest.Slots.Select(static slot => slot.TargetTicks));
            RandomSamplingWindow? randomWindow = null;
            SetSampleInterval(source, adaptiveIntervalTicks);
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceTask.IsFaulted || sourceTask.IsCanceled)
            {
                await sourceTask.ConfigureAwait(false);
            }
            // This is the first point where storage and source scheduling are ready to capture.
            startupReady?.Invoke();
            for (var index = 0; usesOpenEndedSampling || index < slotCount; index++)
            {
                if (stopController?.IsStopRequested == true)
                {
                    break;
                }

                await CaptureSourceHealth.WaitForRecordingAsync(
                    pauseTracker,
                    sourceTask,
                    sourceHealthCancellation).ConfigureAwait(false);
                await PersistPendingPausesAsync(store, catalog, pauseTracker, cancellationToken).ConfigureAwait(false);
                long activeTargetTicks;
                if (isRandom)
                {
                    randomWindow = await CaptureSchedulingHelpers.EnsureRandomSamplingWindowAsync(
                        store,
                        randomWindowStartTicks,
                        cancellationToken).ConfigureAwait(false);
                    activeTargetTicks = randomWindow.TargetTicks;
                }
                else if (isAdaptive)
                {
                    activeTargetTicks = adaptiveTargetTicks;
                }
                else
                {
                    activeTargetTicks = checked((long)index * options.IntervalTicks);
                }
                if (isAdaptive)
                {
                    var nextIntervalTicks = AdaptiveSamplingPolicy.GetIntervalTicks(activeTargetTicks);
                    if (nextIntervalTicks != adaptiveIntervalTicks)
                    {
                        await store.CompactAdaptiveSamplesAsync(
                            activeTargetTicks,
                            cancellationToken).ConfigureAwait(false);
                        adaptiveIntervalTicks = nextIntervalTicks;
                        adaptiveSlotIndex = CaptureSchedulingHelpers.NextSlotIndex(
                            store.Manifest.Slots.Select(static slot => slot.Index));
                        adaptiveTargetTicks = activeTargetTicks;
                        SetSampleInterval(source, adaptiveIntervalTicks);
                        lastAssetFrame = null;
                        lastAssetReference = null;
                    }
                }
                var outputSlotIndex = isAdaptive
                    ? adaptiveSlotIndex
                    : isRandom
                        ? CaptureSchedulingHelpers.NextSlotIndex(
                            store.Manifest.Slots.Select(static slot => slot.Index))
                        : index;
                long wallTargetTicks;
                while (true)
                {
                    var waitSnapshot = safetyGate.Snapshot();
                    wallTargetTicks = TimelinePlanner.WallTargetForActiveTarget(
                        startTicks,
                        activeTargetTicks,
                        pauseTracker.Pauses);
                    await CaptureSourceHealth.WaitUntilAsync(
                        pauseTracker,
                        wallTargetTicks,
                        sourceTask,
                        sourceHealthCancellation).ConfigureAwait(false);
                    await CaptureSourceHealth.WaitForRecordingAsync(
                        pauseTracker,
                        sourceTask,
                        sourceHealthCancellation).ConfigureAwait(false);
                    await PersistPendingPausesAsync(store, catalog, pauseTracker, cancellationToken).ConfigureAwait(false);
                    var updatedWallTargetTicks = TimelinePlanner.WallTargetForActiveTarget(
                        startTicks,
                        activeTargetTicks,
                        pauseTracker.Pauses);
                    var currentSafetySnapshot = safetyGate.Snapshot();
                    if (updatedWallTargetTicks == wallTargetTicks
                        && currentSafetySnapshot.CanPersistFrames
                        && currentSafetySnapshot.TransitionVersion == waitSnapshot.TransitionVersion)
                    {
                        break;
                    }
                }

                if (stopController?.IsStopRequested == true)
                {
                    break;
                }

                var slot = new TimelineSlot(outputSlotIndex, activeTargetTicks, wallTargetTicks);
                metrics?.RecordScheduledSlot(slot.WallTargetTicks, clock.NowTicks);
                CaptureEventSource.Instance.CaptureTickScheduled(options.SourceMode, slot.WallTargetTicks);
                var frames = cache.SnapshotAll();
                var selection = frames.Count == 0
                    ? null
                    : FrameSelector.SelectLatestAtOrBefore(
                        frames,
                        slot.WallTargetTicks,
                        options.MaxFrameAgeTicks,
                        timestampOffsetTicks: 0,
                        sourceEpoch: cache.CurrentSourceEpoch);
                var isGap = selection is null;
                var isDuplicate = selection is not null && selection.Value.IsDuplicate;
                var selectedFrame = selection?.Sample;

                if (isGap)
                {
                    gapCount++;
                }

                if (isDuplicate)
                {
                    duplicateCount++;
                }
                metrics?.RecordSlot(isGap, isDuplicate);
                CaptureEventSource.Instance.SlotRecorded(
                    options.SourceMode,
                    slot.Index,
                    isGap ? "gap" : isDuplicate ? "duplicate" : "filled");

                await WriteEncodedFrameAsync(
                    encoder,
                    new EncodedFrame(
                        slot.Index,
                        slot.ActiveTargetTicks,
                            selectedFrame,
                            isDuplicate,
                            selection?.AgeTicks ?? 0),
                        options.SourceMode,
                        metrics,
                        cancellationToken).ConfigureAwait(false);
                var diskWriteStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                long diskWriteByteLength = 0;
                CaptureEventSource.Instance.DiskWriteStarted(options.SourceMode, slot.Index);
                try
                {
                    var assetReference = isAdaptive
                        ? AdaptiveAssetNaming.For(options.SourceMode, slot.ActiveTargetTicks)
                        : isRandom
                            ? RandomAssetNaming.For(options.SourceMode, slot.ActiveTargetTicks)
                            : $"{options.SourceMode}/slot-{slot.Index:D6}";
                    if (selectedFrame is not null
                        && CaptureFrameAssetWriter.CanWriteBgraFrame(selectedFrame))
                    {
                        if (ReferenceEquals(selectedFrame, lastAssetFrame)
                            && lastAssetReference is not null)
                        {
                            assetReference = lastAssetReference;
                        }
                        else
                        {
                            diskWriteByteLength = await CaptureFrameAssetWriter.WriteAsync(
                                store.DirectoryPath,
                                options.SourceMode,
                                assetReference,
                                selectedFrame,
                                cancellationToken).ConfigureAwait(false);
                            lastAssetFrame = selectedFrame;
                            lastAssetReference = assetReference;
                        }
                    }
                    else
                    {
                        lastAssetFrame = null;
                        lastAssetReference = null;
                    }

                    await store.AppendSlotAsync(
                        new ManifestSlot(
                            slot.Index,
                            slot.ActiveTargetTicks,
                            selectedFrame?.SourceTimestampTicks ?? -1,
                            isDuplicate,
                            selection?.AgeTicks ?? 0,
                            assetReference),
                        sourceFrame: selectedFrame,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CaptureEventSource.Instance.DiskWriteFailed(options.SourceMode, slot.Index);
                    throw;
                }
                catch
                {
                    metrics?.RecordDropped(CaptureDropReason.Disk);
                    CaptureEventSource.Instance.DiskWriteFailed(options.SourceMode, slot.Index);
                    throw;
                }

                CaptureEventSource.Instance.DiskWriteCompleted(
                    options.SourceMode,
                    slot.Index,
                    System.Diagnostics.Stopwatch.GetTimestamp() - diskWriteStartedTimestamp,
                    diskWriteByteLength);
                CaptureEventSource.Instance.CaptureTickCompleted(
                    options.SourceMode,
                    slot.Index,
                    clock.NowTicks);
                writtenSlotCount++;
                if (isAdaptive)
                {
                    adaptiveSlotIndex = checked(adaptiveSlotIndex + 1);
                    adaptiveTargetTicks = checked(activeTargetTicks + adaptiveIntervalTicks);
                }
                else if (isRandom)
                {
                    randomWindowStartTicks = checked(randomWindow!.EndTicks);
                    randomWindow = null;
                }
                await DrainHighlightsAsync(
                    store,
                    catalog,
                    highlightInbox,
                    cancellationToken,
                    cache,
                    options.MaxFrameAgeTicks).ConfigureAwait(false);
            }

            if (stopController?.IsStopRequested == true)
            {
                return await FinalizeAfterStopAsync(
                    store,
                    catalog,
                    highlightInbox,
                    cache,
                    options.MaxFrameAgeTicks,
                    metrics,
                    encoder,
                    safetyGate,
                    pauseTracker,
                    sourceCancellation,
                    sourceTask,
                    clock.NowTicks,
                    writtenSlotCount,
                    gapCount,
                    duplicateCount,
                    isAdaptive,
                    isRandom).ConfigureAwait(false);
            }

            await DrainHighlightsAsync(
                store,
                catalog,
                highlightInbox,
                cancellationToken,
                cache,
                options.MaxFrameAgeTicks).ConfigureAwait(false);
            await WaitForStopBoundaryAsync(
                store,
                catalog,
                pauseTracker,
                sourceTask,
                sourceHealthCancellation,
                startTicks,
                options.DurationTicks,
                controlToken).ConfigureAwait(false);
            if (stopController?.IsStopRequested == true)
            {
                return await FinalizeAfterStopAsync(
                    store,
                    catalog,
                    highlightInbox,
                    cache,
                    options.MaxFrameAgeTicks,
                    metrics,
                    encoder,
                    safetyGate,
                    pauseTracker,
                    sourceCancellation,
                    sourceTask,
                    clock.NowTicks,
                    writtenSlotCount,
                    gapCount,
                    duplicateCount,
                    isAdaptive,
                    isRandom).ConfigureAwait(false);
            }

            if (!await CaptureSourceShutdown.StopAsync(sourceTask, sourceCancellation).ConfigureAwait(false))
            {
                CaptureEventSource.Instance.SourceShutdownTimedOut(options.SourceMode);
                throw new IOException("The capture source did not stop within the shutdown timeout.");
            }
            pauseTracker.CloseAtCurrentTime();
            highlightInbox?.StopAccepting();
            safetyGate.Stop();
            await PersistPendingPausesAsync(store, catalog, pauseTracker, cancellationToken).ConfigureAwait(false);
            await DrainHighlightsAsync(
                store,
                catalog,
                highlightInbox,
                cancellationToken,
                cache,
                options.MaxFrameAgeTicks).ConfigureAwait(false);
            if (metrics is not null)
            {
                await store.UpdateMetricsAsync(metrics.Snapshot(), cancellationToken).ConfigureAwait(false);
            }
            await store.BeginFinalizingAsync(cancellationToken).ConfigureAwait(false);
            if (catalog is not null)
            {
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, cancellationToken).ConfigureAwait(false);
            }
            CaptureEventSource.Instance.EncoderFinalizationStarted(options.SourceMode);
            await encoder.FinalizeAsync(cancellationToken).ConfigureAwait(false);
            CaptureEventSource.Instance.EncoderFinalizationCompleted(options.SourceMode);
            var completedStopTicks = TimelinePlanner.WallTargetForActiveTarget(
                startTicks,
                options.DurationTicks,
                pauseTracker.Pauses);
            await store.CompleteAsync(completedStopTicks, cancellationToken).ConfigureAwait(false);
            if (catalog is not null)
            {
                await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, cancellationToken).ConfigureAwait(false);
            }
            return new CaptureRunResult(
                sessionId,
                startTicks,
                completedStopTicks,
                writtenSlotCount,
                gapCount,
                duplicateCount);
        }
        catch (OperationCanceledException) when (IsNormalStopRequested(stopController, cancellationToken))
        {
            try
            {
                return await FinalizeAfterStopAsync(
                    store,
                    catalog,
                    highlightInbox,
                    cache,
                    options.MaxFrameAgeTicks,
                    metrics,
                    encoder,
                    safetyGate,
                    pauseTracker,
                    sourceCancellation,
                    sourceTask,
                    clock.NowTicks,
                    writtenSlotCount,
                    gapCount,
                    duplicateCount,
                    isAdaptive,
                    isRandom).ConfigureAwait(false);
            }
            catch
            {
                highlightInbox?.StopAccepting();
                if (safetyGate.Snapshot().State != CaptureSafetyState.AwaitingStart)
                {
                    safetyGate.Stop();
                }

                await StopSourceForRecoveryAsync(
                    sourceTask,
                    sourceCancellation,
                    store.Manifest.SourceMode).ConfigureAwait(false);
                try
                {
                    await DrainHighlightsAsync(
                        store,
                        catalog,
                        highlightInbox,
                        CancellationToken.None,
                        cache,
                        options.MaxFrameAgeTicks).ConfigureAwait(false);
                }
                catch
                {
                }

                if (store.Manifest.Status is SessionStatus.Ready or SessionStatus.Exported)
                {
                    // The local manifest is already complete; a catalog mirror failure
                    // must not rewrite the durable session as interrupted.
                    if (catalog is not null)
                    {
                        try
                        {
                            await catalog.UpsertSessionAsync(
                                store.Manifest,
                                store.DirectoryPath,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    throw;
                }

                await MarkInterruptedAsync(store, catalog, metrics).ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            pauseTracker.CloseAtCurrentTime();
            highlightInbox?.StopAccepting();
            if (safetyGate.Snapshot().State != CaptureSafetyState.AwaitingStart)
            {
                safetyGate.Stop();
            }

            await StopSourceForRecoveryAsync(
                sourceTask,
                sourceCancellation,
                store.Manifest.SourceMode).ConfigureAwait(false);
            try
            {
                await PersistPendingPausesAsync(store, catalog, pauseTracker, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                await DrainHighlightsAsync(
                    store,
                    catalog,
                    highlightInbox,
                    CancellationToken.None,
                    cache,
                    options.MaxFrameAgeTicks).ConfigureAwait(false);
            }
            catch
            {
            }

            if (store.Manifest.Status is SessionStatus.Ready or SessionStatus.Exported)
            {
                // The local manifest is already complete; a catalog mirror failure
                // must not rewrite the durable session as interrupted.
                if (catalog is not null)
                {
                    try
                    {
                        await catalog.UpsertSessionAsync(
                            store.Manifest,
                            store.DirectoryPath,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                throw;
            }

            await MarkInterruptedAsync(store, catalog, metrics).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await StopSourceForRecoveryAsync(
                sourceTask,
                sourceCancellation,
                store.Manifest.SourceMode).ConfigureAwait(false);
        }
    }

    private static bool IsNormalStopRequested(
        CaptureStopController? stopController,
        CancellationToken cancellationToken)
    {
        return stopController?.IsStopRequested == true && !cancellationToken.IsCancellationRequested;
    }

    private static int CountSlots(long durationTicks, long intervalTicks)
    {
        return checked((int)((durationTicks - 1) / intervalTicks + 1));
    }

    private static void SetSampleInterval(IFrameSource source, long intervalTicks)
    {
        if (source is IFrameSourceSamplingControl samplingControl)
        {
            samplingControl.SetSampleInterval(intervalTicks);
        }
    }

    private static void StopSafetyGateIfStarted(CaptureSafetyGate safetyGate)
    {
        if (safetyGate.Snapshot().State != CaptureSafetyState.AwaitingStart)
        {
            safetyGate.Stop();
        }
    }

    private static async Task<CaptureRunResult> FinalizeAfterStopAsync(
        SessionStore store,
        CatalogStore? catalog,
        HighlightInbox? highlightInbox,
        FrameCache cache,
        long maxFrameAgeTicks,
        CaptureMetrics? metrics,
        IEncoder encoder,
        CaptureSafetyGate safetyGate,
        CapturePauseTracker pauseTracker,
        CancellationTokenSource sourceCancellation,
        Task sourceTask,
        long stopTicks,
        int slotCount,
        int gapCount,
        int duplicateCount,
        bool adaptiveSampling,
        bool randomSampling)
    {
        if (!await CaptureSourceShutdown.StopAsync(sourceTask, sourceCancellation).ConfigureAwait(false))
        {
            CaptureEventSource.Instance.SourceShutdownTimedOut(store.Manifest.SourceMode);
            throw new IOException("The capture source did not stop within the shutdown timeout.");
        }
        pauseTracker.CloseAtCurrentTime();
        highlightInbox?.StopAccepting();
        safetyGate.Stop();
        // Stopping while paused closes the pause at a clock reading later than the
        // stopTicks passed in; the stop must not land before the pause ends.
        foreach (var pause in pauseTracker.Pauses)
        {
            stopTicks = Math.Max(stopTicks, pause.EndTicks);
        }

        await PersistPendingPausesAsync(store, catalog, pauseTracker, CancellationToken.None).ConfigureAwait(false);
        await DrainHighlightsAsync(
            store,
            catalog,
            highlightInbox,
            CancellationToken.None,
            cache,
            maxFrameAgeTicks).ConfigureAwait(false);
        if (adaptiveSampling)
        {
            var activeDurationTicks = TimelinePlanner.ActiveDuration(
                checked(stopTicks - store.Manifest.StartedAtTicks),
                store.Manifest.StartedAtTicks,
                stopTicks,
                pauseTracker.Pauses);
            await store.CompactAdaptiveSamplesAsync(
                activeDurationTicks,
                CancellationToken.None).ConfigureAwait(false);
        }
        if (metrics is not null)
        {
            await store.UpdateMetricsAsync(metrics.Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }

        await store.BeginFinalizingAsync(CancellationToken.None).ConfigureAwait(false);
        if (catalog is not null)
        {
            await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, CancellationToken.None)
                .ConfigureAwait(false);
        }

        CaptureEventSource.Instance.EncoderFinalizationStarted(store.Manifest.SourceMode);
        await encoder.FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
        CaptureEventSource.Instance.EncoderFinalizationCompleted(store.Manifest.SourceMode);
        await store.CompleteAsync(stopTicks, CancellationToken.None).ConfigureAwait(false);
        if (catalog is not null)
        {
            await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var reportedSlotCount = adaptiveSampling || randomSampling
            ? store.Manifest.Slots.Select(static slot => slot.Index).Distinct().Count()
            : slotCount;

        return new CaptureRunResult(
            store.Manifest.Id,
            store.Manifest.StartedAtTicks,
            stopTicks,
            reportedSlotCount,
            gapCount,
            duplicateCount);
    }

    private static async Task MarkInterruptedAsync(
        SessionStore store,
        CatalogStore? catalog,
        CaptureMetrics? metrics)
    {
        if (metrics is not null)
        {
            try
            {
                await store.UpdateMetricsAsync(metrics.Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Recovery status is more important than a best-effort metrics snapshot.
            }
        }

        await store.MarkInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
        if (catalog is not null)
        {
            await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task StopSourceForRecoveryAsync(
        Task sourceTask,
        CancellationTokenSource sourceCancellation,
        string sourceName)
    {
        try
        {
            if (!await CaptureSourceShutdown.StopAsync(sourceTask, sourceCancellation).ConfigureAwait(false))
            {
                CaptureEventSource.Instance.SourceShutdownTimedOut(sourceName);
            }
        }
        catch
        {
            // Preserve the original capture failure while recovery records the interruption.
        }
    }

    private static async Task WaitForStopBoundaryAsync(
        SessionStore store,
        CatalogStore? catalog,
        CapturePauseTracker pauseTracker,
        Task sourceTask,
        CancellationTokenSource sourceHealthCancellation,
        long startTicks,
        long durationTicks,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await CaptureSourceHealth.WaitForRecordingAsync(
                pauseTracker,
                sourceTask,
                sourceHealthCancellation).ConfigureAwait(false);
            var wallTargetTicks = TimelinePlanner.WallTargetForActiveTarget(
                startTicks,
                durationTicks,
                pauseTracker.Pauses);
            await CaptureSourceHealth.WaitUntilAsync(
                pauseTracker,
                wallTargetTicks,
                sourceTask,
                sourceHealthCancellation).ConfigureAwait(false);
            await CaptureSourceHealth.WaitForRecordingAsync(
                pauseTracker,
                sourceTask,
                sourceHealthCancellation).ConfigureAwait(false);
            await PersistPendingPausesAsync(store, catalog, pauseTracker, cancellationToken).ConfigureAwait(false);
            var updatedWallTargetTicks = TimelinePlanner.WallTargetForActiveTarget(
                startTicks,
                durationTicks,
                pauseTracker.Pauses);
            if (updatedWallTargetTicks == wallTargetTicks)
            {
                return;
            }
        }
    }

    private static async ValueTask WriteEncodedFrameAsync(
        IEncoder encoder,
        EncodedFrame frame,
        string source,
        CaptureMetrics? metrics,
        CancellationToken cancellationToken)
    {
        var startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        CaptureEventSource.Instance.EncoderWriteStarted(source, frame.SlotIndex);
        try
        {
            await encoder.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CaptureEventSource.Instance.EncoderWriteFailed(source, frame.SlotIndex);
            throw;
        }
        catch
        {
            metrics?.RecordDropped(CaptureDropReason.Encoder);
            CaptureEventSource.Instance.EncoderWriteFailed(source, frame.SlotIndex);
            throw;
        }

        CaptureEventSource.Instance.EncoderWriteCompleted(
            source,
            frame.SlotIndex,
            System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp);
    }

    private static async Task ReadFramesAsync(
        IFrameSource source,
        FrameCache cache,
        CaptureMetrics? metrics,
        string sourceName,
        CaptureSafetyGate safetyGate,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Action<CaptureSafetySnapshot> clearCacheOnSafetyTransition = snapshot =>
        {
            if (snapshot.State is CaptureSafetyState.Paused or CaptureSafetyState.Stopping)
            {
                RecordCacheEvictions(cache, sourceName, metrics);
                cache.Clear();
            }
        };
        safetyGate.StateChanged += clearCacheOnSafetyTransition;
        try
        {
            await foreach (var frame in source.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                var safetySnapshot = safetyGate.Snapshot();
                if (!safetySnapshot.CanPersistFrames)
                {
                    continue;
                }

                if (!cache.Publish(frame, out var isOutOfOrder))
                {
                    RecordCacheEvictions(cache, sourceName, metrics);
                    metrics?.RecordDropped(CaptureDropReason.OversizeFrame);
                    CaptureEventSource.Instance.FrameDropped(sourceName, "oversize-frame");
                    continue;
                }

                var postPublishSnapshot = safetyGate.Snapshot();
                if (!postPublishSnapshot.CanPersistFrames
                    || postPublishSnapshot.TransitionVersion != safetySnapshot.TransitionVersion)
                {
                    RecordCacheEvictions(cache, sourceName, metrics);
                    cache.Clear();
                    continue;
                }

                RecordCacheEvictions(cache, sourceName, metrics);

                if (isOutOfOrder)
                {
                    CaptureEventSource.Instance.FrameTimestampOutOfOrder(sourceName, frame.SourceEpoch);
                }

                metrics?.RecordFrameArrived();
                CaptureEventSource.Instance.FrameArrived(sourceName, frame.SourceTimestampTicks);
            }

            if (source is IFrameSourceLifecycle { Completion: FrameSourceCompletion.Unavailable })
            {
                MarkSourceUnavailable(sourceName, "completed-unavailable", safetyGate, clock);
            }
        }
        catch (UnauthorizedAccessException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkSourceUnavailable(sourceName, "access-denied", safetyGate, clock);
        }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkSourceUnavailable(sourceName, "disposed", safetyGate, clock);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkSourceUnavailable(sourceName, "reader-canceled", safetyGate, clock);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && (exception is IOException
                or InvalidDataException
                or ArgumentException
                or OverflowException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException))
        {
            MarkSourceUnavailable(sourceName, "reader-fault", safetyGate, clock);
        }
        finally
        {
            safetyGate.StateChanged -= clearCacheOnSafetyTransition;
        }
    }

    private static void MarkSourceUnavailable(
        string sourceName,
        string reason,
        CaptureSafetyGate safetyGate,
        IClock clock)
    {
        CaptureEventSource.Instance.SourceUnavailable(sourceName, reason);
        if (safetyGate.Snapshot().State == CaptureSafetyState.Recording)
        {
            safetyGate.Pause(CapturePauseReason.SourceUnavailable, clock.NowTicks);
        }
    }

    private static void RecordCacheEvictions(FrameCache cache, string source, CaptureMetrics? metrics)
    {
        for (var count = cache.DrainDroppedBudgetFrameCount(); count > 0; count--)
        {
            metrics?.RecordDropped(CaptureDropReason.Scheduler);
            CaptureEventSource.Instance.FrameDropped(source, "cache-budget");
        }
    }

    private static void EnsureStorageAvailable(string rootDirectory, CaptureRunOptions options)
    {
        if (options.EstimatedBytesPerHour == 0 && options.ReserveBytes == 0)
        {
            return;
        }

        var availability = StorageGuard.Check(
            rootDirectory,
            TimeSpan.FromTicks(options.DurationTicks),
            options.EstimatedBytesPerHour,
            options.ReserveBytes);
        StorageGuard.EnsureSufficient(availability);
    }

    private static async Task DrainHighlightsAsync(
        SessionStore store,
        CatalogStore? catalog,
        HighlightInbox? highlightInbox,
        CancellationToken cancellationToken,
        FrameCache? frameCache = null,
        long maxFrameAgeTicks = 0)
    {
        if (highlightInbox is null)
        {
            return;
        }

        var pendingHighlights = highlightInbox.Drain();
        for (var index = 0; index < pendingHighlights.Count; index++)
        {
            var highlight = pendingHighlights[index];
            var manifestHighlightIndex = store.Manifest.Highlights.FindIndex(
                existing => string.Equals(existing.Id, highlight.Id, StringComparison.Ordinal));
            if (manifestHighlightIndex >= 0
                && !HasSameHighlightMetadata(store.Manifest.Highlights[manifestHighlightIndex], highlight))
            {
                // IDs are the durable identity; never let a conflicting retry
                // rewrite the manifest's event in the catalog.
                highlightInbox.Complete(1);
                continue;
            }

            try
            {
                if (manifestHighlightIndex < 0)
                {
                    if (frameCache is not null)
                    {
                        highlight = await CaptureHighlightAssetWriter.AttachAsync(
                            store.DirectoryPath,
                            highlight,
                            frameCache,
                            cameraCache: null,
                            maxFrameAgeTicks,
                            cameraOffsetTicks: 0,
                            cancellationToken).ConfigureAwait(false);
                    }

                    await store.AppendHighlightAsync(highlight, cancellationToken).ConfigureAwait(false);
                    manifestHighlightIndex = store.Manifest.Highlights.Count - 1;
                }
                else
                {
                    highlight = store.Manifest.Highlights[manifestHighlightIndex];
                }
            }
            catch
            {
                manifestHighlightIndex = store.Manifest.Highlights.FindIndex(
                    existing => string.Equals(existing.Id, highlight.Id, StringComparison.Ordinal));
                if (manifestHighlightIndex < 0)
                {
                    highlightInbox.RequeueFront(pendingHighlights.Skip(index).ToArray());
                }
                else if (catalog is null)
                {
                    highlightInbox.Complete(1);
                    highlightInbox.RequeueFront(pendingHighlights.Skip(index + 1).ToArray());
                }
                else
                {
                    // The local append may have succeeded before its snapshot failed;
                    // keep the event so the catalog mirror can be repaired.
                    var retryHighlights = pendingHighlights.Skip(index).ToArray();
                    retryHighlights[0] = store.Manifest.Highlights[manifestHighlightIndex];
                    highlightInbox.RequeueFront(retryHighlights);
                }

                throw;
            }

            if (catalog is null)
            {
                highlightInbox.Complete(1);
                continue;
            }

            try
            {
                await catalog.UpsertHighlightAsync(
                    store.Manifest.Id,
                    highlight,
                    manifestHighlightIndex,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The manifest write may have succeeded before the catalog failed.
                // Keep the current event so the next drain can repair the catalog.
                var retryHighlights = pendingHighlights.Skip(index).ToArray();
                retryHighlights[0] = highlight;
                highlightInbox.RequeueFront(retryHighlights);
                throw;
            }

            highlightInbox.Complete(1);
        }
    }

    private static bool HasSameHighlightMetadata(HighlightEvent left, HighlightEvent right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && left.EventTicks == right.EventTicks
            && left.Source == right.Source
            && left.HoldTicks == right.HoldTicks
            && left.Included == right.Included;
    }

    private static async Task PersistPendingPausesAsync(
        SessionStore store,
        CatalogStore? catalog,
        CapturePauseTracker pauseTracker,
        CancellationToken cancellationToken)
    {
        var pendingPauses = pauseTracker.DrainUnpersistedPauses();
        for (var index = 0; index < pendingPauses.Count; index++)
        {
            var pause = pendingPauses[index];
            try
            {
                if (!store.Manifest.Pauses.Contains(pause))
                {
                    await store.AppendPauseRangeAsync(pause, cancellationToken).ConfigureAwait(false);
                }

                if (catalog is not null)
                {
                    await catalog.UpsertSessionAsync(store.Manifest, store.DirectoryPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                pauseTracker.RequeuePauses(pendingPauses.Count - index);
                throw;
            }
        }
    }

    private sealed class SessionStoreLifetime(SessionStore? initialStore) : IAsyncDisposable
    {
        private SessionStore? store = initialStore;

        public void Adopt(SessionStore value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Interlocked.CompareExchange(ref store, value, null) is not null)
            {
                throw new InvalidOperationException("The capture session store was already adopted.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            var value = Interlocked.Exchange(ref store, null);
            if (value is not null)
            {
                await value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
