import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import {
  activityExportPath,
  activityRecentPath,
  filenameFromDisposition,
  type ActivityRecentFilters,
} from "./activity-api";

/** Text a person can type into a filter, including everything that means something in an address. */
const typed = fc.oneof(
  fc.string({ unit: "grapheme", maxLength: 16 }),
  fc.constantFrom(
    "a&b=c",
    "100%",
    "x+y",
    "#top",
    "?q=1",
    "é ü",
    "a b",
    "/path/to",
    "=",
    "&",
  ),
);

/** A number a form could hand over: whole, fractional, negative, or not a number at all. */
const anyNumber = fc.oneof(
  fc.integer({ min: -5, max: 1_000_000 }),
  fc.double({ min: -5, max: 1_000_000, noNaN: true }),
  fc.constantFrom(Number.NaN, Infinity, -Infinity),
);

const filters: fc.Arbitrary<ActivityRecentFilters> = fc.record(
  {
    limit: anyNumber,
    module: typed,
    event_type: typed,
    search: typed,
    date_from: typed,
    date_to: typed,
    before_id: anyNumber,
    trigger: typed,
    result: typed,
    library_id: anyNumber,
    file: typed,
    about: fc.constantFrom("weir", "files"),
    known_files_only: fc.boolean(),
    current_only: fc.boolean(),
    with_total: fc.boolean(),
  },
  { requiredKeys: [] },
);

const TEXT_FILTERS = [
  "module",
  "event_type",
  "search",
  "date_from",
  "date_to",
  "trigger",
  "result",
  "file",
] as const;

function queryOf(path: string): URLSearchParams {
  return new URLSearchParams(path.split("?")[1] ?? "");
}

it("asks for each filter that is set exactly once, with the value as typed", () => {
  assertProperty(
    fc.property(filters, (set) => {
      const path = activityRecentPath(set);
      expect(path.startsWith("/api/v1/activity/recent")).toBe(true);
      const query = queryOf(path);
      for (const name of TEXT_FILTERS) {
        const value = set[name];
        expect(query.getAll(name)).toEqual(value ? [value] : []);
      }
      expect(query.getAll("about")).toEqual(set.about ? [set.about] : []);
    }),
  );
});

it("asks only for whole numbers, and leaves out one that is not a number", () => {
  assertProperty(
    fc.property(filters, (set) => {
      const query = queryOf(activityRecentPath(set));
      for (const name of ["limit", "before_id", "library_id"] as const) {
        const value = set[name];
        const asked = query.getAll(name);
        if (value === undefined || !Number.isFinite(value)) {
          expect(asked).toEqual([]);
        } else {
          expect(asked).toEqual([String(Math.trunc(value))]);
        }
      }
    }),
  );
});

it("asks for the live list's switches only when they are on, and for no total only when it is off", () => {
  assertProperty(
    fc.property(filters, (set) => {
      const query = queryOf(activityRecentPath(set));
      expect(query.getAll("known_files_only")).toEqual(
        set.known_files_only ? ["true"] : [],
      );
      expect(query.getAll("current_only")).toEqual(
        set.current_only ? ["true"] : [],
      );
      expect(query.getAll("with_total")).toEqual(
        set.with_total === false ? ["false"] : [],
      );
    }),
  );
});

it("asks for no query at all when no filter is set", () => {
  assertProperty(
    fc.property(
      fc.constantFrom(undefined, {}, { module: "" }, { search: "" }),
      (empty) => {
        expect(activityRecentPath(empty)).toBe("/api/v1/activity/recent");
      },
    ),
  );
});

it("exports the whole record: the filters and the format, never a page, a limit or a live list's switches", () => {
  assertProperty(
    fc.property(filters, fc.constantFrom("csv", "json"), (set, format) => {
      const query = queryOf(activityExportPath(format, set));
      expect(query.getAll("format")).toEqual([format]);
      for (const name of [
        "limit",
        "before_id",
        "known_files_only",
        "current_only",
        "with_total",
      ]) {
        expect(query.has(name)).toBe(false);
      }
      for (const name of TEXT_FILTERS) {
        const value = set[name];
        expect(query.getAll(name)).toEqual(value ? [value] : []);
      }
    }),
  );
});

it("reads the file name from a download's header, or falls back", () => {
  assertProperty(
    fc.property(
      fc.stringMatching(/^[A-Za-z0-9._ -]{1,20}$/),
      fc.constantFrom("", '"', "attachment; "),
      (name, quote) => {
        const header = `attachment; filename=${quote === '"' ? '"' : ""}${name}${quote === '"' ? '"' : ""}`;
        const read = filenameFromDisposition(header, "fallback.csv");
        expect(read).toBe(name.trim() || "fallback.csv");
      },
    ),
  );
  assertProperty(
    fc.property(fc.option(typed, { nil: null }), (header) => {
      const read = filenameFromDisposition(header, "fallback.csv");
      expect(read.length).toBeGreaterThan(0);
    }),
  );
});
