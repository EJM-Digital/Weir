import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useNow } from "./use-now";

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", {
    configurable: true,
    get: () => hidden,
  });
  document.dispatchEvent(new Event("visibilitychange"));
}

describe("useNow", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    setHidden(false);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("moves forward on its own while the tab is visible", () => {
    const { result } = renderHook(() => useNow(1000));
    const first = result.current;

    act(() => {
      vi.advanceTimersByTime(3000);
    });

    expect(result.current).toBeGreaterThan(first);
  });

  it("shows every whole second of a countdown once, as the clock reaches it, starting mid-second", () => {
    const start = 10_000_400;
    vi.setSystemTime(start);
    const endsAt = 10_020_000;
    const left = (at: number) => Math.ceil((endsAt - at) / 1000);
    const { result } = renderHook(() => useNow(1000));
    const shown: number[] = [];

    for (let elapsed = 0; elapsed <= 15_000; elapsed += 200) {
      expect(left(result.current)).toBe(left(Date.now()));
      shown.push(left(result.current));
      act(() => {
        vi.advanceTimersByTime(200);
      });
    }

    const distinct = shown.filter(
      (value, i) => i === 0 || value !== shown[i - 1],
    );
    expect(distinct).toEqual(
      Array.from({ length: distinct.length }, (_, i) => distinct[0] - i),
    );
    expect(distinct.length).toBeGreaterThanOrEqual(15);
  });

  it("stops ticking while the tab is hidden, and catches up as soon as it is shown again", () => {
    const { result } = renderHook(() => useNow(1000));
    setHidden(true);
    const whenHidden = result.current;

    act(() => {
      vi.advanceTimersByTime(10_000);
    });
    expect(result.current).toBe(whenHidden);

    act(() => {
      vi.advanceTimersByTime(5_000);
      setHidden(false);
    });
    expect(result.current).toBeGreaterThan(whenHidden);
  });
});
