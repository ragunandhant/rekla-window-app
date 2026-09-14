using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Services;

/// <summary>
/// One unit of work for one entry. Everything it needs is captured when it is
/// created, including the race and category, so nothing the operator does in the
/// UI afterwards — switching entry, category or race — can change what it acts on.
/// </summary>
public sealed record WorkflowJob(
    RaceScope Scope,
    RaceEntry Entry,
    string? InputPath,
    string OutputPath,
    OverlayData Overlay,
    string OverlayHash,
    bool AllowOverwrite,
    bool UploadEnabled,
    bool ProcessingEnabled = true)
{
    /// <summary>The stages this job runs, fixed when it was created.</summary>
    public WorkflowMode Mode => WorkflowModes.From(ProcessingEnabled, UploadEnabled);
}

public enum WorkflowStage
{
    Processing,
    Uploading,
    Assigning
}

public sealed record WorkflowProgress(
    WorkflowStage Stage,
    double? Percent,
    ProcessingProgress? Processing,
    EntryLocalState State,
    UploadProgress? Upload = null);

public enum WorkflowOutcome
{
    /// <summary>Uploaded and assigned — after processing, or directly with processing OFF.</summary>
    Completed,

    /// <summary>Processed; upload is OFF, so nothing was uploaded or assigned. Not an error.</summary>
    UploadDisabled,

    /// <summary>Processing and upload both OFF: the video is only selected. Not an error.</summary>
    SelectionOnly,

    ProcessingFailed,
    ProcessingCancelled,
    UploadFailed,
    AssignmentFailed,
    AuthenticationFailed,

    /// <summary>The application is closing. Stored state says where to resume.</summary>
    Interrupted
}

public sealed record WorkflowResult(WorkflowOutcome Outcome, EntryLocalState State, string? Error);

/// <summary>
/// Runs one entry's operation in the job's <see cref="WorkflowMode"/>, persisting
/// each stage as it starts and finishes and never repeating a completed stage:
///
///   ProcessAndUpload  process → upload the processed file → assign
///   ProcessOnly       process, keep the output, stop
///   DirectUpload      no FFmpeg at all → upload the selected file → assign
///   SelectionOnly     nothing runs; the selection is recorded
///
///   * processing is skipped when a validated output is still on disk, or the
///     upload already succeeded;
///   * upload is skipped when a link was already obtained, so a failed PATCH is
///     retried without uploading again;
///   * a disabled stage is recorded as disabled or skipped, never as failed.
///
/// Cancelling during processing marks the entry cancelled and stops before
/// upload. Stage failures are returned, never thrown.
/// </summary>
public sealed class EntryWorkflowService
{
    private readonly IVideoProcessingService _video;
    private readonly IMediaPublisher _publisher;
    private readonly ILocalStateRepository _repository;
    private readonly IAppLog _log;

    public EntryWorkflowService(
        IVideoProcessingService video,
        IMediaPublisher publisher,
        ILocalStateRepository repository,
        IAppLog log)
    {
        _video = video;
        _publisher = publisher;
        _repository = repository;
        _log = log;
    }

    public async Task<WorkflowResult> RunAsync(
        WorkflowJob job,
        EntryLocalState startState,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(startState.RaceId, job.Scope.RaceId, StringComparison.Ordinal) ||
            startState.Category != job.Scope.Category)
            throw new InvalidOperationException("The entry state does not belong to the job's race and category.");

        var state = startState.Clone();
        var card = job.Entry.CardNumber;
        var context = $"[{job.Scope}] {card}";

        // ---- Processing ----------------------------------------------------

        var uploadAlreadyDone = state.UploadStatus == UploadStatus.Completed && !string.IsNullOrWhiteSpace(state.UploadedVideoLink);
        state.Mode = job.Mode;

        if (job.ProcessingEnabled)
        {
            var haveOutput = state.ProcessingStatus == LocalProcessingStatus.Completed &&
                             !string.IsNullOrWhiteSpace(state.OutputPath) && File.Exists(state.OutputPath);

            if (!uploadAlreadyDone && !haveOutput)
            {
                var processed = await ProcessAsync(job, state, context, progress, cancellationToken).ConfigureAwait(false);
                if (processed is not null)
                    return processed;
            }
        }
        else if (!uploadAlreadyDone)
        {
            var skipped = await SkipProcessingAsync(job, state, context).ConfigureAwait(false);
            if (skipped is not null)
                return skipped;
        }

        // ---- Upload --------------------------------------------------------

        if (!job.UploadEnabled)
        {
            if (state.UploadStatus != UploadStatus.Completed)
            {
                state.UploadStatus = UploadStatus.Disabled;
                state.AssignmentStatus = AssignmentStatus.NotStarted;
                state.ErrorMessage = null;
                await SaveAsync(state).ConfigureAwait(false);
            }

            if (job.ProcessingEnabled)
            {
                _log.Info($"{context}: upload is OFF — processed only, nothing uploaded or assigned.");
                Report(progress, WorkflowStage.Processing, 100, null, state);
                return new WorkflowResult(WorkflowOutcome.UploadDisabled, state.Clone(), null);
            }

            _log.Info($"{context}: processing and upload are OFF — video selected only; nothing processed, uploaded or assigned.");
            return new WorkflowResult(WorkflowOutcome.SelectionOnly, state.Clone(), null);
        }

        if (!uploadAlreadyDone)
        {
            var uploaded = await UploadAsync(state, context, progress, cancellationToken).ConfigureAwait(false);
            if (uploaded is not null)
                return uploaded;
        }
        else
        {
            _log.Info($"{context}: upload already completed; reusing the existing video link.");
        }

        // ---- Assignment ----------------------------------------------------

        if (state.AssignmentStatus == AssignmentStatus.Completed)
            return new WorkflowResult(WorkflowOutcome.Completed, state.Clone(), null);

        return await AssignAsync(job, state, context, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowResult?> ProcessAsync(
        WorkflowJob job, EntryLocalState state, string context,
        IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.InputPath) || !File.Exists(job.InputPath))
        {
            state.ProcessingStatus = LocalProcessingStatus.VideoNotFound;
            state.ErrorMessage = "Processing Failed: the selected video file was not found.";
            await SaveAsync(state).ConfigureAwait(false);
            return new WorkflowResult(WorkflowOutcome.ProcessingFailed, state.Clone(), state.ErrorMessage);
        }

        state.LocalVideoPath = job.InputPath;
        state.ProcessingStatus = LocalProcessingStatus.Processing;
        state.OutputPath = job.OutputPath;
        state.UploadStatus = UploadStatus.NotStarted;
        state.UploadedVideoLink = null;
        state.AssignmentStatus = AssignmentStatus.NotStarted;
        state.LastFailureWasAuthentication = false;
        state.ErrorMessage = null;
        await SaveAsync(state).ConfigureAwait(false);
        Report(progress, WorkflowStage.Processing, 0, null, state);
        _log.Info($"{context}: processing started ({Path.GetFileName(job.InputPath)}).");

        var processingProgress = new InlineProgress<ProcessingProgress>(p =>
            Report(progress, WorkflowStage.Processing, p.Percent, p, state));

        var request = new ProcessingRequest(job.Entry.EntryId, job.Entry.CardNumber, job.InputPath,
            job.OutputPath, job.Overlay, job.AllowOverwrite);

        ProcessingResult result;
        try
        {
            result = await _video.ProcessAsync(request, processingProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The processing service removes its incomplete temporary output.
            state.ProcessingStatus = LocalProcessingStatus.Cancelled;
            state.ErrorMessage = "Processing Cancelled.";
            await SaveAsync(state).ConfigureAwait(false);
            _log.Info($"{context}: processing cancelled.");
            return new WorkflowResult(WorkflowOutcome.ProcessingCancelled, state.Clone(), state.ErrorMessage);
        }

        if (!result.Success)
        {
            state.ProcessingStatus = LocalProcessingStatus.Failed;
            state.ErrorMessage = "Processing Failed: " + (result.Error ?? "unknown error.");
            await SaveAsync(state).ConfigureAwait(false);
            _log.Error($"{context}: processing failed: {result.Error}");
            return new WorkflowResult(WorkflowOutcome.ProcessingFailed, state.Clone(), state.ErrorMessage);
        }

        state.ProcessingStatus = LocalProcessingStatus.Completed;
        state.OutputPath = result.OutputPath;
        state.LastProcessedDataHash = job.OverlayHash;
        state.ProcessedUtc = DateTimeOffset.UtcNow;
        await SaveAsync(state).ConfigureAwait(false);
        Report(progress, WorkflowStage.Processing, 100, null, state);
        _log.Info($"{context}: processing completed → {result.OutputPath}");
        return null;
    }

    /// <summary>
    /// Processing OFF: the selected file is used exactly as it is. FFmpeg is not
    /// run and no copy is made; the file must simply still be there.
    /// </summary>
    private async Task<WorkflowResult?> SkipProcessingAsync(WorkflowJob job, EntryLocalState state, string context)
    {
        var input = FirstNonEmpty(job.InputPath, state.LocalVideoPath);
        state.LocalVideoPath = input;
        state.OutputPath = null;
        state.LastFailureWasAuthentication = false;

        if (input is null || !File.Exists(input))
        {
            state.ProcessingStatus = LocalProcessingStatus.Skipped;
            if (job.UploadEnabled)
            {
                state.UploadStatus = UploadStatus.Failed;
                state.ErrorMessage = "Upload Failed: the selected video file was not found. Select the video again.";
                await SaveAsync(state).ConfigureAwait(false);
                _log.Error($"{context}: {state.ErrorMessage}");
                return new WorkflowResult(WorkflowOutcome.UploadFailed, state.Clone(), state.ErrorMessage);
            }
        }

        if (state.ProcessingStatus != LocalProcessingStatus.Skipped)
        {
            state.ProcessingStatus = LocalProcessingStatus.Skipped;
            state.UploadStatus = UploadStatus.NotStarted;
            state.UploadedVideoLink = null;
            state.AssignmentStatus = AssignmentStatus.NotStarted;
        }
        state.ErrorMessage = null;
        await SaveAsync(state).ConfigureAwait(false);
        _log.Info(job.UploadEnabled
            ? $"{context}: processing disabled — direct upload mode; FFmpeg is not run ({Path.GetFileName(input)})."
            : $"{context}: processing disabled; FFmpeg is not run ({Path.GetFileName(input)}).");
        return null;
    }

    private async Task<WorkflowResult?> UploadAsync(
        EntryLocalState state, string context, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        var source = state.UploadSourcePath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            state.UploadStatus = UploadStatus.Failed;
            state.ErrorMessage = state.IsDirect
                ? "Upload Failed: the selected video is no longer on disk. Select the video again."
                : "Upload Failed: the processed video is no longer on disk. Select the video again to reprocess.";
            await SaveAsync(state).ConfigureAwait(false);
            return new WorkflowResult(WorkflowOutcome.UploadFailed, state.Clone(), state.ErrorMessage);
        }

        state.UploadStatus = UploadStatus.Uploading;
        state.AssignmentStatus = AssignmentStatus.NotStarted;
        state.LastFailureWasAuthentication = false;
        state.ErrorMessage = null;
        await SaveAsync(state).ConfigureAwait(false);
        Report(progress, WorkflowStage.Uploading, 0, null, state);
        _log.Info(state.IsDirect
            ? $"{context}: direct upload started via {_publisher.Name} — the selected video as it is ({Path.GetFileName(source)})."
            : $"{context}: upload started via {_publisher.Name} ({Path.GetFileName(source)}).");

        var uploadProgress = new InlineProgress<UploadProgress>(p =>
            progress?.Report(new WorkflowProgress(WorkflowStage.Uploading, p.Percent, null, state.Clone(), p)));

        try
        {
            var link = await _publisher.UploadAsync(source, uploadProgress, cancellationToken).ConfigureAwait(false);
            state.UploadStatus = UploadStatus.Completed;
            state.UploadedVideoLink = link;
            await SaveAsync(state).ConfigureAwait(false);
            _log.Info($"{context}: upload completed → {link}");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.UploadStatus = UploadStatus.Failed;
            state.ErrorMessage = "Upload Failed: the upload was interrupted.";
            await SaveAsync(state).ConfigureAwait(false);
            return new WorkflowResult(WorkflowOutcome.Interrupted, state.Clone(), state.ErrorMessage);
        }
        catch (Exception ex)
        {
            var auth = ex is BackendException { Kind: BackendFailureKind.Authentication };
            state.UploadStatus = UploadStatus.Failed;
            state.LastFailureWasAuthentication = auth;
            state.ErrorMessage = (auth ? "Authentication Failed: " : "Upload Failed: ") + ex.Message;
            await SaveAsync(state).ConfigureAwait(false);
            _log.Error($"{context}: {state.ErrorMessage}");
            return new WorkflowResult(auth ? WorkflowOutcome.AuthenticationFailed : WorkflowOutcome.UploadFailed,
                state.Clone(), state.ErrorMessage);
        }
    }

    private async Task<WorkflowResult> AssignAsync(
        WorkflowJob job, EntryLocalState state, string context,
        IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        var playerId = FirstNonEmpty(job.Entry.PlayerId, state.PlayerId);
        var marker = FirstNonEmpty(job.Entry.Marker, state.Marker);
        if (playerId is null || marker is null)
        {
            state.AssignmentStatus = AssignmentStatus.Failed;
            state.ErrorMessage = "Assignment Failed: the entry has no " + (playerId is null ? "playerId." : "marker.");
            await SaveAsync(state).ConfigureAwait(false);
            _log.Error($"{context}: {state.ErrorMessage}");
            return new WorkflowResult(WorkflowOutcome.AssignmentFailed, state.Clone(), state.ErrorMessage);
        }

        state.AssignmentStatus = AssignmentStatus.Assigning;
        state.LastFailureWasAuthentication = false;
        state.ErrorMessage = null;
        await SaveAsync(state).ConfigureAwait(false);
        Report(progress, WorkflowStage.Assigning, null, null, state);
        _log.Info($"{context}: assignment started.");

        try
        {
            await _publisher.AssignAsync(
                new AssignmentRequest(job.Scope, playerId, marker, state.UploadedVideoLink!), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.AssignmentStatus = AssignmentStatus.Failed;
            state.ErrorMessage = "Assignment Failed: the assignment was interrupted.";
            await SaveAsync(state).ConfigureAwait(false);
            return new WorkflowResult(WorkflowOutcome.Interrupted, state.Clone(), state.ErrorMessage);
        }
        catch (Exception ex)
        {
            // The uploaded link stays: a retry repeats only the PATCH.
            var auth = ex is BackendException { Kind: BackendFailureKind.Authentication };
            state.AssignmentStatus = AssignmentStatus.Failed;
            state.LastFailureWasAuthentication = auth;
            state.ErrorMessage = (auth ? "Authentication Failed: " : "Assignment Failed: ") + ex.Message;
            await SaveAsync(state).ConfigureAwait(false);
            _log.Error($"{context}: {state.ErrorMessage}");
            return new WorkflowResult(auth ? WorkflowOutcome.AuthenticationFailed : WorkflowOutcome.AssignmentFailed,
                state.Clone(), state.ErrorMessage);
        }

        state.AssignmentStatus = AssignmentStatus.Completed;
        state.RemoteVideoLink = state.UploadedVideoLink;
        await SaveAsync(state).ConfigureAwait(false);
        Report(progress, WorkflowStage.Assigning, 100, null, state);
        _log.Info($"{context}: assignment completed.");
        return new WorkflowResult(WorkflowOutcome.Completed, state.Clone(), null);
    }

    /// <summary>
    /// Stage transitions are always written, even while cancelling: the stored
    /// state must never claim a stage is still running once it has stopped.
    /// </summary>
    private Task SaveAsync(EntryLocalState state)
    {
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        return _repository.UpsertEntryStateAsync(state.Clone(), CancellationToken.None);
    }

    private static void Report(IProgress<WorkflowProgress>? progress, WorkflowStage stage, double? percent,
        ProcessingProgress? processing, EntryLocalState state)
        => progress?.Report(new WorkflowProgress(stage, percent, processing, state.Clone()));

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>Forwards synchronously; the caller's IProgress decides which thread it lands on.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>Brings stored state back to something truthful after the application stopped mid-job.</summary>
public static class WorkflowRecovery
{
    /// <summary>
    /// Nothing is running at startup, so a stage stored as running was interrupted.
    /// Processing cannot resume mid-file and is marked failed. An interrupted upload
    /// or assignment goes back to not started so it can resume from the processed
    /// file or the existing video link. Returns false when nothing needed changing.
    /// </summary>
    public static bool RecoverInterrupted(EntryLocalState state)
    {
        var changed = false;
        if (state.ProcessingStatus == LocalProcessingStatus.Processing)
        {
            state.ProcessingStatus = LocalProcessingStatus.Failed;
            state.ErrorMessage = "Processing Failed: the application closed while processing. Retry processing to start again.";
            changed = true;
        }

        if (state.UploadStatus == UploadStatus.Uploading)
        {
            state.UploadStatus = UploadStatus.NotStarted;
            state.ErrorMessage = "Upload was interrupted when the application closed; it will resume.";
            changed = true;
        }

        if (state.AssignmentStatus == AssignmentStatus.Assigning)
        {
            state.AssignmentStatus = AssignmentStatus.NotStarted;
            state.ErrorMessage = "Assignment was interrupted when the application closed; it will resume.";
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// A completed (or deliberately skipped) stage whose next stage never started:
    /// only reachable when the application stopped between stages, so it resumes
    /// automatically — from the processed file, from the selected file in direct
    /// mode, or from the uploaded link. Failed stages are not resumed
    /// automatically; the operator retries them. A disabled upload is never
    /// resumed: that operation was deliberately local.
    /// </summary>
    public static bool NeedsAutomaticResume(EntryLocalState state, bool uploadEnabled)
        => uploadEnabled &&
           (state.ProcessingStatus == LocalProcessingStatus.Completed ||
            (state.ProcessingStatus == LocalProcessingStatus.Skipped && !string.IsNullOrWhiteSpace(state.LocalVideoPath))) &&
           (state.UploadStatus == UploadStatus.NotStarted ||
            (state.UploadStatus == UploadStatus.Completed && state.AssignmentStatus == AssignmentStatus.NotStarted));
}
