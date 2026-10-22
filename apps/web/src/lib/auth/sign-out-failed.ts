import { useSyncExternalStore } from "react";

/**
 * Whether the last sign-out did not go through. Signing out shows the sign-in page at once and ends the session behind it, so a
 * sign-out the server refuses leaves the person signed in on a page that looked as if they were not. This outlives the pages
 * that asked, so the app they land back on can say so.
 */
let failed = false;
const listeners = new Set<() => void>();

function set(next: boolean): void {
  if (failed === next) return;
  failed = next;
  listeners.forEach((listener) => listener());
}

export function markSignOutFailed(): void {
  set(true);
}

export function clearSignOutFailed(): void {
  set(false);
}

export function useSignOutFailed(): boolean {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => failed,
  );
}
