import { vi } from "vitest";

/** A request Weir's web app made, as the server would see it. */
export type StubbedRequest = {
  method: string;
  path: string;
  query: URLSearchParams;
  /** The JSON body, or undefined for a request with none. */
  body: unknown;
};

/** What a route answers: a Response as it is, or anything else as a 200 with that JSON body. */
export type StubbedAnswer = unknown;
type Handler = (
  request: StubbedRequest,
) => StubbedAnswer | Promise<StubbedAnswer>;
type Route = StubbedAnswer | Handler;

/** A JSON answer with a status, for a route that fails or answers something other than a 200. */
export function jsonAnswer(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

/** The fields a request saves: its JSON body without the CSRF token every change carries. */
export function savedFields(request: StubbedRequest): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(request.body as Record<string, unknown>).filter(
      ([field]) => field !== "csrf_token",
    ),
  );
}

export type StubbedApi = {
  /** Every request made so far, in order. */
  requests: StubbedRequest[];
  /** The requests made to a route, written as `GET /api/v1/...`. */
  to(route: string): StubbedRequest[];
  /** Requests no route answered; each was refused as if the server could not be reached. */
  unmatched: string[];
  /** Answers a route differently from now on. */
  set(route: string, answer: Route): void;
};

function parsed(input: RequestInfo | URL, init?: RequestInit): StubbedRequest {
  const url = new URL(
    input instanceof Request ? input.url : String(input),
    "http://localhost",
  );
  const text = typeof init?.body === "string" ? init.body : undefined;
  return {
    method: (init?.method ?? "GET").toUpperCase(),
    path: url.pathname,
    query: url.searchParams,
    body: text === undefined ? undefined : (JSON.parse(text) as unknown),
  };
}

/**
 * Stands in for the server at `fetch`, so a test that asserts a request catches a wrong address, verb or field. Routes are
 * written `METHOD /path`. A request no route answers fails the way an unreachable server does and is listed in `unmatched`,
 * which a test checks is empty. Undo with `vi.unstubAllGlobals()`.
 */
export function stubWeirApi(routes: Record<string, Route>): StubbedApi {
  const table = new Map(Object.entries(routes));
  const api: StubbedApi = {
    requests: [],
    unmatched: [],
    to: (route) =>
      api.requests.filter(
        (request) => `${request.method} ${request.path}` === route,
      ),
    set: (route, answer) => void table.set(route, answer),
  };
  vi.stubGlobal(
    "fetch",
    async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = parsed(input, init);
      const key = `${request.method} ${request.path}`;
      api.requests.push(request);
      if (!table.has(key)) {
        api.unmatched.push(key);
        throw new TypeError(`unstubbed request: ${key}`);
      }
      const route = table.get(key);
      const answer = await (typeof route === "function"
        ? (route as Handler)(request)
        : route);
      return answer instanceof Response ? answer.clone() : jsonAnswer(answer);
    },
  );
  return api;
}
