import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import { agoWords, serverMs, tookWords } from "./activity-model";

/** A time as the server writes it: UTC, with or without the fraction and the zone mark. */
const serverTime = fc
  .tuple(
    fc.date({
      min: new Date("2000-01-01T00:00:00Z"),
      max: new Date("2100-01-01T00:00:00Z"),
      noInvalidDate: true,
    }),
    fc.constantFrom("whole", "fraction", "zone", "fraction and zone"),
  )
  .map(([date, form]) => {
    const iso = date.toISOString();
    if (form === "whole") return iso.slice(0, 19);
    if (form === "fraction") return iso.slice(0, -1);
    if (form === "zone") return `${iso.slice(0, 19)}Z`;
    return iso;
  });

const age = fc.oneof(
  // Each scale gets its share: the minutes, the hours, the two days either side of the change to days, and the months.
  fc.integer({ min: -3600, max: 3600 }),
  fc.integer({ min: 3600, max: 2 * 86_400 }),
  fc.integer({ min: 2 * 86_400 - 7200, max: 2 * 86_400 + 7200 }),
  fc.integer({ min: 86_400, max: 400 * 86_400 }),
  fc.constantFrom(59, 60, 89, 90, 3569, 3570, 3600, 172_619, 172_620),
);

const UNIT_SECONDS = { min: 60, h: 3600, days: 86_400 } as const;

/** The seconds an "ago" says, to the unit it names. */
function saidSeconds(words: string): number {
  if (words === "just now") return 0;
  const match = /^(\d+) (min|h|days) ago$/.exec(words);
  if (!match) throw new Error(`not an age: ${words}`);
  return Number(match[1]) * UNIT_SECONDS[match[2] as keyof typeof UNIT_SECONDS];
}

it("says how long ago something happened in minutes, hours or days, and just now below a minute", () => {
  assertProperty(
    fc.property(serverTime, age, (iso, seconds) => {
      const words = agoWords(iso, serverMs(iso) + seconds * 1000);
      expect(words).toMatch(/^(just now|\d+ min ago|\d+ h ago|\d+ days ago)$/);
      if (seconds < 60) expect(words).toBe("just now");
      if (seconds >= 120 && seconds < 59 * 60)
        expect(words).toMatch(/ min ago$/);
      if (seconds >= 2 * 3600 && seconds < 47 * 3600)
        expect(words).toMatch(/ h ago$/);
      if (seconds >= 49 * 3600) expect(words).toMatch(/ days ago$/);
    }),
  );
});

it("never says something that happened later was longer ago", () => {
  assertProperty(
    fc.property(serverTime, age, age, (iso, a, b) => {
      const [sooner, later] = a <= b ? [a, b] : [b, a];
      const at = serverMs(iso);
      expect(
        saidSeconds(agoWords(iso, at + later * 1000)),
      ).toBeGreaterThanOrEqual(saidSeconds(agoWords(iso, at + sooner * 1000)));
    }),
  );
});

it("says nothing for a time that is missing or that nothing can read", () => {
  assertProperty(
    fc.property(fc.string({ unit: "binary" }), (text) => {
      const words = agoWords(text, Date.UTC(2026, 9, 15));
      expect(words).not.toContain("NaN");
    }),
  );
  assertProperty(
    fc.property(fc.constantFrom(null, undefined, ""), (missing) => {
      expect(agoWords(missing, 0)).toBe("");
    }),
  );
});

it("says how long a pass took to the second until an hour and to the minute after", () => {
  assertProperty(
    fc.property(fc.double({ min: 0, max: 400_000, noNaN: true }), (taken) => {
      const words = tookWords(taken)!;
      const whole = Math.round(taken);
      const match = /^(?:(\d+) h (\d+) min|(\d+) min (\d+) s|(\d+) s)$/.exec(
        words,
      );
      expect(match).not.toBeNull();
      const [, hours, hourMinutes, minutes, secondsOfMinute, seconds] = match!;
      const said =
        seconds !== undefined
          ? Number(seconds)
          : minutes !== undefined
            ? Number(minutes) * 60 + Number(secondsOfMinute)
            : Number(hours) * 3600 + Number(hourMinutes) * 60;
      expect(said).toBe(whole < 3600 ? whole : whole - (whole % 60));
      if (hours !== undefined) expect(Number(hourMinutes)).toBeLessThan(60);
      if (minutes !== undefined)
        expect(Number(secondsOfMinute)).toBeLessThan(60);
    }),
  );
  assertProperty(
    fc.property(
      fc.oneof(
        fc.constantFrom(null, undefined, Number.NaN, Infinity),
        fc.double({ max: -Number.MIN_VALUE, noNaN: true }),
      ),
      (missing) => {
        expect(tookWords(missing)).toBeNull();
      },
    ),
  );
});
