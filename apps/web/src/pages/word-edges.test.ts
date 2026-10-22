import { describe, expect, it } from "vitest";

import type { ProcessingFile } from "../lib/processing/files-api";
import { fileGuidance } from "./activity/activity-guidance";
import { checkVerdict } from "./processing/dashboard/health-model";
import {
  bucketWords,
  handedBackSentence,
  spanLabel,
} from "./processing/dashboard/handed-back-words";
import { lineWords } from "./processing/dashboard/next-model";
import { nextBackupAt } from "./processing/dashboard/system/backup-schedule";
import {
  everyChoices,
  everyWords,
  lastRunLine,
} from "./settings/tabs/cleanup/cleanup-jobs";
import type {
  HandedBack,
  HandedBackBucket,
} from "./processing/handed-back-model";
import type { MaintenanceFamilyState } from "../lib/processing/maintenance-api";

const at = (iso: string) => Date.parse(iso);

describe("how long ago a finished batch was", () => {
  const now = at("2026-10-02T12:00:00Z");
  const bucket = (minutesAgo: number): HandedBackBucket =>
    ({
      from: now - minutesAgo * 60_000,
      total: 0,
      ok: 0,
      same: 0,
      warn: 0,
    }) as HandedBackBucket;

  it.each([
    [5, "In the last 5 min · nothing"],
    [40, "35–40 min ago · nothing"],
  ])("a batch that began %i minutes ago reads %j", (minutes, words) => {
    expect(bucketWords(bucket(minutes), now)).toBe(words);
  });

  it("says two hours when nothing has been handed back yet", () => {
    expect(spanLabel({ buckets: [] } as unknown as HandedBack, now)).toBe(
      "2 h ago – now",
    );
  });

  it.each([
    [true, "The oldest of them are not in the chart."],
    [false, "finished in the last 2 hours: 1 cleaned."],
  ])("tells a screen reader about a partial chart: %s", (partial, words) => {
    const handed = { totals: { ok: 1, same: 0, warn: 0 } } as HandedBack;
    expect(handedBackSentence(handed, 1, partial)).toContain(words);
  });
});

describe("the next job's day", () => {
  const now = new Date(2026, 9, 2, 22, 0).getTime();

  it.each([
    ["tomorrow", new Date(2026, 9, 3, 9, 0).getTime(), "tomorrow"],
    ["a later day", new Date(2026, 9, 6, 9, 0).getTime(), "Tue"],
  ])("names %s", (_name, when, word) => {
    expect(lineWords(when, now)).toContain(word);
  });
});

describe("a workflow's check", () => {
  it.each([
    [true, "Couldn't check"],
    [false, "Checking"],
  ])("with a failed check %s reads %j", (isError, words) => {
    expect(checkVerdict({ isError }).words).toContain(words);
  });
});

describe("guidance for a file checked while processing was paused", () => {
  const file = {
    status: "out_of_schedule",
    status_reason: "Processing is paused",
  } as ProcessingFile;

  it.each([
    [true, "Processing is paused."],
    [false, "Refresh this file's status."],
  ])("with processing paused now: %s", (paused, title) => {
    expect(fileGuidance(file, paused).title).toBe(title);
  });

  it("does not treat another reason for being out of schedule as a pause", () => {
    expect(
      fileGuidance(
        {
          status: "out_of_schedule",
          status_reason: "Outside hours",
        } as ProcessingFile,
        false,
      ).title,
    ).not.toBe("Refresh this file's status.");
  });
});

describe("the next automatic backup", () => {
  const run = (overrides: object) => ({
    enabled: true,
    intervalHours: 24,
    preferredTime: "03:00",
    lastRunAt: at("2026-10-01T03:00:00Z"),
    ...overrides,
  });

  it.each([
    [
      "a time that is not a time",
      { preferredTime: "soon" },
      "2026-10-02T00:00:00Z",
    ],
    [
      "an hour that does not exist",
      { preferredTime: "25:00" },
      "2026-10-02T00:00:00Z",
    ],
    ["no interval chosen", { intervalHours: 0 }, "2026-10-02T00:00:00Z"],
    ["a first backup", { lastRunAt: null }, "2026-10-02T00:00:00Z"],
    [
      "a backup already made today",
      { lastRunAt: at("2026-10-02T03:00:00Z") },
      "2026-10-02T04:00:00Z",
    ],
  ])("is still found for %s", (_name, overrides, now) => {
    const next = nextBackupAt(run(overrides) as never, at(now), "UTC");
    expect(next).not.toBeNull();
    expect(next).toBeGreaterThanOrEqual(at(now) - 24 * 3_600_000);
  });

  it("falls back to the earliest time when no day in the window suits", () => {
    const next = nextBackupAt(
      run({ intervalHours: 24 * 400 }) as never,
      at("2026-10-02T04:00:00Z"),
      "Not/AZone",
    );
    expect(next).not.toBeNull();
  });
});

describe("cleanup schedules", () => {
  it.each([
    [2 * 86_400, "2 days"],
    [2 * 3_600, "2 hours"],
    [90 * 60, "90 minutes"],
  ])("reads %i seconds as %j", (seconds, words) => {
    expect(everyWords(seconds)).toBe(words);
  });

  it("adds a saved interval the list lacks, and only then", () => {
    expect(everyChoices(90 * 60).at(-1)).toEqual({
      seconds: 90 * 60,
      label: "90 minutes",
    });
    expect(everyChoices(3_600)).toBe(everyChoices(3_600));
  });

  const job = (partial: Partial<MaintenanceFamilyState>) =>
    ({ running: 0, pending: 0, ...partial }) as MaintenanceFamilyState;

  it.each([
    [
      { last_failed_at: "x", last_error: null },
      "Failed <x>: no reason recorded.",
    ],
    [{ last_failed_at: "x", last_error: "Disk" }, "Failed <x>: Disk."],
    [{ last_completed_at: "x" }, "<x>"],
    [{}, "Not yet."],
  ])("reads %j as %j", (state, words) => {
    expect(lastRunLine(job(state), (iso) => `<${iso}>`)).toBe(words);
  });
});
