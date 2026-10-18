using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Weir.Tests.Shared;

/// <summary>
/// What Weir sent an app, or what the app sent Weir: the method, the path with its query, the body and the headers (lower-case names). A
/// body that is not JSON (a form, for the download clients that take one) is in <see cref="BodyText"/>.
/// </summary>
internal sealed record ArrRequest(string Method, string Target, JsonNode? Body, IReadOnlyDictionary<string, string> Headers, string? BodyText = null)
{
    /// <summary>The body as a person compares two of them: JSON as JSON, a form as its decoded fields, anything else as text.</summary>
    public string BodyKey
    {
        get
        {
            if (Body is not null)
            {
                return Body.ToJsonString();
            }

            if (BodyText is null || !Headers.GetValueOrDefault("content-type", string.Empty).StartsWith("application/x-www-form-urlencoded", StringComparison.Ordinal))
            {
                return BodyText ?? string.Empty;
            }

            var form = HttpUtility.ParseQueryString(BodyText);
            return string.Join('&', form.AllKeys.OfType<string>().Order(StringComparer.Ordinal).Select(key => $"{key}={form[key]}"));
        }
    }

    public string Path => Target.Split('?')[0];

    /// <summary>The query pairs, sorted, so two spellings of one query compare equal.</summary>
    public string Query => Target.Contains('?')
        ? string.Join('&', HttpUtility.ParseQueryString(Target[(Target.IndexOf('?') + 1)..]).AllKeys.OfType<string>().Order(StringComparer.Ordinal)
            .SelectMany(key => HttpUtility.ParseQueryString(Target[(Target.IndexOf('?') + 1)..]).GetValues(key)!.Select(value => $"{key}={value}")))
        : string.Empty;
}

/// <summary>What the app answered. A JSON reply is in <see cref="Body"/>, any other in <see cref="BodyText"/>.</summary>
internal sealed record ArrReply(int Status, JsonNode? Body, string? BodyText, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The body as the app sent it, as text; empty when it sent none.</summary>
    public string Text => Body?.ToJsonString() ?? BodyText ?? string.Empty;

    public string ContentType => Headers.GetValueOrDefault("content-type") ?? "application/json";
}

internal sealed record ArrExchange(ArrRequest Request, ArrReply Reply);

/// <summary>
/// One recording of a real app (Sonarr, Radarr or a download client; <c>fixtures/recorded/&lt;app&gt;/&lt;version&gt;/&lt;state&gt;.json</c>), loaded as it is. A test
/// never writes a request or a reply by hand: a state nobody recorded fails and says where it would have been.
/// </summary>
internal sealed class ArrRecording
{
    private ArrRecording(string app, string version, string state, string note, IReadOnlyList<ArrExchange> exchanges)
    {
        App = app;
        Version = version;
        State = state;
        Note = note;
        Exchanges = exchanges;
    }

    public string App { get; }

    /// <summary>The version of the app the recording was made against.</summary>
    public string Version { get; }

    public string State { get; }

    /// <summary>What was done to reach this state, in the recorder's own words.</summary>
    public string Note { get; }

    public IReadOnlyList<ArrExchange> Exchanges { get; }

    /// <summary>The file as the repository holds it, which is what a failure names.</summary>
    public string File => $"apps/server/tests/fixtures/recorded/{App}/{Version}/{State}.json";

    /// <summary>
    /// This recording with other exchanges: the same app, version and state, for a test that has to change a member of a recorded reply to
    /// reach a case no run produced on demand. The test says which member it changes.
    /// </summary>
    public ArrRecording WithExchanges(IEnumerable<ArrExchange> exchanges) => new(App, Version, State, Note, [.. exchanges]);

    public ArrExchange Only => Exchanges.Count == 1
        ? Exchanges[0]
        : throw new InvalidOperationException($"{File} holds {Exchanges.Count} exchanges; name the one to use.");

    /// <summary>The first exchange of this recording whose request is <paramref name="method"/> <paramref name="path"/>.</summary>
    public ArrExchange To(string method, string path) =>
        Exchanges.First(exchange => exchange.Request.Method == method && exchange.Request.Path == path);

    /// <summary>The first exchange of a JSON-RPC client whose request calls <paramref name="method"/> (Deluge's calls all go to one path).</summary>
    public ArrExchange Call(string method) =>
        Exchanges.First(exchange => (string?)exchange.Request.Body?["method"] == method);

    /// <summary>The recorded states of <paramref name="app"/> whose name starts with <paramref name="prefix"/>, in name order.</summary>
    public static IReadOnlyList<string> States(string app, string prefix) =>
        [.. Directory.GetFiles(VersionFolder(app), prefix + "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>The newest version recorded of <paramref name="app"/>.</summary>
    public static ArrRecording Load(string app, string state) => Load(app, Path.GetFileName(VersionFolder(app)), state);

    /// <summary>The versions recorded of <paramref name="app"/>, oldest first.</summary>
    public static IReadOnlyList<string> Versions(string app) =>
        [.. Directory.GetDirectories(Path.Combine(RecordedFolder(), app)).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    public static ArrRecording Load(string app, string version, string state)
    {
        var appFolder = Path.Combine(RecordedFolder(), app);
        var path = Path.Combine(appFolder, version, state + ".json");
        if (!System.IO.File.Exists(path))
        {
            throw new Xunit.Sdk.XunitException(
                $"Nobody recorded '{state}' for {app} {version} (looked for {path}). Do not write the request or the reply by hand: " +
                "add the step to the recorder (apps/server/tools/Weir.ArrRecorder), or say it is missing in MISSING.md beside the recordings.");
        }

        var document = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        Assert.Equal(app, (string)document["app"]!);
        Assert.Equal(version, (string)document["version"]!);
        Assert.Equal(state, (string)document["state"]!);
        return new ArrRecording(app, version, state, (string)document["note"]!, [.. document["exchanges"]!.AsArray().Select(node => ExchangeOf(node!.AsObject()))]);
    }

    private static ArrExchange ExchangeOf(JsonObject exchange)
    {
        var request = exchange["request"]!.AsObject();
        var reply = exchange["reply"]!.AsObject();
        return new ArrExchange(
            new ArrRequest((string)request["method"]!, (string)request["path"]!, request["body"]?.DeepClone(), HeadersOf(request), (string?)request["bodyText"]),
            new ArrReply((int)reply["status"]!, reply["body"]?.DeepClone(), (string?)reply["bodyText"], HeadersOf(reply)));
    }

    private static Dictionary<string, string> HeadersOf(JsonObject message) =>
        message["headers"] is JsonObject headers ? headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => (string)header.Value!) : [];

    private static string VersionFolder(string app)
    {
        var appFolder = Path.Combine(RecordedFolder(), app);
        return Directory.GetDirectories(appFolder).Order(StringComparer.Ordinal).Last();
    }

    private static string RecordedFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "apps", "server", "tests", "fixtures", "recorded");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The recordings are not above the test run.");
    }
}

/// <summary>
/// Answers a Sonarr or Radarr client from recordings: each request gets the reply recorded for the same method, path and query. A
/// request nothing was recorded for fails loudly, so Weir asking for something the real app was never recorded answering cannot pass.
/// A route recorded more than once answers in recorded order and then repeats its last reply. Every request is kept for
/// <see cref="AssertSentAsRecorded"/>.
/// </summary>
internal sealed class ArrReplayHandler : HttpMessageHandler
{
    /// <summary>The headers that carry a sign-in; their values are scrubbed in a recording, so a test holds that they were sent, not what they held.</summary>
    private static readonly string[] SignInHeaders = ["x-api-key", "authorization", "cookie", "x-transmission-session-id"];

    private readonly List<(ArrRequest Request, ArrReply Reply, string Origin)> _recorded = [];
    private readonly Dictionary<(string Method, string Path, string Query), int> _answered = [];
    private readonly List<ArrRequest> _sent = [];
    private readonly Lock _lock = new();

    public ArrReplayHandler(params ArrRecording[] recordings)
    {
        foreach (var recording in recordings)
        {
            Add(recording);
        }
    }

    /// <summary>Every request received, in order, with its body parsed.</summary>
    public IReadOnlyList<ArrRequest> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public ArrReplayHandler Add(ArrRecording recording)
    {
        foreach (var exchange in recording.Exchanges)
        {
            _recorded.Add((exchange.Request, exchange.Reply, recording.File));
        }

        return this;
    }

    /// <summary>
    /// Fails unless every request in <paramref name="recording"/> was sent as recorded: the same method, path and query, the same JSON body,
    /// and the API key header (whose value the recording scrubbed).
    /// </summary>
    public void AssertSentAsRecorded(ArrRecording recording)
    {
        var seen = new Dictionary<(string Method, string Path, string Query), int>();
        foreach (var exchange in recording.Exchanges)
        {
            var recorded = exchange.Request;
            var route = (recorded.Method, recorded.Path, recorded.Query);
            var nth = seen.GetValueOrDefault(route);
            seen[route] = nth + 1;
            var onRoute = Sent.Where(sent => sent.Method == recorded.Method && sent.Path == recorded.Path && sent.Query == recorded.Query).ToList();

            // A route recorded more than once (a handshake, a JSON-RPC path) is matched call by call, in the order Weir made them.
            var match = onRoute.ElementAtOrDefault(nth) ?? onRoute.FirstOrDefault()
                ?? throw new Xunit.Sdk.XunitException($"Weir never sent {recorded.Method} {recorded.Target}, which {recording.File} records.");
            Assert.True(
                recorded.Body is not null && match.Body is not null ? JsonNode.DeepEquals(recorded.Body, match.Body) : recorded.BodyKey == match.BodyKey,
                $"Weir's body for {recorded.Method} {recorded.Target} was {Show(match.BodyKey)}; {recording.File} records {Show(recorded.BodyKey)}.");
            foreach (var header in SignInHeaders)
            {
                Assert.True(
                    recorded.Headers.ContainsKey(header) == match.Headers.ContainsKey(header),
                    $"{recording.File} records {recorded.Method} {recorded.Target} {(recorded.Headers.ContainsKey(header) ? "with" : "without")} {header}; Weir sent it {(match.Headers.ContainsKey(header) ? "with" : "without")}.");
            }
        }
    }

    private static string Show(string body) => body.Length == 0 ? "empty" : body;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : string.Empty;
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => string.Join(", ", pair.Value));
        var isJson = headers.GetValueOrDefault("content-type", string.Empty).StartsWith("application/json", StringComparison.Ordinal);
        var sent = new ArrRequest(request.Method.Method, request.RequestUri!.PathAndQuery, body.Length > 0 && isJson ? JsonNode.Parse(body) : null, headers, body.Length > 0 && !isJson ? body : null);
        ArrReply reply;
        lock (_lock)
        {
            _sent.Add(sent);
            var candidates = _recorded.Where(entry => entry.Request.Method == sent.Method && entry.Request.Path == sent.Path && entry.Request.Query == sent.Query).ToList();
            if (candidates.Count == 0)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Nothing was recorded for {sent.Method} {sent.Target}. Recordings in play: {string.Join(", ", _recorded.Select(entry => entry.Origin).Distinct())}.");
            }

            // Calls that share a route and differ in their body (a JSON-RPC client's calls all go to one path) are told apart by it.
            var sameBody = candidates.Where(entry => entry.Request.BodyKey == sent.BodyKey).ToList();
            var key = (sent.Method, sent.Path, sent.Query + (sameBody.Count > 0 && sameBody.Count < candidates.Count ? "#" + sent.BodyKey : string.Empty));
            if (sameBody.Count > 0)
            {
                candidates = sameBody;
            }

            var index = _answered.GetValueOrDefault(key);
            reply = candidates[Math.Min(index, candidates.Count - 1)].Reply;
            _answered[key] = index + 1;
        }

        var response = new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(reply.Text)) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(reply.ContentType.Split(';')[0]);
        foreach (var (name, value) in reply.Headers.Where(header => header.Key is not "content-type"))
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}
