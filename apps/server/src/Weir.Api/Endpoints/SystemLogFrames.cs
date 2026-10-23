using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Logging;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>system.log</c> side of the Activity stream: one frame for each line Weir writes to its log that the System log shows
/// (warnings, errors and Weir's own information), so the Log card gains a row the moment it happens. A stream gets nothing
/// logged before it opened; <c>GET /suite/logs</c> has that.
/// </summary>
public sealed class SystemLogFrames
{
    private readonly LogAlerts _alerts;

    public SystemLogFrames(LogAlerts alerts) => _alerts = alerts;

    /// <summary>The frames for one open stream, from when it opens until it ends.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = _alerts.Subscribe();
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return Frame(await subscription.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The <c>system.log</c> SSE frame for one logged line.</summary>
    public static string Frame(LogAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var data = new WireObject()
            .Set("at", Timestamp.FromDateTimeOffset(alert.At.ToUniversalTime()).ToWireText())
            .Set("level", alert.Level)
            .Set("message", alert.Message);
        return $"event: system.log\ndata: {WireJsonWriter.Dumps(data, WireJsonFormat.Compact)}\n\n";
    }
}
