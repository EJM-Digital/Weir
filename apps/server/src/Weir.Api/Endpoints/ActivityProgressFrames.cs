using Weir.Core.Json;
using Weir.Infrastructure.Processing;

namespace Weir.Api.Endpoints;

/// <summary>
/// The <c>processing.progress</c> side of the Activity stream (#750): builds and throttles the frame carrying
/// every file's live progress, sharing one <see cref="LiveProgressStore"/> change signal across every open
/// client instead of any of them polling. <see cref="ActivityEndpoints"/> maps the route and wires this in
/// alongside <c>activity.latest</c>; kept in its own file so that one stays a reasonable size.
/// </summary>
public sealed class ActivityProgressFrames
{
    /// <summary>The most often the live-progress frame goes out, however fast a pass reports.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    /// <summary>How long the stream waits for a change before it just loops and checks cancellation.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(2);

    private readonly LiveProgressStore _liveProgress;

    public ActivityProgressFrames(LiveProgressStore liveProgress) => _liveProgress = liveProgress;

    /// <summary>Frames the live-progress store for one open stream, using its own <see cref="Throttle"/> and idle-poll window.</summary>
    public IAsyncEnumerable<string> ForAsync(TimeProvider time, CancellationToken cancellationToken) =>
        FramesAsync(() => _liveProgress.Version, _liveProgress.WaitForChangeAsync, _liveProgress.Snapshot, time, Throttle, IdlePoll, cancellationToken);

    /// <summary>
    /// One <c>processing.progress</c> frame at most every <paramref name="throttle"/>, carrying every file's current
    /// live progress. Every open stream calls <paramref name="waitForChange"/> on the same shared store, so one
    /// process-wide change wakes every client at once and nothing here polls on a timer.
    /// </summary>
    public static async IAsyncEnumerable<string> FramesAsync(
        Func<long> currentVersion,
        Func<long, TimeSpan, TimeProvider, CancellationToken, Task<long?>> waitForChange,
        Func<IReadOnlyDictionary<string, LiveProgress>> snapshot,
        TimeProvider time,
        TimeSpan throttle,
        TimeSpan idlePoll,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(waitForChange);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(time);

        var version = currentVersion();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await waitForChange(version, idlePoll, time, cancellationToken).ConfigureAwait(false) is null)
            {
                // Nothing changed within the idle window; the activity side of the stream keeps the connection alive.
                continue;
            }

            if (throttle > TimeSpan.Zero)
            {
                // Coalesces a burst of reports into the one frame sent after the throttle window.
                await Task.Delay(throttle, time, cancellationToken).ConfigureAwait(false);
            }

            version = currentVersion();
            yield return Frame(snapshot());
        }
    }

    /// <summary>The <c>processing.progress</c> SSE frame: every file with live progress, keyed by its relative path.</summary>
    private static string Frame(IReadOnlyDictionary<string, LiveProgress> files)
    {
        var entries = files.Select(pair => (WireValue)new WireObject()
            .Set("relative_path", pair.Key)
            .Set("status", pair.Value.Status)
            .Set("stage", pair.Value.Stage)
            .Set("percent", pair.Value.Percent is { } percent ? WireValue.Of(percent) : WireValue.Null)
            .Set("eta_seconds", pair.Value.EtaSeconds is { } eta ? WireValue.Of(eta) : WireValue.Null)
            .Set("message", pair.Value.Message)
            .Set("speed", pair.Value.Speed)
            .Set("elapsed_seconds", pair.Value.ElapsedSeconds is { } elapsed ? WireValue.Of(elapsed) : WireValue.Null)
            .Set("removed_audio", new WireArray(pair.Value.RemovedAudio.Select(t => (WireValue)WireValue.Of(t))))
            .Set("removed_subtitles", new WireArray(pair.Value.RemovedSubtitles.Select(t => (WireValue)WireValue.Of(t)))));
        var json = WireJsonWriter.Dumps(new WireObject().Set("files", new WireArray(entries)), WireJsonFormat.Compact);
        return $"event: processing.progress\ndata: {json}\n\n";
    }
}
