using Weir.Core.Json;

namespace Weir.Infrastructure.Processing.RemuxPass;

public sealed partial class RemuxPassRunner
{
    /// <summary><c>not_ready_kind</c> of a file that cannot start because a drive is short of the space to keep free.</summary>
    public const string DiskSpaceWait = "low_disk_space";

    private const string OutputDrive = "output drive";
    private const string WorkFolderDrive = "work folder's drive";

    /// <summary>What a wait for room reports about the pass so far, so Activity shows the same file details as any other result.</summary>
    private static readonly string[] PassSummaryKeys =
        ["stream_counts", "plan_summary", "audio_before", "audio_after", "subs_before", "subs_after", "remux_required"];

    /// <summary>
    /// Whether the output drive has the space to keep free, before the file is read at all: a full drive must cost a look at the
    /// drive, not a read through the whole file. Null when there is room, or when the check is off.
    /// </summary>
    private WireObject? WaitIfOutputDriveIsShort(RemuxPassRequest request, string src, string inspected, string scope)
    {
        var runtime = request.Runtime;
        // The source was resolved under the watched folder by ResolveMediaFileUnderRoot, which refuses a path outside it.
        var relative = RemuxPassPaths.RelativeTo(src, RemuxPassPaths.Resolve(runtime.WatchedFolder))!;
        var target = Path.Join(RemuxPassPaths.Resolve(runtime.OutputFolder), relative);
        var disk = FileLifecycle.CheckMinimumFreeDiskSpace(target, request.MinimumFreeDiskSpaceMb, FreeBytes);
        return disk.Ok ? null : WaitForDiskSpace(disk, OutputDrive, request.RelativeMediaPath, inspected, scope, new WireObject());
    }

    /// <summary>The wait for a work folder's drive, after the plan showed the file needs a new copy written there.</summary>
    private static WireObject WorkFolderDriveWait(DiskSpaceCheck disk, PassContext context, WireObject output) =>
        WaitForDiskSpace(disk, WorkFolderDrive, context.RelativeMediaPath, context.Inspected, context.Scope, PassSummary(output));

    /// <summary>The wait for the output drive, once the file is written and the drive filled up in the meantime.</summary>
    private static WireObject OutputDriveWait(DiskSpaceCheck disk, PassContext context, WireObject output) =>
        WaitForDiskSpace(
            disk, OutputDrive, context.RelativeMediaPath, context.Inspected, context.Scope,
            PassSummary(output).Set("processing_output_folder_resolved", context.OutputDirectory));

    /// <summary>A retryable wait, never a finished pass: a full disk must not mark a file done.</summary>
    private static WireObject WaitForDiskSpace(DiskSpaceCheck disk, string drive, string relativeMediaPath, string inspected, string scope, WireObject details)
    {
        var wait = SourceNotReady(relativeMediaPath, DiskSpaceWaits.Reason(drive, disk.RequiredMb, disk.FreeMb), inspected)
            .Set("not_ready_kind", DiskSpaceWait)
            .Set("guardrail", "minimum_free_disk_space")
            .Set("disk_checked_path", disk.CheckedPath)
            .Set("disk_free_mb", Math.Round(disk.FreeMb, 1, MidpointRounding.ToEven))
            .Set("minimum_free_disk_space_mb", disk.RequiredMb)
            .Set("media_scope", scope);
        foreach (var (key, value) in details.Items)
        {
            wait.Set(key, value);
        }

        return wait;
    }

    private static WireObject PassSummary(WireObject output)
    {
        var summary = new WireObject();
        foreach (var key in PassSummaryKeys)
        {
            summary.Set(key, output[key]);
        }

        return summary;
    }
}
