import { afterEach, expect, it, vi } from "vitest";

import { zoneClock } from "./schedule-model";

afterEach(() => vi.restoreAllMocks());

it("reads the start of the week when the browser gives no parts of the time", () => {
  vi.spyOn(Intl.DateTimeFormat.prototype, "formatToParts").mockReturnValue([]);

  expect(zoneClock(new Date("2026-10-07T12:34:00Z"), "UTC")).toEqual({
    weekday: 0,
    hour: 0,
    minute: 0,
  });
});
