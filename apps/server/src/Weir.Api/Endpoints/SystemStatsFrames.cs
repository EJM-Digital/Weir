using Weir.Core.Json;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>system.stats</c> side of the Activity stream: each reading the sampler takes goes out as one frame, so the System
/// view's traces move once a second while a browser is watching. A stream gets the newest reading as soon as it opens.
/// Pacing is the sampler's, which reads once a second while any stream is open; nothing here polls. Each frame also carries the
/// machine's facts and the drives, which the sampler reads more slowly, so they follow the stream too.
/// </summary>
public sealed class SystemStatsFrames
{
    private readonly SystemStatsStore _store;

    public SystemStatsFrames(SystemStatsStore store) => _store = store;

    /// <summary>The frames for one open stream, from when it opens until it ends.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long seen = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var update = await _store.NextAfterAsync(seen, cancellationToken).ConfigureAwait(false);
            seen = update.Version;
            yield return Frame(update);
        }
    }

    /// <summary>The <c>system.stats</c> SSE frame: the readings of the moment, the point to add to the traces, and the machine's facts and drives as they stand.</summary>
    public static string Frame(StatsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var data = new WireObject()
            .Set("now", SystemStatsWire.Now(update.Sample.Now))
            .Set("point", SystemStatsWire.Point(update.Sample.Point))
            .Set("machine", SystemStatsWire.Machine(update.Machine))
            .Set("drives", SystemStatsWire.Drives(update.Drives));
        return $"event: system.stats\ndata: {WireJsonWriter.Dumps(data, WireJsonFormat.Compact)}\n\n";
    }
}
