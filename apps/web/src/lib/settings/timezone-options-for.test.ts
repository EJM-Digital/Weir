import { describe, expect, it } from "vitest";

import {
  CURATED_TIMEZONE_ID_SET,
  timezoneOptionsFor,
} from "./timezone-options";

describe("timezoneOptionsFor", () => {
  it("offers UTC, so a server on UTC has a zone to show", () => {
    expect(CURATED_TIMEZONE_ID_SET.has("UTC")).toBe(true);
    expect(
      timezoneOptionsFor("UTC").find((o) => o.id === "UTC")?.label,
    ).toMatch(/^UTC — Coordinated Universal Time \(UTC\+0\)$/);
  });

  it.each(["Australia/Perth", "UTC"])(
    "adds nothing for %s, which the list already has",
    (zone) => {
      const ids = timezoneOptionsFor(zone).map((o) => o.id);
      expect(ids.filter((id) => id === zone)).toHaveLength(1);
      expect(ids).toHaveLength(new Set(ids).size);
    },
  );

  it("adds a saved zone the list lacks, first, named with its offset", () => {
    const options = timezoneOptionsFor("Atlantic/Reykjavik");

    expect(options[0]).toEqual({
      id: "Atlantic/Reykjavik",
      label: "Atlantic/Reykjavik (UTC+0)",
    });
    expect(options).toHaveLength(timezoneOptionsFor("UTC").length + 1);
  });

  it("writes the underscores of a city as spaces", () => {
    expect(timezoneOptionsFor("America/Port_of_Spain")[0]?.label).toMatch(
      /^America\/Port of Spain \(UTC-4\)$/,
    );
  });

  it("names a zone the browser does not know by its id alone", () => {
    expect(timezoneOptionsFor("AUS Eastern Standard Time")[0]).toEqual({
      id: "AUS Eastern Standard Time",
      label: "AUS Eastern Standard Time",
    });
  });

  it("adds nothing when no zone is saved", () => {
    expect(timezoneOptionsFor("  ")).toEqual(timezoneOptionsFor("UTC"));
  });
});
