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
        var snapshot = await service.SynchronizeAsync(TestScopes.RaceA200, CancellationToken.None);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task ChangedOverlayDataMarksCompletedOutputOutdated()
    {
        var original = Entry("001", "1000AAA");
        var changed = Entry("001", "1000AAZ");
        var repo = new InMemoryRepository();
        repo.States[InMemoryRepository.Key(TestScopes.RaceA200, "001")] = new EntryLocalState
        {
            RaceId = "AAA",
            EntryId = "001",
            ProcessingStatus = LocalProcessingStatus.Completed,
            LastProcessedDataHash = OverlayHashCalculator.Calculate(original),
            LastSeenDataHash = OverlayHashCalculator.Calculate(original)
        };
        var service = new EntrySyncService(new StaticRouter(new StaticProvider([changed])), repo, new TestLog());

        var snapshot = await service.SynchronizeAsync(TestScopes.RaceA200, CancellationToken.None);
        Assert.Equal(LocalProcessingStatus.Outdated, snapshot.Entries[0].LocalState.ProcessingStatus);
    }

    [Fact]
    public async Task RefreshReadsAndWritesOnlyTheSelectedRaceAndCategory()
    {
        var repo = new InMemoryRepository();
        var other = TestScopes.State(TestScopes.RaceB200, "001");
        other.LocalVideoPath = @"D:\RaceB\cart100.mp4";
        other.UploadStatus = UploadStatus.Completed;
        other.UploadedVideoLink = "https://media.example/b.mp4";
        repo.States[InMemoryRepository.Key(TestScopes.RaceB200, "001")] = other;

        var provider = new StaticProvider([Entry("001", "100")]);
        var service = new EntrySyncService(new StaticRouter(provider), repo, new TestLog());

        var snapshot = await service.SynchronizeAsync(TestScopes.RaceA200, CancellationToken.None);

        Assert.Equal(TestScopes.RaceA200, provider.Requested.Single());
        Assert.Equal(TestScopes.RaceA200, snapshot.Scope);
        var stateA = Assert.Single(snapshot.Entries).LocalState;
        Assert.Equal("AAA", stateA.RaceId);
        Assert.Null(stateA.LocalVideoPath);
        Assert.Equal(UploadStatus.NotStarted, stateA.UploadStatus);

        // Race B's cart 100 is untouched.
        var stillB = repo.States[InMemoryRepository.Key(TestScopes.RaceB200, "001")];
        Assert.Equal(@"D:\RaceB\cart100.mp4", stillB.LocalVideoPath);
        Assert.Equal("https://media.example/b.mp4", stillB.UploadedVideoLink);
        Assert.All(repo.Writes, w => Assert.Equal("AAA", w.RaceId));
    }

    [Fact]
    public async Task RefreshPreservesOperationStateAndStoresPlayerIdentifiers()
    {
        var repo = new InMemoryRepository();
        var entry = Entry("marker-9", "100");
        var state = TestScopes.State(TestScopes.RaceA200, "marker-9");
        state.ProcessingStatus = LocalProcessingStatus.Completed;
        state.UploadStatus = UploadStatus.Completed;
        state.UploadedVideoLink = "https://media.example/keep.mp4";
        state.AssignmentStatus = AssignmentStatus.Failed;
        state.ErrorMessage = "Assignment Failed: HTTP 500";
        state.LastProcessedDataHash = OverlayHashCalculator.Calculate(TestEntries.Create(entryId: "marker-9", card: "999"));
        repo.States[InMemoryRepository.Key(TestScopes.RaceA200, "marker-9")] = state;

        var service = new EntrySyncService(new StaticRouter(new StaticProvider([entry])), repo, new TestLog());
        var snapshot = await service.SynchronizeAsync(TestScopes.RaceA200, CancellationToken.None);
        var merged = snapshot.Entries[0].LocalState;

        // Data changed after upload: the published video stays published, not "outdated".
        Assert.Equal(LocalProcessingStatus.Completed, merged.ProcessingStatus);
        Assert.Equal("https://media.example/keep.mp4", merged.UploadedVideoLink);
        Assert.Equal(AssignmentStatus.Failed, merged.AssignmentStatus);
        Assert.Equal("Assignment Failed: HTTP 500", merged.ErrorMessage);
        Assert.Equal("100", merged.CardNumber);
        Assert.Equal("player-marker-9", merged.PlayerId);
        Assert.Equal("marker-9", merged.Marker);
    }
}
