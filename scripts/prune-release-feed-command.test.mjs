// node --test scripts/prune-release-feed-command.test.mjs
// prune-release-feed.mjs as a command over an output folder made for the test, and the odd feeds it can be given.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { filterAssetFeed, filterLegacyReleases, prune } from "./prune-release-feed.mjs";

const script = fileURLToPath(new URL("./prune-release-feed.mjs", import.meta.url));
const BOM = "﻿";

const run = (...args) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });

function outputFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-prune-"));
  try {
    body(folder);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

test("a feed with no asset list keeps nothing and drops nothing", () => {
  const { kept, dropped, json } = filterAssetFeed('{"Other":1}', "1.0.1");
  assert.deepEqual([kept, dropped], [[], []]);
  assert.deepEqual(JSON.parse(json), { Other: 1, Assets: [] });
});

test("the legacy feed is read with or without its byte-order mark, and a line with no file name is dropped", () => {
  const own = "AAA Weir-1.0.1-full.nupkg 10";
  assert.deepEqual(filterLegacyReleases(own, "1.0.1").dropped, []);
  assert.equal(filterLegacyReleases(own, "1.0.1").text, BOM + own);
  assert.deepEqual(filterLegacyReleases(`${BOM}${own}\nlonely`, "1.0.1").dropped, ["lonely"]);
});

test("a feed file that cannot be read for any reason but being absent stops the prune", () => {
  outputFolder((folder) => {
    mkdirSync(path.join(folder, "releases.win.json"));
    assert.throws(() => prune(folder, "1.0.1"), { code: "EISDIR" });
  });
});

test("the command asks for both options and exits 2 without them", () => {
  for (const args of [[], ["--output-dir"], ["--output-dir", "x"], ["--version", "1.0.1"]]) {
    const refused = run(...args);
    assert.equal(refused.status, 2, args.join(" "));
    assert.match(refused.stderr, /Usage: node scripts\/prune-release-feed\.mjs --output-dir <dir> --version <X\.Y\.Z>/);
  }
});

test("the command says there is nothing to prune in a folder that holds only its own version", () => {
  outputFolder((folder) => {
    writeFileSync(path.join(folder, "Weir-1.0.1-full.nupkg"), "x");
    const result = run("--output-dir", folder, "--version", "1.0.1");
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /No other-version release assets found in .*; nothing to prune\./);
  });
});

test("the command removes other versions' packages and feed entries, and names each one", () => {
  outputFolder((folder) => {
    writeFileSync(path.join(folder, "Weir-1.0.0-full.nupkg"), "old");
    writeFileSync(path.join(folder, "Weir-1.0.1-full.nupkg"), "new");
    writeFileSync(
      path.join(folder, "releases.win.json"),
      JSON.stringify({ Assets: [{ Version: "1.0.0", FileName: "Weir-1.0.0-full.nupkg" }, { Version: "1.0.1", FileName: "Weir-1.0.1-full.nupkg" }] }),
    );
    writeFileSync(path.join(folder, "RELEASES"), `${BOM}AAA Weir-1.0.0-full.nupkg 3\nBBB Weir-1.0.1-full.nupkg 3`);

    const result = run("--output-dir", folder, "--version", "1.0.1");

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Removed other-version package: Weir-1\.0\.0-full\.nupkg/);
    assert.match(result.stdout, /Dropped releases\.win\.json entry: Weir-1\.0\.0-full\.nupkg \(1\.0\.0\)/);
    assert.match(result.stdout, /Dropped RELEASES line: AAA Weir-1\.0\.0-full\.nupkg 3/);
    assert.equal(existsSync(path.join(folder, "Weir-1.0.0-full.nupkg")), false);
    assert.equal(readFileSync(path.join(folder, "RELEASES"), "utf8"), `${BOM}BBB Weir-1.0.1-full.nupkg 3`);
  });
});
