import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests System › Security makes, kept word for word. Each body is the text
 * the server sent, and each status the one it sent it with.
 */

export const csrfToken: RecordedReply = {
  status: 200,
  body: '{"csrf_token":"csrf.anonymous.atBNYQ.TcHTRYKKC8sTV8BMqob2iGMgIXY"}',
};

export const me: RecordedReply = {
  status: 200,
  body: '{"user":{"id":1,"username":"recorded-admin","role":"admin","app_theme":null}}',
};

export const meRenamed: RecordedReply = {
  status: 200,
  body: '{"user":{"id":1,"username":"renamed-admin","role":"admin","app_theme":null}}',
};

export const meAfterPasswordChange: RecordedReply = {
  status: 401,
  body: '{"detail":"Not authenticated."}',
};

export const currentSession: RecordedReply = {
  status: 200,
  body: '{"session_id":"eaa2d170-0a7a-4499-84d0-97a4f35174c3","client_label":"Browser on device","current":true,"trusted_device":false,"created_at":"2026-10-15T03:26:48.739147","last_seen_at":"2026-10-15T03:26:48.739147","absolute_expires_at":"2027-01-13T03:26:48.739147","idle_timeout_minutes":20160,"absolute_timeout_days":90}',
};

export const threeSessions: RecordedReply = {
  status: 200,
  body: '{"items":[{"session_id":"568e9f8e-6d2b-4b91-a594-e6c5e7eb2f65","client_label":"Browser on device","current":false,"trusted_device":false,"created_at":"2026-10-15T03:26:52.148659","last_seen_at":"2026-10-15T03:26:52.148659","absolute_expires_at":"2027-01-13T03:26:52.148659","idle_timeout_minutes":20160,"absolute_timeout_days":90},{"session_id":"e23c5eeb-7aa8-470e-bef3-5058a3adc385","client_label":"Browser on device","current":false,"trusted_device":false,"created_at":"2026-10-15T03:26:50.622582","last_seen_at":"2026-10-15T03:26:50.622582","absolute_expires_at":"2027-01-13T03:26:50.622582","idle_timeout_minutes":20160,"absolute_timeout_days":90},{"session_id":"eaa2d170-0a7a-4499-84d0-97a4f35174c3","client_label":"Browser on device","current":true,"trusted_device":false,"created_at":"2026-10-15T03:26:48.739147","last_seen_at":"2026-10-15T03:26:48.739147","absolute_expires_at":"2027-01-13T03:26:48.739147","idle_timeout_minutes":20160,"absolute_timeout_days":90}]}',
};

export const twoSessions: RecordedReply = {
  status: 200,
  body: '{"items":[{"session_id":"e23c5eeb-7aa8-470e-bef3-5058a3adc385","client_label":"Browser on device","current":false,"trusted_device":false,"created_at":"2026-10-15T03:26:50.622582","last_seen_at":"2026-10-15T03:26:50.622582","absolute_expires_at":"2027-01-13T03:26:50.622582","idle_timeout_minutes":20160,"absolute_timeout_days":90},{"session_id":"eaa2d170-0a7a-4499-84d0-97a4f35174c3","client_label":"Browser on device","current":true,"trusted_device":false,"created_at":"2026-10-15T03:26:48.739147","last_seen_at":"2026-10-15T03:26:48.739147","absolute_expires_at":"2027-01-13T03:26:48.739147","idle_timeout_minutes":20160,"absolute_timeout_days":90}]}',
};

export const oneSession: RecordedReply = {
  status: 200,
  body: '{"items":[{"session_id":"eaa2d170-0a7a-4499-84d0-97a4f35174c3","client_label":"Browser on device","current":true,"trusted_device":false,"created_at":"2026-10-15T03:26:48.739147","last_seen_at":"2026-10-15T03:26:48.739147","absolute_expires_at":"2027-01-13T03:26:48.739147","idle_timeout_minutes":20160,"absolute_timeout_days":90}]}',
};

export const notAuthenticated: RecordedReply = {
  status: 401,
  body: '{"detail":"Not authenticated."}',
};

export const sessionSignedOut: RecordedReply = {
  status: 200,
  body: '{"message":"Session signed out.","revoked_count":1}',
};

export const sessionAlreadyInactive: RecordedReply = {
  status: 404,
  body: '{"detail":"Session is no longer active."}',
};

export const otherSessionsSignedOut: RecordedReply = {
  status: 200,
  body: '{"message":"Signed out 1 other session.","revoked_count":1}',
};

export const noOtherSessionsToSignOut: RecordedReply = {
  status: 200,
  body: '{"message":"Signed out 0 other sessions.","revoked_count":0}',
};

export const confirmationExpired: RecordedReply = {
  status: 400,
  body: '{"detail":"Your confirmation token expired. Refresh the page and try again."}',
};

export const passwordWrongCurrent: RecordedReply = {
  status: 400,
  body: '{"detail":"Current password is incorrect."}',
};

export const passwordUnchanged: RecordedReply = {
  status: 400,
  body: '{"detail":"New password must be different from the current password."}',
};

export const passwordTooShort: RecordedReply = {
  status: 422,
  body: '{"detail":[{"type":"string_too_short","loc":["body","new_password"],"msg":"String should have at least 8 characters","input":"short1!","ctx":{"min_length":8}}]}',
};

export const passwordTooCommon: RecordedReply = {
  status: 400,
  body: '{"detail":"Password is too common. Choose a stronger password."}',
};

export const passwordContainsUsername: RecordedReply = {
  status: 400,
  body: '{"detail":"Password must not contain the username."}',
};

export const passwordNarrowMix: RecordedReply = {
  status: 400,
  body: '{"detail":"Password must use a wider mix of characters."}',
};

export const passwordChanged: RecordedReply = {
  status: 200,
  body: '{"message":"Password changed. Sign in again with your new password."}',
};

export const usernameWrongPassword: RecordedReply = {
  status: 400,
  body: '{"detail":"Current password is incorrect."}',
};

export const usernameUnchanged: RecordedReply = {
  status: 400,
  body: '{"detail":"New username must be different from the current username."}',
};

export const usernameChanged: RecordedReply = {
  status: 200,
  body: '{"message":"Username changed. Use it the next time you sign in.","username":"renamed-admin"}',
};

export const securityOverview: RecordedReply = {
  status: 200,
  body: '{"session_signing_configured":true,"sign_in_cookie_https_mode":"auto","sign_in_cookie_https_plain":"Matched to each connection — on over HTTPS, off over plain HTTP on your network.","sign_in_cookie_same_site":"Lax (recommended for most setups)","standard_session_idle_timeout_plain":"14 days","standard_session_absolute_timeout_plain":"90 days","trusted_session_idle_timeout_plain":"60 days","trusted_session_absolute_timeout_plain":"365 days","extra_https_hardening_enabled":false,"sign_in_attempt_limit":10,"sign_in_attempt_window_plain":"1 minute","first_time_setup_attempt_limit":10,"first_time_setup_attempt_window_plain":"1 hour","allowed_browser_origins_count":0,"restart_required_note":"These safety options are read when the app starts from the server configuration file. To change them, ask whoever runs the server to edit that file and restart the app."}',
};

export const appSettings: RecordedReply = {
  status: 200,
  body: '{"setup_wizard_state":"pending","app_timezone":"UTC","log_retention_days":30,"activity_retention_days":90,"configuration_backup_enabled":false,"configuration_backup_interval_hours":24,"configuration_backup_preferred_time":"02:00","configuration_backup_last_run_at":null,"updated_at":"2026-10-15T03:26:11"}',
};

export const homeNetworkOff: RecordedReply = {
  status: 200,
  body: '{"enabled":false,"extra_networks":[],"allow_list":[],"warning":null}',
};

export const homeNetworkOn: RecordedReply = {
  status: 200,
  body: '{"enabled":true,"extra_networks":["100.64.0.0/10"],"allow_list":[],"warning":null}',
};

export const homeNetworkWrongPassword: RecordedReply = {
  status: 400,
  body: '{"detail":"Current password is incorrect."}',
};

export const homeNetworkBadEntry: RecordedReply = {
  status: 400,
  body: '{"detail":"\'banana\' is not a valid address or subnet. Use an IP address or a CIDR range such as 192.168.1.0/28."}',
};

export const bootstrapStatusAfterSetup: RecordedReply = {
  status: 200,
  body: '{"bootstrap_allowed":false,"reason":"admin_already_exists","requires_setup_code":false}',
};
