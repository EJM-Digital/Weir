import { describe, expect, it } from "vitest";

import {
  dayKeyAt,
  instantAtWallClock,
  instantFromLocalInput,
  previousDayKeyAt,
  startOfDayAt,
} from "./zoned-time";

const NOON_UTC = Date.UTC(2026, 9, 2, 12, 0);

describe("instantAtWallClock", () => {
  it("reads the wall clock of the zone, not of the browser", () => {
    const clock = { year: 2026, month: 10, day: 2, hour: 8, minute: 30 };
    expect(instantAtWallClock(clock, "UTC")).toBe(Date.UTC(2026, 9, 2, 8, 30));
    // Sydney is UTC+10 until its clocks go forward on 4 October.
    expect(instantAtWallClock(clock, "Australia/Sydney")).toBe(
      Date.UTC(2026, 9, 1, 22, 30),
    );
    // New York is UTC-4 in October.
    expect(instantAtWallClock(clock, "America/New_York")).toBe(
      Date.UTC(2026, 9, 2, 12, 30),
    );
  });

  it("follows the clock change of the day it is asked about", () => {
    // New York's clocks go back at 02:00 on 1 Nov 2026; the day before is still UTC-4, the day after is UTC-5.
    expect(
      instantAtWallClock(
        { year: 2026, month: 10, day: 31, hour: 12, minute: 0 },
        "America/New_York",
      ),
    ).toBe(Date.UTC(2026, 9, 31, 16, 0));
    expect(
      instantAtWallClock(
        { year: 2026, month: 11, day: 2, hour: 12, minute: 0 },
        "America/New_York",
      ),
    ).toBe(Date.UTC(2026, 10, 2, 17, 0));
  });

  it("falls back to the browser's clock for a zone it does not know", () => {
    const clock = { year: 2026, month: 10, day: 2, hour: 8, minute: 30 };
    expect(instantAtWallClock(clock, "Not/AZone")).toBe(
      new Date(2026, 9, 2, 8, 30).getTime(),
    );
  });
});

describe("startOfDayAt", () => {
  it("is midnight in the zone, which is another instant than midnight UTC", () => {
    expect(startOfDayAt(NOON_UTC, "UTC")).toBe(Date.UTC(2026, 9, 2));
    // 12:00 UTC is 22:00 on 2 Oct in Sydney, so that day began at 14:00 UTC on 1 Oct.
    expect(startOfDayAt(NOON_UTC, "Australia/Sydney")).toBe(
      Date.UTC(2026, 9, 1, 14, 0),
    );
    // And 08:00 on 2 Oct in New York.
    expect(startOfDayAt(NOON_UTC, "America/New_York")).toBe(
      Date.UTC(2026, 9, 2, 4, 0),
    );
  });
});

describe("dayKeyAt", () => {
  it("names the day in the zone", () => {
    const lateUtc = Date.UTC(2026, 9, 2, 23, 0);
    expect(dayKeyAt(lateUtc, "UTC")).toBe("2026-10-02");
    expect(dayKeyAt(lateUtc, "Australia/Sydney")).toBe("2026-10-03");
  });

  it("finds the day before, whatever the length of the day", () => {
    expect(previousDayKeyAt(NOON_UTC, "UTC")).toBe("2026-10-01");
    expect(
      previousDayKeyAt(Date.UTC(2026, 10, 1, 12, 0), "America/New_York"),
    ).toBe("2026-10-31");
  });
});

describe("instantFromLocalInput", () => {
  it("reads a date and time picked in the zone", () => {
    expect(instantFromLocalInput("2026-10-02T08:30", "UTC")).toBe(
      Date.UTC(2026, 9, 2, 8, 30),
    );
  });

  it("gives null for text that is not a date and time", () => {
    expect(instantFromLocalInput("", "UTC")).toBeNull();
    expect(instantFromLocalInput("yesterday", "UTC")).toBeNull();
    expect(instantFromLocalInput("2026-10-02", "UTC")).toBeNull();
  });
});

it("reads no instant from a date that is not on the calendar", () => {
  for (const text of [
    "2026-04-31T00:00",
    "2026-02-29T08:00",
    "2026-13-01T08:00",
    "2026-00-10T08:00",
    "2026-10-00T08:00",
    "2026-10-02T24:00",
    "2026-10-02T08:60",
  ]) {
    expect(instantFromLocalInput(text, "UTC")).toBeNull();
  }
  expect(instantFromLocalInput("2028-02-29T08:00", "UTC")).toBe(
    Date.UTC(2028, 1, 29, 8, 0),
  );
});
