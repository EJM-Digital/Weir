import { describe, expect, it } from "vitest";

import {
  SLOTS_PER_DAY,
  SLOTS_PER_WEEK,
  backupWords,
  effectiveGrid,
  gridFromDaysAndTimes,
  windowNow,
  zoneClock,
} from "./schedule-model";

const everyHour = "1".repeat(SLOTS_PER_WEEK);

const limited = {
  schedule_enabled: true,
  schedule_hours_limited: true,
  schedule_grid: "",
  schedule_days: "Mon",
  schedule_start: "09:00",
  schedule_end: "10:00",
};

describe("a days-and-hours window", () => {
  const mondayQuarters = (grid: string) =>
    [...grid.slice(0, SLOTS_PER_DAY)].filter((slot) => slot === "1").length;

  it.each([
    ["a time that is not a time", "soon", "10:00", 41],
    ["an hour past the end of the day", "25:00", "10:00", 41],
    ["a minute past the end of the hour", "09:75", "10:00", 41],
  ])("starts at midnight for %s", (_what, start, end, quarters) => {
    expect(mondayQuarters(gridFromDaysAndTimes("Mon", start, end))).toBe(
      quarters,
    );
  });

  it.each([
    ["a time that is not a time", "09:00", "late", 96 - 36],
    ["an hour past the end of the day", "09:00", "24:00", 96 - 36],
  ])(
    "ends at the last minute of the day for %s",
    (_what, start, end, quarters) => {
      expect(mondayQuarters(gridFromDaysAndTimes("Mon", start, end))).toBe(
        quarters,
      );
    },
  );

  it("covers the whole day when no times are given", () => {
    expect(mondayQuarters(gridFromDaysAndTimes("Mon", "", ""))).toBe(96);
  });

  it("draws nothing when no day is named", () => {
    expect(gridFromDaysAndTimes(" , ", "09:00", "10:00")).toBe("");
  });
});

describe("the week a library actually runs to", () => {
  it("is any time when the grid is unusable and nothing else limits the hours", () => {
    expect(
      effectiveGrid({
        ...limited,
        schedule_grid: "10101",
        schedule_hours_limited: false,
      }),
    ).toBe("");
  });

  it("is any time when the drawn grid cannot be used", () => {
    expect(effectiveGrid({ ...limited, schedule_grid: "10101" })).toBe("");
  });

  it("is the drawn grid when it can be used", () => {
    expect(effectiveGrid({ ...limited, schedule_grid: everyHour })).toBe(
      everyHour,
    );
  });

  it("is any time when the hours are not limited", () => {
    expect(effectiveGrid({ ...limited, schedule_hours_limited: false })).toBe(
      "",
    );
  });

  it("reads a library saved with no window fields as any time", () => {
    expect(
      effectiveGrid({
        schedule_enabled: true,
        schedule_hours_limited: true,
        schedule_grid: "",
        schedule_days: null,
        schedule_start: null,
        schedule_end: null,
      } as never),
    ).toBe("");
  });

  it("is the older window when no grid was drawn", () => {
    expect(effectiveGrid(limited).slice(0, SLOTS_PER_DAY)).toContain("1");
  });
});

describe("the clock in the app's zone", () => {
  const wednesdayNoon = new Date("2026-10-07T12:30:00Z");

  it.each([
    ["Europe/London", { weekday: 2, hour: 13, minute: 30 }],
    ["Asia/Tokyo", { weekday: 2, hour: 21, minute: 30 }],
    [undefined, { weekday: 2, hour: 12, minute: 30 }],
    ["", { weekday: 2, hour: 12, minute: 30 }],
    ["Not/AZone", { weekday: 2, hour: 12, minute: 30 }],
  ])("reads %s", (zone, clock) => {
    expect(zoneClock(wednesdayNoon, zone)).toEqual(clock);
  });
});

describe("whether the library may start work now", () => {
  it("is any time for a week that cannot be used", () => {
    expect(windowNow("", "UTC", new Date())).toEqual({ kind: "any" });
  });

  it("stays open with no end when every hour is on", () => {
    expect(windowNow(everyHour, "UTC", new Date())).toEqual({
      kind: "open",
      until: null,
    });
  });
});

describe("the settings backup timer in words", () => {
  it.each([
    [true, 24, "03:15", "Every day at 03:15"],
    [true, 0, "", "Every day at 02:00"],
    [true, 72, " 04:00 ", "Every 3 days at 04:00"],
    [true, 1, "", "Every hour"],
    [true, 6, "", "Every 6 hours"],
    [true, 36, "", "Every 36 hours"],
    [false, 24, "", "Off"],
  ])("reads enabled=%s every %s hours at %j as %s", (on, hours, at, words) => {
    expect(backupWords(on, hours, at)).toBe(words);
  });
});
