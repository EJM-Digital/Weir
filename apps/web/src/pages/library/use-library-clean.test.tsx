import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { useLibraryClean } from "./use-library-clean";

const mocks = vi.hoisted(() => ({ mutate: vi.fn() }));

vi.mock("../../lib/processing/library-mode-queries", () => ({
  useCleanLibraryFiles: () => ({
    mutate: mocks.mutate,
    reset: vi.fn(),
    isPending: false,
    isError: false,
    error: null,
  }),
}));

describe("confirming a clean", () => {
  it("cleans nothing when no clean is waiting for a confirmation", () => {
    const { result } = renderHook(() => useLibraryClean(1, vi.fn()));

    act(() => result.current.confirm());

    expect(mocks.mutate).not.toHaveBeenCalled();
  });
});
