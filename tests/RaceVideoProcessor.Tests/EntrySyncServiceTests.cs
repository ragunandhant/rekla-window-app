using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Tests;

public sealed class EntrySyncServiceTests
{
    private static RaceEntry Entry(string id, string card = "1000AAA")
        => TestEntries.Create(entryId: id, card: card);

    [Fact]
    public async Task DuplicateApiEntriesBecomeOneSnapshot()
    {
        var provider = new StaticProvider([Entry("001"), Entry("001")]);
        var service = new EntrySyncService(new StaticRouter(provider), new InMemoryRepository(), new TestLog());
        var snapshot = await service.SynchronizeAsync(CancellationToken.None);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task ChangedOverlayDataMarksCompletedOutputOutdated()
    {
        var original = Entry("001", "1000AAA");
        var changed = Entry("001", "1000AAZ");
        var repo = new InMemoryRepository();
        repo.States["001"] = new EntryLocalState
        {
            EntryId = "001",
            ProcessingStatus = LocalProcessingStatus.Completed,
            LastProcessedDataHash = OverlayHashCalculator.Calculate(original),
            LastSeenDataHash = OverlayHashCalculator.Calculate(original)
        };
        var service = new EntrySyncService(new StaticRouter(new StaticProvider([changed])), repo, new TestLog());

        var snapshot = await service.SynchronizeAsync(CancellationToken.None);
        Assert.Equal(LocalProcessingStatus.Outdated, snapshot.Entries[0].LocalState.ProcessingStatus);
    }
}
