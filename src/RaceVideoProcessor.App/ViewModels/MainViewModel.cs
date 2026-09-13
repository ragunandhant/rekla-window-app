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
/// Two rules shape this class:
///   1. Selection is never gated. The operator can move between entries at any
///      time, whatever state any entry is in, and each entry keeps its own
///      mapped video, processing status, output and error.
///   2. A processing run is bound to the entry it started on, not to "the current
///      entry", so navigating away mid-render is safe and changes nothing.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILocalStateRepository _repository;
    private readonly IDemoEntryController _demoController;
    private readonly PollingCoordinator _polling;
    private readonly IVideoProcessingService _videoProcessing;
    private readonly IEncoderCapabilityService _encoderCapability;
    private readonly IApiConnectivityTester _apiTester;
    private readonly IToolHealthService _toolHealth;
    private readonly IFontResolver _fontResolver;
    private readonly IAppLog _log;
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>Cancels the running render only, leaving the application alive.</summary>
    private CancellationTokenSource? _jobCts;

    private EntryItemViewModel? _selectedEntry;
    private AppPage _activePage = AppPage.Work;
    private string _searchText = string.Empty;
    private EntryFilter _filter = EntryFilter.All;

    private string _apiStatusText = "STARTING";
    private string _apiErrorText = string.Empty;
    private string _lastSyncText = "—";
    private string _nextSyncText = "—";
    private bool _isApiConnected;
    private string _encoderDisplay = "Detecting…";

    private bool _isProcessing;
    private string _processingCardNumber = string.Empty;
    private string _processingFile = "—";
    private string _processingStage = "Idle";
    private double _processingPercent;
    private string _processingElapsed = "00:00";
    private string _processingEta = "—";

    private string _bannerText = string.Empty;
    private BannerKind _bannerSeverity = BannerKind.Info;

    public MainViewModel(
        AppSettings settings,
        ILocalStateRepository repository,
        IDemoEntryController demoController,
        PollingCoordinator polling,
        IVideoProcessingService videoProcessing,
        IEncoderCapabilityService encoderCapability,
        IApiConnectivityTester apiTester,
        IToolHealthService toolHealth,
        IFontResolver fontResolver,
        IAppLog log)
    {
        Settings = settings;
        _repository = repository;
        _demoController = demoController;
        _polling = polling;
        _videoProcessing = videoProcessing;
        _encoderCapability = encoderCapability;
        _apiTester = apiTester;
        _toolHealth = toolHealth;
        _fontResolver = fontResolver;
        _log = log;

        _polling.SnapshotUpdated += OnSnapshotUpdated;
        _polling.StatusUpdated += OnPollStatusUpdated;
        _log.LineWritten += OnLogLineWritten;

        SelectVideoCommand = new AsyncRelayCommand(SelectVideoAsync, () => SelectVideoBlockReason.Length == 0);
        ClearVideoCommand = new AsyncRelayCommand(ClearVideoAsync, () => SelectedEntry?.HasLocalVideo == true && !IsProcessing);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => PreviewBlockReason.Length == 0);
        StartProcessingCommand = new AsyncRelayCommand(() => ProcessAsync(force: false), () => StartProcessingBlockReason.Length == 0);
        ReprocessCommand = new AsyncRelayCommand(() => ProcessAsync(force: true), () => ReprocessBlockReason.Length == 0);
        CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => OpenOutputBlockReason.Length == 0);
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(Settings.OutputVideoFolder));
        OpenInputFolderCommand = new RelayCommand(() => OpenFolder(Settings.InputVideoFolder));
        OpenDemoVideoFolderCommand = new RelayCommand(() => OpenFolder(Settings.DemoVideoFolder));
        SimulateNextEntryCommand = new AsyncRelayCommand(SimulateNextEntryAsync, () => IsDemoMode);
        ResetDemoCommand = new AsyncRelayCommand(ResetDemoAsync, () => IsDemoMode);
        PollNowCommand = new AsyncRelayCommand(() => _polling.PollNowAsync(_lifetimeCts.Token));
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        TestApiCommand = new AsyncRelayCommand(TestApiAsync);
        TestFfmpegCommand = new AsyncRelayCommand(TestFfmpegAsync);
        TestNvencCommand = new AsyncRelayCommand(TestNvencAsync);
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
    }

    // ---- Collections -------------------------------------------------------

    public AppSettings Settings { get; }

    /// <summary>Everything known, in arrival order.</summary>
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
    public ICommand ClearVideoCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand StartProcessingCommand { get; }
    public ICommand ReprocessCommand { get; }
    public ICommand CancelProcessingCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand OpenInputFolderCommand { get; }
    public ICommand OpenDemoVideoFolderCommand { get; }
    public ICommand SimulateNextEntryCommand { get; }
    public ICommand ResetDemoCommand { get; }
    public ICommand PollNowCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand TestApiCommand { get; }
    public ICommand TestFfmpegCommand { get; }
    public ICommand TestNvencCommand { get; }
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
            if (!SetProperty(ref _selectedEntry, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            RefreshActionAvailability();
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

    // ---- Processing --------------------------------------------------------

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(ProcessingSummary));
                RefreshActionAvailability();
            }
        }
    }

    public string ProcessingCardNumber { get => _processingCardNumber; private set => SetProperty(ref _processingCardNumber, value); }
    public string ProcessingFile { get => _processingFile; private set => SetProperty(ref _processingFile, value); }
    public string ProcessingStage { get => _processingStage; private set => SetProperty(ref _processingStage, value); }
    public string ProcessingElapsed { get => _processingElapsed; private set => SetProperty(ref _processingElapsed, value); }
    public string ProcessingEta { get => _processingEta; private set => SetProperty(ref _processingEta, value); }

    public double ProcessingPercent
    {
        get => _processingPercent;
        private set
        {
            if (SetProperty(ref _processingPercent, value))
                OnPropertyChanged(nameof(ProcessingPercentText));
        }
    }

    public string ProcessingPercentText => $"{ProcessingPercent:0}%";

    public string ProcessingSummary => IsProcessing ? $"Processing {ProcessingCardNumber}" : "Idle";

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

    public string SelectVideoBlockReason => SelectedEntry is null
        ? "Select an entry first."
        : IsProcessing && IsProcessingSelected ? "This entry is processing." : string.Empty;

    public string PreviewBlockReason
    {
        get
        {
            if (SelectedEntry is null) return "Select an entry first.";
            if (!SelectedEntry.HasLocalVideo) return "Map a local video first.";
            if (!SelectedEntry.LocalVideoExists) return "The mapped video file is missing.";
            if (IsProcessing) return "Available once the current render finishes.";
            return string.Empty;
        }
    }

    public string StartProcessingBlockReason
    {
        get
        {
            if (SelectedEntry is null) return "Select an entry first.";
            if (IsProcessing) return $"{ProcessingCardNumber} is processing. One video at a time.";
            if (SelectedEntry.ExtractionStatus != RemoteExtractionStatus.Completed) return "Remote extraction is not completed yet.";
            if (!SelectedEntry.HasLocalVideo) return "Map a local video first.";
            if (!SelectedEntry.LocalVideoExists) return "The mapped video file is missing.";
            if (SelectedEntry.ProcessingStatus == LocalProcessingStatus.Completed) return "Already completed — use REPROCESS.";
            if (SelectedEntry.ProcessingStatus == LocalProcessingStatus.Outdated) return "Output is outdated — use REPROCESS.";
            return string.Empty;
        }
    }

    public string ReprocessBlockReason
    {
        get
        {
            if (SelectedEntry is null) return "Select an entry first.";
            if (IsProcessing) return $"{ProcessingCardNumber} is processing. One video at a time.";
            if (SelectedEntry.ExtractionStatus != RemoteExtractionStatus.Completed) return "Remote extraction is not completed yet.";
            if (!SelectedEntry.HasLocalVideo) return "Map a local video first.";
            if (!SelectedEntry.LocalVideoExists) return "The mapped video file is missing.";
            if (SelectedEntry.ProcessingStatus is not (LocalProcessingStatus.Completed
                or LocalProcessingStatus.Outdated or LocalProcessingStatus.Failed))
                return "Nothing has been rendered for this entry yet.";
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

    private bool IsProcessingSelected =>
        IsProcessing && SelectedEntry is not null &&
        string.Equals(SelectedEntry.CardNumber, ProcessingCardNumber, StringComparison.Ordinal);

    // ---- Startup -----------------------------------------------------------

    public async Task InitializeAsync()
    {
        LoadRecentLogs();
        ResolveFont();

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

        await _polling.StartAsync(_lifetimeCts.Token);
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(IsDemoMode));
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

    // ---- Polling -----------------------------------------------------------

    private void OnSnapshotUpdated(object? sender, SyncSnapshot snapshot)
        => Application.Current?.Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(SyncSnapshot snapshot)
    {
        foreach (var item in snapshot.Entries)
        {
            var existing = Entries.FirstOrDefault(x =>
                string.Equals(x.EntryId, item.Entry.EntryId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                Entries.Add(new EntryItemViewModel(item.Entry, item.LocalState, Settings));
                _log.Info($"{item.Entry.CardNumber} received.");
            }
            else
            {
                existing.UpdateRemote(item.Entry, item.LocalState);
            }
        }

        // Only ever auto-select when nothing is selected. A poll must never move
        // the operator off the entry they are working on.
        RebuildVisibleEntries();
        if (SelectedEntry is null)
            SelectedEntry = VisibleEntries.FirstOrDefault() ?? Entries.FirstOrDefault();

        RefreshActionAvailability();
    }

    private void OnPollStatusUpdated(object? sender, PollStatus status)
        => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsApiConnected = status.Connected;
            ApiStatusText = status.Connected ? "CONNECTED" : "OFFLINE";
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

        VisibleEntries.Clear();
        foreach (var entry in Entries)
        {
            if (!entry.MatchesSearch(SearchText))
                continue;
            if (!MatchesFilter(entry))
                continue;
            VisibleEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasVisibleEntries));
        OnPropertyChanged(nameof(EntryCountSummary));

        // Filtering the selected entry out of view must not clear the workspace:
        // the operator keeps working on it until they pick something else.
        if (previous is not null && !VisibleEntries.Contains(previous))
            SelectedEntry = previous;
    }

    private bool MatchesFilter(EntryItemViewModel entry) => Filter switch
    {
        EntryFilter.All => true,
        EntryFilter.Pending => entry.DisplayStatus is EntryDisplayStatus.ExtractionPending,
        EntryFilter.Ready => entry.DisplayStatus is EntryDisplayStatus.Ready,
        EntryFilter.Processing => entry.DisplayStatus is EntryDisplayStatus.Processing,
        EntryFilter.Completed => entry.DisplayStatus is EntryDisplayStatus.Completed,
        EntryFilter.Failed => entry.DisplayStatus is EntryDisplayStatus.Failed or EntryDisplayStatus.Outdated,
        EntryFilter.NoVideo => entry.DisplayStatus is EntryDisplayStatus.NoVideo or EntryDisplayStatus.VideoMissing,
        _ => true
    };

    // ---- Video mapping -----------------------------------------------------

    private async Task SelectVideoAsync()
    {
        var entry = SelectedEntry;
        if (entry is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = $"Select the local video for card {entry.CardNumber}",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.m4v;*.ts;*.mts;*.m2ts|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(Settings.InputVideoFolder) ? Settings.InputVideoFolder : null
        };

        if (dialog.ShowDialog() != true)
            return;

        var state = entry.LocalState.Clone();
        state.LocalVideoPath = dialog.FileName;
        state.ErrorMessage = null;
        state.ProcessingStatus = state.ProcessingStatus switch
        {
            LocalProcessingStatus.Completed => LocalProcessingStatus.Outdated,
            LocalProcessingStatus.Outdated => LocalProcessingStatus.Outdated,
            _ => LocalProcessingStatus.Ready
        };
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
        entry.ReplaceLocalState(state);
        _log.Info($"{entry.CardNumber}: video mapped to {dialog.FileName}");
        RebuildVisibleEntries();
        RefreshActionAvailability();
    }

    private async Task ClearVideoAsync()
    {
        var entry = SelectedEntry;
        if (entry is null)
            return;

        var state = entry.LocalState.Clone();
        state.LocalVideoPath = null;
        state.ProcessingStatus = LocalProcessingStatus.VideoNotSelected;
        state.ErrorMessage = null;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await _repository.UpsertEntryStateAsync(state, _lifetimeCts.Token);
        entry.ReplaceLocalState(state);
        _log.Info($"{entry.CardNumber}: video mapping cleared.");
        RebuildVisibleEntries();
        RefreshActionAvailability();
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
            _log.Error($"{entry.CardNumber}: preview failed: {ex.Message}");
            ShowBanner(BannerKind.Error, $"{entry.CardNumber}: preview failed. {ex.Message}");
        }
    }

    // ---- Processing --------------------------------------------------------

    private async Task ProcessAsync(bool force)
    {
        // Capture the entry now: the operator may navigate elsewhere while this runs.
        var entry = SelectedEntry;
        if (entry is null)
            return;

        if (entry.ExtractionStatus != RemoteExtractionStatus.Completed)
        {
            ShowBanner(BannerKind.Warning,
                $"{entry.CardNumber}: remote extraction is not completed. The mapped video stays, but processing will not start.");
            return;
        }

        var input = entry.LocalVideoPath;
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            var missing = entry.LocalState.Clone();
            missing.ProcessingStatus = LocalProcessingStatus.VideoNotFound;
            missing.ErrorMessage = "The mapped local video was not found.";
            missing.UpdatedUtc = DateTimeOffset.UtcNow;
            await _repository.UpsertEntryStateAsync(missing, _lifetimeCts.Token);
            entry.ReplaceLocalState(missing);
            RebuildVisibleEntries();
            return;
        }

        if (!force && entry.ProcessingStatus == LocalProcessingStatus.Completed)
        {
            ShowBanner(BannerKind.Info, $"{entry.CardNumber} is already completed. Use REPROCESS to render it again.");
            return;
        }

        var output = Path.Combine(Settings.OutputVideoFolder, Path.GetFileName(input));
        var allowOverwrite = Settings.AllowOverwriteExistingOutput;
        if (File.Exists(output) && !allowOverwrite)
        {
            var answer = MessageBox.Show(
                $"A processed file already exists:\n\n{output}\n\nReplace it once the new render has validated?",
                "Output already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
            allowOverwrite = true;
        }

        var processingHash = OverlayHashCalculator.Calculate(entry.Entry);
        var overlay = ToOverlay(entry.Entry);

        _jobCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        IsProcessing = true;
        ProcessingCardNumber = entry.CardNumber;
        ProcessingFile = Path.GetFileName(input);
        ProcessingStage = "Probing source";
        ProcessingPercent = 0;
        ProcessingElapsed = "00:00";
        ProcessingEta = "—";

        var processingState = entry.LocalState.Clone();
        processingState.ProcessingStatus = LocalProcessingStatus.Processing;
        processingState.ErrorMessage = null;
        processingState.OutputPath = output;
        processingState.UpdatedUtc = DateTimeOffset.UtcNow;
        await _repository.UpsertEntryStateAsync(processingState, _lifetimeCts.Token);
        entry.ReplaceLocalState(processingState);
        RebuildVisibleEntries();

        var loggedBucket = 0;
        var progress = new Progress<ProcessingProgress>(p =>
        {
            ProcessingPercent = p.Percent;
            ProcessingStage = "Rendering overlay";
            ProcessingElapsed = FormatShortDuration(p.Elapsed);
            ProcessingEta = p.EstimatedRemaining.HasValue ? FormatShortDuration(p.EstimatedRemaining.Value) : "—";

            var bucket = Math.Min(100, (int)p.Percent / 20 * 20);
            if (bucket >= 20 && bucket > loggedBucket)
            {
                loggedBucket = bucket;
                _log.Info($"{entry.CardNumber} FFmpeg progress: {bucket}%");
            }
        });

        var request = new ProcessingRequest(entry.EntryId, entry.CardNumber, input, output, overlay, allowOverwrite);

        ProcessingResult result;
        var cancelled = false;
        try
        {
            result = await _videoProcessing.ProcessAsync(request, progress, _jobCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            result = new ProcessingResult(false, output, "Cancelled by the operator.", null, null, false);
        }
        finally
        {
            IsProcessing = false;
            ProcessingStage = "Idle";
            _jobCts?.Dispose();
            _jobCts = null;
        }

        if (cancelled && _lifetimeCts.IsCancellationRequested)
            return;

        var finalState = entry.LocalState.Clone();
        if (result.Success)
        {
            var currentHash = OverlayHashCalculator.Calculate(entry.Entry);
            finalState.ProcessingStatus = string.Equals(currentHash, processingHash, StringComparison.Ordinal)
                ? LocalProcessingStatus.Completed
                : LocalProcessingStatus.Outdated;
            finalState.OutputPath = result.OutputPath;
            finalState.LastProcessedDataHash = processingHash;
            finalState.ProcessedUtc = DateTimeOffset.UtcNow;
            finalState.ErrorMessage = finalState.ProcessingStatus == LocalProcessingStatus.Outdated
                ? "Race data changed while this video was rendering. Reprocess to apply it."
                : null;
            ProcessingPercent = 100;
        }
        else
        {
            finalState.ProcessingStatus = cancelled ? LocalProcessingStatus.Ready : LocalProcessingStatus.Failed;
            finalState.ErrorMessage = result.Error ?? "Unknown processing error.";
        }

        finalState.UpdatedUtc = DateTimeOffset.UtcNow;
        await _repository.UpsertEntryStateAsync(finalState, _lifetimeCts.Token);
        entry.ReplaceLocalState(finalState);
        RebuildVisibleEntries();
        RefreshActionAvailability();

        if (result.Success)
        {
            if (finalState.ProcessingStatus == LocalProcessingStatus.Outdated)
            {
                ShowBanner(BannerKind.Warning,
                    $"{entry.CardNumber} rendered and validated, but the race data changed during processing. Reprocess to apply it.");
            }
            else
            {
                _log.Info($"{entry.CardNumber} marked COMPLETED.");
                ShowBanner(BannerKind.Success, $"{entry.CardNumber} completed. Output: {result.OutputPath}");
            }
        }
        else if (cancelled)
        {
            _log.Info($"{entry.CardNumber}: processing cancelled by the operator.");
            ShowBanner(BannerKind.Info, $"{entry.CardNumber}: processing cancelled. The original video is untouched.");
        }
        else
        {
            _log.Error($"{entry.CardNumber} marked FAILED: {finalState.ErrorMessage}");
            ShowBanner(BannerKind.Error, $"{entry.CardNumber} failed: {finalState.ErrorMessage}");
        }
    }

    private void CancelProcessing()
    {
        if (_jobCts is { IsCancellationRequested: false })
        {
            ProcessingStage = "Cancelling";
            _jobCts.Cancel();
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
        _log.Info("Settings saved.");
        await _polling.PollNowAsync(_lifetimeCts.Token);
        ShowBanner(BannerKind.Success, "Settings saved.");
    }

    private async Task TestApiAsync()
    {
        if (IsDemoMode)
        {
            ShowBanner(BannerKind.Info, "Demo provider is active and needs no network API.");
            return;
        }
        var result = await _apiTester.TestAsync(Settings.ApiUrl, _lifetimeCts.Token);
        ShowBanner(result.Success ? BannerKind.Success : BannerKind.Error, result.Detail);
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

    private void RefreshActionAvailability()
    {
        OnPropertyChanged(nameof(SelectVideoBlockReason));
        OnPropertyChanged(nameof(PreviewBlockReason));
        OnPropertyChanged(nameof(StartProcessingBlockReason));
        OnPropertyChanged(nameof(ReprocessBlockReason));
        OnPropertyChanged(nameof(OpenOutputBlockReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private OverlayData ToOverlay(RaceEntry entry) => new(
        entry.PrimaryName,
        entry.PrimaryLocation,
        entry.CardNumber,
        entry.SecondaryName,
        entry.SecondaryLocation,
        TimingFormatter.Format(entry.TimingSeconds, Settings.TimingFormat));

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
