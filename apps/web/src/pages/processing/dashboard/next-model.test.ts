import { describe, expect, it } from "vitest";

import type { ProcessingLibrary } from "../../../lib/processing/libraries-api";
import type { MaintenanceFamilyState } from "../../../lib/processing/maintenance-api";
import {
  DAY_SECONDS,
  figureWords,
  lineWords,
  nextItems,
  waitFraction,
  type NextItem,
} from "./next-model";

const NOW = new Date(2026, 7, 18, 10, 0, 0).getTime();
const seconds = (n: number) => NOW + n * 1000;
const minutes = (n: number) => seconds(n * 60);
const iso = (ms: number) => new Date(ms).toISOString();

function workflow(overrides: Partial<ProcessingLibrary>): ProcessingLibrary {
  return {
    id: 1,
    name: "TV",
    enabled: true,
    watched_folder: "D:/downloads/tv",
    scan_interval_seconds: 300,
    next_look_at: iso(seconds(42)),
    ...overrides,
  } as ProcessingLibrary;
}

function cleanupJob(
  overrides: Partial<MaintenanceFamilyState>,
): MaintenanceFamilyState {
  return {
    family: "work_temp_stale_sweep",
    enabled: true,
    description: "",
    pending: 0,
    running: 0,
    last_completed_at: null,
    last_failed_at: null,
    last_error: null,
    interval_seconds: 3600,
    next_run_at: iso(minutes(20)),
    ...overrides,
  };
}

describe("what Weir does next on its own", () => {
  it("puts the soonest first: a workflow's next look, a library clean and a cleanup job", () => {
    const items = nextItems({
      workflows: [
        workflow({ id: 1 }),
        workflow({ id: 2, name: "Movies", next_look_at: iso(minutes(4)) }),
      ],
      cleanRuns: [
        { libraryId: 2, libraryName: "Movies", nextRunAt: iso(minutes(600)) },
      ],
      cleanupJobs: [cleanupJob({})],
    });

    expect(items.map((item) => item.key)).toEqual([
      "scan-1",
      "scan-2",
      "cleanup-work_temp_stale_sweep",
      "clean-2",
    ]);
    expect(items[0].label).toBe("Scan TV");
    expect(items[2].label).toBe("Clear leftover files");
    expect(items[3].label).toBe("Clean Movies library");
  });

  it("leaves out what is switched off or has no time, rather than guessing", () => {
    const items = nextItems({
      workflows: [
        workflow({ id: 1, enabled: false }),
        workflow({ id: 2, next_look_at: null }),
        workflow({ id: 3, watched_folder: " " }),
      ],
      cleanRuns: [{ libraryId: 4, libraryName: "Movies", nextRunAt: null }],
      cleanupJobs: [
        cleanupJob({ enabled: false }),
        cleanupJob({ next_run_at: null }),
      ],
    });

    expect(items).toEqual([]);
  });

  it("lists a cleanup job with no interval of its own, with no interval", () => {
    const withoutInterval = {
      ...cleanupJob({}),
      interval_seconds: undefined,
    };

    const [item] = nextItems({
      workflows: [],
      cleanRuns: [],
      cleanupJobs: [withoutInterval],
      filter: "all",
    });

    expect(item.intervalSeconds).toBeNull();
  });

  describe("narrowed to one kind of work", () => {
    const sources = {
      workflows: [workflow({ id: 1 })],
      cleanRuns: [
        { libraryId: 1, libraryName: "TV", nextRunAt: iso(minutes(600)) },
      ],
      cleanupJobs: [cleanupJob({})],
    };

    it("lists a workflow's scan for new downloads, and nothing else", () => {
      const items = nextItems({ ...sources, filter: "download" });

      expect(items.map((item) => item.key)).toEqual(["scan-1"]);
    });

    it("lists a library's clean for library cleaning, and nothing else", () => {
      const items = nextItems({ ...sources, filter: "library" });

      expect(items.map((item) => item.key)).toEqual(["clean-1"]);
    });

    it("lists every kind, cleanup jobs too, on Everything", () => {
      const items = nextItems({ ...sources, filter: "all" });

      expect(items.map((item) => item.key)).toEqual([
        "scan-1",
        "cleanup-work_temp_stale_sweep",
        "clean-1",
      ]);
    });
  });

  it("links each item to where it is set up", () => {
    const [scan, cleanup] = nextItems({
      workflows: [workflow({ id: 7 })],
      cleanRuns: [],
      cleanupJobs: [cleanupJob({ next_run_at: iso(minutes(90)) })],
    });

    expect(scan.to).toBe("/setup/workflows?edit=7");
    expect(cleanup.to).toBe("/setup/performance/cleanup");
  });
});

describe("how far through its wait an item is", () => {
  const item = (overrides: Partial<NextItem>): NextItem => ({
    key: "k",
    label: "l",
    to: "/",
    at: seconds(75),
    intervalSeconds: 300,
    ...overrides,
  });

  it("fills as the moment nears", () => {
    expect(waitFraction(item({}), NOW)).toBeCloseTo(0.75);
  });

  it("is full once the moment has come", () => {
    expect(waitFraction(item({ at: seconds(-5) }), NOW)).toBe(1);
  });

  it("is not known when the whole wait is not", () => {
    expect(waitFraction(item({ intervalSeconds: null }), NOW)).toBeNull();
  });

  it("counts a library clean over its day", () => {
    const clean = item({
      intervalSeconds: DAY_SECONDS,
      at: NOW + (DAY_SECONDS / 2) * 1000,
    });

    expect(waitFraction(clean, NOW)).toBeCloseTo(0.5);
  });
});

describe("when, in words", () => {
  it("counts seconds, then minutes, then gives a time of day", () => {
    expect(figureWords(seconds(42), NOW)).toBe("42 s");
    expect(figureWords(minutes(12), NOW)).toBe("12 min");
    expect(figureWords(minutes(180), NOW)).toBe("1:00 pm");
  });

  it("says now for a moment that has come", () => {
    expect(figureWords(seconds(-1), NOW)).toBe("now");
  });

  it("names the day for a moment tomorrow, and puts the time beside it in a line", () => {
    const tomorrow = new Date(2026, 7, 19, 2, 30, 0).getTime();

    expect(figureWords(tomorrow, NOW)).toBe("tomorrow");
    expect(lineWords(tomorrow, NOW)).toBe("tomorrow 2:30 am");
  });

  it("gives the same words in a line as in the figure for a moment today", () => {
    expect(lineWords(minutes(180), NOW)).toBe("1:00 pm");
    expect(lineWords(seconds(30), NOW)).toBe("30 s");
  });
});
