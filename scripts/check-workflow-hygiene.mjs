#!/usr/bin/env node
// Two rules for every workflow in .github/workflows, checked as text so nothing has to be installed:
//
//   * a top-level `permissions:` block that grants no write access, so a job gets write access only by asking
//     for it on the job itself; and
//   * `timeout-minutes` on every job (a job that calls a reusable workflow cannot set one; the called
//     workflow's own jobs must); and
//   * an output compared with a quoted string (`== 'true'`), never a bare boolean, which is never equal.
//
// A job with no timeout runs for six hours before GitHub stops it, and a Windows runner bills at twice the rate.
//
// Usage: node scripts/check-workflow-hygiene.mjs
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workflowDir = path.join(root, ".github", "workflows");
const failures = [];

for (const name of readdirSync(workflowDir).filter((file) => /\.ya?ml$/i.test(file))) {
  const relative = `.github/workflows/${name}`;
  const text = readFileSync(path.join(workflowDir, name), "utf8").replace(/\r\n/g, "\n");
  const lines = text.split("\n");

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
    continue;
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
}

if (failures.length) {
  console.error("Workflow hygiene check failed:");
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}
console.log("Every workflow has least-privilege top-level permissions, a timeout on every job, and compares outputs with quoted strings.");
