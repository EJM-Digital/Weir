using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Validation;
using Weir.Infrastructure.Artwork;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.DirectPlay;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>
/// The Files screen — <c>/api/v1/processing/files</c>. <c>passed_through</c> and <c>rejected</c> are valid
/// statuses in both the response and the <c>file_status</c> filter (#530). Covers the file list, delete, move-to-
/// top and requeue (one file or many). The per-file log lives in <see cref="ProcessingFileLogEndpoints"/>; the
/// #501 "choose tracks by hand" flow lives in <see cref="ProcessingFileTracksEndpoints"/>.
/// </summary>
public static class ProcessingFilesEndpoints
{
    public static IEndpointRouteBuilder MapProcessingFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<ProcessingFilesEndpointHandlers>();
        endpoints.MapV1("GET", "/processing/files", handlers.GetFilesAsync);
        endpoints.MapV1("GET", "/processing/files/{file_id}/remove-options", handlers.GetRemoveOptionsAsync);
        endpoints.MapV1("DELETE", "/processing/files/{file_id}", handlers.DeleteFileAsync);
        endpoints.MapV1("POST", "/processing/files/{file_id}/move-to-top", handlers.MoveToTopAsync);
        endpoints.MapV1("POST", "/processing/files/{file_id}/requeue", handlers.RequeueOneAsync);
        endpoints.MapV1("POST", "/processing/files/requeue", handlers.RequeueManyAsync);
        return endpoints;
    }

    /// <summary>Shared with <see cref="ProcessingFileLogEndpoints"/> and <see cref="ProcessingFileTracksEndpoints"/>.</summary>
    internal static async Task<ProcessingFileRecord> RequireFileAsync(UnitOfWork uow, FileStateStore files, long id) =>
        await files.GetAsync(uow, id).ConfigureAwait(false)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "Weir has no record of that file.");
}

/// <summary>Handlers for <see cref="ProcessingFilesEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class ProcessingFilesEndpointHandlers
{
    /// <summary>The most files one bulk requeue takes, matching the most one list request returns.</summary>
    private const int BulkRequeueMaxFiles = 1000;

    private readonly FileStateStore _files;
    private readonly DirectPlayService _directPlay;
    private readonly LiveProgressStore _liveProgress;
    private readonly HandbackStore _handback;
    private readonly ProcessingJobStore _jobs;
    private readonly LibraryStore _libraries;
    private readonly HistoryFileRemovalService _removal;
    private readonly SuitePauseService _pause;
    private readonly ArtworkPosterUrls _posters;

    public ProcessingFilesEndpointHandlers(
        FileStateStore files,
        DirectPlayService directPlay,
        LiveProgressStore liveProgress,
        HandbackStore handback,
        ProcessingJobStore jobs,
        LibraryStore libraries,
        HistoryFileRemovalService removal,
        SuitePauseService pause,
        ArtworkPosterUrls posters)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _directPlay = directPlay ?? throw new ArgumentNullException(nameof(directPlay));
        _liveProgress = liveProgress ?? throw new ArgumentNullException(nameof(liveProgress));
        _handback = handback ?? throw new ArgumentNullException(nameof(handback));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _removal = removal ?? throw new ArgumentNullException(nameof(removal));
        _pause = pause ?? throw new ArgumentNullException(nameof(pause));
        _posters = posters ?? throw new ArgumentNullException(nameof(posters));
    }

    private static WireObject FileOut(ProcessingFileRecord row, string libraryName, List<DirectPlayBadge> directPlay, LiveProgress? progress, string? posterUrl)
    {
        return new WireObject()
            .Set("kind", HistoryEntryKinds.Download)
            .Set("id", row.Id)
            .Set("library_id", row.LibraryId)
            .Set("library_name", libraryName)
            .Set("relative_path", row.RelativePath)
            .Set("poster_url", posterUrl)
            .Set("status", row.Status)
            .Set("status_reason", row.StatusReason)
            .Set("source_gone", GoneSourceText.IsHeld(row.Status, row.StatusReason))
            .Set("blocked_by_connection", row.BlockedByConnection)
            .Set("size_bytes", row.SizeBytes)
            .Set("video_codec", row.VideoCodec)
            .Set("video_width", row.VideoWidth)
            .Set("video_height", row.VideoHeight)
            .Set("audio_track_count", row.AudioTrackCount)
            .Set("subtitle_track_count", row.SubtitleTrackCount)
            .Set("duration_seconds", row.DurationSeconds is { } d ? WireValue.Of(d) : WireValue.Null)
            .Set("direct_play", new WireArray(directPlay.Select(v => (WireValue)new WireObject()
                .Set("device_id", v.DeviceId)
                .Set("device_name", v.DeviceName)
                .Set("verdict", v.Verdict)
                .Set("reasons", new WireArray(v.Reasons.Select(r => (WireValue)WireValue.Of(r)))))))
            .Set("progress_percent", progress?.Percent is { } pct ? WireValue.Of(pct) : WireValue.Null)
            .Set("progress_message", progress?.Message)
            .Set("progress_eta_seconds", progress?.EtaSeconds is { } eta ? WireValue.Of(eta) : WireValue.Null)
            // What Live shows on a working file (docs/archive/live-and-library.md): which step, how fast,
            // and what is coming out. All null when nothing is running on the file.
            .Set("progress_status", progress?.Status)
            .Set("progress_stage", progress?.Stage)
            .Set("progress_speed", progress?.Speed)
            .Set("progress_elapsed_seconds", progress?.ElapsedSeconds is { } elapsed ? WireValue.Of(elapsed) : WireValue.Null)
            .Set("progress_removed_audio", progress is null ? WireValue.Null : new WireArray(progress.RemovedAudio.Select(t => (WireValue)WireValue.Of(t))))
            .Set("progress_removed_subtitles", progress is null ? WireValue.Null : new WireArray(progress.RemovedSubtitles.Select(t => (WireValue)WireValue.Of(t))))
            .Set("failure_class", row.FailureClass)
            .Set("skip_kind", row.SkipKind)
            .Set("failure_attempts", row.FailureAttempts)
            .Set("next_retry_at", row.NextRetryAt?.ToWireText())
            .Set("output_collision_policy", row.OutputCollisionPolicy)
            .Set("output_collision_action", row.OutputCollisionAction)
            .Set("output_collision_reason", row.OutputCollisionReason)
            .Set("hold_until", row.HoldUntil?.ToWireText())
            .Set("size_changed_at", row.SizeChangedAt?.ToWireText())
            .Set("created_at", row.CreatedAt.ToWireText())
            .Set("updated_at", row.UpdatedAt.ToWireText())
            .Set("last_seen_at", row.LastSeenAt?.ToWireText())
            .Set("last_attempt_at", row.LastAttemptAt?.ToWireText());
    }

    public async Task<ApiResult> GetFilesAsync(ApiRequest request)
    {
        await request.RequireUserAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var filter = ProcessingFilesQuery.Read(request, issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var page = await _files.ListPageAsync(uow, filter).ConfigureAwait(false);
        var rows = page.Rows;
        var libraryNames = await _files.LibraryNamesAsync(uow).ConfigureAwait(false);
        var knownDevices = DeviceProfileLoader.Load(request.Options.WeirHome);
        var devices = await _directPlay.SelectedProfilesAsync(uow, knownDevices).ConfigureAwait(false);
        var progressByPath = _liveProgress.Snapshot();
        var handbacks = await _handback.ForLibrariesAsync(uow, rows.Select(row => row.LibraryId)).ConfigureAwait(false);
        var posters = await _posters.ForFilesAsync(uow, rows.Select(row => (row.LibraryId, row.RelativePath))).ConfigureAwait(false);

        var files = new List<WireValue>();
        foreach (var row in rows)
        {
            var libraryName = libraryNames.GetValueOrDefault(row.LibraryId, "Unknown workflow");
            var directPlay = DirectPlayService.ForRow(row, devices);
            progressByPath.TryGetValue(row.RelativePath, out var progress);
            // #652: the copy Weir handed back, and what a media manager said about it, for Activity.
            handbacks.TryGetValue((row.LibraryId, row.RelativePath), out var handback);
            posters.TryGetValue((row.LibraryId, row.RelativePath), out var posterUrl);
            files.Add(FileOut(row, libraryName, directPlay, progress, posterUrl).Set("handback", HandbackStore.ToOut(handback)));
        }

        var counts = await _files.StatusCountsAsync(uow, filter.LibraryId).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("files", new WireArray(files))
            .Set("status_counts", new WireObject().Also(dict =>
            {
                foreach (var (status, count) in counts)
                {
                    dict.Set(status, count);
                }
            }))
            .Set("returned", files.Count)
            .Set("limit", filter.Limit)
            .Set("next_cursor", page.NextCursor));
    }

    /// <summary>
    /// What Activity's remove dialog should offer for this title (#785), read before it is shown: whether it
    /// qualifies for a choice at all (only a failed or rejected file whose original is still in the watched
    /// folder does), and when it does, which manager "delete" would ask — or that Weir would delete the file
    /// itself — and whether "keep" has a manager to tell it will not be imported.
    /// </summary>
    public async Task<ApiResult> GetRemoveOptionsAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        issues.ThrowIfAny();

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await ProcessingFilesEndpoints.RequireFileAsync(uow, _files, id).ConfigureAwait(false);
        var options = await _removal.EvaluateAsync(uow, row, request.Context.RequestAborted).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject()
            .Set("requires_choice", options.RequiresChoice)
            .Set("manager_label", options.ManagerLabel)
            .Set("delete_handled_by_manager", options.DeleteHandledByManager)
            .Set("keep_notifies_manager", options.KeepNotifiesManager)
            .Set("fingerprint_recorded", options.FingerprintRecorded)
            .Set("unconfirmed_size_bytes", options.UnconfirmedSizeBytes)
            .Set("unconfirmed_modified_at", options.UnconfirmedModifiedAt)
            .Set("delete_refused_reason", options.DeleteRefusedReason));
    }

    /// <summary>
    /// "Remove from list" (#785). <c>resolution</c> chooses what happens to a failed or rejected file whose
    /// original is still in the watched folder: <c>delete</c> asks a manager to remove the download and search
    /// again, or Weir deletes it itself; <c>keep</c> leaves the file but skips it until it changes; <c>retry</c>
    /// queues it again, exactly as "Process again" does. A title that does not qualify for a choice — finished, or
    /// its file already gone — is always a plain remove, whatever <c>resolution</c> asks for.
    /// </summary>
    public async Task<ApiResult> DeleteFileAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var rawResolution = model.OptionalStr("resolution");
        var resolution = FileRemovalResolutions.Remove;
        if (rawResolution is not null && FieldRules.TryLiteral(WireValue.Of(rawResolution), ["body", "resolution"], FileRemovalResolutions.All, issues, out var parsedResolution))
        {
            resolution = parsedResolution;
        }

        // What `remove-options` showed the owner for a row with no recorded fingerprint (#786 follow-up): both or
        // neither, since one without the other confirms nothing.
        var confirmSizeBytes = model.OptionalInt("confirm_size_bytes", ge: 0);
        var confirmModifiedAt = model.OptionalStr("confirm_modified_at", maxLength: 64);

        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var confirmation = confirmSizeBytes is { } size && confirmModifiedAt is { } modifiedAt
            ? new FileRemovalConfirmation(size, modifiedAt)
            : (FileRemovalConfirmation?)null;

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await ProcessingFilesEndpoints.RequireFileAsync(uow, _files, id).ConfigureAwait(false);
        var chosen = await TryApplyChosenResolutionAsync(request, uow, row, resolution, confirmation).ConfigureAwait(false);
        if (chosen is { } result)
        {
            return result;
        }

        await _files.ForgetAsync(uow, id).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return NoContentResult();
    }

    /// <summary>
    /// <c>retry</c>, or a <c>delete</c>/<c>keep</c> for a title that qualifies for the choice. Null when
    /// <paramref name="resolution"/> is <see cref="FileRemovalResolutions.Remove"/>, or the title turned out not to
    /// qualify, so the caller's plain remove applies instead.
    /// </summary>
    private async Task<ApiResult?> TryApplyChosenResolutionAsync(ApiRequest request, UnitOfWork uow, ProcessingFileRecord row, string resolution, FileRemovalConfirmation? confirmation)
    {
        if (resolution == FileRemovalResolutions.Retry)
        {
            // A leftover "keep" marker must not shadow the very reprocessing a person just asked for (#786 review
            // of #785); harmless when there was never one.
            await _removal.ClearSkipMarkerAsync(uow, row.LibraryId, row.RelativePath).ConfigureAwait(false);
            // #786 review of #785: a skipped requeue (the library or original is gone) is not an error, but it is
            // not "queued again" either, so the caller reports whichever actually happened.
            var result = await new RequeueStore(_jobs, _libraries).RequeueFileAsync(uow, row).ConfigureAwait(false);
            await request.CommitAsync().ConfigureAwait(false);
            return ApiRoutes.Ok(new WireObject().Set("done", result.Requeued > 0).Set("detail", result.Detail));
        }

        if (resolution is not (FileRemovalResolutions.Delete or FileRemovalResolutions.Keep))
        {
            return null;
        }

        var options = await _removal.EvaluateAsync(uow, row, request.Context.RequestAborted).ConfigureAwait(false);
        if (!options.RequiresChoice)
        {
            return null;
        }

        if (resolution == FileRemovalResolutions.Delete)
        {
            await PausedRequests.RefuseWhilePausedAsync(_pause, request, uow, PausedRequests.DeleteDownload).ConfigureAwait(false);
        }

        var outcome = resolution == FileRemovalResolutions.Delete
            ? await _removal.DeleteAsync(uow, row, confirmation, request.Context.RequestAborted).ConfigureAwait(false)
            : await _removal.KeepAsync(uow, row, confirmation, request.Context.RequestAborted).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return outcome.Done
            ? ApiRoutes.Ok(new WireObject().Set("done", true).Set("detail", outcome.Message))
            : throw new ApiException(StatusCodes.Status502BadGateway, outcome.Message);
    }

    private static CustomApiResult NoContentResult() =>
        new(context =>
        {
            ApiResponses.NoContentJson(context);
            return Task.CompletedTask;
        });

    public async Task<ApiResult> MoveToTopAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await ProcessingFilesEndpoints.RequireFileAsync(uow, _files, id).ConfigureAwait(false);

        // Commit before the store is asked, or the request deadlocks against the store's own connection:
        // RequireUserAsync may have refreshed user_sessions.last_seen_at, and that write holds SQLite's single
        // write lock until this commit runs, while the store's BEGIN IMMEDIATE waits for it. Everything above is
        // a read apart from that session touch, so committing here changes nothing else.
        await request.CommitAsync().ConfigureAwait(false);
        var outcome = await _jobs.MoveFileToTopAsync(row.LibraryId, row.RelativePath).ConfigureAwait(false);
        if (outcome != JobActionOutcome.Ok)
        {
            return ApiRoutes.Ok(new WireObject()
                .Set("moved", false)
                .Set("detail", "There is no queued work for this file to move. It may already be running, or it may not have been picked up by a scan yet."));
        }

        return ApiRoutes.Ok(new WireObject().Set("moved", true).Set("detail", "Moved to the front of the queue. It starts as soon as there is capacity for it."));
    }

    public async Task<ApiResult> RequeueOneAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var id = request.PathInt("file_id", issues);
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await ProcessingFilesEndpoints.RequireFileAsync(uow, _files, id).ConfigureAwait(false);
        var result = await new RequeueStore(_jobs, _libraries).RequeueFileAsync(uow, row).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("requeued", result.Requeued).Set("skipped", result.Skipped).Set("detail", result.Detail));
    }

    public async Task<ApiResult> RequeueManyAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var payload = await request.ReadBodyAsync().ConfigureAwait(false);
        var issues = new ValidationIssues();
        var model = new BodyModel(payload, issues);
        var csrfToken = model.Str("csrf_token", minLength: 1);
        var libraryId = model.OptionalInt("library_id", ge: 1);
        string? fileStatus = null;
        var rawStatus = model.OptionalStr("file_status");
        if (rawStatus is not null && FieldRules.TryLiteral(WireValue.Of(rawStatus), ["body", "file_status"], ProcessingFileStatuses.All, issues, out var parsedStatus))
        {
            fileStatus = parsedStatus;
        }

        var pathContains = model.OptionalStr("path_contains", maxLength: 500);
        var limit = model.Number("limit", 200, required: false, ge: 1, le: BulkRequeueMaxFiles);
        var fileIds = model.IntList("file_ids");
        if (fileIds.Count > BulkRequeueMaxFiles)
        {
            issues.Add(new ValidationIssue(
                "too_long",
                ["body", "file_ids"],
                $"List should have at most {BulkRequeueMaxFiles} items after validation, not {fileIds.Count}",
                WireValue.Null,
                new WireObject().Set("field_type", "List").Set("max_length", BulkRequeueMaxFiles).Set("actual_length", fileIds.Count)));
        }

        model.Finish(ExtraFields.Forbid);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken);

        var uow = await request.DbAsync().ConfigureAwait(false);
        // file_ids is the exact set a person chose on screen, so the other filters still narrow it but the limit
        // never cuts it short.
        var rows = await _files.ListAsync(uow, new ProcessingFileListFilter
        {
            LibraryId = libraryId,
            Statuses = fileStatus is not null ? [fileStatus] : null,
            PathContains = pathContains,
            Ids = fileIds.Count > 0 ? fileIds : null,
            Limit = fileIds.Count > 0 ? BulkRequeueMaxFiles : (int)limit,
        }).ConfigureAwait(false);
        var result = await new RequeueStore(_jobs, _libraries).RequeueFilesAsync(uow, rows).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("requeued", result.Requeued).Set("skipped", result.Skipped).Set("detail", result.Detail));
    }
}

file static class WireObjectExtensions
{
    public static WireObject Also(this WireObject dict, Action<WireObject> configure)
    {
        configure(dict);
        return dict;
    }
}
