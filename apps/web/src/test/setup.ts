import "@testing-library/jest-dom/vitest";
import { configure } from "@testing-library/react";
import { afterEach } from "vitest";

// A wait for the page (findBy, waitFor) ends the moment what it waits for is there. The time it is given is only a hang
// guard for a test that is broken, so it is far longer than anything a working test needs. Library default is 1 second, and
// a stall of the whole machine over that (memory pressure, a busy shared PC) cuts a wait off even though the page was about
// to be ready: test/hang-guard.test.tsx. vite.config.ts gives the test itself longer still.
const HANG_GUARD_MS = 15_000;
configure({ asyncUtilTimeout: HANG_GUARD_MS });

// A test must never reach the network: the answer would depend on what is listening on the test
// host and how fast it replies. Tests stub the api module (or `fetch` itself) for what they need;
// any request that gets past that is refused here and fails the test that made it, even when the
// code under test swallows the rejection.
const unstubbedRequests: string[] = [];

globalThis.fetch = (input: RequestInfo | URL): Promise<Response> => {
  const url = input instanceof Request ? input.url : String(input);
  unstubbedRequests.push(url);
  return Promise.reject(
    new TypeError(
      `unstubbed request to ${url}: mock the api module in this test`,
    ),
  );
};

afterEach(() => {
  const requested = unstubbedRequests.splice(0);
  if (requested.length > 0) {
    throw new Error(
      `This test made unstubbed requests to ${[...new Set(requested)].join(", ")}. Mock the api module in this test.`,
    );
  }
});

if (typeof globalThis.localStorage === "undefined") {
  const storage = new Map<string, string>();

  Object.defineProperty(globalThis, "localStorage", {
    configurable: true,
    value: {
      clear: () => storage.clear(),
      getItem: (key: string) => storage.get(key) ?? null,
      key: (index: number) => Array.from(storage.keys())[index] ?? null,
      get length() {
        return storage.size;
      },
      removeItem: (key: string) => storage.delete(key),
      setItem: (key: string, value: string) => storage.set(key, String(value)),
    },
  });
}

// jsdom has no canvas: say so quietly rather than logging "not implemented" for every shelf that measures its captions.
if (typeof HTMLCanvasElement !== "undefined") {
  HTMLCanvasElement.prototype.getContext = () => null;
}
