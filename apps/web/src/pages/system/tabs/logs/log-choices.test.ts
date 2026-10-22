import { describe, expect, it } from "vitest";

import type { SystemLogPage } from "../../../../lib/system/system-log-api";
import { choicesLeftEmpty, workflowHasRows } from "./log-choices";
import { EMPTY_LOG_FILTERS } from "./log-filters";

const COUNTS: SystemLogPage["counts"] = {
  category: {
    processing: 5,
    scans: 0,
    cleanup: 0,
    library: 0,
    connections: 0,
    backups: 0,
    sign_in: 0,
    updates: 0,
    weir: 0,
  },
  level: { error: 0, warning: 2, info: 3, success: 0 },
  source: { event: 4, job: 1, server: 0 },
  workflow: { "3": 2 },
};

describe("whether a workflow has any rows", () => {
  it("says yes until anything has been counted", () => {
    expect(workflowHasRows(undefined, 9)).toBe(true);
  });

  it("says yes for a workflow with rows and no for one with none, or one the counts do not list", () => {
    expect(workflowHasRows(COUNTS, 3)).toBe(true);
    expect(workflowHasRows(COUNTS, 9)).toBe(false);
  });
});

describe("what a change of source leaves with nothing to show", () => {
  it("is nothing when every choice still has rows", () => {
    expect(
      choicesLeftEmpty(
        { ...EMPTY_LOG_FILTERS, categories: ["processing"], levels: ["info"] },
        COUNTS,
      ),
    ).toBeNull();
  });

  it("names the categories and levels that have no rows left", () => {
    expect(
      choicesLeftEmpty(
        {
          ...EMPTY_LOG_FILTERS,
          categories: ["processing", "scans"],
          levels: ["error", "warning"],
        },
        COUNTS,
      ),
    ).toEqual({ categories: ["processing"], levels: ["warning"] });
  });

  it("drops the workflow for the server log, whose lines belong to none, and keeps it for any other source", () => {
    expect(
      choicesLeftEmpty(
        { ...EMPTY_LOG_FILTERS, source: "server", workflow: 3 },
        COUNTS,
      ),
    ).toEqual({ workflow: null });
    expect(
      choicesLeftEmpty(
        { ...EMPTY_LOG_FILTERS, source: "job", workflow: 3 },
        COUNTS,
      ),
    ).toBeNull();
  });
});
