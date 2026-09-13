using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Services;

public sealed class EntrySyncService
{
    private readonly IEntryProviderRouter _router;
    private readonly ILocalStateRepository _repository;
    private readonly IAppLog _log;

    public EntrySyncService(IEntryProviderRouter router, ILocalStateRepository repository, IAppLog log)
    {
        _router = router;
        _repository = repository;
        _log = log;
    }

    public async Task<SyncSnapshot> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var provider = _router.Current;
        var fetched = await provider.FetchEntriesAsync(cancellationToken).ConfigureAwait(false);

        // Stable EntryId is the duplicate-protection boundary. If an API returns the
        // same ID more than once in a payload, the last occurrence wins.
        var unique = fetched
            .Where(x => !string.IsNullOrWhiteSpace(x.EntryId))
            .GroupBy(x => x.EntryId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var result = new List<EntrySnapshot>(unique.Count);
        var localStates = await _repository.GetAllEntryStatesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hash = OverlayHashCalculator.Calculate(entry);
            var existing = localStates.TryGetValue(entry.EntryId, out var persisted) ? persisted : null;
            var isNew = existing is null;
            var state = existing ?? new EntryLocalState
            {
                EntryId = entry.EntryId,
                ProcessingStatus = LocalProcessingStatus.VideoNotSelected
            };
            var localStateChanged = isNew;

            // A repeated API poll with identical overlay data should not generate a
            // SQLite write. Sync timestamps live in sync_state; entry-state timestamps
            // represent actual state/data-version changes.
            if (!string.Equals(state.LastSeenDataHash, hash, StringComparison.Ordinal))
            {
                state.LastSeenDataHash = hash;
                state.LastSeenUtc = now;
                localStateChanged = true;
            }

            if (state.ProcessingStatus is not LocalProcessingStatus.Completed and not LocalProcessingStatus.Outdated &&
                !string.IsNullOrWhiteSpace(state.LocalVideoPath) && !File.Exists(state.LocalVideoPath) &&
                state.ProcessingStatus != LocalProcessingStatus.VideoNotFound)
            {
                state.ProcessingStatus = LocalProcessingStatus.VideoNotFound;
                localStateChanged = true;
            }
            else if (state.ProcessingStatus == LocalProcessingStatus.Completed &&
                     !string.Equals(state.LastProcessedDataHash, hash, StringComparison.Ordinal))
            {
                state.ProcessingStatus = LocalProcessingStatus.Outdated;
                localStateChanged = true;
                _log.Info($"{entry.CardNumber} overlay data changed; existing output marked OUTDATED.");
            }
            else if (state.ProcessingStatus == LocalProcessingStatus.VideoNotFound &&
                     !string.IsNullOrWhiteSpace(state.LocalVideoPath) && File.Exists(state.LocalVideoPath))
            {
                state.ProcessingStatus = LocalProcessingStatus.Ready;
                localStateChanged = true;
            }

            if (localStateChanged)
            {
                state.UpdatedUtc = now;
                await _repository.UpsertEntryStateAsync(state, cancellationToken).ConfigureAwait(false);
            }

            result.Add(new EntrySnapshot(entry, state.Clone()));
        }

        return new SyncSnapshot(result, now, provider.Name);
    }
}
