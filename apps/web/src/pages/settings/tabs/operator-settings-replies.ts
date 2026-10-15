import type { RecordedReply } from "../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests Setup › Performance and Cleanup make for the settings they save, kept word for word.
 * Each body is the text the server sent, and each status the one it sent it with.
 */

export const csrfToken: RecordedReply = {
  status: 200,
  body: '{"csrf_token":"csrf.session:fa867b1e4139652a91b855f36e38f6e3e37965c8bcce1566c9c1131a23b8227b.atEgaQ.H1CnBaNw5Mb_rtRPhsa2EcgIKMg"}',
};

export const signedInAdmin: RecordedReply = {
  status: 200,
  body: '{"user":{"id":1,"username":"recorded-admin","role":"admin","app_theme":null}}',
};

/** A Weir that has not been tuned. */
export const settingsDefault: RecordedReply = {
  status: 200,
  body: '{"max_concurrent_files":1,"runner_capacity":1,"runner_cost_sd":0,"runner_cost_720p":0,"runner_cost_1080p":1,"runner_cost_4k":1,"runner_budget_enabled":false,"work_temp_stale_sweep_enabled":true,"work_temp_stale_sweep_interval_seconds":null,"unclaimed_handback_cleanup_enabled":false,"unclaimed_handback_window_days":14,"unclaimed_handback_cleanup_interval_seconds":null,"keep_failed_work_files":false,"file_log_retention_days":90,"movie_schedule_enabled":true,"tv_schedule_enabled":true,"schedule_timezone":"UTC","updated_at":"2026-10-15T18:49:50"}',
};

/** Two files at once, with files weighed by resolution. */
export const settingsBudgetOn: RecordedReply = {
  status: 200,
  body: '{"max_concurrent_files":2,"runner_capacity":6,"runner_cost_sd":1,"runner_cost_720p":1,"runner_cost_1080p":2,"runner_cost_4k":4,"runner_budget_enabled":true,"work_temp_stale_sweep_enabled":true,"work_temp_stale_sweep_interval_seconds":null,"unclaimed_handback_cleanup_enabled":false,"unclaimed_handback_window_days":14,"unclaimed_handback_cleanup_interval_seconds":null,"keep_failed_work_files":false,"file_log_retention_days":90,"movie_schedule_enabled":true,"tv_schedule_enabled":true,"schedule_timezone":"UTC","updated_at":"2026-10-15T18:50:18"}',
};

/** Saving a Budget of 12 and costs of 2, 3, 5 and 8 on that. */
export const costsSaved: RecordedReply = {
  status: 200,
  body: '{"max_concurrent_files":2,"runner_capacity":12,"runner_cost_sd":2,"runner_cost_720p":3,"runner_cost_1080p":5,"runner_cost_4k":8,"runner_budget_enabled":true,"work_temp_stale_sweep_enabled":true,"work_temp_stale_sweep_interval_seconds":null,"unclaimed_handback_cleanup_enabled":false,"unclaimed_handback_window_days":14,"unclaimed_handback_cleanup_interval_seconds":null,"keep_failed_work_files":false,"file_log_retention_days":90,"movie_schedule_enabled":true,"tv_schedule_enabled":true,"schedule_timezone":"UTC","updated_at":"2026-10-15T18:50:18"}',
};

export const settingsAfterCosts: RecordedReply = {
  status: 200,
  body: '{"max_concurrent_files":2,"runner_capacity":12,"runner_cost_sd":2,"runner_cost_720p":3,"runner_cost_1080p":5,"runner_cost_4k":8,"runner_budget_enabled":true,"work_temp_stale_sweep_enabled":true,"work_temp_stale_sweep_interval_seconds":null,"unclaimed_handback_cleanup_enabled":false,"unclaimed_handback_window_days":14,"unclaimed_handback_cleanup_interval_seconds":null,"keep_failed_work_files":false,"file_log_retention_days":90,"movie_schedule_enabled":true,"tv_schedule_enabled":true,"schedule_timezone":"UTC","updated_at":"2026-10-15T18:50:18"}',
};

/** A save sent with a confirmation token the server no longer accepts. */
export const saveRefusedTokenExpired: RecordedReply = {
  status: 400,
  body: '{"detail":"Your confirmation token expired. Refresh the page and try again."}',
};

export const filesAtOnce: RecordedReply = {
  status: 200,
  body: '{"files_at_once":2,"worker_slots":0,"effective_files_at_once":0,"running":0,"waiting":0,"waiting_for":"nothing","message":"","slots_note":"This Weir was started with its workers switched off (WEIR_PROCESSING_WORKER_COUNT is 0), so nothing starts here."}',
};
