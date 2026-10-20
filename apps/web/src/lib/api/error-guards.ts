import { ApiHttpError } from "./client";

/** Failed to reach the server at all, as opposed to the server answering with an error. */
export function isLikelyNetworkFailure(error: unknown): boolean {
  // apiFetch classifies its own fetch rejections and rethrows them as an ApiHttpError marked
  // networkUnreachable, so every screen built on it gets the classification without re-deriving it.
  if (error instanceof ApiHttpError) return error.networkUnreachable;
  // A bare fetch outside apiFetch rejects with a TypeError, or one of these messages, when no
  // response ever arrived.
  if (error instanceof TypeError) return true;
  if (!(error instanceof Error)) return false;
  const m = error.message;
  return (
    m === "Failed to fetch" ||
    m.includes("NetworkError") ||
    m.includes("Load failed") ||
    m.includes("Failed to retrieve")
  );
}

/** True when the API was reached and answered with an HTTP status. */
export function isHttpErrorFromApi(error: unknown): error is ApiHttpError {
  return error instanceof ApiHttpError;
}

export function httpStatusFromApiError(error: unknown): number | null {
  return error instanceof ApiHttpError ? error.status : null;
}

const VITE_PROXY_UPSTREAM_DOWN_STATUS = 500;

/**
 * In `vite dev`, proxied `/api/*` requests that cannot reach the backend still produce an HTTP
 * response: the dev server returns 500 with an empty or plain body. The real API avoids 500 on
 * guest-first routes (bootstrap uses 503 for database issues), so a 500 in development is almost
 * always "the API process is not listening".
 */
export function isLikelyViteProxyUpstreamDown(error: unknown): boolean {
  if (!import.meta.env.DEV) return false;
  return httpStatusFromApiError(error) === VITE_PROXY_UPSTREAM_DOWN_STATUS;
}
