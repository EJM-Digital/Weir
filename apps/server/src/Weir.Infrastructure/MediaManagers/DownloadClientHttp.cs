using System.Net.Http.Headers;
using System.Text;
using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Notifications;
using Weir.Infrastructure.ConnectionTraffic;

namespace Weir.Infrastructure.MediaManagers;

/// <summary>One HTTP answer from a download client. Kept low-level: each dialect decides what its own status codes and body mean.</summary>
public sealed record DownloadClientHttpResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Whether the client answered with a 2xx status.</summary>
    public bool Succeeded => Status is >= 200 and < 300;

    public string BodyText => Body.Length == 0 ? string.Empty : new UTF8Encoding(false, false).GetString(Body);

    /// <summary>The body parsed as JSON, or null for an empty body or one that is not valid JSON.</summary>
    public WireValue? Json()
    {
        if (Body.Length == 0)
        {
            return null;
        }

        try
        {
            return WireJsonParser.Parse(BodyText);
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }
}

/// <summary>
/// The shared outbound transport for the five download-client dialects (SABnzbd, NZBGet, qBittorrent, Deluge,
/// Transmission), built through <see cref="IManagerHttpHandlerFactory"/> the same defensive way
/// <see cref="MediaManagerHttpClient"/> is for media managers (no redirects, the local-address policy, a bounded
/// timeout). It stays low-level rather than assuming an X-Api-Key header or a fixed success status: each dialect's
/// own success condition differs (a cookie login, a 409 handshake, a literal "Ok."/"Fails." body), so the dialect
/// classifies its own answer instead of this shared client guessing at one.
/// </summary>
public sealed class DownloadClientHttpClient
{
    private readonly string _base;
    private readonly IManagerHttpHandlerFactory _handlers;
    private readonly TimeSpan _timeout;
    private readonly ConnectionRef? _connection;

    /// <summary>
    /// A client for the download client at <paramref name="baseUrl"/>. <paramref name="connection"/> is the saved connection it
    /// talks to, so its calls light that connection up; null for one that is not saved.
    /// </summary>
    /// <exception cref="DownloadClientHttpException">The base URL is not a plain http(s) address.</exception>
    public DownloadClientHttpClient(string baseUrl, IManagerHttpHandlerFactory handlers, TimeSpan? timeout = null, ConnectionRef? connection = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        try
        {
            _base = ExternalUrlPolicy.NormalizeLocalServiceBaseUrl(baseUrl);
        }
        catch (WireValueException exception)
        {
            throw new DownloadClientHttpException(exception.Message, exception);
        }

        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _connection = connection;
    }

    public string Url(string path, IReadOnlyList<KeyValuePair<string, string>>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (SplitUrl.Parse(path).Scheme.Length > 0)
        {
            throw new DownloadClientHttpException("A download client API path must be relative.");
        }

        var url = _base + (path.StartsWith('/') ? path : "/" + path);
        return parameters is { Count: > 0 } ? url + "?" + QueryStrings.Encode(parameters) : url;
    }

    /// <summary>A JSON request body, matching how every JSON-RPC dialect here builds one.</summary>
    public static ByteArrayContent JsonContent(WireObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var content = new ByteArrayContent(WireJsonWriter.DumpsUtf8(body, WireJsonFormat.Default));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Send the request and classify a transport failure (unreachable, refused, timed out) the same way
    /// <see cref="MediaManagerHttpClient"/> does. Never throws for a status code or an unparsable body — the
    /// caller's dialect decides what those mean for the client it is talking to.
    /// </summary>
    public async Task<DownloadClientHttpResponse> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? queryParameters = null,
        HttpContent? content = null,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, Url(path, queryParameters)) { Content = content };
        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using (request)
        {
            ConnectionTag.Apply(request, _connection);
            using var client = new HttpClient(_handlers.Handler(followRedirects: false, ManagerAddressPolicy.Local), disposeHandler: false) { Timeout = _timeout };
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (MediaManagerHttpClient.IsTransportFailure(exception, cancellationToken))
            {
                throw Unreachable(exception);
            }

            using (response)
            {
                // SendAsync has read the whole body already (a failure while it was read surfaced there), so this is a copy.
                var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var responseHeaders = response.Headers.Concat(response.Content.Headers)
                    .ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase);
                return new DownloadClientHttpResponse((int)response.StatusCode, raw, responseHeaders);
            }
        }
    }

    private static DownloadClientUnreachableException Unreachable(Exception exception) =>
        new($"The connection failed: {MediaManagerHttpClient.ClassifyTransportFailure(exception)}.", exception);
}
