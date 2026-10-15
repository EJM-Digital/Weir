import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import { withQuery } from "./files-api";

const name = fc.stringMatching(/^[a-z_]{1,10}$/);

const value = fc.oneof(
  fc.string({ unit: "grapheme", maxLength: 16 }),
  fc.constantFrom("a&b=c", "100%", "x+y", "#top", "?q=1", "é ü", ",", "a,b"),
  fc.integer({ min: -1000, max: 1_000_000 }),
  fc.double({ noNaN: false }),
  fc.boolean(),
  fc.constantFrom(undefined, null, ""),
);

const query = fc.dictionary(name, value, { maxKeys: 8 });

const isSet = (entry: unknown) =>
  entry !== undefined && entry !== null && entry !== "";

it("asks for each filter that is set exactly once with its value as text, and for none that is not set", () => {
  assertProperty(
    fc.property(query, (filters) => {
      const path = withQuery("/api/v1/processing/files", filters);
      const asked = new URLSearchParams(path.split("?")[1] ?? "");
      const expected = Object.entries(filters).filter(([, entry]) =>
        isSet(entry),
      );
      expect([...asked.keys()].sort()).toEqual(
        expected.map(([key]) => key).sort(),
      );
      for (const [key, entry] of expected) {
        expect(asked.get(key)).toBe(String(entry));
      }
    }),
  );
});

it("leaves the path alone when nothing is set, and puts the filters after it when something is", () => {
  assertProperty(
    fc.property(query, (filters) => {
      const path = withQuery("/api/v1/processing/files", filters);
      const anySet = Object.values(filters).some(isSet);
      expect(path.startsWith("/api/v1/processing/files")).toBe(true);
      expect(path.includes("?")).toBe(anySet);
      expect(path.endsWith("?")).toBe(false);
    }),
  );
});
