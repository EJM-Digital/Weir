import { act } from "@testing-library/react";
import { vi } from "vitest";

/**
 * The hook Testing Library looks for before it moves fake timers on while `waitFor` and the `findBy` queries wait: it
 * runs on Jest, so under Vitest it must be told, once, how to advance the clock.
 */
type JestHook = { advanceTimersByTime: (ms: number) => void };

/**
 * Fake timers for a test whose behaviour is a timer (a debounce, a poll, a delay). Nothing moves until the test says so
 * with {@link elapse}, or until a `waitFor` or `findBy` moves it in small steps, so the wait is the clock's, never the wall's.
 */
export function useFakeClock(config?: Parameters<typeof vi.useFakeTimers>[0]) {
  vi.useFakeTimers(config);
  (globalThis as { jest?: JestHook }).jest = {
    advanceTimersByTime: (ms) => vi.advanceTimersByTime(ms),
  };
}

/** Undoes {@link useFakeClock}. */
export function useRealClock() {
  vi.useRealTimers();
  delete (globalThis as { jest?: JestHook }).jest;
}

/** Moves the fake clock on by `ms`, inside `act` so what the page shows has been drawn when it returns. */
export async function elapse(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}
