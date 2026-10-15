import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests the Connections screens make, kept word for word. Each body is the
 * text the server sent, and each status the one it sent it with.
 */

export const csrfToken: RecordedReply = {
  status: 200,
  body: '{"csrf_token":"csrf.session:77209314d4dc36bbfa59c5df148fe4984112612609b8403bfa5af30c154afc7e.atCoRQ.j9rbTRbQWRry7BfbsBR0422PMyo"}',
};

export const noConnections: RecordedReply = { status: 200, body: "[]" };

const DELUNO_LANES =
  '"lanes":[{"lane":"missing","enabled":false,"max_items_per_run":50,"retry_delay_minutes":1440,"schedule_enabled":false,"schedule_days":"","schedule_start":"00:00","schedule_end":"23:59","schedule_interval_seconds":3600},{"lane":"upgrade","enabled":false,"max_items_per_run":50,"retry_delay_minutes":1440,"schedule_enabled":false,"schedule_days":"","schedule_start":"00:00","schedule_end":"23:59","schedule_interval_seconds":3600}]';

const delunoWithoutSecret =
  '{"id":1,"kind":"deluno","name":"Deluno on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:5099","api_key_is_saved":true,"webhook_secret_is_set":false,"webhook_url_path":"/api/v1/intake/webhook/deluno","unsigned_webhook_warning":"This connection accepts webhooks without a secret. Create a secret and add it to Deluno on localhost.","last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null,"downloaded_scan_enabled":false,' +
  DELUNO_LANES +
  "}";

const delunoWithSecret =
  '{"id":1,"kind":"deluno","name":"Deluno on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:5099","api_key_is_saved":true,"webhook_secret_is_set":true,"webhook_url_path":"/api/v1/intake/webhook/deluno","unsigned_webhook_warning":null,"last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null,"downloaded_scan_enabled":false,' +
  DELUNO_LANES +
  "}";

export const delunoAdded: RecordedReply = {
  status: 201,
  body: delunoWithoutSecret,
};

export const delunoListedWithoutSecret: RecordedReply = {
  status: 200,
  body: `[${delunoWithoutSecret}]`,
};

export const delunoListedWithSecret: RecordedReply = {
  status: 200,
  body: `[${delunoWithSecret}]`,
};

/** The one time the server shows a webhook secret. */
export const WEBHOOK_SECRET = "qaQljO_ViwrdESLfgQzVkcs5UMkEMwg3niJwY-5gT5E";

export const delunoSecretCreated: RecordedReply = {
  status: 200,
  body: `{"connection_id":1,"webhook_secret":"${WEBHOOK_SECRET}","webhook_url_path":"/api/v1/intake/webhook/deluno","header_name":"X-Webhook-Secret"}`,
};

export const sabnzbdAdded: RecordedReply = {
  status: 201,
  body: '{"id":1,"kind":"sabnzbd","name":"SABnzbd on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:8085","username":null,"password_is_saved":false,"api_key_is_saved":true,"last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null}',
};

export const sabnzbdListed: RecordedReply = {
  status: 200,
  body: `[${sabnzbdAdded.body}]`,
};
