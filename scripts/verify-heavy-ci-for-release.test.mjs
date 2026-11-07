// node --test scripts/verify-heavy-ci-for-release.test.mjs
// The release gate for the Windows legs, judged on hand-built runs, jobs and git history.
import assert from "node:assert/strict";
import { test } from "node:test";

import { PROOFS, findProof, runGate } from "./verify-heavy-ci-for-release.mjs";

const TAG = "c".repeat(40);
const OLD = "a".repeat(40);
const OLDER = "b".repeat(40);

// History newest first: TAG, OLD, OLDER. `changes` maps "<base>" to the paths that differ between it and TAG.
function fakeGit(changes = {}) {
  return (args) => {
    if (args[0] === "rev-list") return [TAG, OLD, OLDER].join("\n");
    if (args[0] === "merge-base") return "";
    if (args[0] === "diff") return (changes[args.at(-2)] ?? []).join("\0");
    throw new Error(`unexpected git ${args.join(" ")}`);
  };
}

const run = (overrides = {}) => ({ databaseId: 1, event: "push", status: "completed", conclusion: "success", url: "https://example.test/1", ...overrides });
const windowsJobs = [
  { name: "ci-passed", conclusion: "success", steps: [
    { name: "Every job that was due passed", conclusion: "success" },
    { name: "The Windows legs ran and passed", conclusion: "success" },
  ] },
];
const windowsSkippedJobs = [
  { name: "ci-passed", conclusion: "success", steps: [
    { name: "Every job that was due passed", conclusion: "success" },
    { name: "The Windows legs ran and passed", conclusion: "skipped" },
  ] },
];

const find = (name, { runs = {}, jobs = windowsJobs, changes = {} } = {}) =>
  findProof({ proof: PROOFS[name], sha: TAG, runsOf: (commit) => runs[commit] ?? [], jobsOf: (candidate) => candidate.jobs ?? jobs, git: fakeGit(changes) });

test("a push run on the tagged commit whose Windows step passed is the proof", () => {
  const found = find("windows", { runs: { [TAG]: [run()] } });
  assert.equal(found.ok, true);
  assert.equal(found.carried, false);
});

test("a nightly run counts for Windows, and a pull_request run never does", () => {
  assert.equal(find("windows", { runs: { [TAG]: [run({ event: "schedule" })] } }).ok, true);
  assert.equal(find("windows", { runs: { [TAG]: [run({ event: "pull_request" })] } }).ok, false);
});

test("a green run that skipped the Windows legs is not a Windows pass", () => {
  const found = find("windows", { runs: { [TAG]: [run()] }, jobs: windowsSkippedJobs });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /step "The Windows legs ran and passed" concluded skipped/);
});

test("a docs-only tagged commit takes the pass of an earlier commit with the same shipped files", () => {
  const found = find("windows", {
    runs: { [TAG]: [run({ jobs: windowsSkippedJobs })], [OLD]: [run({ databaseId: 2 })] },
    changes: { [OLD]: ["docs/README.md", "apps/server/tests/Weir.Core.Tests/A.cs"] },
    jobs: windowsJobs,
  });
  assert.equal(found.ok, true);
  assert.equal(found.sha, OLD);
  assert.equal(found.carried, true);
});

test("a pass is not carried over a change to a file that ships", () => {
  const found = find("windows", { runs: { [OLD]: [run({ databaseId: 2 })] }, changes: { [OLD]: ["apps/server/src/Weir.Core/A.cs"] } });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /not the same code .*apps\/server\/src\/Weir\.Core\/A\.cs/);
});

test("a run that failed or was cancelled is not a pass", () => {
  for (const conclusion of ["failure", "cancelled"]) {
    const found = find("windows", { runs: { [TAG]: [run({ conclusion })] } });
    assert.equal(found.ok, false);
    assert.match(found.problems.join("\n"), new RegExp(`concluded ${conclusion}`));
  }
});

test("a run still going is not a pass", () => {
  assert.equal(find("windows", { runs: { [TAG]: [run({ status: "in_progress", conclusion: null })] } }).ok, false);
});

test("the gate passes and says which commit was reused", () => {
  const lines = [];
  const passed = runGate({
    name: "windows", tag: "v1.2.3", repository: "o/r", sha: TAG, runsOf: (commit) => (commit === OLD ? [run({ databaseId: 9 })] : []),
    jobsOf: () => windowsJobs, git: fakeGit({ [OLD]: ["docs/x.md"] }), log: (line) => lines.push(line), error: () => {},
  });
  assert.equal(passed, true);
  assert.match(lines.join("\n"), /earlier commit .*same shipped files|only tests, scripts, docs or CI files changed/);
});

test("an unknown proof is refused", () => {
  assert.throws(() => runGate({ name: "nope", repository: "o/r", sha: TAG }), /Unknown proof/);
});
