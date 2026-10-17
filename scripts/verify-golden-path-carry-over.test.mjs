// node --test scripts/verify-golden-path-carry-over.test.mjs
// A scenario-suite pass recorded on an earlier commit counts for a later one when nothing that ships changed in between.
// Judged on real commits in a throwaway repository and on hand-built commit statuses.
import assert from "node:assert/strict";
import { test } from "node:test";

import { repository } from "./release/temporary-repository.mjs";
import { REPOSITORY } from "./repository.mjs";
import { findGoldenPathPass, runGate } from "./verify-golden-path-for-release.mjs";

let nextId = 1;
const status = (overrides = {}) => ({
  id: nextId++,
  context: "golden-path",
  state: "success",
  description: "Weir scenarios passed: 13 of 13, 1.0.0-rc.18+abc1234",
  created_at: "2026-10-07T10:00:00Z",
  creator: "the-owner",
  ...overrides,
});

const base = {
  "apps/server/src/Weir.Core/Plan.cs": "a",
  "apps/server/Directory.Build.props": "0.0.1-dev",
  "docs/a.md": "a",
};

// Commit statuses by commit; a commit not listed has none. `asked` records which commits were looked up.
function recordFor(byCommit) {
  const asked = [];
  return {
    asked,
    listStatuses: (sha) => {
      asked.push(sha);
      return byCommit[sha] ?? [];
    },
  };
}

const find = (head, { git, record, version = "1.0.0-rc.18", ...rest }) =>
  findGoldenPathPass({ sha: head, version, listStatuses: record.listStatuses, git, ...rest });

test("a pass on the commit itself is used as it is", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const found = find(x, { git, record: recordFor({ [x]: [status()] }) });
  assert.equal(found.ok, true);
  assert.equal(found.carried, false);
  assert.equal(found.sha, x);
});

test("a change to tests, scripts, docs or CI carries the pass of an earlier commit over", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  commit({ "apps/server/tests/PlanTests.cs": "t" }, "a test");
  commit({ "scripts/check.mjs": "s", ".github/workflows/ci.yml": "c" }, "a script and CI");
  const y = commit({ "docs/release-notes/v1.0.0-rc.18.md": "notes" }, "release notes");
  const found = find(y, { git, record: recordFor({ [x]: [status()] }) });
  assert.equal(found.ok, true);
  assert.equal(found.carried, true);
  assert.equal(found.sha, x);
  assert.match(found.reason, /only tests, scripts, docs or CI files changed/);
  assert.equal(found.status.description, "Weir scenarios passed: 13 of 13, 1.0.0-rc.18+abc1234");
});

test("a change to a file that ships is not carried over", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  commit({ "docs/b.md": "b" }, "docs");
  const y = commit({ "apps/server/src/Weir.Core/Plan.cs": "b" }, "code");
  const found = find(y, { git, record: recordFor({ [x]: [status()] }) });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /no status with the context "golden-path"/);
  assert.match(found.problems.join("\n"), /1 file that ships changed: apps\/server\/src\/Weir\.Core\/Plan\.cs/);
});

test("a change to release.yml is not carried over", () => {
  const { git, commit } = repository();
  const x = commit({ ...base, ".github/workflows/release.yml": "a" }, "base");
  const y = commit({ ".github/workflows/release.yml": "b" }, "release workflow");
  const found = find(y, { git, record: recordFor({ [x]: [status()] }) });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /1 file that ships changed: \.github\/workflows\/release\.yml/);
});

test("a version change is not carried over", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "apps/server/Directory.Build.props": "1.0.0-rc.19" }, "version");
  const found = find(y, { git, record: recordFor({ [x]: [status()] }) });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /apps\/server\/Directory\.Build\.props/);
});

test("a pass recorded for another release version is not carried over, whatever changed", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "docs/release-notes/v1.0.0-rc.19.md": "notes" }, "release notes");
  const found = find(y, { git, version: "1.0.0-rc.19", record: recordFor({ [x]: [status()] }) });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /was run for 1\.0\.0-rc\.18, and this tag is 1\.0\.0-rc\.19/);
});

test("a pass that does not say which version it was run for is not carried over", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "docs/b.md": "b" }, "docs");
  const found = find(y, { git, record: recordFor({ [x]: [status({ description: "Passed on the clean VM" })] }) });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /does not say which version it was run for/);
});

test("a pass on a commit that is not an ancestor never counts", () => {
  const { git, commit } = repository();
  commit(base, "base");
  git(["checkout", "-q", "-b", "side"]);
  const side = commit({ "docs/side.md": "s" }, "side");
  git(["checkout", "-q", "main"]);
  const y = commit({ "docs/main.md": "m" }, "main");
  const record = recordFor({ [side]: [status()] });
  const found = find(y, { git, record });
  assert.equal(found.ok, false);
  assert.equal(record.asked.includes(side), false);
});

test("the nearest earlier commit with any golden-path record decides: a failure is not skipped for an older pass", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const z = commit({ "docs/b.md": "b" }, "docs");
  const y = commit({ "docs/c.md": "c" }, "more docs");
  const found = find(y, {
    git,
    record: recordFor({ [x]: [status()], [z]: [status({ state: "failure", description: "Weir scenarios failed: 1 of 13 did not pass, 1.0.0-rc.18+def5678" })] }),
  });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), new RegExp(`${z}.* did not pass`));
});

test("the nearest pass is used and nothing older is looked up", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const w = commit({ "docs/b.md": "b" }, "docs");
  const y = commit({ "docs/c.md": "c" }, "more docs");
  const record = recordFor({ [x]: [status()], [w]: [status({ description: "Weir scenarios passed: 13 of 13, 1.0.0-rc.18+bbb2222" })] });
  const found = find(y, { git, record });
  assert.equal(found.sha, w);
  assert.equal(record.asked.includes(x), false);
});

test("a failure recorded on the commit itself is not overridden by an earlier pass", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "docs/b.md": "b" }, "docs");
  const found = find(y, { git, record: recordFor({ [x]: [status()], [y]: [status({ state: "failure", description: "Pause did not hold" })] }) });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /latest "golden-path" status is failure \(Pause did not hold\)/);
});

test("it looks back only as far as the limit", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  commit({ "docs/b.md": "b" }, "docs");
  commit({ "docs/c.md": "c" }, "more docs");
  const y = commit({ "docs/d.md": "d" }, "even more docs");
  const record = recordFor({ [x]: [status()] });
  const found = find(y, { git, record, maxCommits: 2 });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /none of the 2 commits before it has a golden-path run/);
  assert.equal(find(y, { git, record: recordFor({ [x]: [status()] }), maxCommits: 3 }).ok, true);
});

test("history that cannot be read is a refusal that says to fetch it", () => {
  const found = find("a".repeat(40), {
    git: () => {
      throw new Error("fatal: bad object");
    },
    record: recordFor({}),
  });
  assert.equal(found.ok, false);
  assert.match(found.problems.join("\n"), /fetch the full history/);
});

// The gate as release.yml runs it.
function gate({ tag, head, git, byCommit }) {
  const out = [];
  const err = [];
  const passed = runGate({
    tag,
    repository: REPOSITORY,
    sha: head,
    statusesOf: (_repository, sha) => byCommit[sha] ?? [],
    git,
    log: (line) => out.push(line),
    error: (line) => err.push(line),
  });
  return { passed, out: out.join("\n"), err: err.join("\n") };
}

test("the gate says which commit's pass it used and why", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "docs/release-notes/v1.0.0-rc.18.md": "notes" }, "release notes");
  const verdict = gate({ tag: "v1.0.0-rc.18", head: y, git, byCommit: { [x]: [status({ target_url: "https://example.test/run" })] } });
  assert.equal(verdict.passed, true);
  assert.equal(verdict.err, "");
  assert.match(verdict.out, new RegExp(`PASS \\(carried over\\): "golden-path" is success on ${x}, an earlier commit`));
  assert.match(verdict.out, new RegExp(`Between ${x} and ${y}`));
  assert.match(verdict.out, /only tests, scripts, docs or CI files changed/);
  assert.match(verdict.out, /Weir scenarios passed: 13 of 13, 1\.0\.0-rc\.18\+abc1234/);
  assert.match(verdict.out, /Evidence: https:\/\/example\.test\/run/);
});

test("a pass on the tagged commit is reported as that commit's own", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const verdict = gate({ tag: "v1.0.0-rc.18", head: x, git, byCommit: { [x]: [status()] } });
  assert.equal(verdict.passed, true);
  assert.match(verdict.out, new RegExp(`PASS: "golden-path" is success on ${x}`));
  assert.doesNotMatch(verdict.out, /carried over/);
});

test("the gate refuses a shipped change with the usual message and the reason", () => {
  const { git, commit } = repository();
  const x = commit(base, "base");
  const y = commit({ "apps/server/src/Weir.Core/Plan.cs": "b" }, "code");
  const verdict = gate({ tag: "v1.0.0-rc.18", head: y, git, byCommit: { [x]: [status()] } });
  assert.equal(verdict.passed, false);
  assert.match(verdict.err, /This commit has no passing golden-path run\. Run Weir's scenario suite on this exact build/);
  assert.match(verdict.err, /1 file that ships changed/);
});
