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
///   2. Selecting a video starts the job in the mode set by the Processing and
///      Upload toggles: process + upload, process only, direct upload, or keep the
///      selection. There is no start button, and only one job runs at a time.
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

    /// <summary>The completed entry the operator is left on when no next entry exists yet; a new API entry moves on from it.</summary>
    private EntryItemViewModel? _awaitingNextAfter;
    private CancellationTokenSource? _jobCts;
    private string _processingFile = "—";
    private double _jobPercent;
    private bool _jobPercentKnown = true;
    private string _processingElapsed = "00:00";
    private string _processingEta = "—";
    private string _jobBytesText = string.Empty;

    private string _bannerText = string.Empty;
    private BannerKind _bannerSeverity = BannerKind.Info;

    private sealed record ActiveJob(RaceScope Scope, string EntryId, string CardNumber, string PrimaryName, bool IsRecovery,
        bool ProcessingEnabled, bool UploadEnabled)
    {
        public WorkflowStage Stage { get; set; } = WorkflowStage.Processing;
    }

    public MainViewModel(
        AppSettings settings,
        ILocalStateRepository repository,
        IDemoEntryController demoController,
        PollingCoordinator polling,
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
        ClearVideoCommand = new AsyncRelayCommand(ClearVideoAsync, () => ClearVideoBlockReason.Length == 0);
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
        ZoomInCommand = new AsyncRelayCommand(() => SetZoomAsync(+1), () => UiZoom < AppSettings.ZoomLevels[^1]);
        ZoomOutCommand = new AsyncRelayCommand(() => SetZoomAsync(-1), () => UiZoom > AppSettings.ZoomLevels[0]);
        ResetZoomCommand = new AsyncRelayCommand(() => SetZoomAsync(0));
        NavigateCommand = new RelayCommand<string>(page =>
        {
            if (!Enum.TryParse<AppPage>(page, ignoreCase: true, out var parsed))
                return;
            // Choosing the page already open returns to the work area.
            ActivePage = parsed == ActivePage ? AppPage.Work : parsed;
            if (ActivePage == AppPage.Database)
                _ = RefreshDatabaseAsync();
            if (ActivePage == AppPage.Races)
                _ = RefreshRaceCountsAsync();
        });
        SetSettingsSectionCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<SettingsSection>(name, ignoreCase: true, out var parsed))
                ActiveSettingsSection = parsed;
        });
        SetFilterCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<EntryFilter>(name, ignoreCase: true, out var parsed))
                Filter = parsed;
        });
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        DismissBannerCommand = new RelayCommand(() => BannerText = string.Empty);
        ClearLogsCommand = new RelayCommand(() => { Logs.Clear(); RebuildVisibleLogs(); });
        RefreshLogsCommand = new RelayCommand(LoadRecentLogs);
        ExportLogsCommand = new RelayCommand(ExportLogs);
        SetLogLevelCommand = new RelayCommand<string>(level => LogLevelFilter = level ?? "All");
        OpenLogFileCommand = new RelayCommand(OpenLogFile);
        RefreshDatabaseCommand = new AsyncRelayCommand(RefreshDatabaseAsync);
        BackupDatabaseCommand = new AsyncRelayCommand(BackupDatabaseAsync);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(Path.GetDirectoryName(_log.LogFilePath) ?? string.Empty));

        InitializeRaceManagement();
    }

    // ---- Collections -------------------------------------------------------

    public AppSettings Settings { get; }

    /// <summary>Entries of the selected race and category, newest API date first.</summary>
    public ObservableCollection<EntryItemViewModel> Entries { get; } = [];

    /// <summary>What the list actually shows after search and filter.</summary>
    public ObservableCollection<EntryItemViewModel> VisibleEntries { get; } = [];

    /// <summary>Every log line loaded or written this session (most recent 2000).</summary>
    public List<LogEntry> Logs { get; } = [];

    /// <summary>What the Logs page shows after the level filter and search.</summary>
    public ObservableCollection<LogEntry> VisibleLogs { get; } = [];

    public IReadOnlyList<DataSourceMode> DataSourceModes { get; } = Enum.GetValues<DataSourceMode>();
    public IReadOnlyList<int> PollingIntervals { get; } = [10, 20, 30, 60];
    public IReadOnlyList<EncoderPreference> EncoderPreferences { get; } = Enum.GetValues<EncoderPreference>();
    public IReadOnlyList<EncodingQuality> EncodingQualities { get; } = Enum.GetValues<EncodingQuality>();

    // ---- Commands ----------------------------------------------------------

    public ICommand SelectVideoCommand { get; }
    public ICommand ClearVideoCommand { get; }
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
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand SetSettingsSectionCommand { get; }
    public ICommand RefreshLogsCommand { get; }
    public ICommand ExportLogsCommand { get; }
    public ICommand SetLogLevelCommand { get; }
    public ICommand RefreshDatabaseCommand { get; }
    public ICommand BackupDatabaseCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
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
            if (!ReferenceEquals(value, _awaitingNextAfter))
                _awaitingNextAfter = null;
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

    public int CountAll => Entries.Count;
    public int CountUploaded => Entries.Count(e => e.FilterBucket == EntryFilter.Uploaded);
    public int CountFailed => Entries.Count(e => e.FilterBucket == EntryFilter.Failed);
    public int CountReadyToUpload => Entries.Count(e => e.FilterBucket == EntryFilter.ReadyToUpload);
    public int CountProcessing => Entries.Count(e => e.FilterBucket == EntryFilter.Processing);

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

    /// <summary>
    /// Processing ON: selected videos go through FFmpeg for the scoreboard.
    /// OFF: the selected file is used as it is. Applies to the next operation; a
    /// running one keeps the mode it started with.
    /// </summary>
    public bool ProcessingEnabled
    {
        get => Settings.ProcessingEnabled;
        set => _ = SetWorkflowToggleAsync(processing: value, upload: Settings.UploadEnabled);
    }

    /// <summary>Upload ON: the video is uploaded and assigned. Independent of processing; applies to the next operation.</summary>
    public bool UploadEnabled
    {
        get => Settings.UploadEnabled;
        set => _ = SetWorkflowToggleAsync(processing: Settings.ProcessingEnabled, upload: value);
    }

    /// <summary>What selecting a video will do now, in words.</summary>
    public string WorkflowModeText => WorkflowModes.From(Settings.ProcessingEnabled, Settings.UploadEnabled) switch
    {
        WorkflowMode.ProcessAndUpload => "Select video → process → upload → assign → next entry",
        WorkflowMode.ProcessOnly => "Select video → process → keep the processed file (upload is OFF)",
        WorkflowMode.DirectUpload => "Select video → upload it directly, no processing → assign → next entry",
        _ => "Select video → keep the selection (processing and upload are OFF)"
    };

    private async Task SetWorkflowToggleAsync(bool processing, bool upload)
    {
        var processingChanged = processing != Settings.ProcessingEnabled;
        var uploadChanged = upload != Settings.UploadEnabled;
        if (!processingChanged && !uploadChanged)
            return;

        Settings.ProcessingEnabled = processing;
        Settings.UploadEnabled = upload;
        await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);

        if (processingChanged)
            _log.Info("Processing switched " + (processing ? "ON." : "OFF — selected videos are used as they are; FFmpeg is not run."));
        if (uploadChanged)
            _log.Info("Upload switched " + (upload ? "ON." : "OFF — nothing is uploaded or assigned."));
        if (IsBusy)
            ShowBanner(BannerKind.Info, "The change applies to the next video. The operation already running keeps the settings it started with.");

        OnPropertyChanged(nameof(ProcessingEnabled));
        OnPropertyChanged(nameof(UploadEnabled));
        OnPropertyChanged(nameof(WorkflowModeText));
        RefreshWorkContext();
        if (uploadChanged && upload)
            await TryResumeInterruptedAsync();
    }

    // ---- Zoom --------------------------------------------------------------

    /// <summary>Scale of the whole interface. The video and its scoreboard are never affected.</summary>
    public double UiZoom => Settings.UiZoom;
    public bool IsZoomChanged => Math.Abs(Settings.UiZoom - 1.0) > 0.001;

    private async Task SetZoomAsync(int direction)
    {
        var levels = AppSettings.ZoomLevels;
        var index = Array.IndexOf(levels, Settings.UiZoom);
        if (index < 0)
            index = Array.IndexOf(levels, 1.0);
        var target = direction == 0 ? 1.0 : levels[Math.Clamp(index + direction, 0, levels.Length - 1)];
        if (Math.Abs(target - Settings.UiZoom) < 0.001)
            return;

        Settings.UiZoom = target;
        OnPropertyChanged(nameof(UiZoom));
        OnPropertyChanged(nameof(IsZoomChanged));
        CommandManager.InvalidateRequerySuggested();
        await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);
    }

    // ---- Settings sections -------------------------------------------------

    private SettingsSection _activeSettingsSection = SettingsSection.General;

    public SettingsSection ActiveSettingsSection
    {
        get => _activeSettingsSection;
        set => SetProperty(ref _activeSettingsSection, value);
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

    /// <summary>The scoreboard's own typefaces, bundled with the application.</summary>
    public string ScoreboardFontText => RaceVideoProcessor.Infrastructure.Video.ScoreboardFonts.Bundled() is not null
        ? "Orbitron 700 and Noto Sans Tamil 500, bundled — the fonts of the scoreboard design"
        : "Missing from the installation — the fallback font below is used. Reinstall to restore them.";

    public string FontDisplay { get; private set; } = "Resolving…";
    public string FontWarning { get; private set; } = string.Empty;
    public bool HasFontWarning => FontWarning.Length > 0;

    // ---- Job status (top of the workspace) ---------------------------------

    /// <summary>A job is running: processing, uploading or assigning.</summary>
    public bool IsBusy => _job is not null;

    /// <summary>The running job is in its processing stage — the only stage that can be cancelled.</summary>
    public bool IsProcessing => _job is { Stage: WorkflowStage.Processing };

    public bool IsUploading => _job is { Stage: WorkflowStage.Uploading };

    /// <summary>The running job skips FFmpeg and uploads the selected file itself.</summary>
    public bool IsDirectJob => _job is { ProcessingEnabled: false };

    /// <summary>Headline of the Processing section. Never a progress claim that is not real.</summary>
    public string ProcessingStateText => _job switch
    {
        { Stage: WorkflowStage.Processing } => "Processing video…",
        { ProcessingEnabled: false } => "Processing skipped — direct upload mode",
        not null => "Processing completed",
        _ when CurrentEntry is { ProcessingStatus: LocalProcessingStatus.Skipped } => "Processing disabled for this video",
        _ when !Settings.ProcessingEnabled => "Processing Disabled",
        _ => CurrentEntry?.ProcessingStageText ?? "—"
    };

    public string ProcessingStageDetail => _job is { Stage: WorkflowStage.Processing }
        ? "Adding scoreboard and encoding…"
        : !Settings.ProcessingEnabled && _job is null
            ? "Selected videos are used as they are. FFmpeg is not run."
            : string.Empty;

    public string ProcessingFile { get => _processingFile; private set => SetProperty(ref _processingFile, value); }
    public string ProcessingElapsed { get => _processingElapsed; private set => SetProperty(ref _processingElapsed, value); }
    public string ProcessingEta { get => _processingEta; private set => SetProperty(ref _processingEta, value); }

    /// <summary>"67.0 MB / 100.0 MB" while uploading; empty otherwise.</summary>
    public string JobBytesText { get => _jobBytesText; private set => SetProperty(ref _jobBytesText, value); }

    /// <summary>The video file at the top of the workspace: the running job's, else the selected entry's.</summary>
    public string SelectedVideoText => _job is not null
        ? ProcessingFile
        : SelectedEntry is { HasLocalVideo: true } entry ? entry.LocalVideoFileName : "No video selected";

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

    public string StatusBarText => _job is null ? "Ready" : $"{StageName(_job.Stage)} · Cart {_job.CardNumber}";

    public string AppVersion => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>Shown as "Current": the entry being worked on, else the selected entry.</summary>
    /// <summary>The entry being worked on — the running job's — else the selected entry. The bottom of the workspace shows it.</summary>
    public EntryItemViewModel? CurrentEntry => _job is not null && _job.Scope == CurrentScope
        ? Entries.FirstOrDefault(e => string.Equals(e.EntryId, _job.EntryId, StringComparison.OrdinalIgnoreCase)) ?? SelectedEntry
        : SelectedEntry;

    public bool HasCurrentEntry => CurrentEntry is not null;

    public bool IsAssigning => _job is { Stage: WorkflowStage.Assigning };

    /// <summary>Upload state for the strip under the entry: live while uploading, else the entry's stored stages.</summary>
    public string UploadStateText => _job switch
    {
        { Stage: WorkflowStage.Uploading, ProcessingEnabled: false } => "Uploading the selected video directly…",
        { Stage: WorkflowStage.Uploading } => "Uploading to server…",
        { Stage: WorkflowStage.Assigning } => "Assigning the video to the player…",
        { UploadEnabled: false } => "Upload Disabled",
        not null => "Waiting for processing",
        _ when CurrentEntry is { UploadStatus: UploadStatus.Disabled } => "Upload Disabled",
        _ when !Settings.UploadEnabled => "Upload Disabled",
        _ => CurrentEntry is null ? "—" : $"{CurrentEntry.UploadStageText} · {CurrentEntry.AssignmentStageText}"
    };

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
            if (label != EntryItemViewModel.RetryProcessingLabel && !Settings.UploadEnabled) return "Upload is OFF. Switch it ON to upload.";
            return string.Empty;
        }
    }

    public string RetryLabel => SelectedEntry?.RetryLabel ?? "Retry";

    public string ClearVideoBlockReason
    {
        get
        {
            if (SelectedEntry is not { HasLocalVideo: true } entry) return "No video is selected.";
            if (IsJobEntry(entry)) return "The video is in use by the running operation.";
            if (entry.UploadStatus == UploadStatus.Completed) return "This video is already uploaded.";
            return string.Empty;
        }
    }

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

        // The last workflow finished with nothing left to do; a new entry from the
        // API is where the operator goes next, without a restart or reselection.
        if (_awaitingNextAfter is { } finished && !IsBusy && ReferenceEquals(SelectedEntry, finished) &&
            EntryOrdering.FindNext(Entries, finished, e => e.IsEligibleForNext) is { } next)
        {
            _awaitingNextAfter = null;
            SelectEntry(next);
            ShowBanner(BannerKind.Info, $"New entry received: cart {next.CardNumber} is selected. Select its video.");
        }

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
            var entry = LogEntry.Parse(line);
            Logs.Add(entry);
            if (Logs.Count > 2000)
            {
                Logs.RemoveAt(0);
                if (VisibleLogs.Count > 0 && ReferenceEquals(VisibleLogs[0], Logs[0]))
                    VisibleLogs.RemoveAt(0);
            }
            if (MatchesLogFilter(entry))
                VisibleLogs.Add(entry);
            OnPropertyChanged(nameof(LogCountText));
        });

    private void LoadRecentLogs()
    {
        Logs.Clear();
        try
        {
            if (File.Exists(_log.LogFilePath))
            {
                using var stream = new FileStream(_log.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new Queue<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > 2000)
                        lines.Dequeue();
                }
                Logs.AddRange(lines.Where(l => l.Length > 0).Select(LogEntry.Parse));
            }
        }
        catch
        {
            // The log view must never prevent startup.
        }
        RebuildVisibleLogs();
    }

    private string _logLevelFilter = "All";
    private string _logSearchText = string.Empty;

    public IReadOnlyList<string> LogLevels { get; } = ["All", "INFO", "WARNING", "ERROR"];

    public string LogLevelFilter
    {
        get => _logLevelFilter;
        set
        {
            if (SetProperty(ref _logLevelFilter, value))
                RebuildVisibleLogs();
        }
    }

    public string LogSearchText
    {
        get => _logSearchText;
        set
        {
            if (SetProperty(ref _logSearchText, value))
                RebuildVisibleLogs();
        }
    }

    public string LogCountText => VisibleLogs.Count == Logs.Count ? $"Total logs: {Logs.Count}" : $"Showing {VisibleLogs.Count} of {Logs.Count}";

    private bool MatchesLogFilter(LogEntry entry)
        => (LogLevelFilter == "All" || entry.Level == LogLevelFilter) &&
           (string.IsNullOrWhiteSpace(LogSearchText) ||
            entry.Message.Contains(LogSearchText.Trim(), StringComparison.OrdinalIgnoreCase) ||
            entry.Stage.Contains(LogSearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void RebuildVisibleLogs()
    {
        VisibleLogs.Clear();
        foreach (var entry in Logs.Where(MatchesLogFilter))
            VisibleLogs.Add(entry);
        OnPropertyChanged(nameof(LogCountText));
    }

    /// <summary>Exports the visible rows as CSV. Log lines never contain credentials or tokens.</summary>
    private void ExportLogs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export logs",
            Filter = "CSV file|*.csv",
            FileName = $"race-video-processor-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var lines = new List<string> { "Time,Level,Stage,Message" };
            lines.AddRange(VisibleLogs.Select(e => string.Join(',', Csv(e.Time), Csv(e.Level), Csv(e.Stage), Csv(e.Message))));
            File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _log.Info($"Logs exported: {VisibleLogs.Count} rows to {dialog.FileName}.");
            ShowBanner(BannerKind.Success, $"Exported {VisibleLogs.Count} log rows.");
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKind.Error, "Could not export the logs: " + ex.Message);
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
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountUploaded));
        OnPropertyChanged(nameof(CountFailed));
        OnPropertyChanged(nameof(CountReadyToUpload));
        OnPropertyChanged(nameof(CountProcessing));

        // Filtering the selected entry out of view must not clear the workspace:
        // the operator keeps working on it until they pick something else.
        if (previous is not null && Entries.Contains(previous))
        {
            _selectedEntry = null;
            SelectedEntry = previous;
        }
    }

    private bool MatchesFilter(EntryItemViewModel entry) => Filter == EntryFilter.All || entry.FilterBucket == Filter;

    // ---- Select video: starts the job --------------------------------------

    private async Task SelectVideoAsync()
    {
        var entry = SelectedEntry;
        var race = ActiveRace;
        if (entry is null || race is null || IsBusy)
            return;

        // Never silently overwrite a video that is already on the player.
        if (VideoEligibility.RequiresReplaceConfirmation(entry.VideoLink))
        {
            var choice = ChoiceDialog.Show(
                "Existing video",
                "This entry already has a video. Replace it?",
                $"Cart {entry.CardNumber} — {entry.PrimaryDisplay}\n\nCurrent video:\n{entry.VideoLink}",
                Settings.UploadEnabled
                    ? (Settings.ProcessingEnabled
                        ? "Replace processes the new video, uploads it and assigns it in place of the current one."
                        : "Replace uploads the new video directly, without processing, and assigns it in place of the current one.")
                    : "Upload is OFF, so the video on the player is not changed until the new one is uploaded.",
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

        // The toggles are read once, here: this operation keeps them to the end.
        var processing = Settings.ProcessingEnabled;
        var upload = Settings.UploadEnabled;
        var input = dialog.FileName;
        var output = processing ? OutputPathFor(race, entry.Scope, input) : string.Empty;
        var allowOverwrite = Settings.AllowOverwriteExistingOutput ||
                             string.Equals(output, entry.OutputPath, StringComparison.OrdinalIgnoreCase);
        if (processing && File.Exists(output) && !allowOverwrite)
        {
            var answer = MessageBox.Show(
                $"A processed file already exists:\n\n{output}\n\nReplace it once the new video has been processed and validated?",
                "Output already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
            allowOverwrite = true;
        }

        // A newly selected video always starts over, whatever happened before.
        var state = entry.LocalState.Clone();
        state.LocalVideoPath = input;
        state.ProcessingStatus = LocalProcessingStatus.Ready;
        state.Mode = WorkflowModes.From(processing, upload);
        state.OutputPath = processing ? state.OutputPath : null;
        state.UploadStatus = UploadStatus.NotStarted;
        state.UploadedVideoLink = null;
        state.AssignmentStatus = AssignmentStatus.NotStarted;
        state.LastFailureWasAuthentication = false;
        state.ErrorMessage = null;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
        entry.ReplaceLocalState(state);
        _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: video selected: {input} " +
                  $"(processing {(processing ? "ON" : "OFF")}, upload {(upload ? "ON" : "OFF")}).");

        await RunJobAsync(entry, race, input, output, allowOverwrite, isRecovery: false, processing, upload);
    }

    /// <summary>
    /// Forgets the selected video for an entry that has not been uploaded. Files on
    /// disk — the original and any processed output — are left where they are.
    /// </summary>
    private async Task ClearVideoAsync()
    {
        var entry = SelectedEntry;
        if (entry is null || ClearVideoBlockReason.Length > 0)
            return;

        var state = entry.LocalState.Clone();
        state.LocalVideoPath = null;
        state.OutputPath = null;
        state.Mode = null;
        state.ProcessingStatus = LocalProcessingStatus.VideoNotSelected;
        state.UploadStatus = UploadStatus.NotStarted;
        state.UploadedVideoLink = null;
        state.AssignmentStatus = AssignmentStatus.NotStarted;
        state.LastFailureWasAuthentication = false;
        state.ErrorMessage = null;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
        entry.ReplaceLocalState(state);
        _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: video selection cleared; no files were deleted.");
        RebuildVisibleEntries();
        RefreshWorkContext();
    }

    private async Task RetryAsync()
    {
        var entry = SelectedEntry;
        var race = ActiveRace;
        if (entry is null || race is null || IsBusy || entry.RetryLabel is not { } label)
            return;

        _log.Info($"[{entry.Scope}] cart {entry.CardNumber}: {label.ToLowerInvariant()} requested.");

        if (label == EntryItemViewModel.RetryProcessingLabel)
        {
            var input = entry.LocalVideoPath!;
            var state = entry.LocalState.Clone();
            state.ProcessingStatus = LocalProcessingStatus.Ready;
            state.Mode = WorkflowModes.From(true, Settings.UploadEnabled);
            state.UploadStatus = UploadStatus.NotStarted;
            state.UploadedVideoLink = null;
            state.AssignmentStatus = AssignmentStatus.NotStarted;
            state.ErrorMessage = null;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
            entry.ReplaceLocalState(state);
            await RunJobAsync(entry, race, input, OutputPathFor(race, entry.Scope, input), allowOverwrite: true,
                isRecovery: false, processingEnabled: true, uploadEnabled: Settings.UploadEnabled);
            return;
        }

        if (label == EntryItemViewModel.UploadNowLabel)
        {
            var state = entry.LocalState.Clone();
            state.UploadStatus = UploadStatus.NotStarted;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
            entry.ReplaceLocalState(state);
        }

        // Upload or assignment, from the source the operation used: the processed
        // file, or the selected file when processing was OFF. Completed stages are
        // skipped, so a failed PATCH is retried without uploading again, and a
        // direct upload is never turned into processing.
        await RunJobAsync(entry, race, entry.LocalVideoPath, entry.OutputPath ?? string.Empty, allowOverwrite: true,
            isRecovery: false, processingEnabled: !entry.LocalState.IsDirect, uploadEnabled: true);
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
                  (entry.UploadStatus == UploadStatus.Completed ? "assignment, reusing the uploaded link."
                      : entry.LocalState.IsDirect ? "direct upload of the selected video." : "upload, reusing the processed file."));
        await RunJobAsync(entry, race, entry.LocalVideoPath, entry.OutputPath ?? string.Empty, allowOverwrite: true,
            isRecovery: true, processingEnabled: !entry.LocalState.IsDirect, uploadEnabled: true);
    }

    private async Task RunJobAsync(
        EntryItemViewModel entry, Race race, string? input, string output, bool allowOverwrite, bool isRecovery,
        bool processingEnabled, bool uploadEnabled)
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
            uploadEnabled,
            processingEnabled);

        _awaitingNextAfter = null;
        _job = new ActiveJob(scope, entry.EntryId, entry.CardNumber, entry.PrimaryName, isRecovery, processingEnabled, uploadEnabled)
        {
            Stage = processingEnabled ? WorkflowStage.Processing : WorkflowStage.Uploading
        };
        _jobCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        ProcessingFile = Path.GetFileName(string.IsNullOrWhiteSpace(input) ? output : input);
        ProcessingElapsed = "00:00";
        ProcessingEta = "—";
        JobPercent = 0;
        JobPercentKnown = true;
        JobBytesText = string.Empty;
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
    
            JobBytesText = p.Upload is { TotalBytes: > 0 } upload
                ? $"{upload.BytesSent / 1048576d:0.0} MB / {upload.TotalBytes.Value / 1048576d:0.0} MB"
                : string.Empty;

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
                ShowBanner(BannerKind.Success, processingEnabled
                    ? $"{card} completed: processed, uploaded and assigned."
                    : $"{card} completed: uploaded directly without processing, and assigned.");
                if (!isRecovery && current is not null && scope == CurrentScope)
                {
                    // Pick up entries that arrived while this one was running before choosing the next.
                    await _polling.PollNowAsync(_lifetimeCts.Token);
                    if (!IsBusy && ReferenceEquals(SelectedEntry, current))
                        SelectNextEntryAfter(current);
                }
                break;
            case WorkflowOutcome.UploadDisabled:
                ShowBanner(BannerKind.Success, $"{card}: Processing Completed · Upload Disabled. The processed file is kept: {result.State.OutputPath}");
                break;
            case WorkflowOutcome.SelectionOnly:
                ShowBanner(BannerKind.Info, $"{card}: Processing Disabled · Upload Disabled. The video stays selected; nothing was processed or uploaded.");
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
            // Stay here; the next entry the API sends is selected automatically.
            _awaitingNextAfter = entry;
            ShowBanner(BannerKind.Info, $"No pending entries left in {SelectedCategoryLabel}. The next new entry will be selected as soon as it arrives.");
            return;
        }

        SelectEntry(next);
        _log.Info($"[{next.Scope}] next entry selected: cart {next.CardNumber}. Waiting for its video.");
    }

    private void SelectEntry(EntryItemViewModel entry)
    {
        if (!VisibleEntries.Contains(entry))
        {
            SearchText = string.Empty;
            Filter = EntryFilter.All;
        }
        SelectedEntry = entry;
    }

    private void CancelProcessing()
    {
        // Only FFmpeg can be cancelled; an upload or assignment always runs to its result.
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
        OnPropertyChanged(nameof(SelectedVideoText));
        OnPropertyChanged(nameof(CurrentEntry));
        OnPropertyChanged(nameof(HasCurrentEntry));
        OnPropertyChanged(nameof(IsAssigning));
        OnPropertyChanged(nameof(UploadStateText));
        OnPropertyChanged(nameof(UploadEnabled));
        OnPropertyChanged(nameof(ProcessingEnabled));
        OnPropertyChanged(nameof(NextEntry));
        OnPropertyChanged(nameof(HasNextEntry));
        OnPropertyChanged(nameof(SelectVideoBlockReason));
        OnPropertyChanged(nameof(ClearVideoBlockReason));
        OnPropertyChanged(nameof(IsDirectJob));
        OnPropertyChanged(nameof(ProcessingStateText));
        OnPropertyChanged(nameof(ProcessingStageDetail));
        OnPropertyChanged(nameof(StatusBarText));
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

    private static string FormatShortDuration(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

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
