#!/usr/bin/env node
// A registry outage (npm's audit endpoint returning 5xx, or dropping the connection) must not fail
// Weir's CI the way a real High/Critical advisory does. This runs `npm audit --json` and retries with
// backoff only when the failure looks like the registry's fault (non-JSON output, a connection error,
// or npm's own "audit endpoint" error envelope); a run that returns real advisory data is never retried,
// whatever it contains. Used directly as a CLI (apps/web's audit step) and imported by
// docs-site/scripts/audit-dependencies.mjs, which applies its own exception list to the report.
//
// CLI usage (from the directory to audit): node <path-to>/npm-audit-retry.mjs [--level=high] [--exceptions=<file>]
// With --exceptions, an advisory passes only while that file approves it (see unapprovedAdvisories).

import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const SEVERITIES = ["info", "low", "moderate", "high", "critical"];
const REGISTRY_ERROR_PATTERN = /audit endpoint|ECONNRESET|ETIMEDOUT|EAI_AGAIN|ENOTFOUND|E5\d\d|\b5\d\d\b/i;

export class RegistryUnavailableError extends Error {
  constructor(reason, attempts) {
    super(`The npm registry's audit endpoint did not respond after ${attempts} attempt(s): ${reason}`);
    this.name = "RegistryUnavailableError";
  }
}

// Runs `npm audit --json` once. Exposed for the tests below; production callers use auditWithRetry.
export function runNpmAuditOnce(cwd, { platform = process.platform, spawn = spawnSync } = {}) {
  const onWindows = platform === "win32";
  const command = onWindows ? process.env.ComSpec : "npm";
  const args = onWindows ? ["/d", "/s", "/c", "npm audit --json"] : ["audit", "--json"];
  const result = spawn(command, args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

// Pure classifier: does this `npm audit --json` outcome look like the registry's own fault, or does it
// carry real (or at least parseable) advisory data? Exported so the retry/backoff logic above it can
// stay untested-but-trivial while this, the part with actual branches, is covered directly.
export function classifyAuditOutput({ stdout, stderr }) {
  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch {
    return { transient: true, reason: "npm audit did not return JSON", report: null };
  }
  if (parsed && typeof parsed === "object" && parsed.error) {
    const detail = `${parsed.error.summary ?? ""} ${parsed.error.detail ?? ""} ${parsed.error.code ?? ""} ${stderr}`;
    if (REGISTRY_ERROR_PATTERN.test(detail)) {
      return { transient: true, reason: detail.trim(), report: null };
    }
  }
  return { transient: false, reason: null, report: parsed };
}

function sleep(ms) {
  return new Promise((done) => setTimeout(done, ms));
}

// Retries only the transient (registry/network) case; a report with real findings, or an unrecognized
// failure shape, comes straight back so the caller judges it once, exactly as it would without retrying.
export async function auditWithRetry({ cwd, retries = 3, baseDelayMs = 3000, run = runNpmAuditOnce, wait = sleep }) {
  let lastReason;
  for (let attempt = 1; attempt <= retries + 1; attempt += 1) {
    const outcome = classifyAuditOutput(run(cwd));
    if (!outcome.transient) return outcome.report;
    lastReason = outcome.reason;
    if (attempt <= retries) {
      console.log(`[npm-audit] attempt ${attempt} looked like a registry problem (${lastReason}); retrying.`);
      await wait(baseDelayMs * 2 ** (attempt - 1));
    }
  }
  throw new RegistryUnavailableError(lastReason, retries + 1);
}

// Matches `npm audit --audit-level=<level>`: fails only when a vulnerability count at or above the
// requested severity is present in the report's metadata.
export function meetsAuditLevel(report, level) {
  const threshold = SEVERITIES.indexOf(level);
  if (threshold < 0) throw new Error(`Unknown audit level: ${level}`);
  const counts = report?.metadata?.vulnerabilities ?? {};
  return SEVERITIES.slice(threshold).some((severity) => (counts[severity] ?? 0) > 0);
}

/**
 * The advisories in a report, at or above `level`, that no current exception approves. An exception is
 * `{ advisory: "GHSA-…", expires: "YYYY-MM-DD", mitigation: "why it is safe here" }`; one past its
 * expiry, or one without a mitigation, approves nothing.
 */
export function unapprovedAdvisories(report, { exceptions = [], level = "info", today = new Date().toISOString().slice(0, 10) } = {}) {
  const threshold = SEVERITIES.indexOf(level);
  if (threshold < 0) throw new Error(`Unknown audit level: ${level}`);
  const approved = new Map(exceptions.map((item) => [item.advisory, item]));
  const findings = new Map();
  for (const [name, entry] of Object.entries(report?.vulnerabilities ?? {})) {
    for (const via of entry.via ?? []) {
      if (typeof via === "string") continue;
      const rank = SEVERITIES.indexOf(via.severity);
      if (rank >= 0 && rank < threshold) continue;
      const advisory = String(via.url || "").split("/").at(-1) || `${name} (no advisory id)`;
      const exception = approved.get(advisory);
      if (!exception) findings.set(advisory, `${name}: ${advisory}`);
      else if (!exception.mitigation) findings.set(advisory, `${name}: ${advisory} (exception has no mitigation)`);
      else if (exception.expires < today) findings.set(advisory, `${name}: ${advisory} (exception expired ${exception.expires})`);
    }
  }
  return [...findings.values()];
}

function readExceptions(file) {
  return JSON.parse(readFileSync(resolve(file), "utf8")).exceptions ?? [];
}

/**
 * The audit over `args` (`--level=`, `--exceptions=`); returns the exit code. `audit` is how the report is got
 * and `exceptionsOf` how an exceptions file is read; a test replaces them.
 */
export async function main(args, { audit = () => auditWithRetry({ cwd: process.cwd() }), exceptionsOf = readExceptions, log = console.log, error = console.error } = {}) {
  const levelArg = args.find((arg) => arg.startsWith("--level="));
  const level = levelArg ? levelArg.slice("--level=".length) : "high";
  const exceptionsArg = args.find((arg) => arg.startsWith("--exceptions="));

  let report;
  try {
    report = await audit();
  } catch (failure) {
    if (failure instanceof RegistryUnavailableError) {
      error(`[npm-audit] ${failure.message}`);
      error("[npm-audit] This is the npm registry, not a Weir problem. Re-run the job once npm's audit endpoint recovers.");
      return 1;
    }
    throw failure;
  }

  if (exceptionsArg) {
    const unapproved = unapprovedAdvisories(report, { exceptions: exceptionsOf(exceptionsArg.slice("--exceptions=".length)), level });
    if (unapproved.length > 0) {
      error(`[npm-audit] Advisories at or above "${level}" with no current exception:`);
      for (const item of unapproved) error(`- ${item}`);
      return 1;
    }
    log(`[npm-audit] Every advisory at or above "${level}" is covered by a current exception.`);
    return 0;
  }

  const counts = report?.metadata?.vulnerabilities ?? {};
  const found = SEVERITIES.filter((severity) => (counts[severity] ?? 0) > 0)
    .map((severity) => `${counts[severity]} ${severity}`)
    .join(", ");
  if (meetsAuditLevel(report, level)) {
    error(`[npm-audit] Found vulnerabilities at or above "${level}": ${found}.`);
    return 1;
  }
  log(`[npm-audit] No vulnerabilities at or above "${level}"${found ? ` (${found} below threshold)` : ""}.`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (failure) => {
      console.error(`[npm-audit] ${failure.stack}`);
      process.exitCode = 1;
    },
  );
}
