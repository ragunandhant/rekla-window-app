using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Backend;

namespace RaceVideoProcessor.Infrastructure.Providers;

/// <summary>Players from the backend for the selected race's Race ID and the category's type.</summary>
public sealed class RealApiDataProvider : IEntryDataProvider
{
    private readonly RaceBackendClient _backend;
    private readonly IRealApiPayloadAdapter _adapter;

    public RealApiDataProvider(RaceBackendClient backend, IRealApiPayloadAdapter adapter)
    {
        _backend = backend;
        _adapter = adapter;
    }

    public string Name => "REAL API";

    public async Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(RaceScope scope, CancellationToken cancellationToken)
    {
        var payload = await _backend.GetPlayersPayloadAsync(scope, cancellationToken).ConfigureAwait(false);
        return _adapter.Parse(payload);
    }
}
