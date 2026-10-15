using System.Net;
using System.Net.Sockets;
using Xunit.Sdk;

namespace Weir.Contract.Tests.Harness.Fakes;

/// <summary>What a fake server sends back.</summary>
public sealed record HttpAnswer(int Status, byte[] Payload, string? ContentType = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// A real HTTP server on 127.0.0.1 and a free port, running inside the test process, that records every request it receives.
/// Derived fakes decide the answers. Disposing it stops it.
/// </summary>
public abstract class FakeHttpServer : IDisposable
{
    private const int StartAttempts = 20;

    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly HttpListener _listener;
    private TaskCompletionSource _arrived = NewSignal();

    protected FakeHttpServer()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            // A listener that failed to start cannot be reused (on Linux it is disposed), so each attempt gets its own.
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < StartAttempts)
            {
                // Another process took the port between choosing it and listening; choose again.
                listener.Close();
            }
        }

        _ = Task.Run(AcceptAsync);
    }

    /// <summary>For example <c>http://127.0.0.1:51234</c>, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Every request received so far, oldest first.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> requests satisfy <paramref name="matches"/> and returns them.
    /// Fails with the last requests it saw when <paramref name="timeout"/> (60 seconds by default) passes first.
    /// </summary>
    protected async Task<IReadOnlyList<RecordedRequest>> WaitForAsync(
        Func<RecordedRequest, bool> matches, int count, TimeSpan? timeout, string what)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            Task signal;
            IReadOnlyList<RecordedRequest> seen;
            lock (_gate)
            {
                seen = [.. _requests];
                signal = _arrived.Task;
            }

            var found = seen.Where(matches).ToList();
            if (found.Count >= count)
            {
                return found;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                var last = string.Join(", ", seen.TakeLast(20).Select(request => $"{request.Method} {request.Path}"));
                throw new XunitException(
                    $"{what} was not received {count}x within {limit.TotalSeconds:0}s; last requests: {(last.Length == 0 ? "nothing" : last)}");
            }

            await Task.WhenAny(signal, Task.Delay(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500)));
        }
    }

    protected abstract HttpAnswer Answer(RecordedRequest request);

    public virtual void Dispose()
    {
        _listener.Abort();
        GC.SuppressFinalize(this);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception stopped) when (stopped is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            var request = await ReadAsync(context.Request);
            HttpAnswer answer;
            try
            {
                answer = Answer(request);
            }
            catch (Exception problem)
            {
                answer = new HttpAnswer(500, System.Text.Encoding.UTF8.GetBytes($"{{\"message\":\"the fake raised: {problem.GetType().Name}\"}}"), "application/json");
            }

            // Recorded once the route has run, so a test that waits for a request and then looks at what the route changed
            // (a queue item removed, say) never sees the request first.
            lock (_gate)
            {
                _requests.Add(request);
                _arrived.TrySetResult();
                _arrived = NewSignal();
            }

            var response = context.Response;
            response.StatusCode = answer.Status;
            if (answer.ContentType is not null)
            {
                response.ContentType = answer.ContentType;
            }

            foreach (var (name, value) in answer.Headers ?? new Dictionary<string, string>())
            {
                response.AddHeader(name, value);
            }

            response.ContentLength64 = answer.Payload.Length;
            if (answer.Payload.Length > 0 && request.Method != "HEAD")
            {
                await response.OutputStream.WriteAsync(answer.Payload);
            }
        }
        catch (Exception problem) when (problem is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The client went away, or the fake was stopped, mid-answer.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception problem) when (problem is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed.
            }
        }
    }

    private static async Task<RecordedRequest> ReadAsync(HttpListenerRequest request)
    {
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body);
        var raw = request.RawUrl ?? "/";
        var split = raw.IndexOf('?', StringComparison.Ordinal);
        var path = split < 0 ? raw : raw[..split];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Headers.AllKeys.OfType<string>())
        {
            headers[name] = request.Headers[name] ?? string.Empty;
        }

        return new RecordedRequest(
            request.HttpMethod.ToUpperInvariant(), path, ParseQuery(split < 0 ? string.Empty : raw[(split + 1)..]), headers, body.ToArray(), DateTimeOffset.UtcNow);
    }

    // Query values are percent-decoded with "+" read as a space, and a parameter without a value is dropped.
    private static Dictionary<string, string[]> ParseQuery(string query)
    {
        var parsed = new Dictionary<string, List<string>>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || parts[1].Length == 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (!parsed.TryGetValue(name, out var values))
            {
                parsed[name] = values = [];
            }

            values.Add(Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
        }

        return parsed.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
    }
}
