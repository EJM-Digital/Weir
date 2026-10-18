import { describe, expect, it } from "vitest";
import {
  SLOTS_PER_DAY,
  SLOTS_PER_WEEK,
  backupWords,
  effectiveGrid,
  gridFromDaysAndTimes,
  sameWeek,
  weekHours,
  windowNow,
} from "./schedule-model";

const base = {
  schedule_enabled: true,
  schedule_grid: "",
  schedule_hours_limited: false,
  schedule_days: "",
  schedule_start: "00:00",
  schedule_end: "23:59",
};

/** Weekday nights, 22:00 to 06:00. */
const nights = gridFromDaysAndTimes("Mon,Tue,Wed,Thu,Fri", "22:00", "05:59");

describe("the week a library runs to", () => {
  it("is any time with nothing chosen", () => {
    expect(effectiveGrid(base)).toBe("");
    expect(windowNow("", "UTC", new Date()).kind).toBe("any");
  });

  it("shows the older days-and-hours window, which the server still honours", () => {
    const grid = effectiveGrid({
      ...base,
      schedule_hours_limited: true,
      schedule_days: "Sat,Sun",
      schedule_start: "09:00",
      schedule_end: "17:00",
    });
    expect(grid).toHaveLength(SLOTS_PER_WEEK);
    const hours = weekHours(grid);
    expect(hours[5]![9]).toBe(true);
    expect(hours[5]![18]).toBe(false);
    expect(hours[0]![9]).toBe(false);
  });

  it("lets a drawn grid win, and ignores one when the schedule is switched off", () => {
    expect(effectiveGrid({ ...base, schedule_grid: nights })).toBe(nights);
    expect(
      effectiveGrid({
        ...base,
        schedule_enabled: false,
        schedule_grid: nights,
      }),
    ).toBe("");
  });

  it("carries a window past midnight into the next morning", () => {
    const hours = weekHours(nights);
    expect(hours[4]![23]).toBe(true); // Friday night
    expect(hours[5]![3]).toBe(true); // into Saturday morning
    expect(hours[5]![22]).toBe(false); // Saturday night is not chosen
  });
});

describe("whether two weeks are the same", () => {
  it("counts a week with every hour on as no restriction at all", () => {
    expect(sameWeek("", "1".repeat(SLOTS_PER_WEEK))).toBe(true);
    expect(sameWeek("not a grid", "1".repeat(SLOTS_PER_WEEK))).toBe(true);
  });

  it("tells a week with an hour off from one without", () => {
    expect(sameWeek("", nights)).toBe(false);
    expect(sameWeek(nights, nights)).toBe(true);
  });
});

describe("right now", () => {
  it("says when a closed library opens, in the app's zone", () => {
    // Wednesday 12:00 UTC.
    const state = windowNow(nights, "UTC", new Date("2026-08-19T12:00:00Z"));
    expect(state).toEqual({ kind: "closed", opens: "Wed 22:00" });
  });

  it("says when an open library closes", () => {
    const state = windowNow(nights, "UTC", new Date("2026-08-19T23:10:00Z"));
    expect(state).toEqual({ kind: "open", until: "Thu 06:00" });
  });

  it("reads the clock in the app's zone, not the browser's", () => {
    // 12:00 UTC is 22:00 in Sydney (AEST, before daylight saving starts in October).
    const state = windowNow(
      nights,
      "Australia/Sydney",
      new Date("2026-08-19T12:00:00Z"),
    );
    expect(state.kind).toBe("open");
  });

  it("warns when no hour at all is chosen", () => {
    expect(windowNow("0".repeat(SLOTS_PER_WEEK), "UTC", new Date()).kind).toBe(
      "never",
    );
    expect(SLOTS_PER_DAY).toBe(96);
  });
});

describe("the settings backup timer", () => {
  it("reads as the server runs it", () => {
    expect(backupWords(false, 24, "02:00")).toBe("Off");
    expect(backupWords(true, 24, "02:00")).toBe("Every day at 02:00");
    expect(backupWords(true, 72, "03:30")).toBe("Every 3 days at 03:30");
    expect(backupWords(true, 6, "02:00")).toBe("Every 6 hours");
  });
});
