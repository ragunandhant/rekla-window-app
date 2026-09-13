using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;
using RaceVideoProcessor.Infrastructure.Providers;

namespace RaceVideoProcessor.Tests;

public sealed class MockDataProviderTests
{
    [Fact]
    public async Task StartsWithOneEntry_AndManualAdvanceReleasesExactlyOne()
    {
        var settings = new AppSettings { DemoAutoAdvance = false, DemoReleasedCount = 1 };
        var provider = new MockDataProvider(settings, new InMemoryRepository());

        var first = await provider.FetchEntriesAsync(CancellationToken.None);
        Assert.Single(first);

        var count = await provider.SimulateNextEntryAsync(CancellationToken.None);
        var second = await provider.FetchEntriesAsync(CancellationToken.None);

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

        var entries = await provider.FetchEntriesAsync(CancellationToken.None);

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

        var entries = await provider.FetchEntriesAsync(CancellationToken.None);

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

        var entries = await provider.FetchEntriesAsync(CancellationToken.None);
        var solo = entries.First(e => !e.HasSecondary);

        Assert.Equal("—", solo.SecondaryDisplay);
    }
}
