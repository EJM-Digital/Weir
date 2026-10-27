// node --test scripts/release-gates-commands.test.mjs
// The gh calls and command lines of the CI and golden-path release gates (gh is replaced; nothing is reached).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { gh } from "./verify-ci-for-release.mjs";
import { evaluateStatuses, findGoldenPathPass, listStatuses, main, passedVersion, runGate } from "./verify-golden-path-for-release.mjs";

const script = fileURLToPath(new URL("./verify-golden-path-for-release.mjs", import.meta.url));
const SHA = "a".repeat(40);

test("gh returns the JSON the CLI printed, asked for with a large enough buffer", () => {
  const seen = [];
  const answer = gh(["run", "list"], (program, args, options) => (seen.push({ program, args, encoding: options.encoding, maxBuffer: options.maxBuffer }), '[{"id":1}]'));
  assert.deepEqual(answer, [{ id: 1 }]);
  assert.deepEqual(seen, [{ program: "gh", args: ["run", "list"], encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }]);
});

test("a commit's statuses are listed through gh api, one JSON object per line, blank lines ignored", () => {
  const seen = [];
  const output = '{"id":1,"context":"golden-path"}\n\n{"id":2,"context":"other"}\n';
  const statuses = listStatuses("Owner/Name", SHA, (program, args) => (seen.push([program, ...args.slice(0, 2)]), output));
  assert.deepEqual(statuses.map((status) => status.id), [1, 2]);
  assert.deepEqual(seen, [["gh", "api", "--paginate"]]);
});

test("a status that is not success is reported with its description when it has one, and without when it has none", () => {
  const failed = (description) => [{ id: 1, context: "golden-path", state: "failure", created_at: "2026-10-07T10:00:00Z", ...(description ? { description } : {}) }];
  assert.deepEqual(evaluateStatuses(failed("scenarios failed")), ['the latest "golden-path" status is failure (scenarios failed)']);
  assert.deepEqual(evaluateStatuses(failed()), ['the latest "golden-path" status is failure']);
});

test("the version a pass was run for is read from its description, and is none when there is no status, no description or no version", () => {
  assert.equal(passedVersion({ description: "Weir scenarios passed: 1.0.0-rc.18+abc1234" }), "1.0.0-rc.18");
  assert.equal(passedVersion({ description: "Passed on the clean VM" }), null);
  assert.equal(passedVersion({}), null);
  assert.equal(passedVersion(undefined), null);
});

test("a pass on an earlier commit that does not say which version it was for is refused, with or without a description", () => {
  const git = (args) => (args[0] === "rev-list" ? `${SHA}\n${"b".repeat(40)}\n` : "");
  const statusesWith = (description) => (commit) =>
    commit === "b".repeat(40) ? [{ id: 1, context: "golden-path", state: "success", created_at: "2026-10-07T10:00:00Z", ...(description ? { description } : {}) }] : [];
  for (const [description, shown] of [["a pass", "a pass"], [undefined, "no description"]]) {
    const found = findGoldenPathPass({ sha: SHA, version: "1.0.0", listStatuses: statusesWith(description), git });
    assert.equal(found.ok, false);
    assert.match(found.problems.join("\n"), new RegExp(`does not say which version it was run for \\(${shown}\\), so it cannot be reused`));
  }
});

test("a gate with no commit is refused", () => {
  assert.throws(() => runGate({ tag: "v1.0.0", repository: "Owner/Name", sha: undefined, statusesOf: () => [] }), /Not a full commit SHA: undefined/);
});

test("main takes the commit from --sha, then GITHUB_SHA, then the checkout, and exits 1 when the gate refuses", () => {
  const asked = [];
  const gate = (call) => (asked.push(call), call.tag !== "v9.9.9");
  const headCommit = () => "c".repeat(40);

  assert.equal(main(["--sha", "d".repeat(40), "--tag", "v1.0.0"], { GITHUB_SHA: SHA, GITHUB_REPOSITORY: "O/N" }, { gate, headCommit }), 0);
  assert.equal(main([], { GITHUB_SHA: SHA, GITHUB_REF_NAME: "v1.0.0", GITHUB_REPOSITORY: "O/N" }, { gate, headCommit }), 0);
  assert.equal(main([], { GITHUB_REF_NAME: "v9.9.9" }, { gate, headCommit }), 1);
  assert.deepEqual(asked.map((call) => [call.tag, call.repository, call.sha]), [
    ["v1.0.0", "O/N", "d".repeat(40)],
    ["v1.0.0", "O/N", SHA],
    ["v9.9.9", undefined, "c".repeat(40)],
  ]);
});

test("run as a command without a repository it annotates the error and exits 1", () => {
  const env = { ...process.env };
  delete env.GITHUB_REPOSITORY;
  const run = spawnSync(process.execPath, [script, "--sha", SHA, "--tag", "v1.0.0"], { encoding: "utf8", env });
  assert.equal(run.status, 1);
  assert.match(run.stderr, /^::error::GITHUB_REPOSITORY must be set\./);
});
