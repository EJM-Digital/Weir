import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import {
  systemLogExportPath,
  systemLogPath,
  type SystemLogCategory,
  type SystemLogLevel,
  type SystemLogPageRequest,
  type SystemLogSource,
} from "./system-log-api";

const typed = fc.oneof(
  fc.string({ unit: "grapheme", maxLength: 16 }),
  fc.constantFrom("a&b=c", "100%", "x+y", "#top", "?q=1", "é ü", ",", "a,b"),
);

const SOURCES: SystemLogSource[] = ["event", "job", "server"];
const LEVELS: SystemLogLevel[] = ["error", "warning", "info", "success"];
const CATEGORIES: SystemLogCategory[] = ["processing", "scans", "weir"];

const request: fc.Arbitrary<SystemLogPageRequest> = fc.record(
  {
    source: fc.subarray(SOURCES),
    level: fc.subarray(LEVELS),
    category: fc.subarray(CATEGORIES),
    status: fc.subarray(["pending", "leased", "failed"]),
    workflow: fc.oneof(
      fc.integer({ min: 1, max: 100_000 }),
      fc.double({ min: 0, max: 1000, noNaN: true }),
    ),
    job: fc.integer({ min: 1, max: 100_000 }),
    q: typed,
    from: typed,
    to: typed,
    event_type: typed,
    result: typed,
    trigger: typed,
    has_exception: fc.boolean(),
    sort: fc.constantFrom("time", "level", "source", "category", "workflow"),
    direction: fc.constantFrom("asc", "desc"),
    cursor: typed,
    limit: fc.integer({ min: 1, max: 500 }),
  },
  { requiredKeys: [] },
);

function queryOf(path: string): URLSearchParams {
  return new URLSearchParams(path.split("?")[1] ?? "");
}

it("asks for each filter that is set exactly once and reads it back as it was typed", () => {
  assertProperty(
    fc.property(request, (set) => {
      const query = queryOf(systemLogPath(set));
      for (const name of ["source", "level", "category", "status"] as const) {
        const values = set[name];
        expect(query.getAll(name)).toEqual(
          values?.length ? [values.join(",")] : [],
        );
      }
      for (const name of [
        "q",
        "from",
        "to",
        "event_type",
        "result",
        "trigger",
        "cursor",
      ] as const) {
        const value = set[name];
        expect(query.getAll(name)).toEqual(value ? [value] : []);
      }
      for (const name of ["workflow", "job"] as const) {
        const value = set[name];
        expect(query.getAll(name)).toEqual(
          value === undefined ? [] : [String(Math.trunc(value))],
        );
      }
      expect(query.getAll("has_exception")).toEqual(
        set.has_exception === undefined ? [] : [String(set.has_exception)],
      );
      expect(query.getAll("limit")).toEqual(
        set.limit === undefined ? [] : [String(set.limit)],
      );
    }),
  );
});

it("exports with the same filters as the list asks for, the format, and no page", () => {
  assertProperty(
    fc.property(request, fc.constantFrom("csv", "json"), (set, format) => {
      const listed = queryOf(
        systemLogPath({ ...set, cursor: undefined, limit: undefined }),
      );
      const exported = queryOf(systemLogExportPath(format, set));
      expect(exported.getAll("format")).toEqual([format]);
      expect(exported.has("cursor")).toBe(false);
      expect(exported.has("limit")).toBe(false);
      exported.delete("format");
      expect(exported.toString()).toBe(listed.toString());
    }),
  );
});

it("asks for no query at all when nothing is set", () => {
  expect(systemLogPath({})).toBe("/api/v1/system/log");
  assertProperty(
    fc.property(
      fc.constantFrom({ source: [] }, { q: "" }, { level: [], status: [] }),
      (empty) => {
        expect(systemLogPath(empty)).toBe("/api/v1/system/log");
      },
    ),
  );
});
