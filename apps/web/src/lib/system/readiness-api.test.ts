import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiHttpError } from "../api/client";
import { systemReplies } from "../../test/recorded-system";
import { serveRecordedReplies } from "../../test/recorded-weir";
import { fetchSystemReadiness } from "./readiness-api";

const READINESS = "GET /api/v1/system/readiness";

/** The reply the middleware gives a request that gave up on a busy database, with the words and code it sends (one second waited). */
const databaseBusy = {
  status: 503,
  body: '{"code":"database_busy","detail":"Weir is busy with other changes and could not finish this request. Try again in 1 second."}',
};

/** The recorded ready reply as the server sends it while a worker is down or the start has not finished: same body, status 503. */
const notReady = {
  status: 503,
  body: JSON.stringify({
    ...JSON.parse(systemReplies.readiness.body),
    ready: false,
    status: "starting",
  }),
};

afterEach(() => vi.unstubAllGlobals());

describe("fetchSystemReadiness", () => {
  it("reads the recorded 200 reply", async () => {
    serveRecordedReplies({ [READINESS]: systemReplies.readiness });

    await expect(fetchSystemReadiness()).resolves.toEqual(
      JSON.parse(systemReplies.readiness.body),
    );
  });

  it("reads a 503 that carries the readiness body, because that body is what the shell shows", async () => {
    serveRecordedReplies({ [READINESS]: notReady });

    await expect(fetchSystemReadiness()).resolves.toMatchObject({
      ready: false,
      status: "starting",
      version: "0.0.1-dev",
    });
  });

  it("does not take a busy database's 503 for a readiness reading", async () => {
    serveRecordedReplies({ [READINESS]: databaseBusy });

    await expect(fetchSystemReadiness()).rejects.toMatchObject({
      name: "ApiHttpError",
      status: 503,
    });
  });

  it("throws on a reply that is signed out or a server failure", async () => {
    serveRecordedReplies({ [READINESS]: systemReplies.previewSignedOut });
    await expect(fetchSystemReadiness()).rejects.toBeInstanceOf(ApiHttpError);

    serveRecordedReplies({ [READINESS]: systemReplies.settingsPutFails });
    await expect(fetchSystemReadiness()).rejects.toMatchObject({
      status: 500,
    });
  });
});
