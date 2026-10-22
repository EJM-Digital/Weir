#!/usr/bin/env node
// What CI cost in GitHub Actions minutes over the last N days, per workflow, per job and per operating system, so the cost
// can be watched (docs/ci-cost.md).
//
//   node scripts/ci-minutes.mjs [--days 7] [--repo owner/name] [--top 25] [--max-runs 400] [--json]
//
// It lists the repository's workflow runs created in the window (`gh api repos/<repo>/actions/runs`) and the jobs of each
// (`.../runs/<id>/jobs`), so it makes one request for every 100 runs and one for each run. The job lists are the budget: the
// token's 5,000 requests an hour are shared with everything else, so --max-runs stops at the newest N runs and says so.
//
// A job is billed per started minute, so each job's length is rounded up. Windows minutes cost twice a Linux minute and macOS
// ten times, so beside the minutes the report gives "billed" minutes in Linux terms. A self-hosted runner is not billed and
// is listed apart. A matrix leg's name loses its parenthesis, so the nine contract legs read as one line unless --legs is given.
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { REPOSITORY } from "./repository.mjs";

const MULTIPLIER = { linux: 1, windows: 2, macos: 10, "self-hosted": 0 };

/** The operating system a job's runner labels name. */
export function osOf(labels = []) {
  const names = labels.map((label) => String(label).toLowerCase());
  if (names.includes("self-hosted")) return "self-hosted";
  if (names.some((name) => name.startsWith("windows"))) return "windows";
  if (names.some((name) => name.startsWith("macos"))) return "macos";
  return "linux";
}

/** A job's billed length in whole minutes (each started minute counts); 0 for a job that never ran or has not finished. */
export function jobMinutes(job) {
  if (!job.started_at || !job.completed_at) return 0;
  const seconds = (Date.parse(job.completed_at) - Date.parse(job.started_at)) / 1000;
  return Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds / 60) : 0;
}

/** A job's name without its matrix legs: "contract / contract (auth, windows)" becomes "contract / contract". */
export function jobGroup(name) {
  return String(name).replace(/\s*\([^)]*\)\s*$/, "");
}

const add = (map, key, os, minutes) => {
  const row = map.get(key) ?? { key, linux: 0, windows: 0, macos: 0, "self-hosted": 0, jobs: 0 };
  row[os] += minutes;
  row.jobs += 1;
  map.set(key, row);
};

const billed = (row) => row.linux * MULTIPLIER.linux + row.windows * MULTIPLIER.windows + row.macos * MULTIPLIER.macos;
const total = (row) => row.linux + row.windows + row.macos;

/**
 * Sums minutes. `runs` is [{ name, event, jobs: [{ name, labels, started_at, completed_at }] }]. Returns the rows by operating
 * system, workflow, job and event, each sorted by billed minutes, largest first.
 */
export function summarize(runs, { legs = false } = {}) {
  const byOs = new Map();
  const byWorkflow = new Map();
  const byJob = new Map();
  const byEvent = new Map();
  for (const run of runs) {
    for (const job of run.jobs) {
      const minutes = jobMinutes(job);
      if (minutes === 0) continue;
      const os = osOf(job.labels);
      add(byOs, os, os, minutes);
      add(byWorkflow, run.name, os, minutes);
      add(byJob, `${run.name}: ${legs ? job.name : jobGroup(job.name)}`, os, minutes);
      add(byEvent, run.event, os, minutes);
    }
  }
  const sorted = (map) => [...map.values()].sort((a, b) => billed(b) - billed(a) || total(b) - total(a) || a.key.localeCompare(b.key));
  return { runs: runs.length, os: sorted(byOs), workflows: sorted(byWorkflow), jobs: sorted(byJob), events: sorted(byEvent) };
}

const pad = (text, width) => String(text).padEnd(width);
const num = (value, width = 10) => String(value).padStart(width);

/** The report as text: one table per grouping, with the Windows and Linux minutes and what they bill as. */
export function formatReport(summary, { days, top = 25, truncatedAt = null } = {}) {
  const lines = [];
  const grand = summary.os.reduce((sum, row) => ({ minutes: sum.minutes + total(row), billed: sum.billed + billed(row) }), { minutes: 0, billed: 0 });
  lines.push(`GitHub Actions minutes over the last ${days} days: ${summary.runs} runs, ${grand.minutes} minutes, ${grand.billed} billed in Linux minutes (Windows counts double).`);
  if (truncatedAt) lines.push(`Only the newest ${truncatedAt} runs were read (--max-runs); older runs in the window are not counted.`);
  const table = (title, rows, width) => {
    lines.push("", title, `${pad("", width)}${num("linux")}${num("windows")}${num("self-host")}${num("billed")}${num("jobs")}`);
    for (const row of rows) lines.push(`${pad(row.key.length > width - 2 ? `${row.key.slice(0, width - 3)}...` : row.key, width)}${num(row.linux)}${num(row.windows)}${num(row["self-hosted"])}${num(billed(row))}${num(row.jobs)}`);
  };
  table("By operating system", summary.os, 40);
  table("By workflow", summary.workflows, 40);
  table(`By job (the ${top} that billed most)`, summary.jobs.slice(0, top), 62);
  table("By trigger", summary.events, 40);
  return lines.join("\n");
}

// Retried twice: GitHub answers an occasional 5xx, and the last failure says why.
function ghJson(args, attempts = 3) {
  try {
    return JSON.parse(execFileSync("gh", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] }));
  } catch (error) {
    if (attempts > 1) return ghJson(args, attempts - 1);
    throw new Error(`gh ${args.join(" ")} failed: ${String(error.stderr || error.message).trim()}`);
  }
}

/** The runs created since `since` (a date), newest first, at most `maxRuns`. */
export function listRuns(repository, since, maxRuns, api = ghJson) {
  const runs = [];
  for (let page = 1; runs.length < maxRuns; page += 1) {
    const listing = api(["api", `repos/${repository}/actions/runs?created=%3E%3D${since}&per_page=100&page=${page}`]);
    runs.push(...listing.workflow_runs);
    if (listing.workflow_runs.length < 100) break;
  }
  return runs.slice(0, maxRuns);
}

export function jobsOf(repository, runId, api = ghJson) {
  return api(["api", `repos/${repository}/actions/runs/${runId}/jobs?per_page=100&filter=all`]).jobs;
}

function main() {
  const argv = process.argv.slice(2);
  const option = (flag, fallback) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : fallback);
  const days = Number(option("--days", "7"));
  const top = Number(option("--top", "25"));
  const maxRuns = Number(option("--max-runs", "400"));
  const repository = option("--repo", REPOSITORY);
  if (!Number.isInteger(days) || days < 1) throw new Error("--days must be a whole number of days, 1 or more.");
  if (!Number.isInteger(maxRuns) || maxRuns < 1) throw new Error("--max-runs must be a whole number, 1 or more.");

  const since = new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString().slice(0, 10);
  const listed = listRuns(repository, since, maxRuns);
  // A run whose jobs cannot be read is left out and counted, so one bad answer does not lose the other runs.
  const unreadable = [];
  const runs = [];
  for (const run of listed) {
    try {
      runs.push({ name: run.name, event: run.event, jobs: jobsOf(repository, run.id) });
    } catch (error) {
      unreadable.push(run.id);
      console.error(error.message);
    }
  }
  const summary = summarize(runs, { legs: argv.includes("--legs") });
  if (argv.includes("--json")) console.log(JSON.stringify({ days, since, ...summary }, null, 2));
  else console.log(formatReport(summary, { days, top, truncatedAt: listed.length === maxRuns ? maxRuns : null }));
  if (unreadable.length > 0) console.error(`${unreadable.length} run(s) could not be read and are not counted: ${unreadable.join(", ")}`);
  console.error(`(${Math.max(1, Math.ceil(listed.length / 100)) + listed.length} API requests)`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
