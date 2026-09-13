using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Providers;

public sealed class RealApiDataProvider : IEntryDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;
    private readonly IRealApiPayloadAdapter _adapter;

    public RealApiDataProvider(HttpClient httpClient, AppSettings settings, IRealApiPayloadAdapter adapter)
    {
        _httpClient = httpClient;
        _settings = settings;
        _adapter = adapter;
    }

    public string Name => "REAL API";

    public async Task<IReadOnlyList<RaceEntry>> FetchEntriesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiUrl))
            throw new InvalidOperationException("Real API URL is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Get, _settings.ApiUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return _adapter.Parse(payload);
    }
}
