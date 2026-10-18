import { afterEach, describe, expect, it, vi } from "vitest";

import {
  ApiHttpError,
  apiFetch,
  apiResponseErrorMessage,
  apiUrl,
  readJson,
  requireOk,
  resetUnauthorizedHandlingForTests,
  setUnauthorizedHandler,
} from "./client";

afterEach(() => {
  resetUnauthorizedHandlingForTests();
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function answer(response: Response = new Response("{}", { status: 200 })) {
  const fetchMock = vi.fn().mockResolvedValue(response);
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function sentInit(fetchMock: ReturnType<typeof vi.fn>): RequestInit {
  return fetchMock.mock.calls[0]?.[1] as RequestInit;
}

describe("apiUrl", () => {
  it("is the same-origin path in development, whatever base URL is pinned", () => {
    vi.stubEnv("VITE_API_BASE_URL", "http://other:9000");

    expect(apiUrl("/api/v1/suite/settings")).toBe("/api/v1/suite/settings");
  });

  it("puts a pinned base URL, trimmed and without its trailing slash, in front of the path", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_API_BASE_URL", "  http://nas:9000/  ");

    expect(apiUrl("/api/v1/suite/settings")).toBe(
      "http://nas:9000/api/v1/suite/settings",
    );
  });

  it("is the same-origin path when no base URL is pinned, or only blanks are", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_API_BASE_URL", "   ");

    expect(apiUrl("/api/v1/suite/settings")).toBe("/api/v1/suite/settings");
  });

  it("adds the slash a path is missing and refuses anything outside the API", () => {
    expect(apiUrl("api/v1/suite/settings")).toBe("/api/v1/suite/settings");
    expect(() => apiUrl("/other/path")).toThrow(
      "API paths must be under /api/v1",
    );
    expect(() => apiUrl("other")).toThrow("API paths must be under /api/v1");
  });
});

describe("apiFetch requests", () => {
  it("sends the session cookie and the headers the server expects", async () => {
    const fetchMock = answer();

    await apiFetch("/api/v1/suite/settings");

    expect(fetchMock.mock.calls[0]?.[0]).toBe("/api/v1/suite/settings");
    const init = sentInit(fetchMock);
    expect(init.credentials).toBe("include");
    expect(init.headers).toEqual({
      Accept: "application/json",
      "X-Requested-With": "XMLHttpRequest",
    });
  });

  it("keeps the caller's method, body and extra headers, and lets the caller replace Accept", async () => {
    const fetchMock = answer();

    await apiFetch("/api/v1/suite/settings", {
      method: "PUT",
      body: '{"a":1}',
      headers: { "Content-Type": "application/json", Accept: "text/plain" },
    });

    const init = sentInit(fetchMock);
    expect(init.method).toBe("PUT");
    expect(init.body).toBe('{"a":1}');
    expect(init.headers).toEqual({
      Accept: "text/plain",
      "X-Requested-With": "XMLHttpRequest",
      "Content-Type": "application/json",
    });
  });

  it("does not pass its own timeout option on to fetch", async () => {
    const fetchMock = answer();

    await apiFetch("/api/v1/suite/settings", { timeoutMs: 5 });

    expect(sentInit(fetchMock)).not.toHaveProperty("timeoutMs");
  });

  it("gives fetch a signal that ends after the timeout asked for, 30 seconds when none is", async () => {
    const timeout = vi.spyOn(AbortSignal, "timeout");
    answer();

    await apiFetch("/api/v1/suite/settings");
    await apiFetch("/api/v1/suite/settings", { timeoutMs: 1234 });

    expect(timeout.mock.calls).toEqual([[30_000], [1234]]);
  });

  it("stops the request when the caller does, and says nothing of a timeout", async () => {
    const fetchMock = vi.fn((_url: string, init: RequestInit) =>
      Promise.reject(
        init.signal?.aborted
          ? new DOMException("The operation was aborted.", "AbortError")
          : new Error("not aborted"),
      ),
    );
    vi.stubGlobal("fetch", fetchMock);
    const caller = new AbortController();
    caller.abort();

    const failure = await apiFetch("/api/v1/suite/settings", {
      signal: caller.signal,
    }).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(DOMException);
    expect((failure as DOMException).name).toBe("AbortError");
    expect(failure).not.toBeInstanceOf(ApiHttpError);
  });

  it("ends on the caller's signal alone when this runtime cannot join two signals", async () => {
    const fetchMock = answer();
    const original: unknown = Reflect.get(AbortSignal, "any");
    Object.defineProperty(AbortSignal, "any", {
      value: undefined,
      configurable: true,
    });
    try {
      const caller = new AbortController();
      await apiFetch("/api/v1/suite/settings", { signal: caller.signal });

      expect(sentInit(fetchMock).signal).toBe(caller.signal);
    } finally {
      Object.defineProperty(AbortSignal, "any", {
        value: original,
        configurable: true,
      });
    }
  });

  it("ends on its own timeout alone when the caller gave no signal", async () => {
    const fetchMock = answer();
    const timeoutSignal = new AbortController().signal;
    vi.spyOn(AbortSignal, "timeout").mockReturnValue(timeoutSignal);

    await apiFetch("/api/v1/suite/settings");

    expect(sentInit(fetchMock).signal).toBe(timeoutSignal);
  });

  it("sends no signal at all when this runtime cannot make a timeout", async () => {
    const fetchMock = answer();
    const original: unknown = Reflect.get(AbortSignal, "timeout");
    Object.defineProperty(AbortSignal, "timeout", {
      value: undefined,
      configurable: true,
    });
    try {
      await apiFetch("/api/v1/suite/settings");

      expect(sentInit(fetchMock).signal).toBeUndefined();
    } finally {
      Object.defineProperty(AbortSignal, "timeout", {
        value: original,
        configurable: true,
      });
    }
  });

  it("joins the caller's signal and its own, so either one ends the request", async () => {
    const fetchMock = answer();
    const timeoutSignal = new AbortController().signal;
    vi.spyOn(AbortSignal, "timeout").mockReturnValue(timeoutSignal);
    const any = vi.spyOn(AbortSignal, "any");
    const caller = new AbortController();

    await apiFetch("/api/v1/suite/settings", { signal: caller.signal });

    expect(any).toHaveBeenCalledWith([caller.signal, timeoutSignal]);
    expect(sentInit(fetchMock).signal).toBe(any.mock.results[0]?.value);
  });

  it("does not take an abort for a timeout unless its own timer ran out", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockRejectedValue(
          new DOMException("The operation was aborted.", "AbortError"),
        ),
    );

    await expect(apiFetch("/api/v1/suite/settings")).rejects.toMatchObject({
      name: "AbortError",
    });
  });

  it("passes on a rejection that is not an error object at all", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue("plain text"));

    await expect(apiFetch("/api/v1/suite/settings")).rejects.toBe("plain text");
  });

  it("passes on an error object that has no name or another name", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue({ reason: "odd" }));
    await expect(apiFetch("/api/v1/suite/settings")).rejects.toEqual({
      reason: "odd",
    });

    vi.stubGlobal("fetch", vi.fn().mockRejectedValue({ name: "OtherError" }));
    await expect(apiFetch("/api/v1/suite/settings")).rejects.toEqual({
      name: "OtherError",
    });
  });

  it("takes a plain Error as network trouble only for the words browsers use", async () => {
    for (const message of [
      "Failed to fetch",
      "Failed to retrieve data",
      "A NetworkError occurred",
      "Load failed",
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error(message)));
      await expect(apiFetch("/api/v1/suite/settings")).rejects.toMatchObject({
        networkUnreachable: true,
      });
    }

    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("Failed")));
    const failure = await apiFetch("/api/v1/suite/settings").catch(
      (error: unknown) => error,
    );
    expect(failure).not.toBeInstanceOf(ApiHttpError);
    expect(failure).toMatchObject({ message: "Failed" });
  });

  it("does not call the unauthorized handler for any answer but a 401, or when there is none", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    answer(new Response("{}", { status: 403 }));
    await apiFetch("/api/v1/suite/settings");
    expect(handler).not.toHaveBeenCalled();

    setUnauthorizedHandler(null);
    answer(new Response("", { status: 401 }));
    const response = await apiFetch("/api/v1/suite/settings");
    expect(response.status).toBe(401);
  });

  it("calls the unauthorized handler again once the handler is set afresh", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    answer(new Response("", { status: 401 }));
    await apiFetch("/api/v1/suite/settings");

    setUnauthorizedHandler(handler);
    await apiFetch("/api/v1/suite/settings");

    expect(handler).toHaveBeenCalledTimes(2);
  });
});

describe("readJson", () => {
  it("is nothing for an empty answer and the parsed body for a JSON one", async () => {
    expect(await readJson(new Response(""))).toBeUndefined();
    expect(await readJson(new Response('{"a":1}'))).toEqual({ a: 1 });
  });
});

describe("apiResponseErrorMessage bodies", () => {
  const fallback = "Could not do that.";

  it("reads a JSON body whatever case the content type is written in", async () => {
    const response = new Response('{"detail":"Folder is missing."}', {
      status: 400,
      headers: { "Content-Type": "Application/JSON; charset=utf-8" },
    });

    const result = await apiResponseErrorMessage(
      "/api/v1/x",
      response,
      fallback,
    );

    expect(result.detail).toBe("Folder is missing.");
  });

  it("ignores a body that is not declared JSON, even if it looks like it", async () => {
    const response = new Response('{"detail":"Folder is missing."}', {
      status: 400,
      headers: { "Content-Type": "text/plain" },
    });

    const result = await apiResponseErrorMessage(
      "/api/v1/x",
      response,
      fallback,
    );

    expect(result.detail).toBeUndefined();
    expect(result.message).toBe(fallback);
  });

  it("ignores a body with no content type at all", async () => {
    const result = await apiResponseErrorMessage(
      "/api/v1/x",
      new Response('{"detail":"x"}', { status: 400 }),
      fallback,
    );

    expect(result.detail).toBeUndefined();
  });

  it("answers with the fallback when a JSON body cannot be read", async () => {
    const response = new Response("{not json", {
      status: 500,
      headers: { "Content-Type": "application/json" },
    });

    const result = await apiResponseErrorMessage(
      "/api/v1/x",
      response,
      fallback,
    );

    expect(result.detail).toBeUndefined();
    expect(result.message).toBe(fallback);
  });

  it("does not hide a failure to read the body that is not a bad body", async () => {
    const response = new Response("{}", {
      status: 500,
      headers: { "Content-Type": "application/json" },
    });
    vi.spyOn(response, "clone").mockImplementation(() => {
      throw new RangeError("out of memory");
    });

    await expect(
      apiResponseErrorMessage("/api/v1/x", response, fallback),
    ).rejects.toThrow("out of memory");
  });

  it("keeps no detail for a JSON body that is not an object", async () => {
    const response = new Response("[1,2]", {
      status: 400,
      headers: { "Content-Type": "application/json" },
    });

    const result = await apiResponseErrorMessage(
      "/api/v1/x",
      response,
      fallback,
    );

    expect(result.detail).toBeUndefined();
  });
});

describe("requireOk", () => {
  it("lets a good answer through and turns a bad one into the error with its status and path", async () => {
    await expect(
      requireOk("/api/v1/x", new Response("{}", { status: 200 }), "Failed."),
    ).resolves.toBeUndefined();

    const failure = await requireOk(
      "/api/v1/x",
      new Response("{}", { status: 404 }),
      "Failed.",
    ).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiHttpError);
    expect(failure).toMatchObject({ status: 404, path: "/api/v1/x" });
  });
});
