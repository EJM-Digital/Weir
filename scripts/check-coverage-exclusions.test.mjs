// node --test scripts/check-coverage-exclusions.test.mjs
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { collectItems, compare, configItems, EXCLUSIONS_FILE, KINDS, readExclusions, reasonProblem, run, sourceItems } from "./check-coverage-exclusions.mjs";

const REASON = "Code a tool wrote, which nobody maintains by hand.";

const entry = (over = {}) => ({ id: "one", file: "apps/server/coverage.runsettings", marker: "**/obj/**", kind: "generated", reason: REASON, ...over });
const parse = (entries) => readExclusions(JSON.stringify({ exclusions: entries }));

test("a reason has to say something", () => {
  assert.equal(reasonProblem(REASON), undefined);
  assert.equal(reasonProblem(undefined), "has no reason");
  assert.equal(reasonProblem(""), "has no reason");
  assert.equal(reasonProblem("   "), "has no reason");
  assert.equal(reasonProblem(42), "has no reason");
  assert.match(reasonProblem("TODO"), /says nothing/);
  assert.match(reasonProblem("n/a"), /says nothing/);
  assert.match(reasonProblem("because"), /says nothing/);
  assert.match(reasonProblem("generated"), /too short/);
  assert.match(reasonProblem("not shipped code"), /too short/);
});

test("an entry with no reason fails, and says which entry", () => {
  const { problems } = parse([entry({ reason: undefined })]);
  assert.deepEqual(problems, ['Exclusion "one" has no reason.']);
});

test("an entry with a reason that says nothing fails", () => {
  const { problems } = parse([entry({ reason: "tbd" })]);
  assert.deepEqual(problems, ['Exclusion "one" has a reason that says nothing ("tbd").']);
});

test("a good entry passes", () => {
  assert.deepEqual(parse([entry()]).problems, []);
});

test("an entry needs an id, a file, a marker and a known kind", () => {
  const { problems } = parse([{ reason: REASON }]);
  assert.deepEqual(problems, [
    "Exclusion number 1 has no id.",
    "Exclusion number 1 does not say which file it is in.",
    "Exclusion number 1 does not say what it leaves out (marker).",
    `Exclusion number 1 has the kind "undefined"; use one of: ${Object.keys(KINDS).join(", ")}.`,
  ]);
  assert.match(parse([entry({ kind: "slow" })]).problems[0], /has the kind "slow"/);
});

test("an id is used once", () => {
  assert.deepEqual(parse([entry(), entry()]).problems, ['Exclusion "one" is listed twice.']);
});

test("platform-only code names the system that runs it and the test that proves it there", () => {
  const proved = { kind: "platform-only", coveredBy: "windows", provedBy: "the windows-install scenario" };
  assert.deepEqual(parse([entry(proved)]).problems, []);
  assert.deepEqual(parse([entry({ kind: "platform-only" })]).problems, [
    'Exclusion "one" is platform-only, so it has to name the system that runs it (coveredBy: linux or windows).',
    'Exclusion "one" is platform-only, so it has to name the test or scenario that runs it there (provedBy).',
  ]);
  assert.match(parse([entry({ ...proved, coveredBy: "macos" })]).problems[0], /name the system/);
  assert.match(parse([entry({ ...proved, provedBy: "  " })]).problems[0], /name the test or scenario/);
});

test("a count is a whole number above zero", () => {
  assert.match(parse([entry({ count: 0 })]).problems[0], /count that is not a whole number/);
  assert.match(parse([entry({ count: 1.5 })]).problems[0], /count that is not a whole number/);
  assert.deepEqual(parse([entry({ count: 2 })]).problems, []);
});

test("a file that is not JSON, or has no list, is refused", () => {
  assert.match(readExclusions("{").problems[0], /not valid JSON/);
  assert.match(readExclusions("{}").problems[0], /needs an "exclusions" list/);
});

test("the run settings' exclusions are read name by name, comments left out", () => {
  const settings = `<RunSettings><!-- <Exclude>[Not.Real]*</Exclude> --><Exclude>[A.*Tests]*, [B]*</Exclude>
    <ExcludeByAttribute>GeneratedCodeAttribute,CompilerGeneratedAttribute</ExcludeByAttribute><ExcludeByFile>**/obj/**</ExcludeByFile>
    <Include>[A]*</Include></RunSettings>`;
  assert.deepEqual(configItems("apps/server/coverage.runsettings", settings), [
    "[A.*Tests]*",
    "[B]*",
    "GeneratedCodeAttribute",
    "CompilerGeneratedAttribute",
    "**/obj/**",
  ]);
});

test("the out-of-process settings' excluded attributes and sources are read, and their includes are not", () => {
  const settings = `<Configuration><CodeCoverage><ModulePaths><Include><ModulePath>.*Weir\\.dll$</ModulePath></Include></ModulePaths>
    <Attributes><Exclude><Attribute>^A$</Attribute><Attribute>^B$</Attribute></Exclude></Attributes>
    <Sources><Exclude><Source>.*obj.*</Source></Exclude></Sources></CodeCoverage></Configuration>`;
  assert.deepEqual(configItems("apps/server/coverage.out-of-process.xml", settings), ["^A$", "^B$", ".*obj.*"]);
});

test("the web coverage exclusions are read from the Vite config and its include is not", () => {
  const config = `export default { test: { coverage: { provider: "v8", include: ["src/**/*.ts"],
    exclude: ["src/**/*.test.ts", "src/test/**"], reporter: ["lcov"] } } };`;
  assert.deepEqual(configItems("apps/web/vite.config.ts", config), ["src/**/*.test.ts", "src/test/**"]);
  assert.deepEqual(configItems("apps/web/vite.config.ts", "export default {};"), []);
});

test("an ExcludeFromCodeCoverage attribute in the server or tray code is a marker, wherever it is written", () => {
  const code = `[ExcludeFromCodeCoverage]
public sealed class A {}
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "x")]
public sealed class B {}
[Obsolete, ExcludeFromCodeCoverageAttribute]
public sealed class C {}
// [ExcludeFromCodeCoverage] in a comment is still read, so write the reason in the exclusions file instead
public static class D { public const string Name = "ExcludeFromCodeCoverage"; }`;
  assert.equal(sourceItems("apps/server/src/Weir.Core/A.cs", code).length, 4);
  assert.equal(sourceItems("apps/tray/Weir.Tray/A.cs", code).length, 4);
  assert.equal(sourceItems("apps/server/tests/Weir.Core.Tests/A.cs", code).length, 0);
});

test("a v8, istanbul or c8 ignore comment in the web code is a marker, a stop comment is not, and test files are left alone", () => {
  const code = "/* v8 ignore next */\nconst a = 1;\n/* v8 ignore start */\nconst b = 2;\n/* v8 ignore stop */\n// istanbul ignore if\n/* c8 ignore next 2 */";
  assert.equal(sourceItems("apps/web/src/lib/a.ts", code).length, 4);
  assert.equal(sourceItems("apps/web/src/lib/a.test.ts", code).length, 0);
  assert.equal(sourceItems("apps/web/src/test/setup.ts", code).length, 0);
  assert.equal(sourceItems("apps/web/src/lib/api/generated/types.ts", code).length, 0);
});

test("items are counted by file and marker, and only in the files that can leave something out", () => {
  const files = { "apps/server/src/Weir.Core/A.cs": "[ExcludeFromCodeCoverage]\n[ExcludeFromCodeCoverage]", "apps/server/src/Weir.Core/B.cs": "class B {}" };
  const items = collectItems([...Object.keys(files), "README.md"], (file) => files[file]);
  assert.deepEqual(items, [{ file: "apps/server/src/Weir.Core/A.cs", marker: "ExcludeFromCodeCoverage", found: 2 }]);
});

test("something left out and not listed fails, naming the file", () => {
  const items = [{ file: "apps/server/src/Weir.Core/A.cs", marker: "ExcludeFromCodeCoverage", found: 1 }];
  const problems = compare([], items);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /apps\/server\/src\/Weir.Core\/A.cs leaves out "ExcludeFromCodeCoverage" and docs\/coverage\/exclusions.json does not say why/);
});

test("a listed exclusion that is gone fails, so the list never keeps a stale line", () => {
  const problems = compare([entry()], []);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /no longer leaves that out/);
});

test("an entry for a file that is gone fails", () => {
  const problems = compare([entry()], [], () => false);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /not in the repository/);
});

test("the number of markers in a file has to match the number listed", () => {
  const items = [{ file: "apps/server/src/Weir.Core/A.cs", marker: "ExcludeFromCodeCoverage", found: 2 }];
  const one = entry({ file: items[0].file, marker: items[0].marker, kind: "unreachable" });
  assert.match(compare([one], items)[0], /has 2 of "ExcludeFromCodeCoverage" and .* lists 1/);
  assert.deepEqual(compare([{ ...one, count: 2 }], items), []);
  assert.deepEqual(compare([one, { ...one, id: "two" }], items), []);
});

test("the repository's own exclusions are all listed, and every one has a reason", () => {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const { problems, summary } = run(root);
  assert.deepEqual(problems, []);
  assert.match(summary, /coverage exclusions .* each with a reason/);
});

test("a repository folder with no exclusions file is refused, and one with a bare marker and no entry is too", () => {
  const root = mkdtempSync(path.join(tmpdir(), "exclusions-"));
  try {
    assert.match(run(root, []).problems[0], /is missing/);
    mkdirSync(path.join(root, "docs", "coverage"), { recursive: true });
    mkdirSync(path.join(root, "apps", "server", "src", "Weir.Core"), { recursive: true });
    writeFileSync(path.join(root, EXCLUSIONS_FILE), JSON.stringify({ exclusions: [] }));
    writeFileSync(path.join(root, "apps", "server", "src", "Weir.Core", "A.cs"), "[ExcludeFromCodeCoverage]\npublic sealed class A {}\n");
    const { problems } = run(root, ["apps/server/src/Weir.Core/A.cs"]);
    assert.equal(problems.length, 1);
    assert.match(problems[0], /does not say why/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
