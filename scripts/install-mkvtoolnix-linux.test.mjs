// node --test scripts/install-mkvtoolnix-linux.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";

import { placeMkvmerge } from "./install-mkvtoolnix-linux.mjs";

function extractedAppImage(root) {
  const extracted = path.join(root, "squashfs-root");
  mkdirSync(path.join(extracted, "usr", "bin", "platforms"), { recursive: true });
  mkdirSync(path.join(extracted, "usr", "lib"), { recursive: true });
  writeFileSync(path.join(extracted, "usr", "bin", "mkvmerge"), '#!/bin/sh\necho "libs=$LD_LIBRARY_PATH args=$*"\nexit 3\n');
  chmodSync(path.join(extracted, "usr", "bin", "mkvmerge"), 0o755);
  writeFileSync(path.join(extracted, "usr", "bin", "mkvtoolnix-gui"), "gui");
  writeFileSync(path.join(extracted, "usr", "lib", "libQt6Core.so.6"), "library");
  return extracted;
}

test("only mkvmerge and its libraries are kept, with a launcher beside them", () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-mkvtoolnix-test-"));
  try {
    const destination = path.join(root, "installed");
    placeMkvmerge(extractedAppImage(root), destination);

    assert.equal(readFileSync(path.join(destination, "usr", "lib", "libQt6Core.so.6"), "utf8"), "library");
    assert.ok(statSync(path.join(destination, "usr", "bin", "mkvmerge")).isFile());
    assert.throws(() => statSync(path.join(destination, "usr", "bin", "mkvtoolnix-gui")), { code: "ENOENT" });
    assert.ok(statSync(path.join(destination, "mkvmerge")).isFile());
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the launcher runs mkvmerge with its own libraries, passes the arguments on and keeps the exit code", { skip: process.platform === "win32" }, () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-mkvtoolnix-test-"));
  try {
    const destination = path.join(root, "installed");
    placeMkvmerge(extractedAppImage(root), destination);

    const run = spawnSync(path.join(destination, "mkvmerge"), ["--version", "a b"], { encoding: "utf8", env: { PATH: process.env.PATH } });

    assert.equal(run.status, 3);
    assert.equal(run.stdout.trim(), `libs=${path.join(destination, "usr", "lib")} args=--version a b`);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
