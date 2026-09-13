using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Providers;

/// <summary>
/// Deterministic stand-in for the race API.
///
/// It produces the same shape of data as <see cref="RaceApiPayloadAdapter"/> —
/// card numbers as the entry identifier, Tamil names and locations, an optional
/// secondary player, a timing in seconds — so the whole application, including
/// FFmpeg rendering, can be exercised without the real server.
///
/// Entries are released one at a time, because that is how they arrive in reality.
/// </summary>
public sealed class MockDataProvider : IEntryDataProvider, IDemoEntryController
{
    private readonly AppSettings _settings;
    private readonly ILocalStateRepository _repository;
    private readonly List<RaceEntry> _entries;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastReleaseUtc = DateTimeOffset.UtcNow;
    private long _pollNumber;
    private readonly Dictionary<string, long> _releasedAtPoll = new(StringComparer.OrdinalIgnoreCase);

    public MockDataProvider(AppSettings settings, ILocalStateRepository repository)
    {
        _settings = settings;
        _repository = repository;
        _entries = CreateDeterministicEntries();
        _settings.DemoReleasedCount = Math.Clamp(_settings.DemoReleasedCount, 1, _entries.Count);
        for (var i = 0; i < _settings.DemoReleasedCount; i++)
            _releasedAtPoll[_entries[i].EntryId] = 0;
    }

    public string Name => "DEMO / MOCK";
    public int AvailableCount => _settings.DemoReleasedCount;

    /// <summary>
    /// Every race gets the same demo card numbers, filtered to the category's type.
    /// That is deliberate: it shows that card 1000AAA in one race and 1000AAA in
    /// another are separate entries with separate local state.
    /// </summary>
    public async Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(RaceScope scope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pollNumber++;
            var now = DateTimeOffset.UtcNow;
            if (_settings.DemoAutoAdvance &&
                _settings.DemoReleasedCount < _entries.Count &&
                now - _lastReleaseUtc >= TimeSpan.FromSeconds(_settings.DemoEntryIntervalSeconds))
            {
                ReleaseOne(now);
                await _repository.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);
            }

            var type = scope.Category == RaceCategory.Meter300 ? "300" : "200";
            var result = new List<RaceEntry>(_settings.DemoReleasedCount);
            for (var i = 0; i < _settings.DemoReleasedCount; i++)
            {
                var source = _entries[i];
                if (!source.RaceTypes.Contains(type))
                    continue;

                // Every 10th demo entry spends one poll in NOT_COMPLETED, then updates
                // to COMPLETED. This exercises remote-status changes without blocking
                // the ordinary demo workflow for most entries.
                var releasePoll = _releasedAtPoll.TryGetValue(source.EntryId, out var p) ? p : 0;
                var notYet = (i + 1) % 10 == 0 && _pollNumber <= releasePoll + 1;

                result.Add(source with
                {
                    ExtractionStatus = notYet ? RemoteExtractionStatus.NotCompleted : RemoteExtractionStatus.Completed,
                    RemoteStatusText = notYet ? "processing" : "completed"
                });
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> SimulateNextEntryAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settings.DemoReleasedCount < _entries.Count)
            {
                ReleaseOne(DateTimeOffset.UtcNow);
                await _repository.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);
            }
            return _settings.DemoReleasedCount;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings.DemoReleasedCount = 1;
            _releasedAtPoll.Clear();
            _releasedAtPoll[_entries[0].EntryId] = _pollNumber;
            _lastReleaseUtc = DateTimeOffset.UtcNow;
            await _repository.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Card number of the most recently released demo entry, for log messages.</summary>
    public string LastReleasedCardNumber
        => _entries[Math.Clamp(_settings.DemoReleasedCount - 1, 0, _entries.Count - 1)].CardNumber;

    private void ReleaseOne(DateTimeOffset now)
    {
        if (_settings.DemoReleasedCount >= _entries.Count)
            return;

        _settings.DemoReleasedCount++;
        var entry = _entries[_settings.DemoReleasedCount - 1];
        _releasedAtPoll[entry.EntryId] = _pollNumber;
        _lastReleaseUtc = now;
    }

    private static List<RaceEntry> CreateDeterministicEntries()
    {
        // Tamil-first, as the real data is. A few Latin names are mixed in so that
        // mixed-script layout is exercised too.
        string[] names =
        [
            "S கருப்புசாமி", "ராமசாமி", "குமார்", "முருகன்", "பழனிச்சாமி",
            "வெங்கடேஷ் ராமகுமார்", "சரவணன்", "பாலாஜி", "செந்தில் குமார்", "மோகன்ராஜ்",
            "தினேஷ்", "ராஜேஷ்", "மணிகண்டன்", "சதீஷ்", "அஜித் குமார்",
            "விஜயகுமார்", "ஹரிஹரன்", "நவீன்", "கோபாலகிருஷ்ணன்", "அருண் குமார்",
            "Arun Kumar", "Vignesh", "Prakash", "Karthik"
        ];

        string[] secondaryNames =
        [
            "ரமேஷ்", "கோகுல்", "முத்துக்குமார்", "சஞ்சய்", "தீபக்",
            "அஷ்வின்", "அரவிந்த்", "கணேஷ்", "சூர்யா", "கிருஷ்ணன்",
            "யோகேஷ்", "விமல்", "Ravi Kumar", "Deepak"
        ];

        string[] locations =
        [
            "கணியூர்", "கோயம்புத்தூர்", "மதுரை", "சேலம்", "திருச்சிராப்பள்ளி",
            "திருநெல்வேலி", "ஈரோடு", "தஞ்சாவூர்", "வேலூர்", "தூத்துக்குடி",
            "பொள்ளாச்சி", "கரூர்", "நாமக்கல்", "திண்டுக்கல்", "சிவகாசி",
            "உடுமலைப்பேட்டை", "பழநி", "அவினாசி", "சென்னை", "Coimbatore"
        ];

        var entries = new List<RaceEntry>(100);
        for (var i = 0; i < 100; i++)
        {
            var hasSecondary = i % 5 != 0 && i % 7 != 0;
            string[] types = (i % 3) switch
            {
                0 => ["200", "300"],
                1 => ["300"],
                _ => ["200"]
            };

            var seconds = 17.0 + (i * 7 % 14) + (i % 4) * 0.25;

            entries.Add(new RaceEntry
            {
                EntryId = $"demo-marker-{i + 1:000}",
                CardNumber = CardNumber(i),
                PrimaryName = names[i % names.Length],
                PrimaryLocation = locations[(i * 3) % locations.Length],
                SecondaryName = hasSecondary ? secondaryNames[(i * 5) % secondaryNames.Length] : null,
                SecondaryLocation = hasSecondary ? locations[(i * 11 + 4) % locations.Length] : null,
                TimingSeconds = Math.Round(seconds, 2),
                RaceTypes = types,
                ExtractionStatus = RemoteExtractionStatus.Completed,
                RemoteStatusText = "completed",
                // Most demo players have no video yet; every 8th already has one, which
                // exercises the replace-existing-video confirmation.
                VideoLink = i % 8 == 7 ? $"https://demo.invalid/uploads/existing-{i + 1:000}.mp4" : null,
                IsVideoEnabled = true,
                EntryDateUtc = new DateTimeOffset(2026, 8, 16, 1, 2, 52, TimeSpan.Zero).AddMinutes(i * 4),
                PlayerId = $"demo-player-{i + 1:000}",
                UserId = $"REK{i + 1:0000}",
                Marker = $"demo-marker-{i + 1:000}"
            });
        }
        return entries;
    }

    /// <summary>
    /// Card numbers follow the real format seen in the API (1000AAA, 1000AAB, …)
    /// rather than a sequential entry number, which the application never invents.
    /// </summary>
    private static string CardNumber(int index)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var first = alphabet[(index / 26) % 26];
        var second = alphabet[index % 26];
        return $"1000A{first}{second}";
    }
}
