/**
 * The title of every event type the server records, in the words a person uses. The server's constants
 * (ActivityEventTypes.cs, LibraryFileChangeRules.cs) are the contract; scripts/check-event-titles.mjs fails when one has no
 * title here, so a new event never reaches the log as its raw name.
 */
export const EVENT_LABELS: Record<string, string> = {
  "auth.login_succeeded": "Sign-in finished",
  "auth.login_failed": "Sign-in failed",
  "auth.logout": "Sign-out finished",
  "auth.bootstrap_succeeded": "First admin created",
  "auth.bootstrap_denied": "First-time setup blocked",
  "auth.password_changed": "Password changed",
  "auth.username_changed": "Username changed",
  "auth.sessions_revoked": "Sessions signed out",
  "system.reconciliation.repair": "System repair finished",
  "system.network_access.changed": "Network access changed",
  "system.processing_paused": "Processing paused",
  "system.processing_resumed": "Processing resumed",
  "processing.supplied_payload_evaluation_completed":
    "Manual queue check finished",
  "processing.candidate_gate_completed": "Queue check finished",
  "processing.file_processing_progress": "File processing",
  "processing.file_remux_pass_completed": "File processing finished",
  "processing.work_temp_stale_sweep_completed":
    "Temporary files cleanup finished",
  "processing.failure_cleanup_sweep_completed":
    "Cleanup after failed processing finished",
  "processing.worker_failure": "A processing job failed",
  "processing.file_passed_through": "File handed back unchanged",
  "processing.file_pass_through_failed": "File could not be handed back",
  "processing.file_rejected": "File rejected",
  "processing.file_reject_fell_back": "File handed back instead of rejected",
  "processing.handoff_reported": "Result reported to your media manager",
  "processing.handoff_cancelled": "Hand-off cancelled by your media manager",
  "processing.handoff_reports_dropped":
    "Reports to your media manager given up",
  "processing.file_manual_plan_queued": "Hand-picked tracks queued",
  "processing.file_left_watched_folder": "File left the watched folder",
  "processing.handback_outcome": "What happened to a cleaned copy",
  "processing.unclaimed_handback_cleanup_completed": "Unclaimed copies cleared",
  "processing.downloaded_scan_requested": "Downloaded Scan requested",
  "processing.file_removal_deleted": "Download removed",
  "processing.file_removal_kept": "File kept",
  "processing.file_skipped_repeat": "Skipped: already done or imported",
  "processing.workflow_synced": "Workflow updated from your media manager",
  "processing.workflow_sync_notice": "Workflow could not be updated yet",
  "library.scan_completed": "Library scan finished",
  "library.file_cleaned": "Library file cleaned",
  "library.file_skipped": "Library file skipped",
  "library.file_failed": "Library file could not be cleaned",
  "library.original_keep_conflict": "Kept original does not match its backup",
  "library.redownload_requested": "Download again asked of your media manager",
  "library.file_change_notified": "Media manager told about a changed file",
  "library.file_change_notify_skipped": "Media manager notice skipped",
  "library.file_change_notify_warning":
    "Media manager could not be told about a changed file",
};
