import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests Setup › Performance › Cleanup makes, kept word for word.
 * Each body is the text the server sent, and each status the one it sent it with.
 */

export const maintenance: RecordedReply = {
  status: 200,
  body: '{"families":[{"family":"work_temp_stale_sweep","enabled":true,"description":"Deletes half-written copies Weir left in its work folders once they have sat for a day, including a failed copy you asked Weir to keep. Never touches a file being written.","pending":0,"running":0,"last_completed_at":null,"last_failed_at":null,"last_error":null,"interval_seconds":3600,"next_run_at":null},{"family":"unclaimed_handbacks","enabled":false,"description":"Deletes Weir\'s own cleaned copy from a hand-back folder when no media manager imported it in time. Only a copy that is still exactly as Weir wrote it; never a download. It stays off until you switch it on.","pending":0,"running":0,"last_completed_at":null,"last_failed_at":null,"last_error":null,"interval_seconds":21600,"next_run_at":null,"window_days":14}]}',
};

export const movieSweepQueued: RecordedReply = {
  status: 200,
  body: '{"queued":true,"detail":"Queued a work file sweep for Movies. It runs as soon as a worker is free."}',
};

export const tvSweepQueued: RecordedReply = {
  status: 200,
  body: '{"queued":true,"detail":"Queued a work file sweep for TV. It runs as soon as a worker is free."}',
};

/** Run now sent with a confirmation token the server no longer accepts. */
export const runRefusedTokenExpired: RecordedReply = {
  status: 400,
  body: '{"detail":"Your confirmation token expired. Refresh the page and try again."}',
};
