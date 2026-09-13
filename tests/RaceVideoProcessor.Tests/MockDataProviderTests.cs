using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;
using RaceVideoProcessor.Infrastructure.Providers;

namespace RaceVideoProcessor.Tests;

public sealed class MockDataProviderTests
{
    /// <summary>Both categories together, in release order: every released demo entry.</summary>
    private static async Task<List<RaceEntry>> FetchAll(MockDataProvider provider)
    {
        var e200 = await provider.FetchEntriesAsync(TestScopes.RaceA200, CancellationToken.None);
        var e300 = await provider.FetchEntriesAsync(TestScopes.RaceA300, CancellationToken.None);
        return e200.Concat(e300)
            .DistinctBy(e => e.EntryId)
            .OrderBy(e => e.EntryId, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task EachCategoryReturnsOnlyEntriesOfItsType()
    {
        var settings = new AppSettings { DemoAutoAdvance = false, DemoReleasedCount = 100 };
        var provider = new MockDataProvider(settings, new InMemoryRepository());

        var e200 = await provider.FetchEntriesAsync(TestScopes.RaceA200, CancellationToken.None);
        var e300 = await provider.FetchEntriesAsync(TestScopes.RaceA300, CancellationToken.None);

        Assert.NotEmpty(e200);
        Assert.NotEmpty(e300);
        Assert.All(e200, e => Assert.Contains("200", e.RaceTypes));
        Assert.All(e300, e => Assert.Contains("300", e.RaceTypes));
    }

    [Fact]
    public async Task StartsWithOneEntry_AndManualAdvanceReleasesExactlyOne()
    {
        var settings = new AppSettings { DemoAutoAdvance = false, DemoReleasedCount = 1 };
        var provider = new MockDataProvider(settings, new InMemoryRepository());

        var first = await FetchAll(provider);
        Assert.Single(first);

        var count = await provider.SimulateNextEntryAsync(CancellationToken.None);
        var second = await FetchAll(provider);

        Assert.Equal(2, count);
        Assert.Equal(2, second.Count);
        Assert.NotEqual(second[0].CardNumber, second[1].CardNumber);
    }

    [Fact]
    public async Task ReleasesOneHundredEntriesIdentifiedByCardNumber()
    {
        var settings = new AppSettings { DemoAutoAdvance = false, DemoReleasedCount = 1 };
        var provider = new MockDataProvider(settings, new InMemoryRepository());
        for (var i = 1; i < 100; i++)
            await provider.SimulateNextEntryAsync(CancellationToken.None);

        var entries = await FetchAll(provider);

        Assert.Equal(100, entries.Count);
        // Card numbers follow the real format and are unique; no invented entry numbers.
        Assert.Equal("1000AAA", entries[0].CardNumber);
        Assert.Equal(100, entries.Select(e => e.CardNumber).Distinct().Count());
        Assert.All(entries, e => Assert.StartsWith("1000A", e.CardNumber));
    }

    [Fact]
    public async Task DemoDataExercisesTamilTextAndBothSecondaryCases()
    {
        var settings = new AppSettings { DemoAutoAdvance = false, DemoReleasedCount = 1 };
        var provider = new MockDataProvider(settings, new InMemoryRepository());
        for (var i = 1; i < 100; i++)
            await provider.SimulateNextEntryAsync(CancellationToken.None);

        var entries = await FetchAll(provider);

        Assert.Contains(entries, e => LayoutTextFitter.ContainsTamil(e.PrimaryName));
        Assert.Contains(entries, e => LayoutTextFitter.ContainsTamil(e.PrimaryLocation));
        Assert.Contains(entries, e => e.HasSecondary);
        Assert.Contains(entries, e => !e.HasSecondary);
        Assert.All(entries, e => Assert.True(e.TimingSeconds > 0));
    }

    [Fact]
    public async Task AnEntryWithoutASecondaryPlayerDisplaysADash()
    {
        var settings = new AppSettings { DemoAutoAdvance = false, DemoReleasedCount = 1 };
        var provider = new MockDataProvider(settings, new InMemoryRepository());
        for (var i = 1; i < 100; i++)
            await provider.SimulateNextEntryAsync(CancellationToken.None);

        var entries = await FetchAll(provider);
        var solo = entries.First(e => !e.HasSecondary);

        Assert.Equal("—", solo.SecondaryDisplay);
    }
}
