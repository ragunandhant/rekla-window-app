using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Tests;

internal sealed class TestLog : IAppLog
{
    public event EventHandler<string>? LineWritten;
    public string LogFilePath => Path.Combine(Path.GetTempPath(), "rvp-test.log");
    public List<string> Lines { get; } = [];
    public void Info(string message) { Lines.Add(message); LineWritten?.Invoke(this, message); }
    public void Error(string message) { Lines.Add(message); LineWritten?.Invoke(this, message); }
}

internal sealed class InMemoryRepository : ILocalStateRepository
{
    public Dictionary<string, EntryLocalState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AppSettings? Settings { get; set; }
    public SyncState SyncState { get; set; } = new(null, null, null);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<EntryLocalState?> GetEntryStateAsync(string entryId, CancellationToken cancellationToken = default)
        => Task.FromResult(States.TryGetValue(entryId, out var state) ? state.Clone() : null);
    public Task<IReadOnlyDictionary<string, EntryLocalState>> GetAllEntryStatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, EntryLocalState>>(States.ToDictionary(k => k.Key, v => v.Value.Clone(), StringComparer.OrdinalIgnoreCase));
    public Task UpsertEntryStateAsync(EntryLocalState state, CancellationToken cancellationToken = default)
    {
        States[state.EntryId] = state.Clone();
        return Task.CompletedTask;
    }
    public Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    public Task<SyncState> LoadSyncStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(SyncState);
    public Task SaveSyncStateAsync(SyncState state, CancellationToken cancellationToken = default) { SyncState = state; return Task.CompletedTask; }
}

internal sealed class StaticProvider : IEntryDataProvider
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<RaceEntry>>> _fetch;
    public StaticProvider(IReadOnlyList<RaceEntry> entries) => _fetch = _ => Task.FromResult(entries);
    public StaticProvider(Func<CancellationToken, Task<IReadOnlyList<RaceEntry>>> fetch) => _fetch = fetch;
    public string Name => "TEST";
    public Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(CancellationToken cancellationToken) => _fetch(cancellationToken);
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

    /// <summary>A resolver pointing at a real temporary file, for filter-building tests.</summary>
    public static StubFontResolver WithTempFont(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "stub-font.ttf");
        if (!File.Exists(path))
            File.WriteAllText(path, "not a real font, only a path");
        return new StubFontResolver(path);
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
            PlayerId = "player-1",
            UserId = "REK0076",
            Marker = entryId
        };
}
