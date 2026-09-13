using System.Net;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Infrastructure.Providers;

namespace RaceVideoProcessor.Tests;

public sealed class RealApiDataProviderTests
{
    [Fact]
    public async Task SuccessPassesPayloadToAdapter()
    {
        var entry = TestEntries.Create();
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"anything\":true}")
        }));
        var provider = new RealApiDataProvider(client,
            new AppSettings { ApiUrl = "https://example.test/race" }, new StaticAdapter([entry]));

        var result = await provider.FetchEntriesAsync(CancellationToken.None);
        Assert.Single(result);
        Assert.Equal("1000AAA", result[0].CardNumber);
    }

    [Fact]
    public async Task HttpFailureIsSurfaced()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var provider = new RealApiDataProvider(client,
            new AppSettings { ApiUrl = "https://example.test/race" }, new StaticAdapter([]));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchEntriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationOrTimeoutIsSurfaced()
    {
        using var client = new HttpClient(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var provider = new RealApiDataProvider(client,
            new AppSettings { ApiUrl = "https://example.test/race" }, new StaticAdapter([]));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchEntriesAsync(cts.Token));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = (request, _) => Task.FromResult(handler(request));
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
