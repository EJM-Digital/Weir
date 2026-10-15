// node --test scripts/check-contract-real-tool-tests.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { allScenarios, checkContractRealToolTests, minimumScenarios, scenariosIn, windowsOnlySkipReason } from "./check-contract-real-tool-tests.mjs";

const source = `using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.Libraries;

[ContractArea("libraries")]
public sealed class SomeCleanTests
{
    [Fact]
    public void Not_a_real_tool_scenario() { }

    [RealFfmpegFact]
    public async Task A_clean_scenario()
    {
    }

    [RealMkvmergeFact]
    [Trait("a", "b")]
    public async Task A_mkvmerge_scenario() { }

    [RealFfmpegWindowsFact]
    public async Task A_windows_scenario() { }
}
`;

test("the scenarios a source file holds are read with their area, their reported name and whether only Windows runs them", () => {
  assert.deepEqual(scenariosIn(source), [
    { area: "libraries", name: "Weir.Contract.Tests.Libraries.SomeCleanTests.A_clean_scenario", windowsOnly: false },
    { area: "libraries", name: "Weir.Contract.Tests.Libraries.SomeCleanTests.A_mkvmerge_scenario", windowsOnly: false },
    { area: "libraries", name: "Weir.Contract.Tests.Libraries.SomeCleanTests.A_windows_scenario", windowsOnly: true },
  ]);
});

test("a class with real-tool scenarios that is in no contract area is refused, because no leg would check it", () => {
  assert.throws(() => scenariosIn(source.replace('[ContractArea("libraries")]\n', "")), /not marked \[ContractArea\]/);
});

test("each area holds as many scenarios as its minimum says, and no other area holds any", () => {
  const found = allScenarios();
  for (const [area, minimum] of Object.entries(minimumScenarios)) {
    assert.equal(found.filter((scenario) => scenario.area === area).length, minimum, `the ${area} area`);
  }
  assert.deepEqual([...new Set(found.map((scenario) => scenario.area))].sort(), Object.keys(minimumScenarios).sort());
});

const scenarios = [
  { area: "libraries", name: "Weir.Contract.Tests.Libraries.A.One", windowsOnly: false },
  { area: "libraries", name: "Weir.Contract.Tests.Libraries.A.Two", windowsOnly: true },
  { area: "processing", name: "Weir.Contract.Tests.Processing.B.Three", windowsOnly: false },
];
const result = (name, outcome = "Passed", message = "") => ({ name, outcome, message });

/** The leg's check, with the area's minimum out of the way, for the cases that are about something else. */
function check(results, area, platform, list = scenarios) {
  const saved = minimumScenarios[area];
  minimumScenarios[area] = 0;
  try {
    return checkContractRealToolTests(results, list, area, platform);
  } finally {
    minimumScenarios[area] = saved;
  }
}

test("every scenario of the area passing is accepted, and another area's scenarios are not expected", () => {
  const { failures, summary } = check([result(scenarios[0].name), result(scenarios[1].name)], "libraries", "windows");
  assert.deepEqual(failures, []);
  assert.match(summary, /2 passed, 0 failed, 0 skipped, of 2 real-tool scenarios in the libraries area/);
});

test("a scenario skipped because a tool is missing fails the leg and says why", () => {
  const reason = "mkvmerge is not on PATH (or WEIR_CONTRACT_REAL_MKVTOOLNIX_DIR)";
  const { failures } = check([result(scenarios[0].name, "NotExecuted", reason), result(scenarios[1].name)], "libraries", "windows");
  assert.deepEqual(failures, [`${scenarios[0].name} was skipped: ${reason}`]);
});

test("a scenario the results do not mention fails the leg", () => {
  const { failures } = check([result(scenarios[0].name)], "libraries", "linux");
  assert.deepEqual(failures, [`${scenarios[1].name} was not reported.`]);
});

test("the Windows-only scenario may be skipped for its own reason off Windows, and nowhere else", () => {
  const skipped = [result(scenarios[0].name), result(scenarios[1].name, "NotExecuted", windowsOnlySkipReason)];
  assert.deepEqual(check(skipped, "libraries", "linux").failures, []);
  assert.equal(check(skipped, "libraries", "windows").failures.length, 1);
  const wrongReason = [result(scenarios[0].name), result(scenarios[1].name, "NotExecuted", "ffmpeg and ffprobe are not on PATH")];
  assert.equal(check(wrongReason, "libraries", "linux").failures.length, 1);
});

test("a scenario that failed counts as having run, so the test step's own failure is not repeated", () => {
  const { failures, summary } = check([result(scenarios[0].name, "Failed", "boom"), result(scenarios[1].name)], "libraries", "windows");
  assert.deepEqual(failures, []);
  assert.match(summary, /1 passed, 1 failed, 0 skipped/);
});

test("a parametrised scenario is found by its name before the arguments", () => {
  const { failures, summary } = check(
    [result("Weir.Contract.Tests.Processing.B.Three(a: 1)"), result("Weir.Contract.Tests.Processing.B.Three(a: 2)")],
    "processing",
    "linux",
  );
  assert.deepEqual(failures, []);
  assert.match(summary, /2 passed/);
});

test("fewer scenarios marked in an area than its minimum fails the leg even when none was skipped", () => {
  const { failures } = checkContractRealToolTests([result(scenarios[2].name)], scenarios, "processing", "linux");
  assert.deepEqual(failures, ["Only 1 real-tool scenarios are marked in the processing area; it holds at least 3."]);
});

const script = fileURLToPath(new URL("./check-contract-real-tool-tests.mjs", import.meta.url));

/** A TRX file holding `results`, in the shape dotnet test writes. */
function trxFile(folder, results) {
  const body = results
    .map(({ name, outcome, message }) =>
      message
        ? `<UnitTestResult testName="${name}" outcome="${outcome}"><Output><ErrorInfo><Message>${message}</Message></ErrorInfo></Output></UnitTestResult>`
        : `<UnitTestResult testName="${name}" outcome="${outcome}" />`,
    )
    .join("\n");
  mkdirSync(path.join(folder, "TestResults"), { recursive: true });
  writeFileSync(path.join(folder, "TestResults", "run.trx"), `<TestRun><Results>${body}</Results></TestRun>`);
}

function runCheck(area, results) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-contract-real-tool-check-"));
  try {
    if (results) trxFile(folder, results);
    return spawnSync(process.execPath, [script, area, folder, "linux"], { encoding: "utf8", env: { PATH: process.env.PATH } });
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

const processingRun = () => allScenarios().filter((scenario) => scenario.area === "processing").map((scenario) => result(scenario.name));

test("the command exits 0 when every scenario of the area ran", () => {
  const run = runCheck("processing", processingRun());
  assert.equal(run.status, 0);
  assert.match(run.stdout, /3 passed, 0 failed, 0 skipped, of 3 real-tool scenarios in the processing area/);
});

test("the command exits 1 and names each scenario that was skipped", () => {
  const [first, ...rest] = processingRun();
  const run = runCheck("processing", [{ ...first, outcome: "NotExecuted", message: "ffmpeg and ffprobe are not on PATH (or WEIR_CONTRACT_REAL_FFMPEG_DIR)" }, ...rest]);
  assert.equal(run.status, 1);
  assert.match(run.stdout, /- .* was skipped: ffmpeg and ffprobe are not on PATH/);
});

test("the command exits 1 when no result file was written", () => {
  const run = runCheck("processing", null);
  assert.equal(run.status, 1);
  assert.match(run.stdout, /No test result files were written\./);
});

test("the command refuses a call without an area", () => {
  assert.equal(spawnSync(process.execPath, [script], { encoding: "utf8" }).status, 2);
});
