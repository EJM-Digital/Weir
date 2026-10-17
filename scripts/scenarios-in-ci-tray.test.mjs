// node --test scripts/scenarios-in-ci-tray.test.mjs
// What the windows-tray shard decides without a Windows desktop: how tray-host.log is read, the words the scenarios expect against the C# that
// writes them, how the menu is compared with the standard, what the scenarios do with another program's port and with a log line, and that the
// UI Automation script does not move the mouse anywhere but a hosted runner. The real tray, taskbar and server are the windows-tray job of
// ci-packaging.yml.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { appendFile, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import net from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { loadHomes, scenariosOfShard } from "./scenarios-ci/homes.mjs";
import { LogWatch } from "./scenarios-ci/log-watch.mjs";
import { menuProblems, runningMenu } from "./scenarios-ci/tray-actions.mjs";
import { backoffProblem, failedStarts, lineWith, linesAfter, linesWith, serverProcessIds } from "./scenarios-ci/tray-log.mjs";
import { menuVersion } from "./scenarios-ci/tray-menu.mjs";
import { atLogLine, holdPort, RunningTray } from "./scenarios-ci/tray-world.mjs";
import { address, balloonLine, balloons, menu, status } from "./scenarios-ci/tray-words.mjs";
import { SCENARIOS } from "./scenarios-ci/windows-tray.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(repository, path), "utf8");
const homes = loadHomes();

// --- where the shard's scenarios are --------------------------------------------------------------------------------------------------

test("every ported scenario of the windows-tray shard has code, and every scenario with code is ported", () => {
  const ported = homes.scenarios.filter((scenario) => scenario.shard === "windows-tray" && scenario.state !== "to-port").map((scenario) => scenario.id);
  assert.deepEqual(Object.keys(SCENARIOS).sort(), ported.sort());
});

test("the shard runs update-requests first, and the scenarios written for CI after it", () => {
  assert.deepEqual(
    scenariosOfShard(homes, "windows-tray").map((scenario) => scenario.id),
    ["update-requests", "tray-menu", "tray-supervision", "tray-give-up", "tray-change-port", "tray-lan-access", "tray-second-launch", "tray-start"],
  );
});

// --- tray-host.log ----------------------------------------------------------------------------------------------------------------------

const LOG = [
  "[2026-10-17 23:01:28] Port: Using port 9347 from --port. (interactive desktop: yes)",
  "[2026-10-17 23:01:29] Starting bundled server host: C:\\Weir\\server\\WeirServer.exe (attempt 1)",
  "[2026-10-17 23:01:29] Bundled server host pid=5616",
  "[2026-10-17 23:01:30] Weir is healthy on http://127.0.0.1:9347/",
  "[2026-10-17 23:02:10] Start attempt 2 failed: exited with code 1 after running 40.2 s. Trying again in 1.5 s (1 of 5 failures allowed within 15 min).",
  "[2026-10-17 23:02:12] Bundled server host pid=6001",
  "[2026-10-17 23:02:20] Start attempt 3 failed: exited with code 75 after running 0.9 s. Trying again in 3.2 s (2 of 5 failures allowed within 15 min).",
  "[2026-10-17 23:02:40] Start attempt 6 failed: exited with code 75 after running 1 s. Giving up (5 of 5 failures allowed within 15 min); Weir is not running. Restart Weir tries again.",
  "",
].join("\n");

test("the log is read after a mark, a line at a time, without its time stamp", () => {
  const lines = linesAfter(LOG);
  assert.equal(lines.length, 8);
  assert.equal(lines[0], "Port: Using port 9347 from --port. (interactive desktop: yes)");
  const mark = LOG.indexOf("[2026-10-17 23:02:10]");
  assert.equal(linesAfter(LOG, mark).length, 4);
  assert.deepEqual(linesAfter("", 0), []);
});

test("a line is found by its text or by a pattern, and all of them can be listed", () => {
  const lines = linesAfter(LOG);
  assert.equal(lineWith(lines, "healthy on"), "Weir is healthy on http://127.0.0.1:9347/");
  assert.equal(lineWith(lines, /^Weir is (healthy|ready)/), "Weir is healthy on http://127.0.0.1:9347/");
  assert.equal(lineWith(lines, "not in the log"), undefined);
  assert.equal(linesWith(lines, "Start attempt").length, 3);
});

test("the server's process ids are those the tray started, in order", () => {
  assert.deepEqual(serverProcessIds(linesAfter(LOG)), [5616, 6001]);
});

test("a failed start is read with its attempt, its reason, its wait and how many failures it counts", () => {
  assert.deepEqual(failedStarts(linesAfter(LOG)), [
    { attempt: 2, reason: "exited with code 1 after running 40.2 s", waitSeconds: 1.5, failures: 1, gaveUp: false },
    { attempt: 3, reason: "exited with code 75 after running 0.9 s", waitSeconds: 3.2, failures: 2, gaveUp: false },
    { attempt: 6, reason: "exited with code 75 after running 1 s", waitSeconds: null, failures: 5, gaveUp: true },
  ]);
});

test("a wait lies between half the policy's ceiling and the ceiling, and the ceiling doubles from 2 s up to 60 s", () => {
  assert.equal(backoffProblem(1, 1), null);
  assert.equal(backoffProblem(1, 2), null);
  assert.equal(backoffProblem(2, 2), null);
  assert.equal(backoffProblem(2, 4), null);
  assert.equal(backoffProblem(3, 4.1), null, "the log rounds to a tenth of a second");
  assert.match(backoffProblem(3, 2), /outside 4-8 s/);
  assert.match(backoffProblem(1, 2.5), /outside 1-2 s/);
  assert.equal(backoffProblem(9, 30), null);
  assert.match(backoffProblem(9, 61), /outside 30-60 s/);
});

// --- the words ------------------------------------------------------------------------------------------------------------------------

/** Every fixed piece of a text with parameters in it, so the C# that writes it can be searched for each. */
function fixedPieces(text, ...parameters) {
  return parameters
    .reduce((pieces, value) => pieces.flatMap((piece) => piece.split(String(value))), [text])
    .map((piece) => piece.trim())
    .filter((piece) => piece.length > 3);
}

const csharp = {
  balloons: read("apps/tray/Weir.Tray/TrayBalloons.cs"),
  lan: read("apps/tray/Weir.Tray/LanAccess/LanAccessNotice.cs"),
  menu: read("apps/tray/Weir.Tray/TrayMenu.cs") + read("apps/tray/Weir.Tray/UpdateMenuState.cs") + read("apps/tray/Weir.Tray/LanAccess/LanAccessMenuState.cs"),
  state: read("apps/tray/Weir.Tray/TrayState.cs"),
  change: read("apps/tray/Weir.Tray/PortChange.cs"),
  app: read("apps/tray/Weir.Tray/TrayApp.cs"),
};

test("the balloons the scenarios expect are the ones the tray's source says, word for word", () => {
  const checks = [
    [balloons.running(9347), csharp.balloons, address(9347)],
    [balloons.alreadyRunning(9347), csharp.balloons, address(9347)],
    [balloons.portMoved(9352), csharp.balloons, address(9352)],
    [balloons.stoppedPortInUse(9347, 5), csharp.balloons, 9347, "5 tries"],
    [balloons.stoppedAfterTries(5), csharp.balloons, "5 tries"],
    [balloons.couldNotStart, csharp.balloons],
    [balloons.notRunning, csharp.balloons],
    [balloons.portMoveFailed(9353, 9352), csharp.change, 9353, 9352],
    [balloons.lanOpened, csharp.lan],
    [balloons.lanClosed, csharp.lan],
    [balloons.updateAvailable("3.4.0"), csharp.app, "3.4.0"],
  ];
  for (const [text, source, ...parameters] of checks) {
    for (const piece of fixedPieces(text, ...parameters)) assert.ok(source.includes(piece), `"${piece}" (of "${text}") is not in the C# source`);
  }
});

test("the menu the scenarios expect has the words of the tray's source", () => {
  for (const word of [menu.openWeir, menu.pause, menu.resume, menu.restart, menu.copyAddress, menu.allowOtherDevices, menu.onlyThisPc, menu.openDataFolder, menu.openLogsFolder, menu.startWithWindows, menu.checkForUpdates, menu.reportProblem, menu.quit]) {
    assert.ok(csharp.menu.includes(`"${word}"`), `"${word}" is not in the C# source`);
  }
  assert.ok(csharp.menu.includes("Change port ({port})..."));
  assert.ok(csharp.state.includes('"Paused"') && csharp.state.includes("Running at {Address}") && csharp.state.includes("Stopped - {why.Headline} - choose Restart Weir"));
  assert.equal(status.running(9347), "Weir - Running at http://localhost:9347");
  assert.equal(address(9347), "http://localhost:9347");
});

test("a balloon is kept in the log the way the tray writes it", () => {
  assert.equal(balloonLine("info", "Weir", "Hello."), "Balloon (info) Weir: Hello.");
  assert.ok(csharp.balloons.includes("$\"Balloon ({icon.ToString().ToLowerInvariant()}) {title}: {text}\""));
});

// --- the menu ---------------------------------------------------------------------------------------------------------------------------

test("the menu is the standard's, item for item, and a build's metadata is not in its version line", () => {
  const expected = runningMenu({ port: 9347, version: "3.4.0" });
  assert.equal(expected.length, 15);
  assert.equal(expected[12].name, "Weir v3.4.0");
  assert.equal(menuVersion("1.0.0-rc.4+abc1234"), "1.0.0-rc.4");
  assert.equal(menuVersion("0.0.1-dev"), "0.0.1-dev");
  assert.deepEqual(menuProblems(expected, structuredClone(expected)), []);
});

test("a menu that differs says how: a missing item, another word, an item enabled that should not be", () => {
  const expected = runningMenu({ port: 9347, version: "3.4.0" });
  const shorter = structuredClone(expected).slice(0, 14);
  assert.match(menuProblems(expected, shorter).join("\n"), /has 14 items, not 15/);
  const reworded = structuredClone(expected);
  reworded[3].name = "Restart";
  assert.match(menuProblems(expected, reworded).join("\n"), /item 4 says "Restart", not "Restart Weir"/);
  const wrongState = structuredClone(expected);
  wrongState[6].enabled = true;
  assert.match(menuProblems(expected, wrongState).join("\n"), /"Only allow this PC" is enabled, not disabled/);
});

// --- another program's port, and the log -------------------------------------------------------------------------------------------------

/** A stand-in for the world a RunningTray reads: a data folder with a tray-host.log in it. */
async function logWorld() {
  const runtimeHome = await mkdtemp(join(tmpdir(), "weir-tray-test-"));
  const trayLog = join(runtimeHome, "tray-host.log");
  await writeFile(trayLog, "");
  const clock = { now: () => Date.now(), sleep: (ms) => new Promise((done) => setTimeout(done, ms)) };
  const machine = { paths: { runtimeHome, trayLog }, readText: (path) => readFile(path, "utf8"), isRunning: async () => true };
  return { world: { machine, clock }, trayLog, cleanup: () => rm(runtimeHome, { recursive: true, force: true }) };
}

const stamp = (message) => `[2026-10-17 23:00:00] ${message}\n`;
const nextTick = () => new Promise((done) => setImmediate(done));

/** Whether something else can listen on the port: "listened", or the error's code. */
const tryListening = (port) =>
  new Promise((resolve) => {
    const other = net.createServer();
    other.once("error", (error) => resolve(error.code));
    other.listen(port, "127.0.0.1", () => other.close(() => resolve("listened")));
  });

test("another program's port is held until it is let go", async () => {
  const { world, cleanup } = await logWorld();
  try {
    const probe = net.createServer();
    await new Promise((done) => probe.listen(0, "127.0.0.1", done));
    const { port } = probe.address();
    await new Promise((done) => probe.close(done));
    const holder = await holdPort(world, port);
    assert.equal(await tryListening(port), "EADDRINUSE", "the port is held");
    await holder.release();
    assert.equal(await tryListening(port), "listened", "the port is free after release");
  } finally {
    await cleanup();
  }
});

test("an action waits for the tray to log a line and runs once, on the line and not before", async () => {
  const { world, trayLog, cleanup } = await logWorld();
  try {
    const tray = new RunningTray(world, { pid: 1, exit: Promise.resolve(0) }, 0);
    await appendFile(trayLog, stamp("Something else happened."));
    let ran = 0;
    const watch = atLogLine(world, tray, "Change port: restarting the server on port 9353", async () => ++ran);
    await appendFile(trayLog, stamp("Still not it."));
    await nextTick();
    assert.equal(ran, 0, "the action ran before the line was logged");
    await appendFile(trayLog, stamp("Change port: restarting the server on port 9353 (was 9352)."));
    assert.equal(await watch.done, 1);
    await appendFile(trayLog, stamp("Change port: restarting the server on port 9353 (was 9352)."));
    await nextTick();
    assert.equal(ran, 1, "the action ran again for a second line");
  } finally {
    await cleanup();
  }
});

test("an action skips the lines the tray logged before the scenario began to wait", async () => {
  const { world, trayLog, cleanup } = await logWorld();
  try {
    const tray = new RunningTray(world, { pid: 1, exit: Promise.resolve(0) }, 0);
    await appendFile(trayLog, stamp("Go back to port 9352."));
    const skipped = (await tray.log()).length;
    const watch = atLogLine(world, tray, "Go back to port 9352.", async () => "ran", skipped);
    await appendFile(trayLog, stamp("Go back to port 9352."));
    assert.equal(await watch.done, "ran");
  } finally {
    await cleanup();
  }
});

test("a tray handle reads the log from its mark, finds the server it started last and waits for a line", async () => {
  const { world, trayLog, cleanup } = await logWorld();
  try {
    await appendFile(trayLog, stamp("Bundled server host pid=10"));
    const tray = new RunningTray(world, { pid: 7, exit: Promise.resolve(0) }, 0);
    await tray.markNow();
    assert.deepEqual(await tray.log(), []);
    await assert.rejects(() => tray.serverPid(), /has not logged a server process id/);
    await appendFile(trayLog, stamp("Bundled server host pid=11"));
    await appendFile(trayLog, stamp("Bundled server host pid=12"));
    assert.equal(await tray.serverPid(), 12);
    assert.equal(await tray.lineCount(), 2);
    assert.equal(await tray.line("pid=11", { what: "the first server", ceilingMs: 1000 }), "Bundled server host pid=11");
    assert.equal((await tray.linesHolding("Bundled")).length, 2);
  } finally {
    await cleanup();
  }
});

// --- the log watch ----------------------------------------------------------------------------------------------------------------------

test("a row whose title matches a pattern is let through as many times as the scenario said, and no more", async () => {
  const rows = [];
  const weir = { get: async () => ({ items: [...rows], next_cursor: null }) };
  const row = (id, title) => ({ source: "server", id, at: `2026-10-17T23:00:0${id}Z`, title });
  const watch = await LogWatch.begin(weir);
  watch.expectMatching(/^Weir is starting again: this is start attempt \d+/, { times: 2, why: "the server is ended on purpose" });
  rows.push(row(1, "Weir is starting again: this is start attempt 2, because the earlier attempt failed."), row(2, "Weir is starting again: this is start attempt 3, because the earlier attempt failed."));
  await watch.finish();
  rows.push(row(3, "Weir is starting again: this is start attempt 4, because the earlier attempt failed."));
  await assert.rejects(() => watch.finish(), /1 unexpected warning or error \(Weir is starting again: this is start attempt 4/);
  rows.push(row(4, "Something else broke"));
  await assert.rejects(() => watch.finish(), /Something else broke/);
});

test("a pattern the scenario expects needs a reason", async () => {
  const watch = await LogWatch.begin({ get: async () => ({ items: [], next_cursor: null }) });
  assert.throws(() => watch.expectMatching(/x/, { times: 1 }), /needs a reason/);
});

// --- the UI Automation script ----------------------------------------------------------------------------------------------------------

test("the UI Automation script moves no mouse and sends no key on a machine that is not a hosted runner", { skip: process.platform !== "win32" }, () => {
  for (const command of ["close-menu", "click-balloon"]) {
    const attempt = spawnSync(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", join(repository, "scripts/scenarios-ci/tray-ui.ps1"), command, "-TrayProcessId", "1"],
      { encoding: "utf8", env: { ...process.env, GITHUB_ACTIONS: "", RUNNER_ENVIRONMENT: "" } },
    );
    assert.notEqual(attempt.status, 0, `${command} ran on a machine that is not a hosted runner`);
    assert.match(attempt.stderr, /runs only on a GitHub-hosted runner/);
  }
});
