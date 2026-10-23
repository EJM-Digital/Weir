using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Net;
using Weir.Core.Notifications;
using Weir.Infrastructure.ConnectionTraffic;
using Weir.Infrastructure.Http;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>
/// Which addresses a manager connection's traffic may reach, once resolved. A media manager lives on the LAN or the
/// same host, so <see cref="Local"/> keeps private ranges and loopback allowed; the metadata provider is a public
/// service, so <see cref="Public"/> requires a globally-routable address (ADR-0015).
/// </summary>
public enum ManagerAddressPolicy
{
    Local,
    Public,
}

/// <summary>
/// The HTTP transport for media managers and the metadata provider. A seam so tests answer with a fake handler.
/// </summary>
public interface IManagerHttpHandlerFactory
{
    /// <summary>
    /// A handler the caller does not dispose. <paramref name="followRedirects"/> is true for manager API and metadata
    /// provider calls, and false for a hand-off completion report, which posts once to the exact callback URL.
    /// <paramref name="policy"/> is only meaningful for a handler that actually connects over the network (a test
    /// fake may ignore it).
    /// </summary>
    HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local);
}

/// <summary>
/// One <see cref="SocketsHttpHandler"/> per redirect/address-policy combination, shared across requests. Every
/// handler resolves its target host and connects only to an address <see cref="ManagerAddressPolicy"/> allows,
/// pinning the connection to that address so the check cannot be defeated by the name resolving differently a
/// moment later (DNS rebinding) — the same defence <see cref="ExternalJsonPoster"/> uses for outbound notifications.
/// Handlers keep no cookies: they are shared by every connection, and a cookie jar keyed by host would hand one
/// connection's session to another on the same host and to its own next login (#856). A dialect that needs a
/// session cookie reads Set-Cookie and sends the Cookie header itself.
/// </summary>
/// <remarks>
/// A connection is reused only while it has sat idle for less than <see cref="DefaultIdleConnectionTimeout"/>. Managers, and the proxies in
/// front of them, close idle connections on their own clock. A call that goes out on a connection in the instant the other side closes it
/// fails with a reset, and the transport does not send it again because the request may already have been acted on. Staying under the
/// shortest idle limit Weir meets keeps that from happening to any call, including one that changes something.
/// </remarks>
public sealed class SocketsManagerHttpHandlerFactory : IManagerHttpHandlerFactory, IDisposable
{
    /// <summary>
    /// How long a connection may sit idle before it is no longer reused. It is below the 15 seconds after which .NET's HttpListener (the
    /// server behind the contract suite's fake managers) closes an idle connection, the shortest limit measured, and well below the 60 to
    /// 130 seconds that Kestrel and common reverse proxies allow.
    /// </summary>
    public static readonly TimeSpan DefaultIdleConnectionTimeout = TimeSpan.FromSeconds(10);

    private readonly OutboundAddressGuard.HostResolver? _resolveHost;
    private readonly TimeSpan _idleConnectionTimeout;
    private readonly ConnectionActivityHub? _activity;
    private readonly TimeProvider _time;
    private readonly Dictionary<(bool FollowRedirects, ManagerAddressPolicy Policy), HttpMessageHandler> _handlers = [];
    private readonly Lock _lock = new();

    /// <param name="resolveHost">Overrides DNS resolution; tests use this to prove the policy against fixed answers.</param>
    /// <param name="activity">Where the calls marked with <see cref="ConnectionTag"/> are reported; none reports nothing.</param>
    /// <param name="time">Times those calls.</param>
    /// <param name="idleConnectionTimeout">How long a connection may sit idle before it is no longer reused; <see cref="DefaultIdleConnectionTimeout"/> when none is given.</param>
    public SocketsManagerHttpHandlerFactory(
        OutboundAddressGuard.HostResolver? resolveHost = null, ConnectionActivityHub? activity = null, TimeProvider? time = null, TimeSpan? idleConnectionTimeout = null)
    {
        _idleConnectionTimeout = idleConnectionTimeout ?? DefaultIdleConnectionTimeout;
        _resolveHost = resolveHost;
        _activity = activity;
        _time = time ?? TimeProvider.System;
    }

    public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local)
    {
        var key = (followRedirects, policy);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(key, out var handler))
            {
                handler = Build(followRedirects, policy);
                _handlers.Add(key, handler);
            }

            return handler;
        }
    }

    private HttpMessageHandler Build(bool followRedirects, ManagerAddressPolicy policy)
    {
        Func<NetAddress, bool> isAllowed = policy == ManagerAddressPolicy.Public ? OutboundAddressGuard.IsPublic : OutboundAddressGuard.IsLocalServiceAddress;
        var transport = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = followRedirects,
            MaxAutomaticRedirections = followRedirects ? 10 : 1,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = _idleConnectionTimeout,
            ConnectCallback = (context, cancellationToken) =>
                OutboundAddressGuard.ConnectAsync(context, isAllowed, host => new ManagerAddressRefusedException(host), _resolveHost, cancellationToken),
        };
        return _activity is null ? transport : new ConnectionActivityHandler(transport, _activity, _time);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var handler in _handlers.Values)
            {
                handler.Dispose();
            }
        }
    }
}

/// <summary>The host resolved to an address <see cref="ManagerAddressPolicy"/> refuses to connect to.</summary>
public sealed class ManagerAddressRefusedException : Exception
{
    public ManagerAddressRefusedException(string host)
        : base($"Weir will not connect to {host}: it does not resolve to an address this connection may use.")
    {
    }
}

/// <summary>
/// A narrow JSON client for one media manager. The base URL must
/// be a plain http(s) address with no credentials, query or fragment, and request paths must be relative, so a saved
/// connection cannot be turned into a request against an arbitrary host (ADR-0015).
/// </summary>
public sealed class MediaManagerHttpClient
{
    private readonly string _base;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly ConnectionRef? _connection;

    /// <summary>
    /// A client for the manager at <paramref name="baseUrl"/>. <paramref name="connection"/> is the saved connection it talks
    /// to, so its calls light that connection up; null for a manager that is not saved.
    /// </summary>
    /// <exception cref="MediaManagerHttpException">The base URL is not a plain http(s) address.</exception>
    public MediaManagerHttpClient(string baseUrl, string apiKey, IManagerHttpHandlerFactory handlers, TimeSpan? timeout = null, ConnectionRef? connection = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        try
        {
            _base = ExternalUrlPolicy.NormalizeLocalServiceBaseUrl(baseUrl);
        }
        catch (WireValueException exception)
        {
            throw new MediaManagerHttpException(exception.Message, exception);
        }

        _apiKey = apiKey ?? string.Empty;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _connection = connection;
    }

    /// <summary>The request URL: refuses an absolute path, prefixes a slash, appends urlencoded parameters.</summary>
    public string Url(string path, IReadOnlyList<KeyValuePair<string, string>>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (SplitUrl.Parse(path).Scheme.Length > 0)
        {
            throw new MediaManagerHttpException("A media manager API path must be relative.");
        }

        var url = _base + (path.StartsWith('/') ? path : "/" + path);
        return parameters is { Count: > 0 } ? url + "?" + QueryStrings.Encode(parameters) : url;
    }

    /// <summary>GET and parse JSON. Booleans in <paramref name="parameters"/> are sent as <c>1</c>/<c>0</c>.</summary>
    public Task<WireValue?> GetJsonAsync(string path, IReadOnlyList<KeyValuePair<string, object>>? parameters = null, CancellationToken cancellationToken = default)
    {
        var flat = parameters?.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value switch
        {
            bool flag => flag ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty,
        })).ToList();
        var request = new HttpRequestMessage(HttpMethod.Get, Url(path, flat));
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return SendAsync(request, allowEmpty: false, cancellationToken);
    }

    /// <summary>
    /// POST the body as JSON and parse the answer. <paramref name="acceptedStatuses"/> widens the success set
    /// beyond 200/201/204 for an endpoint verified to answer differently (Deluno's file-changed answers 202
    /// Accepted, #507); every other caller accepts only those three.
    /// </summary>
    public Task<WireValue?> PostJsonAsync(string path, WireObject body, IReadOnlyCollection<int>? acceptedStatuses = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var request = new HttpRequestMessage(HttpMethod.Post, Url(path))
        {
            Content = JsonContent(body),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        return SendAsync(request, allowEmpty: false, cancellationToken, acceptedStatuses);
    }

    /// <summary>DELETE: success is the status code alone. Booleans are sent as <c>true</c>/<c>false</c>, which is how ASP.NET binds them.</summary>
    public async Task DeleteAsync(string path, IReadOnlyList<KeyValuePair<string, object>>? parameters = null, CancellationToken cancellationToken = default)
    {
        var flat = parameters?.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value switch
        {
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty,
        })).ToList();
        var request = new HttpRequestMessage(HttpMethod.Delete, Url(path, flat));
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await SendAsync(request, allowEmpty: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A GET that only has to succeed: it throws on an unreachable manager, a bad status or a non-JSON answer.</summary>
    public Task HealthOkAsync(string path, CancellationToken cancellationToken = default) => GetJsonAsync(path, cancellationToken: cancellationToken);

    private static ByteArrayContent JsonContent(WireObject body)
    {
        var content = new ByteArrayContent(WireJsonWriter.DumpsUtf8(body, WireJsonFormat.Default));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static readonly int[] DefaultAcceptedStatuses = [200, 201, 204];

    /// <summary>Send the request and read the answer as JSON, classifying transport, status and parse failures.</summary>
    private async Task<WireValue?> SendAsync(HttpRequestMessage request, bool allowEmpty, CancellationToken cancellationToken, IReadOnlyCollection<int>? acceptedStatuses = null)
    {
        using (request)
        {
            ConnectionTag.Apply(request, _connection);

            // Real Sonarr, Radarr and Deluno never redirect their own API; a manager base URL that does is either
            // misconfigured or is answering from somewhere Weir did not ask, and following it would carry the
            // X-Api-Key header to whatever host the redirect names.
            using var client = new HttpClient(_handlers.Handler(followRedirects: false, ManagerAddressPolicy.Local), disposeHandler: false) { Timeout = _timeout };
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
            {
                throw Unreachable(exception);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    throw new MediaManagerRedirectedException();
                }

                byte[] raw;
                try
                {
                    raw = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
                {
                    throw Unreachable(exception);
                }

                if (status is < 200 or >= 300)
                {
                    // Neither exception carries the response body: an unreachable manager already leaks nothing, and
                    // the connection test and setup-check screens that surface these messages must not become a way
                    // to read back bytes from whatever answered instead of the manager Weir asked for. The one thing kept
                    // is the answer's error code, and only when it is shaped like a code (ErrorCodeOf), so a caller can
                    // tell what a status meant without reading the manager's words.
                    if (status == 429)
                    {
                        throw new MediaManagerRateLimitedException(
                            $"HTTP {status.ToString(CultureInfo.InvariantCulture)}",
                            RetryAfterSeconds(response.Headers.TryGetValues("Retry-After", out var values) ? values.First() : null));
                    }

                    throw new MediaManagerHttpException($"HTTP {status.ToString(CultureInfo.InvariantCulture)}", status, ErrorCodeOf(raw));
                }

                if (raw.Length == 0 && allowEmpty)
                {
                    return null;
                }

                if (!(acceptedStatuses ?? DefaultAcceptedStatuses).Contains(status))
                {
                    throw new MediaManagerHttpException($"unexpected HTTP {status.ToString(CultureInfo.InvariantCulture)}");
                }

                if (raw.Length == 0)
                {
                    return null;
                }

                // #544 item 1: a 2xx answer that is not JSON (an HTML login page from a reverse proxy, most often)
                // is classified rather than left to surface as a 500, so a connection test and the capabilities list
                // report a plain "this is not that API" message.
                try
                {
                    return WireJsonParser.Parse(new UTF8Encoding(false, true).GetString(raw));
                }
                catch (Exception exception) when (exception is WireJsonDecodeException or DecoderFallbackException)
                {
                    throw new MediaManagerHttpException(
                        $"HTTP {status.ToString(CultureInfo.InvariantCulture)}: the response was not valid JSON. " +
                        "This does not look like a Sonarr, Radarr or Deluno API at this address.",
                        exception);
                }
            }
        }
    }

    /// <summary>
    /// The <c>error</c> member of a JSON object answer when it is a code (lower-case words joined by underscores, at most 64
    /// characters), otherwise null. Anything else in the answer, and an answer that is not JSON, is not kept.
    /// </summary>
    internal static string? ErrorCodeOf(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            var parsed = WireJsonParser.Parse(new UTF8Encoding(false, true).GetString(raw));
            return parsed is WireObject answer && answer.Get("error") is WireString { Value.Length: <= 64 } code && ErrorCodeShape.IsMatch(code.Value) ? code.Value : null;
        }
        catch (Exception exception) when (exception is WireJsonDecodeException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly Regex ErrorCodeShape = new(@"^[a-z][a-z0-9]*(_[a-z0-9]+)*\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>The <c>Retry-After</c> header as a non-negative delay in seconds, or null (an HTTP-date is not acted on).</summary>
    public static double? RetryAfterSeconds(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var text = WireStrings.Strip(raw);
        if (text.Length == 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || double.IsNaN(seconds))
        {
            return null;
        }

        return seconds >= 0 ? seconds : null;
    }

    internal static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException ||
        (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    internal static MediaManagerUnreachableException Unreachable(Exception exception) =>
        new($"The connection failed: {ClassifyTransportFailure(exception)}.", exception);

    /// <summary>
    /// A plain word for why the connection failed, never the framework's own exception text: that text can carry
    /// details (a resolved address, a certificate subject) that do not belong in a message an unauthenticated
    /// caller or a viewer-role screen can read back.
    /// </summary>
    internal static string ClassifyTransportFailure(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return "timed out";
        }

        return Innermost(exception) switch
        {
            ManagerAddressRefusedException refused => refused.Message,
            AuthenticationException => "a certificate problem",
            _ => "couldn't connect",
        };
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }
}
