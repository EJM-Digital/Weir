// node --test scripts/make-release-fixtures-steps.test.mjs
// The docker and dotnet steps of make-release-fixtures.mjs, with the programs replaced by a recorder.
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";

import { fetchRelease, imageOf, session } from "./make-release-fixtures.mjs";

const DIGEST = `sha256:${"ab".repeat(32)}`;

function scratchFolder(body) {
  const scratch = mkdtempSync(path.join(os.tmpdir(), "weir-fixture-steps-"));
  try {
    body(scratch);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

/** A docker that answers like the real one; `digest` is what inspect prints and `program` whether the copy brings Weir out. */
function fakeDocker({ digest = `ghcr.io/x/weir@${DIGEST}\n`, program = true } = {}) {
  const calls = [];
  const execute = (name, args, options) => {
    calls.push([name, ...args]);
    switch (args[0]) {
      case "inspect":
        return digest;
      case "create":
        return "container-1\n";
      case "cp":
        if (program) {
          mkdirSync(args[2], { recursive: true });
          writeFileSync(path.join(args[2], "Weir"), "program");
        }
        return undefined;
      default:
        assert.ok(options, "pull and rm are given their options");
        return undefined;
    }
  };
  return { calls, execute };
}

test("a release is pulled, its program copied out of a container, and the container removed", () =>
  scratchFolder((scratch) => {
    const { calls, execute } = fakeDocker();
    const image = imageOf("2.0.0");

    const fetched = fetchRelease("2.0.0", scratch, execute);

    assert.deepEqual(fetched, { executable: path.join(scratch, "release", "Weir"), digest: DIGEST });
    assert.deepEqual(
      calls.map((call) => call.slice(0, 3)),
      [["docker", "pull", "--quiet"], ["docker", "inspect", "--format"], ["docker", "create", "--platform"], ["docker", "cp", "container-1:/opt/weir/."], ["docker", "rm", "--force"]],
    );
    assert.ok(calls[0].includes(image));
    assert.deepEqual(calls.at(-1), ["docker", "rm", "--force", "container-1"]);
  }));

test("an image with no digest is refused before a container is made", () =>
  scratchFolder((scratch) => {
    for (const digest of ["", "ghcr.io/x/weir@sha256:short\n", "no-at-sign\n"]) {
      const { calls, execute } = fakeDocker({ digest });
      assert.throws(() => fetchRelease("2.0.0", scratch, execute), /Docker gave no digest for /);
      assert.equal(calls.some((call) => call[1] === "create"), false);
    }
  }));

test("an image without the program is refused, and its container is still removed", () =>
  scratchFolder((scratch) => {
    const { calls, execute } = fakeDocker({ program: false });
    assert.throws(() => fetchRelease("2.0.0", scratch, execute), /holds no \/opt\/weir\/Weir/);
    assert.deepEqual(calls.at(-1), ["docker", "rm", "--force", "container-1"]);
  }));

test("the session runs the contract suite's fixture session against the program and hands back what it wrote", () =>
  scratchFolder((scratch) => {
    const asked = [];
    const execute = (program, args, options) => {
      asked.push({ program, args, env: options.env });
      mkdirSync(path.join(scratch, "session"), { recursive: true });
      writeFileSync(options.env.WEIR_FIXTURE_DATABASE_OUT, "database");
      writeFileSync(options.env.WEIR_FIXTURE_BACKUP_OUT, "{}");
    };

    const made = session("/release/Weir", scratch, execute);

    assert.deepEqual(made, { raw: path.join(scratch, "session", "weir.sqlite3"), bundle: path.join(scratch, "session", "configuration-bundle.json") });
    assert.equal(asked.length, 1);
    assert.equal(asked[0].program, "dotnet");
    assert.ok(asked[0].args.includes("FullyQualifiedName~ReleaseFixtureSession"));
    assert.equal(asked[0].env.WEIR_CONTRACT_SERVER, "/release/Weir");
    assert.ok(existsSync(made.raw));
  }));

test("a session that wrote nothing, because it was skipped or failed, is refused", () =>
  scratchFolder((scratch) => {
    assert.throws(() => session("/release/Weir", scratch, () => {}), /The session left no database or backup/);
  }));
