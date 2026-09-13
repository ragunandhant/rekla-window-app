using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Infrastructure.Backend;

/// <summary>
/// The race backend: login, players, media upload and video assignment.
///
/// Authentication: a bearer token is obtained by logging in and reused. When an
/// authenticated call answers 401 the client logs in again and repeats that exact
/// call once — never more, and there is no refresh endpoint. A 401 after that is
/// an authentication failure.
///
/// Nothing sensitive leaves this class: the password, the token and the
/// Authorization header never appear in exceptions or log lines.
/// </summary>
public sealed class RaceBackendClient : IMediaPublisher, IBackendDiagnostics
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _accessToken;

    /// <param name="http">Must not impose its own timeout: uploads of large videos take minutes.</param>
    public RaceBackendClient(HttpClient http, AppSettings settings, IAppLog log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    public string Name => "backend";

    // ---- Players -------------------------------------------------------------

    public async Task<string> GetPlayersPayloadAsync(RaceScope scope, CancellationToken cancellationToken)
    {
        var url = BackendUrls.Players(_settings, scope);
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), RequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Player refresh").ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Upload --------------------------------------------------------------

    public async Task<string> UploadAsync(string filePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
    {
        var url = ParseUrl(_settings.MediaUploadUrl, "Media upload URL");
        var fileName = Path.GetFileName(filePath);

        // A retry after 401 needs a fresh body, so the request is rebuilt from the file each time.
        HttpRequestMessage Build()
        {
            var file = new ProgressFileContent(filePath, progress);
            file.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(filePath));
            var form = new MultipartFormDataContent { { file, "upload", fileName } };
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }

        using var response = await SendAuthenticatedAsync(Build, timeout: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Upload").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ExtractUploadedLink(body);
    }

    /// <summary>Reads <c>data.link[0]</c> from the upload response.</summary>
    internal static string ExtractUploadedLink(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
                throw new BackendException(BackendFailureKind.InvalidResponse,
                    "the server reported the upload as unsuccessful" + MessageSuffix(root));

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("link", out var link))
            {
                var value = link.ValueKind switch
                {
                    JsonValueKind.Array when link.GetArrayLength() > 0 && link[0].ValueKind == JsonValueKind.String => link[0].GetString(),
                    JsonValueKind.String => link.GetString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }
        catch (JsonException)
        {
        }

        throw new BackendException(BackendFailureKind.InvalidResponse, "the upload response did not contain data.link[0].");
    }

    // ---- Assignment ----------------------------------------------------------

    public async Task AssignAsync(AssignmentRequest request, CancellationToken cancellationToken)
    {
        // Race ID and type in the URL, player id in the path, marker in the body.
        var url = BackendUrls.Assign(_settings, request.Scope, request.PlayerId);
        var json = JsonSerializer.Serialize(new { videoLink = request.VideoLink, marker = request.Marker });

        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            },
            RequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Assignment").ConfigureAwait(false);
    }

    // ---- Diagnostics ---------------------------------------------------------

    public async Task<(bool Success, string Detail)> TestLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
            return (true, "Login succeeded.");
        }
        catch (BackendException ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- Authentication ------------------------------------------------------

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Func<HttpRequestMessage> buildRequest, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var token = _accessToken ?? await LoginAsync(cancellationToken).ConfigureAwait(false);

        var response = await SendAsync(buildRequest, token, timeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        _log.Info("Backend answered 401; logging in again and retrying the request once.");
        token = await LoginAsync(cancellationToken, staleToken: token).ConfigureAwait(false);

        response = await SendAsync(buildRequest, token, timeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _accessToken = null;
            throw new BackendException(BackendFailureKind.Authentication,
                "the backend rejected the request even after logging in again (401).", 401);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> buildRequest, string token, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var request = buildRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
            timeoutCts.CancelAfter(limit);

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendException(BackendFailureKind.Network, $"{request.Method} {request.RequestUri?.AbsolutePath} timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new BackendException(BackendFailureKind.Network, "the backend could not be reached: " + ex.Message, inner: ex);
        }
    }

    /// <param name="staleToken">
    /// The token that just failed. If another call already replaced it, that new
    /// token is used instead of logging in a second time.
    /// </param>
    private async Task<string> LoginAsync(CancellationToken cancellationToken, string? staleToken = null)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && (staleToken is null || !string.Equals(_accessToken, staleToken, StringComparison.Ordinal)))
                return _accessToken;

            if (string.IsNullOrWhiteSpace(_settings.LoginEmail) || string.IsNullOrEmpty(_settings.LoginPassword))
                throw new BackendException(BackendFailureKind.Authentication,
                    "login email and password are not set. Enter them in Settings.");

            var url = ParseUrl(_settings.LoginUrl, "Login URL");
            var json = JsonSerializer.Serialize(new { email = _settings.LoginEmail, password = _settings.LoginPassword });
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BackendException(BackendFailureKind.Network, "login timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new BackendException(BackendFailureKind.Network, "the backend could not be reached: " + ex.Message, inner: ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _accessToken = null;
                    var code = (int)response.StatusCode;
                    throw new BackendException(
                        code is 400 or 401 or 403 or 404 or 422 ? BackendFailureKind.Authentication : BackendFailureKind.Http,
                        $"login failed (HTTP {code})" + MessageSuffix(body) + ".", code);
                }

                _accessToken = ExtractAccessToken(body)
                    ?? throw new BackendException(BackendFailureKind.Authentication, "login succeeded but no access token was returned.");
                _log.Info("Logged in to the backend.");
                return _accessToken;
            }
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// Finds the access token in a login response. Handles
    /// <c>tokens.access.token</c>, <c>accessToken</c>, <c>access_token</c> and
    /// <c>token</c>, at the top level or under <c>data</c>. Refresh tokens are ignored.
    /// </summary>
    internal static string? ExtractAccessToken(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return FindToken(document.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindToken(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 4)
            return null;

        foreach (var name in new[] { "accessToken", "access_token", "token" })
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        foreach (var name in new[] { "tokens", "access", "data", "result" })
        {
            if (element.TryGetProperty(name, out var child) && FindToken(child, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    // ---- Helpers -------------------------------------------------------------

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var code = (int)response.StatusCode;
        throw new BackendException(BackendFailureKind.Http,
            $"{operation} failed (HTTP {code} {response.ReasonPhrase})" + MessageSuffix(body) + ".", code);
    }

    /// <summary>The server's own "message", when it sent one, for a readable error.</summary>
    private static string MessageSuffix(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return MessageSuffix(document.RootElement);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string MessageSuffix(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var message) &&
           message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString())
            ? ": " + Truncate(message.GetString()!, 200)
            : string.Empty;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static Uri ParseUrl(string value, string name)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new BackendException(BackendFailureKind.Configuration, $"{name} is not a valid absolute URL.");

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".ts" or ".mts" or ".m2ts" => "video/mp2t",
        _ => "application/octet-stream"
    };
}
