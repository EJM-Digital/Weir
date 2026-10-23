import { afterEach, expect, it, vi } from "vitest";

import { timezoneOptionsFor } from "./timezone-options";

afterEach(() => vi.restoreAllMocks());

const reykjavik = (offset: string) => ({
  id: "Atlantic/Reykjavik",
  label: `Atlantic/Reykjavik (${offset})`,
});

it("calls a zone UTC+0 when the browser reports no offset for it", () => {
  vi.spyOn(Intl.DateTimeFormat.prototype, "formatToParts").mockReturnValue([]);

  expect(timezoneOptionsFor("Atlantic/Reykjavik")[0]).toEqual(
    reykjavik("UTC+0"),
  );
});

it("calls a zone UTC+0 when the browser reports its offset as plain GMT", () => {
  vi.spyOn(Intl.DateTimeFormat.prototype, "formatToParts").mockReturnValue([
    { type: "timeZoneName", value: "GMT" },
  ]);

  expect(timezoneOptionsFor("Atlantic/Reykjavik")[0]).toEqual(
    reykjavik("UTC+0"),
  );
});

it("writes an offset the browser reports with UTC in place of GMT", () => {
  vi.spyOn(Intl.DateTimeFormat.prototype, "formatToParts").mockReturnValue([
    { type: "timeZoneName", value: "GMT+5:30" },
  ]);

  expect(timezoneOptionsFor("Atlantic/Reykjavik")[0]).toEqual(
    reykjavik("UTC+5:30"),
  );
});
