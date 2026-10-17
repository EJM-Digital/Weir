// tray-second-launch and tray-start: how the real tray is started and started again. A second Weir.exe in the same session leaves one tray and
// one server and says so from the running tray's icon; a start with nobody at the desktop that cannot start Weir exits with code 1 and says why;
// a taken saved port is answered with a question, and WEIR_PORT is honoured; and no start, whoever made it, opens the browser. Written for CI:
// the VM suite started the tray once.
import assert from "node:assert/strict";

import { lineWith, linesWith } from "./tray-log.mjs";
import { balloonLine, balloons } from "./tray-words.mjs";
import {
  browserOn,
  closeBrowsers,
  expectNoBrowser,
  holdPort,
  installedAndStopped,
  PORT,
  restoreWeir,
  serverOn,
  startTray,
} from "./tray-world.mjs";
import { ensureSignedIn, wait } from "./windows-install.mjs";

const OTHER_PORT = 9399;
const TAKEN_PORT = 9356;
const NEXT_PORT = TAKEN_PORT + 1;
const ENV_PORT = 9358;
const SETUP_CEILING_MS = 600_000;

const openedBrowser = (lines) => linesWith(lines, "Opening Weir in browser");

async function processCounts(world) {
  const running = await world.machine.processes();
  return { trays: running.filter((process) => process.name === "Weir.exe").length, servers: running.filter((process) => process.name === "WeirServer.exe").length };
}

/** tray-second-launch: one tray and one server stay; the running tray tells the person; a silent second launch says nothing. */
export async function traySecondLaunch(world) {
  const { machine } = world;
  await installedAndStopped(world);
  const tray = await startTray(world, ["--port", String(PORT)]);
  try {
    await tray.ready();
    await ensureSignedIn(world);
    await tray.line(balloonLine("info", "Weir", balloons.running(PORT)), { what: "the tray to tell the person Weir is running" });
    const told = async () => linesWith(await tray.log(), "Weir was started again; telling the person it is already running.");

    const second = await machine.runTray(["--port", String(OTHER_PORT)]);
    assert.equal(second.exitCode, 0, `the second Weir.exe ended with exit code ${second.exitCode}, not 0`);
    const log = await tray.log();
    assert.ok(lineWith(log, "Tray host launch skipped: an existing Weir tray instance is already running."), "the second launch did not say a tray is already running");
    assert.ok(lineWith(log, `Ignoring --port ${OTHER_PORT}: Weir is already running.`), "the second launch did not say it ignored the port it was given");
    await tray.line("Weir was started again; telling the person it is already running.", { what: "the running tray to hear of the second launch" });
    await tray.line(balloonLine("info", "Weir", balloons.alreadyRunning(PORT)), { what: "the running tray's balloon that Weir is already running" });
    assert.deepEqual(await processCounts(world), { trays: 1, servers: 1 }, "a second tray or server is running");
    assert.equal(await serverOn(OTHER_PORT).ready(), false, `Weir answers on port ${OTHER_PORT}, which the second launch gave and should have been ignored`);
    await expectNoBrowser(world, "although the second launch only announces Weir", PORT);

    // A click on that balloon opens Weir, once.
    await world.desktop.clickBalloon(tray.pid);
    await tray.line("(source=already-running-balloon)", { what: "a click on the balloon to open Weir" });
    await browserOn(world, PORT);
    await closeBrowsers(world, PORT);

    // A silent second launch is told to say nothing; the next ordinary one proves the running tray heard only that.
    const silent = await machine.runTray(["--silent"]);
    assert.equal(silent.exitCode, 0, `the silent second launch ended with exit code ${silent.exitCode}`);
    const third = await machine.runTray([]);
    assert.equal(third.exitCode, 0);
    await wait(world, async () => ((await told()).length >= 2 ? true : undefined), "the running tray to hear of the next ordinary launch", 60_000, 500);
    assert.equal((await told()).length, 2, "a silent second launch was announced to the person");
    assert.deepEqual(await processCounts(world), { trays: 1, servers: 1 }, "a second tray or server is running after the silent launch");
  } finally {
    await tray.end();
    await restoreWeir(world);
  }
  return `A second Weir.exe (given port ${OTHER_PORT}) exited 0 and left one tray and one server; the first said it is already running and ignored the port; the running tray's balloon ("${balloons.alreadyRunning(PORT)}") opened Weir when clicked; a silent second launch was not announced; no browser was started by a launch.`;
}

/** The tray with nobody at the desktop that cannot start Weir exits with code 1 and says why. */
async function silentStartsThatCannot(world) {
  const { machine } = world;
  const holder = await holdPort(world, TAKEN_PORT);
  const facts = [];
  try {
    // The saved port is taken by another program: it is not moved on its own, and nobody is there to ask.
    await machine.removeFromDataFolder("current-port.txt");
    await machine.writeToDataFolder("port.txt", String(TAKEN_PORT));
    const saved = await startTray(world, ["--silent"]);
    assert.equal(await saved.exited("the silent start with a taken saved port to end"), 1, "the silent start with the saved port taken did not exit with code 1");
    const why = lineWith(await saved.log(), `Saved port ${TAKEN_PORT} is in use by another program.`);
    assert.ok(why, "the log does not say the saved port is taken");
    assert.equal(await world.desktop.window(saved.pid), null, "a window was shown to nobody");
    assert.deepEqual(await processCounts(world), { trays: 0, servers: 0 }, "something is still running after the start gave up");
    facts.push(`a silent start with the saved port taken exited 1 ("${why.slice(0, 60)}...") and showed no window`);

    // The port is given and taken: the server cannot start, again and again, and the tray ends the way it says.
    await machine.removeFromDataFolder("port.txt");
    const given = await startTray(world, ["--silent", "--port", String(TAKEN_PORT)]);
    assert.equal(await given.exited("the silent start with a taken port to end", 300_000), 1, "the silent start whose server cannot start did not exit with code 1");
    const log = await given.log();
    assert.ok(lineWith(log, /^Nobody is at the desktop to tell, so Weir exits with code 1: /), "the log does not say why the tray ended");
    assert.ok(lineWith(log, /Giving up \(5 of 5 failures/), "the tray ended without trying the start limit");
    facts.push("a silent start whose server could not take its port tried five times and then exited 1, saying nobody is at the desktop to tell");
  } finally {
    await holder.release();
    await machine.stopWeir();
  }
  return facts;
}

/** The saved port is taken and a person is there: the tray asks, and Weir comes up on the port they choose; WEIR_PORT is honoured. */
async function takenPortAndTheDesktop(world) {
  const { machine, desktop } = world;
  const holder = await holdPort(world, TAKEN_PORT);
  const facts = [];
  try {
    await machine.removeFromDataFolder("current-port.txt");
    await machine.writeToDataFolder("port.txt", String(TAKEN_PORT));
    const tray = await startTray(world, []);
    const question = await wait(world, async () => (await desktop.window(tray.pid)) ?? undefined, "the tray to ask about the taken port", 60_000, 500);
    assert.ok(question.texts.some((text) => text.includes(String(TAKEN_PORT))), `the question does not name the taken port: ${question.texts.join(" | ")}`);
    assert.equal(question.edits[0], String(NEXT_PORT), `the question does not offer the next free port (${question.edits[0]})`);
    assert.equal(await serverOn(TAKEN_PORT).ready(), false, "a server is running before the question was answered");
    await desktop.pressInWindow(tray.pid, "Start Weir");
    await tray.line(`Saved port ${TAKEN_PORT} was in use; the user chose ${NEXT_PORT}.`, { what: "the tray to take the chosen port" });
    await wait(world, async () => ((await serverOn(NEXT_PORT).ready()) ? true : undefined), `Weir to answer on port ${NEXT_PORT}`, 120_000, 500);
    assert.equal((await machine.readText(`${machine.paths.runtimeHome}\\port.txt`)).trim(), String(NEXT_PORT), "the chosen port was not saved");
    facts.push(`with the saved port ${TAKEN_PORT} taken and a person at the desktop, the tray asked, offered ${NEXT_PORT}, and Weir came up there and saved it`);
    await tray.end();

    const named = await startTray(world, ["--silent"], { WEIR_PORT: String(ENV_PORT) });
    await named.line(`Using port ${ENV_PORT} from WEIR_PORT.`, { what: "the tray to use WEIR_PORT" });
    await wait(world, async () => ((await serverOn(ENV_PORT).ready()) ? true : undefined), `Weir to answer on port ${ENV_PORT}`, 120_000, 500);
    facts.push(`WEIR_PORT=${ENV_PORT} was honoured`);
  } finally {
    await holder.release();
    await machine.stopWeir();
  }
  return facts;
}

/** Whatever started Weir, nothing opens the browser: a start, a sign-in start, a start that follows Setup. */
async function noStartOpensTheBrowser(world) {
  const { machine } = world;
  const facts = [];
  const checked = async (tray, what) => {
    await tray.ready();
    const log = await tray.log();
    assert.deepEqual(openedBrowser(log), [], `${what} opened the browser`);
    await expectNoBrowser(world, `by ${what}`, PORT);
  };

  const quiet = await startTray(world, ["--port", String(PORT), "--silent"]);
  await checked(quiet, "a silent start");
  await machine.stopWeir();

  const signIn = await startTray(world, ["--no-browser"]);
  await checked(signIn, "a start at sign-in (--no-browser)");
  await ensureSignedIn(world);
  facts.push("a silent start and a sign-in start opened no browser");

  // Setup over a running Weir: it stops Weir, and the tray starts it again by itself once Setup has gone.
  const mark = (await machine.readText(machine.paths.trayLog)).length;
  const setup = await machine.install(world.setupPath);
  assert.equal(setup.exitCode, 0, `Setup --silent over the running Weir exited ${setup.exitCode}`);
  await machine.keepUpdatesFromInstalling();
  await wait(world, async () => ((await serverOn(PORT).ready()) ? true : undefined), "Weir to be started again after Setup", SETUP_CEILING_MS, 1000);
  const afterSetup = (await machine.readText(machine.paths.trayLog)).slice(mark);
  assert.ok(afterSetup.includes("It starts again once Setup (pid"), "the tray did not arrange to start again after Setup");
  assert.equal(openedBrowser(afterSetup.split(/\r?\n/)).length, 0, "the start after Setup opened the browser");
  await expectNoBrowser(world, "by the start after Setup", PORT);
  assert.deepEqual(await processCounts(world), { trays: 1, servers: 1 }, "more than one tray or server is running after Setup");
  facts.push("Setup --silent over a running Weir stopped it and the tray started it again by itself, with one tray and one server and no browser");
  return facts;
}

/** tray-start: the starts nobody sees, and the starts a person answers. */
export async function trayStart(world) {
  await installedAndStopped(world);
  const facts = [];
  try {
    facts.push(...(await noStartOpensTheBrowser(world)));
    await installedAndStopped(world);
    facts.push(...(await silentStartsThatCannot(world)));
    await installedAndStopped(world);
    facts.push(...(await takenPortAndTheDesktop(world)));
  } finally {
    await world.machine.removeFromDataFolder("port.txt", "current-port.txt");
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}
