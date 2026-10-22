using Weir.Core.Json;
using Weir.Infrastructure.Activity;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>server.hello</c> and <c>data.changed</c> side of the Activity stream. The hello leads, carrying the id of this run
/// of the server so a reconnecting page can tell a restart from a dropped connection. A <c>data.changed</c> frame follows each
/// time a component publishes a <see cref="DataTopics"/> name on <see cref="DataChangePublisher"/>, so the screens showing that
/// data read it again without polling.
/// </summary>
public sealed class LiveStreamFrames
{
    private readonly DataChangePublisher _changes;
    private readonly ServerBoot _boot;

    public LiveStreamFrames(DataChangePublisher changes, ServerBoot boot)
    {
        _changes = changes;
        _boot = boot;
    }

    /// <summary>The frames for one open stream, from when it opens until it ends.</summary>
    public async IAsyncEnumerable<string> ForAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Listening before the hello goes out, so a change published while it is being written still reaches this stream.
        using var subscription = _changes.Subscribe();
        yield return HelloFrame(_boot.Id);
        await foreach (var topic in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ChangedFrame(topic);
        }
    }

    /// <summary>The <c>server.hello</c> SSE frame: which run of the server this stream is talking to.</summary>
    public static string HelloFrame(string bootId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootId);
        return $"event: server.hello\ndata: {WireJsonWriter.Dumps(new WireObject().Set("boot_id", bootId), WireJsonFormat.Compact)}\n\n";
    }

    /// <summary>The <c>data.changed</c> SSE frame: the data named by <paramref name="topic"/> changed.</summary>
    public static string ChangedFrame(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return $"event: data.changed\ndata: {WireJsonWriter.Dumps(new WireObject().Set("topic", topic), WireJsonFormat.Compact)}\n\n";
    }
}
