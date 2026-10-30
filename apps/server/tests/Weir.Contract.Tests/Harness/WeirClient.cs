using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Weir.Contract.Tests.Harness;

/// <summary>
/// One browser-like session against one server: its own cookie jar, CSRF handling and sign-in. Returns raw
/// responses so a test asserts on status codes, headers and bodies exactly as a client would see them.
/// </summary>
public sealed class WeirClient : IDisposable
{
    public const string Api = "/api/v1";
    public const string AdminUsername = "alice";
    public const string AdminPassword = "test-password-strong";

    private const string CsrfTokenField = "csrf_token";
    private const string CsrfTokenHeader = "X-CSRF-Token";

    // Above the server's 30 s SQLite busy timeout, so a lock problem shows up as the server's own error
    // rather than as a client that gave up at the same instant (#586). Keep it strictly greater.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    // A browser or media manager always says what it accepts.
    private const string DefaultAccept = "*/*";

    private readonly Uri _baseAddress;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;

    // defaultHeaders go on every request, such as Origin and X-Requested-With for a browser-like client; a header
    // named here replaces the built-in default of the same name (Accept).
    public Uri BaseAddress => _baseAddress;

    public WeirClient(Uri baseAddress, IReadOnlyDictionary<string, string>? defaultHeaders = null)
    {
        _baseAddress = baseAddress;
        _handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = _cookies,
        };
        _http = new HttpClient(_handler) { BaseAddress = baseAddress, Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", DefaultAccept);
        foreach (var (name, value) in defaultHeaders ?? new Dictionary<string, string>())
        {
            _http.DefaultRequestHeaders.Remove(name);
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    // --- cookies --------------------------------------------------------------------------------

    /// <summary>The value of the cookie the jar would send to the server, or null.</summary>
    public string? Cookie(string name) => _cookies.GetCookies(_baseAddress)[name]?.Value;

    public void SetCookie(string name, string value) => _cookies.Add(_baseAddress, new Cookie(name, value, "/"));

    public void ClearCookies()
    {
        foreach (Cookie cookie in _cookies.GetAllCookies())
        {
            cookie.Expired = true;
        }
    }

    // --- requests -------------------------------------------------------------------------------

    public Task<WeirResponse> HeadAsync(string path) => SendAsync(HttpMethod.Head, path, content: null);

    public Task<WeirResponse> OptionsAsync(string path, IReadOnlyDictionary<string, string> headers) =>
        SendAsync(HttpMethod.Options, path, content: null, headers);


    public Task<WeirResponse> GetAsync(string path, params (string Name, object Value)[] query) =>
        SendAsync(HttpMethod.Get, WithQuery(path, query), content: null);

    /// <summary>GET with extra headers for this request, which replace a default header of the same name.</summary>
    public Task<WeirResponse> GetAsync(string path, IReadOnlyDictionary<string, string> headers, params (string Name, object Value)[] query) =>
        SendAsync(HttpMethod.Get, WithQuery(path, query), content: null, headers);

    /// <summary>Any method, with an optional JSON body and extra headers; the body is sent as given, without a CSRF token.</summary>
    public Task<WeirResponse> RequestAsync(
        HttpMethod method, string path, JsonNode? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(method, path, JsonBody(body), headers);

    /// <summary>
    /// Any method with <paramref name="body"/> exactly as given and declared as <paramref name="contentType"/> (no
    /// <c>Content-Type</c> at all when that is null), whether or not the bytes are what the type says.
    /// </summary>
    public Task<WeirResponse> RequestRawAsync(
        HttpMethod method, string path, byte[] body, string? contentType, IReadOnlyDictionary<string, string>? headers = null)
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return SendAsync(method, path, content, headers);
    }

    public Task<WeirResponse> PostAsync(string path, JsonNode? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Post, path, JsonBody(body), headers);

    /// <summary>POST with <paramref name="text"/> as the body exactly as given, declared as JSON, whether or not it is.</summary>
    public Task<WeirResponse> PostTextAsync(string path, string text, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Post, path, new StringContent(text, System.Text.Encoding.UTF8, "application/json"), headers);

    public Task<WeirResponse> DeleteAsync(string path, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Delete, path, content: null, headers);

    /// <summary>POST with a fresh <c>csrf_token</c> in the JSON body, the web app's shape.</summary>
    public Task<WeirResponse> PostWithCsrfAsync(
        string path, JsonObject? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendWithCsrfBodyAsync(HttpMethod.Post, path, body, headers);

    public Task<WeirResponse> PutWithCsrfAsync(
        string path, JsonObject? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendWithCsrfBodyAsync(HttpMethod.Put, path, body, headers);

    public Task<WeirResponse> PatchWithCsrfAsync(
        string path, JsonObject? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendWithCsrfBodyAsync(HttpMethod.Patch, path, body, headers);

    /// <summary>DELETE with the token in <c>X-CSRF-Token</c> (a DELETE has no body).</summary>
    public async Task<WeirResponse> DeleteWithCsrfAsync(string path) =>
        await DeleteAsync(path, new Dictionary<string, string> { [CsrfTokenHeader] = await CsrfTokenAsync() });

    /// <summary>DELETE carrying <c>csrf_token</c> in a JSON body, the shape the library, rule-set and connection delete routes take.</summary>
    public async Task<WeirResponse> DeleteWithCsrfBodyAsync(string path) =>
        await SendAsync(HttpMethod.Delete, path, JsonBody(new JsonObject { [CsrfTokenField] = await CsrfTokenAsync() }));

    /// <summary>Opens a server-sent event stream; only the headers are read before this returns.</summary>
    public async Task<SseReader> OpenStreamAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new SseReader(response, await response.Content.ReadAsStreamAsync());
    }

    // --- accounts -------------------------------------------------------------------------------

    public async Task<string> CsrfTokenAsync()
    {
        var response = await GetAsync($"{Api}/auth/csrf");
        Assert.True(response.Status == HttpStatusCode.OK, response.ToString());
        return (string)response.Fields[CsrfTokenField]!;
    }

    public async Task<WeirResponse> BootstrapAsync(string username = AdminUsername, string password = AdminPassword) =>
        await PostAsync($"{Api}/auth/bootstrap", Credentials(username, password, await CsrfTokenAsync()));

    /// <summary>
    /// Tries to sign in and returns whatever the server answers. <paramref name="headers"/> go on this request only and
    /// <paramref name="extra"/> adds fields to the body.
    /// </summary>
    public async Task<WeirResponse> AttemptLoginAsync(
        string username = AdminUsername,
        string password = AdminPassword,
        IReadOnlyDictionary<string, string>? headers = null,
        params (string Name, JsonNode Value)[] extra)
    {
        var body = Credentials(username, password, await CsrfTokenAsync());
        foreach (var (name, value) in extra)
        {
            body[name] = value;
        }

        return await PostAsync($"{Api}/auth/login", body, headers);
    }

    /// <summary>Signs in; fails the test unless the server answers <paramref name="expected"/>.</summary>
    public async Task<WeirResponse> LoginAsync(
        string username = AdminUsername,
        string password = AdminPassword,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await AttemptLoginAsync(username, password);
        Assert.True(response.Status == expected, response.ToString());
        return response;
    }

    /// <summary>Creates the first admin when the install has none, then signs in as it.</summary>
    public async Task EnsureAdminAsync(string username = AdminUsername, string password = AdminPassword)
    {
        var status = await GetAsync($"{Api}/auth/bootstrap/status");
        Assert.True(status.Status == HttpStatusCode.OK, status.ToString());
        if ((bool)status.Fields["bootstrap_allowed"]!)
        {
            var created = await BootstrapAsync(username, password);
            Assert.True(created.Status == HttpStatusCode.OK, created.ToString());

            // The owner who set this account up saved the recovery codes it showed; nothing here is about them.
            // A published release under test (WEIR_CONTRACT_SERVER, as the release fixtures are made) from before recovery codes has no such route.
            var saved = await PostWithCsrfAsync($"{Api}/auth/recovery-codes/confirm");
            var releaseWithoutCodes = saved.Status == HttpStatusCode.NotFound
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ServerBinary.OverrideVariable));
            Assert.True(saved.Status == HttpStatusCode.OK || releaseWithoutCodes, saved.ToString());
        }

        await LoginAsync(username, password);
    }

    // --- plumbing -------------------------------------------------------------------------------

    private static JsonObject Credentials(string username, string password, string csrfToken) => new()
    {
        ["username"] = username,
        ["password"] = password,
        [CsrfTokenField] = csrfToken,
    };

    private async Task<WeirResponse> SendWithCsrfBodyAsync(
        HttpMethod method, string path, JsonObject? body, IReadOnlyDictionary<string, string>? headers)
    {
        var fields = body ?? new JsonObject();
        if (!fields.ContainsKey(CsrfTokenField))
        {
            fields[CsrfTokenField] = await CsrfTokenAsync();
        }

        return await SendAsync(method, path, JsonBody(fields), headers);
    }

    private async Task<WeirResponse> SendAsync(
        HttpMethod method, string path, HttpContent? content, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request);
        return await WeirResponse.ReadAsync(response, CancellationToken.None);
    }

    private static StringContent? JsonBody(JsonNode? body) =>
        body is null ? null : new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private static string WithQuery(string path, (string Name, object Value)[] query)
    {
        if (query.Length == 0)
        {
            return path;
        }

        var pairs = query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(Convert.ToString(pair.Value, CultureInfo.InvariantCulture)!)}");
        return $"{path}?{string.Join('&', pairs)}";
    }
}
