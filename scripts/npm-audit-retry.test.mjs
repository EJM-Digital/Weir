// node --test scripts/npm-audit-retry.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { auditWithRetry, classifyAuditOutput, main, meetsAuditLevel, RegistryUnavailableError, unapprovedAdvisories } from "./npm-audit-retry.mjs";

const CLEAN_REPORT = { metadata: { vulnerabilities: { info: 0, low: 0, moderate: 0, high: 0, critical: 0 } } };
const HIGH_REPORT = { metadata: { vulnerabilities: { info: 0, low: 2, moderate: 0, high: 1, critical: 0 } } };

test("a normal audit report is not transient", () => {
  const outcome = classifyAuditOutput({ stdout: JSON.stringify(CLEAN_REPORT), stderr: "" });
  assert.equal(outcome.transient, false);
  assert.deepEqual(outcome.report, CLEAN_REPORT);
});

test("output that is not JSON is treated as a registry problem", () => {
  const outcome = classifyAuditOutput({ stdout: "npm ERR! network timeout", stderr: "" });
  assert.equal(outcome.transient, true);
});

test("npm's own audit-endpoint error envelope is a registry problem", () => {
  const stdout = JSON.stringify({ error: { code: "E503", summary: "audit endpoint returned an error" } });
  const outcome = classifyAuditOutput({ stdout, stderr: "" });
  assert.equal(outcome.transient, true);
});

test("an unrecognized error envelope is not assumed to be transient", () => {
  const stdout = JSON.stringify({ error: { code: "EACCES", summary: "permission denied" } });
  const outcome = classifyAuditOutput({ stdout, stderr: "" });
  assert.equal(outcome.transient, false);
});

test("meetsAuditLevel fails only at or above the requested severity", () => {
  assert.equal(meetsAuditLevel(HIGH_REPORT, "high"), true);
  assert.equal(meetsAuditLevel(HIGH_REPORT, "critical"), false);
  assert.equal(meetsAuditLevel(HIGH_REPORT, "low"), true);
  assert.equal(meetsAuditLevel(CLEAN_REPORT, "low"), false);
});

test("a registry error is retried and a later clean run succeeds", async () => {
  let calls = 0;
  const run = () => {
    calls += 1;
    return calls < 3 ? { stdout: "npm ERR! network timeout", stderr: "" } : { stdout: JSON.stringify(CLEAN_REPORT), stderr: "" };
  };
  const waits = [];
  const report = await auditWithRetry({ cwd: ".", retries: 3, baseDelayMs: 10, run, wait: async (ms) => waits.push(ms) });
  assert.deepEqual(report, CLEAN_REPORT);
  assert.equal(calls, 3);
  assert.equal(waits.length, 2);
});

test("a real vulnerability report is never retried", async () => {
  let calls = 0;
  const run = () => {
    calls += 1;
    return { stdout: JSON.stringify(HIGH_REPORT), stderr: "" };
  };
  const report = await auditWithRetry({ cwd: ".", retries: 3, baseDelayMs: 10, run, wait: async () => {} });
  assert.deepEqual(report, HIGH_REPORT);
  assert.equal(calls, 1);
});

test("a registry that never recovers fails with a message that blames the registry", async () => {
  const run = () => ({ stdout: "npm ERR! network timeout", stderr: "" });
  await assert.rejects(
    auditWithRetry({ cwd: ".", retries: 2, baseDelayMs: 10, run, wait: async () => {} }),
    (error) => error instanceof RegistryUnavailableError && /3 attempt/.test(error.message),
  );
});

const ADVISORY_REPORT = {
  vulnerabilities: {
    braces: { via: [{ url: "https://github.com/advisories/GHSA-aaaa-bbbb-cccc", severity: "high" }] },
    micromatch: { via: ["braces"] },
    tiny: { via: [{ url: "https://github.com/advisories/GHSA-low0-0000-0000", severity: "low" }] },
  },
};
const CURRENT = { advisory: "GHSA-aaaa-bbbb-cccc", expires: "2026-11-02", mitigation: "dev only" };

test("an advisory with no exception is reported", () => {
  assert.deepEqual(unapprovedAdvisories(ADVISORY_REPORT, { level: "high", today: "2026-10-03" }), ["braces: GHSA-aaaa-bbbb-cccc"]);
});

test("a current exception with a mitigation approves its advisory", () => {
  assert.deepEqual(unapprovedAdvisories(ADVISORY_REPORT, { exceptions: [CURRENT], level: "high", today: "2026-10-03" }), []);
});

test("an expired exception approves nothing", () => {
  const [finding] = unapprovedAdvisories(ADVISORY_REPORT, { exceptions: [CURRENT], level: "high", today: "2026-11-03" });
  assert.match(finding, /expired 2026-11-02/);
});

test("an exception without a mitigation approves nothing", () => {
  const [finding] = unapprovedAdvisories(ADVISORY_REPORT, { exceptions: [{ ...CURRENT, mitigation: "" }], level: "high", today: "2026-10-03" });
  assert.match(finding, /no mitigation/);
});

test("advisories below the level are left out, and at level info every one counts", () => {
  assert.equal(unapprovedAdvisories(ADVISORY_REPORT, { exceptions: [CURRENT], level: "high", today: "2026-10-03" }).length, 0);
  assert.deepEqual(unapprovedAdvisories(ADVISORY_REPORT, { exceptions: [CURRENT], level: "info", today: "2026-10-03" }), ["tiny: GHSA-low0-0000-0000"]);
});

test("an advisory of unknown severity is never skipped", () => {
  const report = { vulnerabilities: { odd: { via: [{ url: "https://github.com/advisories/GHSA-odd0-0000-0000" }] } } };
  assert.deepEqual(unapprovedAdvisories(report, { level: "critical", today: "2026-10-03" }), ["odd: GHSA-odd0-0000-0000"]);
  assert.deepEqual(unapprovedAdvisories({ vulnerabilities: { bare: { via: [{ severity: "high" }, "bare-dependency"] } } }, { today: "2026-10-03" }), ["bare: bare (no advisory id)"]);
  assert.deepEqual(unapprovedAdvisories(undefined), []);
  assert.throws(() => unapprovedAdvisories(ADVISORY_REPORT, { level: "severe" }), /Unknown audit level: severe/);
});

// The command, with the audit and the exceptions file answered from memory.
async function runCommand(args, { report, failure, exceptions = [] }) {
  const seen = { log: [], error: [] };
  const code = await main(args, {
    audit: async () => {
      if (failure) throw failure;
      return report;
    },
    exceptionsOf: (file) => {
      seen.exceptionsFile = file;
      return exceptions;
    },
    log: (line) => seen.log.push(line),
    error: (line) => seen.error.push(line),
  });
  return { code, ...seen };
}

const counted = (counts) => ({ metadata: { vulnerabilities: counts } });

test("a report with nothing at or above the level passes, noting what is below it", async () => {
  const clean = await runCommand([], { report: counted({ low: 2 }) });
  assert.equal(clean.code, 0);
  assert.deepEqual(clean.log, ['[npm-audit] No vulnerabilities at or above "high" (2 low below threshold).']);
  assert.deepEqual((await runCommand(["--level=low"], { report: counted({}) })).log, ['[npm-audit] No vulnerabilities at or above "low".']);
});

test("a report with a finding at or above the level fails, counting what it found", async () => {
  const result = await runCommand([], { report: counted({ high: 1, critical: 2 }) });
  assert.equal(result.code, 1);
  assert.deepEqual(result.error, ['[npm-audit] Found vulnerabilities at or above "high": 1 high, 2 critical.']);
});

test("with an exceptions file only advisories no current exception covers fail", async () => {
  const exceptions = [{ advisory: "GHSA-aaaa-bbbb-cccc", expires: "2999-01-01", mitigation: "dev only" }];
  const covered = await runCommand(["--exceptions=audit.json", "--level=high"], { report: ADVISORY_REPORT, exceptions });
  assert.equal(covered.code, 0);
  assert.equal(covered.exceptionsFile, "audit.json");
  assert.deepEqual(covered.log, ['[npm-audit] Every advisory at or above "high" is covered by a current exception.']);
  const open = await runCommand(["--exceptions=audit.json"], { report: ADVISORY_REPORT });
  assert.equal(open.code, 1);
  assert.deepEqual(open.error, ['[npm-audit] Advisories at or above "high" with no current exception:', "- braces: GHSA-aaaa-bbbb-cccc"]);
});

test("a registry that did not answer is said to be the registry's fault, and any other failure is thrown", async () => {
  const down = await runCommand([], { failure: new RegistryUnavailableError("timed out", 4) });
  assert.equal(down.code, 1);
  assert.match(down.error[0], /did not respond after 4 attempt\(s\): timed out/);
  assert.match(down.error[1], /not a Weir problem/);
  await assert.rejects(runCommand([], { failure: new Error("disk full") }), /disk full/);
});
