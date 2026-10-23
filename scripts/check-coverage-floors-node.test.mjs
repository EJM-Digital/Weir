// node --test scripts/check-coverage-floors-node.test.mjs
// The Node scripts' share of check-coverage-floors.mjs (#1060): node's lcov report, the scripts no test loaded, and the command line.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  PROJECTS,
  codeLines,
  excludedScriptGlobs,
  filesFromLcov,
  isExcludedScript,
  loadFigures,
  nodeScriptFiles,
  parseArguments,
  run,
  scriptFiles,
  sourceName,
} from "./check-coverage-floors.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-coverage-floors.mjs");
const nodeProject = PROJECTS.find((project) => project.id === "node-scripts");

const LCOV = [
  "TN:",
  "SF:scripts\\a.mjs",
  "DA:1,1",
  "LF:10",
  "LH:9",
  "BRF:4",
  "BRH:3",
  "end_of_record",
  "TN:",
  "SF:scripts/sub/b.mjs",
  "LF:5",
  "LH:5",
  "BRF:0",
  "BRH:0",
  "end_of_record",
  "",
].join("\r\n");

function withRepository(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-node-coverage-"));
  try {
    const put = (file, text) => {
      mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
      writeFileSync(path.join(root, file), text);
    };
    callback({ root, put });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const floorsText = (line, branch) => {
  const floors = {};
  for (const project of PROJECTS) floors[project.id] = { line: 0, branch: 0 };
  floors["node-scripts"] = { line, branch };
  return JSON.stringify({ measuredFrom: { run: 1 }, floors });
};

test("node's lcov report gives each file's lines and branches, whichever slash and line ending it was written with", () => {
  assert.deepEqual(filesFromLcov(LCOV), [
    { filename: "scripts/a.mjs", line: { covered: 9, total: 10 }, branch: { covered: 3, total: 4 } },
    { filename: "scripts/sub/b.mjs", line: { covered: 5, total: 5 }, branch: { covered: 0, total: 0 } },
  ]);
  assert.deepEqual(filesFromLcov(""), []);
});

test("a script is left out when one of the listed globs names it", () => {
  const globs = ["scripts/scenarios-ci/tray-*.mjs", "scripts/docker-smoke/**"];
  assert.equal(isExcludedScript("scripts/scenarios-ci/tray-menu.mjs", globs), true);
  assert.equal(isExcludedScript("scripts/docker-smoke/deep/stack.mjs", globs), true);
  assert.equal(isExcludedScript("scripts/scenarios-ci/homes.mjs", globs), false);
  assert.equal(isExcludedScript("scripts/a.mjs", []), false);
});

test("only the exclusions that name a scripts path leave scripts out of the figure", () => {
  const text = JSON.stringify({
    exclusions: [
      { id: "one", marker: "scripts/docker-smoke/**" },
      { id: "two", marker: "[Weir.*Tests]*" },
      { id: "three" },
    ],
  });
  assert.deepEqual(excludedScriptGlobs(text), ["scripts/docker-smoke/**"]);
  assert.deepEqual(excludedScriptGlobs("{}"), []);
});

test("code lines are the lines that are not blank and not comments, block comments included", () => {
  const text = ["#!/usr/bin/env node", "// a comment", "", "/* one line */", "/*", " * inside", " */", "const a = 1; // trailing", "  ", "export { a };"].join("\n");
  assert.equal(codeLines(text), 3);
  assert.equal(codeLines(""), 0);
});

test("the scripts are every .mjs file under scripts that is not a test or a fixture", () => {
  withRepository(({ root, put }) => {
    put("scripts/a.mjs", "");
    put("scripts/a.test.mjs", "");
    put("scripts/readme.md", "");
    put("scripts/sub/b.mjs", "");
    put("scripts/fixtures/c.mjs", "");
    assert.deepEqual(scriptFiles(root).sort(), ["scripts/a.mjs", "scripts/sub/b.mjs"]);
  });
});

test("a script no test loaded counts as not run, and an excluded one is left out whether node reported it or not", () => {
  const lcov = filesFromLcov(LCOV);
  const files = nodeScriptFiles(lcov, ["scripts/a.mjs", "scripts/sub/b.mjs", "scripts/never.mjs", "scripts/skipped.mjs"], ["scripts/skipped.mjs", "scripts/sub/*"], () => 7);
  assert.deepEqual(
    files.map((file) => [file.filename, file.line.covered, file.line.total, file.branch.total]),
    [
      ["scripts/a.mjs", 9, 10, 4],
      ["scripts/never.mjs", 0, 7, 0],
    ],
  );
});

test("a report folder with no lcov file fails the Node scripts, and says which file was not written", () => {
  withRepository(({ root }) => {
    const loaded = loadFigures(nodeProject, { "node-scripts": root });
    assert.match(loaded.figures.missing, /lcov\.info was not written/);
  });
});

test("a run over a node report is judged against the node-scripts floor, with the exclusions file beside the floors", () => {
  withRepository(({ root, put }) => {
    put("scripts/a.mjs", "export const a = 1;\n");
    put("scripts/never.mjs", "export const never = 1;\nexport const more = 2;\n");
    put("scripts/skipped.mjs", "export const skipped = 1;\n");
    put("coverage/lcov.info", "SF:scripts/a.mjs\nLF:1\nLH:1\nBRF:2\nBRH:1\nend_of_record\n");
    put("docs/coverage/exclusions.json", JSON.stringify({ exclusions: [{ id: "x", marker: "scripts/skipped.mjs" }] }));
    put("docs/coverage/floors.json", floorsText(33.3, 50));
    const options = { floorsFile: path.join(root, "docs/coverage/floors.json"), folders: { "node-scripts": path.join(root, "coverage") }, scriptsRoot: root };
    const result = run(options);
    assert.deepEqual(result.problems, []);
    assert.match(result.text, /\| Node scripts \| 33\.3% \(1\/3\) \| 33\.3% \| 50% \(1\/2\) \| 50% \|/);
    assert.match(result.text, /\| scripts\/never\.mjs \| 2 \| 0 \| 0% \|/);
    writeFileSync(options.floorsFile, floorsText(33.4, 50));
    assert.match(run(options).problems[0], /Node scripts line coverage is 33\.3% \(1 of 3\), below its floor of 33\.4%/);
  });
});

test("without an exclusions file nothing is left out", () => {
  withRepository(({ root, put }) => {
    put("scripts/a.mjs", "export const a = 1;\n");
    put("coverage/lcov.info", "SF:scripts/a.mjs\nLF:1\nLH:1\nend_of_record\n");
    put("floors.json", floorsText(100, 100));
    const result = run({ floorsFile: path.join(root, "floors.json"), folders: { "node-scripts": path.join(root, "coverage") }, scriptsRoot: root });
    assert.deepEqual(result.problems, []);
  });
});

test("the folder is a command line option, and a script path is shown as it is", () => {
  assert.deepEqual(parseArguments(["--floors", "f.json", "--node-scripts", "n"]), { floorsFile: "f.json", folders: { "node-scripts": "n" } });
  assert.equal(sourceName("scripts/coverage/inventory.mjs", "scripts"), "scripts/coverage/inventory.mjs");
  assert.equal(sourceName("Weir.Core/A.cs", "scripts"), "scripts/Weir.Core/A.cs");
});

test("the command prints the table and exits 0 when every figure holds, and writes the job summary when asked", () => {
  withRepository(({ root, put }) => {
    put("scripts/a.mjs", "export const a = 1;\n");
    put("coverage/lcov.info", "SF:scripts/a.mjs\nLF:1\nLH:1\nend_of_record\n");
    put("floors.json", floorsText(100, 100));
    const summary = path.join(root, "summary.md");
    const result = spawnSync(process.execPath, [script, "--floors", path.join(root, "floors.json"), "--node-scripts", path.join(root, "coverage")], {
      cwd: root,
      encoding: "utf8",
      env: { ...process.env, GITHUB_STEP_SUMMARY: summary },
    });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /### Test coverage/);
    assert.match(result.stdout, /Node scripts/);
  });
});

test("the command exits 1 and names the project when a figure is below its floor", () => {
  withRepository(({ root, put }) => {
    put("scripts/a.mjs", "export const a = 1;\n");
    put("coverage/lcov.info", "SF:scripts/a.mjs\nLF:2\nLH:1\nend_of_record\n");
    put("floors.json", floorsText(100, 100));
    const result = spawnSync(process.execPath, [script, "--floors", path.join(root, "floors.json"), "--node-scripts", path.join(root, "coverage")], {
      cwd: root,
      encoding: "utf8",
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /::error::Node scripts line coverage is 50% \(1 of 2\)/);
  });
});

test("the command exits 1 with the reason when its options are wrong", () => {
  const result = spawnSync(process.execPath, [script, "--nope", "x"], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /::error::Unknown option --nope/);
});
