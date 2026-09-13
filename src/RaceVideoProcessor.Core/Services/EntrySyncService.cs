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

    /// <summary>
    /// Fetches one race and category and merges it with that scope's local state.
    /// Only records of this scope are read or written; operation state, local files,
    /// video links and failures are preserved.
    /// </summary>
    public async Task<SyncSnapshot> SynchronizeAsync(RaceScope scope, CancellationToken cancellationToken)
    {
        var provider = _router.Current;
        var fetched = await provider.FetchEntriesAsync(scope, cancellationToken).ConfigureAwait(false);

        // Stable EntryId is the duplicate-protection boundary. If an API returns the
        // same ID more than once in a payload, the last occurrence wins.
        var unique = fetched
            .Where(x => !string.IsNullOrWhiteSpace(x.EntryId))
            .GroupBy(x => x.EntryId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var result = new List<EntrySnapshot>(unique.Count);
        var localStates = await _repository.GetEntryStatesAsync(scope, cancellationToken).ConfigureAwait(false);

        foreach (var entry in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hash = OverlayHashCalculator.Calculate(entry);
            var existing = localStates.TryGetValue(entry.EntryId, out var persisted) ? persisted : null;
            var isNew = existing is null;
            var state = existing ?? new EntryLocalState
            {
                RaceId = scope.RaceId,
                Category = scope.Category,
                EntryId = entry.EntryId,
                ProcessingStatus = LocalProcessingStatus.VideoNotSelected
            };
            var localStateChanged = isNew;

            // Keep the identifiers needed to resume a stage without the API.
            localStateChanged |= Assign(state.CardNumber, entry.CardNumber, v => state.CardNumber = v);
            localStateChanged |= Assign(state.PlayerId, entry.PlayerId, v => state.PlayerId = v);
            localStateChanged |= Assign(state.Marker, entry.Marker, v => state.Marker = v);
            localStateChanged |= Assign(state.RemoteVideoLink, entry.VideoLink, v => state.RemoteVideoLink = v);
            if (state.EntryDateUtc != entry.EntryDateUtc)
            {
                state.EntryDateUtc = entry.EntryDateUtc;
                localStateChanged = true;
            }

            // A repeated API poll with identical overlay data should not generate a
            // SQLite write. Sync timestamps live in sync_state; entry-state timestamps
            // represent actual state/data-version changes.
            if (!string.Equals(state.LastSeenDataHash, hash, StringComparison.Ordinal))
            {
                state.LastSeenDataHash = hash;
                state.LastSeenUtc = now;
                localStateChanged = true;
            }

            if (state.ProcessingStatus is LocalProcessingStatus.Ready or LocalProcessingStatus.VideoNotSelected &&
                !string.IsNullOrWhiteSpace(state.LocalVideoPath) && !File.Exists(state.LocalVideoPath))
            {
                state.ProcessingStatus = LocalProcessingStatus.VideoNotFound;
                localStateChanged = true;
            }
            else if (state.ProcessingStatus == LocalProcessingStatus.VideoNotFound &&
                     !string.IsNullOrWhiteSpace(state.LocalVideoPath) && File.Exists(state.LocalVideoPath))
            {
                state.ProcessingStatus = LocalProcessingStatus.Ready;
                localStateChanged = true;
            }
            else if (state.ProcessingStatus == LocalProcessingStatus.Completed &&
                     state.UploadStatus is UploadStatus.NotStarted or UploadStatus.Disabled &&
                     !string.Equals(state.LastProcessedDataHash, hash, StringComparison.Ordinal))
            {
                // Once a video has been published, the race data changing does not
                // un-publish it; before that, the local output is stale.
                state.ProcessingStatus = LocalProcessingStatus.Outdated;
                localStateChanged = true;
                _log.Info($"[{scope}] {entry.CardNumber} overlay data changed; existing output marked OUTDATED.");
            }

            if (localStateChanged)
            {
                state.UpdatedUtc = now;
                await _repository.UpsertEntryStateAsync(state, cancellationToken).ConfigureAwait(false);
            }

            result.Add(new EntrySnapshot(entry, state.Clone()));
        }

        return new SyncSnapshot(scope, result, now, provider.Name);
    }

    private static bool Assign(string? current, string? incoming, Action<string?> set)
    {
        if (string.IsNullOrWhiteSpace(incoming) || string.Equals(current, incoming, StringComparison.Ordinal))
            return false;
        set(incoming);
        return true;
    }
}
