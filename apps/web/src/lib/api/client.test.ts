import { afterEach, describe, expect, it, vi } from "vitest";
import {
  NETWORK_UNREACHABLE_MESSAGE,
  SIGN_IN_ENDED_MESSAGE,
  TIMED_OUT_MESSAGE,
  TOO_MANY_ATTEMPTS_MESSAGE,
} from "./api-error-text";
import {
  ApiHttpError,
  apiFetch,
  apiResponseErrorMessage,
  apiUrl,
  readJson,
  requireOk,
  resetUnauthorizedHandlingForTests,
  setUnauthorizedHandler,
  throwApiResponseError,
} from "./client";

afterEach(() => {
  resetUnauthorizedHandlingForTests();
  vi.restoreAllMocks();
});

const SETTINGS = "/api/v1/suite/settings";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("apiResponseErrorMessage", () => {
  it("shows the server's own reason for a refusal", async () => {
    const response = jsonResponse(401, {
      detail: "Invalid username or password.",
    });

    await expect(
      throwApiResponseError(
        "/api/v1/auth/login",
        response,
        "Could not sign in",
      ),
    ).rejects.toMatchObject({
      name: "ApiHttpError",
      path: "/api/v1/auth/login",
      status: 401,
      message: "Invalid username or password.",
    });
  });

  it("says the sign-in has ended for a 401 anywhere but the sign-in form", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(401, { detail: "Not authenticated." }),
      "Could not load settings",
    );

    expect(normalized.message).toBe(SIGN_IN_ENDED_MESSAGE);
  });

  it("asks for a wait after too many attempts", async () => {
    const normalized = await apiResponseErrorMessage(
      "/api/v1/auth/login",
      jsonResponse(429, {
        detail: "Too many login attempts from this address.",
      }),
      "Could not sign in",
    );

    expect(normalized.message).toBe(TOO_MANY_ATTEMPTS_MESSAGE);
  });

  it("turns validation answers into sentences about the field", async () => {
    const normalized = await apiResponseErrorMessage(
      "/api/v1/auth/change-password",
      jsonResponse(422, {
        detail: [
          {
            loc: ["body", "new_password"],
            msg: "String should have at least 8 characters",
          },
        ],
      }),
      "Could not change password",
    );

    expect(normalized.message).toBe(
      "New password must be at least 8 characters.",
    );
  });

  it("keeps the server's structured detail for callers that read it", async () => {
    const detail = [{ loc: ["body", "name"], msg: "Field required" }];

    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(422, { detail }),
      "Could not save",
    );

    expect(normalized.detail).toEqual(detail);
  });

  it("never shows a JSON body that has no detail", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(404, { reason: "missing-route" }),
      "Could not load",
    );

    expect(normalized.message).toBe("Could not load.");
  });

  it("never shows Weir's own failure text, which is for its log", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(500, { detail: "database is locked" }),
      "Could not load settings",
    );

    expect(normalized.message).toBe("Could not load settings.");
  });

  it("shows a media manager's failure relayed by Weir", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(502, {
        detail: "Sonarr did not answer when asked what it manages.",
      }),
      "Could not list libraries",
    );

    expect(normalized.message).toBe(
      "Sonarr did not answer when asked what it manages.",
    );
  });

  it("never shows an HTML page or a status code", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      new Response("<!doctype html><title>Bad gateway</title>", {
        status: 502,
      }),
      "Could not check for updates",
    );

    expect(normalized.message).toBe("Could not check for updates.");
  });

  it("never shows a plain-text body", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      new Response("Internal Server Error", { status: 500 }),
      "Could not save.",
    );

    expect(normalized.message).toBe("Could not save.");
  });

  it("says what failed, not the server's wording, for a JSON 500", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(500, {
        detail:
          "Weir ran into an unexpected problem and could not finish this request. The details are in Weir's log.",
        code: "internal_error",
      }),
      "Could not save.",
    );

    expect(normalized.message).toBe("Could not save.");
  });

  it("says the server's own sentence for a busy database, and keeps its code for a caller that reads it", async () => {
    const normalized = await apiResponseErrorMessage(
      SETTINGS,
      jsonResponse(503, {
        detail:
          "Weir is busy with other changes and could not finish this request. Try again in 30 seconds.",
        code: "database_busy",
      }),
      "Could not save.",
    );

    expect(normalized.message).toBe(
      "Weir is busy with other changes and could not finish this request. Try again in 30 seconds.",
    );
    expect(normalized.detail).toBe(normalized.message);
    expect(normalized.code).toBe("database_busy");
  });

  it("exposes status and path on ApiHttpError", () => {
    const error = new ApiHttpError(
      "/api/v1/example",
      503,
      "Service unavailable",
    );

    expect(error.status).toBe(503);
    expect(error.path).toBe("/api/v1/example");
  });
});

describe("apiFetch timeouts", () => {
  it("turns request timeouts into typed ApiHttpError", async () => {
    const timeoutSignal = {
      aborted: true,
      reason: new DOMException("The operation timed out.", "TimeoutError"),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
      onabort: null,
      throwIfAborted: vi.fn(),
    } as unknown as AbortSignal;
    vi.spyOn(AbortSignal, "timeout").mockReturnValue(timeoutSignal);
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockRejectedValue(
          new DOMException("The operation was aborted.", "AbortError"),
        ),
    );

    await expect(apiFetch("/api/v1/suite/settings")).rejects.toMatchObject({
      name: "ApiHttpError",
      status: 0,
      path: "/api/v1/suite/settings",
      timedOut: true,
      message: TIMED_OUT_MESSAGE,
    });
  });
});

describe("apiFetch network failures", () => {
  it("turns a rejected fetch (server unreachable) into the friendly message, not the raw browser text", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );

    await expect(apiFetch("/api/v1/auth/bootstrap")).rejects.toMatchObject({
      name: "ApiHttpError",
      status: 0,
      path: "/api/v1/auth/bootstrap",
      networkUnreachable: true,
      message: NETWORK_UNREACHABLE_MESSAGE,
    });

    // The exact wording never reaches a screen — only the guarded, plain-language message.
    await expect(apiFetch("/api/v1/auth/bootstrap")).rejects.not.toMatchObject({
      message: "Failed to fetch",
    });
  });

  it("recognises browser-specific network rejection text (Safari, Firefox)", async () => {
    for (const rawMessage of [
      "Load failed",
      "NetworkError when attempting to fetch resource.",
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error(rawMessage)));

      await expect(apiFetch("/api/v1/auth/me")).rejects.toMatchObject({
        networkUnreachable: true,
        message: NETWORK_UNREACHABLE_MESSAGE,
      });
    }
  });

  it("leaves unrelated fetch rejections alone", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new Error("boom, something else broke")),
    );

    await expect(apiFetch("/api/v1/auth/me")).rejects.toMatchObject({
      message: "boom, something else broke",
    });
  });
});

describe("apiFetch unauthorized handling", () => {
  it("calls the central unauthorized handler once for repeated 401s", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response("", { status: 401 })),
    );

    await apiFetch("/api/v1/suite/settings");
    await apiFetch("/api/v1/activity/recent");

    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith("/api/v1/suite/settings");
  });

  it("resets unauthorized suppression after a non-401 response", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(new Response("", { status: 401 }))
        .mockResolvedValueOnce(new Response("{}", { status: 200 }))
        .mockResolvedValueOnce(new Response("", { status: 401 })),
    );

    await apiFetch("/api/v1/suite/settings");
    await apiFetch("/api/v1/auth/me");
    await apiFetch("/api/v1/activity/recent");

    expect(handler).toHaveBeenCalledTimes(2);
  });
});

describe("apiFetch requests", () => {
  const answered = () => {
    const fetchStub = vi.fn().mockResolvedValue(new Response("{}"));
    vi.stubGlobal("fetch", fetchStub);
    return fetchStub;
  };

  afterEach(() => vi.unstubAllGlobals());

  it("goes to the same origin under /api/v1, with the session cookie and the headers the server looks for", async () => {
    const fetchStub = answered();

    await apiFetch(SETTINGS);

    const [url, init] = fetchStub.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(SETTINGS);
    expect(init.credentials).toBe("include");
    expect(init.headers).toMatchObject({
      Accept: "application/json",
      "X-Requested-With": "XMLHttpRequest",
    });
  });

  it("adds the caller's own headers to those, and keeps its method and body", async () => {
    const fetchStub = answered();

    await apiFetch(SETTINGS, {
      method: "PUT",
      body: "{}",
      headers: { "Content-Type": "application/json" },
    });

    const [, init] = fetchStub.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe("PUT");
    expect(init.body).toBe("{}");
    expect(init.headers).toEqual({
      Accept: "application/json",
      "X-Requested-With": "XMLHttpRequest",
      "Content-Type": "application/json",
    });
  });

  it("refuses an address outside /api/v1 without sending anything", async () => {
    const fetchStub = answered();

    await expect(apiFetch("/somewhere/else")).rejects.toThrow(
      "API paths must be under /api/v1",
    );

    expect(fetchStub).not.toHaveBeenCalled();
  });

  it("gives a request 30 seconds, or the time its caller asks for", async () => {
    const timeout = vi.spyOn(AbortSignal, "timeout");
    answered();

    await apiFetch(SETTINGS);
    await apiFetch(SETTINGS, { timeoutMs: 5_000 });

    expect(timeout.mock.calls).toEqual([[30_000], [5_000]]);
  });

  it("stops when its caller stops it, and says so as the caller's abort rather than as a timeout", async () => {
    const caller = new AbortController();
    vi.stubGlobal(
      "fetch",
      vi.fn(
        (_url: string, init: RequestInit) =>
          new Promise((_resolve, reject) => {
            init.signal?.addEventListener("abort", () =>
              reject(new DOMException("Aborted", "AbortError")),
            );
          }),
      ),
    );

    const request = apiFetch(SETTINGS, { signal: caller.signal });
    caller.abort();

    await expect(request).rejects.toMatchObject({
      name: "AbortError",
    });
    await expect(request).rejects.not.toBeInstanceOf(ApiHttpError);
  });
});

describe("apiUrl in a built page", () => {
  afterEach(() => vi.unstubAllEnvs());

  it.each([
    ["no address is configured", "", "/api/v1/suite/settings"],
    ["only spaces are configured", "   ", "/api/v1/suite/settings"],
    [
      "an address is configured, with or without a closing slash",
      " https://weir.example/ ",
      "https://weir.example/api/v1/suite/settings",
    ],
  ])("goes to the page's own origin when %s", (_name, configured, url) => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_API_BASE_URL", configured);

    expect(apiUrl(SETTINGS)).toBe(url);
  });

  it("adds the slash an address lacks, and refuses one outside /api/v1", () => {
    expect(apiUrl("api/v1/suite/settings")).toBe(SETTINGS);
    expect(() => apiUrl("/other")).toThrow("API paths must be under");
  });
});

describe("apiFetch where the browser cannot time a request out or join two stops", () => {
  const timeout = Object.getOwnPropertyDescriptor(AbortSignal, "timeout");
  const any = Object.getOwnPropertyDescriptor(AbortSignal, "any");
  afterEach(() => {
    vi.unstubAllGlobals();
    if (timeout) Object.defineProperty(AbortSignal, "timeout", timeout);
    if (any) Object.defineProperty(AbortSignal, "any", any);
  });

  it("sends the request without a time limit when the browser has no timeout signal", async () => {
    const fetchStub = vi.fn().mockResolvedValue(new Response("{}"));
    vi.stubGlobal("fetch", fetchStub);
    Object.defineProperty(AbortSignal, "timeout", {
      configurable: true,
      value: undefined,
    });

    await apiFetch(SETTINGS);

    expect(fetchStub.mock.calls[0][1].signal).toBeUndefined();
  });

  it("keeps the caller's stop when the browser cannot join it with the time limit", async () => {
    const fetchStub = vi.fn().mockResolvedValue(new Response("{}"));
    vi.stubGlobal("fetch", fetchStub);
    Object.defineProperty(AbortSignal, "any", {
      configurable: true,
      value: undefined,
    });
    const caller = new AbortController();

    await apiFetch(SETTINGS, { signal: caller.signal });

    expect(fetchStub.mock.calls[0][1].signal).toBe(caller.signal);
  });

  it.each([
    ["a rejection that is not an error", "refused"],
    ["an error that is not about the network", new Error("Something else")],
  ])("passes on %s as it came", async (_name, rejection) => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(rejection));

    await expect(apiFetch(SETTINGS)).rejects.toBe(rejection);
  });
});

describe("reading an answer", () => {
  it("reads nothing from an answer with no body, and the body when there is one", async () => {
    await expect(
      readJson(new Response(null, { status: 204 })),
    ).resolves.toBeUndefined();
    await expect(readJson(jsonResponse(200, { a: 1 }))).resolves.toEqual({
      a: 1,
    });
  });

  it("lets a good answer through and says why a bad one failed", async () => {
    await expect(
      requireOk(SETTINGS, new Response("{}"), "Failed"),
    ).resolves.toBeUndefined();
    await expect(
      requireOk(SETTINGS, jsonResponse(400, { detail: "No." }), "Failed"),
    ).rejects.toThrow("No.");
  });
});

describe("apiResponseErrorMessage when the body says nothing usable", () => {
  const withType = (type: string | null) =>
    new Response(type === null ? null : "<html>", {
      status: 400,
      headers: type === null ? {} : { "Content-Type": type },
    });

  it.each([
    ["has no type", null],
    ["claims to be JSON and is not", "application/json"],
  ])("says what failed when the body %s", async (_name, type) => {
    await expect(
      apiResponseErrorMessage(SETTINGS, withType(type), "Could not do it"),
    ).resolves.toMatchObject({ message: "Could not do it." });
  });
});

describe("apiResponseErrorMessage when the body cannot be read at all", () => {
  it("passes on a failure that is not a badly formed body", async () => {
    const response = {
      status: 500,
      headers: new Headers({ "Content-Type": "application/json" }),
      clone: () => ({ json: () => Promise.reject(new Error("disk gone")) }),
    } as unknown as Response;

    await expect(
      apiResponseErrorMessage(SETTINGS, response, "Could not do it"),
    ).rejects.toThrow("disk gone");
  });
});
