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
  "        platform: ${{ fromJSON(inputs.platforms) }}",
  "    runs-on: ${{ matrix.platform == 'windows' && 'windows-latest' || 'ubuntu-latest' }}",
  '        run: node scripts/check-contract-real-tool-tests.mjs "$CONTRACT_AREA" apps/server/tests/Weir.Contract.Tests "$PLATFORM" "Real-tool scenarios"',
].join("\n");

test("a contract workflow with a leg per area on each platform has no problems", () => {
  assert.deepEqual(platformProblems(legs), []);
});

const caller = ["      platforms: '[\"linux\"]'", "      platforms: '[\"windows\"]'"].join("\n");

test("ci.yml calling the suite once for Linux and once for Windows has no problems", () => {
  assert.deepEqual(platformProblems(legs, caller), []);
});

test("a contract workflow with a fixed platform list is refused", () => {
  const problems = platformProblems(legs.replace("${{ fromJSON(inputs.platforms) }}", "[linux, windows]"));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /inputs.platforms/);
});

test("a ci.yml that never calls the suite for Windows, or for Linux, is refused", () => {
  const windows = platformProblems(legs, "      platforms: '[\"linux\"]'");
  assert.equal(windows.length, 1);
  assert.match(windows[0], /["windows"].*runs on Windows/);
  const linux = platformProblems(legs, "      platforms: '[\"windows\"]'");
  assert.equal(linux.length, 1);
  assert.match(linux[0], /["linux"].*runs on Linux/);
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
