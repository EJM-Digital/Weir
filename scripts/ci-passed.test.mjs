// node --test scripts/ci-passed.test.mjs
// CI's single verdict on hand-built `needs` objects, the shape GitHub's toJSON(needs) produces.
import assert from "node:assert/strict";
import { test } from "node:test";

import { GATES, verdict } from "./ci-passed.mjs";

function needs({ due = {}, results = {}, changes = "success" } = {}) {
  const outputs = {};
  for (const flag of new Set(Object.values(GATES).filter(Boolean))) outputs[flag] = due[flag] === false ? "false" : "true";
  const all = { changes: { result: changes, outputs } };
  for (const [job, flag] of Object.entries(GATES)) {
    all[job] = { result: results[job] ?? (flag && due[flag] === false ? "skipped" : "success") };
  }
  return all;
}

test("everything due and passed", () => {
  assert.deepEqual(verdict(needs()), []);
});

test("a job skipped because its paths did not change is a pass", () => {
  assert.deepEqual(verdict(needs({ due: { run_server_linux: false, run_contract: false } })), []);
});

test("a due job that failed, was cancelled or was skipped fails the verdict", () => {
  for (const result of ["failure", "cancelled", "skipped"]) {
    const problems = verdict(needs({ results: { "server-windows": result } }));
    assert.equal(problems.length, 1);
    assert.match(problems[0], /server-windows ended .* was due/);
  }
});

test("a job that ran when it was not due means the flags and conditions disagree", () => {
  const problems = verdict(needs({ due: { run_tray: false }, results: { tray: "success" } }));
  assert.match(problems.join("\n"), /tray ended success; it was not due/);
});

test("a job that always runs must pass", () => {
  assert.match(verdict(needs({ results: { "repo-checks": "skipped" } })).join("\n"), /repo-checks ended skipped/);
});

test("failed change detection fails the verdict outright", () => {
  const problems = verdict(needs({ changes: "failure" }));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /changes ended failure/);
});

test("a missing flag, a missing job or an unknown job is refused", () => {
  const noFlag = needs();
  delete noFlag.changes.outputs.run_web;
  assert.match(verdict(noFlag).join("\n"), /no usable run_web/);

  const missing = needs();
  delete missing.tray;
  assert.match(verdict(missing).join("\n"), /tray is not in ci-passed's needs/);

  const extra = { ...needs(), "new-job": { result: "success" } };
  assert.match(verdict(extra).join("\n"), /new-job is not in scripts\/ci-passed.mjs GATES/);
});

test("the Windows legs may be skipped on a pull request while the Linux ones run", () => {
  const skipped = { run_contract_windows: false, run_packaging_windows: false };
  assert.deepEqual(verdict(needs({ due: skipped })), []);
});

test("a Windows leg that was due and failed fails the verdict, and one that ran unasked disagrees with the flags", () => {
  assert.match(verdict(needs({ results: { "contract-windows": "failure" } })).join("\n"), /contract-windows ended failure; it was due/);
  assert.match(verdict(needs({ results: { "packaging-windows": "skipped" } })).join("\n"), /packaging-windows ended skipped; it was due/);
  const unasked = verdict(needs({ due: { run_contract_windows: false }, results: { "contract-windows": "success" } }));
  assert.match(unasked.join("\n"), /contract-windows ended success; it was not due/);
});
