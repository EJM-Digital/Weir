#!/usr/bin/env node
// Downloads the server coverage of the last CI runs, for scripts/check-coverage-flips.mjs.
//
//   node scripts/fetch-coverage-runs.mjs <folder> [--runs 30] [--repo owner/name]
//
// It picks the newest completed runs of ci.yml that still have both the `coverage-server-linux` and the `coverage-server-windows`
// artifact, and downloads each into <folder>/<run id>/<artifact name>, the layout the coverage job reads, with a run.json beside them
// naming the commit the run tested and the tree id of apps/server there (git rev-parse <sha>:apps/server, fetching the commit when
// needed), which is how scripts/check-coverage-flips.mjs knows which runs tested the same server code. A run that did not
// pass is kept only when its two server test jobs passed: a test that failed leaves code unreached, which is not a flip. A run
// already downloaded is not downloaded again.
//
// How many runs: CI keeps these artifacts for one day (retention-days in ci.yml), so the pool is the runs of the last day, 31 on
// the busiest day measured (28 October 2026). The flips found in those 31 runs were missed by 1 or 2 of the 19 to 24 runs that listed
// them, about 5 in 100. A point missed that often is seen in the newest 10 runs 4 times in 10 and in the newest 30 about 8 times
// in 10 (1 - 0.95^N), and the daily schedule looks again with a new day, so the default is 30: more would not be there to download.
// (docs/coverage/README.md, "Runs that reach different code").
//
// GitHub allows 5,000 requests an hour, shared with everything else. The listing takes 3 to 5 requests, a run that did not pass 1
// more, and each of a run's two downloads 2 (the run's artifacts, then the file), one after another with a pause between runs, so
// 30 runs cost about 130.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { ghJson } from "./ci-minutes.mjs";
import { LEGS, RUN_RECORD } from "./check-coverage-flips.mjs";
import { REPOSITORY } from "./repository.mjs";

export const WORKFLOW = "ci.yml";
export const DEFAULT_RUNS = 30;
/** The pages (of 100) read for artifacts and for runs; a day of CI is far fewer. */
const MAX_PAGES = 5;
const PAUSE_MS = 300;

/** The jobs of ci.yml whose success means the unit tests of that system ran to the end. */
export const SERVER_JOBS = ["server-linux", "server-windows"];

const downloadWithGh = (repository, run, artifact, folder) => execFileSync("gh", ["run", "download", String(run), "-R", repository, "-n", artifact, "-D", folder], { stdio: ["ignore", "pipe", "pipe"] });
const runGit = (args) => execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
export const pause = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

/** The tree id of apps/server at a commit: what two runs share when they tested the same server code. Fetches the commit when this checkout lacks it. */
export function treeOf(sha, git = runGit) {
  try {
    return git(["rev-parse", `${sha}:apps/server`]);
  } catch {
    git(["fetch", "--no-tags", "--depth=1", "origin", sha]);
    return git(["rev-parse", `${sha}:apps/server`]);
  }
}

/** Every page of a listing (a route that already has a query), newest first, up to MAX_PAGES; `items` picks the list out of a page. */
function pages(api, route, items) {
  const found = [];
  for (let page = 1; page <= MAX_PAGES; page += 1) {
    const listed = items(api(["api", `${route}&per_page=100&page=${page}`]));
    found.push(...listed);
    if (listed.length < 100) break;
  }
  return found;
}

/** The ids of the runs that still hold an artifact of this name. */
export function runsWithArtifact(repository, name, api = ghJson) {
  return new Set(pages(api, `repos/${repository}/actions/artifacts?name=${name}`, (page) => page.artifacts).filter((artifact) => !artifact.expired).map((artifact) => artifact.workflow_run.id));
}

/** Whether a run that did not pass still ran both server test jobs to a pass. */
export function serverJobsPassed(repository, run, api = ghJson) {
  const jobs = api(["api", `repos/${repository}/actions/runs/${run}/jobs?per_page=100&filter=latest`]).jobs;
  return SERVER_JOBS.every((name) => jobs.some((job) => job.name === name && job.conclusion === "success"));
}

/** The newest `count` runs (id and head commit) that have both server coverage artifacts and are fit to compare, newest first. */
export function pickRuns(repository, count, api = ghJson) {
  const held = LEGS.map(({ artifact }) => runsWithArtifact(repository, artifact, api));
  const picked = [];
  for (const run of pages(api, `repos/${repository}/actions/workflows/${WORKFLOW}/runs?status=completed`, (page) => page.workflow_runs)) {
    if (picked.length === count) break;
    if (!held.every((ids) => ids.has(run.id))) continue;
    if (run.conclusion === "success" || serverJobsPassed(repository, run.id, api)) picked.push({ id: run.id, sha: run.head_sha });
  }
  return picked;
}

/** Downloads the runs `argv` asks for into its folder; returns the exit code. */
export async function main(argv, { api = ghJson, download = downloadWithGh, wait = pause, log = console.log, tree = treeOf } = {}) {
  const option = (flag, fallback) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : fallback);
  const folder = argv[0];
  const count = Number(option("--runs", String(DEFAULT_RUNS)));
  const repository = option("--repo", REPOSITORY);
  if (!folder || folder.startsWith("--")) throw new Error("Usage: fetch-coverage-runs.mjs <folder> [--runs 30] [--repo owner/name]");
  if (!Number.isInteger(count) || count < 1) throw new Error("--runs must be a whole number, 1 or more.");

  const runs = pickRuns(repository, count, api);
  mkdirSync(folder, { recursive: true });
  let downloaded = 0;
  for (const { id, sha } of runs) {
    for (const { artifact } of LEGS) {
      const target = path.join(folder, String(id), artifact);
      if (existsSync(target)) continue;
      download(repository, id, artifact, target);
      downloaded += 1;
    }
    // What the run tested, so the check needs no network: the commit and the tree id of apps/server at it.
    const record = path.join(folder, String(id), RUN_RECORD);
    mkdirSync(path.dirname(record), { recursive: true });
    if (!existsSync(record)) writeFileSync(record, JSON.stringify({ sha, tree: tree(sha) }));
    await wait(PAUSE_MS);
  }
  log(`${runs.length} run(s) of ${WORKFLOW} have both server coverage artifacts and are in ${folder} (${downloaded} artifact(s) downloaded).`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (failure) => { console.error(failure.message); process.exitCode = 1; });
}
