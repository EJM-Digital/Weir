// node --test scripts/check-release-workflow-gates-command.test.mjs
// The release-gate check as a command, and the workflow shapes check-release-workflow-gates.test.mjs does not break.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { SUCCESS_MESSAGE, checkReleaseGates, loadWorkflows } from "./check-release-workflow-gates.mjs";

const script = fileURLToPath(new URL("./check-release-workflow-gates.mjs", import.meta.url));
const real = loadWorkflows();

function replaceOnce(text, from, to) {
  assert.ok(text.includes(from), `the text to replace is not in the workflow: ${from}`);
  return text.replace(from, () => to);
}

const rejects = (edit, pattern) => assert.throws(() => checkReleaseGates({ ...real, ...edit }), pattern);

test("a workflow without a job the check requires is refused by name", () => {
  rejects({ mutation: replaceOnce(real.mutation, "\n  select:\n", "\n  chosen:\n") }, /mutation\.yml is missing required job: select/);
});

test("ci-passed must list its needs on one line", () => {
  rejects({ ci: replaceOnce(real.ci, "  ci-passed:\n    needs: [", "  ci-passed:\n    needs:\n      - [") }, /ci\.yml ci-passed must declare its needs as needs: \[job, \.\.\.\] on one line\./);
});

test("each publish job must attest exactly one thing", () => {
  const without = replaceOnce(real.release, "        uses: actions/attest-build-provenance@", "        uses: actions/something-else@");
  rejects({ release: without }, /must attest .*; found 0 attestation step\(s\)\./);
});

test("ci-passed's list of jobs must be the ci.yml jobs it judges", () => {
  const withExtra = `${real.ci.trimEnd()}\n\n  zzz-extra:\n    runs-on: ubuntu-latest\n`;
  rejects({ ci: replaceOnce(withExtra, "  ci-passed:\n    needs: [changes,", "  ci-passed:\n    needs: [zzz-extra, changes,") }, /scripts\/ci-passed\.mjs GATES \(.*\) must list exactly the ci\.yml jobs it judges \(.*zzz-extra.*\)\./);
});

test("the evidence the release reads must still be a job of ci.yml under its name", () => {
  const renamed = replaceOnce(real.ci, "  ci-passed:\n    needs:", "  ci-passed:\n    name: Renamed verdict\n    needs:");
  rejects({ ci: renamed }, /ci\.yml has no job named "ci-passed", which scripts\/verify-ci-for-release\.mjs requires/);
});

test("run as a command it reports the release gates as sound, and names the break in a workflow that is not", () => {
  const sound = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(sound.status, 0, sound.stderr);
  assert.equal(sound.stdout.trim(), SUCCESS_MESSAGE);

  const folder = mkdtempSync(path.join(tmpdir(), "weir-workflows-"));
  try {
    const files = { "release.yml": real.release, "ci.yml": real.ci, "ci-contract.yml": real.ciContract, "ci-packaging.yml": real.ciPackaging, "mutation.yml": real.mutation };
    files["mutation.yml"] = replaceOnce(real.mutation, "\n  select:\n", "\n  chosen:\n");
    for (const [name, text] of Object.entries(files)) writeFileSync(path.join(folder, name), text);

    const broken = spawnSync(process.execPath, [script, "--workflows", folder], { encoding: "utf8" });
    assert.equal(broken.status, 1);
    assert.match(broken.stderr, /^::error::\.github\/workflows\/mutation\.yml is missing required job: select/);
    assert.equal(broken.stdout, "");
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});
