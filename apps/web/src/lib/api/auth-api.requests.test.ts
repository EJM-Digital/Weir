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
import { jsonAnswer } from "../../test/stub-weir-api";
import {
  fetchBootstrapStatus,
  fetchMe,
  postBootstrap,
  postChangePassword,
  postChangeUsername,
  postLogin,
  postLogout,
  postRevokeOtherSessions,
  postRevokeSession,
  postSetTheme,
} from "./auth-api";
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
