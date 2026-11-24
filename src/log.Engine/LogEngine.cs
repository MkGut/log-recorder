using Log.Core.Capture;
using Log.Core.Diagnostics;
using Log.Core.Store;
using Log.Core.Timeline;

namespace Log.Engine;

/// <summary>
/// Headless composition root. Wires an <see cref="IFrameSource"/> (granted by the
/// consent broker), an <see cref="IClock"/>, an <see cref="IEncoder"/> (from the
/// platform factory), the crash-safe <see cref="SessionStore"/>/<see cref="CatalogStore"/>,
/// and the capture coordinator into a start -> sample-at-interval -> finalize
/// pipeline, plus a session -> MP4 export path. This is the <see cref="IAppEngine"/>
/// the WinUI shell binds to; it depends only on log.Core.
/// </summary>
public sealed class LogEngine : IAppEngine
{
    private readonly object sync = new();
    private readonly string rootDirectory;
    private readonly IClock clock;
    private readonly long clockTicksPerSecond;
    private readonly IEncoderFactory encoderFactory;
    private readonly IConsentBroker consentBroker;
    private readonly ISessionRenderer renderer;
    private readonly Func<string, long> availableFreeSpace;
    private readonly SemaphoreSlim cleanupGate = new(1, 1);

    private EngineActivity activity = EngineActivity.Idle;
    private string? sessionId;
    private CaptureSafetyGate? safetyGate;
    private Action<CaptureSafetySnapshot>? gateHandler;
    private CaptureStopController? stopController;
    private CancellationTokenSource? startupCancellation;
    private TaskCompletionSource<bool>? startupCompletion;
    private bool startInProgress;
    private TaskCompletionSource<bool>? disposeCompletion;
    private CancellationTokenSource? runCancellation;
    private Task? storageWatchdogTask;
    private HighlightInbox? highlightInbox;
    private CatalogStore? catalog;
    private Task<CaptureRunResult>? runTask;
    private bool consentReleasePending;
    private bool disposed;

    // clock.NowTicks and clockTicksPerSecond must share one unit; use the
    // timeline unit (100ns) so stored ticks match the manifest and renderer.
    // Prefer CreateDefault, which wires the normalized QpcClock.
    public LogEngine(
        string rootDirectory,
        IClock clock,
        long clockTicksPerSecond,
        IEncoderFactory encoderFactory,
        IConsentBroker consentBroker,
        ISessionRenderer renderer,
        Func<string, long>? availableFreeSpace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockTicksPerSecond);
        ArgumentNullException.ThrowIfNull(encoderFactory);
        ArgumentNullException.ThrowIfNull(consentBroker);
        ArgumentNullException.ThrowIfNull(renderer);

        this.rootDirectory = Path.GetFullPath(rootDirectory);
        this.clock = clock;
        this.clockTicksPerSecond = clockTicksPerSecond;
        this.encoderFactory = encoderFactory;
        this.consentBroker = consentBroker;
        this.renderer = renderer;
        this.availableFreeSpace = availableFreeSpace
            ?? (static driveRoot => new DriveInfo(driveRoot).AvailableFreeSpace);
    }

    /// <summary>
    /// Compose an engine over the normalized QPC timeline clock and default log
    /// root. The clock emits 100ns ticks (<see cref="TimelineConstants.TicksPerSecond"/>),
    /// matching the manifest's <c>qpc-100ns</c> unit and the renderer/timer, so
    /// stored ticks are unit-correct even where <c>Stopwatch.Frequency</c> is not
    /// 10 MHz. This mirrors the shipped WinUI composition (see App.xaml.cs).
    /// </summary>
    public static LogEngine CreateDefault(
        IEncoderFactory encoderFactory,
        IConsentBroker consentBroker,
        ISessionRenderer renderer,
        string? rootDirectory = null)
    {
        return new LogEngine(
            rootDirectory ?? LogPaths.DefaultRootDirectory,
            new QpcClock(),
            TimelineConstants.TicksPerSecond,
            encoderFactory,
            consentBroker,
            renderer);
    }

    public event Action<EngineStateSnapshot>? StateChanged;

    public event Action<Exception>? CaptureFailed;

    public event Action<CaptureRunResult>? CaptureCompleted;

    public EngineStateSnapshot State
    {
        get
        {
            lock (sync)
            {
                return CreateSnapshot();
            }
        }
    }

    public async Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource startupCts;
        TaskCompletionSource<bool> completion;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activity != EngineActivity.Idle || startInProgress)
            {
                throw new InvalidOperationException("A capture session is already active.");
            }

            // Reserve startup privately; Recording begins after the coordinator is ready.
            startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            startInProgress = true;
            startupCancellation = startupCts;
            startupCompletion = completion;
        }
        cancellationToken = startupCts.Token;

        CatalogStore? openedCatalog = null;
        CaptureSafetyGate? gate = null;
        SessionStore? createdStore = null;
        CaptureStopController? pendingController = null;
        CancellationTokenSource? pendingRunCancellation = null;
        Task<CaptureRunResult>? pendingRunTask = null;
        Task? pendingStorageWatchdogTask = null;
        Action<CaptureSafetySnapshot>? pendingGateHandler = null;
        var pendingEncoders = new List<IEncoder>();
        var storeHandedOff = false;
        var engineStateCommitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newSessionId = SessionIdGenerator.Create(DateTimeOffset.UtcNow);
            // Fail fast on insufficient disk BEFORE consent/catalog/session setup,
            // so an under-provisioned capture never runs the privacy/consent flow,
            // opens the catalog, or leaves an orphan session dir. Pure check on the
            // request; fail-closed with no optimistic estimate.
            if (request.EstimatedBytesPerHour > 0 || request.ReserveBytes > 0)
            {
                StorageGuard.EnsureSufficient(StorageGuard.Check(
                    rootDirectory,
                    request.MaxDuration,
                    request.EstimatedBytesPerHour,
                    request.ReserveBytes));
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                consentReleasePending = true;
            }
            var consent = await consentBroker
                .RequestCaptureConsentAsync(request, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (consent is null || !consent.IndicatorsVerified)
            {
                throw new InvalidOperationException(
                    "Recording consent was denied or the overt recording indicator was not verified.");
            }

            openedCatalog = await CatalogStore
                .OpenAsync(LogPaths.CatalogPath(rootDirectory), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            gate = new CaptureSafetyGate();

            var options = new CaptureRunOptions(
                durationTicks: DurationToTicksAllowZero(request.MaxDuration),
                intervalTicks: DurationToTicks(request.Interval),
                maxFrameAgeTicks: DurationToTicksAllowZero(request.MaxFrameAge),
                estimatedBytesPerHour: request.EstimatedBytesPerHour,
                reserveBytes: request.ReserveBytes,
                samplingMode: request.SamplingMode,
                randomSeed: request.RandomSeed);

            // Create the session store up front so the encoder factory can build
            // store-aware encoders (segmented MF encoder needs the live store for S8
            // rolling segments). Ownership passes to the coordinator, which disposes
            // it on every exit path; we only dispose it if we fail before handoff.
            createdStore = await SessionStore.CreateAsync(
                rootDirectory,
                newSessionId,
                "screen",
                clock.NowTicks,
                options.IsFixedSampling
                    ? options.IntervalTicks
                    : AdaptiveSamplingPolicy.GetIntervalTicks(0),
                0,
                adaptiveSampling: options.IsAdaptiveSampling,
                randomSampling: options.IsRandomSampling,
                randomSeed: options.RandomSeed,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var controller = new CaptureStopController();
            var runCts = new CancellationTokenSource();
            pendingController = controller;
            pendingRunCancellation = runCts;
            var inbox = new HighlightInbox();
            var localCatalog = openedCatalog;
            var localGate = gate;
            var localStore = createdStore;
            var coordinatorReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Build the screen encoder before starting the overt indicator, then
            // launch the single-source coordinator with the pre-created store.
            var encoder = await encoderFactory.CreateAsync(
                new EncoderContext(localStore, "screen", request.OutputWidth, request.OutputHeight, request.OutputFramesPerSecond),
                cancellationToken).ConfigureAwait(false);
            pendingEncoders.Add(encoder);
            cancellationToken.ThrowIfCancellationRequested();
            Func<Task<CaptureRunResult>> runFactory = () => CaptureCoordinator.RunAsync(
                localStore,
                consent.Source,
                clock,
                encoder,
                rootDirectory,
                newSessionId,
                options,
                localGate,
                localCatalog,
                highlightInbox: inbox,
                metrics: null,
                stopController: controller,
                cancellationToken: runCts.Token,
                startupReady: () => { coordinatorReady.TrySetResult(true); });

            pendingGateHandler = OnGateStateChanged;
            gate.StateChanged += pendingGateHandler;
            cancellationToken.ThrowIfCancellationRequested();
            gate.Start(indicatorsVerified: true);

            pendingRunTask = Task.Run(async () =>
            {
                try
                {
                    var result = await runFactory().ConfigureAwait(false);
                    coordinatorReady.TrySetException(new InvalidOperationException(
                        "The capture coordinator ended before confirming startup readiness."));
                    return result;
                }
                catch (Exception exception)
                {
                    coordinatorReady.TrySetException(exception);
                    throw;
                }
            }, CancellationToken.None);
            // The coordinator now owns the store and encoders and disposes them on every exit path.
            storeHandedOff = true;

            await coordinatorReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Monitor free space during the run and cooperatively stop before it
            // consumes the reserve, so a long capture finalizes gracefully instead
            // of hitting disk-full. Only meaningful when a reserve is configured;
            // cleanup cancels and awaits this watchdog.
            if (request.ReserveBytes > 0)
            {
                var driveRoot = Path.GetPathRoot(rootDirectory);
                if (!string.IsNullOrWhiteSpace(driveRoot))
                {
                    var watchdogController = controller;
                    pendingStorageWatchdogTask = StorageWatchdog.RunAsync(
                        () => availableFreeSpace(driveRoot),
                        request.ReserveBytes,
                        TimeSpan.FromSeconds(10),
                        onReserveBreached: watchdogController.RequestStop,
                        onProbeFailed: exception =>
                        {
                            try
                            {
                                watchdogController.RequestStop();
                            }
                            catch
                            {
                                // Cancellation callbacks must not suppress the storage error report.
                            }

                            CaptureEventSource.Instance.StorageProbeFailed(
                                exception.GetType().Name,
                                exception.HResult);
                        },
                        cancellationToken: runCts.Token);
                }
            }

            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                sessionId = newSessionId;
                safetyGate = gate;
                gateHandler = pendingGateHandler;
                stopController = controller;
                runCancellation = runCts;
                highlightInbox = inbox;
                catalog = localCatalog;
                runTask = pendingRunTask;
                storageWatchdogTask = pendingStorageWatchdogTask;
                activity = EngineActivity.Recording;
                engineStateCommitted = true;
            }

            _ = ObserveRunCompletionAsync(pendingRunTask);

            PublishState();
        }
        catch
        {
            if (gate is not null && pendingGateHandler is not null)
            {
                gate.StateChanged -= pendingGateHandler;
            }

            if (!storeHandedOff)
            {
                // The coordinator never took ownership, so dispose the encoders we
                // built (and the store) here rather than leaking their MF resources.
                foreach (var encoder in pendingEncoders)
                {
                    await DisposeEncoderAsync(encoder).ConfigureAwait(false);
                }

                if (createdStore is not null)
                {
                    await createdStore.DisposeAsync().ConfigureAwait(false);
                }

                pendingController?.Dispose();
                pendingRunCancellation?.Dispose();
            }
            else
            {
                try
                {
                    pendingRunCancellation?.Cancel();
                }
                catch
                {
                    // Faulting cancellation callbacks must not skip coordinator cleanup.
                }

                if (pendingRunTask is not null)
                {
                    try
                    {
                        await pendingRunTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Startup failure is already being reported; wait until the coordinator releases its resources.
                    }
                }

                await CancelAndAwaitStorageWatchdogAsync(
                    pendingStorageWatchdogTask,
                    pendingRunCancellation).ConfigureAwait(false);

                if (gate is not null && gate.Snapshot().State != CaptureSafetyState.AwaitingStart)
                {
                    gate.Stop();
                }

                if (engineStateCommitted)
                {
                    await CleanupRunAsync().ConfigureAwait(false);
                }
                else
                {
                    pendingController?.Dispose();
                    pendingRunCancellation?.Dispose();
                }
            }

            if (!storeHandedOff && gate is not null && gate.Snapshot().State != CaptureSafetyState.AwaitingStart)
            {
                gate.Stop();
            }

            if (!engineStateCommitted && openedCatalog is not null)
            {
                await openedCatalog.DisposeAsync().ConfigureAwait(false);
            }

            bool releaseConsent;
            lock (sync)
            {
                activity = EngineActivity.Idle;
                sessionId = null;
                releaseConsent = !engineStateCommitted && consentReleasePending;
                if (releaseConsent)
                {
                    consentReleasePending = false;
                }
            }

            if (releaseConsent)
            {
                await SafeReleaseConsentAsync().ConfigureAwait(false);
            }

            PublishState();
            throw;
        }
        finally
        {
            lock (sync)
            {
                startInProgress = false;
                if (ReferenceEquals(startupCancellation, startupCts))
                {
                    startupCancellation = null;
                }
            }

            try
            {
                startupCts.Dispose();
            }
            finally
            {
                completion.TrySetResult(true);
                lock (sync)
                {
                    if (ReferenceEquals(startupCompletion, completion))
                    {
                        startupCompletion = null;
                    }
                }
            }
        }
    }

    public async Task<CaptureRunResult> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<CaptureRunResult> task;
        CaptureStopController? controller;
        CancellationTokenSource? runCts;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runTask is null)
            {
                throw new InvalidOperationException("No capture session is active.");
            }

            task = runTask;
            controller = stopController;
            runCts = runCancellation;
        }

        controller?.RequestStop();
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Cleanup must never race a still-running coordinator: if the caller's
            // wait was cancelled before the run finished, hard-cancel it and await
            // its terminal state before releasing the shared store/catalog/consent.
            if (!task.IsCompleted)
            {
                runCts?.Cancel();
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // A run aborted by disposal marks itself interrupted for recovery.
                }
            }

            await CleanupRunAsync().ConfigureAwait(false);
        }
    }

    public Task<bool> HighlightAsync(HighlightRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        HighlightInbox? inbox;
        CaptureSafetyGate? gate;
        CaptureStopController? controller;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            inbox = highlightInbox;
            gate = safetyGate;
            controller = stopController;
        }

        if (inbox is null || gate is null)
        {
            return Task.FromResult(false);
        }

        var highlight = new HighlightEvent(
            id: "hl-" + Guid.NewGuid().ToString("N"),
            eventTicks: clock.NowTicks,
            source: request.Source,
            holdTicks: DurationToTicksAllowZero(request.Hold),
            included: request.Included);
        // TryEnqueue only accepts while the gate is Recording, so this naturally
        // returns false when idle, paused, or stopping.
        return Task.FromResult(inbox.TryEnqueue(highlight, gate, controller));
    }

    public Task<bool> PauseAsync(
        CapturePauseReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reason == CapturePauseReason.UserRequested)
        {
            return Task.FromResult(false);
        }

        CaptureSafetyGate? gate;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            gate = safetyGate;
        }

        if (gate is null)
        {
            return Task.FromResult(false);
        }

        var snapshot = gate.Snapshot();
        var canPause = snapshot.State == CaptureSafetyState.Recording
            || (reason != CapturePauseReason.UserRequested
                && snapshot.State == CaptureSafetyState.Paused);
        if (!canPause)
        {
            return Task.FromResult(false);
        }

        gate.Pause(reason, clock.NowTicks);
        return Task.FromResult(true);
    }

    public Task<bool> ResumeAsync(CancellationToken cancellationToken = default)
    {
        CaptureSafetyGate? gate;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            gate = safetyGate;
        }

        // The active session keeps its overt indicator up across a safety pause, so
        // resume re-affirms it rather than re-running consent.
        return Task.FromResult(gate is not null && gate.TryResume(indicatorsVerified: true));
    }

    public async Task<EngineExportResult> ExportAsync(
        string sessionId,
        EngineExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activity != EngineActivity.Idle || startInProgress)
            {
                throw new InvalidOperationException("Cannot export while a capture session is active.");
            }

            activity = EngineActivity.Exporting;
        }

        PublishState();
        try
        {
            await using var exportCatalog = await CatalogStore
                .OpenAsync(LogPaths.CatalogPath(rootDirectory), cancellationToken)
                .ConfigureAwait(false);
            var service = new SessionExportService(exportCatalog, rootDirectory);
            var result = await service.ExportAsync(
                sessionId,
                new SessionExportRequest(
                    request.RenderSettings,
                    request.OutputDirectory,
                    request.FileName,
                    request.EstimatedOutputBytes,
                    request.ReserveBytes),
                renderer.RenderAsync,
                cancellationToken).ConfigureAwait(false);

            var completed = string.Equals(result.Job.Status, "completed", StringComparison.Ordinal);
            return new EngineExportResult(sessionId, result.OutputPath, completed);
        }
        finally
        {
            lock (sync)
            {
                activity = EngineActivity.Idle;
            }

            PublishState();
        }
    }

    public async Task<EngineExportPreview> RenderPreviewAsync(
        string sessionId,
        EngineExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        BeginExportActivity();
        try
        {
            await using var exportCatalog = await CatalogStore
                .OpenAsync(LogPaths.CatalogPath(rootDirectory), cancellationToken)
                .ConfigureAwait(false);
            var service = new SessionExportService(exportCatalog, rootDirectory);
            var result = await service.RenderPreviewAsync(
                sessionId,
                new SessionExportRequest(
                    request.RenderSettings,
                    request.OutputDirectory,
                    request.FileName,
                    request.EstimatedOutputBytes,
                    request.ReserveBytes),
                renderer.RenderAsync,
                cancellationToken).ConfigureAwait(false);
            return new EngineExportPreview(
                sessionId,
                result.Job.Id,
                result.PreviewPath,
                result.SuggestedOutputPath,
                string.Equals(result.Job.Status, "completed", StringComparison.Ordinal),
                result.Job.FinishedAt);
        }
        finally
        {
            EndExportActivity();
        }
    }

    public async Task<IReadOnlyList<EngineExportPreview>> ListPendingExportPreviewsAsync(
        CancellationToken cancellationToken = default)
    {
        BeginExportActivity();
        try
        {
            await using var library = await SessionLibrary
                .OpenAsync(rootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var previews = await library
                .ListPendingExportPreviewsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return previews
                .Select(static preview => new EngineExportPreview(
                    preview.Job.SessionId,
                    preview.Job.Id,
                    preview.PreviewPath,
                    preview.SuggestedOutputPath,
                    Completed: true,
                    preview.Job.FinishedAt))
                .ToArray();
        }
        finally
        {
            EndExportActivity();
        }
    }

    public async Task<EngineExportResult> SavePreviewAsync(
        string jobId,
        string outputDirectory,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        BeginExportActivity();
        try
        {
            await using var library = await SessionLibrary
                .OpenAsync(rootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var result = await library.Export.SavePreviewAsync(
                jobId,
                outputDirectory,
                fileName,
                cancellationToken).ConfigureAwait(false);
            return new EngineExportResult(
                result.Job.SessionId,
                result.OutputPath,
                Completed: true);
        }
        finally
        {
            EndExportActivity();
        }
    }

    public async Task<IReadOnlyList<EngineUnfinishedSave>> ListUnfinishedAsync(
        CancellationToken cancellationToken = default)
    {
        BeginExportActivity();
        try
        {
            // Opening the library runs the recovery scan: a killed capture becomes "interrupted".
            await using var library = await SessionLibrary
                .OpenAsync(rootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var found = new List<(DateTimeOffset When, EngineUnfinishedSave Save)>();
            foreach (var session in await library.ListSessionsAsync(false, cancellationToken).ConfigureAwait(false))
            {
                // Only the screen recorder exists now; other modes, and sessions with no frames, have nothing to show.
                if (session.Status is not ("ready" or "interrupted") || session.SourceMode != "screen")
                {
                    continue;
                }

                try
                {
                    var summary = await SessionSummaryReader.ReadAsync(
                        session.DirectoryPath,
                        SessionSummaryReadOptions.MetadataOnly,
                        cancellationToken).ConfigureAwait(false);
                    if (summary.SlotCount == 0)
                    {
                        continue;
                    }

                    var elapsed = TimeSpan.FromTicks(summary.ActiveDurationTicks);
                    var interrupted = session.Status == "interrupted";
                    var previews = await library
                        .ListPendingExportPreviewsAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false);
                    var kept = previews
                        .Where(static preview => preview.PreviewPath is not null)
                        .OrderByDescending(static preview => preview.Job.FinishedAt)
                        .FirstOrDefault();
                    var (width, height) = kept is null
                        ? await ReadFrameSizeAsync(session, library, cancellationToken).ConfigureAwait(false)
                        : (1920, 1080);
                    found.Add((
                        summary.StartedAtUtc ?? DateTimeOffset.MinValue,
                        new EngineUnfinishedSave(
                            session.Id,
                            summary.Name,
                            elapsed,
                            interrupted,
                            width,
                            height,
                            kept?.Job.Id,
                            kept?.PreviewPath,
                            kept?.SuggestedOutputPath)));
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or System.Text.Json.JsonException)
                {
                    // One unreadable recording must not hide the others.
                }
            }

            return found.OrderByDescending(static item => item.When).Select(static item => item.Save).ToArray();
        }
        finally
        {
            EndExportActivity();
        }
    }

    // The first stored frame is a PNG; its header says how big the screen was.
    private static async Task<(int Width, int Height)> ReadFrameSizeAsync(
        CatalogSession session,
        SessionLibrary library,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = await library.GetSessionRenderInputAsync(session.Id, cancellationToken).ConfigureAwait(false);
            var reference = input.Slots
                .Select(static slot => slot.ScreenAssetReference)
                .FirstOrDefault(static reference => !string.IsNullOrWhiteSpace(reference));
            var directory = Path.GetFullPath(session.DirectoryPath);
            if (reference is not null)
            {
                var path = Path.GetFullPath(Path.Combine(directory, reference));
                if (path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path))
                {
                    var header = new byte[24];
                    await using var stream = File.OpenRead(path);
                    if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) == header.Length
                        && header.AsSpan(1, 3).SequenceEqual("PNG"u8))
                    {
                        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16));
                        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20));
                        if (width > 0 && height > 0)
                        {
                            return (width, height);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or KeyNotFoundException)
        {
            // Fall through to a common size; the render scales the frames to it.
        }

        return (1920, 1080);
    }

    public async Task DiscardUnfinishedAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        BeginExportActivity();
        try
        {
            await using var library = await SessionLibrary
                .OpenAsync(rootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var session = await library.Catalog
                .GetSessionAsync(sessionId, includeDeleting: false, cancellationToken)
                .ConfigureAwait(false);
            if (session is null || session.Status is not ("ready" or "interrupted"))
            {
                throw new InvalidOperationException("This recording is no longer waiting to be saved.");
            }

            // The kept preview lives in the output folder, outside the session folder.
            foreach (var preview in await library.ListPendingExportPreviewsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                if (preview.PreviewPath is { } path)
                {
                    try { File.Delete(path); }
                    catch (IOException) { /* a leftover hidden file is better than a failed discard */ }
                    catch (UnauthorizedAccessException) { }
                }
            }

            await library.Deletion.SoftDeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await library.Deletion.PurgeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndExportActivity();
        }
    }

    private void BeginExportActivity()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activity != EngineActivity.Idle || startInProgress)
            {
                throw new InvalidOperationException("Cannot access the export library while a capture session is active.");
            }

            activity = EngineActivity.Exporting;
        }

        PublishState();
    }

    private void EndExportActivity()
    {
        lock (sync)
        {
            activity = EngineActivity.Idle;
        }

        PublishState();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? startupCts = null;
        Task? startupTask = null;
        CancellationTokenSource? runCts;
        CaptureStopController? controller;
        Task<CaptureRunResult>? task;
        TaskCompletionSource<bool>? ownerCompletion = null;
        Task? existingDisposeTask = null;
        lock (sync)
        {
            if (disposed)
            {
                existingDisposeTask = disposeCompletion?.Task;
            }
            else
            {
                disposed = true;
                ownerCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                disposeCompletion = ownerCompletion;
                startupCts = startupCancellation;
                startupTask = startupCompletion?.Task;
            }
        }

        if (ownerCompletion is null)
        {
            if (existingDisposeTask is not null)
            {
                await existingDisposeTask.ConfigureAwait(false);
            }
            return;
        }

        try
        {
            try
            {
                startupCts?.Cancel();
            }
            catch
            {
                // Faulting cancellation callbacks must not skip startup cleanup.
            }

            if (startupTask is not null)
            {
                await startupTask.ConfigureAwait(false);
            }

            lock (sync)
            {
                runCts = runCancellation;
                controller = stopController;
                task = runTask;
            }

            try
            {
                controller?.RequestStop();
            }
            catch
            {
                // Faulting stop callbacks must not skip task and resource cleanup.
            }
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // A capture interrupted by disposal marks itself interrupted for recovery.
                }
            }

            try
            {
                runCts?.Cancel();
            }
            catch
            {
                // Faulting cancellation callbacks must not skip ownership cleanup.
            }

            await CleanupRunAsync().ConfigureAwait(false);
            ownerCompletion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            ownerCompletion.TrySetException(exception);
            throw;
        }
    }

    private long DurationToTicks(TimeSpan duration)
    {
        var ticks = checked((long)(duration.TotalSeconds * clockTicksPerSecond));
        return ticks <= 0 ? 1 : ticks;
    }

    private long DurationToTicksAllowZero(TimeSpan duration)
    {
        var ticks = checked((long)(duration.TotalSeconds * clockTicksPerSecond));
        return ticks < 0 ? 0 : ticks;
    }

    private static async ValueTask DisposeEncoderAsync(IEncoder encoder)
    {
        try
        {
            switch (encoder)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch
        {
            // Best-effort cleanup on the failure path; never mask the original fault.
        }
    }

    private void OnGateStateChanged(CaptureSafetySnapshot snapshot)
    {
        PublishState();
    }

    private async Task CleanupRunAsync()
    {
        await cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupRunCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            cleanupGate.Release();
        }
    }

    private async Task CleanupRunCoreAsync()
    {
        CaptureSafetyGate? gate;
        Action<CaptureSafetySnapshot>? handler;
        CaptureStopController? controller;
        CancellationTokenSource? runCts;
        Task? watchdogTask;
        CatalogStore? openedCatalog;
        bool releaseConsent;
        lock (sync)
        {
            if (activity == EngineActivity.Idle
                && runTask is null
                && safetyGate is null
                && stopController is null
                && runCancellation is null
                && storageWatchdogTask is null
                && catalog is null
                && !consentReleasePending)
            {
                return;
            }

            gate = safetyGate;
            handler = gateHandler;
            controller = stopController;
            runCts = runCancellation;
            watchdogTask = storageWatchdogTask;
            openedCatalog = catalog;
            releaseConsent = consentReleasePending;
            consentReleasePending = false;

            safetyGate = null;
            gateHandler = null;
            stopController = null;
            runCancellation = null;
            storageWatchdogTask = null;
            highlightInbox = null;
            catalog = null;
            runTask = null;
            sessionId = null;
            activity = EngineActivity.Stopping;
        }

        if (gate is not null && handler is not null)
        {
            gate.StateChanged -= handler;
        }

        await CancelAndAwaitStorageWatchdogAsync(watchdogTask, runCts).ConfigureAwait(false);
        controller?.Dispose();
        runCts?.Dispose();
        if (openedCatalog is not null)
        {
            await openedCatalog.DisposeAsync().ConfigureAwait(false);
        }

        if (releaseConsent)
        {
            await SafeReleaseConsentAsync().ConfigureAwait(false);
        }

        lock (sync)
        {
            activity = EngineActivity.Idle;
        }
        PublishState();
    }

    private static async Task CancelAndAwaitStorageWatchdogAsync(
        Task? watchdogTask,
        CancellationTokenSource? runCancellation)
    {
        try
        {
            runCancellation?.Cancel();
        }
        catch
        {
            // Faulting cancellation callbacks must not leave the watchdog running.
        }

        if (watchdogTask is null)
        {
            return;
        }

        try
        {
            await watchdogTask.ConfigureAwait(false);
        }
        catch
        {
            // Watchdog completion must not block capture ownership cleanup.
        }
    }

    private async Task ObserveRunCompletionAsync(Task<CaptureRunResult> completedTask)
    {
        Exception? failure = null;
        CaptureRunResult? result = null;
        try
        {
            result = await completedTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (Volatile.Read(ref disposed))
        {
            return;
        }

        lock (sync)
        {
            if (!ReferenceEquals(runTask, completedTask))
            {
                return;
            }
        }

        if (failure is null)
        {
            PublishCaptureCompleted(result!);
            await CleanupRunAsync().ConfigureAwait(false);
            return;
        }

        // A source or capture writer can fail without a StopAsync caller. Finish
        // ownership cleanup here so the shell cannot remain in Stopping forever
        // and a later capture cannot overlap the failed source/store.
        await CleanupRunAsync().ConfigureAwait(false);
        if (Volatile.Read(ref disposed))
        {
            return;
        }

        var handlers = CaptureFailed;
        if (handlers is not null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Action<Exception>>())
            {
                try
                {
                    handler(failure);
                }
                catch
                {
                    CaptureEventSource.Instance.StateObserverFailed();
                }
            }
        }
    }

    private void PublishCaptureCompleted(CaptureRunResult result)
    {
        var handlers = CaptureCompleted;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<CaptureRunResult>>())
        {
            try
            {
                handler(result);
            }
            catch
            {
                CaptureEventSource.Instance.StateObserverFailed();
            }
        }
    }

    private async Task SafeReleaseConsentAsync()
    {
        try
        {
            await consentBroker.ReleaseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Consent teardown is best-effort; never mask the capture outcome.
        }
    }

    private void PublishState()
    {
        EngineStateSnapshot snapshot;
        lock (sync)
        {
            snapshot = CreateSnapshot();
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<EngineStateSnapshot>>())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // Observers must not interrupt capture ownership or block other observers.
                CaptureEventSource.Instance.StateObserverFailed();
            }
        }
    }

    private EngineStateSnapshot CreateSnapshot()
    {
        var gateSnapshot = safetyGate?.Snapshot();
        var currentActivity = gateSnapshot is null
            ? activity
            : gateSnapshot.State switch
            {
                CaptureSafetyState.Recording => EngineActivity.Recording,
                CaptureSafetyState.Paused => EngineActivity.Paused,
                CaptureSafetyState.Stopping => EngineActivity.Stopping,
                _ => activity
            };

        return new EngineStateSnapshot(
            currentActivity,
            gateSnapshot?.State,
            gateSnapshot?.PauseReason,
            sessionId);
    }
}
