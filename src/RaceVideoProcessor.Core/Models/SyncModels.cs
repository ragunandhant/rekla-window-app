namespace RaceVideoProcessor.Core.Models;

public sealed record EntrySnapshot(RaceEntry Entry, EntryLocalState LocalState);

public sealed record SyncSnapshot(
    IReadOnlyList<EntrySnapshot> Entries,
    DateTimeOffset SynchronizedAtUtc,
    string ProviderName);

public sealed record SyncState(
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError);

public sealed record PollStatus(
    bool Connected,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextSyncUtc,
    string? Error,
    string ProviderName);
