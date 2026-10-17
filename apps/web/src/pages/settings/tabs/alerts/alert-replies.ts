import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests Setup › Connections › Alerts makes, kept word for word.
 * Each body is the text the server sent, and each status the one it sent it with.
 */

export const noChannels: RecordedReply = {
  status: 200,
  body: '{"items":[],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

export const channelAdded: RecordedReply = {
  status: 201,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}',
};

export const oneChannel: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Adding an alert whose address points at this machine. */
export const addRefusedForItsAddress: RecordedReply = {
  status: 400,
  body: '{"detail":"Blocked provider URL host: 127.0.0.1"}',
};

/** Ticking a third event on the first alert. */
export const tickSaved: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_failed","processing_job_completed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}',
};

export const afterTick: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_failed","processing_job_completed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Unticking the file-failed event on that alert. */
export const untickSaved: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_completed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}',
};

export const afterUntick: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_completed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Editing the first alert: new label, provider, address and events, and switched off. */
export const editSaved: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Pager bridge","provider":"webhook","url":"https://hooks.weir-recording.invalid/pager/9f2c7a1e-token","events":["processing_job_failed","processing_job_completed"],"enabled":false,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}',
};

export const afterEdit: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Pager bridge","provider":"webhook","url":"https://hooks.weir-recording.invalid/pager/9f2c7a1e-token","events":["processing_job_failed","processing_job_completed"],"enabled":false,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Editing the first alert to an address that points at this machine. */
export const editRefusedForItsAddress: RecordedReply = {
  status: 400,
  body: '{"detail":"Blocked provider URL host: 127.0.0.1"}',
};

/** Saving the first alert with nothing changed. */
export const savedAsItWas: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"}',
};

export const twoChannels: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Discord alerts","provider":"discord","url":"https://hooks.weir-recording.invalid/api/webhooks/1034598712/s3cr3t-T0ken_abc","events":["job_failed","processing_job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:50:17"},{"id":2,"label":"Ops webhook","provider":"webhook","url":"https://hooks.weir-recording.invalid/ops/6d41b0c8-token","events":["job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:18","updated_at":"2026-10-15T18:50:18"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Send test to an alert whose address does not resolve. */
export const testCouldNotResolve: RecordedReply = {
  status: 200,
  body: '{"ok":false,"error":"Weir could not resolve the notification destination."}',
};

/** The same, for the second alert. */
export const testCouldNotResolveToo: RecordedReply = {
  status: 200,
  body: '{"ok":false,"error":"Weir could not resolve the notification destination."}',
};

/** Send test to an alert another person has already removed. */
export const testAlertGone: RecordedReply = {
  status: 404,
  body: '{"detail":"Notification channel not found."}',
};

/**
 * Send test to an alert that was delivered. This one is not a recording: delivering needs an address on the internet,
 * which a recording does not send to. It has the two keys the server always answers a test with, as the failed
 * reply above shows, and says what the server's handler sends when nothing went wrong.
 */
export const testDelivered: RecordedReply = {
  status: 200,
  body: '{"ok":true,"error":null}',
};

/** The first alert switched off, beside a second one. */
export const firstOfTwoSwitchedOff: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Pager bridge","provider":"webhook","url":"https://hooks.weir-recording.invalid/pager/9f2c7a1e-token","events":["processing_job_failed","processing_job_completed"],"enabled":false,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:59:11"},{"id":2,"label":"Ops webhook","provider":"webhook","url":"https://hooks.weir-recording.invalid/ops/6d41b0c8-token","events":["job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:18","updated_at":"2026-10-15T18:50:18"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};

/** Ticking a third event on the alert that is switched off. */
export const tickOnSwitchedOffSaved: RecordedReply = {
  status: 200,
  body: '{"id":1,"label":"Pager bridge","provider":"webhook","url":"https://hooks.weir-recording.invalid/pager/9f2c7a1e-token","events":["processing_job_failed","processing_job_completed","job_failed"],"enabled":false,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:59:11"}',
};

export const afterTickOnSwitchedOff: RecordedReply = {
  status: 200,
  body: '{"items":[{"id":1,"label":"Pager bridge","provider":"webhook","url":"https://hooks.weir-recording.invalid/pager/9f2c7a1e-token","events":["processing_job_failed","processing_job_completed","job_failed"],"enabled":false,"created_at":"2026-10-15T18:50:17","updated_at":"2026-10-15T18:59:11"},{"id":2,"label":"Ops webhook","provider":"webhook","url":"https://hooks.weir-recording.invalid/ops/6d41b0c8-token","events":["job_failed"],"enabled":true,"created_at":"2026-10-15T18:50:18","updated_at":"2026-10-15T18:50:18"}],"supported_events":["job_completed","job_failed","processing_job_completed","processing_job_failed"],"supported_providers":["webhook","discord"]}',
};
