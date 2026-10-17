using Weir.Core.Jobs;
using Weir.Core.LibraryMode;
using Weir.Core.Processing;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Jobs;

/// <summary>
/// Which worker lane runs a job kind (#717). File work runs on the slots "Files at once" gates and counts toward each
/// library's own limit. Upkeep (scans and the maintenance sweeps) runs on slots of its own and counts toward neither, so a
/// scan never waits behind a library's hours-long pass, and a pass never waits behind a scan.
/// </summary>
public static class WorkLanes
{
    /// <summary>
    /// Upkeep slots beside the file slots. Two let one library's scan run while another library's scan or a sweep does; a
    /// library itself is never scanned twice at once (<see cref="WorkAdmission.UpkeepBlockedLibraryIds"/>).
    /// </summary>
    public const int UpkeepSlots = 2;

    /// <summary>The heartbeat module the upkeep slots report under, beside the file slots' <see cref="ProcessingWorkerService.HeartbeatModule"/>.</summary>
    public const string UpkeepHeartbeatModule = "upkeep";

    /// <summary>The job kinds that look after libraries rather than clean a file. Every other kind is file work.</summary>
    public static readonly IReadOnlyList<string> UpkeepKinds =
    [
        ProcessingWatchedFolderScanDispatchJobKinds.ScanDispatch,
        LibraryModeJobKinds.ScanKind,
        PeriodicJobKinds.WorkTempStaleSweep,
        PeriodicJobKinds.UnclaimedHandbackCleanup,
        DownloadedScanRetryHandler.Kind,
    ];

    public static WorkLane LaneOf(string jobKind) => UpkeepKinds.Contains(jobKind, StringComparer.Ordinal) ? WorkLane.Upkeep : WorkLane.Files;

    /// <summary>
    /// What the file-lane workers claim: every registered kind except upkeep, plus refused kinds, so an upkeep row is
    /// always left for the upkeep lane and never gated by the files-at-once limit it is exempt from.
    /// </summary>
    public static ClaimableKinds FilesLaneKinds(JobHandlerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ClaimableKinds([.. registry.JobKinds.Where(kind => LaneOf(kind) == WorkLane.Files)], IncludeRefusedKinds: true);
    }

    /// <summary>What the upkeep-lane workers claim: only the registered upkeep kinds, never a kind without a handler.</summary>
    public static ClaimableKinds UpkeepLaneKinds(JobHandlerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ClaimableKinds([.. registry.JobKinds.Where(kind => LaneOf(kind) == WorkLane.Upkeep)], IncludeRefusedKinds: false);
    }
}
