using System.Text;
using System.Text.Json.Nodes;
using Weir.Infrastructure.MediaManagers;

namespace Weir.ArrRecorder;

/// <summary>One request and the reply it got, as the recording keeps them.</summary>
internal sealed record Exchange(
    string Method,
    string Path,
    SortedDictionary<string, string> RequestHeaders,
    JsonNode? RequestBody,
    int Status,
    SortedDictionary<string, string> ReplyHeaders,
    JsonNode? ReplyBody,
    string? ReplyText);

/// <summary>The exchanges made since the last <see cref="Clear"/>.</summary>
internal sealed class ExchangeLog
{
    private readonly List<Exchange> _exchanges = [];
    private readonly Lock _lock = new();

    public void Add(Exchange exchange)
    {
        lock (_lock)
        {
            _exchanges.Add(exchange);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _exchanges.Clear();
        }
    }

    public IReadOnlyList<Exchange> Snapshot()
    {
        lock (_lock)
        {
            return [.. _exchanges];
        }
    }
}

/// <summary>
/// Weir's own transport for a manager (the real sockets handler, with its address rules) with a recorder in front: every
/// request Weir's client code sends and every reply it gets back is kept, byte for byte, in the <see cref="ExchangeLog"/>.
/// </summary>
internal sealed class RecordingTransport : IManagerHttpHandlerFactory
{
    /// <summary>The request headers Weir sets that mean something to the manager; the rest are the HTTP stack's.</summary>
    private static readonly string[] RequestHeadersKept = ["accept", "content-type", "x-api-key"];

    private static readonly string[] ReplyHeadersKept = ["content-type", "retry-after", "location", "www-authenticate"];

    private readonly IManagerHttpHandlerFactory _inner;
    private readonly ExchangeLog _log;

    public RecordingTransport(IManagerHttpHandlerFactory inner, ExchangeLog log)
    {
        _inner = inner;
        _log = log;
    }

    public HttpMessageHandler Handler(bool followRedirects, ManagerAddressPolicy policy = ManagerAddressPolicy.Local) =>
        new Recording(_inner.Handler(followRedirects, policy), _log);

    /// <summary>The JSON in <paramref name="bytes"/>, or null when it is empty or not JSON.</summary>
    internal static JsonNode? ParseJson(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(bytes);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static SortedDictionary<string, string> Kept(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string[] names)
    {
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            var lower = name.ToLowerInvariant();
            if (names.Contains(lower))
            {
                kept[lower] = string.Join(", ", values);
            }
        }

        return kept;
    }

    private sealed class Recording(HttpMessageHandler next, ExchangeLog log) : DelegatingHandler(next)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestBytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var requestHeaders = Kept(request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()), RequestHeadersKept);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var replyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var replyHeaders = Kept(response.Headers.Concat(response.Content.Headers), ReplyHeadersKept);
            var replyJson = ParseJson(replyBytes);
            log.Add(new Exchange(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                requestHeaders,
                ParseJson(requestBytes),
                (int)response.StatusCode,
                replyHeaders,
                replyJson,
                replyJson is null && replyBytes.Length > 0 ? Encoding.UTF8.GetString(replyBytes) : null));

            // The caller reads the body again from a fresh buffer.
            var content = new ByteArrayContent(replyBytes);
            foreach (var header in response.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = content;
            return response;
        }
    }
}
