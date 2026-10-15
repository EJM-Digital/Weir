import fc from "fast-check";
import { expect, it } from "vitest";

import { assertProperty } from "../../test/property";
import { baseName } from "./path";

const name = fc.stringMatching(/^[A-Za-z0-9 ._()[\]-]{1,12}$/);
const separators = fc.constantFrom("/", "\\", "//", "\\\\", "/\\");

/** Names joined by separators of either kind, with a start and an end of separators or none. */
const path = fc
  .tuple(
    fc.array(name, { minLength: 1, maxLength: 6 }),
    fc.array(separators, { minLength: 7, maxLength: 7 }),
    fc.constantFrom("", "/", "C:\\", "\\\\nas\\media\\"),
    fc.constantFrom("", "/", "\\"),
  )
  .map(([names, joiners, start, end]) => ({
    names,
    text:
      start +
      names
        .map((part, index) => (index === 0 ? part : joiners[index] + part))
        .join("") +
      end,
  }));

it("reads the last name of a path written with either kind of separator", () => {
  assertProperty(
    fc.property(path, ({ names, text }) => {
      expect(baseName(text)).toBe(names[names.length - 1]);
    }),
  );
});

it("gives a name with no separator in it back as it is", () => {
  assertProperty(
    fc.property(name, (single) => {
      expect(baseName(single)).toBe(single);
    }),
  );
});

it("gives back something without a separator unless the path is nothing but separators", () => {
  assertProperty(
    fc.property(fc.string({ unit: "binary" }), (anything) => {
      const result = baseName(anything);
      if (/[^\\/]/.test(anything)) {
        expect(result).not.toMatch(/[\\/]/);
      } else {
        expect(result).toBe(anything);
      }
    }),
  );
});
