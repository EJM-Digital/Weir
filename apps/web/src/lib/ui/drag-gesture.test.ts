import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DragGesture, type DragOutcome } from "./drag-gesture";
import type { DragAxis } from "./drag-geometry";
import { SETTLE_MS } from "./drag-motion";

const SIZE = 100;
const FRAME_MS = 20;

type Setup = {
  gesture: DragGesture<string>;
  outcomes: DragOutcome<string>[];
  began: string[];
  items: Record<string, HTMLElement>;
  order: () => string[];
  scrollBy: ReturnType<typeof vi.fn>;
};

/** Three items laid out one after another along the axis, each SIZE long, shifted by the offset they were given. */
function lay(items: Record<string, HTMLElement>, axis: DragAxis) {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
    function (this: HTMLElement) {
      const index = Object.values(items).indexOf(this);
      const shift = new RegExp(
        `translate${axis.toUpperCase()}\\((-?[\\d.]+)px\\)`,
      ).exec(this.style.transform);
      const start = 100 + index * SIZE + Number(shift?.[1] ?? 0);
      const along =
        axis === "y" ? { top: start, left: 0 } : { left: start, top: 0 };
      return {
        ...along,
        width: axis === "x" ? SIZE : 300,
        height: axis === "y" ? SIZE : 300,
        right: 0,
        bottom: 0,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      };
    },
  );
}

function hold(
  axis: DragAxis,
  grab: number,
  options: { threshold?: number; ids?: string[]; withoutElement?: string } = {},
): Setup {
  const ids = options.ids ?? ["a", "b", "c"];
  const items: Record<string, HTMLElement> = {};
  for (const id of ids) {
    const item = document.createElement("div");
    document.body.append(item);
    if (id !== options.withoutElement) items[id] = item;
  }
  lay(items, axis);
  let order = [...ids];
  const outcomes: DragOutcome<string>[] = [];
  const began: string[] = [];
  const page = document.scrollingElement ?? document.documentElement;
  const scrollBy = vi.fn();
  Object.defineProperty(page, "scrollBy", {
    value: scrollBy,
    configurable: true,
  });
  const gesture = new DragGesture<string>(
    {
      axis,
      threshold: options.threshold ?? 0,
      ids: () => ids,
      order: () => order,
      elementsOf: (id) => (items[id] ? [items[id]] : []),
      reorder: (next) => {
        order = next;
      },
      began: (id) => began.push(id),
      ended: (outcome) => outcomes.push(outcome),
    },
    "a",
    axis === "y"
      ? { clientX: 0, clientY: grab }
      : { clientX: grab, clientY: 0 },
  );
  return { gesture, outcomes, began, items, order: () => order, scrollBy };
}

function moveTo(axis: DragAxis, position: number) {
  window.dispatchEvent(
    new MouseEvent("pointermove", {
      clientX: axis === "x" ? position : 0,
      clientY: axis === "y" ? position : 0,
    }),
  );
}

function reduceMotion(reduce: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: reduce && query.includes("reduce"),
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }));
}

beforeEach(() => {
  vi.useFakeTimers();
  reduceMotion(false);
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.replaceChildren();
  document.documentElement.classList.remove("mm-dragging");
});

describe.each(["x", "y"] as const)("a gesture along %s", (axis) => {
  it("reorders as the held item passes the others, and reports the drop once settled", () => {
    const run = hold(axis, 150);

    moveTo(axis, 360);
    expect(run.order()).toEqual(["b", "c", "a"]);
    window.dispatchEvent(new Event("pointerup"));
    expect(run.outcomes).toEqual([]);
    vi.advanceTimersByTime(SETTLE_MS);

    expect(run.outcomes).toEqual([{ kind: "drop", order: ["b", "c", "a"] }]);
  });

  it("scrolls towards the start edge, and towards the end edge, while held there", () => {
    const run = hold(axis, 150);
    const limit = axis === "y" ? window.innerHeight : window.innerWidth;

    moveTo(axis, 2);
    vi.advanceTimersByTime(FRAME_MS);
    moveTo(axis, limit - 2);
    vi.advanceTimersByTime(FRAME_MS);

    const [toStart, toEnd] = run.scrollBy.mock.calls.map(
      (call) =>
        (call[0] as Record<string, number>)[axis === "y" ? "top" : "left"],
    );
    expect(toStart).toBeLessThan(0);
    expect(toEnd).toBeGreaterThan(0);
  });

  it("does not scroll while the pointer is away from the edges", () => {
    const run = hold(axis, 150);

    moveTo(axis, 300);
    vi.advanceTimersByTime(FRAME_MS * 3);

    expect(run.scrollBy).not.toHaveBeenCalled();
  });

  it("stops scrolling once the item is let go", () => {
    const run = hold(axis, 150);
    moveTo(axis, 2);
    window.dispatchEvent(new Event("pointerup"));

    vi.advanceTimersByTime(SETTLE_MS + FRAME_MS * 3);

    expect(run.scrollBy).not.toHaveBeenCalled();
  });
});

describe("keys, cancelling and ending", () => {
  it("ignores every key but Escape", () => {
    const run = hold("y", 150);

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter" }));

    expect(run.outcomes).toEqual([]);
    expect(document.documentElement).toHaveClass("mm-dragging");
  });

  it("puts everything back on Escape while the item is held", () => {
    const run = hold("y", 150);

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));

    expect(run.outcomes).toEqual([{ kind: "cancel" }]);
    expect(document.documentElement).not.toHaveClass("mm-dragging");
  });

  it("reads Escape before the press became a drag as a plain click", () => {
    const run = hold("y", 150, { threshold: 8 });

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));

    expect(run.outcomes).toEqual([{ kind: "click" }]);
  });

  it("reads a release before the press became a drag as a click", () => {
    const run = hold("y", 150, { threshold: 8 });
    moveTo("y", 152);

    window.dispatchEvent(new Event("pointerup"));

    expect(run.outcomes).toEqual([{ kind: "click" }]);
    expect(run.began).toEqual([]);
  });

  it("reports an item dropped where it began as unchanged", () => {
    const run = hold("y", 150);
    moveTo("y", 160);

    window.dispatchEvent(new Event("pointerup"));
    vi.advanceTimersByTime(SETTLE_MS);

    expect(run.outcomes).toEqual([{ kind: "unchanged" }]);
  });

  it("settles at once for someone who asked for less motion", () => {
    reduceMotion(true);
    const run = hold("y", 150);
    moveTo("y", 360);

    window.dispatchEvent(new Event("pointerup"));

    expect(run.outcomes).toEqual([{ kind: "drop", order: ["b", "c", "a"] }]);
  });

  it("lets go of the page when the list goes away mid-drag", () => {
    const run = hold("y", 150);

    run.gesture.dispose();
    moveTo("y", 360);
    window.dispatchEvent(new Event("pointerup"));

    expect(run.outcomes).toEqual([]);
    expect(run.order()).toEqual(["a", "b", "c"]);
    expect(document.documentElement).not.toHaveClass("mm-dragging");
  });
});

describe("a gesture that cannot start or follow yet", () => {
  it("does not become a drag when the held item has no element on the page", () => {
    const run = hold("y", 150, { withoutElement: "a" });

    expect(run.began).toEqual([]);
    expect(document.documentElement).not.toHaveClass("mm-dragging");
  });

  it("leaves an item that has no element on the page at the end of the order", () => {
    const run = hold("y", 150, { withoutElement: "c" });

    moveTo("y", 500);

    expect(run.order()).toEqual(["b", "a", "c"]);
  });

  it("does not move anything for a layout pass before the press became a drag", () => {
    const run = hold("y", 150, { threshold: 8 });

    run.gesture.afterLayout();

    expect(run.items.a.style.transform).toBe("");
  });

  it("does not move the item for a layout pass while it settles", () => {
    const run = hold("y", 150);
    moveTo("y", 360);
    window.dispatchEvent(new Event("pointerup"));
    const settling = run.items.a.style.transform;

    run.gesture.afterLayout();

    expect(run.items.a.style.transform).toBe(settling);
  });

  it("measures the held item again after the list has redrawn", () => {
    const run = hold("y", 150);

    moveTo("y", 360);
    run.gesture.afterLayout();

    expect(run.order()).toEqual(["b", "c", "a"]);
  });
});
