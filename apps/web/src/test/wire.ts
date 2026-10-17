import { expect } from "vitest";

import {
  stubWeirApi,
  type StubbedApi,
  type StubbedRequest,
} from "./stub-weir-api";

/** The token the stubbed server hands out, so a test can see it come back in a request's body. */
export const CSRF_TOKEN = "csrf-test";

const CSRF_ROUTE = "GET /api/v1/auth/csrf";

/**
 * Stands in for the server at `fetch`, answering the token request every change of state makes first. Replies are
 * written as `METHOD /path`; see {@link stubWeirApi}.
 */
export function stubWire(routes: Record<string, unknown>): StubbedApi {
  return stubWeirApi({ [CSRF_ROUTE]: { csrf_token: CSRF_TOKEN }, ...routes });
}

/** The one request the call under test made, leaving out the token request that comes before a change of state. */
export function theRequest(api: StubbedApi): StubbedRequest {
  const sent = api.requests.filter(
    (request) => `${request.method} ${request.path}` !== CSRF_ROUTE,
  );
  expect(
    sent.map((request) => `${request.method} ${request.path}`),
  ).toHaveLength(1);
  return sent[0]!;
}

/** What a call changing state must send: the method, the path, and the JSON body with the token in it. */
export function expectChange(
  api: StubbedApi,
  method: string,
  path: string,
  body: object,
): void {
  const request = theRequest(api);
  expect(`${request.method} ${request.path}`).toBe(`${method} ${path}`);
  expect(request.body).toEqual({ ...body, csrf_token: CSRF_TOKEN });
}

/** What a call reading state must send: a GET to the path, with these query parameters in this order and no body. */
export function expectRead(
  api: StubbedApi,
  path: string,
  query: [string, string][] = [],
): void {
  const request = theRequest(api);
  expect(`${request.method} ${request.path}`).toBe(`GET ${path}`);
  expect([...request.query.entries()]).toEqual(query);
  expect(request.body).toBeUndefined();
}

/** What a call changing state must send when it carries the token in a header instead of its body. */
export function expectChangeWithHeader(
  api: StubbedApi,
  method: string,
  path: string,
): void {
  const request = theRequest(api);
  expect(`${request.method} ${request.path}`).toBe(`${method} ${path}`);
  expect(request.headers["x-csrf-token"]).toBe(CSRF_TOKEN);
  expect(request.body).toBeUndefined();
}
