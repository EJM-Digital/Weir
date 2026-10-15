using System.Text;
using System.Text.Json.Nodes;

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
/// authentication, a session id handshake). A fake expecting no secret lets everyone in. The replies are written from each product's
/// published interface, not recorded from a running client.
/// </summary>
public sealed class FakeDownloadClient : FakeHttpServer
{
    private const string QBittorrentSession = "SID=fake-qbittorrent-session";
    private const string TransmissionSessionId = "fake-transmission-session-id";
    private const string DelugeSession = "_session_id=fake-deluge-session";

    private readonly object _gate = new();
    private readonly List<SignInAttempt> _attempts = [];
    private readonly string _completedFolder;
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
        if (request is { Method: "POST", Path: "/api/v2/auth/login" })
        {
            var form = Form(request);
            var accepted = Record(form.GetValueOrDefault("username"), form.GetValueOrDefault("password") ?? string.Empty);
            return accepted
                ? Text(200, "Ok.", new Dictionary<string, string> { ["Set-Cookie"] = $"{QBittorrentSession}; HttpOnly; path=/" })
                : Text(200, "Fails.");
        }

        if (request.Method == "GET" && request.Header("Cookie")?.Contains(QBittorrentSession, StringComparison.Ordinal) != true)
        {
            return Text(403, "Forbidden");
        }

        return request switch
        {
            { Method: "GET", Path: "/api/v2/app/preferences" } => Json(new JsonObject { ["save_path"] = _completedFolder }),
            { Method: "GET", Path: "/api/v2/torrents/categories" } => Json(new JsonObject()),
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
            return Json(new JsonObject { ["status"] = false, ["error"] = "API Key Incorrect" }, 403);
        }

        return (request.Query.GetValueOrDefault("mode")?[0], request.Query.GetValueOrDefault("section")?[0]) switch
        {
            ("get_config", "misc") => Json(new JsonObject { ["config"] = new JsonObject { ["misc"] = new JsonObject { ["complete_dir"] = _completedFolder } } }),
            ("get_config", "categories") => Json(new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["categories"] = new JsonArray(new JsonObject { ["name"] = "*", ["dir"] = string.Empty, ["priority"] = 0 }),
                },
            }),
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
            return Unauthorized();
        }

        return Json(new JsonObject
        {
            ["version"] = "1.1",
            ["result"] = new JsonArray(new JsonObject { ["Name"] = "DestDir", ["Value"] = _completedFolder }),
        });
    }

    private HttpAnswer Transmission(RecordedRequest request)
    {
        if (request is not { Method: "POST", Path: "/transmission/rpc" })
        {
            return NotFound(request);
        }

        if (!BasicAuthAccepted(request))
        {
            return Unauthorized();
        }

        if (request.Header("X-Transmission-Session-Id") != TransmissionSessionId)
        {
            return new HttpAnswer(409, Encoding.UTF8.GetBytes("409: Conflict"), "text/html", new Dictionary<string, string> { ["X-Transmission-Session-Id"] = TransmissionSessionId });
        }

        return Json(new JsonObject { ["arguments"] = new JsonObject { ["download-dir"] = _completedFolder }, ["result"] = "success" });
    }

    private HttpAnswer Deluge(RecordedRequest request)
    {
        if (request is not { Method: "POST", Path: "/json" } || request.Json is not JsonObject call)
        {
            return NotFound(request);
        }

        var id = call["id"]?.DeepClone();
        if ((string?)call["method"] != "auth.login")
        {
            return Json(new JsonObject { ["id"] = id, ["result"] = null, ["error"] = new JsonObject { ["message"] = "Not authenticated", ["code"] = 1 } });
        }

        var password = (call["params"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? string.Empty;
        var accepted = Record(null, password);
        return Json(
            new JsonObject { ["id"] = id, ["result"] = accepted, ["error"] = null },
            headers: accepted ? new Dictionary<string, string> { ["Set-Cookie"] = $"{DelugeSession}; Path=/; HttpOnly" } : null);
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

    private static Dictionary<string, string> Form(RecordedRequest request) =>
        Encoding.UTF8.GetString(request.Body).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString((parts.Length > 1 ? parts[1] : string.Empty).Replace('+', ' ')));

    private static HttpAnswer Json(JsonNode body, int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json", headers);

    private static HttpAnswer Text(int status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, Encoding.UTF8.GetBytes(body), "text/plain", headers);

    private static HttpAnswer Unauthorized() =>
        Text(401, "Unauthorized", new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"fake download client\"" });

    private HttpAnswer NotFound(RecordedRequest request) =>
        Text(404, $"the fake {Kind} has no route for {request.Method} {request.Path}");
}
