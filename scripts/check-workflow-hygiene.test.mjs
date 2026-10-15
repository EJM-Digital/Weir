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
