import type { RecordedReply } from "../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests the first-run account page makes, kept word for word. Each body is
 * the text the server sent, and each status the one it sent it with.
 */

export const csrfToken: RecordedReply = {
  status: 200,
  body: '{"csrf_token":"csrf.anonymous.atCoMQ.MtgwR55oCZyT9D3Ku7boJtmrTp8"}',
};

export const bootstrapAllowed: RecordedReply = {
  status: 200,
  body: '{"bootstrap_allowed":true,"reason":"no_admin_user","requires_setup_code":false}',
};

export const noOneSignedIn: RecordedReply = {
  status: 401,
  body: '{"detail":"Not authenticated."}',
};

export const accountCreated: RecordedReply = {
  status: 200,
  body: '{"message":"Account created. You are signed in.","username":"recorded-admin","user":{"id":1,"username":"recorded-admin","role":"admin","app_theme":null}}',
};

export const accountRefused: RecordedReply = {
  status: 403,
  body: '{"detail":"Bootstrap is not available: an admin user already exists."}',
};
