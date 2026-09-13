using Microsoft.Data.Sqlite;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Data;

namespace RaceVideoProcessor.Tests;

public sealed class SqliteRepositoryTests
{
    private static Race RaceA => new() { RaceId = "AAA", RaceName = "Race A", RaceDate = new DateOnly(2026, 9, 13) };
    private static Race RaceB => new() { RaceId = "BBB", RaceName = "Race B", RaceDate = new DateOnly(2026, 9, 20) };

    private static async Task<T> WithDatabase<T>(Func<string, Task<T>> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            return await test(Path.Combine(root, "state.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static Task WithDatabase(Func<string, Task> test) => WithDatabase(async db => { await test(db); return 0; });

    private static async Task<SqliteLocalStateRepository> Open(string db)
    {
        var repo = new SqliteLocalStateRepository(db);
        await repo.InitializeAsync();
        return repo;
    }

    private static EntryLocalState FullState(RaceScope scope, string entryId, string cart) => new()
    {
        RaceId = scope.RaceId,
        Category = scope.Category,
        EntryId = entryId,
        CardNumber = cart,
        PlayerId = "player-" + entryId,
        Marker = entryId,
        EntryDateUtc = new DateTimeOffset(2026, 9, 13, 6, 42, 55, 314, TimeSpan.Zero),
        LocalVideoPath = $@"D:\RaceVideos\{scope.RaceId}-{cart}.mp4",
        OutputPath = $@"D:\RaceVideos\Processed\{scope.RaceId}-{cart}.mp4",
        ProcessingStatus = LocalProcessingStatus.Completed,
        UploadStatus = UploadStatus.Completed,
        UploadedVideoLink = $"https://media.test/{scope.RaceId}-{cart}.mp4",
        AssignmentStatus = AssignmentStatus.Failed,
        LastFailureWasAuthentication = true,
        ErrorMessage = "Authentication Failed: 401",
        LastProcessedDataHash = "ABC",
        ProcessedUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task EntryStateSurvivesRepositoryRestartWithEveryStage() => await WithDatabase(async db =>
    {
        var repo1 = await Open(db);
        await repo1.AddRaceAsync(RaceA);
        await repo1.UpsertEntryStateAsync(FullState(TestScopes.RaceA200, "marker-1", "100"));

        var repo2 = await Open(db);
        var restored = await repo2.GetEntryStateAsync(TestScopes.RaceA200, "marker-1");

        Assert.NotNull(restored);
        Assert.Equal("AAA", restored!.RaceId);
        Assert.Equal(RaceCategory.Meter200, restored.Category);
        Assert.Equal("100", restored.CardNumber);
        Assert.Equal("player-marker-1", restored.PlayerId);
        Assert.Equal("marker-1", restored.Marker);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 6, 42, 55, 314, TimeSpan.Zero), restored.EntryDateUtc);
        Assert.EndsWith("AAA-100.mp4", restored.LocalVideoPath);
        Assert.Equal(LocalProcessingStatus.Completed, restored.ProcessingStatus);
        Assert.Equal(UploadStatus.Completed, restored.UploadStatus);
        Assert.Equal("https://media.test/AAA-100.mp4", restored.UploadedVideoLink);
        Assert.Equal(AssignmentStatus.Failed, restored.AssignmentStatus);
        Assert.True(restored.LastFailureWasAuthentication);
        Assert.Equal("ABC", restored.LastProcessedDataHash);
    });

    // ---- Test 1 and 9: create races, duplicate Race ID -------------------------------

    [Fact]
    public async Task RacesAreCreatedAndListedAndSurviveRestart() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        var a = await repo.AddRaceAsync(RaceA);
        await repo.AddRaceAsync(RaceB);

        var races = await (await Open(db)).GetRacesAsync();

        Assert.True(a.Id > 0);
        Assert.Equal(2, races.Count);
        Assert.Contains(races, r => r.RaceId == "AAA" && r.RaceName == "Race A" && r.RaceDateDisplay == "13-09-2026");
        Assert.Contains(races, r => r.RaceId == "BBB" && r.RaceDateDisplay == "20-09-2026");
    });

    [Fact]
    public async Task ADuplicateRaceIdIsRejectedEvenWithDifferentCaseOrName() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.AddRaceAsync(RaceA);

        await Assert.ThrowsAsync<DuplicateRaceIdException>(() =>
            repo.AddRaceAsync(RaceA with { RaceId = "aaa", RaceName = "Another name" }));
        Assert.Single(await repo.GetRacesAsync());
    });

    // ---- Test 7: delete a race --------------------------------------------------------

    [Fact]
    public async Task DeletingARaceRemovesAllItsRecordsAndLeavesOtherRacesUntouched() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.AddRaceAsync(RaceA);
        await repo.AddRaceAsync(RaceB);
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceA200, "m1", "100"));
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceA200, "m2", "101"));
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceA300, "m1", "100"));
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceB200, "m1", "100"));
        Assert.Equal(3, await repo.CountEntryStatesAsync("AAA"));

        Assert.True(await repo.DeleteRaceAsync("AAA"));

        var restarted = await Open(db);
        Assert.Null(await restarted.GetRaceAsync("AAA"));
        Assert.Equal(0, await restarted.CountEntryStatesAsync("AAA"));
        Assert.Empty(await restarted.GetEntryStatesAsync(TestScopes.RaceA200));
        Assert.Empty(await restarted.GetEntryStatesAsync(TestScopes.RaceA300));

        Assert.NotNull(await restarted.GetRaceAsync("BBB"));
        var b = Assert.Single(await restarted.GetEntryStatesAsync(TestScopes.RaceB200)).Value;
        Assert.Equal("https://media.test/BBB-100.mp4", b.UploadedVideoLink);
    });

    [Fact]
    public async Task StateWrittenAfterItsRaceWasDeletedIsNotResurrected() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.AddRaceAsync(RaceA);
        await repo.DeleteRaceAsync("AAA");

        // A job that was finishing when the race was deleted.
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceA200, "m1", "100"));

        Assert.Equal(0, await repo.CountEntryStatesAsync("AAA"));
    });

    [Fact]
    public async Task DeletingAnUnknownRaceReportsFalseAndChangesNothing() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.AddRaceAsync(RaceB);
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceB200, "m1", "100"));

        Assert.False(await repo.DeleteRaceAsync("ZZZ"));
        Assert.Equal(1, await repo.CountEntryStatesAsync("BBB"));
    });

    // ---- Test 10: same cart number in two races -----------------------------------------

    [Fact]
    public async Task TheSameCartAndMarkerInTwoRacesAreSeparateRecords() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.AddRaceAsync(RaceA);
        await repo.AddRaceAsync(RaceB);

        var inA = FullState(TestScopes.RaceA200, "same-marker", "100");
        var inB = FullState(TestScopes.RaceB200, "same-marker", "100");
        inB.AssignmentStatus = AssignmentStatus.Completed;
        inB.ErrorMessage = null;
        await repo.UpsertEntryStateAsync(inA);
        await repo.UpsertEntryStateAsync(inB);

        var a = await repo.GetEntryStateAsync(TestScopes.RaceA200, "same-marker");
        var b = await repo.GetEntryStateAsync(TestScopes.RaceB200, "same-marker");

        Assert.Equal(AssignmentStatus.Failed, a!.AssignmentStatus);
        Assert.Equal(AssignmentStatus.Completed, b!.AssignmentStatus);
        Assert.EndsWith("AAA-100.mp4", a.LocalVideoPath);
        Assert.EndsWith("BBB-100.mp4", b.LocalVideoPath);

        // And the categories of one race are separate too.
        Assert.Null(await repo.GetEntryStateAsync(TestScopes.RaceA300, "same-marker"));
    });

    [Fact]
    public async Task InFlightStatesAreFoundAcrossRacesForRecovery() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.AddRaceAsync(RaceA);
        await repo.AddRaceAsync(RaceB);
        var uploading = FullState(TestScopes.RaceA200, "m1", "100");
        uploading.UploadStatus = UploadStatus.Uploading;
        var processing = FullState(TestScopes.RaceB200, "m2", "200");
        processing.ProcessingStatus = LocalProcessingStatus.Processing;
        await repo.UpsertEntryStateAsync(uploading);
        await repo.UpsertEntryStateAsync(processing);
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceB200, "m3", "300"));

        var inFlight = await repo.GetInFlightEntryStatesAsync();

        Assert.Equal(["m1", "m2"], inFlight.Select(s => s.EntryId).OrderBy(x => x));
    });

    [Fact]
    public async Task AnExistingVersionOneDatabaseUpgradesInPlace() => await WithDatabase(async db =>
    {
        // A database created by the previous build: unscoped entry state only.
        await using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE entry_local_state (entry_id TEXT PRIMARY KEY, local_video_path TEXT NULL,
                    processing_status TEXT NOT NULL, output_path TEXT NULL, error_message TEXT NULL,
                    last_processed_data_hash TEXT NULL, last_seen_data_hash TEXT NULL, last_seen_utc TEXT NULL,
                    processed_utc TEXT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO entry_local_state VALUES ('old', 'D:\old.mp4', 'Completed', NULL, NULL, NULL, NULL, NULL, NULL, '2026-01-01T00:00:00Z');
                CREATE TABLE app_settings (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO app_settings VALUES (1, '{"dataSourceMode":1,"apiUrl":"https://old.example/api","settingsVersion":2}', '2026-01-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repo = await Open(db);
        await repo.AddRaceAsync(RaceA);
        await repo.UpsertEntryStateAsync(FullState(TestScopes.RaceA200, "m1", "100"));
        var settings = await repo.LoadSettingsAsync();

        Assert.Equal(1, await repo.CountEntryStatesAsync("AAA"));
        Assert.Equal(DataSourceMode.RealApi, settings!.DataSourceMode);
        Assert.Null(settings.SelectedRaceId);
        Assert.Contains("{raceId}", settings.Players200UrlTemplate);
    });

    [Fact]
    public async Task ThePasswordIsNotStoredInPlainTextAndRoundTrips() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.SaveSettingsAsync(new AppSettings { LoginEmail = "op@example.test", LoginPassword = "p@ss-Word-123" });

        await using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM app_settings WHERE id = 1;";
            var json = (string)(await command.ExecuteScalarAsync())!;
            Assert.DoesNotContain("p@ss-Word-123", json);
        }

        var restored = await (await Open(db)).LoadSettingsAsync();
        Assert.Equal("p@ss-Word-123", restored!.LoginPassword);
        Assert.Equal("op@example.test", restored.LoginEmail);
    });

    [Fact]
    public async Task SelectedRaceCategoryAndUploadSettingRoundTripWithoutPerCategoryRaceIds() => await WithDatabase(async db =>
    {
        var repo = await Open(db);
        await repo.SaveSettingsAsync(new AppSettings
        {
            SelectedRaceId = "6a8fa0dd70486e002831d000",
            SelectedCategory = RaceCategory.Meter300,
            UploadEnabled = false
        });

        var restored = await (await Open(db)).LoadSettingsAsync();

        Assert.Equal("6a8fa0dd70486e002831d000", restored!.SelectedRaceId);
        Assert.Equal(RaceCategory.Meter300, restored.SelectedCategory);
        Assert.False(restored.UploadEnabled);
        Assert.DoesNotContain(typeof(AppSettings).GetProperties(), p =>
            p.Name.Contains("RaceId", StringComparison.OrdinalIgnoreCase) && p.Name != nameof(AppSettings.SelectedRaceId));
    });

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
