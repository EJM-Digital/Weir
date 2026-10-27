// node --test scripts/publish-release-command.test.mjs
// publish-release.mjs as a command: reading the files named, running gh, and what it does when gh does not answer as hoped.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { main, publish, runGh } from "./publish-release.mjs";

const script = fileURLToPath(new URL("./publish-release.mjs", import.meta.url));

test("runGh runs the GitHub CLI with the arguments and returns what it printed", () => {
  const seen = [];
  const output = runGh(["release", "view", "v1"], (program, args, options) => {
    seen.push({ program, args, stdio: options.stdio });
    return "answer";
  });
  assert.equal(output, "answer");
  assert.deepEqual(seen, [{ program: "gh", args: ["release", "view", "v1"], stdio: ["ignore", "pipe", "pipe"] }]);
});

test("the command measures each file, makes the release, and says how many files it published", () => {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-publish-"));
  try {
    const package_ = path.join(folder, "Weir-win-Setup.exe");
    writeFileSync(package_, "12345");
    const calls = [];
    const attached = [];
    const gh = (args) => {
      calls.push(args.slice(0, 2).join(" "));
      if (args[1] === "view") return JSON.stringify({ isDraft: true, assets: attached });
      if (args[1] === "upload") attached.push({ name: "Weir-win-Setup.exe", size: 5 });
      return "";
    };
    const lines = [];

    main(["v1.0.0", "notes.md", "false", package_], { gh, log: (line) => lines.push(line) });

    assert.deepEqual(lines, ["Published v1.0.0 with 1 files."]);
    assert.deepEqual(calls.filter((call) => call !== "release view"), ["release upload", "release edit"]);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("a release that gh cannot find is made, even when its answer has no assets", () => {
  const calls = [];
  const gh = (args) => {
    calls.push(args[1]);
    if (args[1] === "view") return calls.filter((verb) => verb === "view").length === 1 ? "null" : JSON.stringify({ isDraft: true });
    return "";
  };
  publish({ tag: "v1", notesFile: "n.md", prerelease: false, files: [], gh });
  assert.deepEqual(calls, ["view", "create", "view", "view", "edit"]);
});

test("a gh failure that is not 'release not found' is passed on, with or without a stderr to read", () => {
  const stderrOnly = () => {
    throw Object.assign(new Error("exit 1"), { stderr: "HTTP 502" });
  };
  assert.throws(() => publish({ tag: "v1", notesFile: "n.md", prerelease: false, files: [], gh: stderrOnly }), /exit 1/);

  const notFoundByMessage = (args) => {
    if (args[1] === "view") throw new Error("release not found");
    return "";
  };
  const files = [{ path: "a.zip", name: "a.zip", size: 1 }];
  assert.throws(() => publish({ tag: "v1", notesFile: "n.md", prerelease: false, files, gh: notFoundByMessage }), /draft is still missing a\.zip/);
});

test("run as a command, a file that is not there stops it before gh is asked anything", () => {
  const result = spawnSync(process.execPath, [script, "v1.0.0", "notes.md", "false", path.join(tmpdir(), "weir-no-such-file.exe")], { encoding: "utf8" });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ENOENT/);
});
