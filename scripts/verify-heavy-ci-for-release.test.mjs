// node --test scripts/verify-heavy-ci-for-release.test.mjs
// The release gate for the Windows legs and the mutation run, judged on hand-built runs, jobs and git history.
import assert from "node:assert/strict";
import { test } from "node:test";

import { PROOFS, findProof, isMutationInput, runGate } from "./verify-heavy-ci-for-release.mjs";

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
const mutationJobs = [{ name: "check", conclusion: "success", steps: [{ name: "Mutation scores against the floors", conclusion: "success" }] }];

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

test("mutation counts a scheduled or manual run, never a push or a pull request", () => {
  for (const event of ["schedule", "workflow_dispatch"]) {
    assert.equal(find("mutation", { runs: { [TAG]: [run({ event })] }, jobs: mutationJobs }).ok, true, event);
  }
  for (const event of ["push", "pull_request"]) {
    assert.equal(find("mutation", { runs: { [TAG]: [run({ event })] }, jobs: mutationJobs }).ok, false, event);
  }
});

test("mutation needs its check job to have held the floors", () => {
  const failed = [{ name: "check", conclusion: "failure", steps: [] }];
  const found = find("mutation", { runs: { [TAG]: [run({ event: "schedule" })] }, jobs: failed });
  assert.match(found.problems.join("\n"), /job "check" concluded failure/);
});

test("a mutation pass is not carried over a changed test, even though no shipped file changed", () => {
  const weekly = run({ event: "schedule", databaseId: 3 });
  const changed = find("mutation", { runs: { [OLD]: [weekly] }, jobs: mutationJobs, changes: { [OLD]: ["apps/server/tests/Weir.Infrastructure.Tests/A.cs"] } });
  assert.equal(changed.ok, false);
  assert.match(changed.problems.join("\n"), /file the mutation run reads changed: apps\/server\/tests\/Weir\.Infrastructure\.Tests\/A\.cs/);
  const docs = find("mutation", { runs: { [OLD]: [weekly] }, jobs: mutationJobs, changes: { [OLD]: ["docs/release.md"] } });
  assert.equal(docs.ok, true);
});

test("the files a mutation score reads are named", () => {
  for (const path of ["apps/web/src/a.test.ts", ".github/mutation-targets.json", "docs/coverage/mutation-floors.json", ".github/workflows/mutation.yml"]) {
    assert.equal(isMutationInput(path), true, path);
  }
  assert.equal(isMutationInput("docs/release.md"), false);
});

test("the gate logs why it fails and what to run", () => {
  const lines = [];
  const passed = runGate({
    name: "mutation", tag: "v1.2.3", repository: "o/r", sha: TAG, runsOf: () => [], jobsOf: () => [], git: fakeGit(),
    log: () => {}, error: (line) => lines.push(line),
  });
  assert.equal(passed, false);
  assert.match(lines.join("\n"), /gh workflow run mutation\.yml --repo o\/r --ref v1\.2\.3/);
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
