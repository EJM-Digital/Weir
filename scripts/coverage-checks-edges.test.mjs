// node --test scripts/coverage-checks-edges.test.mjs
// The coverage floor checks, the exclusions check and the recording checks on the input their own tests leave out: a report with
// gaps in it, a base file without a project, ties, a missing list.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { compare } from "./check-coverage-exclusions.mjs";
import {
  figuresFromSummary,
  filesFromCobertura,
  leastCovered,
  loadFigures,
  loweredFloorProblems,
  PROJECTS,
  renderSummary,
} from "./check-coverage-floors.mjs";
import { clustersOf, onlyTargets, renderClusters } from "./check-mutation-floors.mjs";
import { problems as freshnessProblems } from "./check-recorded-freshness.mjs";
import { parseSemver } from "./semver.mjs";

const here = (name) => fileURLToPath(new URL(name, import.meta.url));

function inFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-coverage-edges-"));
  try {
    return body(folder);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

const put = (root, file, text) => {
  mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
  writeFileSync(path.join(root, file), text);
};

test("a project the base file has no floor for is not compared, and a summary with no coverage has no assembly", () => {
  const floors = { floors: Object.fromEntries(PROJECTS.map((project) => [project.id, { line: 50, branch: 50 }])) };
  assert.deepEqual(loweredFloorProblems(floors, { floors: {} }), { problems: [], notes: [] });
  assert.equal(figuresFromSummary({}, "Weir.Core"), undefined);
});

test("a Cobertura class with no file name or no lines is skipped, and a lines block with no line in it counts nothing", () => {
  const report = [
    '<package name="Weir.Core"><classes>',
    '<class name="NoFile"><lines><line number="1" hits="1" /></lines></class>',
    '<class name="NoLines" filename="a.cs"><methods></methods></class>',
    '<class name="EmptyBlock" filename="b.cs"><methods></methods><lines></lines></class>',
    "</classes></package>",
  ].join("\n");
  assert.deepEqual(filesFromCobertura(report), [{ assembly: "Weir.Core", filename: "b.cs", line: { covered: 0, total: 0 }, branch: { covered: 0, total: 0 } }]);
});

test("files with the same number of gaps are listed by name", () => {
  const file = (filename) => ({ filename, line: { covered: 0, total: 1 }, branch: { covered: 0, total: 0 } });
  assert.deepEqual(leastCovered([file("b.ts"), file("a.ts")], "src").map((entry) => entry.name), ["src/a.ts", "src/b.ts"]);
});

test("the summary lists a note and a problem under the table, and says nothing of a target with no files", () => {
  const rows = [];
  const floors = { measuredFrom: { run: 7 } };
  const text = renderSummary({ rows, notes: ["Web line floor raised."], problems: ["Tray fell below its floor."], targets: [{ project: { label: "Web" }, files: [] }], floors });
  assert.match(text, /\n- Web line floor raised\.\n/);
  assert.match(text, /\n\*\*Tray fell below its floor\.\*\*\n/);
  assert.doesNotMatch(text, /Least covered/);
});

test("a web summary with no totals is reported as such", () => {
  inFolder((folder) => {
    put(folder, "coverage-summary.json", JSON.stringify({ "src/a.ts": { lines: { covered: 1, total: 2 }, branches: { covered: 0, total: 0 } } }));
    const web = PROJECTS.find((project) => project.report === "web");
    const { figures, files } = loadFigures(web, { web: folder });
    assert.match(figures.missing, /coverage-summary\.json has no totals\./);
    assert.equal(files.length, 1);
  });
});

test("exclusion entries that are not objects, or lack a file or a marker, are left to the shape check", () => {
  assert.deepEqual(compare([null, {}, { file: "a" }, { marker: "b" }], []), []);
});

test("mutation survivors in files with equally many are listed by file, many tests are counted, and every unknown target is named", () => {
  const survivor = (file, tests) => ({ file, line: 1, column: 1, mutator: "Block", original: "a", replacement: "b", covered: true, tests });
  const clusters = clustersOf([survivor("b.cs", []), survivor("a.cs", [])]);
  assert.deepEqual(clusters.map((cluster) => cluster.file), ["a.cs", "b.cs"]);
  const rendered = renderClusters(clustersOf([survivor("a.cs", ["N.C.One", "N.C.Two", "N.C.Three", "N.C.Four"])]));
  assert.match(rendered, /C\.One; C\.Two; and 2 more/);
  assert.throws(() => onlyTargets({ floors: { core: {} } }, ["a", "b"]), /--only names a, b, which have no floor in the floors file\./);
});

test("with no list of sets there are no recordings to judge", () => {
  assert.deepEqual(freshnessProblems(undefined, [], [], parseSemver("1.0.0")), ["weir-builds-recorded.json lists no recordings, so nothing shows how recent they are."]);
});

test("the freshness check, run as a command, takes the newest release line of the changelog and finds the recordings recent enough", () => {
  inFolder((root) => {
    put(root, "CHANGELOG.md", "- **1.0.0-rc.1** (a). x\n- **1.0.0-rc.3** (b). y\n- **1.0.0-rc.2** (c). z\n");
    put(root, "apps/server/tests/fixtures/recorded/weir-builds-recorded.json", JSON.stringify({ sets: { "deluno/1": { weirVersion: "1.0.0-rc.3" } } }));
    put(root, "apps/server/tests/fixtures/recorded/deluno/1/state.json", "{}");
    const run = spawnSync(process.execPath, [here("./check-recorded-freshness.mjs"), "--root", root], { encoding: "utf8" });
    assert.equal(run.status, 0, run.stderr);
    assert.match(run.stdout, /^The newest recordings of Weir are at most 1 release behind 1\.0\.0-rc\.3\./);
  });
});
