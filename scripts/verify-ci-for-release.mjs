#!/usr/bin/env node
// Release gate: refuse to publish a tag unless `.github/workflows/ci.yml` already passed on the exact commit the
// tag points at. A tag is made only after CI is green on that commit (docs/ci-standard.md), so
// this does not wait for a run that is still going: it fails at once and says what to do.
//
// A CI run counts only when all of these hold:
//
// - it ran ci.yml for exactly this commit, as a `push` (to main) or a `workflow_dispatch` run. A `pull_request`
//   run is never accepted: what it tested was the branch merged into the base as it stood then, not this tree;
// - it is complete and its conclusion is `success`;
// - its `ci-passed` job and that job's verdict step concluded `success`. ci-passed (scripts/ci-passed.mjs) passes
//   only when every job due for the change passed and every other job was skipped by path filtering.
//
// Note what a green push run does and does not prove: a push that changed only documents skips every code job, so
// its green says "nothing that needed testing changed since the last commit that did". Tag from a commit whose own
// run ran the jobs, or run ci.yml by hand on the tag (a manual run skips nothing).
//
// Usage (in release.yml):  node scripts/verify-ci-for-release.mjs
//   env GH_TOKEN (actions: read), GITHUB_REPOSITORY, GITHUB_SHA (else `git rev-parse HEAD`)
// Diagnose one run by id, whatever its event:  node scripts/verify-ci-for-release.mjs --run-id <id>
// Options: --sha <sha>
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const CI_WORKFLOW = "ci.yml";
export const ACCEPTED_EVENTS = ["push", "workflow_dispatch"];

// The ci.yml job and step that carry CI's verdict. scripts/check-release-workflow-gates.mjs fails if ci.yml stops
// declaring them, so a rename cannot silently disarm the gate.
export const REQUIRED_EVIDENCE = [{ job: "ci-passed", steps: ["Every job that was due passed"] }];

// Judges one run against the evidence. `run` has event, status, conclusion; `jobs` is the run's jobs, each with
// name, conclusion and steps[{name, conclusion}]. Returns the reasons it does not qualify; empty means it does. `events` are
// the events whose runs test a tree rather than a merge of a branch into its base (scripts/verify-heavy-ci-for-release.mjs
// accepts a nightly schedule run too).
export function evaluateRun(run, jobs, evidence = REQUIRED_EVIDENCE, events = ACCEPTED_EVENTS) {
  const problems = [];
  if (!events.includes(run.event)) {
    problems.push(`event is ${run.event}, and only ${events.join(" or ")} runs test this exact tree`);
  }
  if (run.status !== "completed") {
    problems.push(`still ${run.status}`);
    return problems;
  }
  if (run.conclusion !== "success") problems.push(`concluded ${run.conclusion}`);
  for (const want of evidence) {
    const matches = jobs.filter((job) => job.name === want.job);
    if (matches.length !== 1) {
      problems.push(matches.length === 0 ? `job "${want.job}" is missing` : `job "${want.job}" appears ${matches.length} times`);
      continue;
    }
    const [job] = matches;
    if (job.conclusion !== "success") {
      problems.push(`job "${want.job}" concluded ${job.conclusion}`);
      continue;
    }
    for (const stepName of want.steps) {
      const step = (job.steps || []).find((candidate) => candidate.name === stepName);
      if (!step) problems.push(`job "${want.job}" has no step "${stepName}"`);
      else if (step.conclusion !== "success") problems.push(`job "${want.job}" step "${stepName}" concluded ${step.conclusion}`);
    }
  }
  return problems;
}

/** Runs the GitHub CLI and parses its JSON answer; `execute` is execFileSync, which a test replaces. */
export function gh(args, execute = execFileSync) {
  return JSON.parse(execute("gh", args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }));
}

export function runJobs(repository, runId, ask = gh) {
  return ask(["run", "view", String(runId), "--repo", repository, "--json", "jobs"]).jobs;
}

function describe(run) {
  return `run ${run.databaseId} (${run.event}, ${run.status}${run.conclusion ? `/${run.conclusion}` : ""}, ${run.url})`;
}

/**
 * The release gate over `argv` and `env`; returns the exit code. `ask` is how `gh` is run (a test replaces it) and `headCommit` how
 * the commit is found when none is given.
 */
export function main(argv, env, { ask = gh, headCommit = () => execFileSync("git", ["rev-parse", "HEAD^{commit}"], { encoding: "utf8" }).trim(), log = console.log, error = console.error } = {}) {
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const repository = env.GITHUB_REPOSITORY;
  if (!repository) throw new Error("GITHUB_REPOSITORY must be set.");

  const runId = option("--run-id");
  if (runId) {
    const run = ask(["run", "view", runId, "--repo", repository, "--json", "databaseId,event,status,conclusion,headSha,url"]);
    const problems = evaluateRun(run, runJobs(repository, runId, ask));
    log(`${describe(run)} for ${run.headSha}:`);
    for (const problem of problems) log(`  - ${problem}`);
    log(problems.length ? "Would NOT satisfy the release gate." : "Would satisfy the release gate.");
    return problems.length ? 1 : 0;
  }

  const sha = option("--sha") || env.GITHUB_SHA || headCommit();
  if (!/^[0-9a-f]{40}$/.test(sha)) throw new Error(`Not a full commit SHA: ${sha}`);
  const tag = env.GITHUB_REF_NAME || "<tag>";
  log(`Release gate: ${CI_WORKFLOW} must already have passed on ${sha}.`);

  const runs = ask([
    "run", "list", "--repo", repository, "--workflow", CI_WORKFLOW, "--commit", sha, "--limit", "50",
    "--json", "databaseId,event,status,conclusion,headSha,url",
  ]).filter((run) => run.headSha === sha);
  const considered = runs.filter((run) => ACCEPTED_EVENTS.includes(run.event));
  const ignored = runs.length - considered.length;

  const verdicts = [];
  for (const run of considered.filter((candidate) => candidate.status === "completed")) {
    // A run that did not succeed is refused without asking for its jobs.
    const problems = run.conclusion === "success" ? evaluateRun(run, runJobs(repository, run.databaseId, ask)) : evaluateRun(run, [], []);
    if (problems.length === 0) {
      log(`PASS: ${describe(run)} passed ci-passed on ${sha}.`);
      return 0;
    }
    verdicts.push({ run, problems });
  }

  error(`::error::No ${CI_WORKFLOW} run proves ${sha}. The release will not publish.`);
  const pending = considered.filter((run) => run.status !== "completed");
  if (pending.length) {
    error(`CI is still running on this commit: ${pending.map(describe).join("; ")}`);
    error("Tags are made after CI is green. Wait for it to finish, then re-run this release.");
  }
  if (considered.length === 0) {
    error(`No push or workflow_dispatch run of ${CI_WORKFLOW} exists for this commit${ignored ? ` (${ignored} pull_request run(s) ignored)` : ""}.`);
  }
  for (const { run, problems } of verdicts) {
    error(`${describe(run)} does not qualify:`);
    for (const problem of problems) error(`  - ${problem}`);
  }
  error("To prove this commit, run the full CI on the tag (a manual run skips nothing), wait for it to pass,");
  error("then re-run this release:");
  error(`  gh workflow run ${CI_WORKFLOW} --repo ${repository} --ref ${tag}`);
  return 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    process.exitCode = main(process.argv.slice(2), process.env);
  } catch (failure) {
    console.error(`::error::${failure.message}`);
    process.exitCode = 1;
  }
}
