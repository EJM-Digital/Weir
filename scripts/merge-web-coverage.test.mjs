// node --test scripts/merge-web-coverage.test.mjs
// How the unit tests' and the browser tests' coverage of one file are laid over each other.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { EXACT, LOOSE, combine, main, mergeFile, parseArguments, readInputs, relativeKey } from "./merge-web-coverage.mjs";

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

// Reading the report files, and the command that merges the browser tests' parts.
async function withFolder(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-web-coverage-"));
  try {
    const put = (name, content) => {
      mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
      writeFileSync(path.join(root, name), JSON.stringify(content));
      return path.join(root, name);
    };
    await callback({ root, put });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const instrumented = (counts) => ({ ...file({ statements: [at(2, 2)], counts }), inputSourceMap: { version: 3 } });

test("reports are read from files and folders, and added up by file under the web app", async () => {
  await withFolder(async ({ root, put }) => {
    put("parts/a.json", { "/work/apps/web/src/a.ts": instrumented({ s: [1], f: 1, b: [1, 0] }) });
    put("parts/deep/b.json", [{ "/work/apps/web/src/a.ts": instrumented({ s: [2], f: 0, b: [0, 3] }), "/work/node_modules/x/index.js": instrumented({}) }]);
    put("parts/notes.txt", "not a report");
    const native = put("vitest.json", { "/work/apps/web/src/c.ts": file({ statements: [at(2, 2)], counts: { s: [4] } }) });

    const groups = readInputs([path.join(root, "parts"), native], "apps/web");

    assert.deepEqual(Object.keys(groups.instrumented), ["src/a.ts"]);
    assert.deepEqual(groups.instrumented["src/a.ts"].s, { 0: 3 });
    assert.deepEqual(groups.instrumented["src/a.ts"].b, { 0: [1, 3] });
    assert.deepEqual(Object.keys(groups.native), ["src/c.ts"]);
    assert.equal(groups.native["src/c.ts"].path, "src/c.ts");
  });
});

test("the command line names an output folder, an optional root and the inputs, and refuses anything else", async () => {
  await withFolder(async ({ root, put }) => {
    const input = put("one.json", {});
    assert.deepEqual(parseArguments(["--out", "o", "--root", "r", "--parts", input]), { root: "r", inputs: [input], parts: true, out: "o" });
    assert.equal(parseArguments(["--out", "o", input]).root, "apps/web");
    assert.throws(() => parseArguments(["--out", "o", "--nope", input]), /Unknown option --nope/);
    assert.throws(() => parseArguments(["--out", "o"]), /Usage: merge-web-coverage\.mjs/);
    assert.throws(() => parseArguments([input]), /Usage: merge-web-coverage\.mjs/);
    assert.throws(() => parseArguments(["--out", "o", path.join(root, "missing.json")]), /missing\.json does not exist/);
  });
});

test("with --parts the instrumented reports of the browser tests are written as one file, keyed by full path", async () => {
  await withFolder(async ({ root, put }) => {
    put("tests/a.json", { "/work/apps/web/src/a.ts": instrumented({ s: [1] }) });
    const out = path.join(root, "merged");
    const lines = [];

    await main(["--out", out, "--root", path.join(root, "apps", "web"), "--parts", path.join(root, "tests")], (line) => lines.push(line));

    assert.deepEqual(lines, [`Merged the browser tests' coverage of 1 files into ${out}.`]);
    const written = JSON.parse(readFileSync(path.join(out, "coverage-final.json"), "utf8"));
    assert.deepEqual(Object.keys(written), [path.join(root, "apps", "web", "src", "a.ts")]);
  });
});

test("inputs that hold nothing under the web app are an error, not an empty report", async () => {
  await withFolder(async ({ root, put }) => {
    const input = put("other.json", { "/work/node_modules/x/index.js": instrumented({}) });
    await assert.rejects(main(["--out", path.join(root, "o"), input], () => {}), /held no coverage of any file under the web app/);
  });
});

test("a function and a branch that only the source report knows are added, with nothing run in them yet", () => {
  const target = file({ statements: [at(2, 2)] });
  const source = file({ statements: [at(2, 2)], functionAt: at(20, 0, 25, 1), ifArms: [at(21, 2), at(23, 2)], counts: { f: 2, b: [1, 4] } });
  source.branchMap[0].loc = at(20, 2, 24, 3);

  assert.equal(mergeFile(target, source, EXACT), 2);

  assert.deepEqual(target.f, { 0: 0, 1: 2 });
  assert.deepEqual(target.b, { 0: [0, 0], 1: [1, 4] });
});

test("the merged map is handed to the reporting and its figures are logged", async () => {
  await withFolder(async ({ root, put }) => {
    put("tests/native.json", { "/work/apps/web/src/a.ts": file({ statements: [at(2, 2)], counts: { s: [1] } }) });
    const seen = {};
    const reporting = {
      toSource: async (instrumentedMap, rootFolder, keyOf) => {
        seen.instrumented = Object.keys(instrumentedMap);
        seen.key = keyOf("/work/apps/web/src/b.ts", rootFolder);
        return { "src/b.ts": file({ statements: [at(3, 3)] }) };
      },
      writeReports: (merged, rootFolder, out) => {
        seen.merged = Object.keys(merged);
        seen.out = out;
        return { lines: { pct: 50, covered: 1, total: 2 }, branches: { pct: 0, covered: 0, total: 2 } };
      },
    };
    const lines = [];

    await main(["--out", "o", "--root", "apps/web", path.join(root, "tests")], (line) => lines.push(line), reporting);

    assert.deepEqual(seen, { instrumented: [], key: "src/b.ts", merged: ["src/a.ts", "src/b.ts"], out: "o" });
    assert.deepEqual(lines, [
      "Merged web coverage: 50% of lines (1/2), 0% of branches (0/2).",
      "3 statements, functions and branches from the browser tests did not match one in the unit tests' report and were added.",
    ]);
  });
});

test("run as a command it says what is wrong and exits 1", () => {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL("./merge-web-coverage.mjs", import.meta.url))], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /Usage: merge-web-coverage\.mjs/);
});
