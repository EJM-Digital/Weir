using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.LibraryMode;
using Weir.Core.Media;
using Weir.Core.Processing;
using Weir.Core.Rules;
using Weir.Core.Validation;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using static Weir.Api.Endpoints.EndpointLookups;

namespace Weir.Api.Endpoints;

/// <summary>
/// Library mode (#505, #568): the Files table, Clean (with the #505 point 5 final-removal confirmation and #508's
/// preflight), and "leave this file alone". The scheduled scan/clean toggle, which shares Clean's confirmation
/// helpers via <see cref="LibraryModeMapping"/>, lives in <see cref="LibraryModeScheduleEndpoints"/>. See
/// <see cref="LibraryModeEndpoints"/> for the rest of the Library mode surface.
/// </summary>
public static class LibraryModeFilesEndpoints
{
    public static IEndpointRouteBuilder MapLibraryModeFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<LibraryModeFilesEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/libraries/{library_id}/library-files", handlers.GetFilesAsync);
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/library-files/clean", handlers.PostCleanAsync);
        endpoints.MapV1("POST", "/processing/libraries/{library_id}/library-files/leave-alone", handlers.PostLeaveAloneAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="LibraryModeFilesEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class LibraryModeFilesEndpointHandlers
{
    private readonly LibraryScanStore _scans;
    private readonly LibraryFileMarksStore _fileMarks;
    private readonly LibrarySettingsStore _librarySettings;
    private readonly LibraryViewStore _libraryView;
    private readonly MediaManagerConnectionService _connections;
    private readonly IHardlinkInspector _hardlinkInspector;
    private readonly RedownloadRiskChecker _riskChecker;
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;
    private readonly ArtworkPosterUrls _posters;

    public LibraryModeFilesEndpointHandlers(
        LibraryScanStore scans,
        LibraryFileMarksStore fileMarks,
        LibrarySettingsStore librarySettings,
        LibraryViewStore libraryView,
        MediaManagerConnectionService connections,
        IHardlinkInspector hardlinkInspector,
        RedownloadRiskChecker riskChecker,
        ProcessingJobStore jobs,
        LibraryStore libraries,
        ArtworkPosterUrls posters)
    {
        _scans = scans ?? throw new ArgumentNullException(nameof(scans));
        _fileMarks = fileMarks ?? throw new ArgumentNullException(nameof(fileMarks));
        _librarySettings = librarySettings ?? throw new ArgumentNullException(nameof(librarySettings));
        _libraryView = libraryView ?? throw new ArgumentNullException(nameof(libraryView));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _hardlinkInspector = hardlinkInspector ?? throw new ArgumentNullException(nameof(hardlinkInspector));
        _riskChecker = riskChecker ?? throw new ArgumentNullException(nameof(riskChecker));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _posters = posters ?? throw new ArgumentNullException(nameof(posters));
    }

    private static WireObject FileOut(LibraryFileRow row, string? posterUrl) => new WireObject()
        .Set("path", row.Path)
        .Set("poster_url", posterUrl)
        .Set("size_bytes", row.SizeBytes)
        .Set("modified_at", row.ModifiedTimeUnixSeconds)
        .Set("classification", LibraryScanFileEntry.ClassificationName(row.Classification))
        .Set("summary", row.Summary)
        .Set("reason", row.Reason)
        .Set("removed_audio_tracks", row.RemovedAudioCount)
        .Set("removed_subtitle_tracks", row.RemovedSubtitleCount)
        .Set("estimated_bytes_saved", row.EstimatedBytesSaved)
        .Set("manager_kind", row.ManagerKind)
        .Set("manager_title", row.ManagerTitle)
        .Set("video_codec", row.VideoCodec)
        .Set("video_height", row.VideoHeight)
        .Set("resolution_class", row.ResolutionClass)
        .Set("audio_track_count", row.AudioTrackCount)
        .Set("subtitle_track_count", row.SubtitleTrackCount)
        .Set("audio_summary", row.AudioSummary)
        .Set("subtitle_summary", row.SubtitleSummary)
        .Set("link_count", row.LinkCount)
        .Set("problem_kind", row.ProblemKind is { } kind ? LibraryProblems.Name(kind) : null)
        .Set("cleaned_at", row.CleanedAt is { } cleaned ? cleaned.ToUnixTimeSeconds() : null)
        .Set("leave_alone", row.LeaveAlone)
        .Set("status", row.Status)
        .Set("status_reason", row.Status == LibraryFileStatus.NeedsCleaning ? row.ChangeReason : null);

    /// <summary>Reads the Files table's filters, sort and page off the query string.</summary>
    private static LibraryFileQuery QueryFrom(ApiRequest request, bool cleansHardlinkedFiles)
    {
        var facets = new List<LibraryFileFacet>();
        foreach (var facet in LibraryFacets.All)
        {
            if (request.Query(facet) is { Length: > 0 } value)
            {
                facets.Add(new LibraryFileFacet(facet, value));
            }
        }

        return new LibraryFileQuery
        {
            Classification = NullIfBlank(request.Query("classification")),
            ManagerKind = NullIfBlank(request.Query("manager")),
            Search = NullIfBlank(request.Query("q")),
            Facets = facets,
            ProblemKind = LibraryProblems.Parse(request.Query("problem")),
            State = StateFilter(request.Query("state")),
            Status = StatusFilter(request.Query("status")),
            CleansHardlinkedFiles = cleansHardlinkedFiles,
            Sort = LibraryFileSort.Normalize(request.Query("sort")),
            Descending = string.Equals(request.Query("direction"), "desc", StringComparison.OrdinalIgnoreCase),
            Page = PositiveInt(request.Query("page"), 1),
            PageSize = Math.Clamp(PositiveInt(request.Query("page_size"), LibraryFileSort.DefaultPageSize), 1, LibraryFileSort.MaxPageSize),
        };
    }

    /// <summary>What Weir has done with a file, as a filter: anything else narrows nothing.</summary>
    private static string? StateFilter(string? value) => value is "cleaned" or "left_alone" ? value : null;

    /// <summary>Where each file stands now, as a filter: anything else narrows nothing.</summary>
    private static string? StatusFilter(string? value) => LibraryFileStatus.IsKnown(value) ? value : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int PositiveInt(string? value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    /// <summary>
    /// #568's Files table: one page of the library's files, sorted by any of
    /// <see cref="LibraryFileSort.Columns"/> and narrowed by classification, manager, a path/title search, any of
    /// the breakdown facets, or a Problems group. <c>summary</c> is the whole library's totals (what the header
    /// says), <c>filtered</c> the totals for what the filters select, and <c>total</c> the row count behind the
    /// paging.
    /// </summary>
    public async Task<ApiResult> GetFilesAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);

        var settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
        var query = QueryFrom(request, settings.CleanHardlinkedFiles);
        var overall = await _libraryView.TotalsAsync(uow, libraryId, new LibraryFileQuery { CleansHardlinkedFiles = settings.CleanHardlinkedFiles }).ConfigureAwait(false);
        var filtered = await _libraryView.TotalsAsync(uow, libraryId, query).ConfigureAwait(false);
        var rows = await _libraryView.ListFilesAsync(uow, libraryId, query).ConfigureAwait(false);
        var posters = await _posters.ForFilesAsync(uow, rows.Select(row => (libraryId, row.Path))).ConfigureAwait(false);

        return ApiRoutes.Ok(new WireObject()
            .Set("library_id", libraryId)
            .Set("scan", await LibraryModeMapping.ScanOutAsync(uow, _scans, libraryId, LibraryModeMapping.Logger(request)).ConfigureAwait(false))
            .Set("summary", LibraryModeMapping.TotalsOut(overall))
            .Set("filtered", LibraryModeMapping.TotalsOut(filtered))
            .Set("files", new WireArray(rows.Select(row => (WireValue)FileOut(row, posters.GetValueOrDefault((libraryId, row.Path))))))
            .Set("total", filtered.Files)
            .Set("page", query.Page)
            .Set("page_size", query.PageSize)
            .Set("sort", query.Sort)
            .Set("direction", query.Descending ? "desc" : "asc"));
    }

    /// <summary>What one person's chosen plan would take out of the one file they chose it for.</summary>
    private sealed record ManualCleanChoice(int Tracks, long BytesSaved);

    /// <summary>
    /// Checks a track choice against the tracks the scan already read from the file, so a choice that cannot apply is
    /// refused here rather than becoming a job that fails later, and so the confirmation dialog quotes the chosen
    /// plan. The clean job checks again when it runs, against a fresh read: this is about answering the request well,
    /// not about deciding whether the file is safe to touch.
    /// </summary>
    private static ManualCleanChoice ManualChoiceFor(WireObject manualPlan, List<LibraryScanFileEntry> selected)
    {
        if (selected.Count != 1)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Choosing tracks yourself applies to one file at a time.");
        }

        if (ManualPlanJson.FromPyJson(manualPlan) is not { } choice)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "'manual_plan' must be an object with 'keep' (a list of {index, default, forced}) and 'order' (a list of track indices).");
        }

        var entry = selected[0];
        if (entry.ProbeJson is not { Length: > 0 } probeJson)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "Weir has not read this file's tracks yet. Scan the library again, then choose.");
        }

        SplitProbeStreams streams;
        ProbeResult probe;
        try
        {
            probe = ProbeResult.Parse(probeJson);
            streams = RemuxRules.SplitStreams(probe);
        }
        catch (RulesInputException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ToolFailureText.UnusableTrackData);
        }

        if (!ManualTrackPlan.TryValidate(choice, ManualTrackPlan.ClassifyIndices(streams), out var problem))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, problem);
        }

        var plan = ManualTrackPlan.BuildPlan(streams, choice);
        if (!RemuxRules.IsRemuxRequired(plan, streams.Audio, streams.Subtitles))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "What you chose is what this file already holds, so there is nothing to do.");
        }

        return new ManualCleanChoice(
            plan.RemovedAudio.Count + plan.RemovedSubtitles.Count,
            LibraryFilePlanner.EstimateSavings(probe, streams, plan));
    }

    public async Task<ApiResult> PostCleanAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var paths = model.StrList("paths", []);
        var confirmed = model.Bool("confirm_final_removal", false);
        var manualPlan = model.OptionalDict("manual_plan");
        var expectedSizeBytes = model.OptionalInt("expected_size_bytes");
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        if (paths.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Select at least one file to clean.");
        }

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        var byPath = await _scans.FilesAtPathsAsync(uow, libraryId, paths).ConfigureAwait(false);
        var selected = paths.Select(p => byPath.GetValueOrDefault(p)).OfType<LibraryScanFileEntry>().ToList();
        if (selected.Count == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "None of the selected files are in the latest scan. Scan the library again first.");
        }

        // A file set aside is dropped here rather than inside the queueing loop, so the confirmation dialog quotes
        // what will actually be removed and no preflight reads a file nothing is going to touch.
        var marks = await _fileMarks.ForLibraryAsync(uow, libraryId).ConfigureAwait(false);
        var skipped = selected
            .Where(entry => marks.TryGetValue(entry.Path, out var mark) && mark.LeaveAlone)
            .Select(entry => entry.Path)
            .ToList();
        selected = selected.Where(entry => !skipped.Contains(entry.Path, StringComparer.Ordinal)).ToList();
        if (selected.Count == 0)
        {
            return ApiRoutes.Ok(CleanOut(0, [], 0, 0, 0, skipped, []));
        }

        var settings = await _librarySettings.GetAsync(uow, libraryId).ConfigureAwait(false);
        var rules = await LibraryModeRules.ForAsync(uow, _libraries, library, settings).ConfigureAwait(false);
        var connectionsById = await LibraryModeMapping.ConnectionsForFilesAsync(uow, _connections, selected).ConfigureAwait(false);
        var preflightResults = await LibraryCleanPreflightRunner.RunAsync(
                selected, settings, rules, library.MediaType, _hardlinkInspector,
                _riskChecker, connectionsById, request.Context.RequestAborted)
            .ConfigureAwait(false);
        var preflight = preflightResults.ToDictionary(r => r.FilePath, StringComparer.Ordinal);

        // #568: the Problems view groups "still shared with a download" and "the manager would download it again"
        // from the file's own row. Only a preflight can know either, and a preflight costs a filesystem read and
        // (for the second) two manager calls per file, so this records what it just found rather than making the
        // Problems view re-run it over a whole library. Even a request that ends in the confirmation dialog below
        // has already done the work, so the notes are recorded either way.
        foreach (var result in preflightResults)
        {
            await _libraryView.RecordPreflightProblemAsync(uow, libraryId, result.FilePath, result.ProblemKind).ConfigureAwait(false);
        }

        // A track choice describes one file's tracks, so it only ever applies to one file, and what it removes is
        // what the confirmation dialog must quote — not what the library's own rules would have done to the same file.
        var chosen = manualPlan is null ? null : ManualChoiceFor(manualPlan, selected);
        var (removingFiles, removingTracks, bytesSaved) = chosen is { } choice
            ? (choice.Tracks > 0 ? 1 : 0, choice.Tracks, choice.BytesSaved)
            : LibraryModeMapping.RemovalTotals(selected);
        if (removingFiles > 0 && !confirmed)
        {
            // Nothing is queued, but the preflight notes just recorded above are real observations worth keeping
            // even if the operator cancels the dialog, so they are committed rather than rolled back with it.
            await request.CommitAsync().ConfigureAwait(false);
            return LibraryModeMapping.ConfirmationRequired(removingFiles, removingTracks, bytesSaved, LibraryCleanPreflightRunner.WarningMessages(preflight.Values));
        }

        var jobIds = new List<long>();
        foreach (var entry in selected)
        {
            // A file the rules are happy with is still one a person may want to change themselves, so their own
            // choice is the one thing that gets past this.
            if (chosen is null && entry.Classification != LibraryFileClassification.WouldChange)
            {
                continue;
            }

            // #508: a file still shared with a download is never queued, confirmation or not — clean_hardlinked_files
            // is the only thing that can allow it, since this is a data-safety fact, not a "are you sure" question.
            if (preflight.TryGetValue(entry.Path, out var fileResult) && fileResult.Skip)
            {
                skipped.Add(entry.Path);
                continue;
            }

            var fileConfirmed = (chosen is { } c ? c.Tracks == 0 : entry.RemovedAudioCount + entry.RemovedSubtitleCount == 0) || confirmed;
            var job = await _scans.EnqueueCleanAsync(
                    uow, _jobs, library.Id, entry.Path, "manual", fileConfirmed,
                    manualPlan, manualPlan is null ? null : expectedSizeBytes ?? entry.SizeBytes, entry.ProbeJson)
                .ConfigureAwait(false);
            jobIds.Add(job.Id);
        }

        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(CleanOut(
            jobIds.Count, jobIds, removingFiles, removingTracks, bytesSaved, skipped, LibraryCleanPreflightRunner.WarningMessages(preflight.Values)));
    }

    private static WireObject CleanOut(
        int queued,
        IReadOnlyList<long> jobIds,
        int filesCount,
        int tracksCount,
        long bytesSaved,
        IReadOnlyList<string> skipped,
        IReadOnlyList<string> warnings) => new WireObject()
        .Set("queued", queued)
        .Set("job_ids", new WireArray(jobIds.Select(id => (WireValue)new WireInteger(id))))
        .Set("files_count", filesCount)
        .Set("tracks_count", tracksCount)
        .Set("estimated_bytes_saved", bytesSaved)
        .Set("skipped_paths", new WireArray(skipped.Select(p => (WireValue)new WireString(p))))
        .Set("warnings", new WireArray(warnings.Select(w => (WireValue)new WireString(w))));

    /// <summary>
    /// "Leave this file alone", and its undo. It outlives a rescan (the scan rewrites its own index from scratch,
    /// so this is kept beside it), and nothing cleans the file while it is set: not a selection on the Library
    /// screen, not a queued job that reaches the handler later.
    /// </summary>
    public async Task<ApiResult> PostLeaveAloneAsync(ApiRequest request)
    {
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var libraryId = request.PathInt("library_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var filePath = model.Str("path", minLength: 1);
        var leaveAlone = model.Bool("leave_alone", true);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var library = await RequireLibraryAsync(uow, _libraries, libraryId, LibraryModeMapping.NoLibraryWithThatId).ConfigureAwait(false);
        await _fileMarks.SetLeaveAloneAsync(uow, library.Id, filePath!, leaveAlone, request.Time.GetUtcNow()).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("path", filePath).Set("leave_alone", leaveAlone));
    }
}
