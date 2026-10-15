import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import {
  filterFromSearch,
  resolveWorkflow,
  viewFromSearch,
  workflowFromSearch,
} from "./dashboard-address";

const typed = fc.oneof(
  fc.string({ unit: "grapheme", maxLength: 14 }),
  fc.constantFrom(
    "1",
    "007",
    "0",
    "-3",
    "3.5",
    "1e3",
    "12345678901",
    "9999999999",
    " 4",
    "4 ",
  ),
);

it("reads a workflow from an address only when it is a whole id, and reads it as that id", () => {
  assertProperty(
    fc.property(typed, (raw) => {
      const found = workflowFromSearch(new URLSearchParams({ workflow: raw }));
      if (/^[1-9]\d{0,9}$/.test(raw)) {
        expect(found).toBe(Number(raw));
        expect(String(found)).toBe(raw);
      } else {
        expect(found).toBeNull();
      }
    }),
  );
  expect(workflowFromSearch(new URLSearchParams())).toBeNull();
});

it("reads every whole id back from the address that names it", () => {
  assertProperty(
    fc.property(fc.integer({ min: 1, max: 9_999_999_999 }), (id) => {
      const address = new URLSearchParams({ workflow: String(id) });
      expect(workflowFromSearch(new URLSearchParams(address.toString()))).toBe(
        id,
      );
    }),
  );
});

it("is on Live and on every kind of work unless the address names something else it knows", () => {
  assertProperty(
    fc.property(typed, (raw) => {
      expect(viewFromSearch(new URLSearchParams({ view: raw }))).toBe(
        raw === "system" ? "system" : "live",
      );
      expect(filterFromSearch(new URLSearchParams({ work: raw }))).toBe(
        raw === "download" || raw === "library" ? raw : "all",
      );
    }),
  );
});

it("keeps the workflow an address asks for until the list is known, and drops one that is not on it", () => {
  const ids = fc.uniqueArray(fc.integer({ min: 1, max: 50 }), { maxLength: 8 });
  assertProperty(
    fc.property(
      fc.option(fc.integer({ min: 1, max: 50 }), { nil: null }),
      fc.option(ids, { nil: undefined }),
      (requested, known) => {
        const resolved = resolveWorkflow(
          requested,
          known?.map((id) => ({ id })),
        );
        if (requested === null || known === undefined) {
          expect(resolved).toBe(requested);
        } else {
          expect(resolved).toBe(known.includes(requested) ? requested : null);
        }
      },
    ),
  );
});
