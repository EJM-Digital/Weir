// node --test scripts/real-tool-checks-commands.test.mjs
// The two real-tool checks as commands (usage, the CI step summary), and the source shapes the contract scan refuses or skips.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { allScenarios, checkContractRealToolTests, scenariosIn } from "./check-contract-real-tool-tests.mjs";

const contractScript = fileURLToPath(new URL("./check-contract-real-tool-tests.mjs", import.meta.url));
const serverScript = fileURLToPath(new URL("./check-real-tool-tests.mjs", import.meta.url));

function inFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-real-tool-"));
  try {
    return body(folder);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

const withSummary = (summary) => ({ PATH: process.env.PATH, ...(summary ? { GITHUB_STEP_SUMMARY: summary } : {}) });

test("a source file with scenarios but no namespace, or a scenario outside any class, is refused", () => {
  assert.throws(() => scenariosIn("[ContractArea(\"a\")] class A { [RealFfmpegFact] public void S() {} }", "A.cs"), /A\.cs has real-tool scenarios but no file-scoped namespace\./);
  assert.throws(() => scenariosIn("namespace N;\n[RealFfmpegFact] public void S() {}\n", "B.cs"), /B\.cs: the scenario S is not inside a class\./);
});

test("the scan skips bin and obj folders and reads other folders' sources", () => {
  inFolder((project) => {
    const source = (name) => `namespace N;\n[ContractArea("a")]\npublic class ${name}\n{\n  [RealFfmpegFact]\n  public void Scenario() {}\n}\n`;
    for (const folder of ["bin", "obj", "tests"]) mkdirSync(path.join(project, folder));
    writeFileSync(path.join(project, "bin", "Built.cs"), "not even C#");
    writeFileSync(path.join(project, "obj", "Built.cs"), "not even C#");
    writeFileSync(path.join(project, "tests", "Real.cs"), source("Real"));
    writeFileSync(path.join(project, "notes.txt"), "[RealFfmpegFact]");
    assert.deepEqual(allScenarios(project).map((scenario) => scenario.name), ["N.Real.Scenario"]);
  });
});

test("an area with no minimum needs none, and judges only its own scenarios", () => {
  const scenarios = [{ area: "other", name: "N.C.S", windowsOnly: false }];
  const { failures, summary } = checkContractRealToolTests([{ name: "N.C.S", outcome: "Passed" }], scenarios, "other", "linux");
  assert.deepEqual(failures, []);
  assert.equal(summary, "1 passed, 0 failed, 0 skipped, of 1 real-tool scenarios in the other area.");
});

for (const [name, script, usage, goodArguments] of [
  ["contract", contractScript, /^Usage: check-contract-real-tool-tests\.mjs <area>/, ["libraries"]],
  ["server", serverScript, /^Usage: check-real-tool-tests\.mjs <folder/, []],
]) {
  test(`the ${name} real-tool check wants its arguments, and a platform of linux or windows`, () => {
    for (const args of [[], [...goodArguments, "somewhere", "macos"], [...goodArguments]]) {
      const run = spawnSync(process.execPath, [script, ...args], { encoding: "utf8", env: withSummary() });
      assert.equal(run.status, 2, args.join(" "));
      assert.match(run.stderr, usage);
    }
  });

  test(`the ${name} real-tool check appends its report to the CI step summary when there is one, and says no result files were written`, () => {
    inFolder((folder) => {
      const summary = path.join(folder, "summary.md");
      const results = path.join(folder, "results");
      mkdirSync(results);
      const run = spawnSync(process.execPath, [script, ...goodArguments, results, "linux", "A title"], { encoding: "utf8", env: withSummary(summary) });
      assert.equal(run.status, 1, run.stderr);
      assert.match(run.stdout, /^### A title\n\nNo test result files were written\./);
      assert.match(readFileSync(summary, "utf8"), /^### A title\n\nNo test result files were written\./);
    });
  });
}
