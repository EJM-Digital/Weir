// node --test scripts/repository-check-commands.test.mjs
// The repository checks as commands: each is run on this repository, which has to pass it, and the ones that take arguments are shown
// what they say when given none or a wrong one. The rules each check applies are tested in its own test file.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = (script, args = [], options = {}) => spawnSync(process.execPath, [path.join(repository, "scripts", script), ...args], { cwd: repository, encoding: "utf8", ...options });

const made = [];
after(() => {
  for (const folder of made) rmSync(folder, { recursive: true, force: true });
});

const passing = [
  ["check-no-python.mjs", /^No Python files are tracked\.$/],
  ["check-event-titles.mjs", /^Every one of the server's \d+ event types has a title\.$/],
  ["check-test-console-programs.mjs", /^No test source starts a system console program \(\d+ files checked\)\.$/],
  ["check-wall-clock-waits.mjs", /^No test waits for the wall clock beyond the \d+ polls listed with their reasons/],
  ["check-windows-paths.mjs", /^Every Windows-only server file is matched by the \d+ patterns/],
  ["check-release-fixtures.mjs", /^Release fixtures are fresh: \d+ releases/],
  ["check-contract-areas.mjs", /^Contract areas match their test classes, and each runs on Linux and Windows: /],
  ["check-workflow-hygiene.mjs", /^Every workflow has least-privilege top-level permissions/],
  ["check-repository-owner.mjs", /is written only where its language keeps it\.$/],
  ["check-coverage-exclusions.mjs", /^\d+ coverage exclusions /],
  ["check-recorded-freshness.mjs", /./],
];
for (const [script, expected] of passing) {
  test(`${script} passes on this repository`, () => {
    const result = run(script);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout.trim(), expected);
  });
}

test("the freshness check takes a version, and refuses one that is not a release version or has no value", () => {
  assert.equal(run("check-recorded-freshness.mjs", ["--version"]).status, 2);
  assert.match(run("check-recorded-freshness.mjs", ["--version"]).stderr, /^Usage: node scripts\/check-recorded-freshness\.mjs/);
  const bad = run("check-recorded-freshness.mjs", ["--version", "banana"]);
  assert.equal(bad.status, 1);
  assert.match(bad.stderr, /::error::"banana" is not a release version in the form 1\.0\.0-rc\.1 or 1\.0\.0\./);
});

test("a release tag is checked for its form, and the command says what it takes", () => {
  assert.equal(run("check-release-version.mjs").status, 2);
  assert.match(run("check-release-version.mjs").stderr, /^Usage: node scripts\/check-release-version\.mjs <tag>/);
  const bad = run("check-release-version.mjs", ["v1.x"]);
  assert.equal(bad.status, 1);
  assert.match(bad.stderr, /^::error::/);
  const rc = run("check-release-version.mjs", ["v3.2.5-rc.1"]);
  assert.equal(rc.status, 0);
  assert.match(rc.stdout, /^Release tag v3\.2\.5-rc\.1 is a well-formed pre-release version \(/);
  assert.match(run("check-release-version.mjs", ["v3.2.5"]).stdout, /is a well-formed release version/);
});

test("a folder of release assets must hold only the version being released", () => {
  assert.equal(run("check-release-assets-single-version.mjs").status, 2);
  assert.match(run("check-release-assets-single-version.mjs").stderr, /^Usage: node scripts\/check-release-assets-single-version\.mjs <dir> <X\.Y\.Z>/);

  const folder = mkdtempSync(path.join(tmpdir(), "weir-assets-"));
  made.push(folder);
  writeFileSync(path.join(folder, "Weir-3.2.5-full.nupkg"), "");
  const fine = run("check-release-assets-single-version.mjs", [folder, "3.2.5"]);
  assert.equal(fine.status, 0);
  assert.equal(fine.stdout.trim(), `${folder} carries only version 3.2.5's release assets.`);

  writeFileSync(path.join(folder, "Weir-3.2.4-full.nupkg"), "");
  const mixed = run("check-release-assets-single-version.mjs", [folder, "3.2.5"]);
  assert.equal(mixed.status, 1);
  assert.match(mixed.stderr, new RegExp(`^::error::.* would publish assets for a version other than 3\\.2\\.5:\\r?\\n {2}- `));
});

test("the previous release is found from the tags on standard input, and the command says what it takes", () => {
  assert.equal(run("find-previous-release.mjs").status, 2);
  assert.match(run("find-previous-release.mjs").stderr, /^Usage: node scripts\/find-previous-release\.mjs <version being released>/);
  assert.equal(run("find-previous-release.mjs", ["3.2.5"], { input: "v3.2.4\nv3.2.3\n\nv3.2.5\n" }).stdout.trim(), "3.2.4");
  assert.equal(run("find-previous-release.mjs", ["1.0.0"], { input: "" }).stdout.trim(), "");
});

test("mutation targets are selected from a list of changed files, or all of them, and written for the workflow", () => {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-targets-"));
  made.push(folder);
  const changed = path.join(folder, "changed.txt");
  writeFileSync(changed, "docs/README.md\n");
  const output = path.join(folder, "output.txt");

  const none = run("select-mutation-targets.mjs", ["--changed", changed], { env: { ...process.env, GITHUB_OUTPUT: output } });
  assert.equal(none.status, 0, none.stderr);
  assert.equal(none.stdout.trim(), "No mutation target is touched by this change.");

  const all = run("select-mutation-targets.mjs", ["--all"], { env: { ...process.env, GITHUB_OUTPUT: output } });
  assert.equal(all.status, 0, all.stderr);
  assert.match(all.stdout, /^Mutation targets to run: /);

  const refused = run("select-mutation-targets.mjs", ["--nope"]);
  assert.equal(refused.status, 1);
  assert.match(refused.stderr, /^::error::/);

  const requested = run("resolve-mutation-targets.mjs", [], { env: { ...process.env, REQUESTED: "core-rules", GITHUB_OUTPUT: output } });
  assert.equal(requested.status, 0, requested.stderr);
  assert.equal(requested.stdout.trim(), "Mutation targets to run: core-rules");
  const unknown = run("resolve-mutation-targets.mjs", [], { env: { ...process.env, REQUESTED: "no-such-target" } });
  assert.equal(unknown.status, 1);
  assert.match(unknown.stderr, /^::error::/);
});
