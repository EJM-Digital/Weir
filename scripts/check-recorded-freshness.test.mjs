// node --test scripts/check-recorded-freshness.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { changelogVersions, foldersOnDisk, problems, releasesBehind } from "./check-recorded-freshness.mjs";
import { parseSemver } from "./semver.mjs";

const versions = (...texts) => texts.map((text) => parseSemver(text));
const line = (version) => `- **${version}** (2026-10-14). Something. [notes](docs/release-notes/v${version}.md)`;

const released = versions("1.0.0-rc.16", "1.0.0-rc.17", "1.0.0-rc.18", "1.0.0-rc.19", "1.0.0-rc.20", "1.0.0", "1.0.1");
const folder = "deluno-side/1.0.0-rc.30.32";
const against = (recorded, repository) => problems({ [folder]: { weirVersion: recorded } }, [folder], released, parseSemver(repository));

test("recordings of the version the repository is at pass", () => {
  assert.deepEqual(against("1.0.0-rc.18", "1.0.0-rc.18"), []);
});

test("recordings one candidate behind pass", () => {
  assert.deepEqual(against("1.0.0-rc.18", "1.0.0-rc.19"), []);
});

test("recordings two candidates behind fail, naming both versions and the set", () => {
  const found = against("1.0.0-rc.18", "1.0.0-rc.20");
  assert.equal(found.length, 1);
  assert.match(found[0], /deluno-side\/1\.0\.0-rc\.30\.32/);
  assert.match(found[0], /made against 1\.0\.0-rc\.18/);
  assert.match(found[0], /repository is at 1\.0\.0-rc\.20, 2 releases later/);
});

test("recordings of a build newer than the repository are not behind", () => {
  assert.deepEqual(against("1.0.0-rc.19", "1.0.0-rc.18"), []);
});

test("the stable release after candidates counts as one release after the last candidate", () => {
  assert.deepEqual(against("1.0.0-rc.20", "1.0.0"), []);
  assert.deepEqual(against("1.0.0", "1.0.0"), []);
  assert.equal(against("1.0.0-rc.19", "1.0.0").length, 1);
});

test("a stable release after a stable release is counted the same way", () => {
  assert.deepEqual(against("1.0.0", "1.0.1"), []);
  assert.equal(against("1.0.0-rc.20", "1.0.1").length, 1);
});

test("the newest of several sets is the one that is held", () => {
  const sets = { "deluno/1.0.0-rc.13": { weirVersion: "1.0.0-rc.13" }, [folder]: { weirVersion: "1.0.0-rc.19" } };
  assert.deepEqual(problems(sets, ["deluno/1.0.0-rc.13", folder], released, parseSemver("1.0.0-rc.20")), []);
});

test("a set with no Weir version fails", () => {
  assert.deepEqual(problems({ [folder]: {} }, [folder], released, parseSemver("1.0.0-rc.18")), [
    `${folder}: its entry in weir-builds-recorded.json has no "weirVersion".`,
  ]);
  assert.deepEqual(problems({ [folder]: { weirVersion: "  " } }, [folder], released, parseSemver("1.0.0-rc.18")), [
    `${folder}: its entry in weir-builds-recorded.json has no "weirVersion".`,
  ]);
});

test("a malformed Weir version fails", () => {
  assert.deepEqual(against("rc.18", "1.0.0-rc.18"), [`${folder}: "rc.18" in weir-builds-recorded.json is not a Weir version such as 1.0.0-rc.18.`]);
  assert.equal(against("1.0.0-rc.18+build", "1.0.0-rc.18").length, 1);
});

test("a recording folder nobody listed fails, so a new set cannot skip the check", () => {
  assert.deepEqual(problems({ [folder]: { weirVersion: "1.0.0-rc.18" } }, [folder, "deluno-side/1.0.0-rc.30.40"], released, parseSemver("1.0.0-rc.18")), [
    'deluno-side/1.0.0-rc.30.40 holds recordings but weir-builds-recorded.json does not say which Weir build they were made against. Add its "weirVersion".',
  ]);
});

test("a listed set with no folder fails", () => {
  assert.deepEqual(problems({ [folder]: { weirVersion: "1.0.0-rc.18" }, "deluno/1.0.0-rc.9": { weirVersion: "1.0.0-rc.9" } }, [folder], released, parseSemver("1.0.0-rc.18")), [
    "weir-builds-recorded.json lists deluno/1.0.0-rc.9, which holds no recordings.",
  ]);
});

test("a list with nothing in it fails", () => {
  assert.equal(problems({}, [], released, parseSemver("1.0.0-rc.18")).length, 1);
});

test("a candidate the changelog never listed still counts by its number", () => {
  assert.equal(releasesBehind(parseSemver("1.0.0-rc.18"), parseSemver("1.0.0-rc.30"), versions("1.0.0-rc.18", "1.0.0-rc.30")), 12);
  assert.equal(releasesBehind(parseSemver("1.0.0-rc.30"), parseSemver("1.0.0-rc.18"), versions("1.0.0-rc.18")), 0);
});

test("the changelog's release lines are read in order, and other lines are not", () => {
  const text = ["# Changelog", "", "## 1.x", "", line("1.0.0-rc.18"), line("1.0.0-rc.17"), "- **not a version** (2026-10-14).", "Prose with **1.0.0-rc.99** inside."].join("\n");
  assert.deepEqual(
    changelogVersions(text).map((version) => version.version),
    ["1.0.0-rc.18", "1.0.0-rc.17"],
  );
});

const script = fileURLToPath(new URL("./check-recorded-freshness.mjs", import.meta.url));
const run = (...args) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });

test("the checked-in recordings are all listed", () => {
  assert.equal(foldersOnDisk(fileURLToPath(new URL("../apps/server/tests/fixtures/recorded", import.meta.url))).length > 0, true);
});

test("the release gate passes the tag, and a tag two releases past the newest recording is refused", () => {
  const stale = run("--version", "v1.0.0-rc.30");
  assert.equal(stale.status, 1);
  assert.match(stale.stderr, /The recordings of Weir are too old/);
  assert.match(stale.stderr, /1\.0\.0-rc\.30/);
  assert.equal(run("--version", "not-a-version").status, 1);
  assert.equal(run("--version").status, 2);
});
