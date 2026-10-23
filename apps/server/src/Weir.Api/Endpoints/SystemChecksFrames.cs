using Weir.Core.Json;
using Weir.Core.Time;
using Weir.Infrastructure.Activity;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>system.checks</c> side of the Activity stream: when the server last checked the workflows' folders and whether Weir is
/// ready, sent when a stream opens and each time one of those looks happens, so Health can say when a row was last checked
/// by the server. Looks that come close together collapse into one frame carrying the newest times.
/// </summary>
public sealed class SystemChecksFrames
{
    private readonly ServerLooks _looks;

    public SystemChecksFrames(ServerLooks looks) => _looks = looks;

    /// <summary>The frames for one open stream, from when it opens until it ends.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = _looks.Subscribe();
        if (_looks.Latest is { Folders: not null } or { Readiness: not null })
        {
            yield return Frame(_looks.Latest);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
            yield return Frame(_looks.Latest);
        }
    }

    /// <summary>The <c>system.checks</c> SSE frame: when each look last happened, or null for one that has not.</summary>
    public static string Frame(ServerLookTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        var data = new WireObject()
            .Set("folders_checked_at", Wire(times.Folders))
            .Set("readiness_checked_at", Wire(times.Readiness));
        return $"event: system.checks\ndata: {WireJsonWriter.Dumps(data, WireJsonFormat.Compact)}\n\n";
    }

    private static WireValue Wire(DateTimeOffset? at) =>
        at is { } when ? new WireString(Timestamp.FromDateTimeOffset(when.ToUniversalTime()).ToWireText()) : WireValue.Null;
}
