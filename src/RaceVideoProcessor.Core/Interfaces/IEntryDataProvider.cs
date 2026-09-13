using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

public interface IEntryDataProvider
{
    string Name { get; }

    /// <summary>Players of one race and category: the race's Race ID with that category's type.</summary>
    Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(RaceScope scope, CancellationToken cancellationToken);
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
