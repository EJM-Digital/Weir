import { afterEach, describe, expect, it, vi } from "vitest";

import {
  CSRF_TOKEN,
  expectChange,
  expectRead,
  stubWire,
  theRequest,
} from "../../test/wire";
import { jsonAnswer } from "../../test/stub-weir-api";
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

afterEach(() => vi.unstubAllGlobals());

const refused = () =>
  jsonAnswer({ detail: "Weir ran into a problem; see its log." }, 500);

/** Every call that asks for a token and then changes state with a JSON body. */
const JSON_CHANGES: {
  name: string;
  call: () => Promise<unknown>;
  path: string;
  body: object;
  failure: string;
}[] = [
  {
    name: "sign in",
    call: () => postLogin("alice", "pw", false),
    path: "/api/v1/auth/login",
    body: { username: "alice", password: "pw", trusted_device: false },
    failure: "Could not sign in.",
  },
  {
    name: "create the first user",
    call: () => postBootstrap("alice", "pw"),
    path: "/api/v1/auth/bootstrap",
    body: { username: "alice", password: "pw" },
    failure: "Could not create the first user.",
  },
  {
    name: "change the password",
    call: () => postChangePassword("old", "new"),
    path: "/api/v1/auth/change-password",
    body: { current_password: "old", new_password: "new" },
    failure: "Could not change the password.",
  },
  {
    name: "save the theme",
    call: () => postSetTheme("dark"),
    path: "/api/v1/auth/theme",
    body: { theme: "dark" },
    failure: "Could not save the theme.",
  },
  {
    name: "change the username",
    call: () => postChangeUsername("pw", "bob"),
    path: "/api/v1/auth/change-username",
    body: { current_password: "pw", new_username: "bob" },
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

    expectChange(api, "POST", path, body);
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

  it("sends JSON with the token in the body and the header", async () => {
    const api = stubWire({ [LOGOUT]: new Response(null, { status: 204 }) });

    await postLogout();

    const request = theRequest(api);
    expect(request.method).toBe("POST");
    expect(request.headers["content-type"]).toBe("application/json");
    expect(request.headers["x-csrf-token"]).toBe(CSRF_TOKEN);
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

describe("the first account's setup code", () => {
  it("is left out of the body when empty", async () => {
    const api = stubWire({ "POST /api/v1/auth/bootstrap": {} });

    await postBootstrap("alice", "pw", "");

    expect(theRequest(api).body).not.toHaveProperty("setup_code");
  });
});
