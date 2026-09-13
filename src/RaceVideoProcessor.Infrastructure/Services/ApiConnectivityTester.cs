using RaceVideoProcessor.Core.Interfaces;

namespace RaceVideoProcessor.Infrastructure.Services;

public sealed class ApiConnectivityTester : IApiConnectivityTester
{
    private readonly HttpClient _httpClient;

    public ApiConnectivityTester(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<(bool Success, string Detail)> TestAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, "API URL is not a valid absolute URL.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}")
                : (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
