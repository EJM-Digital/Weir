using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Weir.Infrastructure.Tests.MediaManagers;

/// <summary>What the fake Deluno was sent on its events route.</summary>
internal sealed record DelunoReport(string Body, string? ApiKey);

/// <summary>
/// A loopback server standing in for Deluno over real HTTP: it answers the health check Weir's heartbeat makes and takes the reports
/// Weir posts to <see cref="EventsPath"/>, answering each with the next reply it was given, then 202 unless told otherwise. It can be stopped and started
/// again on the same port, which is what a Deluno that goes down and comes back looks like to Weir.
/// </summary>
internal sealed class FakeDelunoServer : IDisposable
{
    public const string EventsPath = "/api/integrations/processors/events";
    public const string HealthPath = "/api/integrations/external/health";

    private readonly ConcurrentQueue<DelunoReply> _replies = new();
    private readonly ConcurrentQueue<DelunoReport> _reports = new();
    private DelunoReply _otherwise = new(202, "{}", "application/json");
    private HttpListener? _listener;

    public FakeDelunoServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Start();
    }

    public int Port { get; }

    public string BaseUrl => $"http://localhost:{Port}";

    /// <summary>The reports received so far, in the order they arrived.</summary>
    public IReadOnlyList<DelunoReport> Reports => [.. _reports];

    /// <summary>Answer the next report that arrives with this reply; later ones queue behind it.</summary>
    public FakeDelunoServer Answer(DelunoReply reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    /// <summary>Answer every report that has no reply of its own queued with this one.</summary>
    public void AnswerEverythingWith(DelunoReply reply) => _otherwise = reply;

    public void Start()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();
        _listener = listener;
        _ = ServeAsync(listener);
    }

    /// <summary>Stop answering: a connection to the port is refused until <see cref="Start"/>.</summary>
    public void Stop()
    {
        _listener?.Close();
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            Respond(context);
        }
    }

    private void Respond(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        DelunoReply reply;
        if (request.Url!.AbsolutePath == HealthPath)
        {
            reply = new DelunoReply(200, "{}", "application/json");
        }
        else if (request.Url.AbsolutePath == EventsPath && request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            _reports.Enqueue(new DelunoReport(reader.ReadToEnd(), request.Headers["X-Api-Key"]));
            reply = _replies.TryDequeue(out var next) ? next : _otherwise;
        }
        else
        {
            reply = new DelunoReply(404, string.Empty, "text/plain");
        }

        response.StatusCode = reply.Status;
        response.ContentType = reply.ContentType;
        foreach (var (name, value) in reply.Headers)
        {
            response.Headers.Add(name, value);
        }

        var bytes = Encoding.UTF8.GetBytes(reply.Body);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }
}

/// <summary>One answer of the fake Deluno.</summary>
internal sealed record DelunoReply(int Status, string Body, string ContentType, params (string Name, string Value)[] Headers)
{
    /// <summary>The plain-text 503 a Deluno that is not ready to take reports answers with.</summary>
    public static DelunoReply Unavailable(params (string Name, string Value)[] headers) => new(503, "Service Unavailable", "text/plain", headers);

    public static DelunoReply Of(int status) => new(status, string.Empty, "text/plain");
}
