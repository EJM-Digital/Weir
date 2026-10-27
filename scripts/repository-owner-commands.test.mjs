// node --test scripts/repository-owner-commands.test.mjs
// The two repository-owner commands run as commands, against a small git repository made for the test (--root).
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { FORMER_OWNERS, PLACES, findProblems } from "./check-repository-owner.mjs";
import { REPOSITORY } from "./repository.mjs";

const check = fileURLToPath(new URL("./check-repository-owner.mjs", import.meta.url));
const sweep = fileURLToPath(new URL("./sweep-repository-owner.mjs", import.meta.url));

const run = (script, ...args) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });

function repositoryWith(files) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-owner-"));
  for (const [name, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
    writeFileSync(path.join(root, name), text);
  }
  execFileSync("git", ["init", "--quiet"], { cwd: root });
  execFileSync("git", ["add", "--all"], { cwd: root });
  return root;
}

const placeFiles = () => ({
  "scripts/repository.mjs": `export const REPOSITORY = "${REPOSITORY}";\n`,
  "scripts/check-repository-owner.mjs": `export const FORMER_OWNERS = ${JSON.stringify(FORMER_OWNERS)};\n`,
  "apps/server/src/Weir.Core/Updates/ProjectLocation.cs": `const string Owner = "${REPOSITORY.split("/")[0]}"; const string Repo = "Weir";\n`,
  "apps/tray/Weir.Tray/ProjectLocation.cs": `const string Owner = "${REPOSITORY.split("/")[0]}"; const string Repo = "Weir";\n`,
  "apps/web/src/lib/repository.ts": `export const REPOSITORY_SLUG = "${REPOSITORY}";\n`,
  "scripts/repository.ps1": `$WeirRepository = '${REPOSITORY}'\n`,
  "docs/readme.md": `Releases: https://github.com/${REPOSITORY}/releases\nDeluno: https://github.com/${REPOSITORY.split("/")[0]}/Deluno\n`,
});

test("a place that has no repository in it is named as holding none", () => {
  const files = PLACES.map((place) => ({ path: place.path, text: "nothing here" }));
  const problems = findProblems({ files, repository: REPOSITORY });
  assert.equal(problems.length, PLACES.length);
  for (const problem of problems) assert.match(problem, /holds no repository/);
});

test("the check passes on this repository, whether or not it is told which repository it runs in", () => {
  assert.equal(run(check).status, 0);
  const told = run(check, "--repository", REPOSITORY);
  assert.equal(told.status, 0, told.stderr);
  assert.match(told.stdout, /is written only where its language keeps it/);
});

test("the check names what is wrong, counts it and exits 1; a tracked file that is gone from disk is skipped", () => {
  const root = repositoryWith({ "gone.txt": "x", "binary.bin": "a\0b" });
  rmSync(path.join(root, "gone.txt"));
  try {
    const result = run(check, "--root", root);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /holds no repository/);
    assert.match(result.stderr, /\d+ problem\(s\)\. The owner and name are written once per language/);
    assert.doesNotMatch(result.stderr, /gone\.txt|binary\.bin/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the sweep refuses an owner/name it cannot read, and does nothing when Weir is already there", () => {
  for (const args of [[], ["--to"], ["--to", "not a repository"], ["--to", "a/b/c"]]) {
    const refused = run(sweep, ...args);
    assert.equal(refused.status, 2, args.join(" "));
    assert.match(refused.stderr, /Usage: node scripts\/sweep-repository-owner\.mjs --to <owner\/name>/);
  }
  const same = run(sweep, "--to", REPOSITORY);
  assert.equal(same.status, 0);
  assert.match(same.stdout, new RegExp(`Weir is already ${REPOSITORY}; nothing to sweep`));
});

test("the sweep moves the places, the former owner and the documentation, and then the check passes", () => {
  const root = repositoryWith(placeFiles());
  try {
    const swept = run(sweep, "--to", "New-Org/Weir", "--root", root);
    assert.equal(swept.status, 0, swept.stderr);
    assert.match(swept.stdout, /^7 files now say New-Org\/Weir instead of /);
    assert.match(swept.stdout, /Next: node scripts\/check-repository-owner\.mjs/);

    const read = (name) => readFileSync(path.join(root, name), "utf8");
    assert.match(read("scripts/repository.mjs"), /"New-Org\/Weir"/);
    assert.match(read("apps/web/src/lib/repository.ts"), /"New-Org\/Weir"/);
    assert.equal(read("scripts/check-repository-owner.mjs"), `export const FORMER_OWNERS = ${JSON.stringify([...FORMER_OWNERS, REPOSITORY.split("/")[0]]).replace(/,/g, ", ")};\n`);
    assert.equal(read("docs/readme.md"), "Releases: https://github.com/New-Org/Weir/releases\nDeluno: https://github.com/New-Org/Deluno\n");

    const again = run(sweep, "--to", "New-Org/Weir", "--root", root);
    assert.equal(again.status, 0, again.stderr);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
