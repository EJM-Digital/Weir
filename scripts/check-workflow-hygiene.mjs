#!/usr/bin/env node
// Rules for every workflow in .github/workflows, checked as text so nothing has to be installed:
//
//   * a top-level `permissions:` block that grants no write access, so a job gets write access only by asking
//     for it on the job itself; and
//   * `timeout-minutes` on every job (a job that calls a reusable workflow cannot set one; the called
//     workflow's own jobs must); and
//   * an output compared with a quoted string (`== 'true'`), never a bare boolean, which is never equal; and
//   * no test step with `continue-on-error: true`, so a failed test fails the job and no later step can turn it green
//     (an intermittent test is a bug to root-cause, not one to run twice); and
//   * nothing that costs a lot runs on every pull request (docs/ci-cost.md): a workflow triggered by pull_request runs no
//     mutation testing, and each of its Windows jobs (a Windows runner, a Windows scenario shard, or a job named for
//     Windows) carries the path filter, a job-level `if:` on a `needs.changes.outputs.run_*` flag, unless the workflow's own
//     `pull_request:` trigger lists `paths:`, which keeps the whole workflow off a pull request that touches none of them.
//
// A job with no timeout runs for six hours before GitHub stops it, and a Windows runner bills at twice the rate.
//
// Usage: node scripts/check-workflow-hygiene.mjs
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

// A step runs tests when its name or its command says so: `dotnet test`, `npm run test`, `node --test`, vitest, Playwright.
const TEST_STEP = /\b(?:tests?|vitest|playwright|e2e)\b/i;

/** The problems in one workflow's text; `relative` names the file in each message. */
export function findProblems(relative, source) {
  const failures = [];
  const lines = source.replace(/\r\n/g, "\n").split("\n");

  const permissionsAt = lines.findIndex((line) => /^permissions:/.test(line));
  if (permissionsAt < 0) {
    failures.push(`${relative}: no top-level permissions: block (say \`permissions:\n  contents: read\`).`);
  } else {
    if (/^permissions:\s*write-all/.test(lines[permissionsAt])) failures.push(`${relative}: top-level permissions is write-all.`);
    for (let i = permissionsAt + 1; i < lines.length && /^\s+\S/.test(lines[i]); i += 1) {
      if (/:\s*write\b/.test(lines[i].split("#")[0])) failures.push(`${relative}: top-level permissions grants write access (${lines[i].trim()}); grant it on the one job that needs it.`);
    }
  }

  const jobsAt = lines.findIndex((line) => /^jobs:/.test(line));
  if (jobsAt < 0) {
    failures.push(`${relative}: no jobs: section.`);
    return failures;
  }
  let job = null;
  const finish = () => {
    if (job && !job.timeout && !job.callsWorkflow) failures.push(`${relative}: job "${job.id}" has no timeout-minutes.`);
  };
  for (let i = jobsAt + 1; i < lines.length; i += 1) {
    const opener = lines[i].match(/^ {2}([A-Za-z0-9_-]+):\s*(#.*)?$/);
    if (opener) {
      finish();
      job = { id: opener[1], timeout: false, callsWorkflow: false };
    } else if (job) {
      if (/^ {4}timeout-minutes:/.test(lines[i])) job.timeout = true;
      if (/^ {4}uses:/.test(lines[i])) job.callsWorkflow = true;
    }
  }
  finish();

  // Step and job outputs are always strings, so `outputs.x == true` compares a string with a boolean and is never
  // true: the step it guards is silently skipped.
  lines.forEach((line, index) => {
    const unquoted = line.split("#")[0].match(/outputs\.[A-Za-z0-9_-]+\s*[!=]=\s*(true|false)\b/);
    if (unquoted) failures.push(`${relative}:${index + 1}: outputs are strings; compare with '${unquoted[1]}' in quotes.`);
  });

  failures.push(...findTolerantTestSteps(relative, lines));
  failures.push(...findUnfilteredHeavyJobs(relative, lines));
  return failures;
}

// A Windows runner bills at twice the Linux rate and a mutation run takes hours, so a pull request may start them only
// through a path filter that says its change needs them. The workflow-level trigger is read from `on:`, and each job is
// judged by its id, its runner and what it runs.
const WINDOWS_SHARD = /scenarios-in-ci\.mjs[^\n]*\bwindows-(?:install|update|tray|files)\b|shard:\s*\[\s*windows-/;
const PATH_FILTERED = /^ {4}if:.*needs\.changes\.outputs\.run_[A-Za-z0-9_]+\s*==\s*'true'/m;

function findUnfilteredHeavyJobs(relative, lines) {
  const failures = [];
  const onAt = lines.findIndex((line) => /^on:/.test(line));
  const triggers = [];
  for (let i = onAt + 1; onAt >= 0 && i < lines.length && !/^\S/.test(lines[i]); i += 1) triggers.push(lines[i].split("#")[0]);
  const pullRequestAt = triggers.findIndex((line) => /^ {2}pull_request(?:_target)?:/.test(line));
  if (pullRequestAt < 0) return failures;
  // The trigger's own block: the lines under it that are indented deeper than the trigger itself.
  let blockEnd = pullRequestAt + 1;
  while (blockEnd < triggers.length && /^ {3,}\S/.test(triggers[blockEnd])) blockEnd += 1;
  const triggerPathFiltered = triggers.slice(pullRequestAt + 1, blockEnd).some((line) => /^ {4}paths:\s*$/.test(line));

  const code = lines.map((line) => line.split("#")[0]);
  if (code.some((line) => /\bstryker\b/i.test(line))) {
    failures.push(`${relative}: runs mutation testing and is triggered by a pull request. Mutation runs weekly, by hand and before a release only (docs/ci-cost.md).`);
  }

  const jobsAt = lines.findIndex((line) => /^jobs:/.test(line));
  let job = null;
  const finish = () => {
    if (!job) return;
    const body = job.lines.join("\n");
    const windows = /windows/i.test(job.id) || /^ {4}runs-on:.*windows/im.test(body) || WINDOWS_SHARD.test(body);
    if (windows && !triggerPathFiltered && !PATH_FILTERED.test(body)) {
      failures.push(`${relative}: job "${job.id}" runs on Windows and the workflow is triggered by a pull request, but the job has no path filter. Give it \`if: needs.changes.outputs.run_<name> == 'true'\` (docs/ci-cost.md).`);
    }
  };
  for (let i = jobsAt + 1; jobsAt >= 0 && i < lines.length; i += 1) {
    const opener = code[i].match(/^ {2}([A-Za-z0-9_-]+):\s*$/);
    if (opener) {
      finish();
      job = { id: opener[1], lines: [] };
    } else if (job) {
      job.lines.push(code[i]);
    }
  }
  finish();
  return failures;
}

// A step starts at a `- ` under `steps:` and runs to the next step or the next key of its job. It is a test step when
// its name or its `run:` text says it runs tests, and `continue-on-error: true` on it lets the job pass whatever they did.
function findTolerantTestSteps(relative, lines) {
  const failures = [];
  let step = null;
  const finish = () => {
    if (!step) return;
    const body = step.lines.map((line) => line.split("#")[0]).join("\n");
    if (/^\s*(?:- )?continue-on-error:\s*true\b/m.test(body)) {
      const name = body.match(/^\s*(?:- )?name:\s*(.+)$/m)?.[1] ?? "";
      const commands = [...body.matchAll(/^\s*(?:- )?run:\s*(.*(?:\n {10,}.*)*)/gm)].map((match) => match[1]).join("\n");
      if (TEST_STEP.test(name) || TEST_STEP.test(commands)) {
        failures.push(`${relative}:${step.at + 1}: the test step "${name || "(unnamed)"}" has continue-on-error: true, so a failed test would not fail the job. Remove it; an intermittent test is a bug to fix, not to run twice.`);
      }
    }
    step = null;
  };
  lines.forEach((line, index) => {
    if (/^ {6}- /.test(line)) {
      finish();
      step = { at: index, lines: [line] };
    } else if (step && /^ {0,5}\S/.test(line)) {
      finish();
    } else if (step) {
      step.lines.push(line);
    }
  });
  finish();
  return failures;
}

function main() {
  const root = rootOf(import.meta.url);
  const workflowDir = path.join(root, ".github", "workflows");
  const failures = readdirSync(workflowDir)
    .filter((file) => /\.ya?ml$/i.test(file))
    .flatMap((name) => findProblems(`.github/workflows/${name}`, readFileSync(path.join(workflowDir, name), "utf8")));

  if (failures.length) {
    console.error("Workflow hygiene check failed:");
    for (const failure of failures) console.error(`- ${failure}`);
    process.exit(1);
  }
  console.log("Every workflow has least-privilege top-level permissions, a timeout on every job, compares outputs with quoted strings, lets no test step continue on error, and starts no mutation run or unfiltered Windows job on a pull request.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main();
}
