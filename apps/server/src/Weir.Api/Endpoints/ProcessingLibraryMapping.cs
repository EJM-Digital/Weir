using Microsoft.AspNetCore.Http;
using Weir.Api.Http;
using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Core.Time;
using Weir.Core.Validation;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.Sqlite;

namespace Weir.Api.Endpoints;

/// <summary>Shared library request/response mapping used by <see cref="ProcessingLibraryEndpoints"/> and
/// <see cref="ProcessingLibraryDiscoveryEndpoints"/>: the wire shape a library is read into and serialized
/// back out as, and the checks a save runs before it commits.</summary>
internal static class ProcessingLibraryMapping
{
    internal static async Task<WireObject> LibraryOutAsync(
        ApiRequest request,
        UnitOfWork uow,
        ProcessingLibraryRecord row,
        LibraryStore libraries,
        MediaManagerConnectionStore connections,
        OperatorSettingsStore operatorSettings,
        SuiteSettingsStore suiteSettings,
        ScanWakeups? looks = null)
    {
        var managerIds = await libraries.ManagerConnectionIdsAsync(uow, row.Id).ConfigureAwait(false);
        var activeJobs = await libraries.ActiveJobCountAsync(uow, row).ConfigureAwait(false);
        var performance = await operatorSettings.EnsureAsync(uow).ConfigureAwait(false);
        // Weir never scans a workflow a linked Deluno hands its downloads to, so there is no next scan or look to show.
        var handedOff = (await libraries.ManagerLinksAsync(uow, row.Id).ConfigureAwait(false)).HandedOffByManager;
        var periodicScan = handedOff
            ? new PeriodicScanStatus(PeriodicScanStates.Off, null)
            : await PeriodicScanStatusAsync(request, uow, row, performance, suiteSettings, looks).ConfigureAwait(false);

        // manager_coverage: the linked connections' last saved connection-test result (no live call — a
        // listing must not depend on every linked manager answering right now).
        var managerRows = new List<MediaManagerConnectionRecord?>(managerIds.Count);
        foreach (var connectionId in managerIds)
        {
            managerRows.Add(await connections.GetAsync(uow, connectionId).ConfigureAwait(false));
        }

        // The manager that owns this workflow's watched and output folders, while the workflow is still linked to it.
        var foldersSyncedFrom = WorkflowSyncRules.FoldersSyncedFrom(
            row.DiscoveredFromConnectionId,
            row.DiscoveredLibraryKey,
            managerIds,
            managerRows.FirstOrDefault(item => item?.Id == row.DiscoveredFromConnectionId)?.Kind);

        string coverage;
        string coverageDetail;
        if (managerRows.Count == 0)
        {
            coverage = "no_upstream_signal";
            coverageDetail = "No media manager is linked. Watched-folder remux can still run after local safety gates, " +
                              "but upstream import protection is reduced.";
        }
        else if (managerRows.Any(item => item is null || !item.Enabled || item.LastTestOk == false))
        {
            coverage = "unreachable";
            coverageDetail = "A linked media manager did not answer its last connection test. Remux remains local, " +
                              "but manager-truth-dependent cleanup is held until the connection is available.";
        }
        else if (managerRows.Any(item => item!.LastTestOk != true))
        {
            coverage = "no_upstream_signal";
            coverageDetail = "A manager is linked but has not returned a successful connection signal yet. " +
                              "This is not the same as an empty queue.";
        }
        else
        {
            coverage = "connected";
            coverageDetail = "The linked manager connection is healthy. Upstream checks and manager-truth-dependent " +
                              "cleanup can use its latest answer.";
        }

        return new WireObject()
            .Set("id", row.Id)
            .Set("name", row.Name)
            .Set("enabled", row.Enabled)
            .Set("media_type", row.MediaType)
            .Set("display_order", row.DisplayOrder)
            .Set("watched_folder", row.WatchedFolder)
            .Set("work_folder", row.WorkFolder)
            .Set("output_folder", row.OutputFolder)
            .Set("media_extensions_csv", row.MediaExtensionsCsv)
            .Set("exclude_markers_csv", row.ExcludeMarkersCsv)
            .Set("include_patterns_csv", row.IncludePatternsCsv)
            .Set("exclude_patterns_csv", row.ExcludePatternsCsv)
            .Set("min_file_size_mb", row.MinFileSizeMb)
            .Set("max_file_size_mb", row.MaxFileSizeMb)
            .Set("rejected_file_action", row.RejectedFileAction.Length > 0 ? row.RejectedFileAction : "leave")
            .Set("ready_after_seconds", row.ReadyAfterSeconds)
            .Set("created_after", WindowText(row.CreatedAfter))
            .Set("created_before", WindowText(row.CreatedBefore))
            .Set("modified_after", WindowText(row.ModifiedAfter))
            .Set("modified_before", WindowText(row.ModifiedBefore))
            .Set("exclude_hidden", row.ExcludeHidden)
            .Set("top_level_only", row.TopLevelOnly)
            .Set("scan_interval_seconds", row.ScanIntervalSeconds)
            .Set("sidecar_patterns_csv", row.SidecarPatternsCsv)
            .Set("preserve_original_timestamps", row.PreserveOriginalTimestamps)
            .Set("output_collision_policy", row.OutputCollisionPolicy.Length > 0 ? row.OutputCollisionPolicy : "replace")
            .Set("ffmpeg_strictness", row.FfmpegStrictness.Length > 0 ? row.FfmpegStrictness : "normal")
            .Set("remux_writer", RemuxWriterChoice.Normalize(row.RemuxWriter))
            .Set("ignore_size_changes", row.IgnoreSizeChanges)
            .Set("skip_access_tests", row.SkipAccessTests)
            .Set("file_system_events_enabled", row.FileSystemEventsEnabled)
            .Set("schedule_grid", row.ScheduleGrid)
            .Set("max_attempts", row.MaxAttempts)
            .Set("retry_backoff_seconds", row.RetryBackoffSeconds)
            .Set("retry_execution_failures", row.RetryExecutionFailures)
            .Set("retry_preflight_failures", row.RetryPreflightFailures)
            .Set("failure_policy", ProcessingFailurePolicies.Normalize(row.FailurePolicy))
            .Set("schedule_enabled", row.ScheduleEnabled)
            .Set("schedule_hours_limited", row.ScheduleHoursLimited)
            .Set("schedule_days", row.ScheduleDays)
            .Set("schedule_start", row.ScheduleStart)
            .Set("schedule_end", row.ScheduleEnd)
            .Set("max_concurrent_files", row.MaxConcurrentFiles)
            // What the workflow is held to right now: never more than "Files at once" in Setup › Performance › Speed, even if that was lowered after this was set.
            .Set("effective_max_concurrent_files", OperatorSettingsRules.EffectiveLibraryLimit(row.MaxConcurrentFiles, performance.MaxConcurrentFiles))
            .Set("priority", row.Priority)
            .Set("rule_set_id", row.RuleSetId)
            .Set("manager_connection_ids", new WireArray(managerIds.Select(id => (WireValue)WireValue.Of(id))))
            .Set("remove_original_after_success", row.RemoveOriginalAfterSuccess)
            .Set("minimum_free_disk_space_mb", OperatorSettingsRules.ClampSizeMb(row.MinimumFreeDiskSpaceMb))
            .Set("manager_coverage", coverage)
            .Set("manager_coverage_detail", coverageDetail)
            .Set("discovered_from_connection_id", row.DiscoveredFromConnectionId)
            .Set("discovered_library_key", row.DiscoveredLibraryKey)
            .Set("folders_synced_from_connection_id", foldersSyncedFrom)
            .Set("active_job_count", activeJobs)
            // When Weir next looks at the watched folder, so Processing can count an arriving file down to it.
            .Set("next_look_at", !handedOff && looks?.NextLookFor(row.Id) is { } next ? Timestamp.FromDateTimeOffset(next).ToWireText() : null)
            // Whether the periodic scan-dispatch timer runs for this library right now, and when it next will (#747).
            .Set("periodic_scan", periodicScan.State)
            .Set("next_scan_at", periodicScan.NextScanAt is { } nextScan ? Timestamp.FromDateTimeOffset(nextScan).ToWireText() : null)
            .Set("updated_at", row.UpdatedAt.ToWireText());
    }

    /// <summary>
    /// A detection-window time as UTC with its timezone written out, which is what a save asks for: the stored time has no offset,
    /// so a window read back and saved again would otherwise be refused for naming no timezone.
    /// </summary>
    private static string? WindowText(Timestamp? value) => value is { } time ? Timestamp.FromUtc(time.AsUtc).ToWireText() : null;

    /// <summary>Resolves <see cref="PeriodicScanStatus"/> for one library from the same switches and schedule window
    /// the periodic scan-dispatch scheduler and the worker's upkeep admission read.</summary>
    private static async Task<PeriodicScanStatus> PeriodicScanStatusAsync(
        ApiRequest request, UnitOfWork uow, ProcessingLibraryRecord row, ProcessingOperatorSettingsRecord operatorSettings, SuiteSettingsStore suiteSettingsStore, ScanWakeups? looks)
    {
        var suite = await suiteSettingsStore.EnsureAsync(uow).ConfigureAwait(false);
        var timezoneName = string.IsNullOrWhiteSpace(suite.AppTimezone) ? "UTC" : suite.AppTimezone.Trim();
        var now = request.Time.GetUtcNow();
        return PeriodicScanStatus.Resolve(
            row,
            request.Options.ProcessingWatchedFolderRemuxScanDispatchScheduleEnabled,
            operatorSettings.MovieScheduleEnabled,
            operatorSettings.TvScheduleEnabled,
            timezoneName,
            now,
            looks?.NextPeriodicFor(row.Id));
    }

    /// <summary>
    /// Retired workflow settings: the fallback rewrite (folded into the "best" writer), hardware decoding (Weir copies
    /// video without decoding it) and the three waits that became <c>ready_after_seconds</c>. A save may still send them.
    /// </summary>
    private static readonly string[] RemovedSettings =
    [
        "rewrite_with_ffmpeg", "hardware_decode_mode", "hardware_device", "hardware_disabled_vendors_csv",
        "min_file_age_seconds", "hold_minutes", "file_detection_interval_seconds",
    ];

    internal static ProcessingLibraryInput ReadLibraryBody(BodyModel model)
    {
        model.AcceptAndIgnore(RemovedSettings);
        var name = model.Str("name", minLength: 1, maxLength: 120);
        var mediaType = model.Literal("media_type", ProcessingMediaScopes.All);
        var enabled = model.Bool("enabled", defaultValue: true);
        var watchedFolder = model.OptionalStr("watched_folder", defaultValue: "", maxLength: 4000) ?? "";
        var workFolder = model.OptionalStr("work_folder", defaultValue: "", maxLength: 4000) ?? "";
        var outputFolder = model.OptionalStr("output_folder", defaultValue: "", maxLength: 4000) ?? "";
        var mediaExtensionsCsv = model.OptionalStr("media_extensions_csv", defaultValue: "", maxLength: 1000) ?? "";
        var excludeMarkersCsv = model.OptionalStr("exclude_markers_csv", defaultValue: "", maxLength: 1000) ?? "";
        var includePatternsCsv = model.OptionalStr("include_patterns_csv", defaultValue: "", maxLength: 1000) ?? "";
        var excludePatternsCsv = model.OptionalStr("exclude_patterns_csv", defaultValue: "", maxLength: 1000) ?? "";
        var minFileSizeMb = model.Number("min_file_size_mb", LibraryIntake.DefaultMinFileSizeMb, required: false, ge: 0, le: LibraryIntake.LargestSizeLimitMb);
        var maxFileSizeMb = model.Number("max_file_size_mb", 0, required: false, ge: 0, le: LibraryIntake.LargestSizeLimitMb);
        var rejectedFileAction = model.Literal("rejected_file_action", [.. RejectedFileActions.All], defaultValue: RejectedFileActions.Leave);
        var readyAfterSeconds = model.Number("ready_after_seconds", LibraryIntake.DefaultReadyAfterSeconds, required: false, ge: 0, le: LibraryIntake.MaxReadyAfterSeconds);
        var createdAfter = model.OptionalDateTime("created_after");
        var createdBefore = model.OptionalDateTime("created_before");
        var modifiedAfter = model.OptionalDateTime("modified_after");
        var modifiedBefore = model.OptionalDateTime("modified_before");
        var excludeHidden = model.Bool("exclude_hidden", defaultValue: true);
        var topLevelOnly = model.Bool("top_level_only", defaultValue: false);
        var sidecarPatternsCsv = model.OptionalStr("sidecar_patterns_csv", defaultValue: ".srt,.ass,.ssa,.sub,.idx,.vtt,.nfo,.jpg,.png") ?? string.Empty;
        var preserveOriginalTimestamps = model.Bool("preserve_original_timestamps", defaultValue: false);
        var outputCollisionPolicy = model.Literal("output_collision_policy", [.. OutputCollisionPolicies.All], defaultValue: OutputCollisionPolicies.Replace);
        var ffmpegStrictness = model.Literal("ffmpeg_strictness", [.. FfmpegStrictnessLevels.All], defaultValue: FfmpegStrictnessLevels.Normal);
        // #548: two values, because "mkvmerge" and "auto" would behave identically - see RemuxWriterChoice.
        var remuxWriter = model.Literal("remux_writer", [.. RemuxWriterChoice.All], defaultValue: RemuxWriterChoice.Best);
        var scanIntervalSeconds = model.Number("scan_interval_seconds", 300, required: false, ge: 10, le: 604800);
        var ignoreSizeChanges = model.Bool("ignore_size_changes", defaultValue: false);
        var skipAccessTests = model.Bool("skip_access_tests", defaultValue: false);
        var maxAttempts = model.Number("max_attempts", 3, required: false, ge: 1, le: 20);
        var retryBackoffSeconds = model.Number("retry_backoff_seconds", 300, required: false, ge: 1, le: 3600);
        var retryExecutionFailures = model.Bool("retry_execution_failures", defaultValue: true);
        var failurePolicy = model.Literal("failure_policy", ProcessingFailurePolicies.All, defaultValue: ProcessingFailurePolicies.PassThrough);
        var retryPreflightFailures = model.Bool("retry_preflight_failures", defaultValue: false);
        var scheduleGrid = model.OptionalStr("schedule_grid", defaultValue: "") ?? string.Empty;
        var fileSystemEventsEnabled = model.Bool("file_system_events_enabled", defaultValue: true);
        var scheduleEnabled = model.Bool("schedule_enabled", defaultValue: true);
        var scheduleHoursLimited = model.Bool("schedule_hours_limited", defaultValue: false);
        var scheduleDays = model.OptionalStr("schedule_days", defaultValue: "", maxLength: 200) ?? string.Empty;
        var scheduleStart = model.OptionalStr("schedule_start", defaultValue: "00:00", maxLength: 5) ?? "00:00";
        var scheduleEnd = model.OptionalStr("schedule_end", defaultValue: "23:59", maxLength: 5) ?? "23:59";
        // 0 is "the same as Files at once" (#633), and what a library starts with.
        var maxConcurrentFiles = model.Number(
            "max_concurrent_files", OperatorSettingsRules.LibraryFollowsFilesAtOnce, required: false, ge: 0, le: OperatorSettingsRules.MaxFilesAtOnce);
        var priority = model.Number("priority", 0, required: false, ge: -100, le: 100);
        var ruleSetId = model.OptionalInt("rule_set_id");
        var managerConnectionIds = model.IntList("manager_connection_ids");
        var removeOriginalAfterSuccess = model.Bool("remove_original_after_success", defaultValue: true);
        var minimumFreeDiskSpaceMb = model.Number(
            "minimum_free_disk_space_mb", ProcessingLibraryRecord.DefaultMinimumFreeDiskSpaceMb, required: false, ge: 0, le: 1024 * 1024);

        return new ProcessingLibraryInput
        {
            Name = name,
            MediaType = mediaType,
            Enabled = enabled,
            WatchedFolder = watchedFolder,
            WorkFolder = workFolder,
            OutputFolder = outputFolder,
            MediaExtensionsCsv = mediaExtensionsCsv,
            ExcludeMarkersCsv = excludeMarkersCsv,
            IncludePatternsCsv = includePatternsCsv,
            ExcludePatternsCsv = excludePatternsCsv,
            MinFileSizeMb = minFileSizeMb,
            MaxFileSizeMb = maxFileSizeMb,
            RejectedFileAction = rejectedFileAction,
            ReadyAfterSeconds = readyAfterSeconds,
            CreatedAfter = createdAfter,
            CreatedBefore = createdBefore,
            ModifiedAfter = modifiedAfter,
            ModifiedBefore = modifiedBefore,
            ExcludeHidden = excludeHidden,
            TopLevelOnly = topLevelOnly,
            SidecarPatternsCsv = sidecarPatternsCsv,
            PreserveOriginalTimestamps = preserveOriginalTimestamps,
            OutputCollisionPolicy = outputCollisionPolicy,
            FfmpegStrictness = ffmpegStrictness,
            RemuxWriter = remuxWriter,
            ScanIntervalSeconds = scanIntervalSeconds,
            IgnoreSizeChanges = ignoreSizeChanges,
            SkipAccessTests = skipAccessTests,
            MaxAttempts = maxAttempts,
            RetryBackoffSeconds = retryBackoffSeconds,
            RetryExecutionFailures = retryExecutionFailures,
            FailurePolicy = failurePolicy,
            ScheduleGrid = scheduleGrid,
            RetryPreflightFailures = retryPreflightFailures,
            FileSystemEventsEnabled = fileSystemEventsEnabled,
            ScheduleEnabled = scheduleEnabled,
            ScheduleHoursLimited = scheduleHoursLimited,
            ScheduleDays = scheduleDays,
            ScheduleStart = scheduleStart,
            ScheduleEnd = scheduleEnd,
            MaxConcurrentFiles = maxConcurrentFiles,
            Priority = priority,
            RuleSetId = ruleSetId,
            ManagerConnectionIds = managerConnectionIds,
            RemoveOriginalAfterSuccess = removeOriginalAfterSuccess,
            MinimumFreeDiskSpaceMb = minimumFreeDiskSpaceMb,
        };
    }

    /// <summary>
    /// A detection window needs a timezone, and its windows must be in order. Both are reported as
    /// <c>value_error</c> validation issues so the 422 body has the issue-list shape existing clients read,
    /// not a flat detail string.
    /// </summary>
    internal static void ValidateDetectionWindows(ProcessingLibraryInput body, ValidationIssues issues)
    {
        void RequireTimezone(string field, Timestamp? value)
        {
            if (value is { Offset: null })
            {
                issues.Add(new ValidationIssue("value_error", ["body", field], "Value error, Detection-window times must include a timezone.", WireValue.Null));
            }
        }

        RequireTimezone("created_after", body.CreatedAfter);
        RequireTimezone("created_before", body.CreatedBefore);
        RequireTimezone("modified_after", body.ModifiedAfter);
        RequireTimezone("modified_before", body.ModifiedBefore);
        if (issues.Any)
        {
            return;
        }

        if (body.CreatedAfter is { } ca && body.CreatedBefore is { } cb && ca.AsUtc >= cb.AsUtc)
        {
            issues.Add(new ValidationIssue("value_error", ["body"], "Value error, Created after must be earlier than created before.", WireValue.Null));
        }

        if (body.ModifiedAfter is { } ma && body.ModifiedBefore is { } mb && ma.AsUtc >= mb.AsUtc)
        {
            issues.Add(new ValidationIssue("value_error", ["body"], "Value error, Modified after must be earlier than modified before.", WireValue.Null));
        }
    }

    /// <summary>
    /// The detection windows of a save that passed <see cref="ValidateDetectionWindows"/>, each as the same instant in UTC. The
    /// database keeps the wall clock only and drops the offset, so a time given as <c>02:00+02:00</c> would otherwise be kept as 02:00 UTC.
    /// </summary>
    internal static ProcessingLibraryInput WithWindowsInUtc(ProcessingLibraryInput body)
    {
        static Timestamp? InUtc(Timestamp? value) => value is { } time ? Timestamp.FromUtc(time.AsUtc) : null;
        return body with
        {
            CreatedAfter = InUtc(body.CreatedAfter),
            CreatedBefore = InUtc(body.CreatedBefore),
            ModifiedAfter = InUtc(body.ModifiedAfter),
            ModifiedBefore = InUtc(body.ModifiedBefore),
        };
    }

    /// <summary>
    /// <c>reject</c> deletes downloads, so it cannot be saved for a library no manager
    /// can take one for. Called after the row is written (so the manager links it was just given are the ones checked)
    /// but before the transaction commits.
    /// </summary>
    internal static async Task RefuseUnsupportedRejectAsync(
        UnitOfWork uow, ProcessingLibraryRecord row, LibraryStore libraries, MediaManagerConnectionService connectionService, RejectSupportEvaluator rejectSupport)
    {
        if (ProcessingFailurePolicies.Normalize(row.FailurePolicy) != ProcessingFailurePolicies.Reject)
        {
            return;
        }

        var connectionIds = await libraries.ManagerConnectionIdsAsync(uow, row.Id).ConfigureAwait(false);
        var connections = await connectionService.ConnectionsByIdAsync(uow, connectionIds).ConfigureAwait(false);
        var support = await rejectSupport.EvaluateAsync(connections).ConfigureAwait(false);
        if (!support.Available)
        {
            await uow.RollbackAsync().ConfigureAwait(false);
            throw new ApiException(StatusCodes.Status400BadRequest, $"This workflow cannot use Reject yet. {support.Reason}");
        }
    }
}
