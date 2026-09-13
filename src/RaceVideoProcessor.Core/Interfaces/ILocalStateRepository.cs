using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

public interface ILocalStateRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<EntryLocalState?> GetEntryStateAsync(string entryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, EntryLocalState>> GetAllEntryStatesAsync(CancellationToken cancellationToken = default);
    Task UpsertEntryStateAsync(EntryLocalState state, CancellationToken cancellationToken = default);
    Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<SyncState> LoadSyncStateAsync(CancellationToken cancellationToken = default);
    Task SaveSyncStateAsync(SyncState state, CancellationToken cancellationToken = default);
}
