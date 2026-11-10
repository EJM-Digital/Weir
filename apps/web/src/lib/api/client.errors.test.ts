import { afterEach, describe, expect, it, vi } from "vitest";

import {
  ApiHttpError,
  apiFetch,
  apiResponseErrorMessage,
  apiUrl,
  resetUnauthorizedHandlingForTests,
  setUnauthorizedHandler,
  throwApiResponseError,
} from "./client";

afterEach(() => {
  resetUnauthorizedHandlingForTests();
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const PATH = "/api/v1/suite/settings";

function rejectWith(reason: unknown) {
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(reason));
}

describe("what an ApiHttpError says about itself", () => {
  it("is neither a timeout nor an unreachable server unless told", () => {
    const error = new ApiHttpError(PATH, 500, "Failed.");

    expect(error).toMatchObject({
      name: "ApiHttpError",
      path: PATH,
      status: 500,
      message: "Failed.",
      timedOut: false,
      networkUnreachable: false,
    });
    expect(error.detail).toBeUndefined();
    expect(error.code).toBeUndefined();
  });

  it("is a timeout and nothing else for a request that ran out of time", async () => {
    vi.spyOn(AbortSignal, "timeout").mockReturnValue(AbortSignal.abort());
    rejectWith(new DOMException("timed out", "AbortError"));

    const error = await apiFetch(PATH).catch((e: unknown) => e);

    expect(error).toMatchObject({
      status: 0,
      timedOut: true,
      networkUnreachable: false,
    });
  });

  it("is an unreachable server and not a timeout when the request never got there", async () => {
    rejectWith(new TypeError("Failed to fetch"));

    const error = await apiFetch(PATH).catch((e: unknown) => e);

    expect(error).toMatchObject({
      status: 0,
      timedOut: false,
      networkUnreachable: true,
    });
  });

  it("is neither for an error answer, which keeps the server's status, detail and code", async () => {
    const response = new Response(
      JSON.stringify({ detail: "Busy.", code: "database_busy" }),
      { status: 503, headers: { "Content-Type": "application/json" } },
    );

    const error = await throwApiResponseError(PATH, response, "Failed.").catch(
      (e: unknown) => e,
    );

    expect(error).toBeInstanceOf(ApiHttpError);
    expect(error).toMatchObject({
      status: 503,
      timedOut: false,
      networkUnreachable: false,
      detail: "Busy.",
      code: "database_busy",
    });
  });
});

describe("telling a failed request from a refused one", () => {
  it("takes any TypeError from fetch as an unreachable server, whatever it says", async () => {
    rejectWith(new TypeError("undefined is not a function"));

    await expect(apiFetch(PATH)).rejects.toMatchObject({
      networkUnreachable: true,
    });
  });

  it("passes a null rejection on as it is", async () => {
    rejectWith(null);

    await expect(apiFetch(PATH)).rejects.toBeNull();
  });

  it("passes an empty rejection on as it is", async () => {
    rejectWith(undefined);

    await expect(apiFetch(PATH)).rejects.toBeUndefined();
  });

  it("passes a number on as it is", async () => {
    rejectWith(7);

    await expect(apiFetch(PATH)).rejects.toBe(7);
  });

  it("does not take another kind of error for a timeout even when the timer has run out", async () => {
    vi.spyOn(AbortSignal, "timeout").mockReturnValue(AbortSignal.abort());
    const other = { name: "SomethingElse" };
    rejectWith(other);

    await expect(apiFetch(PATH)).rejects.toBe(other);
  });

  it("does not take an abort for a timeout when this runtime has no timer to run out", async () => {
    const original: unknown = Reflect.get(AbortSignal, "timeout");
    Object.defineProperty(AbortSignal, "timeout", {
      value: undefined,
      configurable: true,
    });
    try {
      const abort = new DOMException("aborted", "AbortError");
      rejectWith(abort);

      await expect(apiFetch(PATH)).rejects.toBe(abort);
    } finally {
      Object.defineProperty(AbortSignal, "timeout", {
        value: original,
        configurable: true,
      });
    }
  });
});

describe("the unauthorized handler", () => {
  it("is not called after the handling was reset", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    resetUnauthorizedHandlingForTests();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response("", { status: 401 })),
    );

    await apiFetch(PATH);

    expect(handler).not.toHaveBeenCalled();
  });

  it("is called with the path of the request that was refused", async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response("", { status: 401 })),
    );

    await apiFetch(PATH);

    expect(handler).toHaveBeenCalledExactlyOnceWith(PATH);
  });
});

describe("the base address", () => {
  it("is the same origin when none is pinned at all", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_API_BASE_URL", undefined);

    expect(apiUrl(PATH)).toBe(PATH);
  });
});

describe("an error answer whose body is JSON null", () => {
  it("says the fallback and keeps no detail", async () => {
    const response = new Response("null", {
      status: 400,
      headers: { "Content-Type": "application/json" },
    });

    const result = await apiResponseErrorMessage(PATH, response, "Failed");

    expect(result).toEqual({
      message: "Failed.",
      detail: undefined,
      code: undefined,
    });
  });
});
