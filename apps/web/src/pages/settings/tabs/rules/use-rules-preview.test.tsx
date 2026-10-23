import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { useRulesPreview } from "./use-rules-preview";

const mocks = vi.hoisted(() => ({ mutate: vi.fn() }));

vi.mock("../../../../lib/processing/libraries-queries", () => ({
  useProcessingRulesPreview: () => ({
    mutate: mocks.mutate,
    isPending: false,
  }),
}));

beforeEach(() => {
  vi.useFakeTimers();
  mocks.mutate.mockReset();
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
