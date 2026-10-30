namespace Weir.Core.Activity;

/// <summary>
/// Stable <c>event_type</c> strings for persisted activity, an append-only contract: stored rows and
/// clients filter on these exact values, so an existing value never changes.
/// </summary>
public static class ActivityEventTypes
{
    // Auth / platform
    public const string AuthLoginSucceeded = "auth.login_succeeded";
    public const string AuthLoginFailed = "auth.login_failed";
    public const string AuthLogout = "auth.logout";
    public const string AuthBootstrapSucceeded = "auth.bootstrap_succeeded";
    public const string AuthBootstrapDenied = "auth.bootstrap_denied";
    public const string AuthPasswordChanged = "auth.password_changed";
    public const string AuthUsernameChanged = "auth.username_changed";
    public const string AuthSessionsRevoked = "auth.sessions_revoked";
    public const string AuthSignInRecovered = "auth.sign_in_recovered";
    public const string SystemReconciliationRepair = "system.reconciliation.repair";

    /// <summary>An admin chose who can reach Weir over the network, from System › About.</summary>
    public const string SystemNetworkAccessChanged = "system.network_access.changed";

    /// <summary>An admin turned skipping sign-in for devices on the home network on or off.</summary>
    public const string SystemHomeNetworkSignInChanged = "system.home_network_sign_in.changed";

    /// <summary>Processing was paused, or its pause changed; the entry says until when, by whom, and whether Weir keeps looking for files.</summary>
    public const string SystemProcessingPaused = "system.processing_paused";

    /// <summary>Processing was resumed, by someone or because a timed pause ran out.</summary>
    public const string SystemProcessingResumed = "system.processing_resumed";

    // Processing durable families (jobs)
    public const string ProcessingFileProcessingProgress = "processing.file_processing_progress";
    public const string ProcessingFileRemuxPassCompleted = "processing.file_remux_pass_completed";
    public const string ProcessingWorkTempStaleSweepCompleted = "processing.work_temp_stale_sweep_completed";
    public const string ProcessingFailureCleanupSweepCompleted = "processing.failure_cleanup_sweep_completed";
    public const string ProcessingWorkerFailure = "processing.worker_failure";

    /// <summary>A file Weir could not process was handed back unmodified rather than kept (#465).</summary>
    public const string ProcessingFilePassedThrough = "processing.file_passed_through";
    public const string ProcessingFilePassThroughFailed = "processing.file_pass_through_failed";

    /// <summary>The opt-in reject policy: the manager accepted the report.</summary>
    public const string ProcessingFileRejected = "processing.file_rejected";

    /// <summary>The opt-in reject policy: Weir handed the original back because rejecting could not be done safely.</summary>
    public const string ProcessingFileRejectFellBack = "processing.file_reject_fell_back";

    /// <summary>A report sent to the media manager that handed a file over.</summary>
    public const string ProcessingHandoffReported = "processing.handoff_reported";

    /// <summary>A person told Weir to give up on reports it still owed a media manager, so the connection could be removed.</summary>
    public const string ProcessingHandoffReportsDropped = "processing.handoff_reports_dropped";

    /// <summary>The manager cancelled a hand-off Weir had not started (#480).</summary>
    public const string ProcessingHandoffCancelled = "processing.handoff_cancelled";

    /// <summary>An operator queued a hand-picked track choice for a held file (#501).</summary>
    public const string ProcessingFileManualPlanQueued = "processing.file_manual_plan_queued";

    /// <summary>A file left the watched folder before Weir finished with it, so it drops off the list (#645).</summary>
    public const string ProcessingFileLeftWatchedFolder = "processing.file_left_watched_folder";

    /// <summary>A media manager said it imported, or will not import, a file Weir handed back, and what Weir did with its copy (#652).</summary>
    public const string ProcessingHandbackOutcome = "processing.handback_outcome";

    /// <summary>The Cleanup job for hand-back copies nobody claimed finished a run (#652).</summary>
    public const string ProcessingUnclaimedHandbackCleanupCompleted = "processing.unclaimed_handback_cleanup_completed";

    /// <summary>Weir asked a Sonarr/Radarr connection to run its Downloaded Scan over a file it just wrote.</summary>
    public const string ProcessingDownloadedScanRequested = "processing.downloaded_scan_requested";

    /// <summary>The Activity page's remove dialog, "delete": the download was removed, by a manager or by Weir itself (#785).</summary>
    public const string ProcessingFileRemovalDeleted = "processing.file_removal_deleted";

    /// <summary>The Activity page's remove dialog, "keep": the file stays, and scans skip it until it changes (#785).</summary>
    public const string ProcessingFileRemovalKept = "processing.file_removal_kept";

    /// <summary>A repeat of a source Weir had already cleaned was left alone: "Skipped: already done" or "already imported".</summary>
    public const string ProcessingFileSkippedRepeat = "processing.file_skipped_repeat";

    /// <summary>A workflow was set up from, or had its folders updated from, a media manager that reports its folders (Deluno).</summary>
    public const string ProcessingWorkflowSynced = "processing.workflow_synced";

    /// <summary>Weir could not set up or update a workflow from a media manager yet, and what to do about it.</summary>
    public const string ProcessingWorkflowSyncNotice = "processing.workflow_sync_notice";
}

/// <summary>
/// Library mode's (#505) event types: cleaning files already in a library, in place. Kept in their own
/// <c>library.*</c> family, apart from the processing and platform types in <see cref="ActivityEventTypes"/>.
/// </summary>
public static class LibraryActivityEventTypes
{
    public const string ScanCompleted = "library.scan_completed";
    public const string FileCleaned = "library.file_cleaned";
    public const string FileSkipped = "library.file_skipped";
    public const string FileFailed = "library.file_failed";

    /// <summary>#735: a crash left a kept original that does not match its backup's contents. Recorded once per swap by
    /// <c>SwapRecoverySweep</c>, which then stops retrying it (<c>SwapJournalState.KeepConflict</c>).</summary>
    public const string OriginalKeepConflict = "library.original_keep_conflict";

    /// <summary>
    /// A person used "Download again" (#509), which asks a Sonarr or Radarr to delete a file and search for a new copy. Recorded for
    /// every answer, with who asked, which manager, whether the file was deleted and whether a search was sent.
    /// </summary>
    public const string RedownloadRequested = "library.redownload_requested";
}
