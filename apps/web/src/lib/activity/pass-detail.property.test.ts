import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import {
  formatDuration,
  formatProcessingSpeed,
  formatSavings,
} from "./pass-detail";

const seconds = fc.oneof(
  fc.double({ min: 0, max: 400_000, noNaN: true }),
  fc.integer({ min: 0, max: 400_000 }),
  // Just either side of the points where the wording changes.
  fc
    .constantFrom(60, 3600, 86_400)
    .chain((edge) =>
      fc.double({ min: -1, max: 1, noNaN: true }).map((nudge) => edge + nudge),
    ),
);

/** The seconds a duration says, and how many of them it can tell apart. */
function secondsSaid(text: string): { total: number; precision: number } {
  const bare = /^(\d+)s$/.exec(text);
  if (bare) return { total: Number(bare[1]), precision: 1 };
  const minutes = /^(\d+)m (\d\d)s$/.exec(text);
  if (minutes) {
    return {
      total: Number(minutes[1]) * 60 + Number(minutes[2]),
      precision: 1,
    };
  }
  const hours = /^(\d+)h (\d\d)m$/.exec(text);
  if (hours) {
    return {
      total: Number(hours[1]) * 3600 + Number(hours[2]) * 60,
      precision: 60,
    };
  }
  throw new Error(`not a duration: ${text}`);
}

it("says how long a pass took in seconds, minutes or hours, to the second until an hour", () => {
  assertProperty(
    fc.property(seconds, (taken) => {
      const text = formatDuration(taken)!;
      const said = secondsSaid(text);
      const whole = Math.round(taken);
      expect(said.total).toBe(whole - (whole % said.precision));
      expect(text).toMatch(
        whole < 60 ? /^\d+s$/ : whole < 3600 ? /^\d+m \d\ds$/ : /^\d+h \d\dm$/,
      );
    }),
  );
});

it("never says a longer pass took less time than a shorter one", () => {
  assertProperty(
    fc.property(seconds, seconds, (a, b) => {
      const [shorter, longer] = a <= b ? [a, b] : [b, a];
      expect(secondsSaid(formatDuration(longer)!).total).toBeGreaterThanOrEqual(
        secondsSaid(formatDuration(shorter)!).total,
      );
    }),
  );
});

it("says nothing for a duration that is missing, not a number or below zero", () => {
  assertProperty(
    fc.property(
      fc.oneof(
        fc.constantFrom(null, undefined, Number.NaN, Infinity, -Infinity),
        fc.double({ max: -Number.MIN_VALUE, noNaN: true }),
      ),
      (missing) => {
        expect(formatDuration(missing)).toBeNull();
      },
    ),
  );
});

it("shows a speed as it came, and ffmpeg's multiple as a multiple of realtime", () => {
  assertProperty(
    fc.property(
      fc.string({ unit: "binary" }),
      fc.constantFrom("", " ", "\t", "\n "),
      (speed, padding) => {
        const given = padding + speed + padding;
        const shown = formatProcessingSpeed(given);
        const trimmed = given.trim();
        if (trimmed === "") {
          expect(shown).toBeNull();
        } else if (/^[0-9]+(?:\.[0-9]+)?x$/i.test(trimmed)) {
          expect(shown).toBe(`${trimmed} realtime`);
        } else {
          expect(shown).toBe(trimmed);
        }
      },
    ),
  );
  assertProperty(
    fc.property(
      fc.integer({ min: 0, max: 100_000 }),
      fc.constantFrom("x", "X"),
      (hundredths, mark) => {
        expect(formatProcessingSpeed(`${hundredths / 100}${mark}`)).toMatch(
          / realtime$/,
        );
      },
    ),
  );
});

const size = fc.oneof(
  fc.integer({ min: 0, max: 5000 }),
  fc.integer({ min: 0, max: 50 * 1024 ** 3 }),
);

it("says what a pass did to a file's size in words that agree with the two sizes", () => {
  assertProperty(
    fc.property(size, size, (source, output) => {
      const said = formatSavings(source, output)!;
      const delta = source - output;
      if (delta === 0) {
        expect(said).toBe("No size change");
      } else if (said.startsWith("Size basically unchanged")) {
        expect(said).toContain(delta > 0 ? "saved" : "container overhead");
      } else if (said.startsWith("Saved ")) {
        expect(delta).toBeGreaterThan(0);
      } else {
        expect(said.startsWith("Grew by ")).toBe(true);
        expect(delta).toBeLessThan(0);
      }
      const percent = /\((\d+(?:\.\d+)?)%\)/.exec(said);
      if (percent && source > 0) {
        expect(
          Math.abs(Number(percent[1]) - (Math.abs(delta) / source) * 100),
        ).toBeLessThanOrEqual(0.05 + 1e-9);
      }
    }),
  );
});

it("says nothing about the size when either size is missing or not a number", () => {
  assertProperty(
    fc.property(
      fc.constantFrom(null, undefined, Number.NaN, Infinity),
      size,
      fc.boolean(),
      (missing, known, missingFirst) => {
        expect(
          missingFirst
            ? formatSavings(missing, known)
            : formatSavings(known, missing),
        ).toBeNull();
      },
    ),
  );
});
