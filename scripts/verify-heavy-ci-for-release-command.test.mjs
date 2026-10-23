// node --test scripts/verify-heavy-ci-for-release-command.test.mjs
// The heavy-CI release gate as a command: reading the run lists, what it prints when it refuses, and the git history it cannot read.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { loadTargets } from "./select-mutation-targets.mjs";
import { PROOFS, findProof, findTargetProofs, main, runGate } from "./verify-heavy-ci-for-release.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const TAG = "c".repeat(40);
const OLD = "a".repeat(40);

const windowsJobs = [
  { name: "ci-passed", conclusion: "success", steps: [
    { name: "Every job that was due passed", conclusion: "success" },
    { name: "The Windows legs ran and passed", conclusion: "success" },
  ] },
];

function history({ changes = {}, readable = true, diffReadable = true } = {}) {
  return (args) => {
    if (args[0] === "rev-list") {
      if (!readable) throw new Error("fatal: not a git repository\nmore");
      return [TAG, OLD].join("\n");
    }
    if (args[0] === "merge-base") return "";
    if (args[0] === "show") return JSON.stringify(loadTargets(repositoryRoot));
    if (args[0] === "diff") {
      if (!diffReadable) throw new Error("fatal: bad object\nmore");
      return (changes[args.at(-2)] ?? []).join("\0");
    }
    throw new Error(`unexpected git ${args.join(" ")}`);
  };
}

const failedRun = { databaseId: 4, event: "push", status: "completed", conclusion: "failure", url: "https://example.test/4" };
const passedRun = { ...failedRun, databaseId: 5, conclusion: "success", url: "https://example.test/5" };

test("when the history cannot be read the gate says to fetch it all", () => {
  const found = findProof({ proof: PROOFS.windows, sha: TAG, runsOf: () => [], jobsOf: () => [], git: history({ readable: false }) });
  assert.equal(found.ok, false);
  assert.deepEqual(found.problems, [`the commits before ${TAG} could not be read (fatal: not a git repository); fetch the full history`]);

  const [first] = findTargetProofs({ proof: PROOFS.mutation, sha: TAG, root: repositoryRoot, targets: loadTargets(repositoryRoot), runsOf: () => [], jobsOf: () => [], git: history({ readable: false }) });
  assert.equal(first.problems[0], `the commits before ${TAG} could not be read (fatal: not a git repository); fetch the full history`);
});

test("a mutation pass that cannot be compared with the tag is not carried, and the reason says so", () => {
  const targets = loadTargets(repositoryRoot);
  const [first] = findTargetProofs({
    proof: PROOFS.mutation, sha: TAG, root: repositoryRoot, targets, runsOf: (commit) => (commit === OLD ? [{ ...passedRun, event: "schedule" }] : []),
    jobsOf: () => [], git: history({ diffReadable: false }),
  });
  assert.equal(first.ok, false);
  assert.match(first.problems.join("\n"), /the changes since could not be read \(fatal: bad object\)/);
});

test("a long list of reasons is cut to three and the rest counted", () => {
  const targets = loadTargets(repositoryRoot).filter((target) => target.target === "core-rules");
  const lines = [];
  const changed = [
    "apps/server/tests/Weir.Core.Tests/A.cs",
    "apps/server/tests/Weir.Core.Tests/B.cs",
    "apps/server/tests/Weir.Core.Tests/C.cs",
    "apps/server/tests/Weir.Core.Tests/D.cs",
    ".github/workflows/mutation.yml",
  ];
  const passed = runGate({
    name: "mutation", repository: "o/r", sha: TAG, root: repositoryRoot, targets,
    runsOf: (commit) => (commit === OLD ? [{ ...passedRun, event: "schedule" }] : []), jobsOf: () => [], git: history({ changes: { [OLD]: changed } }),
    log: () => {}, error: (line) => lines.push(line),
  });
  assert.equal(passed, false);
  assert.match(lines.join("\n"), /, and \d+ more\)/);
  assert.ok(lines.includes("  gh workflow run mutation.yml --repo o/r --ref <tag> -f targets=core-rules"));
});

test("the gate refuses a missing repository and a short commit, before it asks GitHub anything", () => {
  assert.throws(() => runGate({ name: "windows", sha: TAG }), /GITHUB_REPOSITORY must be set\./);
  assert.throws(() => runGate({ name: "windows", repository: "o/r", sha: "abc" }), /Not a full commit SHA: abc/);
  assert.throws(() => runGate({ name: "windows", repository: "o/r" }), /Not a full commit SHA: undefined/);
});

test("when no run proves the Windows legs it says why, and what to run", () => {
  const lines = [];
  const none = runGate({ name: "windows", tag: "v1.0.0", repository: "o/r", sha: TAG, runsOf: () => [], jobsOf: () => [], git: history(), log: () => {}, error: (line) => lines.push(line) });
  assert.equal(none, false);
  assert.match(lines[0], new RegExp(`^::error::No ci\\.yml run proves the Windows contract legs and the Windows scenario shards on ${TAG}\\. The release will not publish\\.$`));
  assert.match(lines[1], /^ {2}- none of the \d+ commits up to it has a push, scheduled or manual run of ci\.yml\.$/);
  assert.equal(lines.at(-2), "  gh workflow run ci.yml --repo o/r --ref v1.0.0");
  assert.equal(lines.at(-1), "then re-run this release.");

  lines.length = 0;
  runGate({ name: "windows", repository: "o/r", sha: TAG, runsOf: (commit) => (commit === TAG ? [failedRun] : []), jobsOf: () => [], git: history(), log: () => {}, error: (line) => lines.push(line) });
  assert.match(lines[1], /run 4 \(push, https:\/\/example\.test\/4\) on c+ does not qualify: concluded failure/);
  assert.equal(lines.at(-2), "  gh workflow run ci.yml --repo o/r --ref <tag>");
});

/** GitHub's list of workflow runs: `pages` maps "<event>:<page>" to the runs on it. */
function stand(pages) {
  const asked = [];
  const ask = (args) => {
    asked.push(args.join(" "));
    if (args[0] === "run") return { jobs: windowsJobs };
    const [, event, page] = /event=(\w+)&.*page=(\d+)/.exec(args[1]);
    return { workflow_runs: pages[`${event}:${page}`] ?? [] };
  };
  return { ask, asked };
}

const listed = (id, sha, event = "push") => ({ id, head_sha: sha, event, status: "completed", conclusion: "success", html_url: `https://example.test/${id}` });

test("the command reads two pages of runs for each event, finds the tagged commit's pass and exits 0", () => {
  const full = Array.from({ length: 100 }, (_, index) => listed(index + 10, "d".repeat(40)));
  const { ask, asked } = stand({ "push:1": full, "push:2": [listed(1, TAG)] });
  const lines = [];

  const code = main(["windows"], { GITHUB_REPOSITORY: "o/r", GITHUB_SHA: TAG, GITHUB_REF_NAME: "v1.0.0" }, { ask, git: history(), log: (line) => lines.push(line), error: () => {} });

  assert.equal(code, 0);
  assert.equal(asked.filter((line) => line.includes("event=push")).length, 2);
  assert.equal(asked.filter((line) => line.includes("event=schedule")).length, 1);
  assert.ok(asked.includes("run view 1 --repo o/r --json jobs"));
  assert.match(lines.at(-1), /^PASS: ci\.yml run 1 \(push, https:\/\/example\.test\/1\) passed on c+\.$/);
});

test("the command takes --sha before the environment's commit before the checked-out one, and exits 1 when nothing proves it", () => {
  const { ask } = stand({});
  const seen = [];
  const errors = [];
  const git = history();
  const options = { ask, git, headCommit: () => TAG, log: (line) => seen.push(line), error: (line) => errors.push(line) };

  assert.equal(main(["windows", "--sha", OLD], { GITHUB_REPOSITORY: "o/r", GITHUB_SHA: TAG }, options), 1);
  assert.match(seen[0], new RegExp(`on ${OLD} or`));
  assert.equal(main(["windows"], { GITHUB_REPOSITORY: "o/r", GITHUB_SHA: OLD }, options), 1);
  assert.match(seen[1], new RegExp(`on ${OLD} or`));
  assert.equal(main(["windows"], { GITHUB_REPOSITORY: "o/r" }, options), 1);
  assert.match(seen[2], new RegExp(`on ${TAG} or`));
});

test("the command asks for a proof by name", () => {
  const errors = [];
  assert.equal(main(["nothing"], {}, { error: (line) => errors.push(line) }), 2);
  assert.deepEqual(errors, ["Usage: node scripts/verify-heavy-ci-for-release.mjs <windows|mutation> [--sha <sha>]"]);
});

test("run as a command, a missing repository is an error annotation and exit 1", () => {
  const script = fileURLToPath(new URL("./verify-heavy-ci-for-release.mjs", import.meta.url));
  const env = { ...process.env };
  delete env.GITHUB_REPOSITORY;
  const result = spawnSync(process.execPath, [script, "windows", "--sha", TAG], { encoding: "utf8", env });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /GITHUB_REPOSITORY must be set/);
});
