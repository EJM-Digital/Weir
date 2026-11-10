import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type MockInstance,
} from "vitest";

import { activityKeys } from "../activity/query-keys";
import { jsonAnswer, type StubbedApi } from "../../test/stub-weir-api";
import { CSRF_TOKEN, stubWire, theRequest } from "../../test/wire";
import { authKeys } from "./query-keys";
import {
  useActiveSessionsQuery,
  useBootstrapMutation,
  useBootstrapStatusQuery,
  useChangePasswordMutation,
  useChangeUsernameMutation,
  useConfirmRecoveryCodesMutation,
  useCurrentSessionQuery,
  useLoginMutation,
  useLogoutMutation,
  useMeQuery,
  useNewRecoveryCodesMutation,
  useRecoveryCodeMutation,
  useRecoveryCodesQuery,
  useRecoveryPasswordMutation,
  useRevokeOtherSessionsMutation,
  useRevokeSessionMutation,
} from "./queries";
import { sessionWasNotKept, markLoginSucceeded } from "./session-kept";
import {
  clearSignedIn,
  finishChangingPassword,
  signedOutPath,
  wasSignedIn,
} from "./signed-in-before";

const USER = { id: 1, username: "alice", role: "admin" };

let client: QueryClient;
let invalidate: MockInstance<QueryClient["invalidateQueries"]>;

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  client = new QueryClient({
    defaultOptions: { queries: { retryDelay: 0 } },
  });
  invalidate = vi.spyOn(client, "invalidateQueries");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.sessionStorage.clear();
  clearSignedIn();
  finishChangingPassword();
});

/** The query keys invalidated so far, in the order asked. */
function invalidatedKeys(): unknown[] {
  return invalidate.mock.calls.map(
    (call) => (call[0] as { queryKey: unknown }).queryKey,
  );
}

function requestsTo(api: StubbedApi, route: string): number {
  return api.to(route).length;
}

describe("the read hooks", () => {
  it("asks who is signed in once, and keeps the answer under the me key", async () => {
    const api = stubWire({ "GET /api/v1/auth/me": { user: USER } });

    const { result } = renderHook(() => useMeQuery(), { wrapper });

    await waitFor(() => expect(result.current.data).toEqual(USER));
    expect(client.getQueryData(authKeys.me)).toEqual(USER);
    expect(requestsTo(api, "GET /api/v1/auth/me")).toBe(1);
  });

  it("does not ask again when who is signed in cannot be had", async () => {
    const api = stubWire({
      "GET /api/v1/auth/me": () => jsonAnswer({ detail: "x" }, 500),
    });

    const { result } = renderHook(() => useMeQuery(), { wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(requestsTo(api, "GET /api/v1/auth/me")).toBe(1);
  });

  it("asks whether the first account can be made, and tries once more on failure", async () => {
    const api = stubWire({
      "GET /api/v1/auth/bootstrap/status": () =>
        jsonAnswer({ detail: "x" }, 500),
    });

    const { result } = renderHook(() => useBootstrapStatusQuery(), {
      wrapper,
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(requestsTo(api, "GET /api/v1/auth/bootstrap/status")).toBe(2);
    expect(client.getQueryState(authKeys.bootstrap)?.status).toBe("error");
  });

  it("reads the current session under the session key and does not retry", async () => {
    const api = stubWire({
      "GET /api/v1/auth/session": () => jsonAnswer({ detail: "x" }, 500),
    });

    const { result } = renderHook(() => useCurrentSessionQuery(), { wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(requestsTo(api, "GET /api/v1/auth/session")).toBe(1);
    expect(client.getQueryState(authKeys.session)?.status).toBe("error");
  });

  it("lists the active sessions under the sessions key, without retrying, unless switched off", async () => {
    const api = stubWire({
      "GET /api/v1/auth/sessions": () => jsonAnswer({ detail: "x" }, 500),
    });

    const on = renderHook(() => useActiveSessionsQuery(), { wrapper });
    await waitFor(() => expect(on.result.current.isError).toBe(true));
    expect(requestsTo(api, "GET /api/v1/auth/sessions")).toBe(1);
    expect(client.getQueryState(authKeys.sessions)?.status).toBe("error");

    const other = new QueryClient();
    const off = renderHook(() => useActiveSessionsQuery(false), {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={other}>{children}</QueryClientProvider>
      ),
    });
    expect(off.result.current.fetchStatus).toBe("idle");
    expect(requestsTo(api, "GET /api/v1/auth/sessions")).toBe(1);
  });

  it("reads the recovery codes under their key, without retrying, unless switched off", async () => {
    const api = stubWire({
      "GET /api/v1/auth/recovery-codes": () => jsonAnswer({ detail: "x" }, 500),
    });

    const on = renderHook(() => useRecoveryCodesQuery(), { wrapper });
    await waitFor(() => expect(on.result.current.isError).toBe(true));
    expect(requestsTo(api, "GET /api/v1/auth/recovery-codes")).toBe(1);
    expect(client.getQueryState(authKeys.recoveryCodes)?.status).toBe("error");

    const other = new QueryClient();
    const off = renderHook(() => useRecoveryCodesQuery(false), {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={other}>{children}</QueryClientProvider>
      ),
    });
    expect(off.result.current.fetchStatus).toBe("idle");
    expect(requestsTo(api, "GET /api/v1/auth/recovery-codes")).toBe(1);
  });
});

describe("ending sessions", () => {
  it("ends the other sessions, then refreshes the list and the activity", async () => {
    const api = stubWire({
      "POST /api/v1/auth/sessions/revoke-others": { revoked: 2 },
    });
    const { result } = renderHook(() => useRevokeOtherSessionsMutation(), {
      wrapper,
    });

    await act(() => result.current.mutateAsync());

    expect(theRequest(api).path).toBe("/api/v1/auth/sessions/revoke-others");
    expect(invalidatedKeys()).toEqual([authKeys.sessions, activityKeys.recent]);
  });

  it("ends the one session asked for, then refreshes the list and the activity", async () => {
    const api = stubWire({
      "POST /api/v1/auth/sessions/s-1/revoke": { revoked: 1 },
    });
    const { result } = renderHook(() => useRevokeSessionMutation(), {
      wrapper,
    });

    await act(() => result.current.mutateAsync("s-1"));

    expect(theRequest(api).path).toBe("/api/v1/auth/sessions/s-1/revoke");
    expect(invalidatedKeys()).toEqual([authKeys.sessions, activityKeys.recent]);
  });
});

describe("signing in", () => {
  const login = () => renderHook(() => useLoginMutation(), { wrapper }).result;

  it("sends what was typed, remembers the sign-in, and puts the user in the cache", async () => {
    const api = stubWire({ "POST /api/v1/auth/login": { user: USER } });
    const result = login();

    await act(() =>
      result.current.mutateAsync({
        username: "alice",
        password: "pw",
        trustedDevice: true,
      }),
    );

    expect(theRequest(api).body).toEqual({
      username: "alice",
      password: "pw",
      trusted_device: true,
      csrf_token: CSRF_TOKEN,
    });
    expect(sessionWasNotKept(Date.now() + 1)).toBe(true);
    expect(wasSignedIn()).toBe(true);
    expect(client.getQueryData(authKeys.me)).toEqual(USER);
    expect(invalidatedKeys()).toEqual([
      authKeys.bootstrap,
      authKeys.session,
      authKeys.recoveryCodes,
      activityKeys.recent,
    ]);
  });

  it("ends the password-change state, so a later expiry is not blamed on a change", async () => {
    stubWire({ "POST /api/v1/auth/login": { user: USER } });
    const changing = renderHook(() => useChangePasswordMutation(), {
      wrapper,
    });
    stubWire({
      "POST /api/v1/auth/change-password": () => new Promise(() => undefined),
    });
    act(() => {
      changing.result.current.mutate({
        currentPassword: "a",
        newPassword: "b",
      });
    });
    await waitFor(() =>
      expect(signedOutPath()).toBe("/login?password=changed"),
    );
    stubWire({ "POST /api/v1/auth/login": { user: USER } });
    const result = login();

    await act(() =>
      result.current.mutateAsync({
        username: "alice",
        password: "pw",
        trustedDevice: false,
      }),
    );

    expect(signedOutPath()).toBe("/login?session=expired");
  });
});

describe("creating the first account", () => {
  it("sends the setup code, signs the account in and puts the user in the cache", async () => {
    const api = stubWire({ "POST /api/v1/auth/bootstrap": { user: USER } });
    const { result } = renderHook(() => useBootstrapMutation(), { wrapper });

    await act(() =>
      result.current.mutateAsync({
        username: "alice",
        password: "pw",
        setupCode: "ABCD-1234",
      }),
    );

    expect(theRequest(api).body).toEqual({
      username: "alice",
      password: "pw",
      setup_code: "ABCD-1234",
      csrf_token: CSRF_TOKEN,
    });
    expect(sessionWasNotKept(Date.now() + 1)).toBe(true);
    expect(wasSignedIn()).toBe(true);
    expect(client.getQueryData(authKeys.me)).toEqual(USER);
    expect(invalidatedKeys()).toEqual([
      authKeys.bootstrap,
      authKeys.session,
      activityKeys.recent,
    ]);
  });
});

describe("signing out", () => {
  it("shows nobody signed in before the request goes out, and refreshes everything after", async () => {
    client.setQueryData(authKeys.me, USER);
    client.setQueryData(authKeys.session, { id: "s" });
    let meWhenSent: unknown = "unset";
    let sessionWhenSent: unknown = "unset";
    stubWire({
      "POST /api/v1/auth/logout": () => {
        meWhenSent = client.getQueryData(authKeys.me);
        sessionWhenSent = client.getQueryData(authKeys.session);
        return new Response(null, { status: 204 });
      },
    });
    const cancel = vi.spyOn(client, "cancelQueries");
    const { result } = renderHook(() => useLogoutMutation(), { wrapper });

    await act(() => result.current.mutateAsync());

    expect(meWhenSent).toBeNull();
    expect(sessionWhenSent).toBeNull();
    expect(cancel.mock.calls.map((c) => c[0])).toEqual([
      { queryKey: authKeys.me },
      { queryKey: authKeys.session },
    ]);
    expect(invalidatedKeys()).toEqual([
      authKeys.me,
      authKeys.session,
      authKeys.bootstrap,
      activityKeys.recent,
    ]);
  });
});

describe("changing the username", () => {
  it("sends the password and the new name, then refreshes who is signed in and the activity", async () => {
    const api = stubWire({
      "POST /api/v1/auth/change-username": { message: "ok", username: "bob" },
    });
    const { result } = renderHook(() => useChangeUsernameMutation(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({ currentPassword: "pw", newUsername: "bob" }),
    );

    expect(theRequest(api).body).toEqual({
      current_password: "pw",
      new_username: "bob",
      csrf_token: CSRF_TOKEN,
    });
    expect(invalidatedKeys()).toEqual([authKeys.me, activityKeys.recent]);
  });
});

describe("changing the password", () => {
  it("sends both passwords", async () => {
    const api = stubWire({
      "POST /api/v1/auth/change-password": { message: "ok" },
    });
    const { result } = renderHook(() => useChangePasswordMutation(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({ currentPassword: "a", newPassword: "b" }),
    );

    expect(theRequest(api).body).toEqual({
      current_password: "a",
      new_password: "b",
      csrf_token: CSRF_TOKEN,
    });
  });

  it("forgets a sign-in kept moments ago and says the password changed while it happens", async () => {
    markLoginSucceeded();
    stubWire({
      "POST /api/v1/auth/change-password": () => new Promise(() => undefined),
    });
    const { result } = renderHook(() => useChangePasswordMutation(), {
      wrapper,
    });

    act(() => {
      result.current.mutate({ currentPassword: "a", newPassword: "b" });
    });

    await waitFor(() =>
      expect(signedOutPath()).toBe("/login?password=changed"),
    );
    expect(sessionWasNotKept()).toBe(false);
  });

  it("stops saying the password changed when the change fails", async () => {
    stubWire({
      "POST /api/v1/auth/change-password": () =>
        jsonAnswer({ detail: "Wrong password." }, 403),
    });
    const { result } = renderHook(() => useChangePasswordMutation(), {
      wrapper,
    });

    await act(async () => {
      await result.current
        .mutateAsync({ currentPassword: "a", newPassword: "b" })
        .catch(() => undefined);
    });

    expect(signedOutPath()).toBe("/login");
  });

  it("shows nobody signed in once it goes through", async () => {
    client.setQueryData(authKeys.me, USER);
    client.setQueryData(authKeys.session, { id: "s" });
    stubWire({ "POST /api/v1/auth/change-password": { message: "ok" } });
    const { result } = renderHook(() => useChangePasswordMutation(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({ currentPassword: "a", newPassword: "b" }),
    );

    expect(client.getQueryData(authKeys.me)).toBeNull();
    expect(client.getQueryData(authKeys.session)).toBeNull();
  });
});

describe("recovery codes", () => {
  it("keeps only the count and the saved state of a new set, never the codes", async () => {
    const api = stubWire({
      "POST /api/v1/auth/recovery-codes": {
        codes: ["AAAA-1111"],
        remaining: 10,
        confirmed: false,
      },
    });
    const { result } = renderHook(() => useNewRecoveryCodesMutation(), {
      wrapper,
    });

    await act(() => result.current.mutateAsync("pw"));

    expect(theRequest(api).body).toEqual({
      current_password: "pw",
      csrf_token: CSRF_TOKEN,
    });
    expect(client.getQueryData(authKeys.recoveryCodes)).toStrictEqual({
      remaining: 10,
      confirmed: false,
    });
  });

  it("keeps only the count and the saved state once the set is confirmed", async () => {
    const api = stubWire({
      "POST /api/v1/auth/recovery-codes/confirm": {
        remaining: 10,
        confirmed: true,
        extra: "x",
      },
    });
    const { result } = renderHook(() => useConfirmRecoveryCodesMutation(), {
      wrapper,
    });

    await act(() => result.current.mutateAsync());

    expect(theRequest(api).path).toBe("/api/v1/auth/recovery-codes/confirm");
    expect(client.getQueryData(authKeys.recoveryCodes)).toStrictEqual({
      remaining: 10,
      confirmed: true,
    });
  });

  it("spends a code and returns what the server answers", async () => {
    const answer = { username: "alice", recovery_token: "tok" };
    const api = stubWire({ "POST /api/v1/auth/recovery/code": answer });
    const { result } = renderHook(() => useRecoveryCodeMutation(), {
      wrapper,
    });

    await act(async () => {
      await expect(result.current.mutateAsync("ABCD-1234")).resolves.toEqual(
        answer,
      );
    });

    expect(theRequest(api).body).toEqual({
      code: "ABCD-1234",
      csrf_token: CSRF_TOKEN,
    });
  });

  it("sets the new password with the token and returns the answer", async () => {
    const api = stubWire({
      "POST /api/v1/auth/recovery/password": { message: "done" },
    });
    const { result } = renderHook(() => useRecoveryPasswordMutation(), {
      wrapper,
    });

    await act(async () => {
      await expect(
        result.current.mutateAsync({ recoveryToken: "tok", newPassword: "n" }),
      ).resolves.toEqual({ message: "done" });
    });

    expect(theRequest(api).body).toEqual({
      recovery_token: "tok",
      new_password: "n",
      csrf_token: CSRF_TOKEN,
    });
  });
});
