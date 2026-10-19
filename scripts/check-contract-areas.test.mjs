// node --test scripts/check-contract-areas.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { areasUsedIn, compareAreas, platformProblems } from "./check-contract-areas.mjs";

test("the area a class names is read from its attribute", () => {
  assert.deepEqual(areasUsedIn('[ContractArea("media_managers")]\npublic sealed class A {}'), ["media_managers"]);
  assert.deepEqual(areasUsedIn("public sealed class A {}"), []);
});

test("a list that matches the classes has no problems", () => {
  assert.deepEqual(compareAreas(["auth", "jobs"], new Map([["auth", "A.cs"], ["jobs", "B.cs"]])), []);
});

test("a listed area with no class would be a leg that passes empty", () => {
  const problems = compareAreas(["auth", "jobs"], new Map([["auth", "A.cs"]]));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /"jobs".*no test class/);
});

test("an area no leg runs is refused", () => {
  const problems = compareAreas(["auth"], new Map([["auth", "A.cs"], ["jobs", "B.cs"]]));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /B\.cs.*"jobs".*never run/);
});

test("a name listed twice is refused", () => {
  const problems = compareAreas(["auth", "auth"], new Map([["auth", "A.cs"]]));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /more than once/);
});

const legs = [
  "        platform: [linux, windows]",
  "    runs-on: ${{ matrix.platform == 'windows' && 'windows-latest' || 'ubuntu-latest' }}",
  '        run: node scripts/check-contract-real-tool-tests.mjs "$CONTRACT_AREA" apps/server/tests/Weir.Contract.Tests "$PLATFORM" "Real-tool scenarios"',
].join("\n");

test("a contract workflow with a leg per area on each platform has no problems", () => {
  assert.deepEqual(platformProblems(legs), []);
});

test("a contract workflow that runs on Linux only is refused", () => {
  const problems = platformProblems(legs.replace("[linux, windows]", "[linux]"));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /windows/);
});

test("a leg that does not run on a Windows runner is refused", () => {
  const problems = platformProblems(legs.replace("windows-latest", "ubuntu-latest"));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /windows-latest/);
});

test("a real-tool check that is told it is always on Linux is refused", () => {
  const problems = platformProblems(legs.replace('"$PLATFORM"', "linux"));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /platform/);
});
