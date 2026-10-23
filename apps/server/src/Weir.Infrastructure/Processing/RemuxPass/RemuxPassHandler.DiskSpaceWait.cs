using Weir.Core.Json;
using Weir.Core.Processing.RemuxPass;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassHandler
{
    /// <summary>
    /// A file that could not start because a drive is short of the space to keep free is looked at again later and stays
    /// waiting until then, never recorded as done. Booked before the file's state is written, so its hold ends when the look is due.
    /// </summary>
    internal async Task DeferUntilDiskSpaceAsync(long jobId, WireObject data, WireObject? origin, WireObject result, CancellationToken cancellationToken)
    {
        if (_jobs is null || result.Get("not_ready_kind") is not WireString { Value: RemuxPassRunner.DiskSpaceWait })
        {
            return;
        }

        var looks = data.Get("disk_space_looks") is WireInteger counted ? (long)counted.Value : 0;
        await BookAnotherLookAsync(
            _jobs,
            data.Copy().Set("disk_space_looks", looks + 1),
            origin,
            $"{RemuxPassOutcomes.JobKind}:disk-space-wait:{jobId}",
            _time.GetUtcNow() + DiskSpaceWaits.LookAfter(looks),
            result,
            cancellationToken).ConfigureAwait(false);
    }
}
