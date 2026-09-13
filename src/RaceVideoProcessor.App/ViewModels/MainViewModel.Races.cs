using System.Collections.ObjectModel;
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

    public RaceItemViewModel(Race race) => Race = race;

    public Race Race { get; }
    public string RaceId => Race.RaceId;
    public string RaceName => Race.RaceName;
    public string RaceDateDisplay => Race.RaceDateDisplay;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => IsActive ? "ACTIVE" : "SAVED";
}

/// <summary>Race Management: creating, selecting and deleting races.</summary>
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

    public ObservableCollection<RaceItemViewModel> Races { get; } = [];

    public ICommand OpenCreateRaceCommand { get; private set; } = null!;
    public ICommand CancelCreateRaceCommand { get; private set; } = null!;
    public ICommand SaveRaceCommand { get; private set; } = null!;
    public ICommand UseSelectedRaceCommand { get; private set; } = null!;
    public ICommand UseDuplicateRaceCommand { get; private set; } = null!;
    public ICommand DeleteSelectedRaceCommand { get; private set; } = null!;

    private void InitializeRaceManagement()
    {
        OpenCreateRaceCommand = new RelayCommand(OpenCreateRace);
        CancelCreateRaceCommand = new RelayCommand(() => IsCreateRaceOpen = false);
        SaveRaceCommand = new AsyncRelayCommand(SaveRaceAsync);
        UseSelectedRaceCommand = new AsyncRelayCommand(
            () => SelectedRaceItem is null ? Task.CompletedTask : ActivateRaceAsync(SelectedRaceItem.Race),
            () => UseRaceBlockReason.Length == 0);
        UseDuplicateRaceCommand = new AsyncRelayCommand(UseDuplicateRaceAsync, () => DuplicateRace is not null);
        DeleteSelectedRaceCommand = new AsyncRelayCommand(DeleteSelectedRaceAsync, () => SelectedRaceItem is not null);
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

    public string UseRaceBlockReason
    {
        get
        {
            if (SelectedRaceItem is null) return "Select a race in the list.";
            if (SelectedRaceItem.IsActive) return "This race is already active.";
            if (_job is not null) return "A job is running. Cancel or finish it before switching races.";
            return string.Empty;
        }
    }

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
        OnPropertyChanged(nameof(UseRaceBlockReason));
        OnPropertyChanged(nameof(DeleteRaceBlockReason));
    }

    private async Task LoadRacesAsync()
    {
        var races = await _repository.GetRacesAsync(_lifetimeCts.Token);
        Races.Clear();
        foreach (var race in races)
        {
            Races.Add(new RaceItemViewModel(race)
            {
                IsActive = ActiveRace is not null && string.Equals(race.RaceId, ActiveRace.RaceId, StringComparison.OrdinalIgnoreCase)
            });
        }
        OnPropertyChanged(nameof(HasRaces));
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
    private async Task ActivateRaceAsync(Race race)
    {
        if (ActiveRace is not null && string.Equals(ActiveRace.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase))
        {
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

    private void OpenCreateRace()
    {
        NewRaceName = string.Empty;
        NewRaceDate = DateTime.Today.ToString(Race.DateFormat);
        NewRaceId = string.Empty;
        RaceFormError = string.Empty;
        DuplicateRace = null;
        IsCreateRaceOpen = true;
    }

    private async Task SaveRaceAsync()
    {
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
            ShowBanner(BannerKind.Success, $"Race {saved.RaceName} saved. Select it and choose USE THIS RACE to work on it.");
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
        if (DeleteRaceBlockReason is { Length: > 0 } reason && _job is not null)
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
}
