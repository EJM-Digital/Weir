import { describe, expect, it } from "vitest";

import type { AppSettings } from "../lib/settings/types";
import type { SystemLogRow } from "../lib/system/system-log-api";
import {
  fractionOfTime,
  traceShape,
} from "../components/charts/live-trace-math";
import {
  emptyHealthWords,
  healthSummary,
} from "./processing/dashboard/system/health-card-model";
import { poseAt } from "./processing/pipeline/flight-path";
import { parseSorters } from "./settings/tabs/rules/rule-set-model";
import { timerRows } from "./settings/tabs/performance/timer-rows";
import { installTypeLabel } from "./system/tabs/about/update-words";
import { groupByDay } from "./system/tabs/logs/log-days";
import type { MaintenanceFamilyState } from "../lib/processing/maintenance-api";

describe("how this install got here", () => {
  it.each([
    ["windows", "Windows installer"],
    ["docker", "Docker"],
    ["source", "Built from source"],
    ["snap", "snap"],
  ])("%s reads %j", (kind, words) => {
    expect(installTypeLabel(kind)).toBe(words);
  });
});

describe("a poster's flight", () => {
  const from = { left: 10, top: 10, width: 100, height: 150 };

  it.each([
    ["to the same place", { left: 10, top: 10, width: 100 }],
    ["upwards", { left: 300, top: -200, width: 100 }],
    ["downwards", { left: 300, top: 400, width: 100 }],
  ])("is placed on the way %s", (_name, to) => {
    const pose = poseAt(0.5, from, to);
    expect(Number.isFinite(pose.x)).toBe(true);
    expect(Number.isFinite(pose.y)).toBe(true);
  });
});

describe("a chart over no time at all", () => {
  const window = { from: 1000, to: 1000 };

  it("puts its one sample at the right edge", () => {
    const shape = traceShape([{ at: 1000, value: 1 }], window, 2);
    expect(shape.points).toHaveLength(1);
    expect(shape.points[0].x).toBe(200);
  });

  it("puts any time at the right edge", () => {
    expect(fractionOfTime(window, 1000)).toBe(1);
  });
});

describe("the health list", () => {
  const check = (meaning: string, fact = false) =>
    ({ meaning, fact, area: "x" }) as never;

  it.each([
    [[check("done")], "done"],
    [[check("done"), check("attention")], "attention"],
    [[check("attention"), check("broken")], "broken"],
    [[check("broken", true)], "done"],
  ])("sums up %#", (checks, meaning) => {
    expect(healthSummary(checks).meaning).toBe(meaning);
  });

  it("names the area it has nothing to show for", () => {
    expect(emptyHealthWords("tools")).toBe("Nothing to show for Tools.");
  });
});

describe("track sorters read from saved text", () => {
  const fallback = [{ field: "language", value: "", reversed: false }];

  it.each([
    ["text that is not JSON", "{oops"],
    ["a value that is not a list", '{"a":1}'],
    ["a list of nothing usable", '[null, 3, {"field":"nonsense"}]'],
  ])("falls back for %s", (_name, raw) => {
    expect(parseSorters(raw, fallback)).toEqual(fallback);
  });

  it("reads a sorter with no field as a language sorter", () => {
    expect(parseSorters('[{"reversed":true}]', fallback)).toEqual([
      { field: "language", value: "", reversed: true },
    ]);
  });
});

describe("the log's days", () => {
  const row = (at: string) => ({ at }) as SystemLogRow;

  it("puts a row with no readable time with the day before it, or alone at the start", () => {
    const days = groupByDay(
      [row("nonsense"), row("2026-10-02T12:00:00Z"), row("nonsense")],
      "UTC",
    );
    expect(days.map((day) => day.rows.length)).toEqual([1, 2]);
  });
});

describe("the timers", () => {
  const settings = (partial: Partial<AppSettings>) => partial as AppSettings;
  const backup = (partial: Partial<AppSettings>) =>
    timerRows([], settings(partial)).at(-1)!;

  it.each([
    ["no saved schedule", {}, 86_400],
    [
      "an interval of nothing",
      { configuration_backup_interval_hours: 0 },
      86_400,
    ],
    [
      "an interval of 6 hours",
      { configuration_backup_interval_hours: 6 },
      21_600,
    ],
  ])("runs the backup on %s", (_name, partial, seconds) => {
    expect(
      backup({ configuration_backup_enabled: true, ...partial }).everySeconds,
    ).toBe(seconds);
  });

  it("shows a cleanup job with no interval as simply on", () => {
    const families = [
      { family: "work_temp_stale_sweep", enabled: true, interval_seconds: 0 },
    ] as unknown as MaintenanceFamilyState[];
    expect(timerRows(families, settings({})).some((r) => r.when === "On")).toBe(
      true,
    );
  });
});
