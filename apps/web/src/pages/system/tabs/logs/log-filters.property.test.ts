import fc from "fast-check";
import { expect, it } from "vitest";

import type {
  SystemLogCategory,
  SystemLogLevel,
} from "../../../../lib/system/system-log-api";
import { assertProperty } from "../../../../test/property";
import {
  anyLogFilterSet,
  EMPTY_LOG_FILTERS,
  filtersFromParams,
  LOG_CATEGORY_LABELS,
  LOG_JOB_STATUSES,
  LOG_LEVEL_CHOICES,
  LOG_PARAMS,
  LOG_SOURCES,
  LOG_WHEN_OPTIONS,
  levelChoicesFor,
  levelsForChoices,
  logQuery,
  paramsFromFilters,
  rangeFor,
  type LogFilters,
} from "./log-filters";

const LEVELS: SystemLogLevel[] = ["error", "warning", "info", "success"];
const CATEGORIES = Object.keys(LOG_CATEGORY_LABELS) as SystemLogCategory[];
const NOW = Date.UTC(2026, 9, 15, 12, 0);

/** Text a person can type into a field: any characters a string can hold without half a surrogate pair. */
const typed = fc.string({ unit: "grapheme", maxLength: 20 });

const shape = {
  source: fc.option(fc.constantFrom(...LOG_SOURCES.map((s) => s.value)), {
    nil: null,
  }),
  levels: fc.subarray(LEVELS),
  categories: fc.subarray(CATEGORIES),
  workflow: fc.option(fc.integer({ min: 1, max: 2 ** 31 }), { nil: null }),
  when: fc.constantFrom(...LOG_WHEN_OPTIONS.map((o) => o.value)),
  from: fc.oneof(
    fc.constant(""),
    fc.constantFrom("2026-10-02T08:30", "2026-10-15T23:59"),
    typed,
  ),
  to: fc.oneof(
    fc.constant(""),
    fc.constantFrom("2026-10-02T08:30", "2026-10-15T23:59"),
    typed,
  ),
  text: fc.oneof(typed, fc.constantFrom("", " ", "  ", " a ", "\tb\n")),
  eventType: typed,
  result: typed,
  trigger: typed,
  statuses: fc.subarray(LOG_JOB_STATUSES.map((s) => s.value)),
  stackOnly: fc.boolean(),
  job: fc.option(fc.integer({ min: 1, max: 2 ** 31 }), { nil: null }),
};

/** A view that sets many of the filters, and one that sets few of them, which is where a view that narrows nothing is found. */
const filters: fc.Arbitrary<LogFilters> = fc.oneof(
  fc.record(shape),
  fc
    .record(shape, { requiredKeys: [] })
    .map((few): LogFilters => ({ ...EMPTY_LOG_FILTERS, ...few })),
);

/** What the address holds of a view: the custom range only when the range is custom, and the text without the white space around it. */
function held(view: LogFilters): LogFilters {
  return {
    ...view,
    from: view.when === "custom" ? view.from : "",
    to: view.when === "custom" ? view.to : "",
    text: view.text.trim(),
  };
}

function throughTheAddress(view: LogFilters, base = ""): LogFilters {
  const address = paramsFromFilters(view, new URLSearchParams(base)).toString();
  return filtersFromParams(new URLSearchParams(address));
}

it("holds a view in the address and reads it back as the same view", () => {
  assertProperty(
    fc.property(filters, (view) => {
      expect(throughTheAddress(view)).toEqual(held(view));
    }),
  );
});

it("leaves every other part of the address as it was", () => {
  const others = fc.array(
    fc.tuple(
      fc
        .stringMatching(/^[a-z]{1,6}$/)
        .filter((name) => !(LOG_PARAMS as readonly string[]).includes(name)),
      typed,
    ),
    { maxLength: 5 },
  );
  assertProperty(
    fc.property(filters, others, (view, extra) => {
      const base = new URLSearchParams(extra);
      const address = paramsFromFilters(view, base);
      for (const [name] of extra) {
        expect(address.getAll(name)).toEqual(base.getAll(name));
      }
      expect(throughTheAddress(view, base.toString())).toEqual(held(view));
    }),
  );
});

it("reads any address as a view that names only choices the page has", () => {
  const address = fc.array(
    fc.tuple(
      fc.constantFrom(...LOG_PARAMS, "other"),
      fc.oneof(
        typed,
        fc.constantFrom(
          "error,warning",
          "event,job",
          "jobs",
          "0",
          "-4",
          "3.5",
          "1e3",
          "007",
          "custom",
          "1",
        ),
      ),
    ),
    { maxLength: 12 },
  );
  assertProperty(
    fc.property(address, (pairs) => {
      const view = filtersFromParams(new URLSearchParams(pairs));
      expect(view.levels.every((level) => LEVELS.includes(level))).toBe(true);
      expect(view.categories.every((c) => CATEGORIES.includes(c))).toBe(true);
      expect(
        view.statuses.every((s) => LOG_JOB_STATUSES.some((j) => j.value === s)),
      ).toBe(true);
      for (const id of [view.workflow, view.job]) {
        expect(id === null || (Number.isInteger(id) && id > 0)).toBe(true);
      }
      expect(LOG_WHEN_OPTIONS.some((o) => o.value === view.when)).toBe(true);
      expect(view.text).toBe(view.text.trim());
    }),
  );
});

it("asks the server for nothing when the view narrows nothing", () => {
  assertProperty(
    fc.property(
      filters,
      fc.constantFrom(undefined, "UTC", "Australia/Sydney"),
      (view, timeZone) => {
        if (anyLogFilterSet(view)) return;
        const query = logQuery(view, NOW, timeZone);
        expect(Object.values(query).filter((v) => v !== undefined)).toEqual([]);
      },
    ),
  );
  expect(anyLogFilterSet(EMPTY_LOG_FILTERS)).toBe(false);
});

it("asks the server for nothing when the only text in the view is white space", () => {
  assertProperty(
    fc.property(fc.stringMatching(/^\s{0,6}$/), (blank) => {
      const view = { ...EMPTY_LOG_FILTERS, text: blank };
      expect(anyLogFilterSet(view)).toBe(false);
      expect(logQuery(view, NOW, undefined)).toEqual({});
    }),
  );
});

it("asks the server for the same thing from a view as from the address that view made", () => {
  assertProperty(
    fc.property(
      filters,
      fc.constantFrom(undefined, "UTC", "America/New_York"),
      (view, timeZone) => {
        expect(logQuery(throughTheAddress(view), NOW, timeZone)).toEqual(
          logQuery(view, NOW, timeZone),
        );
      },
    ),
  );
});

it("starts a relative time exactly its length before now, and an open time at neither end", () => {
  const spans = { hour: 3_600_000, day: 86_400_000, week: 7 * 86_400_000 };
  assertProperty(
    fc.property(
      fc.integer({ min: Date.UTC(2000, 0, 1), max: Date.UTC(2090, 0, 1) }),
      fc.constantFrom("hour", "day", "week"),
      (now, when) => {
        const range = rangeFor({ when, from: "", to: "" }, now, "UTC");
        expect(Date.parse(range.from!)).toBe(now - spans[when]);
        expect(range.to).toBeUndefined();
        expect(
          rangeFor({ when: "any", from: "x", to: "y" }, now, "UTC"),
        ).toEqual({});
      },
    ),
  );
});

it("turns the level choices into the levels they stand for and back, and every choice into every level", () => {
  const choices = LOG_LEVEL_CHOICES.map((choice) => choice.value);
  assertProperty(
    fc.property(fc.subarray(choices), (chosen) => {
      const levels = levelsForChoices(chosen);
      if (chosen.length === choices.length) {
        expect(levels).toEqual([]);
      } else {
        expect(levelChoicesFor(levels)).toEqual(chosen);
      }
    }),
  );
});
