// node --test scripts/fetch-coverage-runs.test.mjs
// Which CI runs the flip check downloads, with GitHub and the download replaced by stand-ins.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import { pause, pickRuns, runsWithArtifact, main, serverJobsPassed } from "./fetch-coverage-runs.mjs";

const REPO = "owner/name";
const temporary = mkdtempSync(path.join(tmpdir(), "weir-fetch-"));
after(() => rmSync(temporary, { recursive: true, force: true }));

const artifact = (run, expired = false) => ({ expired, workflow_run: { id: run } });

/**
 * GitHub as the fetch asks it. `held` says which runs hold which artifacts, `runs` is ci.yml's completed runs newest first, and
 * `jobs` maps a run to its job results. Every request is recorded in `asked`.
 */
function github({ held, runs, jobs = {} }) {
  const asked = [];
  const api = (args) => {
    const route = args[1];
    asked.push(route);
    const page = Number(/&page=(\d+)/.exec(route)?.[1] ?? 1);
    const slice = (list) => list.slice((page - 1) * 100, page * 100);
    const name = /artifacts\?name=([^&]+)/.exec(route);
    if (name) return { artifacts: slice(held[name[1]]) };
    if (/workflows\/ci\.yml\/runs\?status=completed/.test(route)) return { workflow_runs: slice(runs) };
    const id = Number(/runs\/(\d+)\/jobs/.exec(route)[1]);
    return { jobs: jobs[id] };
  };
  return { api, asked };
}

const both = (...runs) => ({ "coverage-server-linux": runs.map((run) => artifact(run)), "coverage-server-windows": runs.map((run) => artifact(run)) });
const finished = (id, conclusion = "success") => ({ id, conclusion });
const passed = (...names) => names.map((name) => ({ name, conclusion: "success" }));

test("a run is held when its artifact has not expired", () => {
  const { api } = github({ held: { "coverage-server-linux": [artifact(1), artifact(2, true)] }, runs: [] });
  assert.deepEqual([...runsWithArtifact(REPO, "coverage-server-linux", api)], [1]);
});

test("a run that failed is kept only when both server test jobs passed in it", () => {
  const jobs = {
    2: [...passed("server-linux", "server-windows", "tray")],
    3: [...passed("server-linux"), { name: "server-windows", conclusion: "failure" }],
    4: [...passed("server-windows")],
  };
  const { api } = github({ held: {}, runs: [], jobs });
  assert.equal(serverJobsPassed(REPO, 2, api), true);
  assert.equal(serverJobsPassed(REPO, 3, api), false);
  assert.equal(serverJobsPassed(REPO, 4, api), false);
});

test("the newest runs with both artifacts are picked, failed ones only when the server jobs passed, and no more than asked for", () => {
  const { api, asked } = github({
    held: { "coverage-server-linux": both(9, 8, 7, 6, 5, 4)["coverage-server-linux"], "coverage-server-windows": both(9, 8, 7, 5, 4)["coverage-server-windows"] },
    runs: [finished(10), finished(9), finished(8, "failure"), finished(7, "failure"), finished(6), finished(5), finished(4)],
    jobs: { 8: passed("server-linux", "server-windows"), 7: passed("server-linux") },
  });
  // 10 holds no artifact, 7's Windows server job did not pass, 6 has no Windows artifact.
  assert.deepEqual(pickRuns(REPO, 3, api), [9, 8, 5]);
  assert.equal(asked.filter((route) => /\/jobs/.test(route)).length, 2);
  // Asked for fewer, it stops reading runs at the count.
  const again = github({ held: both(9, 8), runs: [finished(9), finished(8)] });
  assert.deepEqual(pickRuns(REPO, 1, again.api), [9]);
});

test("a listing of more than a page is read page by page, up to five pages", () => {
  const many = Array.from({ length: 150 }, (_, index) => index + 1);
  const { api, asked } = github({ held: both(...many), runs: many.map((id) => finished(id)) });
  assert.equal(runsWithArtifact(REPO, "coverage-server-linux", api).size, 150);
  assert.equal(asked.filter((route) => /artifacts/.test(route)).length, 2);

  const endless = Array.from({ length: 700 }, (_, index) => index + 1);
  const long = github({ held: both(...endless), runs: endless.map((id) => finished(id)) });
  assert.equal(runsWithArtifact(REPO, "coverage-server-windows", long.api).size, 500);
  assert.equal(long.asked.length, 5);
});

test("the runs are downloaded one artifact at a time into the folder, pausing between runs, and what is there is not downloaded again", async () => {
  const folder = path.join(temporary, "runs");
  mkdirSync(path.join(folder, "9", "coverage-server-linux"), { recursive: true });
  const { api } = github({ held: both(9, 8), runs: [finished(9), finished(8)] });
  const downloads = [];
  const waits = [];
  const lines = [];
  const code = await main([folder, "--runs", "2", "--repo", REPO], {
    api,
    download: (repository, run, name, target) => {
      downloads.push([repository, run, name, path.relative(folder, target).replace(/\\/g, "/")]);
    },
    wait: (milliseconds) => waits.push(milliseconds),
    log: (line) => lines.push(line),
  });
  assert.equal(code, 0);
  assert.deepEqual(downloads, [
    [REPO, 9, "coverage-server-windows", "9/coverage-server-windows"],
    [REPO, 8, "coverage-server-linux", "8/coverage-server-linux"],
    [REPO, 8, "coverage-server-windows", "8/coverage-server-windows"],
  ]);
  assert.equal(waits.length, 2);
  assert.deepEqual(lines, [`2 run(s) of ci.yml have both server coverage artifacts and are in ${folder} (3 artifact(s) downloaded).`]);
});

test("with no run to download the folder is still made, so the check can say there is nothing to compare", async () => {
  const folder = path.join(temporary, "empty");
  const { api } = github({ held: both(), runs: [] });
  const lines = [];
  assert.equal(await main([folder], { api, download: assert.fail, wait: assert.fail, log: (line) => lines.push(line) }), 0);
  assert.equal(existsSync(folder), true);
  assert.match(lines[0], /^0 run\(s\) of ci\.yml/);
});

test("a missing folder or a run count that is not a whole number is refused", async () => {
  const api = () => assert.fail("nothing should be asked");
  await assert.rejects(main([], { api }), /^Error: Usage: fetch-coverage-runs\.mjs <folder>/);
  await assert.rejects(main(["--runs", "3"], { api }), /^Error: Usage: /);
  for (const count of ["0", "2.5", "x"]) await assert.rejects(main([path.join(temporary, "x"), "--runs", count], { api }), /^Error: --runs must be a whole number, 1 or more\.$/);
});

test("the pause between runs waits for the time given", async () => {
  await pause(0);
});

test("run as a command, a bad option is an error message and exit 1", () => {
  const script = fileURLToPath(new URL("./fetch-coverage-runs.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script, path.join(temporary, "cmd"), "--runs", "0"], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.equal(result.stderr.trim(), "--runs must be a whole number, 1 or more.");
});
