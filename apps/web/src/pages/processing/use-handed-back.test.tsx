import { renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ActivityRecentFilters } from "../../lib/api/activity-api";
import { useHandedBack } from "./use-handed-back";

const asked: ActivityRecentFilters[] = [];
const answers: Record<
  string,
  { items: never[]; total: number; complete: boolean }
> = {};

vi.mock("../../lib/activity/queries", () => ({
  useActivityWindowQuery: (filters: ActivityRecentFilters) => {
    asked.push(filters);
    return { data: answers[filters.event_type ?? ""] };
  },
}));

beforeEach(() => {
  asked.length = 0;
  for (const key of Object.keys(answers)) delete answers[key];
});

describe("the last two hours of finished files", () => {
  it("counts each download once, as it stands now, the way Just finished lists it", () => {
    renderHook(() => useHandedBack("all", Date.parse("2026-10-09T10:00:00Z")));

    const passes = asked.find(
      (filters) =>
        filters.event_type === "processing.file_remux_pass_completed",
    );
    expect(passes?.current_only).toBe(true);
  });
});

describe("the count of files handed back", () => {
  const NOW = Date.parse("2026-10-09T10:00:00Z");

  it("is the server's count when either kind has more files than were read, counting a kind not read yet as none", () => {
    answers["processing.file_remux_pass_completed"] = {
      items: [],
      total: 120,
      complete: false,
    };

    const { result } = renderHook(() => useHandedBack("all", NOW));

    expect(result.current.partial).toBe(true);
    expect(result.current.total).toBe(120);
  });

  it("is what was read when every file was read", () => {
    answers["processing.file_remux_pass_completed"] = {
      items: [],
      total: 0,
      complete: true,
    };

    const { result } = renderHook(() => useHandedBack("all", NOW));

    expect(result.current.partial).toBe(false);
    expect(result.current.total).toBe(0);
  });

  it("leaves out the kind the filter does not count", () => {
    answers["processing.file_remux_pass_completed"] = {
      items: [],
      total: 50,
      complete: false,
    };

    const { result } = renderHook(() => useHandedBack("library", NOW));

    expect(result.current.partial).toBe(false);
  });
});
