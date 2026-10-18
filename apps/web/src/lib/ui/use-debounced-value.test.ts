import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { useDebouncedValue } from "./use-debounced-value";

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

it("holds the first value, and takes a new one only once it has stopped changing for the delay", () => {
  const { result, rerender } = renderHook(
    ({ value }) => useDebouncedValue(value, 700),
    { initialProps: { value: "a" } },
  );
  expect(result.current).toBe("a");

  rerender({ value: "ab" });
  act(() => void vi.advanceTimersByTime(699));
  expect(result.current).toBe("a");
  act(() => void vi.advanceTimersByTime(1));
  expect(result.current).toBe("ab");
});

it("starts the wait again on each change, so only the last of a run of changes is taken", () => {
  const { result, rerender } = renderHook(
    ({ value }) => useDebouncedValue(value, 700),
    { initialProps: { value: "a" } },
  );

  rerender({ value: "ab" });
  act(() => void vi.advanceTimersByTime(600));
  rerender({ value: "abc" });
  act(() => void vi.advanceTimersByTime(600));
  expect(result.current).toBe("a");

  act(() => void vi.advanceTimersByTime(100));
  expect(result.current).toBe("abc");
});

it("takes nothing once it is gone", () => {
  const { rerender, unmount } = renderHook(
    ({ value }) => useDebouncedValue(value, 700),
    { initialProps: { value: "a" } },
  );
  rerender({ value: "ab" });

  unmount();

  expect(vi.getTimerCount()).toBe(0);
});
