// node --test scripts/e2e-shards.test.mjs
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { TESTS_PER_SHARD, allClasses, assignShards, classesIn, filterFor, shardCountFor } from "./e2e-shards.mjs";

const script = fileURLToPath(new URL("./e2e-shards.mjs", import.meta.url));

const file = (name, tests, derives = "E2ETestBase(server)") =>
  `namespace Weir.E2E.Tests;\npublic sealed class ${name}(E2EServer server) : ${derives}\n{\n${"    [E2EFact]\n    public async Task T() {}\n".repeat(tests)}}\n`;

test("a class is read with its namespace and the tests it holds", () => {
  assert.deepEqual(classesIn(file("A", 3)), [{ name: "Weir.E2E.Tests.A", tests: 3 }]);
});

test("two classes in one file each keep their own tests", () => {
  const source = `${file("A", 2)}${file("B", 1).replace("namespace Weir.E2E.Tests;\n", "")}`;
  assert.deepEqual(classesIn(source), [
    { name: "Weir.E2E.Tests.A", tests: 2 },
    { name: "Weir.E2E.Tests.B", tests: 1 },
  ]);
});

test("tests in a file with no class to run them are refused, not dropped", () => {
  assert.throws(() => classesIn("namespace X;\npublic class A { [E2EFact] public void T() {} }", "A.cs"), /A\.cs.*no class deriving from E2ETestBase/);
});

test("a helper class that is not a test class is not a shard's concern", () => {
  assert.deepEqual(classesIn("namespace X;\npublic sealed class Helper(int a) : IDisposable {}"), []);
});

test("a class split over partial files adds its tests up", () => {
  assert.deepEqual(allClasses([["A.cs", file("A", 2)], ["A.More.cs", file("A", 3)]]), [{ name: "Weir.E2E.Tests.A", tests: 5 }]);
});

const classes = [
  ["A", 9], ["B", 7], ["C", 6], ["D", 5], ["E", 5], ["F", 4], ["G", 2], ["H", 1], ["I", 1],
].map(([name, tests]) => ({ name: `Weir.E2E.Tests.${name}`, tests }));

test("every class is in exactly one shard, however many shards there are", () => {
  for (const count of [1, 2, 3, 4, 9]) {
    const shards = assignShards(classes, count);
    assert.equal(shards.length, count);
    assert.deepEqual(shards.flatMap((shard) => shard.classes).sort(), classes.map((entry) => entry.name).sort());
  }
});

test("the shards are balanced to within the heaviest class", () => {
  const shards = assignShards(classes, 3);
  const loads = shards.map((shard) => shard.tests);
  assert.ok(Math.max(...loads) - Math.min(...loads) <= 9, `loads ${loads}`);
  assert.deepEqual(loads.reduce((sum, load) => sum + load, 0), 40);
});

test("the same source gives the same shards every time", () => {
  assert.deepEqual(assignShards(classes, 3), assignShards([...classes].reverse(), 3));
});

test("the number of shards grows with the tests so none passes the per-shard size", () => {
  const of = (tests) => shardCountFor([{ name: "A", tests }]);
  assert.equal(of(1), 1);
  assert.equal(of(TESTS_PER_SHARD), 1);
  assert.equal(of(TESTS_PER_SHARD + 1), 2);
});

test("the filter names whole classes, so one name cannot match inside another", () => {
  assert.equal(filterFor(["Weir.E2E.Tests.A", "Weir.E2E.Tests.AB"]), "FullyQualifiedName~Weir.E2E.Tests.A.|FullyQualifiedName~Weir.E2E.Tests.AB.");
});

test("on the real test project every class is in some shard and the matrix lists each shard", () => {
  const matrix = JSON.parse(execFileSync("node", [script, "--matrix"], { encoding: "utf8" }));
  assert.deepEqual(matrix, matrix.map((_, index) => index + 1));
  const filters = matrix.map((shard) => execFileSync("node", [script, "--shard", String(shard)], { encoding: "utf8" }).trim());
  const named = filters.flatMap((filter) => filter.split("|"));
  assert.ok(named.length > 0);
  assert.equal(new Set(named).size, named.length, "a class is in two shards");
});

test("on the real test project the shards hold every browser test there is", () => {
  const project = fileURLToPath(new URL("../apps/server/tests/Weir.E2E.Tests", import.meta.url));
  const sources = readdirSync(project, { recursive: true })
    .filter((name) => name.endsWith(".cs") && !/(^|[\/])(bin|obj)[\/]/.test(name))
    .map((name) => readFileSync(path.join(project, name), "utf8"));
  const written = sources.reduce((sum, source) => sum + (source.match(/\[E2EFact\b/g) ?? []).length, 0);
  const summary = execFileSync("node", [script, "--summary"], { encoding: "utf8" });
  const sharded = [...summary.matchAll(/: (\d+) tests/g)].reduce((sum, match) => sum + Number(match[1]), 0);
  assert.ok(written > 0);
  assert.equal(sharded, written);
});
