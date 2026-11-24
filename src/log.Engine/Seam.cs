using Log.Core.Capture;
using Log.Core.Store;

namespace Log.Engine;

// ---------------------------------------------------------------------------
// Shell <-> engine seam. These three interfaces are the stable contract that
// src/log.Shell (the Native AOT Win32 shell) binds to. The engine implements
// IAppEngine; the shell implements IConsentBroker. Platform capture concretes
// (WGC source, MF encoder) are injected via the factory interfaces below rather
// than referenced by the engine directly, so the engine stays dependent only
// on log.Core.
// ---------------------------------------------------------------------------

/// <summary>
/// The engine facade the shell drives: start a recording, stop it, export a
/// session, and observe state for the overt recording indicator.
/// </summary>
public interface IAppEngine : IAsyncDisposable
{
    /// <summary>Current engine state.</summary>
    EngineStateSnapshot State { get; }

    /// <summary>Raised on every state transition (start, pause, resume, stop, export).</summary>
    event Action<EngineStateSnapshot>? StateChanged;

    /// <summary>Raised when a capture task terminates unexpectedly without a user stop.</summary>
    event Action<Exception>? CaptureFailed;

    /// <summary>Raised after a capture is finalized so the shell can continue to review and save it.</summary>
    event Action<CaptureRunResult>? CaptureCompleted;

    /// <summary>Begin a recording. Returns once capture is running; ends via <see cref="StopAsync"/>.</summary>
    Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cooperatively stop the active recording, finalize the session, and return its result.</summary>
    Task<CaptureRunResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Record a first-class highlight for the active recording (hotkey). Returns
    /// true if accepted; false when nothing is recording, capture is paused or
    /// stopping, or the highlight buffer is full.
    /// </summary>
    Task<bool> HighlightAsync(HighlightRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// System-transition pause (lock, secure desktop, suspend, or source
    /// uncertainty). The reason is persisted in the capture timeline; callers
    /// must still use <see cref="ResumeAsync"/> explicitly after verification.
    /// </summary>
    Task<bool> PauseAsync(
        CapturePauseReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a safety-paused recording. Returns true if it resumed; false when the
    /// session is not paused.
    /// </summary>
    Task<bool> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Render a recorded session to a durable preview while keeping its source session.</summary>
    Task<EngineExportPreview> RenderPreviewAsync(
        string sessionId,
        EngineExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>List completed, verified previews that have not been saved or dismissed.</summary>
    Task<IReadOnlyList<EngineExportPreview>> ListPendingExportPreviewsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Publish a previously rendered preview to the requested local output directory.</summary>
    Task<EngineExportResult> SavePreviewAsync(
        string jobId,
        string outputDirectory,
        string? fileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recordings that were never saved, newest first. Opening the library runs the recovery scan, so a
    /// capture killed mid-recording is found as interrupted. Refused while a capture is active.
    /// </summary>
    Task<IReadOnlyList<EngineUnfinishedSave>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete an unfinished recording and any preview kept for it. Refuses a recording that is no longer unfinished.</summary>
    Task DiscardUnfinishedAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Render a recorded session directly to a playable MP4 for compatibility callers.</summary>
    Task<EngineExportResult> ExportAsync(
        string sessionId,
        EngineExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shell-implemented. Obtains user consent for a recording and verifies the
/// persistent overt-recording indicator before any frame is captured
/// (overt-only invariant). Returns the granted frame source.
/// </summary>
public interface IConsentBroker
{
    Task<CaptureConsent> RequestCaptureConsentAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Called when capture ends so the shell can tear down its consent surface.</summary>
    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-implemented. Creates the capture-time encoder for a track. The engine
/// creates the session's <see cref="Log.Core.Store.SessionStore"/> first and passes
/// it in <see cref="EncoderContext"/>, so a segmented encoder can append crash-safe
/// rolling finalized segments (S8) to the same live store the coordinator writes.
/// </summary>
public interface IEncoderFactory
{
    Task<IEncoder> CreateAsync(EncoderContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Render-lane / platform-implemented. Composes a recorded session's stored
/// frame assets into the staging MP4 at <see cref="SessionExportContext.OutputPath"/>.
/// The export service moves it to <see cref="SessionExportContext.FinalOutputPath"/>
/// only after rendering succeeds and cancellation has been checked.
/// </summary>
public interface ISessionRenderer
{
    Task RenderAsync(SessionExportContext context, CancellationToken cancellationToken = default);
}
