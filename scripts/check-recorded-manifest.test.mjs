// node --test scripts/check-recorded-manifest.test.mjs
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { currentManifest, defaultRoot, hashOf, problems } from "./check-recorded-manifest.mjs";

const script = fileURLToPath(new URL("./check-recorded-manifest.mjs", import.meta.url));

/** A folder of recordings with the manifest that matches it; `change` can then disturb either. */
function withRecordings(run) {
  const root = mkdtempSync(join(tmpdir(), "recorded-manifest-"));
  try {
    mkdirSync(join(root, "1.0.0-rc.1"));
    mkdirSync(join(root, "1.0.0-rc.2"));
    writeFileSync(join(root, "1.0.0-rc.1", "b-second.json"), '{"state":"b-second"}\n');
    writeFileSync(join(root, "1.0.0-rc.1", "a-first.json"), '{"state":"a-first"}\n');
    writeFileSync(join(root, "1.0.0-rc.2", "c-third.json"), '{"state":"c-third"}\n');
    const manifest = join(root, "MANIFEST.sha256");
    writeFileSync(manifest, currentManifest(root));
    return run(root, manifest);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a folder that matches its manifest has no problems, and the manifest is sorted by name", () => {
  withRecordings((root, manifest) => {
    assert.deepEqual(problems(root, manifest), []);
    assert.deepEqual(
      currentManifest(root)
        .trimEnd()
        .split("\n")
        .map((line) => line.slice(66)),
      ["1.0.0-rc.1/a-first.json", "1.0.0-rc.1/b-second.json", "1.0.0-rc.2/c-third.json"],
    );
  });
});

test("a recording that was edited is named, with both hashes", () => {
  withRecordings((root, manifest) => {
    writeFileSync(join(root, "1.0.0-rc.1", "a-first.json"), '{"state":"a-first","edited":true}\n');
    const found = problems(root, manifest);
    assert.equal(found.length, 1);
    assert.match(found[0], /^1\.0\.0-rc\.1\/a-first\.json changed: the manifest holds [0-9a-f]{12}, the file is [0-9a-f]{12}\.$/);
  });
});

test("a recording the manifest does not list, and one it lists that is gone, are both named", () => {
  withRecordings((root, manifest) => {
    writeFileSync(join(root, "1.0.0-rc.2", "d-new.json"), "{}\n");
    rmSync(join(root, "1.0.0-rc.1", "b-second.json"));
    assert.deepEqual(problems(root, manifest), [
      "1.0.0-rc.2/d-new.json is not in the manifest (a new recording).",
      "1.0.0-rc.1/b-second.json is in the manifest but the file is gone.",
    ]);
  });
});

test("a line that is not a hash and a name is reported, and a missing manifest says where to get it", () => {
  withRecordings((root, manifest) => {
    writeFileSync(manifest, currentManifest(root) + "not a hash line\n");
    assert.deepEqual(problems(root, manifest), ['The manifest has a line that is not "<sha256>  <version>/<state>.json": not a hash line']);
    rmSync(manifest);
    assert.match(problems(root, manifest)[0], /is missing\. Copy it from Deluno/);
  });
});

test("a Windows checkout of a recording hashes the same as a Linux one", () => {
  withRecordings((root) => {
    const lf = join(root, "1.0.0-rc.1", "a-first.json");
    const before = hashOf(lf);
    writeFileSync(lf, '{"state":\r\n"a-first"}\r\n');
    const crlf = hashOf(lf);
    writeFileSync(lf, '{"state":\n"a-first"}\n');
    assert.equal(hashOf(lf), crlf);
    assert.notEqual(crlf, before);
    writeFileSync(lf, '﻿{"state":\n"a-first"}\n');
    assert.equal(hashOf(lf), crlf);
  });
});

test("the command fails with a named file and exit code 1 when a recording changes, and passes when it does not", () => {
  withRecordings((root, manifest) => {
    const passed = execFileSync(process.execPath, [script, "--root", root, "--manifest", manifest], { encoding: "utf8" });
    assert.match(passed, /matches its manifest/);
    writeFileSync(join(root, "1.0.0-rc.2", "c-third.json"), "{}\n");
    const failed = spawnSync(process.execPath, [script, "--root", root, "--manifest", manifest], { encoding: "utf8" });
    assert.equal(failed.status, 1);
    assert.match(failed.stderr, /1\.0\.0-rc\.2\/c-third\.json changed/);
  });
});

test("the recordings checked in beside the contract tests match the manifest copied with them", () => {
  assert.deepEqual(problems(defaultRoot, join(defaultRoot, "MANIFEST.sha256")), []);
});

test("--write makes the manifest of a folder, and the check then passes", () => {
  withRecordings((root, manifest) => {
    rmSync(manifest);
    const written = execFileSync(process.execPath, [script, "--write", "--root", root], { encoding: "utf8" });
    assert.match(written, /Wrote .*MANIFEST.sha256/);
    assert.deepEqual(problems(root, manifest), []);
  });
});
