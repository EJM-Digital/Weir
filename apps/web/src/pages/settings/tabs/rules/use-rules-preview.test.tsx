import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { useRulesPreview } from "./use-rules-preview";

const mocks = vi.hoisted(() => ({ mutate: vi.fn(), pending: false }));

vi.mock("../../../../lib/processing/libraries-queries", () => ({
  useProcessingRulesPreview: () => ({
    mutate: mocks.mutate,
    isPending: mocks.pending,
  }),
}));

beforeEach(() => {
  vi.useFakeTimers();
  mocks.mutate.mockReset();
  mocks.pending = false;
});

afterEach(() => vi.useRealTimers());

it("previews with the library's own rules when no rules are being edited", () => {
  renderHook(() =>
    useRulesPreview({
      active: true,
      libraryId: 3,
      target: { mode: "library", relativePath: "Movies/Heat.mkv" },
      rules: null,
    }),
  );

  act(() => {
    vi.advanceTimersByTime(1000);
  });

  expect(mocks.mutate).toHaveBeenCalledWith(
    {
      libraryId: 3,
      relativePath: "Movies/Heat.mkv",
      absolutePath: undefined,
      rules: undefined,
    },
    expect.anything(),
  );
});

const heat = {
  active: true,
  libraryId: 3,
  target: { mode: "library", relativePath: "Movies/Heat.mkv" } as const,
  rules: null,
};

it("does not send again the very request a Preview press already sent, when the pause in typing ends", () => {
  const { result } = renderHook(() => useRulesPreview(heat));

  act(() => result.current.runNow?.());
  act(() => {
    vi.advanceTimersByTime(2000);
  });

  expect(mocks.mutate).toHaveBeenCalledTimes(1);
});

it("holds a change made while a preview is running until it ends, because the server runs one preview at a time", () => {
  mocks.pending = true;
  const { rerender } = renderHook(
    ({ name }) =>
      useRulesPreview({
        ...heat,
        target: { mode: "library", relativePath: name },
      }),
    { initialProps: { name: "Movies/Heat.mkv" } },
  );
  act(() => {
    vi.advanceTimersByTime(2000);
  });
  expect(mocks.mutate).not.toHaveBeenCalled();

  mocks.pending = false;
  rerender({ name: "Movies/Heat.mkv" });

  expect(mocks.mutate).toHaveBeenCalledTimes(1);
  expect(mocks.mutate).toHaveBeenCalledWith(
    expect.objectContaining({ relativePath: "Movies/Heat.mkv" }),
    expect.anything(),
  );
  act(() => {
    vi.advanceTimersByTime(2000);
  });
  expect(mocks.mutate).toHaveBeenCalledTimes(1);
});
