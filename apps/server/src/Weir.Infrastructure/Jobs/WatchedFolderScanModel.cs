using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.IO;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Jobs;

/// <summary>Everything a watched-folder scan knows before it looks at its first file.</summary>
internal sealed record WatchedFolderScan(
    ProcessingLibraryRecord Library,
    string MediaScope,
    WatchedFolderScanOps.ProcessingScanPathRuntime Paths,
    LibraryAdmissionRules Rules,
    IReadOnlyList<ManagerQueueSignal> Signals,
    ScanAdmissionWindow Window,
    bool EnqueueRemuxJobs,
    DateTimeOffset Now,
    WorkflowManagerLinks Links,
    IFileSystem Files)
{
    /// <summary>The source stays after cleaning: the workflow says so, or a linked media manager and its download client own it.</summary>
    public bool KeepsOriginals => !Library.RemoveOriginalAfterSuccess || Links.KeepsOriginals;

    public bool IsMovieScope => MediaScope == ProcessingMediaScopes.Movie;
}

/// <summary>Whether the library may start work now, and if not, why and until when.</summary>
internal sealed record ScanAdmissionWindow(bool InWindow, string? PauseReason, DateTimeOffset? PauseUntil, DateTimeOffset? ReopensAt)
{
    /// <summary>Processing is paused: the scan may look, record and queue, but never changes a media file.</summary>
    public bool Paused => PauseReason is not null;
}

/// <summary>
/// What a scan reads from the database once, before it looks at any file (#708): the library's file rows, the files with a
/// pass queued or running, and the passes booked for later. Passes this scan queues are added as it goes.
/// </summary>
internal sealed record WatchedFolderScanLookups(
    IReadOnlyDictionary<string, ProcessingFileRecord> Rows,
    HashSet<string> ActivePasses,
    IReadOnlyDictionary<string, DateTimeOffset> HeldBackPasses,
    IReadOnlyDictionary<string, FileSkipMarker> SkipMarkers)
{
    public static async Task<WatchedFolderScanLookups> ReadAsync(UnitOfWork reads, FileStateStore files, FileSkipMarkerStore skipMarkers, WatchedFolderScan scan)
    {
        var rows = await files.ListForLibraryAsync(reads, scan.Library.Id).ConfigureAwait(false);
        return new WatchedFolderScanLookups(
            rows.ToDictionary(row => row.RelativePath, StringComparer.Ordinal),
            await ActiveRemuxPasses.PathsAsync(reads, scan.MediaScope, scan.Library.Id).ConfigureAwait(false),
            await ActiveRemuxPasses.HeldBackStartsAsync(reads, scan.MediaScope, scan.Library.Id, scan.Now).ConfigureAwait(false),
            await skipMarkers.ForLibraryAsync(reads, scan.Library.Id).ConfigureAwait(false));
    }
}

/// <summary>A rejected file this library is set to delete, removed only after its decision is recorded.</summary>
internal sealed record RejectedFileRemoval(string RelativePath, string FilePath, string Reason, string Action, string? SkipKind);

/// <summary>What a scan does about one file.</summary>
internal sealed record WatchedFileDecision
{
    public required string RelativePath { get; init; }

    /// <summary>A row to mark seen, with nothing else about it changed.</summary>
    public long? TouchRowId { get; init; }

    /// <summary>The state to record for the file; when <see cref="FinishMovieRemoval"/> is set, what to record if it cannot run.</summary>
    public ScannedFileWrite? Write { get; init; }

    /// <summary>Queue a pass for the file once <see cref="Write"/>, if any, has been applied.</summary>
    public bool Enqueue { get; init; }

    /// <summary>The row a queued pass is costed from.</summary>
    public ProcessingFileRecord? Previous { get; init; }

    /// <summary>The file is cleaned and only removing its original download is left to do (movies whose library removes originals).</summary>
    public bool FinishMovieRemoval { get; init; }

    public SettlingObservation? Settling { get; init; }

    public RejectedFileRemoval? Removal { get; init; }

    /// <summary>When the scan next needs to look at this file: when a hold it put on the file ends, or when a failed file's retry falls due.</summary>
    public DateTimeOffset? HoldEnds { get; init; }
}
