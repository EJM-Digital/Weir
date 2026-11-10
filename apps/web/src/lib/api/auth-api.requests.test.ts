import { afterEach, describe, expect, it, vi } from "vitest";

import { recordedBody } from "../../test/recorded";
import {
  CSRF_TOKEN,
  expectChange,
  expectChangeWithHeader,
  expectRead,
  stubWire,
  theRequest,
} from "../../test/wire";
import { jsonAnswer, unexpectedProblem } from "../../test/stub-weir-api";
import {
  fetchActiveSessions,
  fetchBootstrapStatus,
  fetchCsrfToken,
  fetchCurrentSession,
  fetchMe,
  fetchRecoveryCodes,
  postBootstrap,
  postChangePassword,
  postChangeUsername,
  postConfirmRecoveryCodes,
  postLogin,
  postLogout,
  postNewRecoveryCodes,
  postRecoveryCode,
  postRecoveryPassword,
  postRevokeOtherSessions,
  postRevokeSession,
  postSetTheme,
} from "./auth-api";
import { ApiHttpError } from "./client";
import type { UserPublic } from "./types";

/**
 * Every request the sign-in, sign-out and account screens make, judged at `fetch`: the method, the address and, for each
 * change of state, the token Weir asked for first. The server's own words are kept in the recordings of the screens that
 * show them; these judge what is sent.
 */

const ME = "GET /api/v1/auth/me";
const user = recordedBody<{ user: UserPublic }>(ME).user;

afterEach(() => vi.unstubAllGlobals());

describe("a change of state asks for a token first and sends it back", () => {
  it("signs in with the token in the body", async () => {
    const api = stubWire({ "POST /api/v1/auth/login": { user } });

    const result = await postLogin("alice", "a long passphrase", true);

    expectChange(api, "POST", "/api/v1/auth/login", {
      username: "alice",
      password: "a long passphrase",
      trusted_device: true,
    });
    expect(result.user).toEqual(user);
    expect(api.requests[0]).toMatchObject({
      method: "GET",
      path: "/api/v1/auth/csrf",
    });
  });

  it("signs out with the token in the body and in the header", async () => {
    const api = stubWire({
      "POST /api/v1/auth/logout": new Response(null, { status: 204 }),
    });

    await postLogout();

    const request = theRequest(api);
    expect(request.body).toEqual({ csrf_token: CSRF_TOKEN });
    expect(request.headers["x-csrf-token"]).toBe(CSRF_TOKEN);
  });

  it("sends the sign-out so that it outlives the page, after the caller's hook", async () => {
    const order: string[] = [];
    const fetchSpy = vi
      .fn()
      .mockImplementation((url: string, init?: RequestInit) => {
        if (String(url).endsWith("/auth/csrf")) {
          order.push("csrf");
          return Promise.resolve(
            new Response(JSON.stringify({ csrf_token: CSRF_TOKEN }), {
              status: 200,
            }),
          );
        }
        order.push(`logout keepalive=${String(init?.keepalive)}`);
        return Promise.resolve(new Response(null, { status: 204 }));
      });
    vi.stubGlobal("fetch", fetchSpy);

    await postLogout(async () => {
      order.push("before send");
    });

    expect(order).toEqual(["csrf", "before send", "logout keepalive=true"]);
    vi.unstubAllGlobals();
  });

  it("creates the first account with the token in the body, and the setup code only when there is one", async () => {
    const api = stubWire({ "POST /api/v1/auth/bootstrap": { user } });

    await postBootstrap("alice", "a long passphrase", "ABCD-1234");
    expectChange(api, "POST", "/api/v1/auth/bootstrap", {
      username: "alice",
      password: "a long passphrase",
      setup_code: "ABCD-1234",
    });
    vi.unstubAllGlobals();

    const without = stubWire({ "POST /api/v1/auth/bootstrap": { user } });
    await postBootstrap("alice", "a long passphrase");
    expectChange(without, "POST", "/api/v1/auth/bootstrap", {
      username: "alice",
      password: "a long passphrase",
    });
  });

  it("changes the password with the token in the body", async () => {
    const api = stubWire({
      "POST /api/v1/auth/change-password": { message: "Password changed." },
    });

    await postChangePassword("the old one", "the new one");

    expectChange(api, "POST", "/api/v1/auth/change-password", {
      current_password: "the old one",
      new_password: "the new one",
    });
  });

  it("changes the username with the token in the body", async () => {
    const api = stubWire({
      "POST /api/v1/auth/change-username": {
        message: "Username changed.",
        username: "bob",
      },
    });

    await postChangeUsername("the password", "bob");

    expectChange(api, "POST", "/api/v1/auth/change-username", {
      current_password: "the password",
      new_username: "bob",
    });
  });

  it("saves the theme with the token in the body", async () => {
    const api = stubWire({
      "POST /api/v1/auth/theme": { message: "Saved.", app_theme: "dark" },
    });

    await postSetTheme("dark");

    expectChange(api, "POST", "/api/v1/auth/theme", { theme: "dark" });
  });

  it("ends the other sessions with the token in a header and no body", async () => {
    const api = stubWire({
      "POST /api/v1/auth/sessions/revoke-others": { revoked: 2 },
    });

    await postRevokeOtherSessions();

    expectChangeWithHeader(api, "POST", "/api/v1/auth/sessions/revoke-others");
  });

  it("ends one session by its id, written safely into the address, with the token in a header", async () => {
    const api = stubWire({
      "POST /api/v1/auth/sessions/a%2Fb%20c/revoke": { revoked: 1 },
    });

    await postRevokeSession("a/b c");

    expectChangeWithHeader(
      api,
      "POST",
      "/api/v1/auth/sessions/a%2Fb%20c/revoke",
    );
  });

  it("sends nothing when the token cannot be had", async () => {
    const api = stubWire({
      "GET /api/v1/auth/csrf": jsonAnswer({ detail: "No." }, 500),
      "POST /api/v1/auth/theme": { message: "Saved.", app_theme: "dark" },
    });

    await expect(postSetTheme("dark")).rejects.toThrow();

    expect(api.to("POST /api/v1/auth/theme")).toEqual([]);
  });
});

describe("a read sends no token and no body", () => {
  it("asks who is signed in, and answers nobody for a 401", async () => {
    const api = stubWire({ [ME]: jsonAnswer({ detail: "No." }, 401) });

    expect(await fetchMe()).toBeNull();

    expectRead(api, "/api/v1/auth/me");
  });

  it("asks whether the first account can still be made", async () => {
    const api = stubWire({
      "GET /api/v1/auth/bootstrap/status": {
        bootstrap_allowed: false,
        reason: "admin_already_exists",
        requires_setup_code: false,
      },
    });

    await fetchBootstrapStatus();

    expectRead(api, "/api/v1/auth/bootstrap/status");
  });
});

const refused = unexpectedProblem;

/** Every call that asks for a token and then changes state with a JSON body. */
const JSON_CHANGES: {
  name: string;
  call: () => Promise<unknown>;
  path: string;
  body?: object;
  failure: string;
}[] = [
  {
    name: "sign in",
    call: () => postLogin("alice", "pw", false),
    path: "/api/v1/auth/login",
    failure: "Could not sign in.",
  },
  {
    name: "create the first user",
    call: () => postBootstrap("alice", "pw"),
    path: "/api/v1/auth/bootstrap",
    failure: "Could not create the first user.",
  },
  {
    name: "change the password",
    call: () => postChangePassword("old", "new"),
    path: "/api/v1/auth/change-password",
    failure: "Could not change the password.",
  },
  {
    name: "save the theme",
    call: () => postSetTheme("dark"),
    path: "/api/v1/auth/theme",
    failure: "Could not save the theme.",
  },
  {
    name: "change the username",
    call: () => postChangeUsername("pw", "bob"),
    path: "/api/v1/auth/change-username",
    failure: "Could not change the username.",
  },
  {
    name: "make recovery codes",
    call: () => postNewRecoveryCodes("pw"),
    path: "/api/v1/auth/recovery-codes",
    body: { current_password: "pw" },
    failure: "Could not make recovery codes.",
  },
  {
    name: "confirm recovery codes",
    call: () => postConfirmRecoveryCodes(),
    path: "/api/v1/auth/recovery-codes/confirm",
    body: {},
    failure: "Could not save that the codes were saved.",
  },
  {
    name: "use a recovery code",
    call: () => postRecoveryCode("ABCD-1234"),
    path: "/api/v1/auth/recovery/code",
    body: { code: "ABCD-1234" },
    failure: "Could not use that recovery code.",
  },
  {
    name: "set a new password with a recovery token",
    call: () => postRecoveryPassword("tok", "new"),
    path: "/api/v1/auth/recovery/password",
    body: { recovery_token: "tok", new_password: "new" },
    failure: "Could not set the new password.",
  },
];

describe.each(JSON_CHANGES)("to $name", ({ call, path, body, failure }) => {
  it("posts JSON with the token in the body, and returns the answer", async () => {
    const answer = { ok: "the answer" };
    const api = stubWire({ [`POST ${path}`]: answer });

    await expect(call()).resolves.toEqual(answer);

    if (body) expectChange(api, "POST", path, body);
    else expect(theRequest(api)).toMatchObject({ method: "POST", path });
    const request = theRequest(api);
    expect(request.headers["content-type"]).toBe("application/json");
    expect(request.headers["x-requested-with"]).toBe("XMLHttpRequest");
  });

  it("says what failed when the server gives no reason it wants shown", async () => {
    stubWire({ [`POST ${path}`]: refused });

    const error = await call().catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiHttpError);
    expect((error as ApiHttpError).message).toBe(failure);
    expect((error as ApiHttpError).status).toBe(500);
    expect((error as ApiHttpError).path).toBe(path);
  });
});

describe("reads", () => {
  const READS: {
    name: string;
    call: () => Promise<unknown>;
    path: string;
    failure: string;
  }[] = [
    {
      name: "the token",
      call: fetchCsrfToken,
      path: "/api/v1/auth/csrf",
      failure: "Could not start a secure session.",
    },
    {
      name: "who is signed in",
      call: fetchMe,
      path: "/api/v1/auth/me",
      failure: "Could not load the current user.",
    },
    {
      name: "the current session",
      call: fetchCurrentSession,
      path: "/api/v1/auth/session",
      failure: "Could not load the current session.",
    },
    {
      name: "the active sessions",
      call: fetchActiveSessions,
      path: "/api/v1/auth/sessions",
      failure: "Could not load active sessions.",
    },
    {
      name: "first-run setup",
      call: fetchBootstrapStatus,
      path: "/api/v1/auth/bootstrap/status",
      failure: "Could not check first-run setup.",
    },
    {
      name: "the recovery codes",
      call: fetchRecoveryCodes,
      path: "/api/v1/auth/recovery-codes",
      failure: "Could not load your recovery codes.",
    },
  ];

  it.each(READS)(
    "asks for $name and says what failed on a refusal",
    async ({ call, path, failure }) => {
      stubWire({ [`GET ${path}`]: refused });

      const error = await call().catch((e: unknown) => e);

      expect(error).toBeInstanceOf(ApiHttpError);
      expect((error as ApiHttpError).message).toBe(failure);
      expect((error as ApiHttpError).status).toBe(500);
      expect((error as ApiHttpError).path).toBe(path);
    },
  );

  it("reads the token from the answer", async () => {
    const api = stubWire({});

    await expect(fetchCsrfToken()).resolves.toBe(CSRF_TOKEN);

    expect(api.requests.map((r) => `${r.method} ${r.path}`)).toEqual([
      "GET /api/v1/auth/csrf",
    ]);
  });

  it("lists the active sessions", async () => {
    const api = stubWire({
      "GET /api/v1/auth/sessions": { items: [{ id: "a" }, { id: "b" }] },
    });

    await expect(fetchActiveSessions()).resolves.toEqual([
      { id: "a" },
      { id: "b" },
    ]);

    expectRead(api, "/api/v1/auth/sessions");
  });

  it("asks how many recovery codes are left", async () => {
    const status = { remaining: 3, confirmed: true };
    const api = stubWire({ "GET /api/v1/auth/recovery-codes": status });

    await expect(fetchRecoveryCodes()).resolves.toEqual(status);

    expectRead(api, "/api/v1/auth/recovery-codes");
  });

  it("reads the current session", async () => {
    const session = { id: "s1" };
    const api = stubWire({ "GET /api/v1/auth/session": session });

    await expect(fetchCurrentSession()).resolves.toEqual(session);

    expectRead(api, "/api/v1/auth/session");
  });

  it("treats only a 401 as nobody signed in", async () => {
    stubWire({ "GET /api/v1/auth/me": () => jsonAnswer({ detail: "x" }, 403) });
    await expect(fetchMe()).rejects.toBeInstanceOf(ApiHttpError);
    vi.unstubAllGlobals();

    stubWire({
      "GET /api/v1/auth/session": () => jsonAnswer({ detail: "x" }, 403),
    });
    await expect(fetchCurrentSession()).rejects.toBeInstanceOf(ApiHttpError);
  });
});

describe("session actions", () => {
  it.each([
    [
      "postRevokeOtherSessions",
      () => postRevokeOtherSessions(),
      "revoke-others",
    ],
    ["postRevokeSession", () => postRevokeSession("abc"), "abc/revoke"],
  ])("%s returns the answer and says what failed", async (_n, call, tail) => {
    const path = `/api/v1/auth/sessions/${tail}`;
    const api = stubWire({ [`POST ${path}`]: { revoked: 2 } });

    await expect(call()).resolves.toEqual({ revoked: 2 });
    const request = theRequest(api);
    expect(request.method).toBe("POST");
    expect(request.headers["x-csrf-token"]).toBe(CSRF_TOKEN);
    vi.unstubAllGlobals();

    stubWire({ [`POST ${path}`]: refused });
    await expect(call()).rejects.toThrow("Could not update sessions.");
  });
});

describe("signing out", () => {
  const LOGOUT = "POST /api/v1/auth/logout";

  it("sends JSON, as a POST", async () => {
    const api = stubWire({ [LOGOUT]: new Response(null, { status: 204 }) });

    await postLogout();

    const request = theRequest(api);
    expect(request.method).toBe("POST");
    expect(request.headers["content-type"]).toBe("application/json");
  });

  it("is done on any success, a 204 or a 200", async () => {
    stubWire({ [LOGOUT]: jsonAnswer({ ok: true }, 200) });
    await expect(postLogout()).resolves.toBeUndefined();
  });

  it("says it could not sign out for a refusal", async () => {
    stubWire({ [LOGOUT]: refused });
    await expect(postLogout()).rejects.toThrow("Could not sign out.");
  });

  it("does not run the caller's hook when the token cannot be had", async () => {
    stubWire({ "GET /api/v1/auth/csrf": refused });
    const before = vi.fn();

    await expect(postLogout(before)).rejects.toThrow();

    expect(before).not.toHaveBeenCalled();
  });
});
