// node --test scripts/verify-ci-for-release.test.mjs
// The release gate's judgement of one CI run, on hand-built run and job objects.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { evaluateRun, main } from "./verify-ci-for-release.mjs";

const passedJobs = [
  { name: "changes", conclusion: "success", steps: [] },
  { name: "ci-passed", conclusion: "success", steps: [{ name: "Every job that was due passed", conclusion: "success" }] },
];
const run = (overrides = {}) => ({ event: "push", status: "completed", conclusion: "success", ...overrides });

test("a completed push run whose ci-passed passed qualifies", () => {
  assert.deepEqual(evaluateRun(run(), passedJobs), []);
});

test("a manual run qualifies too", () => {
  assert.deepEqual(evaluateRun(run({ event: "workflow_dispatch" }), passedJobs), []);
});

test("a pull_request run never qualifies", () => {
  assert.match(evaluateRun(run({ event: "pull_request" }), passedJobs).join("\n"), /only push or workflow_dispatch/);
});

test("a run that is still going, failed or was cancelled does not qualify", () => {
  assert.match(evaluateRun(run({ status: "in_progress", conclusion: null }), passedJobs).join("\n"), /still in_progress/);
  assert.match(evaluateRun(run({ conclusion: "failure" }), passedJobs).join("\n"), /concluded failure/);
  assert.match(evaluateRun(run({ conclusion: "cancelled" }), passedJobs).join("\n"), /concluded cancelled/);
});

test("a green run without a passing ci-passed job or verdict step does not qualify", () => {
  assert.match(evaluateRun(run(), []).join("\n"), /job "ci-passed" is missing/);
  const failedJob = [{ name: "ci-passed", conclusion: "failure", steps: [] }];
  assert.match(evaluateRun(run(), failedJob).join("\n"), /job "ci-passed" concluded failure/);
  const noStep = [{ name: "ci-passed", conclusion: "success", steps: [{ name: "Something else", conclusion: "success" }] }];
  assert.match(evaluateRun(run(), noStep).join("\n"), /no step "Every job that was due passed"/);
  const twice = [...passedJobs, passedJobs[1]];
  assert.match(evaluateRun(run(), twice).join("\n"), /job "ci-passed" appears 2 times/);
  const failedStep = [{ name: "ci-passed", conclusion: "success", steps: [{ name: "Every job that was due passed", conclusion: "failure" }] }];
  assert.match(evaluateRun(run(), failedStep).join("\n"), /step "Every job that was due passed" concluded failure/);
  assert.match(evaluateRun(run(), [{ name: "ci-passed", conclusion: "success" }]).join("\n"), /no step/);
});

// The command, with `gh` answered from a table of runs.
const SHA = "a".repeat(40);
const RUN_URL = "https://example.com/run";

function gate(argv, runs, { env = {}, jobs = passedJobs, head } = {}) {
  const log = [];
  const error = [];
  const asked = [];
  const ask = (args) => {
    asked.push(args);
    if (args.includes("jobs")) return { jobs };
    if (args[1] === "view") return runs[0];
    return runs;
  };
  const code = main(argv, { GITHUB_REPOSITORY: "owner/repo", GITHUB_SHA: SHA, ...env }, { ask, headCommit: () => head, log: (line) => log.push(line), error: (line) => error.push(line) });
  return { code, log, error, asked };
}

const ciRun = (overrides = {}) => ({ databaseId: 7, event: "push", status: "completed", conclusion: "success", headSha: SHA, url: RUN_URL, ...overrides });

test("a successful run of the commit lets the release go, without listing other runs' jobs", () => {
  const result = gate([], [ciRun({ databaseId: 1, conclusion: "failure" }), ciRun()]);
  assert.equal(result.code, 0);
  assert.deepEqual(result.log, [`Release gate: ci.yml must already have passed on ${SHA}.`, `PASS: run 7 (push, completed/success, ${RUN_URL}) passed ci-passed on ${SHA}.`]);
  assert.equal(result.asked.filter((args) => args.includes("jobs")).length, 1);
});

test("no run, a pull request run, a running run and a failed run each stop the release, saying why", () => {
  const none = gate([], [], { env: { GITHUB_REF_NAME: "v1.2.3" } });
  assert.equal(none.code, 1);
  assert.match(none.error.join("\n"), /No ci\.yml run proves a{40}\. The release will not publish\./);
  assert.match(none.error.join("\n"), /No push or workflow_dispatch run of ci\.yml exists for this commit\.\n/);
  assert.match(none.error.join("\n"), /gh workflow run ci\.yml --repo owner\/repo --ref v1\.2\.3/);

  const ignored = gate([], [ciRun({ event: "pull_request" }), ciRun({ headSha: "b".repeat(40) })]);
  assert.match(ignored.error.join("\n"), /\(1 pull_request run\(s\) ignored\)/);
  assert.match(ignored.error.join("\n"), /--ref <tag>/);

  const running = gate([], [ciRun({ status: "in_progress", conclusion: null })]);
  assert.match(running.error.join("\n"), /CI is still running on this commit: run 7 \(push, in_progress, /);
  assert.match(running.error.join("\n"), /Wait for it to finish/);

  const failed = gate([], [ciRun({ conclusion: "failure" })]);
  assert.match(failed.error.join("\n"), /run 7 \(push, completed\/failure, .*\) does not qualify:\n {2}- concluded failure/);
  assert.equal(failed.asked.filter((args) => args.includes("jobs")).length, 0);

  const unproven = gate([], [ciRun()], { jobs: [] });
  assert.match(unproven.error.join("\n"), /job "ci-passed" is missing/);
});

test("the commit comes from --sha, then GITHUB_SHA, then the checked-out commit, and must be a full hash", () => {
  assert.match(gate(["--sha", "c".repeat(40)], [], { env: { GITHUB_SHA: "" } }).log[0], /c{40}/);
  assert.match(gate([], [], { env: { GITHUB_SHA: "" }, head: "d".repeat(40) }).log[0], /d{40}/);
  assert.throws(() => gate(["--sha", "abc"], []), /Not a full commit SHA: abc/);
});

test("a run named by --run-id is judged whatever its event, and says whether it would satisfy the gate", () => {
  const good = gate(["--run-id", "7"], [ciRun({ event: "workflow_dispatch" })]);
  assert.equal(good.code, 0);
  assert.deepEqual(good.log, [`run 7 (workflow_dispatch, completed/success, ${RUN_URL}) for ${SHA}:`, "Would satisfy the release gate."]);
  const bad = gate(["--run-id", "7"], [ciRun({ event: "pull_request" })]);
  assert.equal(bad.code, 1);
  assert.match(bad.log.join("\n"), /- event is pull_request/);
  assert.match(bad.log.at(-1), /Would NOT satisfy the release gate\./);
});

test("the repository must be named", () => {
  assert.throws(() => main([], {}, {}), /GITHUB_REPOSITORY must be set\./);
});

test("run as a command without a repository it says so and exits 1", () => {
  const result = spawnSync(process.execPath, [fileURLToPath(new URL("./verify-ci-for-release.mjs", import.meta.url))], { encoding: "utf8", env: { PATH: process.env.PATH } });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /::error::GITHUB_REPOSITORY must be set\./);
});
