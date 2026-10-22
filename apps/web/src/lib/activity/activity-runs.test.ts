import { describe, expect, it } from "vitest";
import type { ActivityEventItem } from "../api/types";
import { activityTriggerLabel, summarizeRun } from "./activity-runs";

function ev(
  overrides: Partial<ActivityEventItem> & { id: number },
): ActivityEventItem {
  return {
    created_at: "2026-08-12T22:00:00Z",
    event_type: "processing.file_remux_pass_completed",
    module: "processing",
    title: "t",
    ...overrides,
  };
}

describe("activity runs", () => {
  it("names triggers in plain words and never guesses a missing one", () => {
    expect(activityTriggerLabel("manual")).toBe("You started this");
    expect(activityTriggerLabel("webhook")).toBe("From your media manager");
    expect(activityTriggerLabel(null)).toBeNull();
    expect(activityTriggerLabel("unheard_of")).toBeNull();
  });

  it("counts the newest outcome per file once", () => {
    const summary = summarizeRun([
      ev({
        id: 3,
        relative_path: "a.mkv",
        detail: '{"outcome":"ok"}',
        trigger: "retry",
      }),
      ev({ id: 2, relative_path: "a.mkv", result: "failed" }),
      ev({ id: 1, relative_path: "b.mkv", result: "failed" }),
    ]);
    expect(summary.headline).toBe(
      "Automatic retry run · 2 files: 1 processed, 1 failed",
    );
    expect(summary.failed).toBe(1);
  });

  it("counts a pass that skipped its file as skipped, not processed", () => {
    const summary = summarizeRun([
      ev({
        id: 2,
        relative_path: "a.mkv",
        result: "skipped",
        detail: '{"outcome":"skipped_guardrail"}',
      }),
      ev({ id: 1, relative_path: "b.mkv", detail: '{"outcome":"ok"}' }),
    ]);

    expect(summary.headline).toContain("1 skipped");
    expect(summary.headline).toContain("1 processed");
  });

  it("falls back to entry counts when no file is named", () => {
    expect(
      summarizeRun([
        ev({ id: 2, event_type: "processing.handoff_reported" }),
        ev({ id: 1, event_type: "processing.handoff_reported" }),
      ]).headline,
    ).toBe("Run · 2 entries");
  });

  it.each([
    [
      "a file passed through",
      { event_type: "processing.file_passed_through" },
      "passed through",
    ],
    [
      "a file whose rejection fell back to passing through",
      { event_type: "processing.file_reject_fell_back" },
      "passed through",
    ],
    [
      "a file rejected by the rules",
      { event_type: "processing.file_rejected" },
      "rejected",
    ],
    [
      "a pass that left the file unchanged",
      { detail: '{"pass_through_unchanged":true}' },
      "passed through",
    ],
    [
      "a pass that found nothing to change",
      { detail: '{"outcome":"live_skipped_not_required"}' },
      "no changes needed",
    ],
    ["a pass whose detail cannot be read", { detail: "words" }, "processed"],
    ["a pass with no detail", {}, "processed"],
  ])("counts %s", (_name, entry, outcome) => {
    expect(
      summarizeRun([ev({ id: 1, relative_path: "a.mkv", ...entry })]).headline,
    ).toBe(`Run · 1 file: 1 ${outcome}`);
  });

  it("counts a final outcome of its own for each entry that names no file", () => {
    expect(
      summarizeRun([
        ev({ id: 2, event_type: "processing.file_rejected" }),
        ev({ id: 1, event_type: "processing.file_rejected" }),
      ]).headline,
    ).toBe("Run · 2 entries: 2 rejected");
  });

  it("names no outcome for entries that are not one", () => {
    expect(
      summarizeRun([
        ev({
          id: 1,
          relative_path: "a.mkv",
          event_type: "processing.handoff_reported",
        }),
      ]),
    ).toEqual({ headline: "Run · 1 file", failed: 0 });
  });
});
