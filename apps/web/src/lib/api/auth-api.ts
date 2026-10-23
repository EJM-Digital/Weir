import { markSignedIn } from "../auth/signed-in-before";
import { apiFetch, readJson, requireOk } from "./client";
import type { AppTheme } from "../ui/app-theme";
import type {
  ActiveSession,
  BootstrapResult,
  BootstrapStatus,
  CurrentSession,
  SessionAction,
  UserPublic,
} from "./types";

export async function fetchCsrfToken(): Promise<string> {
  const path = "/api/v1/auth/csrf";
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not start a secure session");
  const data = await readJson<{ csrf_token: string }>(r);
  return data.csrf_token;
}

export async function fetchMe(): Promise<UserPublic | null> {
  const path = "/api/v1/auth/me";
  const r = await apiFetch(path);
  if (r.status === 401) {
    return null;
  }
  await requireOk(path, r, "Could not load the current user");
  const data = await readJson<{ user: UserPublic }>(r);
  markSignedIn();
  return data.user;
}

export async function fetchCurrentSession(): Promise<CurrentSession | null> {
  const path = "/api/v1/auth/session";
  const r = await apiFetch(path);
  if (r.status === 401) {
    return null;
  }
  await requireOk(path, r, "Could not load the current session");
  return readJson<CurrentSession>(r);
}

export async function fetchActiveSessions(): Promise<ActiveSession[]> {
  const path = "/api/v1/auth/sessions";
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load active sessions");
  const data = await readJson<{ items: ActiveSession[] }>(r);
  return data.items;
}

async function postSessionAction(path: string): Promise<SessionAction> {
  const csrf = await fetchCsrfToken();
  const r = await apiFetch(path, {
    method: "POST",
    headers: { "X-CSRF-Token": csrf },
  });
  await requireOk(path, r, "Could not update sessions");
  return readJson<SessionAction>(r);
}

export function postRevokeOtherSessions(): Promise<SessionAction> {
  return postSessionAction("/api/v1/auth/sessions/revoke-others");
}

export function postRevokeSession(sessionId: string): Promise<SessionAction> {
  return postSessionAction(
    `/api/v1/auth/sessions/${encodeURIComponent(sessionId)}/revoke`,
  );
}

export async function fetchBootstrapStatus(): Promise<BootstrapStatus> {
  const path = "/api/v1/auth/bootstrap/status";
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not check first-run setup");
  return readJson<BootstrapStatus>(r);
}

export type LoginResult = { user: UserPublic };

export async function postLogin(
  username: string,
  password: string,
  trustedDevice: boolean,
): Promise<LoginResult> {
  const csrf_token = await fetchCsrfToken();
  const path = "/api/v1/auth/login";
  const r = await apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      username,
      password,
      csrf_token,
      trusted_device: trustedDevice,
    }),
  });
  await requireOk(path, r, "Could not sign in");
  return readJson<LoginResult>(r);
}

export async function postLogout(): Promise<void> {
  const csrf = await fetchCsrfToken();
  const path = "/api/v1/auth/logout";
  const r = await apiFetch(path, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-CSRF-Token": csrf,
    },
    body: JSON.stringify({ csrf_token: csrf }),
  });
  if (!r.ok && r.status !== 204) {
    await requireOk(path, r, "Could not sign out");
  }
}

export async function postBootstrap(
  username: string,
  password: string,
  setupCode?: string,
): Promise<BootstrapResult> {
  const csrf_token = await fetchCsrfToken();
  const path = "/api/v1/auth/bootstrap";
  const r = await apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      username,
      password,
      csrf_token,
      ...(setupCode ? { setup_code: setupCode } : {}),
    }),
  });
  await requireOk(path, r, "Could not create the first user");
  return readJson(r);
}

export async function postChangePassword(
  currentPassword: string,
  newPassword: string,
): Promise<{ message: string }> {
  const csrf_token = await fetchCsrfToken();
  const path = "/api/v1/auth/change-password";
  const r = await apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      csrf_token,
      current_password: currentPassword,
      new_password: newPassword,
    }),
  });
  await requireOk(path, r, "Could not change the password");
  return readJson(r);
}

export async function postSetTheme(
  theme: AppTheme,
): Promise<{ message: string; app_theme: AppTheme }> {
  const csrf_token = await fetchCsrfToken();
  const path = "/api/v1/auth/theme";
  const r = await apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ csrf_token, theme }),
  });
  await requireOk(path, r, "Could not save the theme");
  return readJson(r);
}

export async function postChangeUsername(
  currentPassword: string,
  newUsername: string,
): Promise<{ message: string; username: string }> {
  const csrf_token = await fetchCsrfToken();
  const path = "/api/v1/auth/change-username";
  const r = await apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      csrf_token,
      current_password: currentPassword,
      new_username: newUsername,
    }),
  });
  await requireOk(path, r, "Could not change the username");
  return readJson(r);
}
