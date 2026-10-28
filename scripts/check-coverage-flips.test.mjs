// node --test scripts/check-coverage-flips.test.mjs
// The flip check on small OpenCover reports written to a temporary folder: runs that agree, runs that do not, a run without one
// system's reports, and a run without any.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import { addPoints, compareRuns, formatFlips, main, readRun, sourceOf } from "./check-coverage-flips.mjs";

const script = fileURLToPath(new URL("./check-coverage-flips.mjs", import.meta.url));
const temporary = mkdtempSync(path.join(tmpdir(), "weir-flips-"));
after(() => rmSync(temporary, { recursive: true, force: true }));

let counter = 0;
const newFolder = () => {
  counter += 1;
  const folder = path.join(temporary, `case-${counter}`);
  mkdirSync(folder);
  return folder;
};

const LINUX = "/home/runner/work/Weir/Weir";
const WINDOWS = "D:\\a\\Weir\\Weir";
const SOURCE = "apps/server/src/Weir.Core/Thing.cs";

/** A method with a line (visited or not) and, when `branches` is given, a branch point per visit count. */
const method = (name, { line = 10, lineVisits = 1, branches = [], file = 1 } = {}) => `
<Method><Name>${name}</Name><FileRef uid="${file}" />
<SequencePoints><SequencePoint vc="${lineVisits}" uspid="1" ordinal="0" sl="${line}" sc="1" el="${line}" ec="2" fileid="${file}" /></SequencePoints>
<BranchPoints>${branches.map((visits, path) => `<BranchPoint vc="${visits}" uspid="1" ordinal="${path}" path="${path}" offset="7" offsetend="9" sl="${line}" fileid="${file}" />`).join("")}</BranchPoints>
</Method>`;

/** An OpenCover report of one module whose files are `files` (uid to full path) and whose methods are `methods`. */
const report = (files, methods) => `<?xml version="1.0" encoding="utf-8"?>
<CoverageSession><Modules><Module hash="A"><ModuleName>Weir.Core</ModuleName>
<Files>${Object.entries(files).map(([uid, fullPath]) => `<File uid="${uid}" fullPath="${fullPath}" />`).join("")}</Files>
<Classes><Class><Methods>${methods.join("")}</Methods></Class></Classes></Module></Modules></CoverageSession>`;

/** Writes the report a project's tests left, in the layout of the downloaded artifact. */
function leave(run, artifact, text, project = "Weir.Core.Tests") {
  const folder = path.join(run, artifact, project, "TestResults", "0f1e");
  mkdirSync(folder, { recursive: true });
  writeFileSync(path.join(folder, "coverage.opencover.xml"), text);
}

/** One run with both systems' reports, each made from the same `methods` with the Windows one's paths written as Windows writes them. */
function run(folder, id, methods, { linux = methods, windows = methods } = {}) {
  const dir = path.join(folder, String(id));
  mkdirSync(dir, { recursive: true });
  leave(dir, "coverage-server-linux", report({ 1: `${LINUX}/${SOURCE}` }, linux));
  leave(dir, "coverage-server-windows", report({ 1: `${WINDOWS}\\${SOURCE.replace(/\//g, "\\")}` }, windows));
  return dir;
}

const steady = [method("System.Void Thing::Steady()", { branches: [3, 2] })];

test("a source file is the shipped one under apps/server/src, however the system writes its path", () => {
  assert.equal(sourceOf(`${LINUX}/${SOURCE}`), SOURCE);
  assert.equal(sourceOf(`${WINDOWS}\\apps\\server\\src\\Weir.Core\\Thing.cs`), SOURCE);
  assert.equal(sourceOf(`${LINUX}/apps/server/tests/Weir.Core.Tests/Thing.cs`), undefined);
  assert.equal(sourceOf(`${LINUX}/apps/server/src/Weir.Core/obj/Debug/Generated.cs`), undefined);
  assert.equal(sourceOf("/somewhere/else/Thing.cs"), undefined);
});

test("lines and branches are keyed by where they are, and a point any report reached stays reached", () => {
  const first = addPoints(report({ 1: `${LINUX}/${SOURCE}` }, [method("M()", { lineVisits: 0, branches: [0, 4] })]));
  assert.equal(first.size, 3);
  const second = addPoints(report({ 1: `${LINUX}/${SOURCE}` }, [method("M()", { lineVisits: 5, branches: [0, 0] })]), first);
  const byKind = Object.fromEntries([...second.values()].map((point) => [`${point.branch ? "branch" : "line"}${point.path ?? ""}`, point.visited]));
  assert.deepEqual(byKind, { line: true, branch0: false, branch1: true });
  // A later report that did not reach what an earlier one did leaves it reached.
  addPoints(report({ 1: `${LINUX}/${SOURCE}` }, [method("M()", { lineVisits: 0, branches: [0, 0] })]), second);
  assert.equal([...second.values()].filter((point) => point.visited).length, 2);
});

test("points of tests and of generated code are left out, a module has its own file numbers, and names are read as text", () => {
  const text = `<CoverageSession><Modules>
<Module hash="A"><Files><File uid="1" fullPath="${LINUX}/${SOURCE}" /><File uid="2" fullPath="${LINUX}/apps/server/tests/T.cs" /></Files><Classes><Class><Methods>
${method("System.Void A&lt;T&gt;::Go()", { file: 1 })}${method("Test::Run()", { file: 2 })}${method("Lost::Run()", { file: 9 })}
</Methods></Class></Classes></Module>
<Module hash="B"><Files><File uid="2" fullPath="${LINUX}/apps/server/src/Weir.Api/Other.cs" /></Files><Classes><Class><Methods>
${method("Other::Go()", { file: 2 })}
</Methods></Class></Classes></Module></Modules></CoverageSession>`;
  const points = [...addPoints(text).values()];
  assert.deepEqual(points.map((point) => [point.file, point.method]), [
    [SOURCE, "System.Void A<T>::Go()"],
    ["apps/server/src/Weir.Api/Other.cs", "Other::Go()"],
  ]);
});

test("runs that reach the same places are steady, whether the branches are reached by one system or the other", () => {
  const folder = newFolder();
  const split = [method("System.Void Thing::Split()", { branches: [1, 0] })];
  const other = [method("System.Void Thing::Split()", { branches: [0, 1] })];
  for (const id of ["40000000001", "40000000002", "40000000003"]) run(folder, id, steady.concat(split), { linux: steady.concat(split), windows: steady.concat(other) });
  const result = compareRuns(folder);
  assert.deepEqual(result.compared, ["40000000001", "40000000002", "40000000003"]);
  assert.deepEqual(result.flips, []);
  const lines = [];
  assert.equal(main([folder], { log: (line) => lines.push(line), error: assert.fail }), 0);
  assert.deepEqual(lines, ["Coverage is steady: no line or branch in the server was reached in only some of 3 runs."]);
});

test("a branch and a line reached in some runs and not others are listed with how many runs reached them and which did not", () => {
  const folder = newFolder();
  const flaky = (reached) => [method("System.Void Thing::Flaky()", { line: 41, lineVisits: reached ? 1 : 0, branches: [4, reached ? 2 : 0] })];
  run(folder, "40000000001", steady.concat(flaky(true)));
  run(folder, "40000000002", steady.concat(flaky(false)));
  run(folder, "40000000003", steady.concat(flaky(true)));
  const lines = [];
  assert.equal(main([folder], { log: (line) => lines.push(line), error: assert.fail }), 1);
  const text = lines.join("\n");
  assert.match(text, /^Coverage is not steady: 2 line\(s\) and branch\(es\) in 1 place\(s\) in the server were reached in some CI runs and not in others \(3 runs compared\)\.$/m);
  assert.match(text, new RegExp(`^${SOURCE.replace(/\./g, "\\.")}$`, "m"));
  assert.match(text, /^ {2}System\.Void Thing::Flaky\(\)$/m);
  assert.match(text, /^ {4}line 41: reached in 2 of 3 runs, not in 40000000002$/m);
  assert.match(text, /^ {4}branch at line 41 \(offset 7, path 1\): reached in 2 of 3 runs, not in 40000000002$/m);
  assert.doesNotMatch(text, /Steady/);
  assert.match(text, /^What to do: .*test reaches only when timing/m);
  assert.match(text, /Write a test that reaches each one every time/);
  assert.doesNotMatch(text, /Left out/);
});

test("only the first runs that missed a point are named, flips are grouped by file and method, and a point is compared only across the runs that list it", () => {
  const folder = newFolder();
  const second = "apps/server/src/Weir.Api/Second.cs";
  const files = (runFiles) => ({ 1: `${LINUX}/${SOURCE}`, 2: `${LINUX}/${second}`, ...runFiles });
  for (let index = 1; index <= 8; index += 1) {
    const dir = path.join(folder, String(40000000000 + index));
    mkdirSync(dir);
    const missedOften = method("System.Void Thing::Often()", { branches: [index === 8 ? 1 : 0, 1] });
    const hereOnly = index === 1 ? [method("System.Void Thing::OnlyHere()", { lineVisits: 1 })] : [];
    const neverReached = method("System.Void Thing::Never()", { line: 3, lineVisits: 0 });
    const inSecond = method("System.Void Second::First()", { line: 5, lineVisits: index === 1 ? 1 : 0, file: 2 });
    const inSecondToo = method("System.Void Second::Second()", { line: 6, lineVisits: index === 2 ? 0 : 1, file: 2 });
    leave(dir, "coverage-server-linux", report(files({}), [missedOften, neverReached, inSecond, inSecondToo, ...hereOnly]));
    leave(dir, "coverage-server-windows", report(files({}), [neverReached]));
  }
  const text = formatFlips(compareRuns(folder));
  assert.match(text, /branch at line 10 \(offset 7, path 0\): reached in 1 of 8 runs, not in 40000000001, 40000000002, 40000000003, 40000000004, 40000000005 and 2 more$/m);
  assert.match(text, /line 5: reached in 1 of 8 runs, not in 40000000002, 40000000003, 40000000004, 40000000005, 40000000006 and 2 more$/m);
  assert.match(text, /line 6: reached in 7 of 8 runs, not in 40000000002$/m);
  assert.doesNotMatch(text, /OnlyHere|Never/);
  // The two files are listed once each, in order, and the three flips sit under their methods.
  const order = text.split("\n").filter((line) => /^apps\/|^ {2}System/.test(line));
  assert.deepEqual(order, [
    "apps/server/src/Weir.Api/Second.cs",
    "  System.Void Second::First()",
    "  System.Void Second::Second()",
    `${SOURCE}`,
    "  System.Void Thing::Often()",
  ]);
  assert.match(text, /^Coverage is not steady: 3 line\(s\) and branch\(es\) in 3 place\(s\)/);
});

test("a run without one system's reports is left out and named, and the others are still compared", () => {
  const folder = newFolder();
  const flaky = (reached) => [method("System.Void Thing::Flaky()", { lineVisits: reached ? 1 : 0 })];
  run(folder, "40000000001", flaky(true));
  run(folder, "40000000002", flaky(false));
  // This one's Windows leg did not run: the Windows-only line would look like a flip if it were compared.
  const dir = path.join(folder, "40000000003");
  mkdirSync(dir);
  leave(dir, "coverage-server-linux", report({ 1: `${LINUX}/${SOURCE}` }, flaky(false)));
  // This one's Linux folder is there and holds nothing.
  const empty = path.join(folder, "40000000004");
  mkdirSync(path.join(empty, "coverage-server-linux"), { recursive: true });
  leave(empty, "coverage-server-windows", report({ 1: `${WINDOWS}\\${SOURCE.replace(/\//g, "\\")}` }, flaky(true)));
  // And a file that is not a run.
  writeFileSync(path.join(folder, "notes.txt"), "not a run");

  const result = compareRuns(folder);
  assert.deepEqual(result.compared, ["40000000001", "40000000002"]);
  assert.deepEqual(result.left, [
    { run: "40000000003", missing: ["Windows"] },
    { run: "40000000004", missing: ["Linux"] },
  ]);
  const text = formatFlips(result);
  assert.match(text, /\(2 runs compared\)/);
  assert.match(text, /^Left out, because a system's coverage is missing from the run: 40000000003 \(no Windows report\), 40000000004 \(no Linux report\)\.$/m);
});

test("a run with no reports at all is left out, and when fewer than two runs remain there is nothing to compare", () => {
  const folder = newFolder();
  run(folder, "40000000001", steady);
  mkdirSync(path.join(folder, "40000000002"));
  const lines = [];
  assert.equal(main([folder], { log: (line) => lines.push(line), error: assert.fail }), 0);
  assert.deepEqual(lines, [
    "Nothing to compare: 1 run(s) have both the Linux and the Windows server coverage, and two are needed. Left out, because a system's coverage is missing from the run: 40000000002 (no Linux or Windows report).",
  ]);
  assert.deepEqual(readRun(path.join(folder, "40000000002")).missing, ["Linux", "Windows"]);

  const none = newFolder();
  const noRuns = [];
  assert.equal(main([none], { log: (line) => noRuns.push(line), error: assert.fail }), 0);
  assert.match(noRuns[0], /^Nothing to compare: 0 run\(s\)/);
});

test("steady runs say so beside the runs they left out", () => {
  const folder = newFolder();
  run(folder, "40000000001", steady);
  run(folder, "40000000002", steady);
  mkdirSync(path.join(folder, "40000000003"));
  const lines = [];
  assert.equal(main([folder], { log: (line) => lines.push(line), error: assert.fail }), 0);
  assert.equal(lines[0], "Coverage is steady: no line or branch in the server was reached in only some of 2 runs. Left out, because a system's coverage is missing from the run: 40000000003 (no Linux or Windows report).");
});

test("a folder that is missing, or none given, is told what to do", () => {
  const errors = [];
  assert.equal(main([], { log: assert.fail, error: (line) => errors.push(line) }), 2);
  assert.equal(main([path.join(temporary, "nowhere")], { log: assert.fail, error: (line) => errors.push(line) }), 2);
  assert.equal(errors[0], "Usage: check-coverage-flips.mjs <folder of runs>");
  assert.match(errors[1], /nowhere\. Download the runs first: node scripts\/fetch-coverage-runs\.mjs /);
});

test("run as a command, it prints the places and exits 1, and exits 0 when the runs agree", () => {
  const folder = newFolder();
  run(folder, "40000000001", [method("System.Void Thing::Flaky()", { lineVisits: 1 })]);
  run(folder, "40000000002", [method("System.Void Thing::Flaky()", { lineVisits: 0 })]);
  const flipping = spawnSync(process.execPath, [script, folder], { encoding: "utf8" });
  assert.equal(flipping.status, 1);
  assert.match(flipping.stdout, /^Coverage is not steady: 1 line\(s\) and branch\(es\) in 1 place\(s\)/);

  const agreeing = newFolder();
  run(agreeing, "40000000001", steady);
  run(agreeing, "40000000002", steady);
  const result = spawnSync(process.execPath, [script, agreeing], { encoding: "utf8" });
  assert.equal(result.status, 0);
  assert.match(result.stdout, /^Coverage is steady/);

  // A path that is a file, not a folder, is the check failing, which is not the same as places flipping.
  const unreadable = spawnSync(process.execPath, [script, path.join(agreeing, "40000000001", "coverage-server-linux", "Weir.Core.Tests", "TestResults", "0f1e", "coverage.opencover.xml")], { encoding: "utf8" });
  assert.equal(unreadable.status, 2);
  assert.match(unreadable.stderr, /ENOTDIR/);
});
