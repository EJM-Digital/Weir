// What the windows-install shard decides, tested without a Windows PC: the scenarios run against a small stand-in for the runner and the Weir
// installed on it (scenarios-ci/fake-windows.mjs), healthy and then broken in each way a scenario must catch. The real runner and the real
// Setup are the windows-install job of ci-packaging.yml.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { csrfSignedWith } from "./scenarios-ci/csrf-signature.mjs";
import { FakeWindows } from "./scenarios-ci/fake-windows.mjs";
import { linuxShards, loadHomes, scenariosOfShard, windowsShards } from "./scenarios-ci/homes.mjs";
import { exitCode, runScenarios } from "./scenarios-ci/run.mjs";
import { SCENARIOS, dataFolderProblems, watchLogs } from "./scenarios-ci/windows-install.mjs";
import { consoleEnvironment, whyNotAThrowawayMachine } from "./scenarios-ci/windows-machine.mjs";
import { expectedVersion } from "./scenarios-ci/windows-shard.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(repository, path), "utf8");
const homes = loadHomes();

/** Runs the scenarios in the shard's order against a stand-in with the given fault. */
async function run(behaviour = {}, ids = Object.keys(SCENARIOS)) {
  const stand = new FakeWindows(behaviour);
  const world = { ...stand.world(), watchLogs };
  return runScenarios({ ids, registry: SCENARIOS, world });
}

const only = async (id, behaviour) => (await run(behaviour, [id]))[0];

/** A scenario that fails, with the words of the failure. */
async function failureOf(id, behaviour) {
  const result = await only(id, behaviour);
  assert.equal(result.status, "failed", `${id} passed although ${JSON.stringify(behaviour)}`);
  return result.detail;
}

// --- where the shard runs -----------------------------------------------------------------------------------------------------

test("a PC that is not a GitHub-hosted Windows runner is refused before anything is installed", () => {
  assert.equal(whyNotAThrowawayMachine({ GITHUB_ACTIONS: "true", RUNNER_ENVIRONMENT: "github-hosted" }, "win32"), null);
  assert.match(whyNotAThrowawayMachine({ GITHUB_ACTIONS: "true", RUNNER_ENVIRONMENT: "github-hosted" }, "linux"), /linux, not Windows/);
  assert.match(whyNotAThrowawayMachine({}, "win32"), /not a GitHub-hosted runner/);
  assert.match(whyNotAThrowawayMachine({ GITHUB_ACTIONS: "true", RUNNER_ENVIRONMENT: "self-hosted" }, "win32"), /not a GitHub-hosted runner/);
});

test("the entry point refuses to install anything on a machine that is not a hosted runner", () => {
  const entry = spawnSync(process.execPath, ["scripts/scenarios-in-ci.mjs", "windows-install", "--setup", "Weir-win-Setup.exe"], {
    cwd: repository,
    encoding: "utf8",
    env: { ...process.env, GITHUB_ACTIONS: "", RUNNER_ENVIRONMENT: "" },
  });
  assert.equal(entry.status, 2);
  assert.match(entry.stderr, /installs and uninstalls Weir for real, and this is not a GitHub-hosted runner|installs and uninstalls Weir for real, and this is linux, not Windows/);
});

test("a person's console has no variable of Weir's, whatever its case, and keeps the rest", () => {
  assert.deepEqual(consoleEnvironment({ PATH: "C:\\bin", WEIR_HOME: "C:\\x", Weir_Env: "production", WEIR_BUILD_VERSION: "1.0.0" }), { PATH: "C:\\bin" });
});

test("the build under test reports the version it was given, or the one the server project declares", () => {
  const props = "<Project><PropertyGroup><WeirVersion>3.4.0</WeirVersion></PropertyGroup></Project>";
  assert.equal(expectedVersion({ given: "v1.0.0-rc.4+abc1234", props }), "1.0.0-rc.4+abc1234");
  assert.equal(expectedVersion({ given: "", props }), "3.4.0");
  assert.equal(expectedVersion({ props }), "3.4.0");
  assert.throws(() => expectedVersion({ props: "<Project/>" }), /WeirVersion is not in/);
  assert.match(read("apps/server/Directory.Build.props"), /<WeirVersion>[^<]+<\/WeirVersion>/);
});

test("every ported Windows scenario has code, and every scenario with code is ported", () => {
  const ported = homes.scenarios.filter((scenario) => scenario.shard === "windows-install" && scenario.state !== "to-port").map((scenario) => scenario.id);
  assert.deepEqual(Object.keys(SCENARIOS).sort(), ported.sort());
});

test("the workflow runs the Windows shards that have a ported scenario, against the Setup the package job built", () => {
  const workflow = read(".github/workflows/ci-packaging.yml");
  assert.deepEqual(/scenarios-windows:[\s\S]*?shard: \[([^\]]*)\]/.exec(workflow)?.[1].split(",").map((name) => name.trim()), windowsShards(homes));
  assert.deepEqual(linuxShards(homes).filter((shard) => windowsShards(homes).includes(shard)), []);
  const job = /\n  scenarios-windows:\n([\s\S]*?)(?=\n  [a-z0-9-]+:\n|$)/.exec(workflow)?.[1] ?? "";
  assert.match(job, /runs-on: windows-latest/);
  assert.match(job, /needs: \[windows-package-smoke, windows-update-package\]/);
  assert.match(job, /timeout-minutes: \d+/);
  assert.match(job, /fail-fast: false/);
  assert.match(job, /name: weir-windows-setup-under-test/);
  assert.match(job, /node scripts\/scenarios-in-ci\.mjs "\$SHARD" --setup setup\/Weir-win-Setup\.exe --version "\$BUILD_VERSION"/);
  assert.match(job, /node scripts\/scenarios-in-ci\.mjs windows-update --feed feed --previous-setup previous\/Weir-win-Setup\.exe/);
  assert.match(job, /actions\/upload-artifact@/);
  const kept = /Keep the Setup for the Windows scenarios[\s\S]*?path: (\S+)/.exec(workflow)?.[1];
  assert.equal(kept, "dist/windows/releases/Weir-win-Setup.exe");
});

test("the shard is its scenarios in order, the one written for CI last", () => {
  assert.deepEqual(scenariosOfShard(homes, "windows-install").map((scenario) => scenario.id), ["fresh-install", "recover-locked-out-account", "uninstall-keeps-the-data"]);
});

// --- the CSRF signature -------------------------------------------------------------------------------------------------------------

test("a token the server signed verifies with its secret and with no other (recorded tokens from the server's own test fixture)", () => {
  const fixture = JSON.parse(read("apps/server/tests/Weir.Core.Tests/Fixtures/python-security.json")).csrf;
  assert.ok(csrfSignedWith(fixture.anonymous, fixture.secret));
  assert.ok(csrfSignedWith(fixture.session_bound, fixture.secret));
  assert.ok(!csrfSignedWith(fixture.anonymous, `${fixture.secret}x`));
  assert.ok(!csrfSignedWith(fixture.anonymous.replace(/.$/, "A"), fixture.secret));
  assert.ok(!csrfSignedWith(fixture.anonymous, ""));
  assert.ok(!csrfSignedWith("no-dots-at-all", fixture.secret));
});

// --- what an uninstall did to the data folder -----------------------------------------------------------------------------------------

test("a data folder is as it was when nothing is gone, new or changed, and the tray's log may only grow", () => {
  const before = [
    { path: "data\\weir.sqlite3", bytes: 10, sha256: "a" },
    { path: "tray-host.log", bytes: 5, sha256: "b" },
  ];
  assert.deepEqual(dataFolderProblems(before, before, { mayGrow: ["tray-host.log"] }), []);
  assert.deepEqual(dataFolderProblems(before, [before[0], { ...before[1], bytes: 9, sha256: "c" }], { mayGrow: ["tray-host.log"] }), []);
  assert.deepEqual(dataFolderProblems(before, [before[0]], { mayGrow: ["tray-host.log"] }), ["tray-host.log is gone"]);
  assert.deepEqual(dataFolderProblems(before, [before[0], { ...before[1], bytes: 4 }], { mayGrow: ["tray-host.log"] }), ["tray-host.log is shorter than it was"]);
  assert.deepEqual(dataFolderProblems(before, [{ ...before[0], sha256: "z" }, before[1]]), ["data\\weir.sqlite3 changed"]);
  assert.deepEqual(dataFolderProblems(before, [...before, { path: "extra", bytes: 1, sha256: "d" }]), ["extra is new"]);
});

// --- the scenarios, healthy -----------------------------------------------------------------------------------------------------------

test("on a clean machine the three scenarios pass in order, and the logs gained nothing", async () => {
  const results = await run();
  assert.deepEqual(results.map((result) => [result.id, result.status]), [
    ["fresh-install", "passed"],
    ["recover-locked-out-account", "passed"],
    ["uninstall-keeps-the-data", "passed"],
  ]);
  assert.equal(exitCode(results), 0);
  assert.match(results[0].detail, /session\.secret and credentials\.secret are 32\+ characters/);
  assert.match(results[1].detail, /found .*weir\.sqlite3.*reset the password and said 2 signed-in sessions were ended/);
  assert.match(results[2].detail, /Uninstalled twice/);
});

test("each scenario runs alone: it installs, starts and signs in to what it needs", async () => {
  for (const id of Object.keys(SCENARIOS)) {
    const result = await only(id);
    assert.equal(result.status, "passed", `${id}: ${result.detail}`);
  }
});

test("the recovery command's output is kept with the record, and not the password it was given", async () => {
  const stand = new FakeWindows();
  const [result] = await runScenarios({ ids: ["recover-locked-out-account"], registry: SCENARIOS, world: { ...stand.world(), watchLogs } });
  assert.equal(result.status, "passed", result.detail);
  assert.deepEqual([...stand.recordings.keys()], ["recover-help.txt", "recover-list.txt", "recover-recover.txt"]);
  assert.match(stand.recordings.get("recover-recover.txt"), /Password reset for 'scenario-admin'/);
  assert.doesNotMatch([...stand.recordings.values()].join("\n"), /Rc-/);
});

// --- the build under test stays the build under test --------------------------------------------------------------------------------

test("a newer release in the feed does not replace the build under test, because the install only announces updates", async () => {
  const stand = new FakeWindows({ feedHasNewerRelease: true });
  const results = await runScenarios({ ids: Object.keys(SCENARIOS), registry: SCENARIOS, world: { ...stand.world(), watchLogs } });
  assert.deepEqual(results.map((result) => result.status), ["passed", "passed", "passed"], results.map((result) => result.detail).join(" | "));
  assert.ok(stand.calls.indexOf("keepUpdatesFromInstalling") < stand.calls.indexOf("startTray 9347"));
});

test("a Weir that installs the newest release when it next starts fails the scenario that restarts it", async () => {
  const stand = new FakeWindows({ feedHasNewerRelease: true });
  stand.machine.keepUpdatesFromInstalling = async () => undefined;
  const [result] = await runScenarios({ ids: ["recover-locked-out-account"], registry: SCENARIOS, world: { ...stand.world(), watchLogs } });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /reports 1\.0\.0-rc\.18, not the build under test \(3\.4\.0\)/);
});

// --- fresh-install, broken -------------------------------------------------------------------------------------------------------------

test("a fresh install whose Setup fails, whose server reports another version or whose tray misbehaves does not pass", async () => {
  assert.match(await failureOf("fresh-install", { setupExit: 1603 }), /Setup --silent exited 1603, not 0/);
  assert.match(await failureOf("fresh-install", { reportedVersion: "3.3.9" }), /reports 3\.3\.9, not the build under test \(3\.4\.0\)/);
  assert.match(await failureOf("fresh-install", { trayShowsWindow: true }), /the tray opened a window/);
  assert.match(await failureOf("fresh-install", { listensOn: ["127.0.0.1", "0.0.0.0"] }), /listens on 127\.0\.0\.1, 0\.0\.0\.0, not on this PC only/);
});

test("secrets that are short, shared, readable by other accounts or inherited from the folder do not pass", async () => {
  assert.match(await failureOf("fresh-install", { secretLength: 20 }), /session\.secret is 20 characters, shorter than 32/);
  assert.match(await failureOf("fresh-install", { sameSecrets: true }), /the session secret and the credentials secret are the same/);
  assert.match(await failureOf("fresh-install", { usersReadSecrets: true }), /session\.secret can be read by BUILTIN\\Users/);
  assert.match(await failureOf("fresh-install", { secretsInherit: true }), /session\.secret takes its access list from its folder/);
});

test("a server that was not started with the session secret, or that complains about its secrets, does not pass", async () => {
  assert.match(await failureOf("fresh-install", { serverIgnoresSecret: true }), /does not verify with the secret in session\.secret/);
  assert.match(await failureOf("fresh-install", { serverLogLine: "WEIR_CREDENTIALS_SECRET is not set - stored provider credentials" }), /says "WEIR_CREDENTIALS_SECRET is not set"/);
});

test("a warning the logs gained while the scenario ran fails it, although it did everything else", async () => {
  assert.match(await failureOf("fresh-install", { warnsWhenStarted: "Could not open the update feed" }), /gained 1 unexpected warning or error \(Could not open the update feed\)/);
  assert.match(await failureOf("uninstall-keeps-the-data", { warnsWhenStarted: "Could not open the update feed" }), /gained .*Could not open the update feed/);
});

test("the refused sign-in of the recovery scenario is the one warning it may cause, and a second warning fails it", async () => {
  const healthy = await only("recover-locked-out-account");
  assert.equal(healthy.status, "passed", healthy.detail);
  assert.match(healthy.detail, /The logs gained no unexpected warning or error\./);
  const stand = new FakeWindows({ warnsWhenStarted: "Could not open the update feed" });
  const [result] = await runScenarios({ ids: ["recover-locked-out-account"], registry: SCENARIOS, world: { ...stand.world(), watchLogs } });
  assert.match(result.detail, /gained 2 unexpected warning or error \(Could not open the update feed \| Could not open the update feed\)/);
});

// --- recover-locked-out-account, broken ----------------------------------------------------------------------------------------------

test("a recovery command that does not find the data folder the installed server uses does not pass", async () => {
  assert.match(await failureOf("recover-locked-out-account", { recoverLooksElsewhere: true }), /did not find the data folder the installed server uses/);
});

test("a recovery command that says it reset the password and did not does not pass", async () => {
  assert.match(await failureOf("recover-locked-out-account", { recoverChangesNothing: true }), /the forgotten password was answered 200, not refused/);
});

test("a recovery that leaves an earlier session signed in does not pass", async () => {
  assert.match(await failureOf("recover-locked-out-account", { recoverKeepsSessions: true }), /a session from before the reset is still accepted/);
});

test("Weir is running again after a recovery that failed, so the next scenario finds it", async () => {
  const stand = new FakeWindows({ recoverLooksElsewhere: true });
  const results = await runScenarios({ ids: ["recover-locked-out-account", "fresh-install"], registry: SCENARIOS, world: { ...stand.world(), watchLogs } });
  assert.deepEqual(results.map((result) => result.status), ["failed", "passed"]);
  assert.equal(stand.calls.at(-1), "startTray 9347");
});

// --- uninstall-keeps-the-data, broken -------------------------------------------------------------------------------------------------

test("an uninstaller that deletes the data folder does not pass, and says what it deleted", async () => {
  assert.match(await failureOf("uninstall-keeps-the-data", { uninstallDeletesData: true }), /changed the data folder: .*weir\.sqlite3 is gone.*session\.secret is gone/);
});

test("an uninstaller that edits the database does not pass", async () => {
  assert.match(await failureOf("uninstall-keeps-the-data", { uninstallEditsDatabase: true }), /changed the data folder: .*weir\.sqlite3 changed/);
});

test("an uninstaller that leaves a shortcut, the running mark or Weir running does not pass", async () => {
  assert.match(await failureOf("uninstall-keeps-the-data", { uninstallLeavesShortcut: true }), /left these behind: .*Weir\.lnk/);
  assert.match(await failureOf("uninstall-keeps-the-data", { uninstallLeavesMark: true }), /left these behind: the running mark in the registry/);
  assert.match(await failureOf("uninstall-keeps-the-data", { uninstallLeavesRunning: true }), /left Weir running: Weir\.exe/);
});

test("a reinstall that replaces a secret, or that does not find the account, does not pass", async () => {
  assert.match(await failureOf("uninstall-keeps-the-data", { reinstallReplacesSecrets: true }), /the reinstall replaced a secret/);
  assert.match(await failureOf("uninstall-keeps-the-data", { reinstallForgetsAccount: true }), /the account is gone: Weir offers to create the first one again/);
});

test("every scenario of the shard runs, whatever the one before it found", async () => {
  const results = await run({ secretLength: 20 });
  assert.deepEqual(results.map((result) => result.status), ["failed", "passed", "passed"]);
  assert.equal(exitCode(results), 1);
});
