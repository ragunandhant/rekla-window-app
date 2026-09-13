using System.IO;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.App.ViewModels;

/// <summary>
/// Operator-facing status of one entry, combining the remote extraction state and
/// the local processing state into the single badge shown in the list.
/// Each value has its own glyph as well as its own colour, so the list stays
/// readable without relying on colour alone.
/// </summary>
public enum EntryDisplayStatus
{
    ExtractionPending,
    NoVideo,
    VideoMissing,
    Ready,
    Processing,
    Completed,
    Outdated,
    Failed
}

/// <summary>
/// One entry in the list and, when selected, in the workspace.
///
/// The operator-facing identifier is always the primary player's card number.
/// Every entry owns its own local state — mapped video, processing status, output,
/// error — and switching between entries never mutates another entry's state.
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

    /// <summary>Stable internal key. Not shown to the operator.</summary>
    public string EntryId => _entry.EntryId;

    /// <summary>The entry identifier the operator sees everywhere.</summary>
    public string CardNumber => _entry.CardNumber;

    public string PrimaryDisplay => _entry.PrimaryDisplay;
    public string SecondaryDisplay => _entry.SecondaryDisplay;
    public bool HasSecondary => _entry.HasSecondary;
    public string RaceTypeDisplay => _entry.RaceTypeDisplay;
    public string TimingDisplay => TimingFormatter.Format(_entry.TimingSeconds, _settings.TimingFormat);
    public string? VideoLink => _entry.VideoLink;
    public bool HasVideoLink => !string.IsNullOrWhiteSpace(_entry.VideoLink);

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
        : "No video mapped";

    public string LocalVideoFolder => HasLocalVideo
        ? Path.GetDirectoryName(_localState.LocalVideoPath) ?? string.Empty
        : string.Empty;

    public string LocalVideoStatusText => !HasLocalVideo
        ? "Not mapped"
        : LocalVideoExists ? "Video found" : "File missing on disk";

    // ---- Processing / output ----------------------------------------------

    public LocalProcessingStatus ProcessingStatus => _localState.ProcessingStatus;

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

    // ---- Combined display status ------------------------------------------

    public EntryDisplayStatus DisplayStatus
    {
        get
        {
            switch (_localState.ProcessingStatus)
            {
                case LocalProcessingStatus.Processing: return EntryDisplayStatus.Processing;
                case LocalProcessingStatus.Completed: return EntryDisplayStatus.Completed;
                case LocalProcessingStatus.Outdated: return EntryDisplayStatus.Outdated;
                case LocalProcessingStatus.Failed: return EntryDisplayStatus.Failed;
                case LocalProcessingStatus.VideoNotFound: return EntryDisplayStatus.VideoMissing;
            }

            if (_entry.ExtractionStatus != RemoteExtractionStatus.Completed)
                return EntryDisplayStatus.ExtractionPending;

            return HasLocalVideo ? EntryDisplayStatus.Ready : EntryDisplayStatus.NoVideo;
        }
    }

    public string StatusText => DisplayStatus switch
    {
        EntryDisplayStatus.Processing => "PROCESSING",
        EntryDisplayStatus.Completed => "COMPLETED",
        EntryDisplayStatus.Outdated => "OUTDATED",
        EntryDisplayStatus.Failed => "FAILED",
        EntryDisplayStatus.VideoMissing => "VIDEO MISSING",
        EntryDisplayStatus.ExtractionPending => "EXTRACTION PENDING",
        EntryDisplayStatus.NoVideo => "NO VIDEO",
        _ => "READY"
    };

    /// <summary>Shape as well as colour, so status is never carried by colour alone.</summary>
    public string StatusGlyph => DisplayStatus switch
    {
        EntryDisplayStatus.Processing => "⟳",
        EntryDisplayStatus.Completed => "✓",
        EntryDisplayStatus.Outdated => "⚠",
        EntryDisplayStatus.Failed => "✕",
        EntryDisplayStatus.VideoMissing => "⚠",
        EntryDisplayStatus.ExtractionPending => "◷",
        EntryDisplayStatus.NoVideo => "○",
        _ => "●"
    };

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

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(LocalState));
        OnPropertyChanged(nameof(CardNumber));
        OnPropertyChanged(nameof(PrimaryDisplay));
        OnPropertyChanged(nameof(SecondaryDisplay));
        OnPropertyChanged(nameof(HasSecondary));
        OnPropertyChanged(nameof(RaceTypeDisplay));
        OnPropertyChanged(nameof(TimingDisplay));
        OnPropertyChanged(nameof(VideoLink));
        OnPropertyChanged(nameof(HasVideoLink));
        OnPropertyChanged(nameof(ExtractionStatus));
        OnPropertyChanged(nameof(RemoteStatusDisplay));
        OnPropertyChanged(nameof(LocalVideoPath));
        OnPropertyChanged(nameof(HasLocalVideo));
        OnPropertyChanged(nameof(LocalVideoExists));
        OnPropertyChanged(nameof(LocalVideoFileName));
        OnPropertyChanged(nameof(LocalVideoFolder));
        OnPropertyChanged(nameof(LocalVideoStatusText));
        OnPropertyChanged(nameof(ProcessingStatus));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(OutputDisplay));
        OnPropertyChanged(nameof(OutputFileName));
        OnPropertyChanged(nameof(OutputStatusText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusGlyph));
    }
}
