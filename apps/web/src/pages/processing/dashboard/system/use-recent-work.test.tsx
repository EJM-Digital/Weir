import { renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ActivityRecentFilters } from "../../../../lib/api/activity-api";
import { useRecentWork } from "./use-recent-work";

const PASSES = "processing.file_remux_pass_completed";
const CLEANS = "library.file_cleaned";

const asked: ActivityRecentFilters[] = [];
/** What the server has answered so far, by the kind of event asked for; nothing until it has. */
const answers: Record<string, { items: unknown[]; total: number } | undefined> =
  {};

vi.mock("../../../../lib/activity/queries", () => ({
  useActivityWindowQuery: (filters: ActivityRecentFilters) => {
    asked.push(filters);
    return { data: answers[filters.event_type ?? ""] };
  },
}));
vi.mock("../../../../lib/activity/use-activity-stream-invalidation", () => ({
  useActivityStreamInvalidations: vi.fn(),
}));

const NOW = Date.parse("2026-10-09T10:00:00Z");

beforeEach(() => {
  asked.length = 0;
  delete answers[PASSES];
  delete answers[CLEANS];
});

describe("the work finished in the last ten minutes", () => {
  it("counts each download once, as it stands now, so a skip or a failure it got past is not work done", () => {
    renderHook(() => useRecentWork(NOW));

    const passes = asked.find((filters) => filters.event_type === PASSES);
    expect(passes?.current_only).toBe(true);
  });

  it("is not known until both the passes and the cleans have been read", () => {
    answers[PASSES] = { items: [], total: 3 };

    const { result, rerender } = renderHook(() => useRecentWork(NOW));
    expect(result.current).toBeUndefined();

    delete answers[PASSES];
    answers[CLEANS] = { items: [], total: 2 };
    rerender();
    expect(result.current).toBeUndefined();
  });

  it("adds the passes and the library cleans once both have been read", () => {
    answers[PASSES] = { items: [], total: 3 };
    answers[CLEANS] = { items: [], total: 2 };

    const { result } = renderHook(() => useRecentWork(NOW));

    expect(result.current).toEqual({ done: 5, savedBytes: 0 });
  });
});
