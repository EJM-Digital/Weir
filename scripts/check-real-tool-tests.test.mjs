// node --test scripts/check-real-tool-tests.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { checkRealToolTests, minimumRealToolTests, realToolTestClasses, windowsOnlySkipReason } from "./check-real-tool-tests.mjs";

const namedPipeTest = `${realToolTestClasses[0]}.A_progress_run_that_never_writes_a_line_is_still_stopped_by_the_timer`;

/** The reported results of `count` real-tool tests, spread over the three classes, all with `outcome`. */
function realToolResults(count, outcome = "Passed", message = "") {
  return Array.from({ length: count }, (_, index) => ({
    name: `${realToolTestClasses[index % realToolTestClasses.length]}.Test_${index}`,
    outcome,
    message,
  }));
}

const unrelated = [{ name: "Weir.Infrastructure.Tests.Jobs.SomeTests.Passes", outcome: "Passed", message: "" }];

test("the minimum is the 31 real-tool tests the server tests hold", () => {
  assert.equal(minimumRealToolTests, 31);
});

test("every real-tool test passing on Windows is accepted", () => {
  const { failures, summary } = checkRealToolTests([...realToolResults(31), ...unrelated], "windows");
  assert.deepEqual(failures, []);
  assert.match(summary, /31 passed, 0 failed, 0 skipped/);
});

test("on Linux the one Windows-only test may be skipped for its own reason, and nothing else may", () => {
  const results = [...realToolResults(30), { name: namedPipeTest, outcome: "NotExecuted", message: windowsOnlySkipReason }];
  const { failures, summary } = checkRealToolTests(results, "linux");
  assert.deepEqual(failures, []);
  assert.match(summary, /30 passed, 0 failed, 1 skipped/);
});

test("on Windows that same skip is a failure, because the test must run there", () => {
  const results = [...realToolResults(30), { name: namedPipeTest, outcome: "NotExecuted", message: windowsOnlySkipReason }];
  const { failures } = checkRealToolTests(results, "windows");
  assert.equal(failures.length, 1);
  assert.match(failures[0], /A_progress_run_that_never_writes_a_line/);
});

test("a real-tool test skipped because a tool is missing fails the leg and says why", () => {
  const skipped = realToolResults(8, "NotExecuted", "No working mkvmerge was found: set WEIR_MKVTOOLNIX_DIR or put it on PATH to run the real-mkvmerge tests.");
  const { failures } = checkRealToolTests([...realToolResults(23), ...skipped], "linux");
  assert.equal(failures.length, 8);
  assert.match(failures[0], /No working mkvmerge was found/);
});

test("fewer real-tool tests than the minimum fails the leg even when none was skipped", () => {
  const { failures } = checkRealToolTests(realToolResults(30), "windows");
  assert.deepEqual(failures, ["Only 30 real-tool tests were reported; the server tests hold at least 31."]);
});

test("a failed real-tool test counts as having run, so the test step's own failure is not repeated", () => {
  const results = [...realToolResults(30), ...realToolResults(1, "Failed", "Expected 1")];
  const { failures, summary } = checkRealToolTests(results, "windows");
  assert.deepEqual(failures, []);
  assert.match(summary, /30 passed, 1 failed, 0 skipped/);
});

test("no real-tool tests in the results at all fails the leg", () => {
  const { failures } = checkRealToolTests(unrelated, "linux");
  assert.deepEqual(failures, ["Only 0 real-tool tests were reported; the server tests hold at least 31."]);
});

const script = fileURLToPath(new URL("./check-real-tool-tests.mjs", import.meta.url));

/** A TRX file holding `results`, in the shape dotnet test writes. */
function trxFile(folder, results) {
  const body = results
    .map(({ name, outcome, message }) =>
      message
        ? `<UnitTestResult testName="${name}" outcome="${outcome}"><Output><ErrorInfo><Message>${message}</Message></ErrorInfo></Output></UnitTestResult>`
        : `<UnitTestResult testName="${name}" outcome="${outcome}" />`,
    )
    .join("\n");
  mkdirSync(path.join(folder, "Weir.Infrastructure.Tests", "TestResults"), { recursive: true });
  writeFileSync(path.join(folder, "Weir.Infrastructure.Tests", "TestResults", "run.trx"), `<TestRun><Results>${body}</Results></TestRun>`);
}

function runCheck(results, platform) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-real-tool-check-"));
  try {
    if (results) trxFile(folder, results);
    return spawnSync(process.execPath, [script, folder, platform], { encoding: "utf8", env: { PATH: process.env.PATH } });
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

test("the command exits 0 when every real-tool test ran", () => {
  const run = runCheck(realToolResults(31), "windows");
  assert.equal(run.status, 0);
  assert.match(run.stdout, /31 passed, 0 failed, 0 skipped, of 31 real-tool tests/);
});

test("the command exits 1 and lists each skipped test when some were skipped", () => {
  const run = runCheck([...realToolResults(23), ...realToolResults(8, "NotExecuted", "No working mkvmerge was found.")], "linux");
  assert.equal(run.status, 1);
  assert.match(run.stdout, /23 passed, 0 failed, 8 skipped/);
  assert.match(run.stdout, /- .*Test_\d+ was skipped: No working mkvmerge was found\./);
});

test("the command exits 1 when no result file was written", () => {
  const run = runCheck(null, "linux");
  assert.equal(run.status, 1);
  assert.match(run.stdout, /No test result files were written\./);
});

test("the command refuses a platform it does not know", () => {
  const run = runCheck(realToolResults(31), "macos");
  assert.equal(run.status, 2);
});
