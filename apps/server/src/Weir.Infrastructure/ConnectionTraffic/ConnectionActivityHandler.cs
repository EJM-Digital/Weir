using System.Net;

namespace Weir.Infrastructure.ConnectionTraffic;

/// <summary>
/// The one place every call to a media manager or download client is reported from: it says a call was asked, and how it
/// ended and how long it took, for every request <see cref="ConnectionTag"/> marks. Wraps the transport the connection
/// clients share, so no call site reports for itself.
/// </summary>
public sealed class ConnectionActivityHandler : DelegatingHandler
{
    private readonly ConnectionActivityHub _hub;
    private readonly TimeProvider _time;

    public ConnectionActivityHandler(HttpMessageHandler inner, ConnectionActivityHub hub, TimeProvider time)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(time);
        _hub = hub;
        _time = time;
    }

    /// <summary>
    /// Whether an answer means the connection is not working for Weir: it is broken (5xx) or refuses Weir's credentials.
    /// Any other status is an answer, even a "not found" about one title.
    /// </summary>
    public static bool IsFailure(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ConnectionTag.TryRead(request, out var connection))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        _hub.Publish(connection, ConnectionPhase.Asked, ConnectionDirection.Outbound, milliseconds: null);
        var started = _time.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Reports the failure and rethrows it unchanged; the caller classifies the exception.
        catch (Exception)
#pragma warning restore CA1031
        {
            _hub.Publish(connection, ConnectionPhase.Failed, ConnectionDirection.Outbound, ElapsedMilliseconds(started));
            throw;
        }

        var phase = IsFailure(response.StatusCode) ? ConnectionPhase.Failed : ConnectionPhase.Answered;
        _hub.Publish(connection, phase, ConnectionDirection.Outbound, ElapsedMilliseconds(started));
        return response;
    }

    private long ElapsedMilliseconds(long started) => (long)_time.GetElapsedTime(started).TotalMilliseconds;
}
