using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>
/// A loopback server that answers like qBittorrent 5.x. A login without a live session cookie starts one (204 with
/// Set-Cookie); a login that already carries a live session cookie is answered 204 with no Set-Cookie; a wrong password
/// is 401. Reads answer only for a live session.
/// </summary>
internal sealed class FakeQBittorrentServer : IDisposable
{
    private const string CorrectPassword = "right";
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, byte> _liveSessions = new();
    private readonly ConcurrentQueue<string?> _loginCookieHeaders = new();
    private readonly Dictionary<string, Action<HttpListenerContext, bool, string?>> _routes;
    private int _sessionCount;

    public FakeQBittorrentServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _routes = new Dictionary<string, Action<HttpListenerContext, bool, string?>> { ["/api/v2/auth/login"] = Login };
        _ = ServeAsync();
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string CookieName => $"QBT_SID_{Port}";

    /// <summary>The Cookie header of each login received, in order; null where the login carried none.</summary>
    public IReadOnlyList<string?> LoginCookieHeaders => [.. _loginCookieHeaders];

    /// <summary>Forgets every session, as qBittorrent does when its session timeout passes.</summary>
    public void ExpireSessions() => _liveSessions.Clear();

    public void Dispose() => _listener.Close();

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            Answer(context);
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var cookieHeader = request.Headers["Cookie"];
        var hasLiveSession = HasLiveSession(cookieHeader);
        var route = _routes.GetValueOrDefault(request.Url!.AbsolutePath, ReadWithSession);
        route(context, hasLiveSession, cookieHeader);

        response.Close();
    }

    private void ReadWithSession(HttpListenerContext context, bool hasLiveSession, string? cookieHeader)
    {
        var response = context.Response;
        response.StatusCode = hasLiveSession ? (int)HttpStatusCode.OK : (int)HttpStatusCode.Forbidden;
        if (hasLiveSession)
        {
            using var writer = new StreamWriter(response.OutputStream);
            writer.Write("{}");
        }
    }

    private void Login(HttpListenerContext context, bool hasLiveSession, string? cookieHeader)
    {
        _loginCookieHeaders.Enqueue(cookieHeader);
        using var reader = new StreamReader(context.Request.InputStream);
        var body = reader.ReadToEnd();
        if (!body.Contains($"password={CorrectPassword}", StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        if (!hasLiveSession)
        {
            var session = $"session-{Interlocked.Increment(ref _sessionCount)}";
            _liveSessions[session] = 0;
            context.Response.Headers.Add("Set-Cookie", $"{CookieName}={session}; HttpOnly; SameSite=Strict; path=/");
        }
    }

    private bool HasLiveSession(string? cookieHeader) =>
        cookieHeader is not null
        && cookieHeader.Split(';', StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Any(pair => pair.Length == 2 && pair[0] == CookieName && _liveSessions.ContainsKey(pair[1]));
}
