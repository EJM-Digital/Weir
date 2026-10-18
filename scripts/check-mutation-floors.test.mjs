// node --test scripts/check-mutation-floors.test.mjs
// The mutation ratchet, read against reports Stryker.NET and StrykerJS wrote (scripts/fixtures/mutation), and the
// matrix of the Mutation workflow against the floors file and the configs on disk.
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  checkFloorsFile,
  clustersOf,
  countMutants,
  evaluate,
  failingSurvivors,
  loadTarget,
  loweredFloorProblems,
  onlyTargets,
  parseArguments,
  readFloors,
  renderSurvivorSummary,
  repoPath,
  run,
  scoreTenth,
  snippetOf,
  survivorsOf,
} from "./check-mutation-floors.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const fixtures = path.join(here, "fixtures", "mutation");
const readFixture = (...parts) => readFileSync(path.join(fixtures, ...parts), "utf8");
const reportOf = (name) => JSON.parse(readFixture(name, "mutation-report.json"));

function floorsFile({ runs, floors = {}, lowered, targets = ["alpha", "beta"], base = 50 } = {}) {
  const all = {};
  const measured = {};
  for (const target of targets) {
    all[target] = { score: floors[target] ?? base };
    measured[target] = runs?.[target] ?? 123;
  }
  return { measuredFrom: { runs: measured }, floors: all, ...(lowered ? { lowered } : {}) };
}

const counts = (detected, survived, noCoverage = 0) => ({ killed: detected, timeout: 0, survived, noCoverage, other: 0, detected, judged: detected + survived + noCoverage });

test("a floor is the score rounded down to one decimal", () => {
  assert.equal(scoreTenth(counts(9174, 826)), 91.7);
  assert.equal(scoreTenth(counts(9169, 831)), 91.6);
  assert.equal(scoreTenth(counts(1, 2)), 33.3);
  assert.equal(scoreTenth(counts(7, 0)), 100);
  assert.equal(scoreTenth(counts(0, 0)), 100, "nothing to judge is not a failure");
});

test("a floors file names a floor and the run it was measured from for every target", () => {
  assert.doesNotThrow(() => readFloors(JSON.stringify(floorsFile())));
  const noRun = floorsFile();
  delete noRun.measuredFrom.runs.beta;
  assert.throws(() => readFloors(JSON.stringify(noRun)), /measuredFrom\.runs\.beta/);
  assert.throws(() => readFloors(JSON.stringify(floorsFile({ floors: { alpha: 101 } }))), /floors\.alpha\.score/);
  assert.throws(() => readFloors(JSON.stringify({ measuredFrom: { runs: {} }, floors: {} })), /at least one target/);
  assert.throws(() => readFloors("{"), /not valid JSON/);
});

test("the shipped floors file is usable and names the targets the workflow runs", () => {
  const floors = readFloors(readFileSync(path.join(root, "docs", "coverage", "mutation-floors.json"), "utf8"));
  const matrix = JSON.parse(readFileSync(path.join(root, ".github", "mutation-targets.json"), "utf8"));
  assert.deepEqual(matrix.map((entry) => entry.target).sort(), Object.keys(floors.floors).sort());
  for (const { target, directory, config } of matrix) {
    assert.ok(existsSync(path.join(root, directory, config)), `${target}: ${config} from ${directory}`);
  }
});

test("a pull request that mutated some targets is held to the floors of just those", () => {
  const floors = floorsFile({ floors: { alpha: 50, beta: 60 } });
  assert.deepEqual(Object.keys(onlyTargets(floors, ["beta"]).floors), ["beta"]);
  assert.throws(() => onlyTargets(floors, ["gamma"]), /--only names gamma, which has no floor/);
  assert.deepEqual(parseArguments(["--floors", "f.json", "--reports", "r", "--only", "alpha, beta"]).only, ["alpha", "beta"]);
});

test("the parts of SafeSwap.cs that are mutated as separate targets leave no character out", () => {
  const spans = ["preflight", "prepare", "commit"].flatMap((part) => {
    const config = JSON.parse(readFileSync(path.join(root, "apps", "server", "mutation", `file-lifecycle-swap-${part}`, "stryker-config.json"), "utf8"));
    return config["stryker-config"].mutate.flatMap((pattern) => {
      const match = pattern.match(/SafeSwap\.cs\{(\d+)\.\.(\d+)\}$/);
      return match ? [[Number(match[1]), Number(match[2])]] : [];
    });
  });
  const length = readFileSync(path.join(root, "apps", "server", "src", "Weir.Infrastructure", "LibraryMode", "SafeSwap.cs"), "utf8").length;
  assert.equal(spans.length, 3);
  assert.equal(spans[0][0], 0);
  assert.equal(spans[1][0], spans[0][1] + 1, "the second part starts where the first ends");
  assert.equal(spans[2][0], spans[1][1] + 1, "the third part starts where the second ends");
  assert.ok(spans[2][1] >= length, "the last part runs to the end of the file");
});

test("a score at its floor passes and one below it fails, naming the target and the figures", () => {
  const floors = floorsFile({ floors: { alpha: 91.7 } });
  assert.deepEqual(evaluate(floors, { alpha: { counts: counts(917, 83) }, beta: { counts: counts(5, 5) } }).problems, []);
  const below = evaluate(floors, { alpha: { counts: counts(916, 84) }, beta: { counts: counts(5, 5) } });
  assert.equal(below.problems.length, 1);
  assert.match(below.problems[0], /"alpha" mutation score is 91\.6% \(916 of 1000 changes caught\), below its floor of 91\.7%/);
  assert.equal(below.rows.find((row) => row.target === "alpha").status, "below floor");
});

test("a target with no report fails, so a matrix job that wrote nothing is never read as clean", () => {
  const result = evaluate(floorsFile(), { alpha: { counts: counts(90, 10) }, beta: { missing: "mutation-report.json was not written" } });
  assert.deepEqual(result.problems, ['"beta": mutation-report.json was not written']);
  assert.deepEqual(evaluate(floorsFile(), {}).problems.length, 2);
});

test("a floor lowered without its own entry fails, naming the target and the figures", () => {
  const base = floorsFile({ floors: { alpha: 80 } });
  const { problems } = loweredFloorProblems(floorsFile({ floors: { alpha: 70 } }), base);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /"alpha" floor was lowered from 80% to 70%/);
  assert.match(problems[0], /pull request of its own/);
});

test("a lowered floor with an entry that names both figures and a reason passes, and says so", () => {
  const base = floorsFile({ floors: { alpha: 80 } });
  const lowered = floorsFile({ floors: { alpha: 70 }, lowered: { alpha: { from: 80, to: 70, reason: "SwapJournal.cs was split." } } });
  const result = loweredFloorProblems(lowered, base);
  assert.deepEqual(result.problems, []);
  assert.match(result.notes.join("\n"), /"alpha" floor LOWERED from 80% to 70%: SwapJournal\.cs was split\./);
});

test("a lowered entry that does not match the change, or gives no reason, does not excuse it", () => {
  const base = floorsFile({ floors: { alpha: 80 } });
  for (const entry of [{ from: 80, to: 69, reason: "x" }, { from: 85, to: 70, reason: "x" }, { from: 80, to: 70, reason: "  " }, { from: 80, to: 70 }]) {
    const lowered = floorsFile({ floors: { alpha: 70 }, lowered: { alpha: entry } });
    assert.equal(loweredFloorProblems(lowered, base).problems.length, 1, JSON.stringify(entry));
  }
});

test("a floor that rises or stays is never a problem, and a rise is noted", () => {
  const base = floorsFile({ floors: { alpha: 80 } });
  const result = loweredFloorProblems(floorsFile({ floors: { alpha: 82.5 } }), base);
  assert.deepEqual(result.problems, []);
  assert.match(result.notes.join("\n"), /"alpha" floor raised from 80% to 82\.5%/);
  assert.deepEqual(loweredFloorProblems(base, base), { problems: [], notes: [] });
});

test("file names read the same whichever folder or slash a tool wrote", () => {
  assert.equal(repoPath("/home/runner/work/Weir/Weir/apps/server/src/Weir.Core/LibraryMode/SafeSwapRules.cs"), "apps/server/src/Weir.Core/LibraryMode/SafeSwapRules.cs");
  assert.equal(repoPath("D:\\a\\Weir\\Weir\\apps\\tray\\Weir.Tray\\StartWatch.cs"), "apps/tray/Weir.Tray/StartWatch.cs");
  assert.equal(repoPath("src/lib/api/client.ts"), "apps/web/src/lib/api/client.ts");
});

test("the source of a change is read from its 1-based position", () => {
  const source = "if (a == b)\n    return 1;\n";
  assert.equal(snippetOf(source, { start: { line: 1, column: 5 }, end: { line: 1, column: 11 } }), "a == b");
  assert.equal(snippetOf(source, { start: { line: 1, column: 1 }, end: { line: 2, column: 13 } }), "if (a == b) ...");
});

test("Stryker.NET's report: every change is counted by outcome and the score leaves out what could not be judged", () => {
  const report = reportOf("dotnet");
  const result = countMutants(report);
  assert.equal(result.judged, result.killed + result.timeout + result.survived + result.noCoverage);
  assert.ok(result.other > 0, "ignored changes are in the report and are not judged");
  assert.ok(result.detected > 0);
});

test("Stryker.NET's survivors name the file from the repository root, the line, the mutator, the code and the tests that ran it", () => {
  const survivors = survivorsOf(reportOf("dotnet"));
  assert.ok(survivors.length > 0);
  for (const survivor of survivors) {
    assert.match(survivor.file, /^apps\/server\/src\//);
    assert.ok(survivor.line > 0 && survivor.mutator);
    assert.ok(survivor.original.length > 0, `${survivor.file}:${survivor.line} shows the code it changed`);
  }
  const ordered = [...survivors].sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line || a.column - b.column);
  assert.deepEqual(survivors, ordered);
  const unrun = survivors.filter((survivor) => !survivor.covered);
  assert.ok(unrun.every((survivor) => survivor.tests.length === 0), "a line no test runs has no tests to name");
});

test("StrykerJS's report reads the same way, with paths from apps/web", () => {
  const report = reportOf("web");
  const survivors = survivorsOf(report);
  assert.ok(countMutants(report).judged > 0);
  assert.ok(survivors.every((survivor) => survivor.file.startsWith("apps/web/src/")));
});

test("survivors are clustered by file, the file with the most first", () => {
  const survivors = [
    { file: "b.cs", mutator: "Statement", line: 1 },
    { file: "a.cs", mutator: "Statement", line: 2 },
    { file: "a.cs", mutator: "Statement", line: 3 },
    { file: "a.cs", mutator: "Equality", line: 4 },
  ];
  const clusters = clustersOf(survivors);
  assert.deepEqual(clusters.map((cluster) => [cluster.file, cluster.survivors.length]), [["a.cs", 3], ["b.cs", 1]]);
  assert.equal(clusters[0].mutators.get("Statement"), 2);
});

test("the survivors page lists each cluster and keeps the line-by-line list within the page's budget", () => {
  const survivor = (file, line) => ({ file, line, column: 1, mutator: "Equality mutation", original: "a == b", replacement: "a != b", covered: true, tests: ["N.T.Test_one", "N.T.Test_two", "N.T.Test_three"] });
  const text = renderSurvivorSummary([{ target: "alpha", survivors: [survivor("a.cs", 3), survivor("a.cs", 9), survivor("b.cs", 1)] }, { target: "beta", survivors: [] }]);
  assert.match(text, /### Survivors: alpha/);
  assert.match(text, /\| a\.cs \| 2 \| Equality mutation \(2\) \|/);
  assert.match(text, /\| 3 \| Equality mutation \| `a == b` to `a != b` \| T\.Test_one; T\.Test_two; and 1 more \|/);
  assert.doesNotMatch(text, /beta/, "a target with no survivors has no section");
});

test("a pipe or a backtick in the code never breaks the table", () => {
  const text = renderSurvivorSummary([
    { target: "alpha", survivors: [{ file: "a.cs", line: 1, column: 1, mutator: "Logical", original: "a || `b`", replacement: "a && b", covered: false, tests: [] }] },
  ]);
  assert.match(text, /`a \\\|\\\| 'b'` to `a && b` \| none run this line \|/);
});

// run() against report folders written to disk, the way CI lays them out.
function withReports(callback) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-mutation-"));
  try {
    const put = (target, name) => {
      mkdirSync(path.join(folder, `mutation-${target}`), { recursive: true });
      writeFileSync(path.join(folder, `mutation-${target}`, "mutation-report.json"), readFixture(name, "mutation-report.json"));
    };
    callback(folder, put);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

test("run judges each target's own report against its own floor and lists the survivors of every target", () => {
  withReports((folder, put) => {
    put("alpha", "dotnet");
    put("beta", "web");
    const floorsPath = path.join(folder, "floors.json");
    writeFileSync(floorsPath, JSON.stringify(floorsFile({ floors: { alpha: 0, beta: 0 } })));
    const result = run({ floorsFile: floorsPath, reportsFolder: folder });
    assert.deepEqual(result.problems, []);
    assert.match(result.text, /\| alpha \| \d+(\.\d)?% \| 0% \|/);
    assert.match(result.survivorText, /### Survivors: alpha/);
    assert.match(result.survivorText, /### Survivors: beta/);

    writeFileSync(floorsPath, JSON.stringify(floorsFile({ floors: { alpha: 100, beta: 0 } })));
    const failed = run({ floorsFile: floorsPath, reportsFolder: folder });
    assert.equal(failed.problems.length, 1);
    assert.match(failed.problems[0], /"alpha" mutation score is/);
    const failing = failingSurvivors(failed.rows, failed.targets);
    assert.deepEqual(failing.map((item) => item.target), ["alpha"]);
    assert.ok(failing[0].survivors.length > 0, "the failing target's survivors are the ones to name");
  });
});

test("a target whose report folder is missing fails the run", () => {
  withReports((folder, put) => {
    put("alpha", "dotnet");
    const floorsPath = path.join(folder, "floors.json");
    writeFileSync(floorsPath, JSON.stringify(floorsFile({ floors: { alpha: 0, beta: 0 } })));
    const result = run({ floorsFile: floorsPath, reportsFolder: folder });
    assert.equal(result.problems.length, 1);
    assert.match(result.problems[0], /"beta": .*mutation-report\.json was not written/);
    assert.match(result.text, /\| beta \| - \| 0% \| - \| - \| - \| NO REPORT \|/);
  });
});

test("a report with nothing to judge fails rather than scoring 100", () => {
  withReports((folder) => {
    mkdirSync(path.join(folder, "mutation-alpha"), { recursive: true });
    writeFileSync(path.join(folder, "mutation-alpha", "mutation-report.json"), JSON.stringify({ schemaVersion: "2", files: {} }));
    assert.match(loadTarget("alpha", folder).result.missing, /nothing/);
  });
});

test("a report where most changes timed out fails rather than scoring them all as caught", () => {
  withReports((folder) => {
    mkdirSync(path.join(folder, "mutation-alpha"), { recursive: true });
    const mutants = Array.from({ length: 8 }, (_, id) => ({ id: String(id), mutatorName: "Statement", status: "Timeout", location: { start: { line: 1, column: 1 }, end: { line: 1, column: 2 } } }));
    writeFileSync(path.join(folder, "mutation-alpha", "mutation-report.json"), JSON.stringify({ schemaVersion: "1.0", files: { "src/a.ts": { language: "typescript", source: "x", mutants } } }));
    assert.match(loadTarget("alpha", folder).result.missing, /8 of 8 changes ended by timing out/);
  });
});

test("the floors can be checked against the base branch's without any report", () => {
  withReports((folder) => {
    const floorsPath = path.join(folder, "floors.json");
    const basePath = path.join(folder, "base.json");
    writeFileSync(basePath, JSON.stringify(floorsFile({ floors: { alpha: 80 } })));
    writeFileSync(floorsPath, JSON.stringify(floorsFile({ floors: { alpha: 70 } })));
    assert.equal(checkFloorsFile({ floorsFile: floorsPath, baseFloorsFile: basePath }).problems.length, 1);
    assert.deepEqual(checkFloorsFile({ floorsFile: floorsPath, baseFloorsFile: path.join(folder, "none.json") }).problems, []);
  });
});

test("arguments need a floors file and either the reports folder or --floors-only", () => {
  assert.deepEqual(parseArguments(["--floors", "f.json", "--reports", "r", "--out", "o", "--base-floors", "b.json"]), {
    floorsFile: "f.json",
    reportsFolder: "r",
    outFolder: "o",
    baseFloorsFile: "b.json",
  });
  assert.deepEqual(parseArguments(["--floors", "f.json", "--floors-only", "--base-floors", "b.json"]), { floorsFile: "f.json", floorsOnly: true, baseFloorsFile: "b.json" });
  assert.throws(() => parseArguments(["--reports", "r"]), /--floors/);
  assert.throws(() => parseArguments(["--floors", "f.json"]), /--reports/);
  assert.throws(() => parseArguments(["--floors", "f.json", "--nope", "x"]), /Unknown option --nope/);
  assert.throws(() => parseArguments(["--floors"]), /needs a value/);
});
