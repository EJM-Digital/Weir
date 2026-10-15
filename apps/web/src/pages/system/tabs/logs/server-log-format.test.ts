import { expect, it } from "vitest";

import { formatAverageMs } from "./server-log-format";

it("prints a time that rounds up to 100 milliseconds as 100, without a decimal", () => {
  expect(formatAverageMs(99.95)).toBe("100 ms");
  expect(formatAverageMs(99.94)).toBe("99.9 ms");
  expect(formatAverageMs(100.4)).toBe("100 ms");
});
