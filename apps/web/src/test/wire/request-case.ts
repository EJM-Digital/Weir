import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiHttpError } from "../../lib/api/client";
import { recordedBody, recordedResponse, recordedStatus } from "../recorded";
import {
  expectChange,
  expectChangeWithHeader,
  expectRead,
  stubWire,
} from "../wire";

/**
 * One request builder, called the way the web app calls it, and the one request it must make: the method, the path, the
 * query string, and for a change of state the JSON body with the token in it.
 */
export type RequestCase = {
  /** The exported function under test. The guard test requires every request function to be named by a case. */
  fn: (...args: never[]) => unknown;
  /** What is being asked, in a few words. */
  asking: string;
  call: () => Promise<unknown>;
  method: "GET" | "POST" | "PUT" | "DELETE";
  path: string;
  /** The query string, in the order it is sent. */
  query?: [string, string][];
  /** The JSON body sent besides the token. Leave out for a read. */
  body?: object;
  /** Where the token travels: in the JSON body, as nearly every change of state does, or in a header. */
  tokenIn?: "body" | "header";
  /**
   * The recorded `METHOD /path` the server answers with; leave out for an answer with no body. A recorded refusal makes
   * the case one that expects the call to throw the server's words.
   */
  reply?: string;
  /** What the call resolves to, when that is not the recorded body as it is. */
  result?: unknown;
  /** True for a call that hands a refusal back to its caller as its answer instead of throwing it. */
  handlesRefusal?: boolean;
};

/** A request the server refuses, as it really does: for a file it has no record of. */
const REFUSAL = "GET /api/v1/processing/files/9999/why-held";

function route(testCase: RequestCase): string {
  return `${testCase.method} ${testCase.path}`;
}

function isRefused(testCase: RequestCase): boolean {
  return (
    testCase.reply !== undefined &&
    recordedStatus(testCase.reply) >= 400 &&
    !testCase.handlesRefusal
  );
}

function refusalWords(reply: string): string {
  return recordedBody<{ detail: string }>(reply).detail;
}

/** Runs each case: the request it makes, what it returns, and that a refusal reaches the person with the server's words. */
export function describeRequests(
  name: string,
  cases: readonly RequestCase[],
): void {
  describe(name, () => {
    afterEach(() => vi.unstubAllGlobals());

    it.each(cases)("$fn.name: $asking", async (testCase) => {
      const api = stubWire({
        [route(testCase)]: testCase.reply
          ? recordedResponse(testCase.reply)
          : new Response(null, { status: 204 }),
      });

      const outcome = testCase.call().then(
        (result) => ({ result }),
        (error: unknown) => ({ error }),
      );
      const settled = await outcome;

      if (testCase.method === "GET") {
        expectRead(api, testCase.path, testCase.query);
      } else if (testCase.tokenIn === "header") {
        expectChangeWithHeader(api, testCase.method, testCase.path);
      } else {
        expectChange(api, testCase.method, testCase.path, testCase.body ?? {});
      }
      if (isRefused(testCase)) {
        expect(settled).toHaveProperty("error");
        const error = (settled as { error: ApiHttpError }).error;
        expect(error).toBeInstanceOf(ApiHttpError);
        expect(error.message).toBe(refusalWords(testCase.reply!));
        return;
      }
      expect(settled).not.toHaveProperty("error");
      const expected =
        "result" in testCase
          ? testCase.result
          : testCase.reply
            ? (recordedBody(testCase.reply) ?? undefined)
            : undefined;
      expect((settled as { result: unknown }).result).toEqual(expected);
    });

    it.each(
      cases.filter(
        (testCase) => !testCase.handlesRefusal && !isRefused(testCase),
      ),
    )(
      "$fn.name: says what the server said when it refuses",
      async (testCase) => {
        stubWire({ [route(testCase)]: recordedResponse(REFUSAL) });

        const failure = await testCase.call().catch((error: unknown) => error);

        expect(failure).toBeInstanceOf(ApiHttpError);
        expect((failure as ApiHttpError).status).toBe(404);
        expect((failure as ApiHttpError).message).toBe(refusalWords(REFUSAL));
      },
    );
  });
}
