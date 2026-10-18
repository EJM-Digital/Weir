using System.Text;
using System.Text.Json.Nodes;
using Weir.Tests.Shared;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>What a fake download client was shown when someone tried to sign in, and what it made of it.</summary>
/// <param name="Username">The user name sent, or null when none was.</param>
/// <param name="Secret">The password or API key sent; empty when none was.</param>
/// <param name="Accepted">Whether the fake let them in.</param>
public sealed record SignInAttempt(string? Username, string Secret, bool Accepted);

/// <summary>
/// A fake download client (qBittorrent, SABnzbd, NZBGet, Transmission or Deluge): a real HTTP server that only lets a caller in
/// when it presents the credentials the fake is set to expect, and writes down every attempt. Each kind answers the calls Weir
/// makes to read that client's settings, in that product's way of asking for credentials (a cookie login, an API key, HTTP basic
/// authentication, a session id handshake). A fake expecting no secret lets everyone in.
/// <para>
/// Every reply is one a real client gave (<c>fixtures/recorded/&lt;client&gt;</c>), served as recorded; the one thing a test may change is
/// the completed folder the client reports. The fake decides only whether the credentials are the expected ones, which a recording cannot
/// know, and answers with the recorded welcome or the recorded refusal. Two answers are not recorded and stay as plain text: a request
/// without the session a login gave, and a route the client has no call for.
/// </para>
/// </summary>
public sealed class FakeDownloadClient : FakeHttpServer
{
    private readonly object _gate = new();
    private readonly List<SignInAttempt> _attempts = [];
    private readonly string _completedFolder;
    private readonly Dictionary<string, ArrRecording> _recordings = [];
    private string? _username;
    private string _secret;

    private FakeDownloadClient(string kind, string? username, string secret, string completedFolder)
    {
        Kind = kind;
        _username = username;
        _secret = secret;
        _completedFolder = completedFolder;
    }

    public string Kind { get; }

    /// <summary>Every sign-in attempt so far, oldest first: a login, or a call that carries the credentials itself.</summary>
    public IReadOnlyList<SignInAttempt> Attempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>A client of this <paramref name="kind"/> that asks for <paramref name="secret"/>, and <paramref name="username"/> where its product has one.</summary>
    public static FakeDownloadClient Start(string kind, string? username, string secret, string completedFolder = "/downloads/complete")
    {
        if (kind is not ("qbittorrent" or "sabnzbd" or "nzbget" or "transmission" or "deluge"))
        {
            throw new ArgumentException("kind must be qbittorrent, sabnzbd, nzbget, transmission or deluge", nameof(kind));
        }

        return new FakeDownloadClient(kind, username, secret, completedFolder);
    }

    /// <summary>Changes what the client asks for, as its owner does in its own settings.</summary>
    public void Require(string? username, string secret)
    {
        lock (_gate)
        {
            (_username, _secret) = (username, secret);
        }
    }

    protected override HttpAnswer Answer(RecordedRequest request) => Kind switch
    {
        "qbittorrent" => QBittorrent(request),
        "sabnzbd" => Sabnzbd(request),
        "nzbget" => Nzbget(request),
        "transmission" => Transmission(request),
        _ => Deluge(request),
    };

    private HttpAnswer QBittorrent(RecordedRequest request)
    {
        var test = Recording("test");
        if (request is { Method: "POST", Path: "/api/v2/auth/login" })
        {
            var form = Form(request);
            var accepted = Record(form.GetValueOrDefault("username"), form.GetValueOrDefault("password") ?? string.Empty);
            return Replied(accepted ? test.Exchanges[0].Reply : Recording("test-wrong-password").Only.Reply);
        }

        var session = test.Exchanges[0].Reply.Headers["set-cookie"].Split(';')[0];
        if (request.Method == "GET" && request.Header("Cookie")?.Contains(session, StringComparison.Ordinal) != true)
        {
            return Text(403, "Forbidden");
        }

        var folders = Recording("folders");
        return request switch
        {
            { Method: "GET", Path: "/api/v2/app/preferences" } => Replied(folders.To("GET", request.Path).Reply, "save_path"),
            { Method: "GET", Path: "/api/v2/torrents/categories" } => Replied(folders.To("GET", request.Path).Reply),
            _ => NotFound(request),
        };
    }

    private HttpAnswer Sabnzbd(RecordedRequest request)
    {
        if (request is not { Method: "GET", Path: "/api" })
        {
            return NotFound(request);
        }

        var secret = request.Query.TryGetValue("apikey", out var keys) ? keys[0] : string.Empty;
        if (!Record(null, secret))
        {
            return Replied(Recording("test-wrong-key").Only.Reply);
        }

        var folders = Recording("folders");
        return (request.Query.GetValueOrDefault("mode")?[0], request.Query.GetValueOrDefault("section")?[0]) switch
        {
            ("get_config", "misc") => Replied(folders.Exchanges.First(exchange => exchange.Request.Query.Contains("section=misc", StringComparison.Ordinal)).Reply, "config", "misc", "complete_dir"),
            ("get_config", "categories") => Replied(folders.Exchanges.First(exchange => exchange.Request.Query.Contains("section=categories", StringComparison.Ordinal)).Reply),
            _ => NotFound(request),
        };
    }

    private HttpAnswer Nzbget(RecordedRequest request)
    {
        if (request is not { Method: "POST", Path: "/jsonrpc" })
        {
            return NotFound(request);
        }

        if (!BasicAuthAccepted(request))
        {
            return Replied(Recording("test-wrong-password").Only.Reply);
        }

        var reply = Recording("folders").Only.Reply;
        var config = reply.Body!.DeepClone();
        foreach (var option in config["result"]!.AsArray().OfType<JsonObject>().Where(option => (string?)option["Name"] == "DestDir"))
        {
            option["Value"] = _completedFolder;
        }

        return Replied(reply with { Body = config });
    }

    private HttpAnswer Transmission(RecordedRequest request)
    {
        if (request is not { Method: "POST", Path: "/transmission/rpc" })
        {
            return NotFound(request);
        }

        if (!BasicAuthAccepted(request))
        {
            return Replied(Recording("test-wrong-password").Only.Reply);
        }

        var handshake = Recording("test-open");
        if (request.Header("X-Transmission-Session-Id") != handshake.Exchanges[0].Reply.Headers["x-transmission-session-id"])
        {
            return Replied(handshake.Exchanges[0].Reply);
        }

        return Replied(handshake.Exchanges[1].Reply, "arguments", "download-dir");
    }

    private HttpAnswer Deluge(RecordedRequest request)
    {
        if (request is not { Method: "POST", Path: "/json" } || request.Json is not JsonObject call)
        {
            return NotFound(request);
        }

        var method = (string?)call["method"];
        var folders = Recording("folders");
        if (method == "auth.login")
        {
            var password = (call["params"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? string.Empty;
            return Replied(Record(null, password) ? Recording("test").Only.Reply : Recording("test-wrong-password").Only.Reply);
        }

        var session = Recording("test").Only.Reply.Headers["set-cookie"].Split(';')[0];
        if (request.Header("Cookie")?.Contains(session, StringComparison.Ordinal) != true)
        {
            return Json(new JsonObject { ["id"] = call["id"]?.DeepClone(), ["result"] = null, ["error"] = new JsonObject { ["message"] = "Not authenticated", ["code"] = 1 } });
        }

        var answered = folders.Exchanges.FirstOrDefault(exchange => exchange.Request.Body is { } body && body["method"]?.ToJsonString() == call["method"]?.ToJsonString() && body["params"]?.ToJsonString() == call["params"]?.ToJsonString());
        string[] folderMember = method == "core.get_config" ? ["result", "move_completed_path"] : [];
        return answered is null ? NotFound(request) : Replied(answered.Reply, folderMember);
    }

    private bool BasicAuthAccepted(RecordedRequest request)
    {
        string? username = null;
        var secret = string.Empty;
        var header = request.Header("Authorization");
        if (header is not null && header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            var split = pair.IndexOf(':', StringComparison.Ordinal);
            (username, secret) = split < 0 ? (pair, string.Empty) : (pair[..split], pair[(split + 1)..]);
        }

        return Record(username, secret);
    }

    /// <summary>Writes down an attempt and says whether the credentials are the ones expected.</summary>
    private bool Record(string? username, string secret)
    {
        lock (_gate)
        {
            var accepted = secret == _secret && (_username is null || username == _username);
            _attempts.Add(new SignInAttempt(string.IsNullOrEmpty(username) ? null : username, secret, accepted));
            return accepted;
        }
    }

    /// <summary>The newest recorded version of this kind of client in <paramref name="state"/>.</summary>
    private ArrRecording Recording(string state)
    {
        lock (_gate)
        {
            if (!_recordings.TryGetValue(state, out var recording))
            {
                _recordings[state] = recording = ArrRecording.Load(Kind, state);
            }

            return recording;
        }
    }

    /// <summary>
    /// A recorded reply, served as recorded. With <paramref name="folderMember"/>, the member of its JSON body at that path is set to the
    /// completed folder the fake was started with, which is the one change a test can ask for.
    /// </summary>
    private HttpAnswer Replied(ArrReply reply, params string[] folderMember)
    {
        if (folderMember.Length > 0 && reply.Body is not null)
        {
            var body = reply.Body.DeepClone();
            folderMember[..^1].Aggregate(body, (node, name) => node[name]!)[folderMember[^1]] = _completedFolder;
            reply = reply with { Body = body };
        }

        var headers = reply.Headers.Where(header => header.Key != "content-type").ToDictionary(header => header.Key, header => header.Value);
        return new HttpAnswer(reply.Status, Encoding.UTF8.GetBytes(reply.Text), reply.ContentType, headers);
    }

    private static Dictionary<string, string> Form(RecordedRequest request) =>
        Encoding.UTF8.GetString(request.Body).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString((parts.Length > 1 ? parts[1] : string.Empty).Replace('+', ' ')));

    private static HttpAnswer Json(JsonNode body, int status = 200) =>
        new(status, Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json");

    private static HttpAnswer Text(int status, string body) =>
        new(status, Encoding.UTF8.GetBytes(body), "text/plain");

    private HttpAnswer NotFound(RecordedRequest request) =>
        Text(404, $"the fake {Kind} has no route for {request.Method} {request.Path}");
}
