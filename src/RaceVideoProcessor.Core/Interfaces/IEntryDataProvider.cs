using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

public interface IEntryDataProvider
{
    string Name { get; }
    Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(CancellationToken cancellationToken);
}

public interface IEntryProviderRouter
{
    IEntryDataProvider Current { get; }
}

public interface IDemoEntryController
{
    int AvailableCount { get; }

    /// <summary>Card number of the most recently released demo entry, for log messages.</summary>
    string LastReleasedCardNumber { get; }
    Task<int> SimulateNextEntryAsync(CancellationToken cancellationToken);
    Task ResetAsync(CancellationToken cancellationToken);
}

public interface IRealApiPayloadAdapter
{
    IReadOnlyList<RaceEntry> Parse(string payload);
}
