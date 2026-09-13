using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RaceVideoProcessor.App.Commands;
using RaceVideoProcessor.App.Services;
using RaceVideoProcessor.App.Views;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.App.ViewModels;

/// <summary>
/// Drives the whole operator surface.
///
/// Rules that shape this class:
///   1. Everything happens inside the selected race and category. The race
///      supplies the one Race ID; the category only supplies the API type.
///   2. Selecting a video starts the job: process, then upload and assign when
///      upload is ON. There is no start button, and only one job runs at a time.
///   3. A job captures its race, category and entry when it starts. Nothing the
///      operator does afterwards changes what it acts on, and switching race is
///      refused while it runs.
///   4. Selection is never gated: the operator can look at any entry at any time.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILocalStateRepository _repository;
    private readonly IDemoEntryController _demoController;
    private readonly PollingCoordinator _polling;
    private readonly IVideoProcessingService _videoProcessing;
    private readonly EntryWorkflowService _workflow;
    private readonly IEncoderCapabilityService _encoderCapability;
    private readonly IBackendDiagnostics _backendDiagnostics;
    private readonly IToolHealthService _toolHealth;
    private readonly IFontResolver _fontResolver;
    private readonly IAppLog _log;
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>Entries already auto-resumed this session, so a failing resume is not repeated in a loop.</summary>
    private readonly HashSet<string> _resumeAttempted = new(StringComparer.OrdinalIgnoreCase);

    private EntryItemViewModel? _selectedEntry;
    private AppPage _activePage = AppPage.Work;
    private string _searchText = string.Empty;
    private EntryFilter _filter = EntryFilter.All;
    private bool _rebuildingList;

    private string _apiStatusText = "STARTING";
    private string _apiErrorText = string.Empty;
    private string _lastSyncText = "—";
    private string _nextSyncText = "—";
    private bool _isApiConnected;
    private string _encoderDisplay = "Detecting…";

    // ---- The running job -----------------------------------------------------

    private ActiveJob? _job;
    private CancellationTokenSource? _jobCts;
    private string _processingFile = "—";
    private double _jobPercent;
    private bool _jobPercentKnown = true;
    private string _processingElapsed = "00:00";
    private string _processingEta = "—";

    private string _bannerText = string.Empty;
    private BannerKind _bannerSeverity = BannerKind.Info;

    private sealed record ActiveJob(RaceScope Scope, string EntryId, string CardNumber, string PrimaryName, bool IsRecovery)
    {
        public WorkflowStage Stage { get; set; } = WorkflowStage.Processing;
    }

    public MainViewModel(
        AppSettings settings,
        ILocalStateRepository repository,
        IDemoEntryController demoController,
        PollingCoordinator polling,
        IVideoProcessingService videoProcessing,
        EntryWorkflowService workflow,
        IEncoderCapabilityService encoderCapability,
        IBackendDiagnostics backendDiagnostics,
        IToolHealthService toolHealth,
        IFontResolver fontResolver,
        IAppLog log)
    {
        Settings = settings;
        _repository = repository;
        _demoController = demoController;
        _polling = polling;
        _videoProcessing = videoProcessing;
        _workflow = workflow;
        _encoderCapability = encoderCapability;
        _backendDiagnostics = backendDiagnostics;
        _toolHealth = toolHealth;
        _fontResolver = fontResolver;
        _log = log;

        _polling.SnapshotUpdated += OnSnapshotUpdated;
        _polling.StatusUpdated += OnPollStatusUpdated;
        _log.LineWritten += OnLogLineWritten;

        SelectVideoCommand = new AsyncRelayCommand(SelectVideoAsync, () => SelectVideoBlockReason.Length == 0);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => PreviewBlockReason.Length == 0);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => RetryBlockReason.Length == 0);
        CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => OpenOutputBlockReason.Length == 0);
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(Settings.OutputVideoFolder));
        OpenInputFolderCommand = new RelayCommand(() => OpenFolder(Settings.InputVideoFolder));
        OpenDemoVideoFolderCommand = new RelayCommand(() => OpenFolder(Settings.DemoVideoFolder));
        SimulateNextEntryCommand = new AsyncRelayCommand(SimulateNextEntryAsync, () => IsDemoMode && HasActiveRace);
        ResetDemoCommand = new AsyncRelayCommand(ResetDemoAsync, () => IsDemoMode);
        PollNowCommand = new AsyncRelayCommand(() => _polling.PollNowAsync(_lifetimeCts.Token), () => HasActiveRace);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        TestLoginCommand = new AsyncRelayCommand(TestLoginAsync);
        TestFfmpegCommand = new AsyncRelayCommand(TestFfmpegAsync);
        TestNvencCommand = new AsyncRelayCommand(TestNvencAsync);
        SetCategoryCommand = new AsyncRelayCommand<string>(SetCategoryAsync);
        SetUploadCommand = new AsyncRelayCommand<string>(SetUploadAsync);
        NavigateCommand = new RelayCommand<string>(page =>
        {
            if (Enum.TryParse<AppPage>(page, ignoreCase: true, out var parsed))
                ActivePage = parsed;
        });
        SetFilterCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<EntryFilter>(name, ignoreCase: true, out var parsed))
                Filter = parsed;
        });
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        DismissBannerCommand = new RelayCommand(() => BannerText = string.Empty);
        ClearLogsCommand = new RelayCommand(() => LogLines.Clear());
        OpenLogFileCommand = new RelayCommand(OpenLogFile);

        InitializeRaceManagement();
    }

    // ---- Collections -------------------------------------------------------

    public AppSettings Settings { get; }

    /// <summary>Entries of the selected race and category, newest API date first.</summary>
    public ObservableCollection<EntryItemViewModel> Entries { get; } = [];

    /// <summary>What the list actually shows after search and filter.</summary>
    public ObservableCollection<EntryItemViewModel> VisibleEntries { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public IReadOnlyList<DataSourceMode> DataSourceModes { get; } = Enum.GetValues<DataSourceMode>();
    public IReadOnlyList<int> PollingIntervals { get; } = [10, 20, 30, 60];
    public IReadOnlyList<EncoderPreference> EncoderPreferences { get; } = Enum.GetValues<EncoderPreference>();
    public IReadOnlyList<EncodingQuality> EncodingQualities { get; } = Enum.GetValues<EncodingQuality>();

    // ---- Commands ----------------------------------------------------------

    public ICommand SelectVideoCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelProcessingCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand OpenInputFolderCommand { get; }
    public ICommand OpenDemoVideoFolderCommand { get; }
    public ICommand SimulateNextEntryCommand { get; }
    public ICommand ResetDemoCommand { get; }
    public ICommand PollNowCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand TestLoginCommand { get; }
    public ICommand TestFfmpegCommand { get; }
    public ICommand TestNvencCommand { get; }
    public ICommand SetCategoryCommand { get; }
    public ICommand SetUploadCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand OpenLogFileCommand { get; }

    // ---- Navigation and selection -----------------------------------------

    public AppPage ActivePage
    {
        get => _activePage;
        set => SetProperty(ref _activePage, value);
    }

    /// <summary>
    /// The entry shown in the workspace. Deliberately a plain settable property:
    /// nothing in the application may refuse or defer a selection change.
    /// </summary>
    public EntryItemViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            // Rebuilding the list briefly empties it, and the ListBox reports that
            // as "nothing selected". That is not the operator's choice; ignore it.
            if (_rebuildingList && value is null)
                return;
            if (!SetProperty(ref _selectedEntry, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            RefreshWorkContext();
        }
    }

    public bool HasSelection => SelectedEntry is not null;
    public bool HasEntries => Entries.Count > 0;
    public bool HasVisibleEntries => VisibleEntries.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                RebuildVisibleEntries();
        }
    }

    public EntryFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
                RebuildVisibleEntries();
        }
    }

    public string EntryCountSummary => VisibleEntries.Count == Entries.Count
        ? $"{Entries.Count} entries"
        : $"{VisibleEntries.Count} of {Entries.Count} entries";

    public string EmptyListText => !HasActiveRace
        ? "No race selected. Open Races to create or select one."
        : HasEntries
            ? "No entries match. Clear the search or filter."
            : $"No {SelectedCategoryLabel} entries yet for this race.";

    // ---- Category and upload ----------------------------------------------

    public RaceCategory SelectedCategory => Settings.SelectedCategory;
    public string SelectedCategoryLabel => Settings.SelectedCategory == RaceCategory.Meter300 ? "300 Meter" : "200 Meter";
    public bool UploadEnabled => Settings.UploadEnabled;

    public RaceScope? CurrentScope => ActiveRace is null ? null : new RaceScope(ActiveRace.RaceId, Settings.SelectedCategory);

    private async Task SetCategoryAsync(string? name)
    {
        if (!Enum.TryParse<RaceCategory>(name, ignoreCase: true, out var category) || category == Settings.SelectedCategory)
            return;

        Settings.SelectedCategory = category;
        await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);
        _log.Info($"Category switched to {SelectedCategoryLabel}" + (ActiveRace is null ? "." : $" for race {ActiveRace.RaceName}."));
        await LoadScopeAsync();
    }

    private async Task SetUploadAsync(string? value)
    {
        var enabled = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase);
        if (enabled == Settings.UploadEnabled)
            return;

        Settings.UploadEnabled = enabled;
        await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);
        _log.Info("Upload switched " + (enabled ? "ON." : "OFF."));
        if (IsBusy)
            ShowBanner(BannerKind.Info, $"Upload is now {(enabled ? "ON" : "OFF")}. The job already running keeps the setting it started with.");
        RefreshWorkContext();
        if (enabled)
            await TryResumeInterruptedAsync();
    }

    // ---- Provider status ---------------------------------------------------

    public string ApiStatusText { get => _apiStatusText; private set => SetProperty(ref _apiStatusText, value); }
    public string ApiErrorText { get => _apiErrorText; private set => SetProperty(ref _apiErrorText, value); }
    public string LastSyncText { get => _lastSyncText; private set => SetProperty(ref _lastSyncText, value); }
    public string NextSyncText { get => _nextSyncText; private set => SetProperty(ref _nextSyncText, value); }
    public bool IsApiConnected { get => _isApiConnected; private set => SetProperty(ref _isApiConnected, value); }
    public string EncoderDisplay { get => _encoderDisplay; private set => SetProperty(ref _encoderDisplay, value); }
    public bool IsDemoMode => Settings.DataSourceMode == DataSourceMode.Demo;
    public string ModeLabel => IsDemoMode ? "DEMO" : "REAL API";

    public string FontDisplay { get; private set; } = "Resolving…";
    public string FontWarning { get; private set; } = string.Empty;
    public bool HasFontWarning => FontWarning.Length > 0;

    // ---- Job status (top of the workspace) ---------------------------------

    /// <summary>A job is running: processing, uploading or assigning.</summary>
    public bool IsBusy => _job is not null;

    /// <summary>The running job is in its processing stage — the only stage that can be cancelled.</summary>
    public bool IsProcessing => _job is { Stage: WorkflowStage.Processing };

    public bool IsUploading => _job is { Stage: WorkflowStage.Uploading };

    public string ProcessingFile { get => _processingFile; private set => SetProperty(ref _processingFile, value); }
    public string ProcessingElapsed { get => _processingElapsed; private set => SetProperty(ref _processingElapsed, value); }
    public string ProcessingEta { get => _processingEta; private set => SetProperty(ref _processingEta, value); }

    /// <summary>Progress of the running stage: FFmpeg progress while processing, bytes sent while uploading.</summary>
    public double JobPercent
    {
        get => _jobPercent;
        private set
        {
            if (SetProperty(ref _jobPercent, value))
                OnPropertyChanged(nameof(JobPercentText));
        }
    }

    /// <summary>False when the real percentage is not available; the view shows an indeterminate bar.</summary>
    public bool JobPercentKnown
    {
        get => _jobPercentKnown;
        private set
        {
            if (SetProperty(ref _jobPercentKnown, value))
                OnPropertyChanged(nameof(JobPercentText));
        }
    }

    public string JobPercentText => !IsBusy ? "—" : JobPercentKnown ? $"{JobPercent:0}%" : "…";

    public string ProcessingSummary => _job is null ? "Idle" : $"{StageName(_job.Stage)} · Cart {_job.CardNumber}";

    /// <summary>Shown as "Current": the entry being worked on, else the selected entry.</summary>
    public string CurrentCardText => _job is not null ? _job.CardNumber : SelectedEntry?.CardNumber ?? "—";

    public string CurrentNameText => _job is not null ? _job.PrimaryName : SelectedEntry?.PrimaryName ?? string.Empty;

    public string StageText => _job is not null
        ? StageName(_job.Stage)
        : SelectedEntry is null ? "—" : ToTitle(SelectedEntry.StatusText);

    public string UploadProgressText => _job is { Stage: WorkflowStage.Uploading } ? JobPercentText : Settings.UploadEnabled ? "—" : "Off";

    // ---- Next entry --------------------------------------------------------

    /// <summary>
    /// The entry that will be selected automatically once the current one
    /// completes. Informational only; it is never selected before that.
    /// </summary>
    public EntryItemViewModel? NextEntry
    {
        get
        {
            var anchorId = _job is not null && _job.Scope == CurrentScope ? _job.EntryId : SelectedEntry?.EntryId;
            var anchor = anchorId is null ? null : Entries.FirstOrDefault(e => string.Equals(e.EntryId, anchorId, StringComparison.OrdinalIgnoreCase));
            return EntryOrdering.FindNext(Entries, anchor, e => e.IsEligibleForNext && !IsJobEntry(e));
        }
    }

    public bool HasNextEntry => NextEntry is not null;

    // ---- Banner ------------------------------------------------------------

    public string BannerText
    {
        get => _bannerText;
        private set
        {
            if (SetProperty(ref _bannerText, value))
                OnPropertyChanged(nameof(IsBannerVisible));
        }
    }

    public BannerKind BannerSeverity { get => _bannerSeverity; private set => SetProperty(ref _bannerSeverity, value); }
    public bool IsBannerVisible => BannerText.Length > 0;

    private void ShowBanner(BannerKind kind, string text)
    {
        BannerSeverity = kind;
        BannerText = text;
    }

    // ---- Why an action is unavailable --------------------------------------

    public string SelectVideoBlockReason
    {
        get
        {
            if (!HasActiveRace) return "Select a race first.";
            if (SelectedEntry is null) return "Select an entry first.";
            if (_job is not null) return $"Cart {_job.CardNumber} is {StageName(_job.Stage).ToLowerInvariant()}. One video at a time.";
            if (SelectedEntry.ExtractionStatus != RemoteExtractionStatus.Completed) return "This race result is not completed yet.";
            return string.Empty;
        }
    }

    public string PreviewBlockReason
    {
        get
        {
            if (SelectedEntry is null) return "Select an entry first.";
            if (!SelectedEntry.HasLocalVideo) return "Select a video first.";
            if (!SelectedEntry.LocalVideoExists) return "The selected video file is missing.";
            if (IsBusy) return "Available once the current job finishes.";
            return string.Empty;
        }
    }

    public string RetryBlockReason
    {
        get
        {
            if (SelectedEntry is null) return "Select an entry first.";
            if (_job is not null) return $"Cart {_job.CardNumber} is {StageName(_job.Stage).ToLowerInvariant()}. One job at a time.";
            var label = SelectedEntry.RetryLabel;
            if (label is null) return "Nothing to retry.";
            if (label != "RETRY PROCESSING" && !Settings.UploadEnabled) return "Upload is OFF. Switch it ON to retry.";
            return string.Empty;
        }
    }

    public string RetryLabel => SelectedEntry?.RetryLabel ?? "RETRY";

    public string OpenOutputBlockReason
    {
        get
        {
            if (SelectedEntry is null) return "Select an entry first.";
            if (!SelectedEntry.HasOutput) return "No output has been produced yet.";
            if (!File.Exists(SelectedEntry.OutputPath)) return "The output file is no longer on disk.";
            return string.Empty;
        }
    }

    // ---- Startup -----------------------------------------------------------

    public async Task InitializeAsync()
    {
        LoadRecentLogs();
        ResolveFont();
        await RecoverInterruptedStatesAsync();
        await LoadRacesAsync();

        try
        {
            using var detectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            detectCts.CancelAfter(TimeSpan.FromSeconds(20));
            var capability = await _encoderCapability.DetectAsync(detectCts.Token);
            EncoderDisplay = capability.DisplayName;
        }
        catch (Exception ex)
        {
            EncoderDisplay = "CPU / x264";
            _log.Error("Encoder detection failed: " + ex.Message);
        }

        await RestoreSelectedRaceAsync();
        await _polling.StartAsync(_lifetimeCts.Token);
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(IsDemoMode));
    }

    /// <summary>
    /// Nothing runs at startup, so any stage stored as running was interrupted by
    /// a crash or a forced close. Make the stored state truthful before anything reads it.
    /// </summary>
    private async Task RecoverInterruptedStatesAsync()
    {
        try
        {
            var inFlight = await _repository.GetInFlightEntryStatesAsync(_lifetimeCts.Token);
            foreach (var state in inFlight)
            {
                if (!WorkflowRecovery.RecoverInterrupted(state))
                    continue;
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
                _log.Info($"Recovery: [{state.Scope}] cart {state.CardNumber ?? state.EntryId} was interrupted. " +
                          $"Processing {state.ProcessingStatus}, upload {state.UploadStatus}, assignment {state.AssignmentStatus}.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Recovery check failed: " + ex.Message);
        }
    }

    private void ResolveFont()
    {
        var font = _fontResolver.Resolve();
        FontDisplay = font.Path is null ? "No font found" : $"{font.Name} ({font.Source})";
        FontWarning = font.Path is null
            ? "No usable font was found. Install a Tamil-capable font or set one in Settings; rendering will fail without one."
            : font.SupportsTamil
                ? string.Empty
                : $"No installed font contains Tamil glyphs, so Tamil names would render as boxes in the video. " +
                  "Install Nirmala UI or Noto Sans Tamil, or set a Tamil font file in Settings.";

        OnPropertyChanged(nameof(FontDisplay));
        OnPropertyChanged(nameof(FontWarning));
        OnPropertyChanged(nameof(HasFontWarning));

        if (FontWarning.Length > 0)
            _log.Error("Font check: " + FontWarning);
        else
            _log.Info($"Scoreboard font: {FontDisplay}.");
    }

    // ---- Loading a race and category ---------------------------------------

    /// <summary>Clears the list and loads the current race and category.</summary>
    private async Task LoadScopeAsync()
    {
        var scope = CurrentScope;
        _polling.Scope = scope;

        Entries.Clear();
        _rebuildingList = false;
        SelectedEntry = null;
        RebuildVisibleEntries();
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(SelectedCategoryLabel));
        RefreshWorkContext();

        if (scope is null)
        {
            ApiStatusText = "NO RACE";
            return;
        }

        ApiStatusText = "LOADING";
        await _polling.PollNowAsync(_lifetimeCts.Token);
    }

    // ---- Polling -----------------------------------------------------------

    private void OnSnapshotUpdated(object? sender, SyncSnapshot snapshot)
        => Application.Current?.Dispatcher.InvokeAsync(() => ApplySnapshotAsync(snapshot));

    private async Task ApplySnapshotAsync(SyncSnapshot snapshot)
    {
        // A refresh that started before the race or category changed belongs to
        // the old context. It is already stored; it just must not be shown here.
        if (snapshot.Scope != CurrentScope)
            return;

        foreach (var item in snapshot.Entries)
        {
            var existing = Entries.FirstOrDefault(x =>
                string.Equals(x.EntryId, item.Entry.EntryId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var added = new EntryItemViewModel(item.Entry, item.LocalState, Settings);
                Entries.Insert(SortedIndexFor(added), added);
                _log.Info($"[{snapshot.Scope}] cart {item.Entry.CardNumber} received.");
            }
            else
            {
                var dateChanged = existing.EntryDateUtc != item.Entry.EntryDateUtc;
                existing.UpdateRemote(item.Entry, item.LocalState);
                if (dateChanged)
                {
                    Entries.Remove(existing);
                    Entries.Insert(SortedIndexFor(existing), existing);
                }
            }
        }

        RebuildVisibleEntries();

        // Only ever auto-select when nothing is selected. A refresh must never move
        // the operator off the entry they are working on.
        if (SelectedEntry is null)
            SelectedEntry = VisibleEntries.FirstOrDefault(e => e.IsEligibleForNext) ?? VisibleEntries.FirstOrDefault();

        RefreshWorkContext();
        await TryResumeInterruptedAsync();
    }

    private int SortedIndexFor(EntryItemViewModel item)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (ReferenceEquals(Entries[i], item))
                continue;
            if (EntryOrdering.CompareNewestFirst(item.EntryDateUtc, item.CardNumber, Entries[i].EntryDateUtc, Entries[i].CardNumber) < 0)
                return i;
        }
        return Entries.Count;
    }

    private void OnPollStatusUpdated(object? sender, PollStatus status)
        => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsApiConnected = status.Connected;
            ApiStatusText = !HasActiveRace ? "NO RACE" : status.Connected ? "CONNECTED" : "OFFLINE";
            ApiErrorText = status.Error ?? string.Empty;
            LastSyncText = status.LastSuccessUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
            NextSyncText = status.NextSyncUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
        });

    private void OnLogLineWritten(object? sender, string line)
        => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        });

    private void LoadRecentLogs()
    {
        try
        {
            if (!File.Exists(_log.LogFilePath))
                return;
            foreach (var line in File.ReadLines(_log.LogFilePath).TakeLast(300))
                LogLines.Add(line);
        }
        catch
        {
            // The log view must never prevent startup.
        }
    }

    // ---- List filtering ----------------------------------------------------

    private void RebuildVisibleEntries()
    {
        var previous = SelectedEntry;

        _rebuildingList = true;
        try
        {
            VisibleEntries.Clear();
            foreach (var entry in Entries)
            {
                if (entry.MatchesSearch(SearchText) && MatchesFilter(entry))
                    VisibleEntries.Add(entry);
            }
        }
        finally
        {
            _rebuildingList = false;
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasVisibleEntries));
        OnPropertyChanged(nameof(EntryCountSummary));
        OnPropertyChanged(nameof(EmptyListText));

        // Filtering the selected entry out of view must not clear the workspace:
        // the operator keeps working on it until they pick something else.
        if (previous is not null && Entries.Contains(previous))
        {
            _selectedEntry = null;
            SelectedEntry = previous;
        }
    }

    private bool MatchesFilter(EntryItemViewModel entry) => Filter switch
    {
        EntryFilter.All => true,
        EntryFilter.Pending => entry.DisplayStatus is EntryDisplayStatus.NoVideo or EntryDisplayStatus.Ready
            or EntryDisplayStatus.ExtractionPending or EntryDisplayStatus.VideoMissing,
        EntryFilter.Active => entry.DisplayStatus is EntryDisplayStatus.Processing or EntryDisplayStatus.Uploading
            or EntryDisplayStatus.Assigning or EntryDisplayStatus.Processed,
        EntryFilter.Completed => entry.DisplayStatus is EntryDisplayStatus.Completed or EntryDisplayStatus.HasVideo,
        EntryFilter.Failed => entry.DisplayStatus is EntryDisplayStatus.Failed or EntryDisplayStatus.UploadFailed
            or EntryDisplayStatus.AssignmentFailed or EntryDisplayStatus.AuthenticationFailed
            or EntryDisplayStatus.Cancelled or EntryDisplayStatus.Outdated,
        _ => true
    };

    // ---- Select video: starts the job --------------------------------------

    private async Task SelectVideoAsync()
    {
        var entry = SelectedEntry;
        var race = ActiveRace;
        if (entry is null || race is null || IsBusy)
            return;

        if (entry.ExtractionStatus != RemoteExtractionStatus.Completed)
        {
            ShowBanner(BannerKind.Warning, $"Cart {entry.CardNumber}: the race result is not completed yet, so it cannot be processed.");
            return;
        }

        // Never silently overwrite a video that is already on the player.
        if (entry.HasVideoLink)
        {
            var choice = ChoiceDialog.Show(
                "Existing video",
                "This entry already has a video. Replace it?",
                $"Cart {entry.CardNumber} — {entry.PrimaryDisplay}\n\nCurrent video:\n{entry.VideoLink}",
                "Replace processes the new video and, with upload ON, assigns it in place of the current one.",
                new Choice("CANCEL", "cancel", IsCancel: true),
                new Choice("SKIP", "skip"),
                new Choice("REPLACE", "replace", ChoiceStyle.Danger));

            if (choice == "skip")
            {
                _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: skipped; it already has a video.");
                SelectNextEntryAfter(entry);
                return;
            }
            if (choice != "replace")
                return;
            _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: operator chose to replace the existing video.");
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Select the video for cart {entry.CardNumber}",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.m4v;*.ts;*.mts;*.m2ts|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(Settings.InputVideoFolder) ? Settings.InputVideoFolder : null
        };

        if (dialog.ShowDialog() != true)
            return;

        // The file dialog is modal, but a refresh or race change could still have landed.
        if (IsBusy || !ReferenceEquals(SelectedEntry, entry) || entry.Scope != CurrentScope)
            return;

        var input = dialog.FileName;
        var output = OutputPathFor(race, entry.Scope, input);
        var allowOverwrite = Settings.AllowOverwriteExistingOutput ||
                             string.Equals(output, entry.OutputPath, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(output) && !allowOverwrite)
        {
            var answer = MessageBox.Show(
                $"A processed file already exists:\n\n{output}\n\nReplace it once the new video has been processed and validated?",
                "Output already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
            allowOverwrite = true;
        }

        // A newly selected video always starts from processing, whatever happened before.
        var state = entry.LocalState.Clone();
        state.LocalVideoPath = input;
        state.ProcessingStatus = LocalProcessingStatus.Ready;
        state.UploadStatus = UploadStatus.NotStarted;
        state.UploadedVideoLink = null;
        state.AssignmentStatus = AssignmentStatus.NotStarted;
        state.LastFailureWasAuthentication = false;
        state.ErrorMessage = null;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
        entry.ReplaceLocalState(state);
        _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: video selected: {input}");

        await RunJobAsync(entry, race, input, output, allowOverwrite, isRecovery: false);
    }

    private async Task RetryAsync()
    {
        var entry = SelectedEntry;
        var race = ActiveRace;
        if (entry is null || race is null || IsBusy || entry.RetryLabel is not { } label)
            return;

        _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: {label.ToLowerInvariant()} requested.");

        if (label == "RETRY PROCESSING")
        {
            var input = entry.LocalVideoPath!;
            var state = entry.LocalState.Clone();
            state.ProcessingStatus = LocalProcessingStatus.Ready;
            state.UploadStatus = UploadStatus.NotStarted;
            state.UploadedVideoLink = null;
            state.AssignmentStatus = AssignmentStatus.NotStarted;
            state.ErrorMessage = null;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
            entry.ReplaceLocalState(state);
            await RunJobAsync(entry, race, input, OutputPathFor(race, entry.Scope, input), allowOverwrite: true, isRecovery: false);
            return;
        }

        // Upload or assignment: completed stages are skipped, so a failed PATCH is
        // retried with the link already obtained, without uploading again.
        await RunJobAsync(entry, race, entry.LocalVideoPath, entry.OutputPath ?? string.Empty, allowOverwrite: true, isRecovery: false);
    }

    /// <summary>
    /// Resumes a stage that was interrupted by a crash, one entry at a time, for
    /// the race and category on screen. Only when idle and upload is ON.
    /// </summary>
    private async Task TryResumeInterruptedAsync()
    {
        var race = ActiveRace;
        if (IsBusy || race is null || !Settings.UploadEnabled)
            return;

        var entry = Entries.FirstOrDefault(e =>
            WorkflowRecovery.NeedsAutomaticResume(e.LocalState, Settings.UploadEnabled) &&
            _resumeAttempted.Add($"{e.Scope.RaceId}|{e.Scope.Category}|{e.EntryId}"));
        if (entry is null)
            return;

        _log.Info($"Recovery: resuming [{entry.Scope}] cart {entry.CardNumber} from " +
                  (entry.UploadStatus == UploadStatus.Completed ? "assignment, reusing the uploaded link." : "upload, reusing the processed file."));
        await RunJobAsync(entry, race, entry.LocalVideoPath, entry.OutputPath ?? string.Empty, allowOverwrite: true, isRecovery: true);
    }

    private async Task RunJobAsync(
        EntryItemViewModel entry, Race race, string? input, string output, bool allowOverwrite, bool isRecovery)
    {
        var scope = entry.Scope;
        var job = new WorkflowJob(
            scope,
            entry.Entry,
            input,
            output,
            ToOverlay(entry.Entry),
            OverlayHashCalculator.Calculate(entry.Entry),
            allowOverwrite,
            Settings.UploadEnabled);

        _job = new ActiveJob(scope, entry.EntryId, entry.CardNumber, entry.PrimaryName, isRecovery);
        _jobCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        ProcessingFile = Path.GetFileName(input ?? output);
        ProcessingElapsed = "00:00";
        ProcessingEta = "—";
        JobPercent = 0;
        JobPercentKnown = true;
        RefreshWorkContext();

        var loggedBucket = 0;
        var progress = new Progress<WorkflowProgress>(p =>
        {
            if (_job is null)
                return;

            if (_job.Stage != p.Stage)
            {
                _job.Stage = p.Stage;
                loggedBucket = 0;
                RefreshWorkContext();
            }

            JobPercentKnown = p.Percent.HasValue;
            JobPercent = p.Percent ?? 0;
            OnPropertyChanged(nameof(UploadProgressText));

            if (p.Processing is { } processing)
            {
                ProcessingElapsed = FormatShortDuration(processing.Elapsed);
                ProcessingEta = processing.EstimatedRemaining.HasValue ? FormatShortDuration(processing.EstimatedRemaining.Value) : "—";
            }

            var bucket = Math.Min(100, (int)JobPercent / 20 * 20);
            if (p.Percent.HasValue && bucket >= 20 && bucket > loggedBucket)
            {
                loggedBucket = bucket;
                _log.Info($"[{scope}] cart {job.Entry.CardNumber} {StageName(p.Stage).ToLowerInvariant()}: {bucket}%");
            }

            FindEntry(scope, job.Entry.EntryId)?.ReplaceLocalState(p.State);
        });

        WorkflowResult result;
        try
        {
            result = await _workflow.RunAsync(job, entry.LocalState, progress, _jobCts.Token);
        }
        catch (Exception ex)
        {
            _log.Error($"[{scope}] cart {job.Entry.CardNumber}: unexpected job error: {ex}");
            ShowBanner(BannerKind.Error, $"Cart {job.Entry.CardNumber}: unexpected error. {ex.Message}");
            return;
        }
        finally
        {
            _job = null;
            _jobCts?.Dispose();
            _jobCts = null;
            RefreshWorkContext();
        }

        if (_lifetimeCts.IsCancellationRequested)
            return;

        var current = FindEntry(scope, job.Entry.EntryId);
        current?.ReplaceLocalState(result.State);
        RebuildVisibleEntries();
        RefreshWorkContext();

        var card = $"Cart {job.Entry.CardNumber}";
        switch (result.Outcome)
        {
            case WorkflowOutcome.Completed:
                ShowBanner(BannerKind.Success, $"{card} completed: processed, uploaded and assigned.");
                if (!isRecovery && current is not null && scope == CurrentScope)
                    SelectNextEntryAfter(current);
                break;
            case WorkflowOutcome.UploadDisabled:
                ShowBanner(BannerKind.Success, $"{card}: Processing Completed. Upload is OFF, so nothing was uploaded or assigned.");
                break;
            case WorkflowOutcome.ProcessingCancelled:
                ShowBanner(BannerKind.Info, $"{card}: Processing Cancelled. Nothing was uploaded; the original video is untouched.");
                break;
            case WorkflowOutcome.AuthenticationFailed:
                ShowBanner(BannerKind.Error, $"{card}: {result.Error} Check the login email and password in Settings, then retry.");
                break;
            case WorkflowOutcome.Interrupted:
                break;
            default:
                ShowBanner(BannerKind.Error, $"{card}: {result.Error}");
                break;
        }

        await TryResumeInterruptedAsync();
    }

    /// <summary>
    /// Moves to the next eligible entry of the same race and category, and waits
    /// there: the operator must select a new video. Nothing carries over.
    /// </summary>
    private void SelectNextEntryAfter(EntryItemViewModel entry)
    {
        var next = EntryOrdering.FindNext(Entries, entry, e => e.IsEligibleForNext);
        if (next is null)
        {
            ShowBanner(BannerKind.Info, $"No pending entries left in {SelectedCategoryLabel}.");
            return;
        }

        if (!VisibleEntries.Contains(next))
        {
            SearchText = string.Empty;
            Filter = EntryFilter.All;
        }

        SelectedEntry = next;
        _log.Info($"[{next.Scope}] next entry selected: cart {next.CardNumber}. Waiting for its video.");
    }

    private void CancelProcessing()
    {
        if (_job is { Stage: WorkflowStage.Processing } && _jobCts is { IsCancellationRequested: false })
        {
            _log.Info($"[{_job.Scope}] cart {_job.CardNumber}: cancel requested.");
            _jobCts.Cancel();
        }
    }

    private EntryItemViewModel? FindEntry(RaceScope scope, string entryId)
        => scope != CurrentScope
            ? null
            : Entries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.OrdinalIgnoreCase));

    private bool IsJobEntry(EntryItemViewModel entry)
        => _job is not null && _job.Scope == entry.Scope &&
           string.Equals(_job.EntryId, entry.EntryId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Processed files are grouped per race and category, so the same file name
    /// in two races never overwrites the other race's output.
    /// </summary>
    private string OutputPathFor(Race race, RaceScope scope, string input)
    {
        var raceFolder = SafeFolderName($"{race.RaceDate:yyyy-MM-dd} {race.RaceName} [{race.RaceId}]");
        var categoryFolder = scope.Category == RaceCategory.Meter300 ? "300m" : "200m";
        return Path.Combine(Settings.OutputVideoFolder, raceFolder, categoryFolder, Path.GetFileName(input));
    }

    private static string SafeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "race" : cleaned;
    }

    // ---- Preview -----------------------------------------------------------

    private async Task PreviewAsync()
    {
        var entry = SelectedEntry;
        if (entry?.LocalVideoPath is not string input || !File.Exists(input))
            return;

        try
        {
            var preview = await _videoProcessing.GeneratePreviewAsync(input, ToOverlay(entry.Entry), _lifetimeCts.Token);
            var window = new PreviewWindow(preview, entry.CardNumber)
            {
                Owner = Application.Current?.MainWindow
            };
            window.ShowDialog();
            TryDeleteDirectory(Path.GetDirectoryName(preview.NormalPreviewPath));
        }
        catch (Exception ex)
        {
            _log.Error($"Cart {entry.CardNumber}: preview failed: {ex.Message}");
            ShowBanner(BannerKind.Error, $"Cart {entry.CardNumber}: preview failed. {ex.Message}");
        }
    }

    // ---- Demo --------------------------------------------------------------

    private async Task SimulateNextEntryAsync()
    {
        await _demoController.SimulateNextEntryAsync(_lifetimeCts.Token);
        _log.Info($"Demo: released card {_demoController.LastReleasedCardNumber}.");
        await _polling.PollNowAsync(_lifetimeCts.Token);
    }

    private async Task ResetDemoAsync()
    {
        var answer = MessageBox.Show(
            "Reset the demo arrival cursor to the first card? Local processing history is kept.",
            "Reset demo", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        await _demoController.ResetAsync(_lifetimeCts.Token);
        await _polling.PollNowAsync(_lifetimeCts.Token);
    }

    // ---- Output and folders ------------------------------------------------

    private void OpenOutput()
    {
        var path = SelectedEntry?.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKind.Error, "Could not open the folder: " + ex.Message);
        }
    }

    private void OpenLogFile()
    {
        try
        {
            if (File.Exists(_log.LogFilePath))
                Process.Start(new ProcessStartInfo(_log.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKind.Error, "Could not open the log file: " + ex.Message);
        }
    }

    // ---- Settings ----------------------------------------------------------

    private async Task SaveSettingsAsync()
    {
        Settings.Normalize();
        Directory.CreateDirectory(Settings.InputVideoFolder);
        Directory.CreateDirectory(Settings.OutputVideoFolder);
        Directory.CreateDirectory(Settings.DemoVideoFolder);
        await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);

        ResolveFont();
        foreach (var entry in Entries)
            entry.ReplaceLocalState(entry.LocalState);

        // Normalize() clamps values, so re-notify to show what was actually stored.
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsDemoMode));
        OnPropertyChanged(nameof(ModeLabel));
        RefreshWorkContext();
        _log.Info("Settings saved.");
        if (HasActiveRace)
            await _polling.PollNowAsync(_lifetimeCts.Token);
        ShowBanner(BannerKind.Success, "Settings saved.");
    }

    private async Task TestLoginAsync()
    {
        if (IsDemoMode)
        {
            ShowBanner(BannerKind.Info, "The demo provider is active; it needs no login. Switch the data source to RealApi to test.");
            return;
        }
        var result = await _backendDiagnostics.TestLoginAsync(_lifetimeCts.Token);
        ShowBanner(result.Success ? BannerKind.Success : BannerKind.Error, result.Success ? result.Detail : "Authentication Failed: " + result.Detail);
    }

    private async Task TestFfmpegAsync()
    {
        var result = await _toolHealth.TestFfmpegAsync(_lifetimeCts.Token);
        ShowBanner(result.Success ? BannerKind.Success : BannerKind.Error, result.Detail);
    }

    private async Task TestNvencAsync()
    {
        var result = await _encoderCapability.TestNvencAsync(_lifetimeCts.Token);
        EncoderDisplay = result.Success ? "NVIDIA NVENC" : "CPU / x264";
        ShowBanner(result.Success ? BannerKind.Success : BannerKind.Warning, result.Detail);
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>Re-evaluates everything derived from the selection, the job and the context.</summary>
    private void RefreshWorkContext()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsUploading));
        OnPropertyChanged(nameof(ProcessingSummary));
        OnPropertyChanged(nameof(JobPercentText));
        OnPropertyChanged(nameof(CurrentCardText));
        OnPropertyChanged(nameof(CurrentNameText));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(UploadEnabled));
        OnPropertyChanged(nameof(UploadProgressText));
        OnPropertyChanged(nameof(NextEntry));
        OnPropertyChanged(nameof(HasNextEntry));
        OnPropertyChanged(nameof(SelectVideoBlockReason));
        OnPropertyChanged(nameof(PreviewBlockReason));
        OnPropertyChanged(nameof(RetryBlockReason));
        OnPropertyChanged(nameof(RetryLabel));
        OnPropertyChanged(nameof(OpenOutputBlockReason));
        OnPropertyChanged(nameof(EmptyListText));
        RefreshRaceActionAvailability();
        CommandManager.InvalidateRequerySuggested();
    }

    private OverlayData ToOverlay(RaceEntry entry) => new(
        entry.PrimaryName,
        entry.PrimaryLocation,
        entry.CardNumber,
        entry.SecondaryName,
        entry.SecondaryLocation,
        TimingFormatter.Format(entry.TimingSeconds, Settings.TimingFormat));

    private static string StageName(WorkflowStage stage) => stage switch
    {
        WorkflowStage.Uploading => "Uploading",
        WorkflowStage.Assigning => "Assigning",
        _ => "Processing"
    };

    private static string ToTitle(string upper)
        => string.Join(' ', upper.Split(' ').Select(w => w.Length <= 1 ? w : w[0] + w[1..].ToLowerInvariant()));

    private static string FormatShortDuration(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preview scratch files are disposable.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _polling.SnapshotUpdated -= OnSnapshotUpdated;
        _polling.StatusUpdated -= OnPollStatusUpdated;
        _log.LineWritten -= OnLogLineWritten;
        _lifetimeCts.Cancel();
        await _polling.DisposeAsync();
        _jobCts?.Dispose();
        _lifetimeCts.Dispose();
    }
}
