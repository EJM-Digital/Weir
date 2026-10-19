// node --test scripts/ci-minutes.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { formatReport, jobGroup, jobMinutes, jobsOf, listRuns, osOf, summarize } from "./ci-minutes.mjs";

const job = (name, labels, seconds, extra = {}) => ({
  name,
  labels,
  started_at: "2026-10-19T10:00:00Z",
  completed_at: new Date(Date.parse("2026-10-19T10:00:00Z") + seconds * 1000).toISOString(),
  ...extra,
});

test("each started minute of a job is billed", () => {
  assert.equal(jobMinutes(job("a", [], 1)), 1);
  assert.equal(jobMinutes(job("a", [], 60)), 1);
  assert.equal(jobMinutes(job("a", [], 61)), 2);
});

test("a job that never ran, was skipped or has not finished bills nothing", () => {
  assert.equal(jobMinutes({ name: "a", started_at: null, completed_at: null }), 0);
  assert.equal(jobMinutes({ name: "a", started_at: "2026-10-19T10:00:00Z", completed_at: null }), 0);
  assert.equal(jobMinutes(job("a", [], 0)), 0);
});

test("the runner labels name the operating system", () => {
  assert.equal(osOf(["ubuntu-latest"]), "linux");
  assert.equal(osOf(["windows-latest"]), "windows");
  assert.equal(osOf(["Windows", "X64"]), "windows");
  assert.equal(osOf(["macos-14"]), "macos");
  assert.equal(osOf(["self-hosted", "Windows"]), "self-hosted");
  assert.equal(osOf([]), "linux");
});

test("a matrix leg's parenthesis is dropped from its group", () => {
  assert.equal(jobGroup("contract / contract (auth, windows)"), "contract / contract");
  assert.equal(jobGroup("packaging / docker-smoke"), "packaging / docker-smoke");
});

const runs = [
  { name: "CI", event: "pull_request", jobs: [job("server-linux", ["ubuntu-latest"], 400), job("contract / contract (auth, windows)", ["windows-latest"], 300), job("contract / contract (jobs, windows)", ["windows-latest"], 130)] },
  { name: "CI", event: "push", jobs: [job("contract / contract (auth, windows)", ["windows-latest"], 59)] },
  { name: "Mutation", event: "schedule", jobs: [job("Mutate core-rules", ["windows-latest"], 3600), job("Select the targets", ["ubuntu-latest"], 5)] },
  { name: "CI", event: "pull_request", jobs: [job("tray", ["self-hosted", "Windows"], 600), { name: "skipped", labels: ["ubuntu-latest"], started_at: null, completed_at: null }] },
];

test("minutes are summed per operating system with Windows billed double", () => {
  const { os } = summarize(runs);
  const windows = os.find((row) => row.key === "windows");
  assert.equal(windows.windows, 5 + 3 + 1 + 60);
  assert.equal(os.find((row) => row.key === "linux").linux, 7 + 1);
  assert.equal(os.find((row) => row.key === "self-hosted")["self-hosted"], 10);
  assert.equal(os[0].key, "windows");
});

test("minutes are summed per workflow, per job group and per trigger, largest first", () => {
  const summary = summarize(runs);
  assert.deepEqual(summary.workflows.map((row) => row.key), ["Mutation", "CI"]);
  const contract = summary.jobs.find((row) => row.key === "CI: contract / contract");
  assert.equal(contract.windows, 9);
  assert.equal(contract.jobs, 3);
  assert.deepEqual(summary.events.map((row) => row.key), ["schedule", "pull_request", "push"]);
});

test("--legs keeps every matrix leg apart", () => {
  const keys = summarize(runs, { legs: true }).jobs.map((row) => row.key);
  assert.ok(keys.includes("CI: contract / contract (auth, windows)"));
  assert.ok(keys.includes("CI: contract / contract (jobs, windows)"));
});

test("the report names the totals, the windows share and a cut-off", () => {
  const text = formatReport(summarize(runs), { days: 7, top: 2, truncatedAt: 4 });
  assert.match(text, /last 7 days: 4 runs, 77 minutes, 146 billed/);
  assert.match(text, /Only the newest 4 runs were read/);
  assert.match(text, /By operating system\n.*linux\s+windows\s+self-host\s+billed\s+jobs/);
  assert.match(text, /By job \(the 2 that billed most\)/);
});

test("runs are listed page by page until a short page or the cap", () => {
  const page = (count, from) => ({ workflow_runs: Array.from({ length: count }, (_, i) => ({ id: from + i })) });
  const calls = [];
  const api = (args) => {
    calls.push(args[1]);
    return /page=1$/.test(args[1]) ? page(100, 1) : page(30, 101);
  };
  assert.equal(listRuns("o/r", "2026-10-13", 400, api).length, 130);
  assert.equal(calls.length, 2);
  assert.match(calls[0], /repos\/o\/r\/actions\/runs\?created=%3E%3D2026-10-13&per_page=100&page=1/);
  assert.equal(listRuns("o/r", "2026-10-13", 120, api).length, 120);
});

test("a run's jobs come from its own jobs listing", () => {
  const api = (args) => ({ jobs: [{ name: args[1] }] });
  assert.match(jobsOf("o/r", 42, api)[0].name, /repos\/o\/r\/actions\/runs\/42\/jobs/);
});
