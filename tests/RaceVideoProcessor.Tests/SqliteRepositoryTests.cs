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
}
