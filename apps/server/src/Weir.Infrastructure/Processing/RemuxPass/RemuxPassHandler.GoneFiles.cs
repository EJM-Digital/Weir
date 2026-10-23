using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    private const string GoneLooksKey = GoneLooks.Key;

    /// <summary>Whether the pass found its source gone.</summary>
    internal static bool IsSourceGone(WireObject result) => result.Get("outcome") is WireString { Value: RemuxPassOutcomes.SourceGone };

    /// <summary>How many looks a file has had after it was found gone; none when nothing has counted them.</summary>
    internal static long GoneLooksIn(WireObject payloadOrResult) => payloadOrResult.Get(GoneLooksKey) is WireInteger looks ? (long)looks.Value : 0;

    /// <summary>A text the object holds under <paramref name="key"/>, or <paramref name="fallback"/> when it holds none.</summary>
    internal static string TextIn(WireObject result, string key, string fallback) => result.Get(key) is WireString text ? text.Value : fallback;

    /// <summary>
    /// A pass that failed because its file was not there is looked at again after a few seconds, the way a hand-off is told a share
    /// that dropped for a moment is not a deleted download. A file that is back is processed normally; one that is still gone settles
    /// as nothing to do, and is held (<see cref="LookAgainForGoneFileAsync"/>) before Weir forgets it.
    /// </summary>
    private async Task<WireObject> SettleVanishedAsync(RemuxPassRequest request, WireObject result, CancellationToken cancellationToken)
    {
        var watchedFolder = request.Runtime.WatchedFolder;
        if (result.Get("ok") is not WireBool { Value: false } || !GoneSources.HasLeft(watchedFolder, request.RelativeMediaPath))
        {
            return result;
        }

        await (GoneLookAgain?.Invoke(cancellationToken) ?? Task.Delay(GoneSettle, cancellationToken)).ConfigureAwait(false);
        if (!GoneSources.HasLeft(watchedFolder, request.RelativeMediaPath))
        {
            return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (GoneConfirmed is { } confirmed)
        {
            await confirmed().ConfigureAwait(false);
        }

        return RemuxPassRunner.SourceGone(request.RelativeMediaPath, TextIn(result, "inspected_source_path", string.Empty));
    }

    /// <summary>
    /// Whether this look adds an entry to Activity. A file is recorded as gone once: not by the look that forgets it, which is the
    /// second, and not by a look at a file that was already held as gone when the pass claimed it, which something else recorded.
    /// The manager that handed the file over is still told at the final look.
    /// </summary>
    internal static bool AddsGoneEntry(WireObject result, bool listedWhenClaimed)
    {
        if (!IsSourceGone(result))
        {
            return true;
        }

        return !listedWhenClaimed && GoneLooksIn(result) <= 0;
    }

    /// <summary>
    /// The first time a file is found gone it is held, not forgotten: its attempts, reason and any hand-picked plan stay until it is
    /// plain that it is not coming back. A look is booked for after the scan's own grace for a vanished file
    /// (<see cref="GoneSources.LookAgainAfter"/>). That look finds the file back, and processes it, or still gone, and forgets it and
    /// tells the manager that handed it over. The first look is not final, so nothing is reported yet.
    /// </summary>
    private async Task LookAgainForGoneFileAsync(
        long jobId, WireObject data, WireObject? origin, WireObject result, (string WatchedFolder, string RelativePath, string MediaScope, long LibraryId) file, CancellationToken cancellationToken)
    {
        if (_jobs is not { } jobs || !IsSourceGone(result) || GoneLooksIn(result) != 0)
        {
            return;
        }

        await BookAnotherLookAsync(
            jobs,
            data.Copy().Set(GoneLooksKey, 1),
            origin,
            $"{RemuxPassOutcomes.JobKind}:gone-wait:{jobId}",
            _time.GetUtcNow() + GoneSources.LookAgainAfter,
            result,
            cancellationToken).ConfigureAwait(false);

        // The file can come back, and a send for it arrive, in the time it took to get here; nothing would start the look until the sweep.
        if (GoneSources.IsBack(file.WatchedFolder, file.RelativePath))
        {
            await LockedWrites.RunAsync(
                _database,
                uow => GoneLooks.StartForFileAsync(uow, file.RelativePath, file.MediaScope, file.LibraryId, jobs.WakeWorkers),
                _logger,
                "start the look at a file that is back",
                cancellationToken).ConfigureAwait(false);
        }
    }
}
