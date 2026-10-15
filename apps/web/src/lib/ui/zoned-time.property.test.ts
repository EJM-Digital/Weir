import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import {
  dayKeyAt,
  instantAtWallClock,
  instantFromLocalInput,
  previousDayKeyAt,
  startOfDayAt,
  type WallClock,
} from "./zoned-time";

/** Zones with and without clock changes, on whole, half and quarter hours, east and west of UTC. None changes its clocks at midnight. */
const ZONES = [
  "UTC",
  "Europe/London",
  "Europe/Berlin",
  "America/New_York",
  "America/Los_Angeles",
  "America/St_Johns",
  "Asia/Kolkata",
  "Asia/Kathmandu",
  "Australia/Sydney",
  "Australia/Lord_Howe",
  "Pacific/Auckland",
  "Pacific/Chatham",
  "Pacific/Kiritimati",
  "Pacific/Pago_Pago",
];

const zone = fc.constantFrom(...ZONES);

const instant = fc.integer({
  min: Date.UTC(1990, 0, 1),
  max: Date.UTC(2090, 0, 1),
});

const wallClock = fc
  .record({
    year: fc.integer({ min: 1990, max: 2090 }),
    month: fc.integer({ min: 1, max: 12 }),
    day: fc.integer({ min: 1, max: 28 }),
    hour: fc.integer({ min: 0, max: 23 }),
    minute: fc.integer({ min: 0, max: 59 }),
  })
  .map((clock): WallClock => clock);

/** What a zone's wall clock reads at an instant, counted as if it were UTC so two readings can be subtracted. */
function readingAt(ms: number, timeZone: string): number {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    hourCycle: "h23",
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "numeric",
  }).formatToParts(ms);
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    Number(parts.find((p) => p.type === type)?.value);
  return Date.UTC(
    part("year"),
    part("month") - 1,
    part("day"),
    part("hour"),
    part("minute"),
  );
}

const asReading = (clock: WallClock) =>
  Date.UTC(clock.year, clock.month - 1, clock.day, clock.hour, clock.minute);

const HOUR = 3_600_000;

it("finds the instant a wall clock reads in a zone, which reads that wall clock back", () => {
  assertProperty(
    fc.property(wallClock, zone, (clock, timeZone) => {
      const found = instantAtWallClock(clock, timeZone);
      const off = readingAt(found, timeZone) - asReading(clock);
      // A reading a clock change skips or repeats is moved by the change, which is an hour at most; every other one is exact.
      expect(Math.abs(off)).toBeLessThanOrEqual(HOUR);
      if (clock.hour >= 6 && clock.hour <= 21) expect(off).toBe(0);
    }),
  );
});

/** Days in 2026 on which a zone changes its clocks, so the readings near them (which skip or repeat) are tried as often as the rest. */
const CLOCK_CHANGES: [string, number, number][] = [
  ["America/New_York", 3, 8],
  ["America/New_York", 11, 1],
  ["America/St_Johns", 3, 8],
  ["America/Los_Angeles", 11, 1],
  ["Europe/London", 3, 29],
  ["Europe/London", 10, 25],
  ["Europe/Berlin", 10, 25],
  ["Australia/Sydney", 4, 5],
  ["Australia/Sydney", 10, 4],
  ["Australia/Lord_Howe", 4, 5],
  ["Australia/Lord_Howe", 10, 4],
  ["Pacific/Auckland", 4, 5],
  ["Pacific/Auckland", 9, 27],
  ["Pacific/Chatham", 9, 27],
];

it("finds the wall clock in the zone whenever the zone shows it at all, on the days its clocks change as on any other", () => {
  const nearAChange = fc
    .tuple(
      fc.constantFrom(...CLOCK_CHANGES),
      fc.integer({ min: -1, max: 1 }),
      fc.integer({ min: 0, max: 23 }),
      fc.constantFrom(0, 15, 30, 45),
    )
    .map(([[timeZone, month, day], shift, hour, minute]) => ({
      timeZone,
      clock: { year: 2026, month, day: day + shift, hour, minute },
    }));
  assertProperty(
    fc.property(nearAChange, ({ timeZone, clock }) => {
      const found = instantAtWallClock(clock, timeZone);
      const wanted = asReading(clock);
      // Every quarter hour in the two days around it: is there an instant at which the zone's clock reads this?
      const start = wanted - 30 * HOUR;
      let shown = false;
      for (let at = start; at <= wanted + 30 * HOUR && !shown; at += 900_000) {
        shown = readingAt(at, timeZone) === wanted;
      }
      if (shown) expect(readingAt(found, timeZone)).toBe(wanted);
    }),
  );
});

it("finds a later instant for a later wall clock on the same day, in any zone", () => {
  assertProperty(
    fc.property(
      wallClock,
      zone,
      fc.integer({ min: 1, max: 12 }),
      (clock, timeZone, hours) => {
        const earlier = { ...clock, hour: 6 };
        const later = { ...clock, hour: 6 + hours };
        expect(instantAtWallClock(later, timeZone)).toBeGreaterThan(
          instantAtWallClock(earlier, timeZone),
        );
      },
    ),
  );
});

it("starts a day at midnight of the zone, no later than the instant and less than a day and an hour before it", () => {
  assertProperty(
    fc.property(instant, zone, (ms, timeZone) => {
      const start = startOfDayAt(ms, timeZone);
      expect(start).toBeLessThanOrEqual(ms);
      expect(ms - start).toBeLessThan(25 * HOUR);
      expect(readingAt(start, timeZone) % (24 * HOUR)).toBe(0);
      expect(dayKeyAt(start, timeZone)).toBe(dayKeyAt(ms, timeZone));
      expect(startOfDayAt(start, timeZone)).toBe(start);
    }),
  );
});

it("names the day before a day as the day that ends where the first begins", () => {
  assertProperty(
    fc.property(instant, zone, (ms, timeZone) => {
      const start = startOfDayAt(ms, timeZone);
      expect(previousDayKeyAt(ms, timeZone)).toBe(
        dayKeyAt(start - 1, timeZone),
      );
      expect(previousDayKeyAt(ms, timeZone) < dayKeyAt(ms, timeZone)).toBe(
        true,
      );
    }),
  );
});

it("reads a datetime-local value as the wall clock it names, and nothing else as an instant", () => {
  const twoDigits = (value: number) => String(value).padStart(2, "0");
  assertProperty(
    fc.property(wallClock, zone, (clock, timeZone) => {
      const text = `${clock.year}-${twoDigits(clock.month)}-${twoDigits(clock.day)}T${twoDigits(clock.hour)}:${twoDigits(clock.minute)}`;
      expect(instantFromLocalInput(text, timeZone)).toBe(
        instantAtWallClock(clock, timeZone),
      );
    }),
  );
  assertProperty(
    fc.property(fc.string({ unit: "binary" }), zone, (text, timeZone) => {
      if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d$/.test(text)) {
        expect(instantFromLocalInput(text, timeZone)).toBeNull();
      }
    }),
  );
});

it("reads no instant from a date or a time that does not exist", () => {
  const impossible = fc.oneof(
    fc.record({
      month: fc.oneof(fc.constant(0), fc.integer({ min: 13, max: 99 })),
      day: fc.integer({ min: 1, max: 28 }),
      hour: fc.integer({ min: 0, max: 23 }),
      minute: fc.integer({ min: 0, max: 59 }),
    }),
    // 2026 is not a leap year, and four months have thirty days.
    fc.record({
      month: fc.constant(2),
      day: fc.integer({ min: 29, max: 31 }),
      hour: fc.integer({ min: 0, max: 23 }),
      minute: fc.integer({ min: 0, max: 59 }),
    }),
    fc.record({
      month: fc.constantFrom(4, 6, 9, 11),
      day: fc.constant(31),
      hour: fc.integer({ min: 0, max: 23 }),
      minute: fc.integer({ min: 0, max: 59 }),
    }),
    fc.record({
      month: fc.integer({ min: 1, max: 12 }),
      day: fc.oneof(fc.constant(0), fc.integer({ min: 32, max: 99 })),
      hour: fc.integer({ min: 0, max: 23 }),
      minute: fc.integer({ min: 0, max: 59 }),
    }),
    fc.record({
      month: fc.integer({ min: 1, max: 12 }),
      day: fc.integer({ min: 1, max: 28 }),
      hour: fc.integer({ min: 24, max: 99 }),
      minute: fc.integer({ min: 0, max: 59 }),
    }),
    fc.record({
      month: fc.integer({ min: 1, max: 12 }),
      day: fc.integer({ min: 1, max: 28 }),
      hour: fc.integer({ min: 0, max: 23 }),
      minute: fc.integer({ min: 60, max: 99 }),
    }),
  );
  const twoDigits = (value: number) => String(value).padStart(2, "0");
  assertProperty(
    fc.property(impossible, zone, (clock, timeZone) => {
      const text = `2026-${twoDigits(clock.month)}-${twoDigits(clock.day)}T${twoDigits(clock.hour)}:${twoDigits(clock.minute)}`;
      expect(instantFromLocalInput(text, timeZone)).toBeNull();
    }),
  );
});
