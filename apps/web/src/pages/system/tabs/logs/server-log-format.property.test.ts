import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../../../test/property";
import {
  formatAverageMs,
  formatRuntimeUptime,
  requestIssueSummary,
} from "./server-log-format";

const DAY = 86_400;
const HOUR = 3_600;

it("says how long Weir has been running in the largest two units, never more than it has run", () => {
  assertProperty(
    fc.property(
      fc.oneof(
        fc.double({ min: 1, max: 100 * DAY, noNaN: true }),
        fc.integer({ min: 1, max: 100 * DAY }),
      ),
      (seconds) => {
        const text = formatRuntimeUptime(seconds);
        const whole = Math.floor(seconds);
        const days = /^(\d+)d (\d+)h$/.exec(text);
        const hours = /^(\d+)h (\d+)m$/.exec(text);
        const minutes = /^(\d+)m$/.exec(text);
        if (days) {
          expect(whole).toBeGreaterThanOrEqual(DAY);
          expect(Number(days[1]) * DAY + Number(days[2]) * HOUR).toBe(
            whole - (whole % HOUR),
          );
          expect(Number(days[2])).toBeLessThan(24);
        } else if (hours) {
          expect(whole).toBeGreaterThanOrEqual(HOUR);
          expect(whole).toBeLessThan(DAY);
          expect(Number(hours[1]) * HOUR + Number(hours[2]) * 60).toBe(
            whole - (whole % 60),
          );
          expect(Number(hours[2])).toBeLessThan(60);
        } else {
          expect(minutes).not.toBeNull();
          expect(whole).toBeLessThan(HOUR);
          expect(Number(minutes![1])).toBe(Math.floor(whole / 60));
        }
      },
    ),
  );
});

it("says Weir has just started when there is no time to say", () => {
  assertProperty(
    fc.property(
      fc.oneof(
        fc.constantFrom(Number.NaN, -Infinity, 0),
        fc.double({ max: 0, noNaN: true }),
      ),
      (nothing) => {
        expect(formatRuntimeUptime(nothing)).toBe("Just started");
      },
    ),
  );
});

it("prints a request time in whole milliseconds from 100 up and with one decimal below", () => {
  assertProperty(
    fc.property(
      fc.oneof(
        fc.double({ min: 0, max: 100_000, noNaN: true }),
        fc.double({ min: 99.9, max: 100.1, noNaN: true }),
      ),
      (milliseconds) => {
        const text = formatAverageMs(milliseconds);
        const match = /^(\d+)(?:\.(\d))? ms$/.exec(text);
        expect(match).not.toBeNull();
        const shown = Number(`${match![1]}${match![2] ? `.${match![2]}` : ""}`);
        if (milliseconds <= 0) {
          expect(text).toBe("0 ms");
          return;
        }
        expect(match![2] === undefined).toBe(shown >= 100);
        expect(Math.abs(shown - milliseconds)).toBeLessThanOrEqual(
          shown >= 100 ? 0.5 + 1e-9 : 0.05 + 1e-9,
        );
      },
    ),
  );
  assertProperty(
    fc.property(
      fc.oneof(
        fc.constantFrom(Number.NaN, Infinity, -Infinity),
        fc.double({ max: 0, noNaN: true }),
      ),
      (nothing) => {
        expect(formatAverageMs(nothing)).toBe("0 ms");
      },
    ),
  );
});

it("names the server failures first when there are any, then the rejected requests, and says so when there are none", () => {
  const count = fc.integer({ min: 0, max: 100_000 });
  assertProperty(
    fc.property(count, count, count, count, (ok, moved, rejected, failed) => {
      const { value, detail } = requestIssueSummary({
        "2xx": ok,
        "3xx": moved,
        "4xx": rejected,
        "5xx": failed,
      });
      const shown = (number: number) => number.toLocaleString();
      if (failed > 0) {
        expect(value).toContain(shown(failed));
        expect(value).toMatch(/server failures?$/);
      } else if (rejected > 0) {
        expect(value).toContain(shown(rejected));
        expect(value).toMatch(/request issues?$/);
      } else {
        expect(value).toBe("No request issues");
      }
      for (const number of [ok, moved, rejected, failed]) {
        expect(detail).toContain(String(number));
      }
    }),
  );
});
