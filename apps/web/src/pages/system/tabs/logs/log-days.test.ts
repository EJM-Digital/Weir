import { describe, expect, it } from "vitest";

import type { SystemLogRow } from "../../../../lib/system/system-log-api";
import { datedClock, dayHeading, groupByDay } from "./log-days";

const row = (id: string, at: string): SystemLogRow => ({
  id,
  source: "event",
  at,
  level: "info",
  category: "weir",
  workflow: null,
  title: id,
  detail: null,
  event: null,
  job: null,
  server: null,
});

describe("groupByDay", () => {
  it("splits the rows by the day they fall on in the time zone, keeping their order", () => {
    const rows = [
      row("a", "2026-10-02T23:30:00Z"),
      row("b", "2026-10-02T03:00:00Z"),
      row("c", "2026-10-01T20:00:00Z"),
    ];

    expect(
      groupByDay(rows, "UTC").map((d) => [d.key, d.rows.map((r) => r.id)]),
    ).toEqual([
      ["2026-10-02", ["a", "b"]],
      ["2026-10-01", ["c"]],
    ]);
    // In Sydney (UTC+10) the first row is already the next day, and the last is on the 2nd.
    expect(
      groupByDay(rows, "Australia/Sydney").map((d) => [
        d.key,
        d.rows.map((r) => r.id),
      ]),
    ).toEqual([
      ["2026-10-03", ["a"]],
      ["2026-10-02", ["b", "c"]],
    ]);
  });

  it("has no days for no rows", () => {
    expect(groupByDay([], "UTC")).toEqual([]);
  });
});

describe("dayHeading", () => {
  const now = Date.UTC(2026, 9, 2, 12, 0);

  it("names today and yesterday", () => {
    expect(dayHeading("2026-10-02", now, "UTC")).toBe("Today");
    expect(dayHeading("2026-10-01", now, "UTC")).toBe("Yesterday");
  });

  it("falls back to the key itself for a day it cannot name", () => {
    expect(dayHeading("", now, "UTC")).toBe("");
  });

  it("names any other day by its weekday and date", () => {
    expect(dayHeading("2026-09-25", now, "UTC")).toMatch(/Friday/);
    expect(dayHeading("2026-09-25", now, "UTC")).toMatch(/25/);
    expect(dayHeading("2026-09-25", now, "UTC")).not.toMatch(/2026/);
  });

  it("adds the year to a day of another year", () => {
    expect(dayHeading("2025-12-31", now, "UTC")).toMatch(/2025/);
  });
});

describe("datedClock", () => {
  const now = Date.UTC(2026, 9, 2, 12, 0);
  const clock = (ms: number) => new Date(ms).toISOString().slice(11, 19);
  const at = (iso: string) => Date.parse(iso);

  it("puts Today or Yesterday before the time of a recent row", () => {
    expect(datedClock(at("2026-10-02T09:15:30Z"), now, "UTC", clock)).toBe(
      "Today 09:15:30",
    );
    expect(datedClock(at("2026-10-01T23:59:59Z"), now, "UTC", clock)).toBe(
      "Yesterday 23:59:59",
    );
  });

  it("puts the date before the time of an older row, with the year when it is not this one", () => {
    const older = datedClock(at("2026-09-25T08:00:00Z"), now, "UTC", clock);
    expect(older).toMatch(/25/);
    expect(older).not.toMatch(/2026/);
    expect(older.endsWith(" 08:00:00")).toBe(true);
    expect(datedClock(at("2025-12-31T08:00:00Z"), now, "UTC", clock)).toMatch(
      /2025.* 08:00:00$/,
    );
  });

  it("names the day in the time zone, not in UTC", () => {
    // 23:30 UTC on the 2nd is already the 3rd in Sydney, where it is also the day "now" falls on.
    const sydneyNow = Date.UTC(2026, 9, 3, 1, 0);
    expect(
      datedClock(
        at("2026-10-02T23:30:00Z"),
        sydneyNow,
        "Australia/Sydney",
        clock,
      ),
    ).toBe("Today 23:30:00");
  });
});
