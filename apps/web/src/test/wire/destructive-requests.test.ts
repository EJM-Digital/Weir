import { afterEach, describe, expect, it, vi } from "vitest";

import { CSRF_TOKEN, expectChange, stubWire, theRequest } from "../wire";
import {
  destructiveRequests,
  type DestructiveRequest,
} from "./destructive-requests";

/** Sends the request the way the web app does and hands back the one request the server would have received. */
async function sendRequest({ call, method, path }: DestructiveRequest) {
  const api = stubWire({
    [`${method} ${path}`]: new Response(null, { status: 204 }),
  });
  await call().catch(() => undefined);
  expect(api.unmatched).toEqual([]);
  return api;
}

/**
 * A request that can delete a file or switch on a clean nobody watches, held to the method, address and every field the server
 * reads. A wrong field name would send a body the server refuses or, worse, reads as the other choice.
 */
describe("the requests that delete, clean or download again", () => {
  afterEach(() => vi.unstubAllGlobals());

  it.each(destructiveRequests)(
    "$name sends exactly its method, address and body",
    async (request) => {
      const api = await sendRequest(request);

      expectChange(api, request.method, request.path, request.body);
    },
  );

  it("names each request once", () => {
    const names = destructiveRequests.map((request) => request.name);

    expect(new Set(names)).toHaveLength(names.length);
  });

  /**
   * The server tests send these requests as the web app builds them, from this file. It is regenerated with
   * `npm run web-bodies:write`; a body the web app builds differently fails here until it is.
   */
  it("matches the file the server tests send", async () => {
    const sent = [];
    for (const request of destructiveRequests) {
      const { body } = theRequest(await sendRequest(request));
      const { csrf_token, ...rest } = body as { csrf_token: string };
      expect(csrf_token).toBe(CSRF_TOKEN);
      sent.push({
        name: request.name,
        method: request.method,
        path: request.path,
        body: rest,
      });
      vi.unstubAllGlobals();
    }

    await expect(`${JSON.stringify(sent, null, 2)}\n`).toMatchFileSnapshot(
      "../../../../server/tests/fixtures/web-bodies/destructive-requests.json",
    );
  });
});
