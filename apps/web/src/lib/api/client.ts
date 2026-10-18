/**
 * Browser API client: cookie session auth with `credentials: 'include'`. No tokens in
 * localStorage; the server's session and its cookie are authoritative.
 */

import {
  NETWORK_UNREACHABLE_MESSAGE,
  TIMED_OUT_MESSAGE,
  codeOf,
  responseErrorText,
} from "./api-error-text";

export { NETWORK_UNREACHABLE_MESSAGE };

const API_PREFIX = "/api/v1";
const DEFAULT_TIMEOUT_MS = 30_000;
type UnauthorizedHandler = (path: string) => void;

export type ApiFetchInit = RequestInit & {
  timeoutMs?: number;
};

export class ApiHttpError extends Error {
  readonly status: number;
  readonly path: string;
  readonly detail: unknown;
  readonly timedOut: boolean;
  /**
   * True when this error was synthesised from a `fetch` rejection because the server
   * could not be reached at all — as opposed to the server answering with an HTTP
   * error, or the request timing out. See {@link NETWORK_UNREACHABLE_MESSAGE}.
   */
  readonly networkUnreachable: boolean;
  /** The `code` the server put on its answer, when it gave one (for example `database_busy`). */
  readonly code: string | undefined;

  constructor(
    path: string,
    status: number,
    message: string,
    detail?: unknown,
    timedOut = false,
    networkUnreachable = false,
    code?: string,
  ) {
    super(message);
    this.name = "ApiHttpError";
    this.status = status;
    this.path = path;
    this.detail = detail;
    this.timedOut = timedOut;
    this.networkUnreachable = networkUnreachable;
    this.code = code;
  }
}

/**
 * True for the `TypeError` (or equivalent) a rejected `fetch` throws when the request
 * never reached a server — never true for a `Response` that came back with an error
 * status, which is a different situation with its own handling in
 * {@link apiResponseErrorMessage}. Browsers word it ("Failed to fetch", "NetworkError…",
 * "Load failed") in ways that tell a user nothing, so it becomes {@link NETWORK_UNREACHABLE_MESSAGE}.
 */
function isNetworkUnreachableError(error: unknown): boolean {
  if (error instanceof TypeError) {
    return true;
  }
  if (!(error instanceof Error)) {
    return false;
  }
  const m = error.message;
  return (
    m === "Failed to fetch" ||
    m.includes("NetworkError") ||
    m.includes("Load failed") ||
    m.includes("Failed to retrieve")
  );
}

let unauthorizedHandler: UnauthorizedHandler | null = null;
let unauthorizedHandled = false;

export function setUnauthorizedHandler(
  handler: UnauthorizedHandler | null,
): void {
  unauthorizedHandler = handler;
  unauthorizedHandled = false;
}

export function resetUnauthorizedHandlingForTests(): void {
  unauthorizedHandler = null;
  unauthorizedHandled = false;
}

function baseUrl(): string {
  // In `vite dev`, always use same-origin `/api` so the dev proxy applies (including
  // `WEIR_DEV_STACK_API_PROXY_TARGET` when the API moved to a fallback port). A pinned
  // `VITE_API_BASE_URL` in `.env` would otherwise bypass the proxy and keep talking to an old API
  // process on the previous port.
  if (import.meta.env.DEV) {
    return "";
  }
  const raw = import.meta.env.VITE_API_BASE_URL?.trim();
  return raw ? raw.replace(/\/$/, "") : "";
}

export function apiUrl(path: string): string {
  const p = path.startsWith("/") ? path : `/${path}`;
  if (!p.startsWith(API_PREFIX)) {
    throw new Error(`API paths must be under ${API_PREFIX}`);
  }
  return `${baseUrl()}${p}`;
}

function buildTimeoutSignal(timeoutMs: number): AbortSignal | null {
  if (typeof AbortSignal === "undefined") {
    return null;
  }
  if (typeof AbortSignal.timeout === "function") {
    return AbortSignal.timeout(timeoutMs);
  }
  return null;
}

function combineSignals(
  callerSignal: AbortSignal | null | undefined,
  timeoutSignal: AbortSignal | null,
): AbortSignal | null {
  if (callerSignal && timeoutSignal && typeof AbortSignal.any === "function") {
    return AbortSignal.any([callerSignal, timeoutSignal]);
  }
  return callerSignal ?? timeoutSignal;
}

function isTimeoutAbort(
  error: unknown,
  timeoutSignal: AbortSignal | null,
): boolean {
  if (
    !error ||
    typeof error !== "object" ||
    !("name" in error) ||
    error.name !== "AbortError"
  ) {
    return false;
  }
  return Boolean(timeoutSignal?.aborted);
}

export async function apiFetch(
  path: string,
  init?: ApiFetchInit,
): Promise<Response> {
  const {
    timeoutMs = DEFAULT_TIMEOUT_MS,
    signal: callerSignal,
    ...requestInit
  } = init ?? {};
  const timeoutSignal = buildTimeoutSignal(timeoutMs);
  const signal = combineSignals(callerSignal ?? null, timeoutSignal);
  let response: Response;
  try {
    response = await fetch(apiUrl(path), {
      ...requestInit,
      credentials: "include",
      signal: signal ?? undefined,
      headers: {
        Accept: "application/json",
        "X-Requested-With": "XMLHttpRequest",
        ...requestInit.headers,
      },
    });
  } catch (error) {
    if (isTimeoutAbort(error, timeoutSignal)) {
      throw new ApiHttpError(path, 0, TIMED_OUT_MESSAGE, undefined, true);
    }
    if (isNetworkUnreachableError(error)) {
      throw new ApiHttpError(
        path,
        0,
        NETWORK_UNREACHABLE_MESSAGE,
        undefined,
        false,
        true,
      );
    }
    throw error;
  }
  if (response.status === 401 && unauthorizedHandler && !unauthorizedHandled) {
    unauthorizedHandled = true;
    unauthorizedHandler(path);
  }
  if (response.status !== 401) {
    unauthorizedHandled = false;
  }
  return response;
}

export async function readJson<T>(r: Response): Promise<T> {
  const text = await r.text();
  if (!text) {
    return undefined as T;
  }
  return JSON.parse(text) as T;
}

async function readJsonBody(r: Response): Promise<unknown> {
  const ctype = (r.headers.get("content-type") || "").toLowerCase();
  if (!ctype.includes("application/json")) return undefined;
  try {
    return (await r.clone().json()) as unknown;
  } catch (error) {
    // A body that claims JSON but isn't is treated like no body: the caller's fallback is said instead.
    if (error instanceof SyntaxError) return undefined;
    throw error;
  }
}

/**
 * The plain-language message for an error answer (see `responseErrorText`), with the server's
 * `detail` kept alongside for a caller that reads its structure.
 */
export async function apiResponseErrorMessage(
  path: string,
  r: Response,
  fallback: string,
): Promise<{ message: string; detail?: unknown; code?: string }> {
  const body = await readJsonBody(r);
  const message = responseErrorText({
    path,
    status: r.status,
    body,
    fallback,
  });
  const detail =
    typeof body === "object" && body !== null && "detail" in body
      ? (body as { detail: unknown }).detail
      : undefined;
  return { message, detail, code: codeOf(body) };
}

export async function throwApiResponseError(
  path: string,
  r: Response,
  fallback: string,
): Promise<never> {
  const normalized = await apiResponseErrorMessage(path, r, fallback);
  throw new ApiHttpError(
    path,
    r.status,
    normalized.message,
    normalized.detail,
    false,
    false,
    normalized.code,
  );
}

export async function requireOk(
  path: string,
  r: Response,
  fallback: string,
): Promise<void> {
  if (!r.ok) {
    await throwApiResponseError(path, r, fallback);
  }
}
