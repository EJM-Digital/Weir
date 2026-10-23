using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Infrastructure.ConnectionTraffic;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>connection.activity</c> side of the Activity stream: one frame each time a media manager or download client is
/// asked, answers, fails, or calls Weir, so the Connections views light up without polling. Throttling is the hub's: every
/// open stream shares <see cref="ConnectionActivityHub"/>, and each connection already sends at most one frame per phase per
/// <see cref="ConnectionActivityHub.Throttle"/>.
/// </summary>
public sealed class ConnectionActivityFrames
{
    private readonly ConnectionActivityHub _hub;

    public ConnectionActivityFrames(ConnectionActivityHub hub) => _hub = hub;

    /// <summary>The frames for one open stream, from when it opens until it ends.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = _hub.Subscribe();
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return Frame(await subscription.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The <c>connection.activity</c> SSE frame for one thing that happened on a connection.</summary>
    public static string Frame(ConnectionActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var data = new WireObject()
            .Set("kind", activity.Connection.Kind == ConnectionKind.MediaManager ? "media_manager" : "download_client")
            .Set("id", activity.Connection.Id)
            .Set("phase", activity.Phase switch
            {
                ConnectionPhase.Asked => "asked",
                ConnectionPhase.Answered => "answered",
                _ => "failed",
            })
            .Set("direction", activity.Direction == ConnectionDirection.Outbound ? "outbound" : "inbound")
            .Set("at", activity.At.ToWireText())
            .Set("ms", activity.Milliseconds is { } milliseconds ? WireValue.Of(milliseconds) : WireValue.Null);
        return $"event: connection.activity\ndata: {WireJsonWriter.Dumps(data, WireJsonFormat.Compact)}\n\n";
    }
}
