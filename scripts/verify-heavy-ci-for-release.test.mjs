// node --test scripts/verify-heavy-ci-for-release.test.mjs
// The release gate for the Windows legs and the mutation run, judged on hand-built runs, jobs and git history.
import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { changedTargets } from "./release/mutation-inputs.mjs";
import { loadTargets } from "./select-mutation-targets.mjs";
import { PROOFS, findProof, findTargetProofs, runGate } from "./verify-heavy-ci-for-release.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const TAG = "c".repeat(40);
const OLD = "a".repeat(40);
const OLDER = "b".repeat(40);

// History newest first: TAG, OLD, OLDER. `changes` maps "<base>" to the paths that differ between it and TAG.
function fakeGit(changes = {}) {
  return (args) => {
    if (args[0] === "rev-list") return [TAG, OLD, OLDER].join("\n");
    if (args[0] === "merge-base") return "";
    if (args[0] === "show") return JSON.stringify(loadTargets(repositoryRoot));
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

// Mutation is judged per target, against the real targets file and Stryker configs.
const targets = loadTargets(repositoryRoot);
const names = targets.map((target) => target.target);
const mutatedFile = "apps/server/src/Weir.Infrastructure/LibraryMode/SafeSwap.cs";
const othersFile = "apps/server/src/Weir.Infrastructure/MediaManagers/HandbackOutcomes.cs";
const mutateJobs = (among = names) => [
  ...mutationJobs,
  ...among.map((name) => ({ name: `Mutate ${name}`, conclusion: "success", steps: [] })),
];
const proofOf = ({ runs = {}, changes = {}, jobs = mutateJobs(), only = names } = {}) =>
  findTargetProofs({
    proof: PROOFS.mutation, sha: TAG, root: repositoryRoot, targets: targets.filter((target) => only.includes(target.target)),
    runsOf: (commit) => runs[commit] ?? [], jobsOf: (candidate) => candidate.jobs ?? jobs, git: fakeGit(changes),
  });
const verdicts = (results) => Object.fromEntries(results.map((result) => [result.target, result.ok]));
const weekly = run({ event: "schedule", databaseId: 3 });

test("mutation counts a scheduled or manual run, never a push or a pull request", () => {
  for (const event of ["schedule", "workflow_dispatch"]) {
    assert.ok(proofOf({ runs: { [TAG]: [run({ event })] } }).every((result) => result.ok), event);
  }
  for (const event of ["push", "pull_request"]) {
    assert.ok(proofOf({ runs: { [TAG]: [run({ event })] } }).every((result) => !result.ok), event);
  }
});

test("mutation needs its check job to have held the floors", () => {
  const failed = [{ name: "check", conclusion: "failure", steps: [] }, ...mutateJobs().slice(1)];
  const [first] = proofOf({ runs: { [TAG]: [run({ event: "schedule" })] }, jobs: failed });
  assert.match(first.problems.join("\n"), /job "check" concluded failure/);
});

test("a run proves only the targets it mutated", () => {
  const partial = run({ event: "workflow_dispatch", databaseId: 8, jobs: mutateJobs(["core-rules", "web-api-client"]) });
  const results = proofOf({ runs: { [TAG]: [partial] } });
  assert.deepEqual(names.filter((name) => verdicts(results)[name]), ["core-rules", "web-api-client"]);
  assert.match(results.find((result) => result.target === "handoff-intake").problems.join("\n"), /did not mutate handoff-intake/);
});

test("a pass is carried forward while the target's own files, tests and setup are unchanged, whatever else changed", () => {
  const carried = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: [othersFile, "apps/web/src/lib/api/client.ts", "docs/release.md"] } });
  assert.equal(carried.find((result) => result.target === "file-lifecycle-swap-preflight").ok, true);
  assert.equal(carried.find((result) => result.target === "file-lifecycle-swap-preflight").carried, true);
  assert.deepEqual(names.filter((name) => !verdicts(carried)[name]), ["outcome-handling", "web-api-client"]);
});

test("a changed mutated file, test or setup file withdraws the carried pass of that target only", () => {
  const dotnetTest = "apps/server/tests/Weir.Infrastructure.Tests/SafeSwapTests.cs";
  const coreTest = "apps/server/tests/Weir.Core.Tests/SafeSwapRulesTests.cs";
  const results = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: [dotnetTest, coreTest] } });
  const failing = names.filter((name) => !verdicts(results)[name]);
  assert.ok(failing.includes("file-lifecycle-swap-preflight"), "a test in its own test project");
  assert.ok(failing.includes("core-rules"), "core-rules runs the Core and the Infrastructure tests");
  assert.ok(!failing.includes("start-supervision"), "the tray tests are another project");
  assert.ok(!failing.includes("web-api-client"));
  const tray = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: ["apps/tray/Weir.Tray.StandInServer/Program.cs"] } });
  assert.deepEqual(names.filter((name) => !verdicts(tray)[name]), ["start-supervision"]);
  const setup = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: [".github/workflows/mutation.yml"] } });
  assert.ok(setup.every((result) => !result.ok), "the workflow is every target's setup");
  const build = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: ["apps/server/Directory.Build.props"] } });
  assert.ok(!verdicts(build)["core-rules"] && verdicts(build)["start-supervision"] && verdicts(build)["web-api-client"]);
  const [swap] = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: [mutatedFile] }, only: ["file-lifecycle-swap-preflight"] });
  assert.equal(swap.ok, false);
  assert.match(swap.problems.join("\n"), /changed this target's files, tests or setup \(a{40}: apps\/server\/src\/Weir\.Infrastructure\/LibraryMode\/SafeSwap\.cs \(a file it mutates\)\)/);
});

test("a target's entry in the targets file is part of its setup", () => {
  const git = (args) => {
    if (args[0] === "show") {
      const list = JSON.parse(JSON.stringify(targets));
      if (args[1].startsWith(OLD)) list.find((entry) => entry.target === "core-rules").solution = "Old.slnx";
      return JSON.stringify(list);
    }
    return fakeGit()(args);
  };
  const results = findTargetProofs({
    proof: PROOFS.mutation, sha: TAG, root: repositoryRoot, targets, runsOf: (commit) => (commit === OLD ? [weekly] : []), jobsOf: () => mutateJobs(), git,
  });
  assert.deepEqual(names.filter((name) => !verdicts(results)[name]), ["core-rules"]);
});

test("web targets: run paths name their tests exactly, related-test targets count every web test", () => {
  const apiTest = "apps/web/src/lib/api/client.test.ts";
  const results = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: [apiTest] } });
  assert.deepEqual(names.filter((name) => !verdicts(results)[name]).sort(), ["web-api-client", "web-dialogs-activity-profile", "web-dialogs-library"]);
  const setup = proofOf({ runs: { [OLD]: [weekly] }, changes: { [OLD]: ["apps/web/src/test/setup.ts"] } });
  const web = names.filter((name) => name.startsWith("web-"));
  assert.deepEqual(names.filter((name) => web.includes(name) && !verdicts(setup)[name]), web);
});

test("a newer partial run and an older full run together prove the release", () => {
  const changed = changedTargets([mutatedFile], repositoryRoot, targets).map((item) => item.target.target);
  const dispatched = run({ event: "workflow_dispatch", databaseId: 9, jobs: mutateJobs(changed) });
  const results = proofOf({ runs: { [TAG]: [dispatched], [OLD]: [weekly] }, changes: { [OLD]: [mutatedFile] } });
  assert.ok(results.every((result) => result.ok));
  assert.equal(results.find((result) => result.target === "file-lifecycle-swap-preflight").carried, false);
  assert.equal(results.find((result) => result.target === "core-rules").sha, OLD);
  assert.ok(changed.length < names.length);
});

test("the gate logs what is missing and the command that runs just those targets", () => {
  const lines = [];
  const passed = runGate({
    name: "mutation", tag: "v1.2.3", repository: "o/r", sha: TAG, root: repositoryRoot, targets,
    runsOf: (commit) => (commit === OLD ? [weekly] : []), jobsOf: () => mutateJobs(), git: fakeGit({ [OLD]: [mutatedFile, othersFile] }),
    log: () => {}, error: (line) => lines.push(line),
  });
  assert.equal(passed, false);
  const changed = changedTargets([mutatedFile, othersFile], repositoryRoot, targets).map((item) => item.target.target);
  assert.ok(changed.includes("file-lifecycle-swap-preflight") && changed.includes("outcome-handling") && changed.length < names.length);
  assert.ok(lines.includes(`  gh workflow run mutation.yml --repo o/r --ref v1.2.3 -f targets=${changed.join(",")}`), lines.join("\n"));
  assert.match(lines.join("\n"), new RegExp(`for ${changed.length} of ${names.length} targets`));
});

test("with nothing found at all the gate asks for every target", () => {
  const lines = [];
  const passed = runGate({
    name: "mutation", tag: "v1.2.3", repository: "o/r", sha: TAG, root: repositoryRoot, targets, runsOf: () => [], jobsOf: () => [], git: fakeGit(),
    log: () => {}, error: (line) => lines.push(line),
  });
  assert.equal(passed, false);
  assert.match(lines.join("\n"), new RegExp(`-f targets=${names.join(",")}$`, "m"));
});

test("the gate passes and says which commit each target reused", () => {
  const lines = [];
  const passed = runGate({
    name: "mutation", tag: "v1.2.3", repository: "o/r", sha: TAG, root: repositoryRoot, targets,
    runsOf: (commit) => (commit === OLD ? [weekly] : []), jobsOf: () => mutateJobs(), git: fakeGit({ [OLD]: ["docs/x.md"] }),
    log: (line) => lines.push(line), error: () => {},
  });
  assert.equal(passed, true);
  assert.match(lines.join("\n"), /PASS core-rules: .* its files, tests and mutation setup are unchanged since/);
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

test("a run that did not succeed is named by how it ended, and its jobs are not asked for", () => {
  const results = findTargetProofs({
    proof: PROOFS.mutation, sha: TAG, root: repositoryRoot, targets: targets.slice(0, 1),
    runsOf: (commit) => (commit === TAG ? [run({ event: "schedule", conclusion: "failure", databaseId: 4 })] : []),
    jobsOf: () => assert.fail("a failed run's jobs are not listed"), git: fakeGit(),
  });
  assert.equal(results[0].ok, false);
  assert.match(results[0].problems.join("\n"), /run 4 .* does not qualify: concluded failure/);
});

test("the gate reads the targets from the checkout when it is not given them", () => {
  const lines = [];
  const passed = runGate({ name: "mutation", tag: "v1.2.3", repository: "o/r", sha: TAG, root: repositoryRoot, runsOf: () => [], jobsOf: () => [], git: fakeGit(), log: () => {}, error: (line) => lines.push(line) });
  assert.equal(passed, false);
  assert.match(lines.join("\n"), new RegExp(`-f targets=${names.join(",")}$`, "m"));
});

test("an unknown proof is refused", () => {
  assert.throws(() => runGate({ name: "nope", repository: "o/r", sha: TAG }), /Unknown proof/);
});
