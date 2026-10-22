// node --test scripts/check-workflow-hygiene.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { findProblems } from "./check-workflow-hygiene.mjs";

const workflow = (steps) => `name: ci
permissions:
  contents: read
jobs:
  e2e:
    runs-on: ubuntu-latest
    timeout-minutes: 30
    steps:
${steps}
  other:
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - run: echo done
`;

test("a clean workflow has no problem", () => {
  const steps = `      - uses: actions/checkout@abc
      - name: Weir E2E smoke
        working-directory: apps/server
        run: dotnet test tests/Weir.E2E.Tests --no-build`;
  assert.deepEqual(findProblems("ci.yml", workflow(steps)), []);
});

test("a test step that continues on error is refused, named by its step", () => {
  const steps = `      - name: Weir E2E smoke (Playwright + real .NET server)
        id: e2e
        continue-on-error: true
        run: dotnet test tests/Weir.E2E.Tests --no-build`;
  const problems = findProblems("ci.yml", workflow(steps));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /ci\.yml:9: the test step "Weir E2E smoke \(Playwright \+ real \.NET server\)" has continue-on-error: true/);
});

test("a step whose command, not its name, runs tests is refused too", () => {
  const steps = `      - name: Browser run
        continue-on-error: true
        run: |
          cd apps/web
          npm run test`;
  assert.equal(findProblems("ci.yml", workflow(steps)).length, 1);
});

test("a step that is not a test may continue on error", () => {
  const steps = `      - name: Upload the evidence
        continue-on-error: true
        uses: actions/upload-artifact@abc`;
  assert.deepEqual(findProblems("ci.yml", workflow(steps)), []);
});

test("continue-on-error on another job's step is not charged to a clean test step", () => {
  const steps = `      - name: Weir E2E smoke
        run: dotnet test tests/Weir.E2E.Tests --no-build
      - name: Upload the evidence
        continue-on-error: true
        uses: actions/upload-artifact@abc`;
  assert.deepEqual(findProblems("ci.yml", workflow(steps)), []);
});

test("a job with no timeout and a workflow with no permissions are still refused", () => {
  const problems = findProblems("ci.yml", "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n");
  assert.equal(problems.length, 2);
});

const pullRequestWorkflow = (jobs, trigger = "pull_request:") => `name: ci
on:
  push:
    branches: ["main"]
  ${trigger}
  workflow_dispatch:
permissions:
  contents: read
jobs:
${jobs}
`;

const windowsLegCall = (condition) => `  contract-windows:
    needs: changes
${condition}    uses: ./.github/workflows/ci-contract.yml
`;

test("a Windows job that a pull request can start must carry the path filter", () => {
  const problems = findProblems("ci.yml", pullRequestWorkflow(windowsLegCall("")));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /ci\.yml: job "contract-windows" runs on Windows .* no path filter/);
  assert.deepEqual(findProblems("ci.yml", pullRequestWorkflow(windowsLegCall("    if: needs.changes.outputs.run_contract_windows == 'true'\n"))), []);
});

test("a filter that is not a path flag does not excuse a Windows job", () => {
  const problems = findProblems("ci.yml", pullRequestWorkflow(windowsLegCall("    if: github.event_name != 'schedule'\n")));
  assert.equal(problems.length, 1);
});

test("a Windows runner or a Windows scenario shard is a Windows job whatever the job is called", () => {
  const runner = `  tray:
    runs-on: windows-latest
    timeout-minutes: 10
    steps:
      - run: echo hi
`;
  assert.equal(findProblems("ci.yml", pullRequestWorkflow(runner)).length, 1);
  const shard = `  scenarios:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - run: node scripts/scenarios-in-ci.mjs windows-tray --setup x
`;
  assert.equal(findProblems("ci.yml", pullRequestWorkflow(shard)).length, 1);
});

test("a workflow no pull request starts may hold Windows jobs without a filter", () => {
  const nightly = `  nightly:
    runs-on: windows-latest
    timeout-minutes: 10
    steps:
      - run: echo hi
`;
  assert.deepEqual(findProblems("ci-stress.yml", pullRequestWorkflow(nightly, "schedule:")), []);
});

test("mutation testing is refused in a workflow a pull request triggers, and allowed in a scheduled one", () => {
  const stryker = `  mutate:
    runs-on: ubuntu-latest
    timeout-minutes: 200
    steps:
      - run: dotnet stryker -f config.json
`;
  const problems = findProblems("mutation.yml", pullRequestWorkflow(stryker));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /runs mutation testing and is triggered by a pull request/);
  assert.deepEqual(findProblems("mutation.yml", pullRequestWorkflow(stryker, "schedule:")), []);
  assert.equal(findProblems("mutation.yml", pullRequestWorkflow(stryker, "pull_request_target:")).length, 1);
});
