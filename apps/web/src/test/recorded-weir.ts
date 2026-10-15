import { vi } from "vitest";

/** What a running Weir server answered once: its status and the body text it sent, kept as recorded. */
export type RecordedReply = { status: number; body: string };

/** A request the page made, as the server would have seen it. */
export type SentRequest = {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
};

/**
 * Replies by `"METHOD /path"`. A list is answered in order, one reply for each request, and the last one stays for any
 * further request; this is how a list that changes after a sign-out answers first as it was and then as it is. A function
 * answers when the test lets it, which is how a request is kept waiting while the page shows it is busy.
 */
export type RecordedRoutes = Record<
  string,
  RecordedReply | RecordedReply[] | (() => Promise<RecordedReply>)
>;

/** The part of a page's world that is the server: it answers from recordings and remembers what it was asked. */
export type RecordedWeir = {
  requests: SentRequest[];
  /** Requests no recording answers; every one is also refused, so a test that makes one fails on what the page shows. */
  unrecorded: string[];
  sent: (method: string, path: string) => SentRequest[];
};

const SERVER_ORIGIN = "http://weir.test";

function routeOf(input: RequestInfo | URL, init?: RequestInit) {
  const url = new URL(
    input instanceof Request ? input.url : String(input),
    SERVER_ORIGIN,
  );
  const method = (
    init?.method ?? (input instanceof Request ? input.method : "GET")
  ).toUpperCase();
  return { method, path: url.pathname };
}

function readHeaders(init?: RequestInit): Record<string, string> {
  return Object.fromEntries(
    [...new Headers(init?.headers).entries()].map(([name, value]) => [
      name.toLowerCase(),
      value,
    ]),
  );
}

function readBody(init?: RequestInit): unknown {
  return typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
}

/**
 * Stands in for the server at the one place the page reaches it, `fetch`, so the page's own API client, its error
 * wording and its caching all run for real. Call it in a test; `vi.unstubAllGlobals()` puts `fetch` back.
 */
export function serveRecordedReplies(routes: RecordedRoutes): RecordedWeir {
  const served = new Map<string, number>();
  const weir: RecordedWeir = {
    requests: [],
    unrecorded: [],
    sent: (method, path) =>
      weir.requests.filter((r) => r.method === method && r.path === path),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const { method, path } = routeOf(input, init);
      weir.requests.push({
        method,
        path,
        headers: readHeaders(init),
        body: readBody(init),
      });
      const key = `${method} ${path}`;
      const route = routes[key];
      if (!route) {
        weir.unrecorded.push(key);
        return Promise.reject(new Error(`No recording answers ${key}.`));
      }
      const answer = (reply: RecordedReply) =>
        new Response(reply.body, {
          status: reply.status,
          headers: { "Content-Type": "application/json" },
        });
      if (typeof route === "function") return route().then(answer);
      const replies = Array.isArray(route) ? route : [route];
      const index = Math.min(served.get(key) ?? 0, replies.length - 1);
      served.set(key, (served.get(key) ?? 0) + 1);
      return Promise.resolve(answer(replies[index]));
    }),
  );
  return weir;
}
