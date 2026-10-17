import {
  DATABASE_BUSY_CODE,
  GENERIC_FAILURE_MESSAGE,
  NETWORK_UNREACHABLE_MESSAGE,
  SIGN_IN_ENDED_MESSAGE,
} from "./api-error-text";
import { ApiHttpError } from "./client";

const UNAUTHORIZED = 401;

/**
 * What to tell someone when something fails. A failed request already carries its plain-language
 * message (see `responseErrorText`); an `Error` Weir's own code threw carries a sentence written for
 * the operator. A fault inside the browser (a `TypeError` and the like) says nothing useful to a
 * person, so it gets `fallback`, which a caller sets to name what failed.
 */
export function errorMessage(
  error: unknown,
  fallback: string = GENERIC_FAILURE_MESSAGE,
): string {
  if (typeof error === "string") return error.trim() ? error : fallback;
  const browserFault =
    error instanceof Error &&
    !(error instanceof ApiHttpError) &&
    error.name !== "Error";
  if (browserFault) return fallback;
  if (error && typeof error === "object" && "message" in error) {
    const message = String((error as { message: unknown }).message ?? "");
    if (message.trim()) return message;
  }
  return fallback;
}

/**
 * What a panel says when its data could not be loaded: sign in again, check Weir is running, wait
 * out a busy Weir for the time it asks, or reload. `thing` names what was being loaded, as in
 * "Weir couldn't load {thing}."
 */
export function loadErrorMessage(error: unknown, thing: string): string {
  if (error instanceof ApiHttpError) {
    if (error.networkUnreachable) return NETWORK_UNREACHABLE_MESSAGE;
    if (error.status === UNAUTHORIZED) return SIGN_IN_ENDED_MESSAGE;
    if (error.code === DATABASE_BUSY_CODE) return error.message;
  }
  return `Weir couldn't load ${thing}. Reload the page to try again.`;
}
