// node --test scripts/check-github-action-pins.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { main, pinFailures } from "./check-github-action-pins.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-github-action-pins.mjs");
const SHA = "3d3c42e5aac5ba805825da76410c181273ba90b1";

function withWorkflows(files, callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-pins-"));
  try {
    mkdirSync(path.join(root, ".github", "workflows"), { recursive: true });
    for (const [name, lines] of Object.entries(files)) writeFileSync(path.join(root, ".github", "workflows", name), lines.join("\n"));
    callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a step pinned to a full commit with a release comment passes, as does a local action", () => {
  withWorkflows({ "ci.yml": [`      - uses: actions/checkout@${SHA} # v7`, `      - uses: actions/setup-node@${SHA} # v4.1.0`, "      - uses: ./.github/actions/local"] }, (root) => {
    assert.deepEqual(pinFailures(root), { failures: [], workflows: 1 });
  });
});

test("a tag, a short commit and a missing version each fail, with the file and line", () => {
  withWorkflows(
    {
      "ci.yaml": ["jobs:", "  a:", "    steps:", "      - uses: actions/checkout@v4", "      - uses: actions/cache@abc123 # v1", `      - uses: actions/cache@${SHA}`, `      - uses: actions/cache@${SHA} # main`, "      - uses: nover"],
    },
    (root) => {
      assert.deepEqual(pinFailures(root).failures, [
        ".github/workflows/ci.yaml:4: actions/checkout@v4 (not a full commit SHA)",
        ".github/workflows/ci.yaml:5: actions/cache@abc123 (not a full commit SHA)",
        `.github/workflows/ci.yaml:6: actions/cache@${SHA} (no "# vX.Y.Z" comment naming the release)`,
        `.github/workflows/ci.yaml:7: actions/cache@${SHA} (no "# vX.Y.Z" comment naming the release)`,
        ".github/workflows/ci.yaml:8: nover (not a full commit SHA)",
      ]);
    },
  );
});

test("comments and files that are not workflows are ignored", () => {
  withWorkflows({ "ci.yml": ["# uses: actions/checkout@v4", "  # uses: actions/checkout@v4", "name: x"], "notes.md": ["uses: actions/checkout@v4"] }, (root) => {
    assert.deepEqual(pinFailures(root), { failures: [], workflows: 1 });
  });
});

test("the report goes to the right stream and sets the exit code", () => {
  const seen = { log: [], error: [] };
  const out = { log: (line) => seen.log.push(line), error: (line) => seen.error.push(line) };
  withWorkflows({ "ci.yml": [`      - uses: actions/checkout@${SHA} # v7`] }, (root) => assert.equal(main(root, out), 0));
  assert.deepEqual(seen.log, ["All GitHub Actions in 1 workflow(s) are pinned to full commit SHAs."]);
  withWorkflows({ "ci.yml": ["      - uses: actions/checkout@v7"] }, (root) => assert.equal(main(root, out), 1));
  assert.deepEqual(seen.error, ["GitHub Actions must be pinned to a full commit SHA with a release comment:", "- .github/workflows/ci.yml:1: actions/checkout@v7 (not a full commit SHA)"]);
});

test("run as a command it checks this repository and exits 0", () => {
  const result = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /are pinned to full commit SHAs/);
});
