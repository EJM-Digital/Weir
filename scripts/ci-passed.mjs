#!/usr/bin/env node
// CI's single verdict (the `ci-passed` job in .github/workflows/ci.yml): every job that was due for this change
// ran and passed, and every job that was not due was skipped by path filtering, not by an error.
//
// `changes` decides which jobs are due and publishes one run_* flag per job; GATES below maps each job to its
// flag (null: the job always runs). A due job must end `success`. A job that was not due must end `skipped`:
// anything else means the flags and the job conditions disagree, which is a bug in ci.yml to fix, not a pass.
// A scheduled run on a day main had no commit (`changes` publishes idle=true) has nothing to test, so every job, the ones that
// always run included, must end `skipped`; that run passes but never counts as the release's proof (its "The Windows legs ran and
// passed" step is skipped, and the release reads only push and manual runs of the verdict).
// `changes` itself must succeed, because without it no flag can be trusted.
//
// Usage (in ci.yml): NEEDS='${{ toJSON(needs) }}' node scripts/ci-passed.mjs
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";

export const GATES = {
  "repo-checks": null,
  // Reads main's history, so it is a job of its own with a full clone (scripts/check-no-silent-reverts.mjs).
  "no-silent-reverts": null,
  "server-linux": "run_server_linux",
  "server-windows": "run_server_windows",
  "web-dist": "run_web",
  web: "run_web",
  "e2e-smoke": "run_e2e",
  // Both call ci-contract.yml, every area's leg: the Linux legs and the Windows ones (docs/ci-cost.md).
  contract: "run_contract",
  "contract-windows": "run_contract_windows",
  tray: "run_tray",
  // Merges the server, tray and web coverage and holds each to its floor (docs/coverage/floors.json).
  coverage: "run_coverage",
  // Both call ci-packaging.yml: the Docker smokes and Linux scenario shards, and the Windows package smoke and scenario shards.
  packaging: "run_packaging",
  "packaging-windows": "run_packaging_windows",
};

// Returns one line per problem; empty means CI passed.
export function verdict(needs) {
  const problems = [];
  const changes = needs.changes;
  if (!changes || changes.result !== "success") {
    return [`changes ended ${changes?.result ?? "missing"}; without it no job's flag can be trusted.`];
  }
  const flags = changes.outputs || {};

  for (const job of Object.keys(needs)) {
    if (job !== "changes" && !(job in GATES)) problems.push(`${job} is not in scripts/ci-passed.mjs GATES; add it with its flag.`);
  }
  for (const [job, flag] of Object.entries(GATES)) {
    const result = needs[job]?.result;
    if (result === undefined) {
      problems.push(`${job} is not in ci-passed's needs.`);
      continue;
    }
    if (flag !== null && !["true", "false"].includes(flags[flag])) {
      problems.push(`changes published no usable ${flag} (got ${JSON.stringify(flags[flag])}).`);
      continue;
    }
    const due = flags.idle === "true" ? false : flag === null || flags[flag] === "true";
    const expected = due ? "success" : "skipped";
    if (result !== expected) {
      problems.push(`${job} ended ${result}; it ${due ? "was due, so it must pass" : "was not due, so it must be skipped"}.`);
    }
  }
  return problems;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const needs = JSON.parse(process.env.NEEDS || "{}");
  const problems = verdict(needs);
  for (const [job, flag] of Object.entries(GATES)) {
    const due = needs.changes?.outputs?.idle === "true" ? "idle" : flag === null ? "always" : needs.changes?.outputs?.[flag] === "true" ? "due" : "not due";
    console.log(`${job.padEnd(22)} ${due.padEnd(8)} ${needs[job]?.result ?? "missing"}`);
  }
  if (problems.length) {
    for (const problem of problems) console.error(`::error::${problem}`);
    process.exit(1);
  }
  console.log("Every job that was due passed, and every other job was skipped by path filtering.");
}
