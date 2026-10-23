// node --test scripts/check-node-docker-version.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { checkNodeDockerVersion, main } from "./check-node-docker-version.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-node-docker-version.mjs");

function check(nodeVersion, dockerfile) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-node-version-"));
  try {
    writeFileSync(path.join(root, ".node-version"), nodeVersion);
    writeFileSync(path.join(root, "Dockerfile"), dockerfile);
    return checkNodeDockerVersion(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("the same major in both passes, whether .node-version says v24 or 24.1.0", () => {
  assert.deepEqual(check("24\n", "FROM node:24-alpine@sha256:abc AS web\n"), { ok: true, message: "Dockerfile's node image (major 24) matches .node-version." });
  assert.equal(check("v24.1.0\n", "FROM --platform=x\nFROM node:24 AS web\n").ok, true);
});

test("a Dockerfile on another major fails, naming both", () => {
  const result = check("24\n", "FROM node:22-alpine\n");
  assert.equal(result.ok, false);
  assert.match(result.message, /Dockerfile's node image is major 22, but \.node-version pins 24/);
});

test("a .node-version that is not a version fails, quoting it", () => {
  assert.deepEqual(check("latest\n", "FROM node:24\n"), { ok: false, message: '.node-version does not start with a version number: "latest"' });
});

test("a Dockerfile with no node image fails", () => {
  assert.match(check("24\n", "FROM alpine:3\n").message, /no `FROM node:<major>\.\.\.` line/);
});

test("the report goes to the right stream and sets the exit code", () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-node-version-"));
  try {
    writeFileSync(path.join(root, ".node-version"), "24\n");
    writeFileSync(path.join(root, "Dockerfile"), "FROM node:22\n");
    const seen = { log: [], error: [] };
    const out = { log: (line) => seen.log.push(line), error: (line) => seen.error.push(line) };
    assert.equal(main(root, out), 1);
    writeFileSync(path.join(root, "Dockerfile"), "FROM node:24\n");
    assert.equal(main(root, out), 0);
    assert.equal(seen.error.length, 1);
    assert.equal(seen.log.length, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("run as a command it checks this repository and exits 0", () => {
  const result = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /matches \.node-version/);
});
