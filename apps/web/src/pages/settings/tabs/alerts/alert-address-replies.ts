import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests Setup › Connections › Alerts makes about an alert's address, kept word
 * for word. The server never sends an address's token: it shows the host and the start of the path, with the rest hidden.
 * The first alert posts to a Discord-style address, the second to a plain webhook.
 */

export const twoAlerts: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••","has_token":true,"events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"},{"id":2,"label":"Ops webhook","provider":"webhook","url":"https://hooks.weir-recording.invalid/ops/••••","has_token":true,"events":["job_failed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Saving the first alert with no address sent. */
export const savedKeepingTheAddress: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••","has_token":true,"events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"}',
};

/** Saving the first alert with a new address typed. */
export const addressReplaced: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••","has_token":true,"events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"}',
};

/** Ticking a third event on the first alert, with no address sent. */
export const tickSaved: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••","has_token":true,"events":["job_failed","processing_job_failed","processing_job_completed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"}',
};

export const twoAlertsAfterTick: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/••••","has_token":true,"events":["job_failed","processing_job_failed","processing_job_completed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"},{"id":2,"label":"Ops webhook","provider":"webhook","url":"https://hooks.weir-recording.invalid/ops/••••","has_token":true,"events":["job_failed"],"enabled":true,"created_at":"2026-10-15T19:15:52","updated_at":"2026-10-15T19:15:52"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** An alert whose address another alert already posts to, whether it is being added or saved. */
export const addressInUse: RecordedReply = {
  status: 409,
  body: '{"detail":"An alert already sends to that address."}',
};
