// node --test scripts/scenarios-in-ci-command.test.mjs
// The command that runs a shard: what it asks for, what it refuses, and what it writes and exits with once the shard has run.
// The shards themselves (Docker Compose, a Windows runner) are replaced by stand-ins here and run in ci-packaging.yml.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import { UsageError, chooseScenarios, main } from "./scenarios-in-ci.mjs";
import { loadHomes } from "./scenarios-ci/homes.mjs";

const folders = [];
after(() => {
  for (const folder of folders) rmSync(folder, { recursive: true, force: true });
});

/** Runs the command with stand-in shards; returns what it exited with, printed and was asked to run. */
async function command(argv, { env = {}, platform = "linux", results = [{ id: "weir-only-film", status: "passed", detail: "", seconds: 1 }], collectFailed = false, homes } = {}) {
  const logs = mkdtempSync(path.join(tmpdir(), "weir-shard-logs-"));
  folders.push(logs);
  const ran = [];
  const printed = [];
  const complaints = [];
  const shards = {
    runLinuxShard: async (options) => (ran.push(["linux", options]), { results, collectFailed }),
    runWindowsShardHere: async (options) => (ran.push(["windows", options]), results),
  };
  const code = await main([...argv, "--logs", logs], { env, platform, shards, homes, log: (line) => printed.push(line), error: (line) => complaints.push(line) });
  return { code, ran, printed, complaints, logs };
}

const SETUP = ["windows-install", "--setup", "Weir-win-Setup.exe"];
const RUNNER = { env: { GITHUB_ACTIONS: "true", RUNNER_ENVIRONMENT: "github-hosted" }, platform: "win32" };

test("a Linux shard needs the image to test", async () => {
  const { code, complaints, ran } = await command(["weir-only"]);
  assert.equal(code, 2);
  assert.match(complaints[0], /^weir-only runs against an image, so --candidate is needed\.\nUsage: node scripts\/scenarios-in-ci\.mjs <linux shard>/);
  assert.deepEqual(ran, []);
});

test("a Windows shard needs the Setup, and the update shard everything it names", async () => {
  assert.match((await command(["windows-install"], RUNNER)).complaints[0], /^windows-install runs against a Setup, so --setup is needed\./);
  assert.match((await command(["windows-update", "--feed", "feed", "--version", "1.2.3"], RUNNER)).complaints[0], /needs --previous-setup, --previous-feed, --previous-version\./);
});

test("anything else is not a shard, and the ones there are are listed", async () => {
  const { code, complaints } = await command(["nonsense"]);
  assert.equal(code, 2);
  assert.match(complaints[0], /^The shard must be one of weir-only, .*windows-install/);
  assert.equal((await command([])).code, 2);
});

test("scenario homes that disagree with the catalogue stop the run before it starts", async () => {
  const homes = structuredClone(loadHomes());
  homes.scenarios.push({ id: "invented", home: "linux", shard: "weir-only", state: "ported", why: "x" });
  const { code, complaints } = await command(["weir-only", "--candidate", "image"], { homes });
  assert.equal(code, 2);
  assert.match(complaints[0], /^The scenario homes \(scripts\/scenarios-ci\/homes\.json\) are wrong:\ninvented/);
});

test("--only picks among the ported scenarios of the shard, and one that is not is refused by name", async () => {
  const homes = loadHomes();
  assert.deepEqual(chooseScenarios(homes, "weir-only", "process-again, pause-resume"), ["process-again", "pause-resume"]);
  assert.ok(chooseScenarios(homes, "weir-only").includes("weir-only-film"));
  assert.throws(() => chooseScenarios(homes, "weir-only", "tray-menu"), (error) => error instanceof UsageError && /^tray-menu is not a ported scenario of weir-only \(/.test(error.message));
  assert.throws(() => chooseScenarios(homes, "weir-only", "tray-menu,fresh-install"), /tray-menu, fresh-install are not a ported scenario of weir-only/);
  const { code, complaints } = await command(["weir-only", "--candidate", "image", "--only", "tray-menu"]);
  assert.equal(code, 2);
  assert.match(complaints[0], /tray-menu is not a ported scenario of weir-only/);
});

test("a shard that passes is summarised, written down and exits 0", async () => {
  const stepSummary = path.join(mkdtempSync(path.join(tmpdir(), "weir-step-summary-")), "summary.md");
  folders.push(path.dirname(stepSummary));
  const { code, ran, printed, complaints, logs } = await command(["weir-only", "--candidate", "weir-image", "--only", "weir-only-film"], { env: { GITHUB_STEP_SUMMARY: stepSummary } });

  assert.equal(code, 0);
  assert.deepEqual(complaints, []);
  assert.deepEqual(ran, [["linux", { shard: "weir-only", ids: ["weir-only-film"], candidate: "weir-image", logFolder: path.join(logs, "weir-only") }]]);
  assert.match(printed[0], /weir-only-film/);
  const written = JSON.parse(readFileSync(path.join(logs, "weir-only", "results.json"), "utf8"));
  assert.deepEqual([written.shard, written.candidate, written.results.length], ["weir-only", "weir-image", 1]);
  assert.equal(readFileSync(stepSummary, "utf8"), printed[0]);
});

test("a scenario that failed is reported by name and the exit code is 1, as is a shard whose recordings could not be kept", async () => {
  const failing = [{ id: "weir-only-film", status: "failed", detail: "the film kept every track", seconds: 3 }, { id: "process-again", status: "passed", detail: "", seconds: 1 }];
  const failed = await command(["weir-only", "--candidate", "image"], { results: failing });
  assert.equal(failed.code, 1);
  assert.deepEqual(failed.complaints, ["::error::weir-only-film: the film kept every track"]);

  const unkept = await command(["weir-only", "--candidate", "image"], { collectFailed: true });
  assert.equal(unkept.code, 1);
  assert.deepEqual(unkept.complaints, []);
});

test("a Windows shard is refused on any machine but a GitHub-hosted Windows runner", async () => {
  const mine = await command(SETUP);
  assert.equal(mine.code, 2);
  assert.match(mine.complaints[0], /^windows-install installs and uninstalls Weir for real, and this is linux, not Windows\./);
  assert.match((await command(SETUP, { platform: "win32" })).complaints[0], /this is not a GitHub-hosted runner/);
  assert.deepEqual(mine.ran, []);
});

test("a Windows shard is run on the Setup, and the update shard with its feed and the previous release", async () => {
  const install = await command([...SETUP, "--version", "1.2.3"], RUNNER);
  assert.equal(install.code, 0);
  assert.deepEqual(install.ran[0][1], { shard: "windows-install", ids: install.ran[0][1].ids, setup: "Weir-win-Setup.exe", version: "1.2.3", logFolder: path.join(install.logs, "windows-install"), update: undefined });
  assert.equal(JSON.parse(readFileSync(path.join(install.logs, "windows-install", "results.json"), "utf8")).setup, "Weir-win-Setup.exe");

  const update = await command(
    ["windows-update", "--feed", "feed", "--previous-setup", "old/Setup.exe", "--previous-feed", "old/releases.win.json", "--previous-version", "v1.2.2", "--version", "1.2.3"],
    RUNNER,
  );
  assert.equal(update.code, 0);
  const given = update.ran[0][1].update;
  assert.deepEqual(given, {
    feedFolder: path.resolve("feed"),
    previousSetup: path.resolve("old/Setup.exe"),
    previousFeed: path.resolve("old/releases.win.json"),
    previousVersion: "1.2.2",
  });
  assert.equal(JSON.parse(readFileSync(path.join(update.logs, "windows-update", "results.json"), "utf8")).setup, "feed");
  assert.match(update.printed[0], /Feed of the build under test/);
});

test("run as a command with nothing to run it says what it takes and exits 2", () => {
  const script = fileURLToPath(new URL("./scenarios-in-ci.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script], { encoding: "utf8", cwd: path.resolve(path.dirname(script), "..") });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /The shard must be one of .*\nUsage: node scripts\/scenarios-in-ci\.mjs/);
});
