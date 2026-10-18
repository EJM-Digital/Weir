using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using Weir.Core.Configuration;
using Weir.Core.Json;
using Weir.Core.Notifications;
using Weir.Core.Updates;
using Weir.Infrastructure.Notifications;

namespace Weir.Infrastructure.Http;

/// <summary>Finds the newest GitHub release this install should be offered.</summary>
public interface IReleaseCatalogClient
{
    /// <summary>
    /// The newest published release by SemVer precedence, counting pre-releases only when <paramref name="currentVersion"/> is a
    /// pre-release itself; null when none qualifies. Throws <see cref="ReleaseFetchException"/> for an HTTP error status (with its
    /// <see cref="ReleaseFetchException.RateLimit"/> when GitHub is limiting the network), other exceptions for anything else.
    /// </summary>
    Task<GitHubReleaseRecord?> FetchLatestAsync(string currentVersion, CancellationToken cancellationToken);
}

/// <summary>An operator-facing failure to reach or use an external endpoint; the message is shown as is.</summary>
public sealed class ExternalEndpointException : Exception
{
    public ExternalEndpointException()
    {
    }

    public ExternalEndpointException(string message)
        : base(message)
    {
    }

    public ExternalEndpointException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Posts JSON to a public endpoint.</summary>
public interface IExternalJsonPoster
{
    Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the host, refuses any non-public answer, connects to exactly the validated address with no
/// redirects and TLS 1.2 or newer, so a hostname cannot be re-resolved to a private address between the
/// check and the connection.
/// </summary>
public sealed class ExternalJsonPoster : IExternalJsonPoster
{
    /// <summary>The address a name resolves to when a test has stood in for the network: public, and never connected to.</summary>
    private static readonly IPAddress StandInResolvedAddress = IPAddress.Parse("93.184.216.34");

    private readonly int? _standInPort;

    /// <param name="standInPort">
    /// For tests only (<see cref="Weir.Core.Configuration.WeirOptions.NotificationStandInPort"/>): when set, every name resolves to a public address and the
    /// connection goes to this port on this machine, so the address checks run as they do for real and only the destination is the test's.
    /// </param>
    public ExternalJsonPoster(int? standInPort = null)
    {
        _standInPort = standInPort;
    }

    public async Task<int> PostJsonAsync(string url, byte[] body, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);
        var endpoint = await ResolveAsync(url, _standInPort is null ? null : StandInResolve, cancellationToken).ConfigureAwait(false);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = timeout,
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
            ConnectCallback = async (context, token) =>
            {
                try
                {
                    return _standInPort is { } standIn
                        ? await LoopbackFirstConnect.ConnectAsync(LoopbackFirstConnect.LoopbackAddresses(), standIn, token).ConfigureAwait(false)
                        : await LoopbackFirstConnect.ConnectAsync([endpoint.Address], endpoint.Port, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotConnect, exception);
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Uri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.InnerException is ExternalEndpointException inner)
        {
            throw new ExternalEndpointException(inner.Message, exception);
        }
        catch (HttpRequestException exception) when (exception.InnerException is AuthenticationException or IOException && endpoint.Uri.Scheme == "https")
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotSecure, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotDeliver, exception);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw new ExternalEndpointException(ExternalUrlPolicy.RedirectRefused);
            }

            return status;
        }
    }

    private sealed record ResolvedEndpoint(Uri Uri, IPAddress Address, int Port);

    private static Task<IPAddress[]> StandInResolve(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { StandInResolvedAddress });

    /// <summary>Validates the URL and resolves it to a public address, throwing <see cref="ExternalEndpointException"/> otherwise.</summary>
    private static async Task<ResolvedEndpoint> ResolveAsync(string raw, OutboundAddressGuard.HostResolver? resolveHost, CancellationToken cancellationToken)
    {
        SplitUrl parsed;
        try
        {
            parsed = SplitUrl.Parse((raw ?? string.Empty).Trim());
        }
        catch (WireValueException exception)
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.InvalidDestination, exception);
        }

        var hostname = parsed.Hostname;
        if (parsed.Scheme is not ("http" or "https") || hostname is null)
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.InvalidDestination);
        }

        if (!string.IsNullOrEmpty(parsed.Username) || !string.IsNullOrEmpty(parsed.Password))
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.EmbeddedCredentials);
        }

        int port;
        try
        {
            port = parsed.Port is { } explicitPort && explicitPort != 0 ? explicitPort : parsed.Scheme == "https" ? 443 : 80;
        }
        catch (WireValueException exception)
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.InvalidPort, exception);
        }

        var resolved = await OutboundAddressGuard.ResolveAllowedAsync(hostname, OutboundAddressGuard.IsPublic, resolveHost, cancellationToken).ConfigureAwait(false);
        if (resolved.CouldNotResolve)
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.CouldNotResolve);
        }

        // Every resolved address must be global, not merely the first one: a name that resolves to both a public
        // and a private address must not be trusted just because the public one happened to come first.
        if (resolved.Allowed.Count == 0)
        {
            throw new ExternalEndpointException(ExternalUrlPolicy.NonPublicAddress);
        }

        var path = parsed.Path.Length == 0 ? "/" : parsed.Path;
        if (parsed.Query.Length > 0)
        {
            path += "?" + parsed.Query;
        }

        var host = hostname.Contains(':', StringComparison.Ordinal) ? $"[{hostname}]" : hostname;
        var uri = new Uri($"{parsed.Scheme}://{host}:{port}{path}");
        return new ResolvedEndpoint(uri, resolved.Allowed[0], port);
    }
}

/// <summary>Delivers notifications to Discord and webhook channels.</summary>
public sealed class NotificationDispatcher
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10);
    private readonly IExternalJsonPoster _poster;
    private readonly TimeProvider _time;
    private readonly NotificationChannelStore _channels;
    private readonly MachineIdentity _machine;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public NotificationDispatcher(IExternalJsonPoster poster, TimeProvider time, NotificationChannelStore channels, MachineIdentity machine)
    {
        _poster = poster;
        _time = time;
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    }

    /// <summary>Posts one notification to one channel, in the channel's payload shape; returns the HTTP status.</summary>
    public Task<int> PostOneAsync(NotificationChannelRecord channel, string title, string detail, string jobEvent, string module, long jobId, string jobKind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var now = Core.Time.Timestamp.UtcNow(_time);
        var body = channel.Provider == "discord"
            ? NotificationRules.DiscordPayload(title, detail, jobEvent, module, jobId, now, _machine.AppName)
            : NotificationRules.WebhookPayload(jobEvent, module, jobId, jobKind, title, detail, now, _machine.Name);
        return _poster.PostJsonAsync(channel.Url, body, new Dictionary<string, string>(StringComparer.Ordinal) { ["User-Agent"] = "Weir/1.0" }, DispatchTimeout, cancellationToken);
    }

    /// <summary>Sends a test notification: the error text, or <see langword="null"/> on success.</summary>
    public async Task<string?> TestAsync(NotificationChannelRecord channel, CancellationToken cancellationToken)
    {
        try
        {
            var status = await PostOneAsync(
                channel, $"{_machine.AppName} test notification", $"This is a test notification from {_machine.AppName}.", "job_completed", "processing", 0, "test", cancellationToken)
                .ConfigureAwait(false);
            return status >= 400 ? ExternalUrlPolicy.DestinationRefused(status) : null;
        }
        catch (ExternalEndpointException exception)
        {
            return exception.Message;
        }
#pragma warning disable CA1031 // A test delivery that fails for any reason reads as the generic delivery error, never an exception page.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return ExternalUrlPolicy.GenericDeliveryError;
        }
    }

    /// <summary>
    /// Sends <c>{module}_job_{eventKind}</c> to every enabled channel subscribed
    /// to it or to its generic form, without the caller waiting. For <c>failed</c> the job must be permanently
    /// failed. Never throws; failures are logged through <paramref name="warn"/>.
    /// </summary>
    /// <param name="database">The database to read channels and the job's status from.</param>
    /// <param name="module">The module the job belongs to (e.g. <c>processing</c>).</param>
    /// <param name="eventKind"><c>completed</c> or <c>failed</c>.</param>
    /// <param name="jobId">The job's id, for the notification payload and the permanently-failed check.</param>
    /// <param name="jobKind">The job's kind, for the notification payload.</param>
    /// <param name="warn">Where a delivery or lookup failure is logged; never thrown to the caller.</param>
    /// <param name="willRetry">
    /// Whether another attempt follows this failure, so the detail text says so instead of claiming
    /// retries are exhausted (#540). Ignored for <c>completed</c>.
    /// </param>
    /// <param name="workFailed">
    /// The job ended normally but the work it did failed for good (a file the tools could not finish), so its status is not
    /// <c>failed</c> and the check that the job failed for good is skipped.
    /// </param>
    public void DispatchJobNotification(
        Sqlite.SqliteDatabase database, string module, string eventKind, long jobId, string jobKind, Action<string, Exception?> warn, bool willRetry = false, bool workFailed = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(warn);
        var (jobEvent, title, detail) = NotificationRules.JobNotification(module, eventKind, jobId, jobKind, _machine.AppName, willRetry, workFailed);
        Track(Task.Run(async () =>
        {
            List<NotificationChannelRecord> channels;
            try
            {
                var uow = await Sqlite.UnitOfWork.OpenAsync(database).ConfigureAwait(false);
                await using (uow.ConfigureAwait(false))
                {
                    // Any failure, not only file processing: an alert says "failed" only once no retry follows.
                    if (eventKind == "failed" && !workFailed)
                    {
                        var status = await uow.ScalarAsync("SELECT status FROM jobs WHERE id = $id", ("$id", jobId)).ConfigureAwait(false);
                        if (status is not string text || text != "failed")
                        {
                            return;
                        }
                    }

                    channels = await _channels.ForEventAsync(uow, jobEvent).ConfigureAwait(false);
                    var generic = jobEvent.StartsWith(module + "_", StringComparison.Ordinal) ? jobEvent[(module.Length + 1)..] : jobEvent;
                    if (generic != jobEvent)
                    {
                        foreach (var channel in await _channels.ForEventAsync(uow, generic).ConfigureAwait(false))
                        {
                            if (!channels.Any(existing => existing.Id == channel.Id))
                            {
                                channels.Add(channel);
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
            {
                warn($"Notification dispatch: failed to read channels for event={jobEvent}", exception);
                return;
            }

            foreach (var channel in channels)
            {
                try
                {
                    var status = await PostOneAsync(channel, title, detail, jobEvent, module, jobId, jobKind, CancellationToken.None).ConfigureAwait(false);
                    if (status >= 400)
                    {
                        warn($"The alert \"{channel.Label}\" was not delivered, and Weir will not send it again. {ExternalUrlPolicy.DestinationRefused(status)}", null);
                    }
                }
                catch (Exception exception) when (exception is ExternalEndpointException or HttpRequestException or IOException or OperationCanceledException)
                {
                    var message = exception is ExternalEndpointException ? exception.Message : ExternalUrlPolicy.GenericDeliveryError;
                    warn($"The alert \"{channel.Label}\" was not delivered, and Weir will not send it again. {message}", null);
                }
            }
        }));
    }

    /// <summary>
    /// Completes once every delivery started so far has finished. The caller of <see cref="DispatchJobNotification"/>
    /// never waits for its delivery; this has no ceiling of its own, so it is for tests that need deliveries to
    /// settle before asserting — callers should apply their own timeout.
    /// </summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Keys);

    private void Track(Task delivery)
    {
        _inFlight.TryAdd(delivery, 0);
        _ = delivery.ContinueWith(finished => _inFlight.TryRemove(finished, out _), TaskScheduler.Default);
    }
}
