// node --test scripts/repository-check-failures.test.mjs
// What each repository check says, and the exit code it ends with, when the repository breaks its rule. repository-check-commands.test.mjs
// shows each pass on this repository; here each is given a small repository of its own (--root) that fails.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

import { findProblems } from "./check-workflow-hygiene.mjs";
import { repository } from "./release/temporary-repository.mjs";
import { rootOf } from "./repository-root.mjs";

const scripts = fileURLToPath(new URL(".", import.meta.url));

/** Runs `script` on a git repository holding `files` (`{ path: text }`), through --root. */
function runOn(script, files, ...extra) {
  const { git, commit } = repository();
  commit(files, "fixture");
  const root = git(["rev-parse", "--show-toplevel"]).trim();
  return spawnSync(process.execPath, [`${scripts}${script}`, "--root", root, ...extra], { encoding: "utf8" });
}

function fails(run, firstLine) {
  assert.equal(run.status, 1, `${run.stdout}${run.stderr}`);
  assert.match(run.stderr, firstLine);
  assert.equal(run.stdout, "");
}

test("the root is the checkout the script is in, unless --root names another", () => {
  const script = pathToFileURL(path.resolve("repo", "scripts", "check.mjs")).href;
  assert.equal(rootOf(script, []), path.resolve("repo"));
  assert.equal(rootOf(script, ["--root", "elsewhere"]), path.resolve("elsewhere"));
});

test("a tracked Python file fails the no-Python check, listing it", () => {
  fails(runOn("check-no-python.mjs", { "tools/run.py": "print(1)\n" }), /^Weir has no Python\. Remove these files, or write the tool in Node, PowerShell or \.NET:\n {2}tools\/run\.py/);
});

test("the event-title check fails on sources that hold no event types, and on a type with no title", () => {
  const sources = (constants) => ({
    "apps/server/src/Weir.Core/Activity/ActivityEventTypes.cs": constants,
    "apps/server/src/Weir.Core/MediaManagers/LibraryFileChangeRules.cs": "",
    "apps/web/src/lib/activity/event-labels.ts": 'export const titles = {\n  "file.moved": "Moved",\n};\n',
  });
  fails(runOn("check-event-titles.mjs", sources("// nothing\n")), /^No event types were found in the server sources; this check's pattern is out of date\./);
  fails(
    runOn("check-event-titles.mjs", sources('const string Moved = "file.moved";\nconst string Lost = "file.lost";\n')),
    /^These event types have no title in apps\/web\/src\/lib\/activity\/event-labels\.ts, so the log would show their raw names:\n {2}file\.lost\n$/,
  );
});

test("a test that starts a system console program fails the console-program check, naming file and line", () => {
  const source = 'class T { void Run() {\n  Start("ping 127.0.0.1");\n} }\n';
  fails(runOn("check-test-console-programs.mjs", { "apps/server/tests/Weir.Api.Tests/T.cs": source }), /^A test must not start a system console program;[^\n]*\n- apps\/server\/tests\/Weir\.Api\.Tests\/T\.cs:2: "ping 127\.0\.0\.1"/);
});

test("a wait for the wall clock that the list does not allow fails the wall-clock check, and so does a list entry that is stale", () => {
  const files = (allowed) => ({
    "apps/server/tests/Weir.Api.Tests/T.cs": "class T { async Task Run() {\n  await Task.Delay(50);\n} }\n",
    "scripts/wall-clock-waits-allowed.json": JSON.stringify({ allowed }),
  });
  fails(runOn("check-wall-clock-waits.mjs", files([])), /^A test waits for the wall clock:\n- apps\/server\/tests\/Weir\.Api\.Tests\/T\.cs:2: /);
  const stale = { file: "apps/server/tests/Weir.Api.Tests/Gone.cs", kind: "delay", count: 1, reason: "A poll that is long gone from the tests." };
  fails(runOn("check-wall-clock-waits.mjs", files([stale])), /the list allows 1 delay wait\(s\) and the file has 0/);
});

test("a Windows-only server file that the ci.yml filter misses fails the Windows-paths check", () => {
  const workflow = ["jobs:", "  changes:", "    steps:", "      - with:", "          filters: |", "            windows:", "              - 'apps/tray/**'", ""].join("\n");
  fails(
    runOn("check-windows-paths.mjs", { ".github/workflows/ci.yml": workflow, "apps/server/src/Weir.Infrastructure/Firewall.cs": "class Firewall {}\n" }),
    /^These server files are Windows-only, but the `windows:` filter[^\n]*\n {2}- apps\/server\/src\/Weir\.Infrastructure\/Firewall\.cs\nAdd the file, or its folder, to that filter\./,
  );
});

test("the workflow hygiene check fails on a workflow that grants write access and has no jobs", () => {
  fails(runOn("check-workflow-hygiene.mjs", { ".github/workflows/a.yml": "name: A\npermissions: write-all\n" }), /^Workflow hygiene check failed:\n- \.github\/workflows\/a\.yml: top-level permissions is write-all\.\n- \.github\/workflows\/a\.yml: no jobs: section\./);
});

test("workflow hygiene names a top-level write grant, a missing jobs section and an unquoted output comparison", () => {
  const withWrite = findProblems("w.yml", "permissions:\n  contents: write # releases\n  issues: read\njobs:\n  a:\n    timeout-minutes: 5\n");
  assert.deepEqual(withWrite, ["w.yml: top-level permissions grants write access (contents: write # releases); grant it on the one job that needs it."]);
  assert.deepEqual(findProblems("w.yml", "permissions:\n  contents: read\n"), ["w.yml: no jobs: section."]);
  const unquoted = findProblems("w.yml", "permissions:\n  contents: read\njobs:\n  a:\n    timeout-minutes: 5\n    if: needs.changes.outputs.run_web == true\n");
  assert.deepEqual(unquoted, ["w.yml:6: outputs are strings; compare with 'true' in quotes."]);
});

test("release fixtures that are not in the manifest fail the fixture check", () => {
  const changelog = "- **1.0.0** (2026-01-01). [release](https://example.test/1)\n";
  const manifest = JSON.stringify({ databases: [{ version: "0.9.0" }] });
  fails(
    runOn("check-release-fixtures.mjs", { "CHANGELOG.md": changelog, "apps/server/tests/fixtures/databases/manifest.json": manifest }),
    /^The newest release fixture is 0\.9\.0, but 1\.0\.0 is published/,
  );
});

test("a changelog with no release line fails the recorded-freshness check", () => {
  fails(runOn("check-recorded-freshness.mjs", { "CHANGELOG.md": "# Changelog\n" }), /^::error::CHANGELOG\.md has no release line, so the repository's version cannot be read\./);
});

test("contract areas that the tests do not use fail the contract-areas check", () => {
  const files = {
    "apps/server/tests/Weir.Contract.Tests/areas.json": JSON.stringify({ areas: [{ name: "Intake" }] }),
    "apps/server/tests/Weir.Contract.Tests/bin/Ignored.cs": '[ContractArea("Built")] class Ignored {}\n',
    ".github/workflows/ci-contract.yml": "name: contract\n",
    ".github/workflows/ci.yml": "name: ci\n",
  };
  fails(runOn("check-contract-areas.mjs", files), /^areas\.json lists "Intake", but no test class is marked \[ContractArea\("Intake"\)\]\./);
});

/** A repository with one recording of each kind; the contract tests name `replayed` (in a string literal each). */
function recordingsRepository(replayed) {
  const files = {
    "apps/server/tests/fixtures/recorded/deluno-side/1.0.0/real-a.json": "{}",
    "apps/server/tests/fixtures/recorded/deluno-side-no-weir-behaviour.json": "{}",
    "apps/server/tests/fixtures/recorded/deluno-side-awaiting-replay.json": "{}",
    "apps/server/tests/Weir.Contract.Tests/Replays.cs": `${replayed.map((state) => `var s = "${state}";`).join("\n")}\n`,
    "apps/server/src/Weir.Core/Server.cs": "class Server {}\n",
  };
  for (const app of ["sonarr", "radarr", "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"]) {
    files[`apps/server/tests/fixtures/recorded/${app}/1.0/queue.json`] = "{}";
  }
  return files;
}

test("the replay check passes when every recording is replayed, and says which are not when one is not", () => {
  const passed = runOn("check-recorded-replays.mjs", recordingsRepository(["real-a", "queue"]));
  assert.equal(passed.status, 0, passed.stderr);
  assert.match(passed.stdout, /^Every recording of Deluno's own side is replayed by a contract test/);

  const failed = runOn("check-recorded-replays.mjs", recordingsRepository(["real-a"]));
  assert.equal(failed.status, 1);
  assert.doesNotMatch(failed.stderr, /disagree \(#971\)/);
  assert.match(failed.stderr, /^The Sonarr, Radarr and download client recordings and the tests that replay them disagree \(#1013, #1014, #1015\):\n {2}sonarr\/queue is recorded but no test replays it/);

  const both = runOn("check-recorded-replays.mjs", recordingsRepository([]));
  assert.equal(both.status, 1);
  assert.match(both.stderr, /^Deluno's recordings and the tests that replay them disagree \(#971\):\n {2}real-a is recorded but no contract test replays it[\s\S]*\(#1013, #1014, #1015\):/);
});

test("the coverage exclusions check fails when the exclusions file is missing", () => {
  const run = runOn("check-coverage-exclusions.mjs", { "README.md": "x\n" });
  assert.equal(run.status, 1);
  assert.match(run.stderr, /^::error::docs\/coverage\/exclusions\.json is missing\./);
});
