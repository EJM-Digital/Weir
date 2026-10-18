using System.Net;
using System.Text;

namespace Weir.ArrRecorder;

/// <summary>
/// Stands where Weir's intake webhook stands and keeps what the app posts to it. The app really sends these; the 200 it gets back is
/// this listener's, not Weir's, so a recording of one holds the app's request and says nothing about Weir's answer.
/// </summary>
internal sealed class WebhookReceiver : IDisposable
{
    private static readonly string[] HeadersKept = ["content-type", "user-agent", "x-webhook-secret"];

    private readonly HttpListener _listener = new();
    private readonly List<(string EventType, Exchange Exchange)> _received = [];
    private readonly Lock _lock = new();
    private readonly Task _loop;

    private WebhookReceiver(int port)
    {
        _listener.Prefixes.Add($"http://+:{port}/");
        _listener.Start();
        _loop = Task.Run(ListenAsync);
    }

    public static WebhookReceiver Start(int port) => new(port);

    /// <summary>The first request of each event type, in the order they arrived.</summary>
    public IReadOnlyList<(string EventType, Exchange Exchange)> FirstOfEachType()
    {
        lock (_lock)
        {
            return [.. _received.GroupBy(entry => entry.EventType).Select(group => group.First())];
        }
    }

    public bool Has(string eventType)
    {
        lock (_lock)
        {
            return _received.Any(entry => entry.EventType == eventType);
        }
    }

    /// <summary>Waits for the app to post an event of this type; false when it never does.</summary>
    public async Task<bool> WaitForAsync(string eventType, TimeSpan limit)
    {
        try
        {
            await Wait.UntilAsync($"a {eventType} webhook", limit, () => Task.FromResult(Has(eventType))).ConfigureAwait(false);
            return true;
        }
        catch (RecorderException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends with the listener.
        }
    }

    /// <summary>The torrent the app is told to fetch: named after the release in the path, and built when asked for.</summary>
    private static void ServeTorrent(HttpListenerContext context)
    {
        var name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(context.Request.Url!.AbsolutePath));
        var bytes = TorrentFile.Build(name);
        context.Response.ContentType = "application/x-bittorrent";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url!.AbsolutePath.StartsWith("/torrents/", StringComparison.Ordinal))
            {
                ServeTorrent(context);
                continue;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var bytes = Encoding.UTF8.GetBytes(await reader.ReadToEndAsync().ConfigureAwait(false));
            var body = RecordingTransport.ParseJson(bytes);
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in context.Request.Headers.AllKeys.OfType<string>())
            {
                if (HeadersKept.Contains(name.ToLowerInvariant()))
                {
                    headers[name.ToLowerInvariant()] = context.Request.Headers[name] ?? string.Empty;
                }
            }

            var eventType = (string?)body?["eventType"] ?? "unknown";
            lock (_lock)
            {
                _received.Add((eventType, new Exchange(
                    context.Request.HttpMethod,
                    context.Request.Url!.PathAndQuery,
                    headers,
                    body,
                    200,
                    [],
                    null,
                    null)));
            }

            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }
}
