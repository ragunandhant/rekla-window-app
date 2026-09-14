using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

/// <summary>Thrown when a race is saved with a Race ID that is already stored.</summary>
public sealed class DuplicateRaceIdException(string raceId)
    : Exception($"A race with this Race ID already exists: {raceId}")
{
    public string RaceId { get; } = raceId;
}

/// <summary>Locally stored entries of one race, per category.</summary>
public sealed record RaceEntryCounts(int Meter200, int Meter300)
{
    public int Total => Meter200 + Meter300;
}

/// <summary>What the Database page shows about the local store.</summary>
public sealed record DatabaseInfo(string Path, long SizeBytes, int SchemaVersion, int Races, int EntryRecords);

public interface ILocalStateRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // ---- Races -------------------------------------------------------------

    Task<IReadOnlyList<Race>> GetRacesAsync(CancellationToken cancellationToken = default);
    Task<Race?> GetRaceAsync(string raceId, CancellationToken cancellationToken = default);

    /// <summary>Saves a new race. Throws <see cref="DuplicateRaceIdException"/> if the Race ID exists.</summary>
    Task<Race> AddRaceAsync(Race race, CancellationToken cancellationToken = default);

    /// <summary>Renames or re-dates a saved race. The Race ID is its identity and cannot change. Returns false if it is gone.</summary>
    Task<bool> UpdateRaceAsync(Race race, CancellationToken cancellationToken = default);

    /// <summary>Locally stored entry records per race, keyed by Race ID (case-insensitive).</summary>
    Task<IReadOnlyDictionary<string, RaceEntryCounts>> GetRaceEntryCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the race and every locally stored record scoped to it, in one
    /// transaction: either all of it goes or none of it does. Other races are untouched.
    /// Returns false when no such race exists.
    /// </summary>
    Task<bool> DeleteRaceAsync(string raceId, CancellationToken cancellationToken = default);

    // ---- Entry state, always scoped to a race and category -----------------

    Task<EntryLocalState?> GetEntryStateAsync(RaceScope scope, string entryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, EntryLocalState>> GetEntryStatesAsync(RaceScope scope, CancellationToken cancellationToken = default);

    /// <summary>Records whose last known stage was still running: processing, uploading or assigning.</summary>
    Task<IReadOnlyList<EntryLocalState>> GetInFlightEntryStatesAsync(CancellationToken cancellationToken = default);

    Task<int> CountEntryStatesAsync(string raceId, CancellationToken cancellationToken = default);
    Task UpsertEntryStateAsync(EntryLocalState state, CancellationToken cancellationToken = default);

    // ---- Application -------------------------------------------------------

    Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<SyncState> LoadSyncStateAsync(CancellationToken cancellationToken = default);
    Task SaveSyncStateAsync(SyncState state, CancellationToken cancellationToken = default);

    // ---- Database ----------------------------------------------------------

    Task<DatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a consistent copy of the whole database to <paramref name="destinationPath"/>.</summary>
    Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default);
}
