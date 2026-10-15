import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import { formatBytes } from "./bytes";

const UNITS = ["B", "KB", "MB", "GB", "TB"];
const SHOWN = /^(-?)(\d+(?:\.\d+)?) (B|KB|MB|GB|TB)$/;

/** A size as the number a unit is shown with: what is printed, and how many bytes one of that unit is. */
function read(text: string) {
  const match = SHOWN.exec(text);
  if (!match) throw new Error(`not a size: ${text}`);
  const unit = UNITS.indexOf(match[3]);
  return {
    negative: match[1] === "-",
    figure: Number(match[2]),
    decimals: match[2].split(".")[1]?.length ?? 0,
    unit,
    unitBytes: 1024 ** unit,
  };
}

const wholeBytes = fc.oneof(
  fc.integer({ min: 0, max: 5000 }),
  fc.integer({ min: 0, max: Number.MAX_SAFE_INTEGER }),
  fc.integer({ min: 1, max: 6 }).chain((power) =>
    // The ends of each unit, where the printed figure rounds up into the next one.
    fc.integer({ min: -2048, max: 2048 }).map((nudge) => 1024 ** power + nudge),
  ),
);

it("prints a size as a figure and a unit, with a minus sign only when the size is below zero", () => {
  assertProperty(
    fc.property(
      fc.oneof(
        wholeBytes,
        wholeBytes.map((bytes) => -bytes),
      ),
      (bytes) => {
        const shown = read(formatBytes(bytes));
        expect(shown.negative).toBe(bytes < 0);
      },
    ),
  );
});

it("never prints a figure of 1024 or more in a unit that has a bigger one", () => {
  assertProperty(
    fc.property(wholeBytes, (bytes) => {
      const shown = read(formatBytes(bytes));
      if (shown.unit < UNITS.length - 1) {
        expect(shown.figure).toBeLessThan(1024);
      }
    }),
  );
});

it("prints a size to within half a step of the last digit it shows", () => {
  assertProperty(
    fc.property(wholeBytes, (bytes) => {
      const shown = read(formatBytes(bytes));
      const step = 10 ** -shown.decimals * shown.unitBytes;
      expect(
        Math.abs(shown.figure * shown.unitBytes - bytes),
      ).toBeLessThanOrEqual(step / 2 + 1e-6 * bytes);
    }),
  );
});

it("prints a bigger size in the same or a bigger unit than a smaller one", () => {
  assertProperty(
    fc.property(wholeBytes, wholeBytes, (a, b) => {
      const [smaller, larger] = a <= b ? [a, b] : [b, a];
      expect(read(formatBytes(larger)).unit).toBeGreaterThanOrEqual(
        read(formatBytes(smaller)).unit,
      );
    }),
  );
});

it("says nothing for a size that is missing or not a number", () => {
  assertProperty(
    fc.property(
      fc.constantFrom(null, undefined, Number.NaN, Infinity, -Infinity),
      (missing) => {
        expect(formatBytes(missing)).toBe("");
      },
    ),
  );
});
