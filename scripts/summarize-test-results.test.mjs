// node --test scripts/summarize-test-results.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { findTrx, readTrx, readTrxResults, summarizePlaywright, summarizeTrx } from "./summarize-test-results.mjs";

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

// The summaries and the command, over real folders and files.
const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "summarize-test-results.mjs");

function withFolder(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-summary-"));
  try {
    const put = (file, text) => {
      mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
      writeFileSync(path.join(root, file), text);
      return path.join(root, file);
    };
    callback({ root, put });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const command = (...args) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8", env: { ...process.env, GITHUB_STEP_SUMMARY: "" } });

test("result files are found in any folder below, in order, skipping node_modules and .git", () => {
  withFolder(({ root, put }) => {
    put("b/TestResults/two.TRX", "");
    put("a/TestResults/one.trx", "");
    put("a/node_modules/x/skipped.trx", "");
    put("a/.git/skipped.trx", "");
    put("a/notes.txt", "");
    assert.deepEqual(
      findTrx(root).map((file) => path.relative(root, file).split(path.sep).join("/")),
      ["a/TestResults/one.trx", "b/TestResults/two.TRX"],
    );
    assert.deepEqual(findTrx(path.join(root, "missing")), []);
    assert.deepEqual(findTrx(path.join(root, "a", "notes.txt")), []);
  });
});

test("a folder with no result files says so, under the title it was given", () => {
  withFolder(({ root }) => {
    assert.equal(summarizeTrx(root), "### Backend tests\n\nNo test result files were written.\n");
    assert.equal(summarizeTrx(root, "Tray tests"), "### Tray tests\n\nNo test result files were written.\n");
  });
});

test("the TRX summary totals the projects and lists the failed tests with their first message line", () => {
  withFolder(({ root, put }) => {
    put("Weir.Core.Tests/TestResults/r.trx", trx.replace("Expected 1 &amp; got 2", "Expected 1\nsecond line"));
    put("Weir.Api.Tests/TestResults/r.trx", ["<Results>", result("B.ok", "Passed"), "</Results>", '<Counters total="1" passed="1" failed="0" />'].join("\n"));
    assert.equal(
      summarizeTrx(root, "Server tests"),
      [
        "### Server tests",
        "",
        "2 passed, 1 failed, 1 skipped, of 4 tests in 2 project(s).",
        "",
        "| Project | Tests | Passed | Failed | Skipped |",
        "|---|---:|---:|---:|---:|",
        "| Weir.Api.Tests | 1 | 1 | 0 | 0 |",
        "| Weir.Core.Tests | 3 | 1 | 1 | 1 |",
        "",
        "Failed tests:",
        "",
        "- `Weir.Core.Tests`: A.fails - Expected 1",
        "",
      ].join("\n"),
    );
  });
});

test("a failed test with no message is listed by name, and more than twenty-five failures are cut short with a count", () => {
  withFolder(({ root, put }) => {
    const many = Array.from({ length: 27 }, (_, index) => result(`T.f${index}`, "Failed")).join("\n");
    put("P/TestResults/r.trx", `<Results>${many}</Results><Counters total="27" passed="0" failed="27" />`);
    const lines = summarizeTrx(root).split("\n");
    assert.equal(lines.filter((line) => line.startsWith("- `P`")).length, 25);
    assert.ok(lines.includes("- `P`: T.f0"));
    assert.ok(lines.includes("- ... and 2 more (see the TRX artifact)."));
  });
});

const playwrightReport = {
  stats: { expected: 5, unexpected: 2, flaky: 1, skipped: 3, duration: 12400 },
  suites: [
    {
      title: "smoke.spec.ts",
      suites: [
        {
          title: "Sign in",
          specs: [
            { title: "shows the form", file: "smoke.spec.ts", tests: [{ projectName: "chromium", status: "expected" }] },
            { title: "rejects a wrong password", file: "smoke.spec.ts", tests: [{ projectName: "chromium", status: "unexpected" }, { status: "flaky" }] },
          ],
        },
      ],
      specs: [{ title: "loads", file: "smoke.spec.ts" }],
    },
    { title: "", specs: [{ title: "top level", file: "x.tsx", tests: [{ projectName: "firefox", status: "unexpected" }] }] },
  ],
};

test("a Playwright report gives the counts, then each failed and flaky test with its place", () => {
  withFolder(({ put }) => {
    const file = put("report.json", JSON.stringify(playwrightReport));
    assert.equal(
      summarizePlaywright(file),
      [
        "### Browser smoke tests",
        "",
        "5 passed, 2 failed, 1 flaky, 3 skipped in 12 s.",
        "",
        "Failed:",
        "",
        "- Sign in > rejects a wrong password [chromium] (smoke.spec.ts)",
        "- top level [firefox] (x.tsx)",
        "",
        "Flaky (passed on retry):",
        "",
        "- Sign in > rejects a wrong password [default] (smoke.spec.ts)",
        "",
      ].join("\n"),
    );
  });
});

test("a Playwright report with nothing in it reads as zero, and more than twenty-five failures are cut short", () => {
  withFolder(({ put }) => {
    assert.equal(summarizePlaywright(put("empty.json", "{}"), "Browser"), "### Browser\n\n0 passed, 0 failed, 0 flaky, 0 skipped in 0 s.\n");
    const specs = Array.from({ length: 26 }, (_, index) => ({ title: `t${index}`, file: "f.ts", tests: [{ status: "unexpected" }] }));
    const text = summarizePlaywright(put("many.json", JSON.stringify({ suites: [{ title: "s", specs }] })));
    assert.ok(text.includes("- ... and 1 more."));
  });
});

test("a Playwright report that is missing or not JSON says so", () => {
  withFolder(({ root, put }) => {
    assert.match(summarizePlaywright(path.join(root, "none.json")), /No Playwright result file was written/);
    assert.match(summarizePlaywright(put("bad.json", "{not json")), /could not be read/);
  });
});

test("the command prints the summary, and adds it to the job summary when asked", () => {
  withFolder(({ root, put }) => {
    const report = put("report.json", JSON.stringify(playwrightReport));
    const summary = path.join(root, "summary.md");
    const result = spawnSync(process.execPath, [script, "playwright", report, "Smoke"], { encoding: "utf8", env: { ...process.env, GITHUB_STEP_SUMMARY: summary } });
    assert.equal(result.status, 0);
    assert.match(result.stdout, /### Smoke/);
    assert.match(readFileSync(summary, "utf8"), /### Smoke/);
    put("P/TestResults/r.trx", trx);
    assert.match(command("trx", root).stdout, /1 passed, 1 failed, 1 skipped, of 3 tests in 1 project/);
  });
});

test("the command with no usable arguments prints its usage and still exits 0", () => {
  const result = command("trx");
  assert.equal(result.status, 0);
  assert.match(result.stderr, /Usage: summarize-test-results\.mjs trx <folder> \[title\] \| playwright <file> \[title\]/);
  assert.match(command("other", "x").stderr, /Usage/);
});

test("a job summary that cannot be written is reported, and never fails the step", () => {
  withFolder(({ root }) => {
    const result = spawnSync(process.execPath, [script, "trx", root], { encoding: "utf8", env: { ...process.env, GITHUB_STEP_SUMMARY: root } });
    assert.equal(result.status, 0);
    assert.match(result.stderr, /Could not summarize test results: /);
  });
});
