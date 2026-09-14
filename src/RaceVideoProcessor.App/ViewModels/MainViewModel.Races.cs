using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RaceVideoProcessor.App.Commands;
using RaceVideoProcessor.App.Views;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.App.ViewModels;

/// <summary>One saved race in Race Management.</summary>
public sealed class RaceItemViewModel : ObservableObject
{
    private bool _isActive;
    private RaceEntryCounts _counts = new(0, 0);

    public RaceItemViewModel(Race race) => Race = race;

    /// <summary>Entries stored locally for this race: the ones seen from the API or worked on.</summary>
    public RaceEntryCounts Counts
    {
        get => _counts;
        set
        {
            if (SetProperty(ref _counts, value))
            {
                OnPropertyChanged(nameof(TotalEntries));
                OnPropertyChanged(nameof(Entries200));
                OnPropertyChanged(nameof(Entries300));
            }
        }
    }

    public int TotalEntries => Counts.Total;
    public int Entries200 => Counts.Meter200;
    public int Entries300 => Counts.Meter300;

    public bool Matches(string? term)
        => string.IsNullOrWhiteSpace(term) ||
           RaceName.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) ||
           RaceId.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) ||
           RaceDateDisplay.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);

    public Race Race { get; }
    public string RaceId => Race.RaceId;
    public string RaceName => Race.RaceName;
    public string RaceDateDisplay => Race.RaceDateDisplay;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            SetProperty(ref _isActive, value);
        }
    }

}

/// <summary>Race Management: creating, editing, selecting and deleting races.</summary>
public sealed partial class MainViewModel
{
    private Race? _activeRace;
    private RaceItemViewModel? _selectedRaceItem;
    private bool _isCreateRaceOpen;
    private string _newRaceName = string.Empty;
    private string _newRaceDate = string.Empty;
    private string _newRaceId = string.Empty;
    private string _raceFormError = string.Empty;
    private Race? _duplicateRace;
    private string? _editingRaceId;
    private string _raceSearchText = string.Empty;

    public ObservableCollection<RaceItemViewModel> Races { get; } = [];

    /// <summary>The Races table after search.</summary>
    public ObservableCollection<RaceItemViewModel> VisibleRaces { get; } = [];

    public string RaceSearchText
    {
        get => _raceSearchText;
        set
        {
            if (SetProperty(ref _raceSearchText, value))
                RebuildVisibleRaces();
        }
    }

    public string RaceCountText => $"Total races: {Races.Count}";

    public ICommand SelectRaceCommand { get; private set; } = null!;
    public ICommand EditRaceCommand { get; private set; } = null!;
    public ICommand DeleteRaceCommand { get; private set; } = null!;

    public ICommand OpenCreateRaceCommand { get; private set; } = null!;
    public ICommand CancelCreateRaceCommand { get; private set; } = null!;
    public ICommand SaveRaceCommand { get; private set; } = null!;
    public ICommand UseDuplicateRaceCommand { get; private set; } = null!;

    private void InitializeRaceManagement()
    {
        OpenCreateRaceCommand = new RelayCommand(OpenCreateRace);
        CancelCreateRaceCommand = new RelayCommand(() => IsCreateRaceOpen = false);
        SaveRaceCommand = new AsyncRelayCommand(SaveRaceAsync);
        UseDuplicateRaceCommand = new AsyncRelayCommand(UseDuplicateRaceAsync, () => DuplicateRace is not null);
        SelectRaceCommand = new AsyncRelayCommand<RaceItemViewModel>(item => item is null ? Task.CompletedTask : ActivateRaceAsync(item.Race));
        EditRaceCommand = new RelayCommand<RaceItemViewModel>(item => { if (item is not null) OpenEditRace(item.Race); });
        DeleteRaceCommand = new AsyncRelayCommand<RaceItemViewModel>(async item =>
        {
            if (item is null)
                return;
            SelectedRaceItem = item;
            await DeleteSelectedRaceAsync();
        });
    }

    /// <summary>
    /// The race chosen in the header. Setting it switches the work context, which
    /// is refused while an operation runs; the header then snaps back.
    /// </summary>
    public RaceItemViewModel? HeaderRace
    {
        get => Races.FirstOrDefault(r => r.IsActive);
        set
        {
            if (value is null || value.IsActive)
                return;
            _ = SwitchRaceFromHeaderAsync(value.Race);
        }
    }

    private async Task SwitchRaceFromHeaderAsync(Race race)
    {
        await ActivateRaceAsync(race, stayOnPage: true);
        OnPropertyChanged(nameof(HeaderRace));
    }

    // ---- Active race -------------------------------------------------------

    /// <summary>The race everything on the Work page belongs to.</summary>
    public Race? ActiveRace
    {
        get => _activeRace;
        private set
        {
            if (!SetProperty(ref _activeRace, value))
                return;
            OnPropertyChanged(nameof(HasActiveRace));
            OnPropertyChanged(nameof(ActiveRaceName));
            OnPropertyChanged(nameof(ActiveRaceDate));
            foreach (var item in Races)
                item.IsActive = value is not null && string.Equals(item.RaceId, value.RaceId, StringComparison.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(HeaderRace));
        }
    }

    public bool HasActiveRace => ActiveRace is not null;
    public string ActiveRaceName => ActiveRace?.RaceName ?? "No race selected";
    public string ActiveRaceDate => ActiveRace?.RaceDateDisplay ?? "Open Races to create or select one";

    // ---- List --------------------------------------------------------------

    public RaceItemViewModel? SelectedRaceItem
    {
        get => _selectedRaceItem;
        set
        {
            if (SetProperty(ref _selectedRaceItem, value))
                RefreshRaceActionAvailability();
        }
    }

    public bool HasRaces => Races.Count > 0;

    public string DeleteRaceBlockReason
    {
        get
        {
            if (SelectedRaceItem is null) return "Select a race in the list.";
            if (_job is not null && string.Equals(_job.Scope.RaceId, SelectedRaceItem.RaceId, StringComparison.OrdinalIgnoreCase))
                return "This race currently has an active operation. Cancel or finish the operation before deleting the race.";
            return string.Empty;
        }
    }

    private void RefreshRaceActionAvailability()
    {
        OnPropertyChanged(nameof(DeleteRaceBlockReason));
    }

    private async Task LoadRacesAsync()
    {
        var races = await _repository.GetRacesAsync(_lifetimeCts.Token);
        var counts = await _repository.GetRaceEntryCountsAsync(_lifetimeCts.Token);
        Races.Clear();
        foreach (var race in races)
        {
            Races.Add(new RaceItemViewModel(race)
            {
                IsActive = ActiveRace is not null && string.Equals(race.RaceId, ActiveRace.RaceId, StringComparison.OrdinalIgnoreCase),
                Counts = counts.TryGetValue(race.RaceId, out var c) ? c : new RaceEntryCounts(0, 0)
            });
        }
        OnPropertyChanged(nameof(HasRaces));
        OnPropertyChanged(nameof(HeaderRace));
        OnPropertyChanged(nameof(RaceCountText));
        RebuildVisibleRaces();
    }

    private async Task RefreshRaceCountsAsync()
    {
        try
        {
            var counts = await _repository.GetRaceEntryCountsAsync(_lifetimeCts.Token);
            foreach (var item in Races)
                item.Counts = counts.TryGetValue(item.RaceId, out var c) ? c : new RaceEntryCounts(0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Could not count race entries: " + ex.Message);
        }
    }

    private void RebuildVisibleRaces()
    {
        VisibleRaces.Clear();
        foreach (var item in Races.Where(r => r.Matches(RaceSearchText)))
            VisibleRaces.Add(item);
    }

    private async Task RestoreSelectedRaceAsync()
    {
        var race = Settings.SelectedRaceId is { } id ? await _repository.GetRaceAsync(id, _lifetimeCts.Token) : null;
        if (race is null)
        {
            if (Settings.SelectedRaceId is not null)
            {
                Settings.SelectedRaceId = null;
                await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);
            }

            ApiStatusText = "NO RACE";
            RefreshWorkContext();
            ActivePage = AppPage.Races;
            if (!HasRaces)
                OpenCreateRace();
            return;
        }

        ActiveRace = race;
        SelectedRaceItem = Races.FirstOrDefault(r => r.IsActive);
        _log.Info($"Race selected: {race.RaceName} ({race.RaceDateDisplay}), Race ID {race.RaceId}.");
        await LoadScopeAsync();
    }

    /// <summary>
    /// Makes <paramref name="race"/> the working context. Refused while a job runs:
    /// the running job keeps its own race either way, but the operator must not be
    /// moved away from it mid-operation.
    /// </summary>
    private async Task ActivateRaceAsync(Race race, bool stayOnPage = false)
    {
        if (ActiveRace is not null && string.Equals(ActiveRace.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase))
        {
            if (!stayOnPage)
                ActivePage = AppPage.Work;
            return;
        }

        if (_job is not null)
        {
            MessageBox.Show(
                $"Cart {_job.CardNumber} is still {StageName(_job.Stage).ToLowerInvariant()}.\n\n" +
                "Cancel or finish the operation before switching races.",
                "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previous = ActiveRace;
        ActiveRace = race;
        Settings.SelectedRaceId = race.RaceId;
        await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);

        _log.Info(previous is null
            ? $"Race selected: {race.RaceName} ({race.RaceDateDisplay}), Race ID {race.RaceId}."
            : $"Race switched from {previous.RaceName} ({previous.RaceId}) to {race.RaceName} ({race.RaceDateDisplay}), Race ID {race.RaceId}.");

        SearchText = string.Empty;
        BannerText = string.Empty;
        _awaitingNextAfter = null;
        if (!stayOnPage)
            ActivePage = AppPage.Work;
        await LoadScopeAsync();
    }

    // ---- Create ------------------------------------------------------------

    public bool IsCreateRaceOpen
    {
        get => _isCreateRaceOpen;
        set => SetProperty(ref _isCreateRaceOpen, value);
    }

    public string NewRaceName { get => _newRaceName; set => SetProperty(ref _newRaceName, value); }
    public string NewRaceDate { get => _newRaceDate; set => SetProperty(ref _newRaceDate, value); }
    public string NewRaceId { get => _newRaceId; set => SetProperty(ref _newRaceId, value); }

    public string RaceFormError
    {
        get => _raceFormError;
        private set
        {
            if (SetProperty(ref _raceFormError, value))
                OnPropertyChanged(nameof(HasRaceFormError));
        }
    }

    public bool HasRaceFormError => RaceFormError.Length > 0;

    /// <summary>The saved race whose Race ID the operator just tried to reuse.</summary>
    public Race? DuplicateRace
    {
        get => _duplicateRace;
        private set
        {
            if (SetProperty(ref _duplicateRace, value))
                OnPropertyChanged(nameof(HasDuplicateRace));
        }
    }

    public bool HasDuplicateRace => DuplicateRace is not null;

    /// <summary>The form edits an existing race: its Race ID is shown but cannot change.</summary>
    public bool IsEditingRace => _editingRaceId is not null;
    public string RaceFormTitle => IsEditingRace ? "Edit Race" : "Add Race";

    private void OpenCreateRace()
    {
        _editingRaceId = null;
        NewRaceName = string.Empty;
        NewRaceDate = DateTime.Today.ToString(Race.DateFormat);
        NewRaceId = string.Empty;
        RaceFormError = string.Empty;
        DuplicateRace = null;
        OnPropertyChanged(nameof(IsEditingRace));
        OnPropertyChanged(nameof(RaceFormTitle));
        IsCreateRaceOpen = true;
    }

    private void OpenEditRace(Race race)
    {
        _editingRaceId = race.RaceId;
        NewRaceName = race.RaceName;
        NewRaceDate = race.RaceDateDisplay;
        NewRaceId = race.RaceId;
        RaceFormError = string.Empty;
        DuplicateRace = null;
        OnPropertyChanged(nameof(IsEditingRace));
        OnPropertyChanged(nameof(RaceFormTitle));
        IsCreateRaceOpen = true;
    }

    /// <summary>
    /// Saves the name and date of an existing race. The Race ID is the race's
    /// identity — every stored entry is keyed by it — so it is never changed.
    /// </summary>
    private async Task SaveEditedRaceAsync(string raceId)
    {
        var others = Races.Select(r => r.Race).Where(r => !string.Equals(r.RaceId, raceId, StringComparison.OrdinalIgnoreCase)).ToList();
        var validation = RaceValidator.Validate(NewRaceName, NewRaceDate, raceId, others);
        if (!validation.IsValid)
        {
            RaceFormError = string.Join("\n", validation.Errors);
            return;
        }

        var existing = Races.FirstOrDefault(r => string.Equals(r.RaceId, raceId, StringComparison.OrdinalIgnoreCase))?.Race;
        if (existing is null || !await _repository.UpdateRaceAsync(existing with
            {
                RaceName = validation.RaceName,
                RaceDate = validation.RaceDate!.Value
            }, _lifetimeCts.Token))
        {
            RaceFormError = "This race no longer exists.";
            await LoadRacesAsync();
            return;
        }

        _log.Info($"Race edited: Race ID {raceId} is now {validation.RaceName} ({validation.RaceDate:dd-MM-yyyy}).");
        IsCreateRaceOpen = false;
        _editingRaceId = null;
        await LoadRacesAsync();

        if (ActiveRace is not null && string.Equals(ActiveRace.RaceId, raceId, StringComparison.OrdinalIgnoreCase))
            ActiveRace = await _repository.GetRaceAsync(raceId, _lifetimeCts.Token) ?? ActiveRace;
        ShowBanner(BannerKind.Success, $"Race {validation.RaceName} saved.");
    }

    private async Task SaveRaceAsync()
    {
        if (_editingRaceId is { } editing)
        {
            await SaveEditedRaceAsync(editing);
            return;
        }

        var existing = Races.Select(r => r.Race).ToList();
        var validation = RaceValidator.Validate(NewRaceName, NewRaceDate, NewRaceId, existing);
        DuplicateRace = validation.ExistingRaceWithSameId;
        if (!validation.IsValid)
        {
            RaceFormError = string.Join("\n", validation.Errors);
            return;
        }

        Race saved;
        try
        {
            saved = await _repository.AddRaceAsync(new Race
            {
                RaceId = validation.RaceId,
                RaceName = validation.RaceName,
                RaceDate = validation.RaceDate!.Value
            }, _lifetimeCts.Token);
        }
        catch (DuplicateRaceIdException)
        {
            // Saved elsewhere since the list was loaded.
            await LoadRacesAsync();
            DuplicateRace = Races.FirstOrDefault(r => string.Equals(r.RaceId, validation.RaceId, StringComparison.OrdinalIgnoreCase))?.Race;
            RaceFormError = RaceValidator.DuplicateMessage;
            return;
        }

        _log.Info($"Race created: {saved.RaceName} ({saved.RaceDateDisplay}), Race ID {saved.RaceId}.");
        IsCreateRaceOpen = false;
        RaceFormError = string.Empty;
        await LoadRacesAsync();
        SelectedRaceItem = Races.FirstOrDefault(r => string.Equals(r.RaceId, saved.RaceId, StringComparison.OrdinalIgnoreCase));

        if (ActiveRace is null && _job is null)
            await ActivateRaceAsync(saved);
        else
            ShowBanner(BannerKind.Success, $"Race {saved.RaceName} saved. Choose Select to work on it.");
    }

    private async Task UseDuplicateRaceAsync()
    {
        if (DuplicateRace is not { } race)
            return;
        IsCreateRaceOpen = false;
        SelectedRaceItem = Races.FirstOrDefault(r => string.Equals(r.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase));
        await ActivateRaceAsync(race);
    }

    // ---- Delete ------------------------------------------------------------

    private async Task DeleteSelectedRaceAsync()
    {
        if (SelectedRaceItem is not { } item)
            return;

        var race = item.Race;
        if (_job is not null && string.Equals(_job.Scope.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase) &&
            DeleteRaceBlockReason is { Length: > 0 } reason)
        {
            MessageBox.Show(reason, "Cannot delete active race", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var records = await _repository.CountEntryStatesAsync(race.RaceId, _lifetimeCts.Token);
        var choice = ChoiceDialog.Show(
            "Delete Race",
            "Delete Race?",
            $"Race:  {race.RaceName}\nDate:  {race.RaceDateDisplay}\nRace ID:  {race.RaceId}\n\n" +
            "This will permanently delete:\n" +
            "•  Race configuration\n" +
            $"•  Player/entry data stored locally ({records} record{(records == 1 ? "" : "s")})\n" +
            "•  Processing states\n" +
            "•  Upload states\n" +
            "•  Assignment states\n" +
            "•  Local race-related SQLite records\n" +
            "•  Race history/state associated with this race\n\n" +
            "Video files on disk are not deleted: original and processed videos stay where they are. " +
            "Nothing on the backend is changed.",
            "This action cannot be undone.",
            new Choice("CANCEL", "cancel", IsCancel: true),
            new Choice("DELETE RACE", "delete", ChoiceStyle.Danger));

        if (choice != "delete")
            return;

        // Re-check: a job could have started while the dialog was open.
        if (_job is not null && string.Equals(_job.Scope.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("This race currently has an active operation.\n\nCancel or finish the operation before deleting the race.",
                "Cannot delete active race", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _repository.DeleteRaceAsync(race.RaceId, _lifetimeCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"Race delete failed for {race.RaceName} ({race.RaceId}); nothing was removed: {ex.Message}");
            MessageBox.Show($"The race could not be deleted, and nothing was removed.\n\n{ex.Message}",
                "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _log.Info($"Race deleted: {race.RaceName} ({race.RaceDateDisplay}), Race ID {race.RaceId}; {records} local record(s) removed.");

        var wasActive = ActiveRace is not null && string.Equals(ActiveRace.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase);
        await LoadRacesAsync();
        SelectedRaceItem = null;

        if (wasActive)
        {
            ActiveRace = null;
            Settings.SelectedRaceId = null;
            await _repository.SaveSettingsAsync(Settings, _lifetimeCts.Token);
            await LoadScopeAsync();
        }

        ShowBanner(BannerKind.Success, $"Race {race.RaceName} deleted, with {records} local record(s).");
    }

    // ---- Database ------------------------------------------------------------

    private DatabaseInfo? _databaseInfo;
    private string _lastBackupText = "No backup made this session.";

    public DatabaseInfo? DatabaseInfo
    {
        get => _databaseInfo;
        private set
        {
            if (SetProperty(ref _databaseInfo, value))
                OnPropertyChanged(nameof(DatabaseSizeText));
        }
    }

    public string DatabaseSizeText => DatabaseInfo is null
        ? "—"
        : DatabaseInfo.SizeBytes >= 1024 * 1024
            ? $"{DatabaseInfo.SizeBytes / 1048576d:0.0} MB"
            : $"{DatabaseInfo.SizeBytes / 1024d:0} KB";

    public string LastBackupText { get => _lastBackupText; private set => SetProperty(ref _lastBackupText, value); }

    private async Task RefreshDatabaseAsync()
    {
        try
        {
            DatabaseInfo = await _repository.GetDatabaseInfoAsync(_lifetimeCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Could not read the database details: " + ex.Message);
        }
    }

    /// <summary>A consistent copy of the whole local database, next to it under Backups.</summary>
    private async Task BackupDatabaseAsync()
    {
        try
        {
            var info = DatabaseInfo ?? await _repository.GetDatabaseInfoAsync(_lifetimeCts.Token);
            var folder = Path.Combine(Path.GetDirectoryName(info.Path) ?? ".", "Backups");
            var destination = Path.Combine(folder, $"race-video-processor-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            await _repository.BackupAsync(destination, _lifetimeCts.Token);
            LastBackupText = $"Last backup: {destination}";
            _log.Info($"Database backed up to {destination}.");
            ShowBanner(BannerKind.Success, "Database backed up.");
            await RefreshDatabaseAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Database backup failed: " + ex.Message);
            ShowBanner(BannerKind.Error, "Database backup failed: " + ex.Message);
        }
    }
}
