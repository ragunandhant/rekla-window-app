using System.Globalization;
using System.IO;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.App.ViewModels;

/// <summary>
/// Operator-facing status of one entry: the single badge shown in the list.
/// Each value has its own glyph as well as its own colour, so the list stays
/// readable without relying on colour alone.
/// </summary>
public enum EntryDisplayStatus
{
    ExtractionPending,
    NoVideo,
    HasVideo,
    VideoMissing,
    Ready,
    Processing,
    Processed,
    Uploading,
    Assigning,
    Completed,
    Outdated,
    Cancelled,
    Failed,
    UploadFailed,
    AssignmentFailed,
    AuthenticationFailed
}

/// <summary>
/// One entry of the selected race and category, in the list and, when selected,
/// in the workspace.
///
/// The operator-facing identifier is always the primary player's card number.
/// Every entry owns its own local state, scoped to its race and category.
/// </summary>
public sealed class EntryItemViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private RaceEntry _entry;
    private EntryLocalState _localState;

    public EntryItemViewModel(RaceEntry entry, EntryLocalState localState, AppSettings settings)
    {
        _entry = entry;
        _localState = localState;
        _settings = settings;
    }

    public RaceEntry Entry => _entry;
    public EntryLocalState LocalState => _localState;
    public RaceScope Scope => _localState.Scope;

    /// <summary>Stable internal key. Not shown to the operator.</summary>
    public string EntryId => _entry.EntryId;

    /// <summary>The entry identifier the operator sees everywhere.</summary>
    public string CardNumber => _entry.CardNumber;

    public string PrimaryName => _entry.PrimaryName;
    public string PrimaryLocation => _entry.PrimaryLocation;
    public string PrimaryDisplay => _entry.PrimaryDisplay;
    public string SecondaryDisplay => _entry.SecondaryDisplay;
    public string SecondaryName => _entry.HasSecondary ? _entry.SecondaryName!.Trim() : "—";
    public string SecondaryLocation => _entry.HasSecondary ? (_entry.SecondaryLocation ?? string.Empty).Trim() : string.Empty;

    /// <summary>The race performance time from the API <c>timings</c>, e.g. "17.88 sec". Not a date.</summary>
    public string TimingSecondsText => _entry.TimingSeconds > 0
        ? _entry.TimingSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " sec"
        : "—";
    public bool HasSecondary => _entry.HasSecondary;
    public string RaceTypeDisplay => _entry.RaceTypeDisplay;
    public string TimingDisplay => TimingFormatter.Format(_entry.TimingSeconds, _settings.TimingFormat);

    public DateTimeOffset? EntryDateUtc => _entry.EntryDateUtc;

    public string EntryDateDisplay => _entry.EntryDateUtc?.ToLocalTime().ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>The player's video link: from the API, or the one this application just assigned.</summary>
    public string? VideoLink => VideoEligibility.HasVideo(_entry.VideoLink)
        ? _entry.VideoLink
        : _localState.AssignmentStatus == AssignmentStatus.Completed ? _localState.UploadedVideoLink : null;

    public bool HasVideoLink => VideoEligibility.HasVideo(VideoLink);

    public RemoteExtractionStatus ExtractionStatus => _entry.ExtractionStatus;

    public string RemoteStatusDisplay => _entry.ExtractionStatus switch
    {
        RemoteExtractionStatus.Completed => "COMPLETED",
        RemoteExtractionStatus.NotCompleted => "NOT COMPLETED",
        RemoteExtractionStatus.Failed => "FAILED",
        _ => "UNKNOWN"
    };

    // ---- Local video -------------------------------------------------------

    public string? LocalVideoPath => _localState.LocalVideoPath;
    public bool HasLocalVideo => !string.IsNullOrWhiteSpace(_localState.LocalVideoPath);
    public bool LocalVideoExists => HasLocalVideo && File.Exists(_localState.LocalVideoPath);

    public string LocalVideoFileName => HasLocalVideo
        ? Path.GetFileName(_localState.LocalVideoPath)!
        : "No video selected";

    public string LocalVideoFolder => HasLocalVideo
        ? Path.GetDirectoryName(_localState.LocalVideoPath) ?? string.Empty
        : string.Empty;

    public string LocalVideoStatusText => !HasLocalVideo
        ? "Select a video: processing starts as soon as you choose it."
        : LocalVideoExists ? "Video found" : "File missing on disk";

    // ---- Stages ------------------------------------------------------------

    public LocalProcessingStatus ProcessingStatus => _localState.ProcessingStatus;
    public UploadStatus UploadStatus => _localState.UploadStatus;
    public AssignmentStatus AssignmentStatus => _localState.AssignmentStatus;
    public OverallStatus Overall => _localState.Overall;

    public string ProcessingStageText => _localState.ProcessingStatus switch
    {
        LocalProcessingStatus.Processing => "Processing",
        LocalProcessingStatus.Completed => "Processing Completed",
        LocalProcessingStatus.Failed or LocalProcessingStatus.VideoNotFound => "Processing Failed",
        LocalProcessingStatus.Cancelled => "Processing Cancelled",
        LocalProcessingStatus.Outdated => "Outdated — race data changed",
        LocalProcessingStatus.Ready => "Ready",
        _ => "Not started"
    };

    public string UploadStageText => _localState.UploadStatus switch
    {
        UploadStatus.Uploading => "Uploading",
        UploadStatus.Completed => "Upload Completed",
        UploadStatus.Failed => _localState.LastFailureWasAuthentication ? "Authentication Failed" : "Upload Failed",
        UploadStatus.Disabled => "Upload Disabled",
        _ => "Not Started"
    };

    public string AssignmentStageText => _localState.AssignmentStatus switch
    {
        AssignmentStatus.Assigning => "Assigning",
        AssignmentStatus.Completed => "Assignment Completed",
        AssignmentStatus.Failed => _localState.LastFailureWasAuthentication ? "Authentication Failed" : "Assignment Failed",
        _ => "Not Started"
    };

    public string? UploadedVideoLink => _localState.UploadedVideoLink;
    public bool HasUploadedVideoLink => !string.IsNullOrWhiteSpace(_localState.UploadedVideoLink);

    public string? OutputPath => _localState.OutputPath;
    public bool HasOutput => !string.IsNullOrWhiteSpace(_localState.OutputPath);
    public string OutputDisplay => HasOutput ? _localState.OutputPath! : "No output yet";
    public string OutputFileName => HasOutput ? Path.GetFileName(_localState.OutputPath)! : "—";

    public string OutputStatusText => _localState.ProcessingStatus switch
    {
        LocalProcessingStatus.Completed when HasOutput && File.Exists(_localState.OutputPath) => "Validated",
        LocalProcessingStatus.Outdated => "Outdated",
        _ => "Not ready"
    };

    public string? ErrorMessage => _localState.ErrorMessage;
    public bool HasError => !string.IsNullOrWhiteSpace(_localState.ErrorMessage);

    /// <summary>What a retry would do next, or null when there is nothing to retry.</summary>
    public string? RetryLabel
    {
        get
        {
            if (_localState.ProcessingStatus is LocalProcessingStatus.Failed or LocalProcessingStatus.Cancelled
                or LocalProcessingStatus.Outdated && LocalVideoExists)
                return "RETRY PROCESSING";
            if (_localState.ProcessingStatus == LocalProcessingStatus.Completed && _localState.UploadStatus == UploadStatus.Failed)
                return "RETRY UPLOAD";
            if (_localState.UploadStatus == UploadStatus.Completed && _localState.AssignmentStatus == AssignmentStatus.Failed)
                return "RETRY ASSIGNMENT";
            return null;
        }
    }

    public bool CanRetry => RetryLabel is not null;

    // ---- Combined display status ------------------------------------------

    public EntryDisplayStatus DisplayStatus
    {
        get
        {
            switch (_localState.Overall)
            {
                case OverallStatus.Processing: return EntryDisplayStatus.Processing;
                case OverallStatus.Cancelled: return EntryDisplayStatus.Cancelled;
                case OverallStatus.ProcessingFailed:
                    return _localState.ProcessingStatus == LocalProcessingStatus.VideoNotFound
                        ? EntryDisplayStatus.VideoMissing
                        : EntryDisplayStatus.Failed;
                case OverallStatus.ProcessingCompleted:
                case OverallStatus.UploadCompleted: return EntryDisplayStatus.Processed;
                case OverallStatus.Uploading: return EntryDisplayStatus.Uploading;
                case OverallStatus.UploadFailed: return EntryDisplayStatus.UploadFailed;
                case OverallStatus.Assigning: return EntryDisplayStatus.Assigning;
                case OverallStatus.AssignmentFailed: return EntryDisplayStatus.AssignmentFailed;
                case OverallStatus.AuthenticationFailed: return EntryDisplayStatus.AuthenticationFailed;
                case OverallStatus.Completed: return EntryDisplayStatus.Completed;
            }

            if (_localState.ProcessingStatus == LocalProcessingStatus.Outdated)
                return EntryDisplayStatus.Outdated;
            // Only the video link decides: no link means no video yet, whatever the
            // race result status says.
            if (HasVideoLink)
                return EntryDisplayStatus.HasVideo;
            return HasLocalVideo ? EntryDisplayStatus.Ready : EntryDisplayStatus.NoVideo;
        }
    }

    public string StatusText => DisplayStatus switch
    {
        EntryDisplayStatus.Processing => "PROCESSING",
        EntryDisplayStatus.Processed => _localState.UploadStatus == UploadStatus.Disabled ? "PROCESSED · UPLOAD OFF" : "PROCESSED",
        EntryDisplayStatus.Uploading => "UPLOADING",
        EntryDisplayStatus.Assigning => "ASSIGNING",
        EntryDisplayStatus.Completed => "COMPLETED",
        EntryDisplayStatus.Outdated => "OUTDATED",
        EntryDisplayStatus.Cancelled => "CANCELLED",
        EntryDisplayStatus.Failed => "PROCESSING FAILED",
        EntryDisplayStatus.UploadFailed => "UPLOAD FAILED",
        EntryDisplayStatus.AssignmentFailed => "ASSIGNMENT FAILED",
        EntryDisplayStatus.AuthenticationFailed => "AUTH FAILED",
        EntryDisplayStatus.VideoMissing => "VIDEO MISSING",
        EntryDisplayStatus.ExtractionPending => "NOT COMPLETED",
        EntryDisplayStatus.HasVideo => "HAS VIDEO",
        EntryDisplayStatus.NoVideo => "READY FOR UPLOAD",
        _ => "READY"
    };

    /// <summary>Shape as well as colour, so status is never carried by colour alone.</summary>
    public string StatusGlyph => DisplayStatus switch
    {
        EntryDisplayStatus.Processing or EntryDisplayStatus.Uploading or EntryDisplayStatus.Assigning => "⟳",
        EntryDisplayStatus.Completed or EntryDisplayStatus.HasVideo => "✓",
        EntryDisplayStatus.Processed => "◐",
        EntryDisplayStatus.Outdated or EntryDisplayStatus.VideoMissing => "⚠",
        EntryDisplayStatus.Failed or EntryDisplayStatus.UploadFailed or EntryDisplayStatus.AssignmentFailed
            or EntryDisplayStatus.AuthenticationFailed => "✕",
        EntryDisplayStatus.Cancelled => "■",
        EntryDisplayStatus.ExtractionPending => "◷",
        EntryDisplayStatus.NoVideo => "○",
        _ => "●"
    };

    /// <summary>"Not Uploaded" or "Already Assigned", from the video link alone.</summary>
    public string VideoAssignmentText => HasVideoLink ? "Already Assigned" : "Not Uploaded";

    /// <summary>
    /// Can be picked automatically as the next entry: no video link yet (null,
    /// empty or whitespace) and nothing done to it locally. The race result status
    /// is deliberately not a condition.
    /// </summary>
    public bool IsEligibleForNext =>
        !HasVideoLink &&
        _localState.ProcessingStatus is LocalProcessingStatus.VideoNotSelected or LocalProcessingStatus.Ready
            or LocalProcessingStatus.VideoNotFound;

    // ---- Search / filter ---------------------------------------------------

    /// <summary>Search covers card number, names, locations and the technical ids.</summary>
    public bool MatchesSearch(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return true;

        var needle = term.Trim();
        return Contains(CardNumber, needle)
               || Contains(_entry.PrimaryName, needle)
               || Contains(_entry.PrimaryLocation, needle)
               || Contains(_entry.SecondaryName, needle)
               || Contains(_entry.SecondaryLocation, needle)
               || Contains(_entry.UserId, needle)
               || Contains(_entry.PlayerId, needle)
               || Contains(LocalVideoFileName, needle);
    }

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack) &&
           haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ---- Mutation ----------------------------------------------------------

    public void UpdateRemote(RaceEntry entry, EntryLocalState localState)
    {
        _entry = entry;

        // Polling and operator actions run concurrently: never let an older poll
        // snapshot overwrite a newer local mapping or processing state.
        if (localState.UpdatedUtc >= _localState.UpdatedUtc)
            _localState = localState;

        NotifyAll();
    }

    public void ReplaceLocalState(EntryLocalState state)
    {
        _localState = state;
        NotifyAll();
    }

    private void NotifyAll() => OnPropertyChanged(string.Empty);
}
