import { afterEach, describe, expect, it, vi } from "vitest";

import {
  clamp,
  edgeScrollStep,
  moved,
  sameOrder,
  scrollParentOf,
  scrollPosition,
  sizeOf,
  slotIndex,
  startOf,
  visibleSpan,
} from "./drag-geometry";

afterEach(() => {
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

function box(left: number, top: number, width: number, height: number) {
  return {
    left,
    top,
    width,
    height,
    right: left + width,
    bottom: top + height,
    x: left,
    y: top,
    toJSON: () => ({}),
  } as DOMRect;
}

describe("clamp", () => {
  it.each([
    [5, 0, 10, 5],
    [-5, 0, 10, 0],
    [15, 0, 10, 10],
    [5, 8, 3, 8],
  ])("keeps %s between %s and %s as %s", (value, low, high, expected) => {
    expect(clamp(value, low, high)).toBe(expected);
  });
});

describe("moved and sameOrder", () => {
  it.each([
    ["a", 2, ["b", "c", "a"]],
    ["c", 0, ["c", "a", "b"]],
    ["b", 1, ["a", "b", "c"]],
  ])("moves %s to place %s", (id, index, expected) => {
    expect(moved(["a", "b", "c"], id, index)).toEqual(expected);
  });

  it.each([
    [["a", "b"], ["a", "b"], true],
    [["a", "b"], ["b", "a"], false],
    [["a", "b"], ["a"], false],
  ])("compares %j with %j as %s", (a, b, same) => {
    expect(sameOrder(a, b)).toBe(same);
  });
});

describe("slotIndex", () => {
  it.each([
    [10, 0],
    [50, 1],
    [90, 2],
  ])(
    "puts a held centre at %s in place %s among the centres 20 and 60",
    (held, index) => {
      expect(slotIndex([20, 60], held)).toBe(index);
    },
  );
});

describe("edgeScrollStep", () => {
  it.each([
    ["well inside the view", 300, 0],
    ["at the start edge", 0, -18],
    ["half way into the start zone", 28, -9],
    ["at the end edge", 600, 18],
    ["half way into the end zone", 572, 9],
    ["beyond the end", 700, 18],
  ])("scrolls %s by %s", (_where, pointer, step) => {
    expect(edgeScrollStep(pointer, 0, 600)).toBe(step);
  });

  it("treats the middle of a short view as its edge", () => {
    expect(edgeScrollStep(10, 0, 40)).toBe(-9);
    expect(edgeScrollStep(30, 0, 40)).toBe(9);
    expect(edgeScrollStep(20, 0, 40)).toBe(0);
  });
});

describe("axis readings", () => {
  const rect = box(10, 20, 30, 40);

  it("reads the start and size along each axis", () => {
    expect(startOf(rect, "y")).toBe(20);
    expect(startOf(rect, "x")).toBe(10);
    expect(sizeOf(rect, "y")).toBe(40);
    expect(sizeOf(rect, "x")).toBe(30);
  });

  it("reads the scroll position along each axis", () => {
    const scroller = document.createElement("div");
    scroller.scrollTop = 7;
    scroller.scrollLeft = 3;

    expect(scrollPosition(scroller, "y")).toBe(7);
    expect(scrollPosition(scroller, "x")).toBe(3);
  });
});

describe("scrollParentOf", () => {
  function nested(overflow: string, axis: "y" | "x", overflows: boolean) {
    const outer = document.createElement("div");
    const inner = document.createElement("div");
    const item = document.createElement("div");
    outer.append(inner);
    inner.append(item);
    document.body.append(outer);
    const style = axis === "y" ? "overflowY" : "overflowX";
    outer.style[style] = overflow;
    const [scrollSize, clientSize] =
      axis === "y"
        ? (["scrollHeight", "clientHeight"] as const)
        : (["scrollWidth", "clientWidth"] as const);
    Object.defineProperty(outer, scrollSize, { value: overflows ? 500 : 100 });
    Object.defineProperty(outer, clientSize, { value: 100 });
    return { outer, item };
  }

  it.each([
    ["y", "auto"],
    ["y", "scroll"],
    ["x", "auto"],
    ["x", "scroll"],
  ] as const)(
    "finds an ancestor that scrolls along %s with overflow %s",
    (axis, overflow) => {
      const { outer, item } = nested(overflow, axis, true);

      expect(scrollParentOf(item, axis)).toBe(outer);
    },
  );

  it.each([
    ["has nothing more to show", "auto", false],
    ["does not scroll", "visible", true],
  ] as const)(
    "passes over an ancestor that %s",
    (_why, overflow, overflows) => {
      const { outer, item } = nested(overflow, "y", overflows);

      expect(scrollParentOf(item, "y")).not.toBe(outer);
    },
  );

  it("falls back to the page when nothing above scrolls", () => {
    const { item } = nested("visible", "x", false);

    expect(scrollParentOf(item, "x")).toBe(
      document.scrollingElement ?? document.documentElement,
    );
  });
});

describe("visibleSpan", () => {
  it("is the window for the page itself", () => {
    const page = document.scrollingElement ?? document.documentElement;

    expect(visibleSpan(page, "y")).toEqual([0, window.innerHeight]);
    expect(visibleSpan(page, "x")).toEqual([0, window.innerWidth]);
  });

  it("is the scroller's own box for anything else", () => {
    const scroller = document.createElement("div");
    vi.spyOn(scroller, "getBoundingClientRect").mockReturnValue(
      box(10, 20, 300, 400),
    );

    expect(visibleSpan(scroller, "y")).toEqual([20, 420]);
    expect(visibleSpan(scroller, "x")).toEqual([10, 310]);
  });
});
