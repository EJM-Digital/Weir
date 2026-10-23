// node --test scripts/check-mutation-floors-command.test.mjs
// The mutation floors check as a command and at the edges of its reports: what it prints and exits with, reports that are short of fields,
// floors that rise or fall, and a list of survivors too long for a job summary.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  countMutants,
  evaluate,
  loweredFloorProblems,
  main,
  readFloors,
  renderSummary,
  renderSurvivorSummary,
  repoPath,
  snippetOf,
  survivorsOf,
} from "./check-mutation-floors.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const fixture = (name) => readFileSync(path.join(here, "fixtures", "mutation", name, "mutation-report.json"), "utf8");

const made = [];
after(() => {
  for (const folder of made) rmSync(folder, { recursive: true, force: true });
});
function folder() {
  const created = mkdtempSync(path.join(tmpdir(), "weir-mutation-command-"));
  made.push(created);
  return created;
}

const floorsOf = (floors, extra = {}) => ({
  measuredFrom: { runs: Object.fromEntries(Object.keys(floors).map((target) => [target, 41])) },
  floors: Object.fromEntries(Object.entries(floors).map(([target, score]) => [target, { score }])),
  ...extra,
});

function lay(root, { floors, reports = {}, base }) {
  const floorsFile = path.join(root, "floors.json");
  writeFileSync(floorsFile, JSON.stringify(floors));
  for (const [target, name] of Object.entries(reports)) {
    mkdirSync(path.join(root, `mutation-${target}`), { recursive: true });
    writeFileSync(path.join(root, `mutation-${target}`, "mutation-report.json"), fixture(name));
  }
  const baseFile = base ? path.join(root, "base.json") : null;
  if (base) writeFileSync(baseFile, JSON.stringify(base));
  return { floorsFile, baseFile };
}

function command(argv, env = {}) {
  const printed = [];
  const complaints = [];
  const code = main(argv, { env, log: (line) => printed.push(line), error: (line) => complaints.push(line) });
  return { code, out: printed.join("\n"), err: complaints.join("\n") };
}

test("--floors-only judges the file against the base's and prints what was raised, without a report", () => {
  const root = folder();
  const { floorsFile, baseFile } = lay(root, { floors: floorsOf({ alpha: 60 }), base: floorsOf({ alpha: 50 }) });

  const raised = command(["--floors", floorsFile, "--floors-only", "--base-floors", baseFile]);
  assert.equal(raised.code, 0);
  assert.equal(raised.out, '"alpha" floor raised from 50% to 60%.');

  writeFileSync(baseFile, JSON.stringify(floorsOf({ alpha: 70 })));
  const lowered = command(["--floors", floorsFile, "--floors-only", "--base-floors", baseFile]);
  assert.equal(lowered.code, 1);
  assert.match(lowered.err, /^::error::"alpha" floor was lowered from 70% to 60%\./);
});

test("a run whose targets are all at their floors prints the table and the survivors, writes them out, and exits 0", () => {
  const root = folder();
  const { floorsFile } = lay(root, { floors: floorsOf({ alpha: 0, beta: 0 }), reports: { alpha: "dotnet", beta: "web" } });
  const out = path.join(root, "survivors");
  const summary = path.join(root, "summary.md");

  const result = command(["--floors", floorsFile, "--reports", root, "--out", out], { GITHUB_STEP_SUMMARY: summary });

  assert.equal(result.code, 0);
  assert.equal(result.err, "");
  assert.match(result.out, /### Mutation testing/);
  assert.match(result.out, /### Survivors: alpha/);
  assert.match(readFileSync(path.join(out, "survivors.md"), "utf8"), /### Survivors: beta/);
  const survivors = JSON.parse(readFileSync(path.join(out, "survivors.json"), "utf8"));
  assert.deepEqual(Object.keys(survivors), ["alpha", "beta"]);
  assert.ok(survivors.alpha.length > 0);
  assert.match(readFileSync(summary, "utf8"), /### Mutation testing/);
});

test("a target below its floor fails the run, and the survivors are named as annotations, the first ten", () => {
  const root = folder();
  const { floorsFile } = lay(root, { floors: floorsOf({ alpha: 100, beta: 0 }), reports: { beta: "web" } });
  const mutants = Array.from({ length: 13 }, (_, index) => ({ id: String(index), status: "Survived", mutatorName: "Statement", location: { start: { line: index + 1, column: 1 }, end: { line: index + 1, column: 4 } } }));
  mkdirSync(path.join(root, "mutation-alpha"), { recursive: true });
  writeFileSync(path.join(root, "mutation-alpha", "mutation-report.json"), JSON.stringify({ files: { "src/a.ts": { source: "x", mutants: [...mutants, { id: "k", status: "Killed", location: mutants[0].location }] } } }));

  const result = command(["--floors", floorsFile, "--reports", root]);

  assert.equal(result.code, 1);
  const lines = result.err.split("\n");
  assert.match(lines[0], /^::error::"alpha" mutation score is/);
  const annotated = lines.filter((line) => line.startsWith("::error::Survived alpha: "));
  const plain = lines.filter((line) => line.startsWith("Survived alpha: "));
  assert.equal(annotated.length, 10);
  assert.ok(plain.length > 0, "the survivors after the tenth are listed without an annotation");
  assert.ok(!lines.some((line) => line.includes("Survived beta")));
});

test("a missing report, an option without a value and an unknown target for --only are errors, not crashes", () => {
  const root = folder();
  const { floorsFile } = lay(root, { floors: floorsOf({ alpha: 0 }) });

  assert.match(command(["--floors", floorsFile, "--reports", root]).err, /^::error::"alpha": .*mutation-report\.json was not written/m);
  assert.match(command(["--floors", floorsFile]).err, /^::error::--reports <folder> is required, or --floors-only\.$/);
  assert.match(command(["--floors", floorsFile, "--reports", root, "--only", "alpha, gamma"]).err, /^::error::--only names gamma, which has no floor in the floors file\.$/);
  assert.equal(command(["--floors", floorsFile, "--reports", root, "--only", "alpha"]).code, 1);
});

test("run as a command it exits 1 with the error annotation when told nothing", () => {
  const script = fileURLToPath(new URL("./check-mutation-floors.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /::error::--floors <file> is required\./);
});

test("a floors file has to be JSON, name a target, and give each a score and the run it was measured from", () => {
  assert.throws(() => readFloors("{ nope"), /^Error: The floors file is not valid JSON: /);
  assert.throws(() => readFloors("{}"), /"floors" must name at least one target\./);
  const bad = JSON.stringify({ measuredFrom: { runs: { a: 0, b: 7, c: 7 } }, floors: { a: { score: 50 }, b: { score: 101 }, c: {} } });
  assert.throws(() => readFloors(bad), /"measuredFrom\.runs\.a" must be the id of the CI run[\s\S]*"floors\.b\.score" must be a percentage from 0 to 100\.[\s\S]*"floors\.c\.score"/);
  assert.throws(() => readFloors(JSON.stringify({ floors: { a: { score: 5 } } })), /"measuredFrom\.runs\.a"/);
});

test("a floor that rises is noted, one that falls needs its own explanation with the old and new figures, and a new target is not compared", () => {
  const base = floorsOf({ up: 50, down: 80, same: 60, kept: 70 });
  const now = floorsOf({ up: 55, down: 70, same: 60, kept: 65, fresh: 40 });

  const unexplained = loweredFloorProblems(now, base);
  assert.deepEqual(unexplained.notes, ['"up" floor raised from 50% to 55%.']);
  assert.equal(unexplained.problems.length, 2);
  assert.match(unexplained.problems[0], /"down" floor was lowered from 80% to 70%[\s\S]*"down": \{ "from": 80, "to": 70, "reason": "\.\.\." \}/);

  now.lowered = { down: { from: 80, to: 70, reason: " the target lost its slowest tests " }, kept: { from: 75, to: 65, reason: "x" } };
  const explained = loweredFloorProblems(now, base);
  assert.ok(explained.notes.includes('"down" floor LOWERED from 80% to 70%: the target lost its slowest tests'));
  assert.equal(explained.problems.length, 1, "an entry for another starting figure explains nothing");
  assert.match(explained.problems[0], /"kept" floor was lowered from 70% to 65%/);
  assert.deepEqual(loweredFloorProblems(now, {}), { problems: [], notes: [] });
});

test("a score is judged against its floor, with room to rise shown only when there is some", () => {
  const counts = (detected, judged) => ({ detected, judged, survived: judged - detected, noCoverage: 0 });
  const floors = floorsOf({ high: 50, tight: 50, low: 90, gone: 10 });
  const { rows, problems } = evaluate(floors, { high: { counts: counts(80, 100) }, tight: { counts: counts(50, 100) }, low: { counts: counts(80, 100) }, gone: undefined });

  assert.deepEqual(rows.map((row) => [row.target, row.status, row.headroom]), [["high", "ok", 30], ["tight", "ok", 0], ["low", "below floor", -10], ["gone", "no report", undefined]]);
  assert.equal(problems.length, 2);
  assert.match(problems[1], /^"gone": no report\.$/);

  const text = renderSummary({ rows, notes: ["a note"], problems, floors });
  assert.match(text, /measured from CI run 41\./);
  assert.match(text, /\| high \| 80% \| 50% \| 80 of 100 \| 20 \| 0 \| ok, floor can rise \|/);
  assert.match(text, /\| tight \| 50% \| 50% \| 50 of 100 \| 50 \| 0 \| ok \|/);
  assert.match(text, /\| low \| 80% \| 90% \| 80 of 100 \| 20 \| 0 \| BELOW FLOOR \|/);
  assert.match(text, /\| gone \| - \| 10% \| - \| - \| - \| NO REPORT \|/);
  assert.match(text, /\n- a note\n/);

  const many = floorsOf({ a: 1, b: 1 });
  many.measuredFrom.runs = { a: 7, b: 8 };
  assert.match(renderSummary({ rows: [], notes: [], problems: [], floors: many }), /measured from CI runs 7, 8\./);
});

test("a report short of fields is read as far as it goes", () => {
  assert.deepEqual(countMutants({}), { killed: 0, timeout: 0, survived: 0, noCoverage: 0, other: 0, detected: 0, judged: 0 });
  assert.deepEqual(countMutants({ files: { "a.ts": {} } }).judged, 0);
  assert.deepEqual(countMutants({ files: { "a.ts": { mutants: [{ status: "CompileError" }, { status: "Ignored" }, { status: "Killed" }] } } }), { killed: 1, timeout: 0, survived: 0, noCoverage: 0, other: 2, detected: 1, judged: 1 });
  assert.deepEqual(survivorsOf({}), []);
  assert.deepEqual(survivorsOf({ files: { "a.ts": {} } }), []);

  const location = { start: { line: 2, column: 3 }, end: { line: 3, column: 4 } };
  const [survivor] = survivorsOf({
    files: { "src/a.ts": { mutants: [{ status: "NoCoverage", mutatorName: "Block", location, coveredBy: ["t9"] }] } },
    testFiles: { "t.ts": {} },
  });
  assert.deepEqual(survivor, { file: "apps/web/src/a.ts", line: 2, column: 3, mutator: "Block", original: "...", replacement: "", covered: false, tests: ["t9"] });

  const named = survivorsOf({
    files: { "src/a.ts": { source: "one\ntwo\nthree", mutants: [{ status: "Survived", mutatorName: "Block", location, replacement: "{}", coveredBy: ["t1", "t2"] }] } },
    testFiles: { "t.ts": { tests: [{ id: "t1", name: "Suite.Name.First" }] } },
  });
  assert.deepEqual([named[0].original, named[0].replacement, named[0].tests], ["o ...", "{}", ["Suite.Name.First", "t2"]]);
});

test("a snippet is cut from the report's source by line and column, and a position outside it is empty", () => {
  const source = "alpha\r\nbravo charlie\r\ndelta";
  assert.equal(snippetOf(source, { start: { line: 2, column: 7 }, end: { line: 2, column: 14 } }), "charlie");
  assert.equal(snippetOf(source, { start: { line: 9, column: 1 }, end: { line: 9, column: 3 } }), "");
  assert.equal(snippetOf(source, { start: { line: 9, column: 1 }, end: { line: 10, column: 3 } }), " ...");
  assert.equal(snippetOf(source, { start: { line: 2, column: 1 }, end: { line: 3, column: 3 } }), "bravo charlie ...");
});

test("file names are made repository paths whichever runner wrote them", () => {
  assert.equal(repoPath("D:\\a\\Weir\\Weir\\apps\\server\\src\\A.cs"), "apps/server/src/A.cs");
  assert.equal(repoPath("/home/runner/work/Weir/Weir/apps/server/src/A.cs"), "apps/server/src/A.cs");
  assert.equal(repoPath("src/lib/a.ts"), "apps/web/src/lib/a.ts");
  assert.equal(repoPath("/elsewhere/a.ts"), "/elsewhere/a.ts");
  assert.equal(repoPath("C:/elsewhere/a.ts"), "C:/elsewhere/a.ts");
});

test("a list of survivors too long for a job summary is left to the artifact, after the table of files", () => {
  const survivors = Array.from({ length: 6000 }, (_, index) => ({ file: "apps/server/src/Big.cs", line: index + 1, column: 1, mutator: "Statement", original: "x".repeat(60), replacement: "y".repeat(10), covered: true, tests: ["A.B.One", "A.B.Two", "A.B.Three"] }));
  const text = renderSurvivorSummary([{ target: "big", survivors }, { target: "small", survivors: survivors.slice(0, 2) }, { target: "clean", survivors: [] }]);

  assert.match(text, /### Survivors: big\n\n6000 changes no test noticed, in 1 files\./);
  assert.match(text, /\| apps\/server\/src\/Big\.cs \| 6000 \| Statement \(6000\) \|/);
  assert.match(text, /The line-by-line list for big is too long for this page; it is in survivors\.md in the mutation-survivors artifact\./);
  assert.ok(text.length < 100_000);
  assert.match(text, /### Survivors: small/);
  assert.ok(!text.includes("### Survivors: clean"));
});
