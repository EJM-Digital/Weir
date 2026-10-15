import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests Setup › Performance › Speed makes, kept word for word. The server
 * had its workers switched off and one file queued, which is why the second readout says the file is waiting.
 */

/** Files at once is 1 and the resolution budget is off. */
export const operatorSettings: RecordedReply = {
  status: 200,
  body: '{"max_concurrent_files":1,"runner_capacity":1,"runner_cost_sd":0,"runner_cost_720p":0,"runner_cost_1080p":1,"runner_cost_4k":1,"runner_budget_enabled":false,"work_temp_stale_sweep_enabled":true,"work_temp_stale_sweep_interval_seconds":null,"unclaimed_handback_cleanup_enabled":false,"unclaimed_handback_window_days":14,"unclaimed_handback_cleanup_interval_seconds":null,"keep_failed_work_files":false,"file_log_retention_days":90,"movie_schedule_enabled":true,"tv_schedule_enabled":true,"schedule_timezone":"UTC","updated_at":"2026-10-15T10:17:45"}',
};

/** The answer to saving Files at once as 4 with everything else as it was. */
export const operatorSettingsSavedAsFour: RecordedReply = {
  status: 200,
  body: '{"max_concurrent_files":4,"runner_capacity":1,"runner_cost_sd":0,"runner_cost_720p":0,"runner_cost_1080p":1,"runner_cost_4k":1,"runner_budget_enabled":false,"work_temp_stale_sweep_enabled":true,"work_temp_stale_sweep_interval_seconds":null,"unclaimed_handback_cleanup_enabled":false,"unclaimed_handback_window_days":14,"unclaimed_handback_cleanup_interval_seconds":null,"keep_failed_work_files":false,"file_log_retention_days":90,"movie_schedule_enabled":true,"tv_schedule_enabled":true,"schedule_timezone":"UTC","updated_at":"2026-10-15T10:18:07"}',
};

/** Nothing is queued. */
export const filesAtOnceNothingWaiting: RecordedReply = {
  status: 200,
  body: '{"files_at_once":1,"worker_slots":0,"effective_files_at_once":0,"running":0,"waiting":0,"waiting_for":"nothing","message":"","slots_note":"This Weir was started with its workers switched off (WEIR_PROCESSING_WORKER_COUNT is 0), so nothing starts here."}',
};

/** One file is queued, after Files at once was saved as 4. */
export const filesAtOnceOneWaiting: RecordedReply = {
  status: 200,
  body: '{"files_at_once":4,"worker_slots":0,"effective_files_at_once":0,"running":0,"waiting":1,"waiting_for":"workers_off","message":"1 file is waiting because this Weir\'s workers are switched off.","slots_note":"This Weir was started with its workers switched off (WEIR_PROCESSING_WORKER_COUNT is 0), so nothing starts here."}',
};
