import { useEffect } from "react";
import { useBlocker, type Location } from "react-router-dom";

/** Another address is another screen, which is what takes a page's own edits away. */
function otherScreen(current: Location, next: Location): boolean {
  return current.pathname !== next.pathname;
}

/**
 * While `dirty`, leaving for another screen waits on the returned blocker (the caller asks), and
 * closing or reloading the tab gets the browser's own prompt. Moving between tabs of the same
 * screen is not leaving, so it is never blocked, unless the caller says which moves take the edits
 * away (`takesEditsAway`) because the edits live in a tab and not in the screen.
 */
export function useUnsavedChangesGuard(
  dirty: boolean,
  takesEditsAway: (current: Location, next: Location) => boolean = otherScreen,
) {
  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      dirty && takesEditsAway(currentLocation, nextLocation),
  );

  useEffect(() => {
    if (!dirty) return undefined;
    const holdUnload = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", holdUnload);
    return () => window.removeEventListener("beforeunload", holdUnload);
  }, [dirty]);

  return blocker;
}
