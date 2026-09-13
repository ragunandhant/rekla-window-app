using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Data;

namespace RaceVideoProcessor.Tests;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public async Task ProcessingHistoryAndVideoMappingSurviveRepositoryRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "state.db");
        try
        {
            var repo1 = new SqliteLocalStateRepository(db);
            await repo1.InitializeAsync();
            await repo1.UpsertEntryStateAsync(new EntryLocalState
            {
                EntryId = "001",
                LocalVideoPath = @"D:\RaceVideos\Race_001.mp4",
                OutputPath = @"D:\RaceVideos\Processed\Race_001.mp4",
                ProcessingStatus = LocalProcessingStatus.Completed,
                LastProcessedDataHash = "ABC",
                ProcessedUtc = DateTimeOffset.UtcNow
            });

            var repo2 = new SqliteLocalStateRepository(db);
            await repo2.InitializeAsync();
            var restored = await repo2.GetEntryStateAsync("001");

            Assert.NotNull(restored);
            Assert.Equal(LocalProcessingStatus.Completed, restored!.ProcessingStatus);
            Assert.EndsWith("Race_001.mp4", restored.LocalVideoPath);
            Assert.Equal("ABC", restored.LastProcessedDataHash);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task DefaultSettingsCanBeSaved()
    {
        // Startup saves settings before the window is shown, so anything that
        // cannot be serialised takes the whole application down with it. A
        // default AppSettings has no window position yet; that must round-trip.
        var root = Path.Combine(Path.GetTempPath(), "rvp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "state.db");
        try
        {
            var repo = new SqliteLocalStateRepository(db);
            await repo.InitializeAsync();
            var settings = new AppSettings();
            settings.Normalize();

            await repo.SaveSettingsAsync(settings);
            var restored = await repo.LoadSettingsAsync();

            Assert.NotNull(restored);
            Assert.Null(restored!.WindowLeft);
            Assert.Null(restored.WindowTop);
            Assert.Equal(settings.TimingFormat, restored.TimingFormat);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task SettingsWithWindowPlacementRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "state.db");
        try
        {
            var repo = new SqliteLocalStateRepository(db);
            await repo.InitializeAsync();
            var settings = new AppSettings
            {
                WindowLeft = 120,
                WindowTop = 64,
                WindowWidth = 1600,
                WindowHeight = 900,
                WindowMaximized = true
            };

            await repo.SaveSettingsAsync(settings);
            var restored = await repo.LoadSettingsAsync();

            Assert.Equal(120, restored!.WindowLeft);
            Assert.Equal(64, restored.WindowTop);
            Assert.True(restored.WindowMaximized);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void NormalizeDropsANonFiniteWindowPosition()
    {
        var settings = new AppSettings { WindowLeft = double.NaN, WindowTop = double.PositiveInfinity };

        settings.Normalize();

        Assert.Null(settings.WindowLeft);
        Assert.Null(settings.WindowTop);
    }
}
