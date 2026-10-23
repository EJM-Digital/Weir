import { useEffect, useSyncExternalStore } from "react";

/**
 * A header refits by moving controls (a picker into its card, a chip into More), and moving a control remounts it, which
 * closes whatever it has open. So while any popover is open the layout stands still: every open popover holds the fit,
 * and the fit waits for the last of them to close (see useFitLevels).
 */
let holds = 0;
const listeners = new Set<() => void>();

function changeHolds(by: number): void {
  holds += by;
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Whether a popover is open right now, read outside React's render (an observer's callback, say). */
export function isFitHeld(): boolean {
  return holds > 0;
}

/** Holds the fit while `active`, until it is false again or the caller unmounts. */
export function useHoldFit(active: boolean): void {
  useEffect(() => {
    if (!active) return undefined;
    changeHolds(1);
    return () => changeHolds(-1);
  }, [active]);
}

/** Whether a popover is open, so the fit must wait. */
export function useFitHeld(): boolean {
  return useSyncExternalStore(subscribe, isFitHeld);
}

/** Calls `onRelease` whenever the last hold lets go. */
export function onFitRelease(onRelease: () => void): () => void {
  let held = isFitHeld();
  return subscribe(() => {
    const now = isFitHeld();
    if (held && !now) onRelease();
    held = now;
  });
}
