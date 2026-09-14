using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Tests;

internal sealed class TestLog : IAppLog
{
    public event EventHandler<string>? LineWritten;
    public string LogFilePath => Path.Combine(Path.GetTempPath(), "rvp-test.log");
    public List<string> Lines { get; } = [];
    public void Info(string message) { Lines.Add(message); LineWritten?.Invoke(this, message); }
    public void Warning(string message) { Lines.Add(message); LineWritten?.Invoke(this, message); }
    public void Error(string message) { Lines.Add(message); LineWritten?.Invoke(this, message); }
}

internal sealed class InMemoryRepository : ILocalStateRepository
{
    public List<Race> Races { get; } = [];
    public Dictionary<(string RaceId, RaceCategory Category, string EntryId), EntryLocalState> States { get; } = new();
    public AppSettings? Settings { get; set; }
    public SyncState SyncState { get; set; } = new(null, null, null);

    /// <summary>Every state written, in order, for asserting stage transitions.</summary>
    public List<EntryLocalState> Writes { get; } = [];

    public static (string, RaceCategory, string) Key(RaceScope scope, string entryId)
        => (scope.RaceId.ToUpperInvariant(), scope.Category, entryId.ToUpperInvariant());

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Race>> GetRacesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Race>>(Races.ToList());

    public Task<Race?> GetRaceAsync(string raceId, CancellationToken cancellationToken = default)
        => Task.FromResult(Races.FirstOrDefault(r => string.Equals(r.RaceId, raceId, StringComparison.OrdinalIgnoreCase)));

    public Task<Race> AddRaceAsync(Race race, CancellationToken cancellationToken = default)
    {
        if (Races.Any(r => string.Equals(r.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateRaceIdException(race.RaceId);
        Races.Add(race);
        return Task.FromResult(race);
    }

    public Task<bool> UpdateRaceAsync(Race race, CancellationToken cancellationToken = default)
    {
        var index = Races.FindIndex(r => string.Equals(r.RaceId, race.RaceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return Task.FromResult(false);
        Races[index] = Races[index] with { RaceName = race.RaceName, RaceDate = race.RaceDate };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyDictionary<string, RaceEntryCounts>> GetRaceEntryCountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, RaceEntryCounts>>(States.Keys
            .GroupBy(k => k.RaceId)
            .ToDictionary(g => g.Key, g => new RaceEntryCounts(g.Count(k => k.Category == RaceCategory.Meter200),
                g.Count(k => k.Category == RaceCategory.Meter300)), StringComparer.OrdinalIgnoreCase));

    public Task<DatabaseInfo> GetDatabaseInfoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DatabaseInfo("memory", 0, 3, Races.Count, States.Count));

    public Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> DeleteRaceAsync(string raceId, CancellationToken cancellationToken = default)
    {
        foreach (var key in States.Keys.Where(k => k.RaceId == raceId.ToUpperInvariant()).ToList())
            States.Remove(key);
        return Task.FromResult(Races.RemoveAll(r => string.Equals(r.RaceId, raceId, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    public Task<EntryLocalState?> GetEntryStateAsync(RaceScope scope, string entryId, CancellationToken cancellationToken = default)
        => Task.FromResult(States.TryGetValue(Key(scope, entryId), out var state) ? state.Clone() : null);

    public Task<IReadOnlyDictionary<string, EntryLocalState>> GetEntryStatesAsync(RaceScope scope, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, EntryLocalState>>(States.Values
            .Where(s => string.Equals(s.RaceId, scope.RaceId, StringComparison.OrdinalIgnoreCase) && s.Category == scope.Category)
            .ToDictionary(s => s.EntryId, s => s.Clone(), StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<EntryLocalState>> GetInFlightEntryStatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EntryLocalState>>(States.Values
            .Where(s => s.ProcessingStatus == LocalProcessingStatus.Processing || s.UploadStatus == UploadStatus.Uploading ||
                        s.AssignmentStatus == AssignmentStatus.Assigning)
            .Select(s => s.Clone()).ToList());

    public Task<int> CountEntryStatesAsync(string raceId, CancellationToken cancellationToken = default)
        => Task.FromResult(States.Keys.Count(k => k.RaceId == raceId.ToUpperInvariant()));

    public Task UpsertEntryStateAsync(EntryLocalState state, CancellationToken cancellationToken = default)
    {
        States[Key(state.Scope, state.EntryId)] = state.Clone();
        Writes.Add(state.Clone());
        return Task.CompletedTask;
    }

    public Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    public Task<SyncState> LoadSyncStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(SyncState);
    public Task SaveSyncStateAsync(SyncState state, CancellationToken cancellationToken = default) { SyncState = state; return Task.CompletedTask; }
}

internal sealed class StaticProvider : IEntryDataProvider
{
    private readonly Func<RaceScope, CancellationToken, Task<IReadOnlyList<RaceEntry>>> _fetch;
    public StaticProvider(IReadOnlyList<RaceEntry> entries) => _fetch = (_, _) => Task.FromResult(entries);
    public StaticProvider(Func<RaceScope, CancellationToken, Task<IReadOnlyList<RaceEntry>>> fetch) => _fetch = fetch;
    public string Name => "TEST";
    public List<RaceScope> Requested { get; } = [];
    public Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(RaceScope scope, CancellationToken cancellationToken)
    {
        Requested.Add(scope);
        return _fetch(scope, cancellationToken);
    }
}

internal sealed class StaticRouter(IEntryDataProvider provider) : IEntryProviderRouter
{
    public IEntryDataProvider Current => provider;
}

internal sealed class StaticAdapter(IReadOnlyList<RaceEntry> entries) : IRealApiPayloadAdapter
{
    public IReadOnlyList<RaceEntry> Parse(string payload) => entries;
}

internal sealed class StubProbe(VideoMetadata metadata) : IFfprobeService
{
    public Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(metadata with { Path = path });
}

internal sealed class NoNvenc : IEncoderCapabilityService
{
    public Task<EncoderCapability> DetectAsync(CancellationToken cancellationToken)
        => Task.FromResult(new EncoderCapability(false, "CPU / x264", "test"));
    public Task<(bool Success, string Detail)> TestNvencAsync(CancellationToken cancellationToken)
        => Task.FromResult((false, "test"));
}

/// <summary>
/// Font stub. Tests must not depend on which fonts the build machine has
/// installed, so they inject a known resolution instead.
/// </summary>
internal sealed class StubFontResolver : IFontResolver
{
    private readonly FontResolution _resolution;

    public StubFontResolver(string? path, bool supportsTamil = true)
        => _resolution = new FontResolution(path, "StubFont", supportsTamil, "test");

    public FontResolution Resolve() => _resolution;

    /// <summary>Noto Sans Tamil, shipped with the tests so rendering never depends on installed fonts.</summary>
    public static string TestTamilFont => Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansTamil.ttf");

    /// <summary>A resolver pointing at the bundled test font, for filter-building tests.</summary>
    public static StubFontResolver WithTempFont(string directory)
    {
        Directory.CreateDirectory(directory);
        return new StubFontResolver(TestTamilFont);
    }
}

internal static class TestEntries
{
    /// <summary>
    /// A representative entry. Tamil by default, because the real data is Tamil
    /// and layout/hashing must be exercised against it rather than against ASCII.
    /// </summary>
    public static RaceEntry Create(
        string entryId = "marker-1",
        string card = "1000AAA",
        string primary = "S கருப்புசாமி",
        string primaryLocation = "கணியூர்",
        string? secondary = "ரமேஷ்",
        string? secondaryLocation = "கோயம்புத்தூர்",
        double timing = 22.5,
        RemoteExtractionStatus status = RemoteExtractionStatus.Completed) => new()
        {
            EntryId = entryId,
            CardNumber = card,
            PrimaryName = primary,
            PrimaryLocation = primaryLocation,
            SecondaryName = secondary,
            SecondaryLocation = secondaryLocation,
            TimingSeconds = timing,
            RaceTypes = ["200", "300"],
            ExtractionStatus = status,
            RemoteStatusText = status.ToString().ToLowerInvariant(),
            PlayerId = "player-" + entryId,
            UserId = "REK0076",
            Marker = entryId
        };
}

internal static class TestScopes
{
    public static readonly RaceScope RaceA200 = new("AAA", RaceCategory.Meter200);
    public static readonly RaceScope RaceA300 = new("AAA", RaceCategory.Meter300);
    public static readonly RaceScope RaceB200 = new("BBB", RaceCategory.Meter200);

    public static EntryLocalState State(RaceScope scope, string entryId) => new()
    {
        RaceId = scope.RaceId,
        Category = scope.Category,
        EntryId = entryId
    };
}
