import { renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import {
  parseAppDate,
  parseAppTime,
  useAppClockFormatter,
  useAppClockSecondsFormatter,
  useAppDateFormatter,
} from "./mm-format-date";

const settings = { timezone: "UTC" };
vi.mock("../settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: { app_timezone: settings.timezone } }),
}));

const TEN_AM_UTC = Date.UTC(2026, 7, 22, 10, 0, 0);

describe("parseAppDate", () => {
  it("reads a timestamp without a zone as UTC", () => {
    expect(parseAppDate("2026-08-22T10:00:00").getTime()).toBe(TEN_AM_UTC);
  });

  it("keeps a trailing Z as it is", () => {
    expect(parseAppDate("2026-08-22T10:00:00Z").getTime()).toBe(TEN_AM_UTC);
  });

  it("keeps a positive offset", () => {
    expect(parseAppDate("2026-08-22T20:00:00+10:00").getTime()).toBe(
      TEN_AM_UTC,
    );
  });

  it("keeps a negative offset rather than appending a second zone", () => {
    expect(parseAppDate("2026-08-22T05:00:00-05:00").getTime()).toBe(
      TEN_AM_UTC,
    );
  });
});

describe("parseAppTime", () => {
  it("gives null for a missing or unreadable timestamp", () => {
    expect(parseAppTime(null)).toBeNull();
    expect(parseAppTime("")).toBeNull();
    expect(parseAppTime("not a date")).toBeNull();
  });

  it("gives the epoch ms of a readable one", () => {
    expect(parseAppTime("2026-08-22T10:00:00")).toBe(TEN_AM_UTC);
  });
});

describe("useAppClockFormatter", () => {
  const clockIn = (timezone: string) => {
    settings.timezone = timezone;
    return renderHook(() => useAppClockFormatter()).result.current(TEN_AM_UTC);
  };

  it("writes the time in the timezone chosen in Settings", () => {
    expect(clockIn("UTC")).toMatch(/^10:00\sam$/);
    expect(clockIn("Australia/Brisbane")).toMatch(/^8:00\spm$/);
  });

  it("falls back to the browser's clock for a timezone it does not know", () => {
    expect(clockIn("Not/AZone")).toMatch(/\d:\d\d/);
  });
});

describe("useAppDateFormatter", () => {
  const formatIn = (timezone: string, iso: string | null) => {
    settings.timezone = timezone;
    return renderHook(() => useAppDateFormatter()).result.current(iso);
  };

  it("writes the date and time in the timezone chosen in Settings", () => {
    // Unlike the clock formatters, this one keeps the browser's own wording (AM or am, 12 or 24 hour), so only the hour is asserted.
    expect(formatIn("UTC", "2026-08-22T10:00:00")).toMatch(/\b10:00/);
    expect(formatIn("Australia/Brisbane", "2026-08-22T10:00:00")).toMatch(
      /\b(8:00|20:00)/,
    );
  });

  it("shows a dash for no timestamp", () => {
    expect(formatIn("UTC", null)).toBe("—");
  });

  it("shows what the server sent when the timestamp cannot be read", () => {
    expect(formatIn("UTC", "not a date")).toBe("not a date");
  });

  it("shows what the server sent when the timezone is not one the browser knows", () => {
    expect(formatIn("Not/AZone", "2026-08-22T10:00:00")).toBe(
      "2026-08-22T10:00:00",
    );
  });
});

describe("useAppClockSecondsFormatter", () => {
  const clockIn = (timezone: string) => {
    settings.timezone = timezone;
    return renderHook(() => useAppClockSecondsFormatter()).result.current(
      TEN_AM_UTC + 5000,
    );
  };

  it("writes the time with its seconds in the timezone chosen in Settings", () => {
    expect(clockIn("UTC")).toMatch(/^10:00:05\sam$/);
    expect(clockIn("Australia/Brisbane")).toMatch(/^8:00:05\spm$/);
  });

  it("falls back to the browser's clock for a timezone it does not know", () => {
    expect(clockIn("Not/AZone")).toMatch(/\d:\d\d:\d\d/);
  });
});
