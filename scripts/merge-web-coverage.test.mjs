// node --test scripts/merge-web-coverage.test.mjs
// How the unit tests' and the browser tests' coverage of one file are laid over each other.
import assert from "node:assert/strict";
import { test } from "node:test";

import { EXACT, LOOSE, combine, mergeFile, relativeKey } from "./merge-web-coverage.mjs";

const at = (line, column, endLine = line, endColumn = column + 4) => ({ start: { line, column }, end: { line: endLine, column: endColumn } });

/** One file's coverage: statements at the given places, one `if` over lines 5 to 9, one function. */
function file({ statements, ifArms = [at(6, 2), at(8, 2)], functionAt = at(1, 0, 10, 1), counts = {} }) {
  return {
    path: "src/a.ts",
    statementMap: Object.fromEntries(statements.map((place, id) => [String(id), place])),
    s: Object.fromEntries(statements.map((_, id) => [String(id), counts.s?.[id] ?? 0])),
    fnMap: { 0: { name: "f", decl: functionAt, loc: functionAt, line: functionAt.start.line } },
    f: { 0: counts.f ?? 0 },
    branchMap: { 0: { type: "if", loc: at(5, 2, 9, 3), locations: ifArms, line: 5 } },
    b: { 0: counts.b ?? [0, 0] },
  };
}

test("a path is matched by where it sits under the web app", () => {
  assert.equal(relativeKey("C:\\work\\Weir\\apps\\web\\src\\main.tsx", "apps/web"), "src/main.tsx");
  assert.equal(relativeKey("/home/runner/work/Weir/Weir/apps/web/src/main.tsx", "apps/web"), "src/main.tsx");
  assert.equal(relativeKey("/home/runner/work/Weir/Weir/node_modules/react/index.js", "apps/web"), undefined);
});

test("what the same positions ran in two reports of one build is added up", () => {
  const target = file({ statements: [at(2, 2), at(3, 2)], counts: { s: [1, 0], f: 1, b: [1, 0] } });
  const source = file({ statements: [at(2, 2), at(3, 2)], counts: { s: [2, 3], f: 4, b: [0, 5] } });

  assert.equal(mergeFile(target, source, EXACT), 0);

  assert.deepEqual(target.s, { 0: 3, 1: 3 });
  assert.deepEqual(target.f, { 0: 5 });
  assert.deepEqual(target.b, { 0: [1, 5] });
});

test("two tools that start a thing at the same place but end it elsewhere count it once", () => {
  const unit = file({ statements: [at(2, 2, 2, null)], ifArms: [at(6, 2, 6, null), at(8, 2, 8, null)], functionAt: at(1, 0, 10, null) });
  const browser = file({ statements: [at(2, 2, 2, 30)], ifArms: [at(6, 2, 7, 3), at(8, 2, 9, 3)], functionAt: at(1, 0, 10, 1), counts: { s: [1], f: 1, b: [0, 1] } });

  const { merged, unmatched } = combine({ "src/a.ts": unit }, { "src/a.ts": browser });

  assert.equal(unmatched, 0);
  assert.deepEqual(merged["src/a.ts"].s, { 0: 1 });
  assert.deepEqual(merged["src/a.ts"].b, { 0: [0, 1] });
  assert.deepEqual(merged["src/a.ts"].f, { 0: 1 });
});

test("a statement only the browser report knows is kept, so the two tools' differences can only lower the figure", () => {
  const unit = file({ statements: [at(2, 2)] });
  const browser = file({ statements: [at(2, 2), at(4, 2)], counts: { s: [0, 0] } });

  const { merged, unmatched } = combine({ "src/a.ts": unit }, { "src/a.ts": browser });

  assert.equal(unmatched, 1);
  assert.deepEqual(merged["src/a.ts"].s, { 0: 0, 1: 0 });
});

test("a file only the browser report has is added whole", () => {
  const browser = file({ statements: [at(2, 2)], counts: { s: [1] } });

  const { merged } = combine({}, { "src/a.ts": browser });

  assert.deepEqual(Object.keys(merged), ["src/a.ts"]);
});

test("statements that start at the same place are matched in the order they come", () => {
  const unit = file({ statements: [at(2, 2), at(2, 2)] });
  const browser = file({ statements: [at(2, 2), at(2, 2)], counts: { s: [0, 7] } });

  mergeFile(unit, browser, LOOSE);

  assert.deepEqual(unit.s, { 0: 0, 1: 7 });
});
