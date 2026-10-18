import { afterEach, describe, expect, it, vi } from "vitest";

import { motionAllowed } from "./motion-allowed";
import { RESIZING_CLASS, windowIsResizing } from "./resizing-class";

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.classList.remove(RESIZING_CLASS);
});

function reducedMotion(reduce: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: reduce && query === "(prefers-reduced-motion: reduce)",
  }));
}

describe("motionAllowed", () => {
  it("allows movement in a tab that is showing, for someone who has not asked for less", () => {
    reducedMotion(false);

    expect(motionAllowed()).toBe(true);
  });

  it("allows none in a tab nobody is looking at", () => {
    reducedMotion(false);
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");

    expect(motionAllowed()).toBe(false);
  });

  it("allows none for someone who has asked for reduced motion", () => {
    reducedMotion(true);

    expect(motionAllowed()).toBe(false);
  });

  it("allows movement where the browser cannot say what is preferred", () => {
    vi.stubGlobal("matchMedia", undefined);

    expect(motionAllowed()).toBe(true);
  });
});

describe("windowIsResizing", () => {
  it("is true only while the document carries the resizing class", () => {
    expect(windowIsResizing()).toBe(false);

    document.body.classList.add(RESIZING_CLASS);
    expect(windowIsResizing()).toBe(true);

    document.body.classList.remove(RESIZING_CLASS);
    expect(windowIsResizing()).toBe(false);
  });
});
