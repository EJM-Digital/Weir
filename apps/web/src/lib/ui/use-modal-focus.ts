import { useEffect, useRef, type RefObject } from "react";

/**
 * Open modals, newest last. Escape closes only the newest, so a dialog over a panel leaves the
 * panel open. Tracked by the layer's own DOM element rather than an opaque token (#749): removing a
 * modal's element from the document happens synchronously when React commits the unmount, before its
 * `useEffect` cleanup runs, so a stale entry — one whose cleanup is still pending, or that a test left
 * mounted at teardown — is always identifiable and pruned before it can outrank the real topmost layer.
 */
const openLayers: HTMLElement[] = [];

/** Drops any layer whose element has already left the document, then returns what remains on top. */
function topOpenLayer(): HTMLElement | undefined {
  for (let index = openLayers.length - 1; index >= 0; index -= 1) {
    if (!openLayers[index].isConnected) openLayers.splice(index, 1);
  }
  return openLayers[openLayers.length - 1];
}

/** What a layer sets on everything outside it, so it cannot be clicked, tabbed to or read while the layer is open. */
const INERT = "inert";

/** Marks a sibling of a modal layer that has to keep working while it is open, such as the backdrop that closes it when clicked. */
export const MODAL_COMPANION_ATTRIBUTE = "data-modal-companion";

/** Elements this code made inert, and how many open layers want each of them inert. */
const inertHolds = new Map<Element, number>();

/** Whether `element` is now held inert by this code; one that was inert already is somebody else's to free. */
function holdInert(element: Element): boolean {
  const holds = inertHolds.get(element);
  if (holds !== undefined) {
    inertHolds.set(element, holds + 1);
    return true;
  }
  if (element.hasAttribute(INERT)) return false;
  element.setAttribute(INERT, "");
  inertHolds.set(element, 1);
  return true;
}

function releaseInert(element: Element): void {
  const holds = inertHolds.get(element);
  if (holds === undefined) return;
  if (holds > 1) {
    inertHolds.set(element, holds - 1);
    return;
  }
  inertHolds.delete(element);
  element.removeAttribute(INERT);
}

/**
 * Makes everything outside a modal layer inert: at every level from the layer up to the page, each sibling of the branch the layer
 * sits on, so the side menu, the header and the page behind a dialog are out of reach of the pointer, the keyboard and a screen reader.
 * The branch itself stays, and so does anything marked {@link MODAL_COMPANION_ATTRIBUTE}. Returns what frees them again.
 */
function makeOutsideInert(layer: HTMLElement): () => void {
  const held: Element[] = [];
  for (
    let branch: Element | null = layer.closest("[aria-modal='true']");
    branch?.parentElement && branch !== document.documentElement;
    branch = branch.parentElement
  ) {
    for (const sibling of Array.from(branch.parentElement.children)) {
      const skipped =
        sibling === branch ||
        sibling.hasAttribute(MODAL_COMPANION_ATTRIBUTE) ||
        sibling.matches("head, script, style, link, template");
      if (!skipped && holdInert(sibling)) held.push(sibling);
    }
  }
  return () => held.forEach(releaseInert);
}

/**
 * The last element that took focus anywhere on the page. A button that disables itself while it loads what a dialog shows loses
 * focus to the page the moment it does, so by the time the dialog opens nothing has focus; this is who had it before.
 */
let lastFocused: Element | null = null;
if (typeof document !== "undefined") {
  document.addEventListener(
    "focusin",
    (event) => {
      if (event.target instanceof Element) lastFocused = event.target;
    },
    true,
  );
}

/** Where focus goes back to when a layer closes: the element that had it as the layer opened, or the last one that did. */
function focusToReturnTo(): Element | null {
  const active = document.activeElement;
  const nothingFocused =
    !active || active === document.body || active === document.documentElement;
  return nothingFocused ? lastFocused : active;
}

/** Everything Tab can land on. */
const TABBABLE = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled]):not([type='hidden'])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  "summary",
  "[tabindex]:not([tabindex='-1'])",
  "[contenteditable='true']",
].join(",");

function tabbableWithin(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(TABBABLE)).filter(
    (element) => !element.closest("[hidden], [inert]"),
  );
}

/**
 * Keeps Tab inside a layer that declares `aria-modal`: past the last control it wraps to the first,
 * and before the first to the last. Anything not modal lets Tab leave as usual.
 */
function keepTabInside(container: HTMLElement, event: KeyboardEvent): void {
  if (!container.closest("[aria-modal='true']")) return;
  const stops = tabbableWithin(container);
  if (stops.length === 0) {
    event.preventDefault();
    container.focus();
    return;
  }
  const first = stops[0];
  const last = stops[stops.length - 1];
  const active = document.activeElement;
  const outside = !(active instanceof Node) || !container.contains(active);
  if (event.shiftKey && (outside || active === first || active === container)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && (outside || active === last)) {
    event.preventDefault();
    first.focus();
  }
}

/**
 * Keyboard behaviour for anything modal. On open, focus moves to `initialFocus` when given (a
 * confirmation points it at the safe choice), otherwise to the returned ref's element; Tab stays
 * inside while the layer declares `aria-modal`; Escape closes unless `busy`; on close, focus goes back
 * to whatever had it, if that is still on the page. A layer that declares `aria-modal` also makes everything outside it inert while it is
 * open (see {@link makeOutsideInert}). `lockScroll` stops the page behind a slide-over
 * from scrolling.
 */
export function useModalFocus<T extends HTMLElement>({
  open = true,
  onClose,
  busy = false,
  lockScroll = false,
  initialFocus,
}: {
  open?: boolean;
  onClose: () => void;
  busy?: boolean;
  lockScroll?: boolean;
  initialFocus?: RefObject<HTMLElement | null>;
}): RefObject<T | null> {
  const target = useRef<T>(null);
  // Everything but `open` is read through a ref. A parent that re-renders with a new onClose (every
  // keystroke in a form inside the panel) must not re-run the effect, or focus jumps out of the
  // field being typed in (#697).
  const latest = useRef({ onClose, busy, lockScroll, initialFocus });
  useEffect(() => {
    latest.current = { onClose, busy, lockScroll, initialFocus };
  });

  useEffect(() => {
    const layer = target.current;
    if (!open || !layer) return undefined;
    const returnTo = focusToReturnTo();
    const locksScroll = latest.current.lockScroll;
    openLayers.push(layer);
    const freeOutside = makeOutsideInert(layer);
    (latest.current.initialFocus?.current ?? target.current)?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (topOpenLayer() !== layer) return;
      if (event.key === "Escape" && !latest.current.busy) {
        latest.current.onClose();
      } else if (event.key === "Tab" && target.current) {
        keepTabInside(target.current, event);
      }
    };
    document.addEventListener("keydown", onKey);
    if (locksScroll) document.body.classList.add("mm-drawer-open");
    return () => {
      const index = openLayers.indexOf(layer);
      if (index !== -1) openLayers.splice(index, 1);
      document.removeEventListener("keydown", onKey);
      if (locksScroll) document.body.classList.remove("mm-drawer-open");
      // Freed first: focus cannot return to a button that is still inert.
      freeOutside();
      if (returnTo instanceof HTMLElement && returnTo.isConnected) {
        returnTo.focus();
      }
    };
  }, [open]);

  return target;
}
