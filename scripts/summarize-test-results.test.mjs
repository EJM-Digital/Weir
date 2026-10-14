// node --test scripts/summarize-test-results.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { readTrx, readTrxResults } from "./summarize-test-results.mjs";

const result = (name, outcome, message) =>
  message === undefined
    ? `<UnitTestResult testName="${name}" outcome="${outcome}" />`
    : `<UnitTestResult testName="${name}" outcome="${outcome}"><Output><ErrorInfo><Message>${message}</Message></ErrorInfo></Output></UnitTestResult>`;

// dotnet test counts a skipped test in Total but leaves notExecuted at 0, so the counters cannot say how many skipped.
const trx = [
  '<TestRun><Results>',
  result("A.passes", "Passed"),
  result("A.fails", "Failed", "Expected 1 &amp; got 2"),
  result("A.skips", "NotExecuted", "No working mkvmerge was found."),
  "</Results>",
  '<ResultSummary><Counters total="3" executed="2" passed="1" failed="1" notExecuted="0" /></ResultSummary></TestRun>',
].join("\n");

test("each result is read with its name, outcome and message", () => {
  assert.deepEqual(readTrxResults(trx), [
    { name: "A.passes", outcome: "Passed", message: "" },
    { name: "A.fails", outcome: "Failed", message: "Expected 1 & got 2" },
    { name: "A.skips", outcome: "NotExecuted", message: "No working mkvmerge was found." },
  ]);
});

test("skipped tests are counted from the results, not from a counter dotnet test leaves at 0", () => {
  const run = readTrx(trx);
  assert.deepEqual({ total: run.total, passed: run.passed, failed: run.failed, skipped: run.skipped }, { total: 3, passed: 1, failed: 1, skipped: 1 });
  assert.deepEqual(run.failures, [{ name: "A.fails", message: "Expected 1 & got 2" }]);
});
