using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

public interface IEntryDataProvider
{
    string Name { get; }

    /// <summary>Players of one race and category: the race's Race ID with that category's type.</summary>
    Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(RaceScope scope, CancellationToken cancellationToken);
}

public interface IRealApiPayloadAdapter
{
    IReadOnlyList<RaceEntry> Parse(string payload);
}
