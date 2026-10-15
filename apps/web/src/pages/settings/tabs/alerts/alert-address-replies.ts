import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave when an alert's address is typed, kept word for word. The server never sends an
 * address's token: it shows the host and the start of the path, with the rest hidden.
 */

/** Saving the first alert with a new address typed. */
export const addressReplaced: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••","has_token":true,"events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"}',
};

/** An alert whose address another alert already posts to, whether it is being added or saved. */
export const addressInUse: RecordedReply = {
  status: 409,
  body: '{"detail":"An alert already sends to that address."}',
};
