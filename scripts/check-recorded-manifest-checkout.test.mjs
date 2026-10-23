// node --test scripts/check-recorded-manifest-checkout.test.mjs
// The manifest check over a whole checkout of recordings (Deluno's side and the seven apps), made for the test with --checkout.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("./check-recorded-manifest.mjs", import.meta.url));
const RECORDED = "apps/server/tests/fixtures/recorded";
const APPS = ["sonarr", "radarr", "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"];

function checkout(body) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-manifest-checkout-"));
  try {
    for (const folder of ["deluno-side", ...APPS]) {
      for (const [version, state] of [["2.0.0", "b"], ["1.0.0", "b"], ["1.0.0", "a"]]) {
        const file = path.join(root, RECORDED, folder, version, `${state}.json`);
        mkdirSync(path.dirname(file), { recursive: true });
        writeFileSync(file, `{"app":"${folder}","state":"${state}"}\n`);
      }
    }
    return body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const run = (root, ...args) => spawnSync(process.execPath, [script, "--checkout", root, ...args], { cwd: root, encoding: "utf8" });

test("--write makes the manifest of every app's folder of the checkout, and the check then passes for all eight folders", () => {
  checkout((root) => {
    const written = run(root, "--write");
    assert.equal(written.status, 0, written.stderr);
    assert.equal(written.stdout.trim().split("\n").length, APPS.length);

    // Deluno's folder takes its manifest from Deluno, so it is copied in from one of the app folders here.
    const delunoSide = path.join(root, RECORDED, "deluno-side");
    assert.equal(run(root, "--write", "--root", delunoSide).status, 0);

    const checked = run(root);
    assert.equal(checked.status, 0, checked.stderr);
    assert.equal(checked.stdout.trim().split("\n").length, 1 + APPS.length);
    assert.match(checked.stdout, /Every recording under apps[\\/]server[\\/]tests[\\/]fixtures[\\/]recorded[\\/]deluno-side matches its manifest\./);
  });
});

test("a folder that is the working folder is called '.', and a recording that changed makes the whole check exit 1", () => {
  checkout((root) => {
    const folder = path.join(root, RECORDED, "sonarr");
    assert.equal(run(root, "--write", "--root", folder).status, 0);
    const here = spawnSync(process.execPath, [script, "--root", "."], { cwd: folder, encoding: "utf8" });
    assert.equal(here.status, 0, here.stderr);
    assert.match(here.stdout, /^Every recording under \. matches its manifest\./);

    writeFileSync(path.join(folder, "1.0.0", "a.json"), "{}\n");
    const edited = spawnSync(process.execPath, [script, "--root", "."], { cwd: folder, encoding: "utf8" });
    assert.equal(edited.status, 1);
    assert.match(edited.stderr, /^The recordings under \. and their manifest disagree \(#971, #1013, #1015\):\n {2}1\.0\.0\/a\.json changed:/);
    assert.match(edited.stderr, /A recording is a byte-for-byte copy and is never edited\./);
  });
});

test("--manifest names another manifest to hold the folder to", () => {
  checkout((root) => {
    const folder = path.join(root, RECORDED, "radarr");
    const other = path.join(root, "other-manifest.sha256");
    writeFileSync(other, "");
    const run_ = run(root, "--root", folder, "--manifest", other);
    assert.equal(run_.status, 1);
    assert.match(run_.stderr, /1\.0\.0\/a\.json is not in the manifest \(a new recording\)\./);
  });
});
