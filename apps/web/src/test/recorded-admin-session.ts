import type { RecordedReply } from "./recorded-weir";

/** Replies a running Weir server gave to a signed-in administrator's session checks, kept word for word. */

export const csrfToken: RecordedReply = {
  status: 200,
  body: '{"csrf_token":"csrf.session:4030a977bfa91b81525ed81ec452f7bbad57062455c9a3fb53e30cd8d5f32ee9.atCoXw.J9HLGE2poD_TOZ_ocQZZF_GvM5Y"}',
};

export const adminMe: RecordedReply = {
  status: 200,
  body: '{"user":{"id":1,"username":"recorded-admin","role":"admin","app_theme":null}}',
};
