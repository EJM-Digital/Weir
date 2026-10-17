using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Weir.Api.Http;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.MediaManagers;
using Weir.Core.Text;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>
/// Request/response shaping shared by the Library mode endpoint files (<see cref="LibraryModeEndpoints"/>,
/// <see cref="LibraryModeOverviewEndpoints"/>, <see cref="LibraryModeFilesEndpoints"/>,
/// <see cref="LibraryModeScheduleEndpoints"/> and <see cref="LibraryModeRedownloadsEndpoints"/>).
/// </summary>
internal static class LibraryModeMapping
{
    /// <summary>The 404 detail for these routes, kept as it is because clients may match on it.</summary>
    internal const string NoLibraryWithThatId = "No library with that id.";

    internal static ILogger Logger(ApiRequest request) => request.LoggerFactory.CreateLogger("weir.library_mode.router");

    internal static WireObject SettingsOut(LibrarySettings settings) => new WireObject()
        .Set("library_folders", new WireArray(settings.Folders.Select(f => (WireValue)new WireString(f))))
        .Set("library_schedule_enabled", settings.ScheduleEnabled)
        .Set("clean_hardlinked_files", settings.CleanHardlinkedFiles)
        .Set("skip_if_manager_would_redownload", settings.SkipIfManagerWouldRedownload)
        .Set("keep_original_after_clean", settings.KeepOriginalAfterClean)
        .Set("originals_folder", settings.OriginalsFolder)
        .Set("library_rule_set_id", settings.RuleSetId);

    /// <summary>
    /// The scan's own state for the header: which job, what it is doing, when it last finished and anything it
    /// could not do. <c>running</c> is what the "Scan now" button and its progress read; <c>files_seen</c> is how far the walk
    /// of a running scan has got.
    /// </summary>
    internal static async Task<WireValue> ScanOutAsync(UnitOfWork uow, LibraryScanStore scans, long libraryId, ILogger logger)
    {
        var latest = await scans.LatestAsync(uow, libraryId).ConfigureAwait(false);
        var outcome = await scans.OutcomeAsync(uow, libraryId, latest, logger).ConfigureAwait(false);
        if (latest is null)
        {
            return outcome is null
                ? WireValue.Null
                : new WireObject().Set("job_id", WireValue.Null).Set("status", "completed").Set("running", false)
                    .Set("generated_at", outcome.GeneratedAt.ToUnixTimeSeconds()).Set("files_seen", WireValue.Null).Set("errors", new WireArray([]));
        }

        var running = latest.Status is "pending" or "leased";
        return new WireObject()
            .Set("job_id", latest.JobId)
            .Set("status", latest.Status)
            .Set("running", running)
            .Set("files_seen", running ? scans.FilesSeen(latest.JobId) : null)
            .Set("generated_at", outcome?.GeneratedAt.ToUnixTimeSeconds())
            .Set("errors", new WireArray((outcome?.Errors ?? []).Select(e => (WireValue)new WireString(e))));
    }

    internal static WireObject TotalsOut(LibraryTotals totals) => new WireObject()
        .Set("files", totals.Files)
        .Set("size_bytes", totals.SizeBytes)
        .Set("matches", totals.Matches)
        .Set("would_change", totals.WouldChange)
        .Set("cannot_process", totals.CannotProcess)
        .Set("estimated_bytes_saved", totals.EstimatedBytesSaved)
        .Set("total_removed_audio_tracks", totals.RemovedAudioTracks)
        .Set("total_removed_subtitle_tracks", totals.RemovedSubtitleTracks)
        .Set("cleaned", totals.Cleaned)
        .Set("left_alone", totals.LeftAlone)
        .Set("by_status", new WireObject()
            .Set(LibraryFileStatus.NeedsCleaning, totals.ByStatus.NeedsCleaning)
            .Set(LibraryFileStatus.Cleaning, totals.ByStatus.Cleaning)
            .Set(LibraryFileStatus.Matches, totals.ByStatus.Matches)
            .Set(LibraryFileStatus.CantCleanYet, totals.ByStatus.CantCleanYet)
            .Set(LibraryFileStatus.LeftAlone, totals.ByStatus.LeftAlone));

    /// <summary>The final-removal confirmation numbers for a set of files (#505 point 5), shared by Clean and the schedule toggle.</summary>
    internal static (int Files, int Tracks, long BytesSaved) RemovalTotals(IEnumerable<LibraryScanFileEntry> files)
    {
        var removing = files.Where(f => f.Classification == LibraryFileClassification.WouldChange && f.RemovedAudioCount + f.RemovedSubtitleCount > 0).ToList();
        return (removing.Count, removing.Sum(f => f.RemovedAudioCount + f.RemovedSubtitleCount), removing.Sum(f => f.EstimatedBytesSaved));
    }

    /// <summary>
    /// The 400 the web renders as the #505 point 5 confirmation dialog. <c>detail</c> is the exact required sentence; the web
    /// combines it with <c>estimated_bytes_saved</c> for "the size saved" and re-sends the same request with
    /// <c>confirm_final_removal: true</c> once the user agrees. <paramref name="warnings"/> is #508's per-file preflight
    /// notes (seeding, re-download risk) — shown alongside the removal count, whether or not they caused a file to be
    /// skipped outright.
    /// </summary>
    internal static JsonApiResult ConfirmationRequired(int files, int tracks, long bytesSaved, IReadOnlyList<string> warnings) => new(
        StatusCodes.Status400BadRequest,
        new WireObject()
            .Set("error", "confirm_final_removal_required")
            .Set("detail", $"{Plural.Of(files, "file")}, {Plural.Of(tracks, "track")} will be removed. Removed tracks are gone for good; getting one back means downloading the title again.")
            .Set("files_count", files)
            .Set("tracks_count", tracks)
            .Set("estimated_bytes_saved", bytesSaved)
            .Set("warnings", new WireArray(warnings.Select(w => (WireValue)new WireString(w)))));

    /// <summary>Every distinct manager connection a set of scanned files was matched to, resolved once for a preflight pass.</summary>
    internal static async Task<Dictionary<long, ManagerConnection>> ConnectionsForFilesAsync(
        UnitOfWork uow, MediaManagerConnectionService connections, IEnumerable<LibraryScanFileEntry> files)
    {
        var ids = files.Select(f => f.ManagerConnectionId).OfType<long>().Distinct().ToList();
        var resolved = await connections.ConnectionsByIdAsync(uow, ids).ConfigureAwait(false);
        return resolved.Where(c => c.ConnectionId is not null).ToDictionary(c => c.ConnectionId!.Value);
    }
}
