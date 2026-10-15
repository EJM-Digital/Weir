#!/usr/bin/env node
// Rules for every workflow in .github/workflows, checked as text so nothing has to be installed:
//
//   * a top-level `permissions:` block that grants no write access, so a job gets write access only by asking
//     for it on the job itself; and
//   * `timeout-minutes` on every job (a job that calls a reusable workflow cannot set one; the called
//     workflow's own jobs must); and
//   * an output compared with a quoted string (`== 'true'`), never a bare boolean, which is never equal; and
//   * no test step with `continue-on-error: true`, so a failed test fails the job and no later step can turn it green
//     (an intermittent test is a bug to root-cause, not one to run twice).
//
// A job with no timeout runs for six hours before GitHub stops it, and a Windows runner bills at twice the rate.
//
// Usage: node scripts/check-workflow-hygiene.mjs
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

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
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const workflowDir = path.join(root, ".github", "workflows");
  const failures = readdirSync(workflowDir)
    .filter((file) => /\.ya?ml$/i.test(file))
    .flatMap((name) => findProblems(`.github/workflows/${name}`, readFileSync(path.join(workflowDir, name), "utf8")));

  if (failures.length) {
    console.error("Workflow hygiene check failed:");
    for (const failure of failures) console.error(`- ${failure}`);
    process.exit(1);
  }
  console.log("Every workflow has least-privilege top-level permissions, a timeout on every job, compares outputs with quoted strings, and lets no test step continue on error.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main();
}
