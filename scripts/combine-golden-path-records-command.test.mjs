// node --test scripts/combine-golden-path-records-command.test.mjs
// The command line of the golden-path combiner, and the records it refuses that scenarios.test.mjs does not build.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { combineRecords, run, setStatusWithGh } from "./combine-golden-path-records.mjs";
import { repository } from "./release/temporary-repository.mjs";
import { GOLDEN_PATH_CONTEXT } from "./verify-golden-path-for-release.mjs";

const script = fileURLToPath(new URL("./combine-golden-path-records.mjs", import.meta.url));
const COMMIT = "a".repeat(40);
const TITLES = ["Fresh install", "First visit"];

function record({ chosen, scriptsCommit, results }) {
  const facts = [`- **Commit:** ${COMMIT}`, ...(scriptsCommit ? [`- **Scripts commit:** ${scriptsCommit}`] : []), ...(chosen ? [`- **Scenarios chosen:** ${chosen}`] : [])];
  const rows = TITLES.map((title, index) => `| ${index + 1} | ${title} | ${results[title]} | 1 | saw |`);
  return ["# Weir scenario suite 3.2.17, commit aaaaaaa", "", ...facts, "", ...rows, ""].join("\n");
}

const problemsOf = (git, full, ...reruns) =>
  combineRecords({ full: { name: "full.md", text: full }, reruns: reruns.map((text, index) => ({ name: `rerun-${index + 1}.md`, text })), git }).problems?.join("\n");

test("scripts commits that cannot be compared are refused: not one commit, or not in this clone", () => {
  const { git, commit } = repository();
  const there = commit({ "a.txt": "a" }, "A");
  const full = (scriptsCommit) => record({ scriptsCommit, results: { "Fresh install": "passed", "First visit": "failed" } });
  const rerun = (scriptsCommit) => record({ scriptsCommit, chosen: "first-visit-account", results: { "First visit": "passed" } });

  assert.match(problemsOf(git, full("HEAD"), rerun(there)), /full\.md ran its scenario scripts from HEAD, which is not one committed state/);
  assert.match(problemsOf(git, full("f".repeat(40)), rerun(there)), /full\.md ran from commit f{40}, which is not in this clone\. Fetch it/);
});

test("a full run in which a scenario did not run, and a re-run with a scenario the full run lacks, are refused", () => {
  const { git, commit } = repository();
  const there = commit({ "a.txt": "a" }, "A");
  const incomplete = record({ scriptsCommit: there, results: { "Fresh install": "not run yet", "First visit": "failed" } });
  const rerun = record({ scriptsCommit: there, chosen: "first-visit-account", results: { "Fresh install": "not chosen", "First visit": "passed" } });
  assert.match(problemsOf(git, incomplete, rerun), /full\.md is not a complete full run: 1 scenario did not run \(Fresh install\)/);

  const full = record({ scriptsCommit: there, results: { "Fresh install": "passed", "First visit": "failed" } });
  const stranger = record({ scriptsCommit: there, chosen: "first-visit-account", results: { "First visit": "passed" } }).replace("| 2 | First visit |", "| 2 | Something else |");
  assert.match(problemsOf(git, full, stranger), /rerun-1\.md has scenarios the full run does not have \(Something else\)/);
});

function combiner(files, overrides = {}) {
  const lines = { log: [], error: [] };
  const written = {};
  const statuses = [];
  const git = repository();
  const there = git.commit({ "a.txt": "a" }, "A");
  const texts = {
    "records/full.md": record({ scriptsCommit: there, results: { "Fresh install": "passed", "First visit": "failed" } }),
    "records/rerun.md": record({ scriptsCommit: there, chosen: "first-visit-account", results: { "First visit": "passed" } }),
  };
  const code = run(files, {
    git: git.git,
    read: (path) => texts[path.replaceAll("\\", "/")],
    write: (path, text) => (written[path] = text),
    setStatus: (call) => statuses.push(call),
    log: (line) => lines.log.push(line),
    error: (line) => lines.error.push(line),
    ...overrides,
  });
  return { code, lines, written, statuses };
}

test("fewer than two records is a usage error", () => {
  const { code, lines } = combiner(["records/full.md"]);
  assert.equal(code, 2);
  assert.match(lines.error[0], /^Usage: node scripts\/combine-golden-path-records\.mjs/);
});

test("without --out the combined record is written beside the full run's, named for the build", () => {
  const { code, written, statuses, lines } = combiner(["records/full.md", "records/rerun.md"]);
  assert.equal(code, 0);
  assert.deepEqual(Object.keys(written).map((path) => path.replaceAll("\\", "/").split("/").slice(-2).join("/")), ["records/scenario-3.2.17-aaaaaaa-combined.md"]);
  assert.equal(statuses.length, 1);
  assert.equal(statuses[0].repository.includes("/"), true);
  assert.match(statuses[0].target, /^https:\/\/github\.com\/.+\/commit\/a{40}$/);
  assert.match(lines.log.at(-1), new RegExp(`^${GOLDEN_PATH_CONTEXT} status on aaaaaaa: success`));
});

test("--no-status writes the record and says what the status would read, and sets nothing", () => {
  const { code, statuses, lines } = combiner(["records/full.md", "records/rerun.md", "--no-status"]);
  assert.equal(code, 0);
  assert.equal(statuses.length, 0);
  assert.match(lines.log.at(-1), /^--no-status: the golden-path status is not set\. It would read: Weir scenarios passed, combined from a full run and 1 re-run/);
});

test("--repository and --evidence-url decide where the status is set and what it links to", () => {
  const { statuses } = combiner(["records/full.md", "records/rerun.md", "--repository", "Owner/Name", "--evidence-url", "https://example.test/run/1"]);
  assert.deepEqual([statuses[0].repository, statuses[0].target], ["Owner/Name", "https://example.test/run/1"]);
});

test("the status is set through gh api with the success state, the context and a description cut to 140 characters", () => {
  const seen = [];
  setStatusWithGh({ repository: "Owner/Name", sha: COMMIT, description: "d".repeat(200), target: "https://example.test/x" }, (program, args, options) => seen.push({ program, args, options }));
  assert.equal(seen.length, 1);
  assert.equal(seen[0].program, "gh");
  assert.deepEqual(seen[0].args.slice(0, 2), ["api", `repos/Owner/Name/statuses/${COMMIT}`]);
  assert.ok(seen[0].args.includes("state=success"));
  assert.ok(seen[0].args.includes(`context=${GOLDEN_PATH_CONTEXT}`));
  assert.ok(seen[0].args.includes(`description=${"d".repeat(140)}`));
  assert.ok(seen[0].args.includes("target_url=https://example.test/x"));
});

test("run as a command it prints the usage with one record, and an unreadable record is an error annotation and exit 1", () => {
  const usage = spawnSync(process.execPath, [script, "only-one.md"], { encoding: "utf8" });
  assert.equal(usage.status, 2);
  assert.match(usage.stderr, /^Usage: node scripts\/combine-golden-path-records\.mjs/);

  const unreadable = spawnSync(process.execPath, [script, "no-such-full.md", "no-such-rerun.md"], { encoding: "utf8" });
  assert.equal(unreadable.status, 1);
  assert.match(unreadable.stderr, /^::error::ENOENT/);
});
