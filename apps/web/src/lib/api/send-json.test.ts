import { afterEach, describe, expect, it, vi } from "vitest";

import { jsonAnswer } from "../../test/stub-weir-api";
import { CSRF_TOKEN, stubWire, theRequest } from "../../test/wire";
import { ApiHttpError } from "./client";
import { sendJson, sendJsonUnchecked } from "./send-json";

afterEach(() => vi.unstubAllGlobals());

const PATH = "/api/v1/things/1";

describe("sendJsonUnchecked", () => {
  it.each(["POST", "PUT", "PATCH", "DELETE"] as const)(
    "sends a %s with a JSON body carrying the token",
    async (method) => {
      const api = stubWire({ [`${method} ${PATH}`]: { ok: true } });

      const response = await sendJsonUnchecked(PATH, method, { name: "x" });

      expect(response.status).toBe(200);
      const request = theRequest(api);
      expect(request.method).toBe(method);
      expect(request.headers["content-type"]).toBe("application/json");
      expect(request.body).toEqual({ name: "x", csrf_token: CSRF_TOKEN });
    },
  );

  it("answers a refusal as it comes, for the caller to read", async () => {
    stubWire({ [`PUT ${PATH}`]: jsonAnswer({ detail: "No." }, 409) });

    const response = await sendJsonUnchecked(PATH, "PUT", {});

    expect(response.status).toBe(409);
  });

  it("puts the fresh token over one the caller's body already has", async () => {
    const api = stubWire({ [`PUT ${PATH}`]: {} });

    await sendJsonUnchecked(PATH, "PUT", { csrf_token: "stale" });

    expect(theRequest(api).body).toEqual({ csrf_token: CSRF_TOKEN });
  });
});

describe("sendJson", () => {
  it("returns the answer of a request that went through", async () => {
    stubWire({ [`PATCH ${PATH}`]: { done: true } });

    const response = await sendJson(PATH, "PATCH", {}, "Could not save.");

    await expect(response.json()).resolves.toEqual({ done: true });
  });

  it("says what failed when the server gives no reason it wants shown", async () => {
    stubWire({
      [`DELETE ${PATH}`]: jsonAnswer({ detail: "log text" }, 500),
    });

    const failure = await sendJson(
      PATH,
      "DELETE",
      {},
      "Could not delete",
    ).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiHttpError);
    expect(failure).toMatchObject({
      status: 500,
      path: PATH,
      message: "Could not delete.",
    });
  });

  it("shows the server's reason for a refusal", async () => {
    stubWire({
      [`PUT ${PATH}`]: jsonAnswer({ detail: "Name is taken." }, 409),
    });

    await expect(sendJson(PATH, "PUT", {}, "Could not save.")).rejects.toThrow(
      "Name is taken.",
    );
  });
});
