using System.Net;
using System.Text;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;
using RaceVideoProcessor.Infrastructure.Backend;
using RaceVideoProcessor.Infrastructure.Providers;

namespace RaceVideoProcessor.Tests;

/// <summary>A recorded request. Bodies are read at send time because content is disposed afterwards.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string Body);

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<SentRequest, int, HttpResponseMessage> _respond;
    public List<SentRequest> Requests { get; } = [];

    public RecordingHandler(Func<SentRequest, int, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new SentRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType, body);
        Requests.Add(sent);
        return _respond(sent, Requests.Count);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class RaceBackendClientTests
{
    private const string Base = "https://backend.test";
    private const string LoginOk = """{"user":{"id":"u1"},"tokens":{"access":{"token":"ACCESS-1"},"refresh":{"token":"REFRESH-1"}}}""";

    private static AppSettings Settings() => new()
    {
        LoginEmail = "operator@example.test",
        LoginPassword = "s3cret-password",
        LoginUrl = Base + "/v1/auth/login",
        Players200UrlTemplate = Base + "/v1/races/{raceId}/players/all?type={type}",
        Players300UrlTemplate = Base + "/v1/races/{raceId}/players/all?type={type}",
        Assign200UrlTemplate = Base + "/v1/races/{raceId}/player/{playerId}/video?type={type}",
        Assign300UrlTemplate = Base + "/v1/races/{raceId}/player/{playerId}/video?type={type}",
        MediaUploadUrl = Base + "/v1/media/upload"
    };

    private static (RaceBackendClient Client, RecordingHandler Handler, TestLog Log) Create(
        Func<SentRequest, int, HttpResponseMessage> respond, AppSettings? settings = null)
    {
        var handler = new RecordingHandler(respond);
        var log = new TestLog();
        return (new RaceBackendClient(new HttpClient(handler), settings ?? Settings(), log), handler, log);
    }

    private static bool IsLogin(SentRequest r) => r.Uri.AbsolutePath == "/v1/auth/login";

    // ---- Test 2: one Race ID, two categories -----------------------------------

    [Fact]
    public async Task BothCategoriesUseTheSameRaceIdAndDifferOnlyByType()
    {
        var (client, handler, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.OK, "[]"));

        await client.GetPlayersPayloadAsync(new RaceScope("AAA", RaceCategory.Meter200), CancellationToken.None);
        await client.GetPlayersPayloadAsync(new RaceScope("AAA", RaceCategory.Meter300), CancellationToken.None);

        var gets = handler.Requests.Where(r => r.Method == HttpMethod.Get).Select(r => r.Uri.PathAndQuery).ToList();
        Assert.Equal(["/v1/races/AAA/players/all?type=200", "/v1/races/AAA/players/all?type=300"], gets);
    }

    [Fact]
    public async Task LoginSendsTheConfiguredCredentialsAndUsesTheBearerToken()
    {
        var (client, handler, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.OK, "[]"));

        await client.GetPlayersPayloadAsync(TestScopes.RaceA200, CancellationToken.None);

        var login = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Contains("\"email\":\"operator@example.test\"", login.Body);
        Assert.Contains("\"password\":\"s3cret-password\"", login.Body);
        Assert.Equal("Bearer ACCESS-1", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task TheTokenIsReusedAcrossCalls()
    {
        var (client, handler, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.OK, "[]"));

        await client.GetPlayersPayloadAsync(TestScopes.RaceA200, CancellationToken.None);
        await client.GetPlayersPayloadAsync(TestScopes.RaceA300, CancellationToken.None);

        Assert.Single(handler.Requests, IsLogin);
    }

    // ---- 401 handling -----------------------------------------------------------

    [Fact]
    public async Task A401LogsInAgainAndRetriesTheSameRequestOnce()
    {
        var logins = 0;
        var (client, handler, _) = Create((r, _) =>
        {
            if (IsLogin(r))
                return RecordingHandler.Json(HttpStatusCode.OK, ++logins == 1 ? LoginOk : LoginOk.Replace("ACCESS-1", "ACCESS-2"));
            return r.Authorization == "Bearer ACCESS-1"
                ? RecordingHandler.Json(HttpStatusCode.Unauthorized, """{"message":"Please authenticate"}""")
                : RecordingHandler.Json(HttpStatusCode.OK, "[]");
        });

        await client.AssignAsync(new AssignmentRequest(TestScopes.RaceA300, "player-7", "marker-7", "https://media.test/v.mp4"),
            CancellationToken.None);

        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, patches.Count);
        Assert.Equal(patches[0].Uri, patches[1].Uri);
        Assert.Equal(patches[0].Body, patches[1].Body);
        Assert.Equal("Bearer ACCESS-2", patches[1].Authorization);
        Assert.Equal(2, logins);
    }

    [Fact]
    public async Task A401AfterLoggingInAgainIsAnAuthenticationFailureWithoutAThirdAttempt()
    {
        var (client, handler, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.Unauthorized, "{}"));

        var ex = await Assert.ThrowsAsync<BackendException>(() =>
            client.GetPlayersPayloadAsync(TestScopes.RaceA200, CancellationToken.None));

        Assert.Equal(BackendFailureKind.Authentication, ex.Kind);
        Assert.Equal(2, handler.Requests.Count(r => !IsLogin(r)));
        Assert.Equal(2, handler.Requests.Count(IsLogin));
    }

    [Fact]
    public async Task RejectedCredentialsAreAnAuthenticationFailureThatRevealsNoSecrets()
    {
        var (client, _, log) = Create((r, _) => RecordingHandler.Json(HttpStatusCode.Unauthorized,
            """{"code":401,"message":"Incorrect email or password"}"""));

        var ex = await Assert.ThrowsAsync<BackendException>(() =>
            client.GetPlayersPayloadAsync(TestScopes.RaceA200, CancellationToken.None));

        Assert.Equal(BackendFailureKind.Authentication, ex.Kind);
        Assert.Contains("Incorrect email or password", ex.Message);
        Assert.DoesNotContain("s3cret-password", ex.Message);
        Assert.All(log.Lines, l => Assert.DoesNotContain("s3cret-password", l));
    }

    [Fact]
    public async Task NoTokenOrPasswordEverReachesTheLog()
    {
        var logins = 0;
        var (client, _, log) = Create((r, _) =>
        {
            if (IsLogin(r)) { logins++; return RecordingHandler.Json(HttpStatusCode.OK, LoginOk); }
            return logins == 1 ? RecordingHandler.Json(HttpStatusCode.Unauthorized, "{}") : RecordingHandler.Json(HttpStatusCode.OK, "[]");
        });

        await client.GetPlayersPayloadAsync(TestScopes.RaceA200, CancellationToken.None);

        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, l =>
        {
            Assert.DoesNotContain("ACCESS-1", l);
            Assert.DoesNotContain("REFRESH-1", l);
            Assert.DoesNotContain("s3cret-password", l);
            Assert.DoesNotContain("Bearer", l);
        });
    }

    [Fact]
    public async Task MissingCredentialsFailWithoutCallingTheBackend()
    {
        var settings = Settings();
        settings.LoginPassword = string.Empty;
        var (client, handler, _) = Create((_, _) => RecordingHandler.Json(HttpStatusCode.OK, LoginOk), settings);

        var ex = await Assert.ThrowsAsync<BackendException>(() =>
            client.GetPlayersPayloadAsync(TestScopes.RaceA200, CancellationToken.None));

        Assert.Equal(BackendFailureKind.Authentication, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"tokens":{"access":{"token":"T1","expires":"x"},"refresh":{"token":"R"}}}""", "T1")]
    [InlineData("""{"accessToken":"T2","refreshToken":"R"}""", "T2")]
    [InlineData("""{"data":{"access_token":"T3"}}""", "T3")]
    [InlineData("""{"token":"T4"}""", "T4")]
    [InlineData("""{"status":true}""", null)]
    public void TheAccessTokenIsFoundInCommonLoginResponseShapes(string body, string? expected)
        => Assert.Equal(expected, RaceBackendClient.ExtractAccessToken(body));

    // ---- Assignment -------------------------------------------------------------

    [Fact]
    public async Task AssignmentPatchesTheSelectedRaceWithPlayerIdInThePathAndMarkerInTheBody()
    {
        var (client, handler, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.OK, "{}"));

        await client.AssignAsync(new AssignmentRequest(new RaceScope("6a8fa0dd70486e002831d000", RaceCategory.Meter200),
            "68b6bc77cfff6e0026a6f19c", "6aa645f13877a40027128230", "https://media.namadhurekla.com/uploads/x.mp4"),
            CancellationToken.None);

        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/v1/races/6a8fa0dd70486e002831d000/player/68b6bc77cfff6e0026a6f19c/video?type=200", patch.Uri.PathAndQuery);
        Assert.Equal("application/json", patch.ContentType);
        Assert.Equal("""{"videoLink":"https://media.namadhurekla.com/uploads/x.mp4","marker":"6aa645f13877a40027128230"}""", patch.Body);
    }

    [Fact]
    public async Task AssignmentHttpFailureIsReported()
    {
        var (client, _, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.InternalServerError, """{"message":"boom"}"""));

        var ex = await Assert.ThrowsAsync<BackendException>(() => client.AssignAsync(
            new AssignmentRequest(TestScopes.RaceA200, "p", "m", "https://l"), CancellationToken.None));

        Assert.Equal(BackendFailureKind.Http, ex.Kind);
        Assert.Equal(500, ex.StatusCode);
    }

    // ---- Upload -----------------------------------------------------------------

    [Fact]
    public async Task UploadStreamsTheFileAsMultipartFieldUploadReportsRealProgressAndReturnsTheLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvp-upload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "cart 100.mp4");
        var bytes = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(file, bytes);

        try
        {
            var (client, handler, _) = Create((r, _) => IsLogin(r)
                ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
                : RecordingHandler.Json(HttpStatusCode.OK,
                    """{"status":true,"message":"Files uploaded successfully","data":{"link":["https://media.namadhurekla.com/uploads/example.mp4"]}}"""));

            var reports = new List<UploadProgress>();
            var link = await client.UploadAsync(file, new SyncProgress<UploadProgress>(reports.Add), CancellationToken.None);

            Assert.Equal("https://media.namadhurekla.com/uploads/example.mp4", link);
            var upload = handler.Requests.Single(r => r.Uri.AbsolutePath == "/v1/media/upload");
            Assert.Equal("multipart/form-data", upload.ContentType);
            Assert.Contains("name=upload", upload.Body);
            Assert.Contains("filename=\"cart 100.mp4\"", upload.Body);

            Assert.True(reports.Count > 2);
            Assert.All(reports, r => Assert.Equal(bytes.Length, r.TotalBytes));
            Assert.Equal(0, reports[0].Percent);
            Assert.Equal(100, reports[^1].Percent);
            Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.BytesSent >= p.First.BytesSent));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData("""{"status":true,"data":{"link":["https://a/1.mp4","https://a/2.mp4"]}}""", "https://a/1.mp4")]
    [InlineData("""{"status":true,"data":{"link":"https://a/3.mp4"}}""", "https://a/3.mp4")]
    public void TheUploadedLinkIsDataLinkZero(string body, string expected)
        => Assert.Equal(expected, RaceBackendClient.ExtractUploadedLink(body));

    [Theory]
    [InlineData("""{"status":false,"message":"too large"}""")]
    [InlineData("""{"status":true,"data":{"link":[]}}""")]
    [InlineData("not json")]
    public void AnUploadResponseWithoutALinkIsAFailure(string body)
        => Assert.Throws<BackendException>(() => RaceBackendClient.ExtractUploadedLink(body));

    // ---- Players provider ---------------------------------------------------------

    [Fact]
    public async Task RealProviderParsesThePlayersOfTheRequestedScope()
    {
        var entry = TestEntries.Create();
        var (client, handler, _) = Create((r, _) => IsLogin(r)
            ? RecordingHandler.Json(HttpStatusCode.OK, LoginOk)
            : RecordingHandler.Json(HttpStatusCode.OK, "[]"));
        var provider = new RealApiDataProvider(client, new StaticAdapter([entry]));

        var result = await provider.FetchEntriesAsync(new RaceScope("BBB", RaceCategory.Meter300), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("/v1/races/BBB/players/all?type=300", handler.Requests.Last().Uri.PathAndQuery);
    }

    [Fact]
    public void AUrlCannotBeBuiltWithoutARaceId()
        => Assert.Throws<InvalidOperationException>(() => BackendUrls.Players(Settings(), new RaceScope(" ", RaceCategory.Meter200)));

    /// <summary>Reports synchronously, so assertions see every report in order.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
