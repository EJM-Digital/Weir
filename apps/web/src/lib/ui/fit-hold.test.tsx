import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { isFitHeld, onFitRelease, useFitHeld, useHoldFit } from "./fit-hold";

it("holds the fit while a popover is open and lets go when it closes or goes away", () => {
  const { rerender, unmount } = renderHook(({ open }) => useHoldFit(open), {
    initialProps: { open: false },
  });
  expect(isFitHeld()).toBe(false);

  rerender({ open: true });
  expect(isFitHeld()).toBe(true);

  rerender({ open: false });
  expect(isFitHeld()).toBe(false);

  rerender({ open: true });
  unmount();
  expect(isFitHeld()).toBe(false);
});

it("keeps holding until the last popover has closed, and then says so once", () => {
  const released = vi.fn();
  const stop = onFitRelease(released);
  const first = renderHook(() => useHoldFit(true));
  const second = renderHook(() => useHoldFit(true));

  first.unmount();
  expect(isFitHeld()).toBe(true);
  expect(released).not.toHaveBeenCalled();

  second.unmount();
  expect(isFitHeld()).toBe(false);
  expect(released).toHaveBeenCalledTimes(1);
  stop();
});

it("tells a reader of the hold when it changes", () => {
  const reader = renderHook(() => useFitHeld());
  expect(reader.result.current).toBe(false);

  const holder = renderHook(() => useHoldFit(true));
  expect(reader.result.current).toBe(true);

  act(() => holder.unmount());
  expect(reader.result.current).toBe(false);
});
