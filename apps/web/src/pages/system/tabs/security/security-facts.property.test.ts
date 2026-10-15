import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../../../test/property";
import { formatSessionTimeout } from "./security-facts";

const MINUTES = { minute: 1, hour: 60, day: 1440 } as const;

it("says a session timeout in the largest whole unit that holds it exactly", () => {
  assertProperty(
    fc.property(
      fc.oneof(
        fc.integer({ min: 1, max: 1_000_000 }),
        fc.integer({ min: 1, max: 700 }).map((days) => days * 1440),
        fc.integer({ min: 1, max: 5000 }).map((hours) => hours * 60),
      ),
      (minutes) => {
        const text = formatSessionTimeout(minutes);
        const match = /^([\d,]+) (minute|hour|day)s?$/.exec(text);
        expect(match).not.toBeNull();
        const count = Number(match![1].replaceAll(",", ""));
        const unit = match![2] as keyof typeof MINUTES;
        expect(count * MINUTES[unit]).toBe(minutes);
        expect(text.endsWith("s")).toBe(count !== 1);
        // Nothing bigger would have held it exactly.
        if (unit === "minute") expect(minutes % 60).not.toBe(0);
        if (unit === "hour") expect(minutes % 1440).not.toBe(0);
      },
    ),
  );
});
