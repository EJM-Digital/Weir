// tray-change-port and tray-lan-access: the two menu items that restart the real server somewhere else. Change port asks which port, and the
// move either works (the new port is saved and answers) or goes back (nothing is saved, the old port answers again); Allow other devices asks
// Windows for the firewall rule for real (the runner is an administrator) and restarts the server for the whole network. Written for CI: the
// VM suite never restarted the server from the menu.
import assert from "node:assert/strict";

import { choose, openMenu, statusLine } from "./tray-actions.mjs";
import { failedStarts, lineWith } from "./tray-log.mjs";
import { atLogLine, browserOn, expectRowsOfAServerEndedOnPurpose, closeBrowsers, expectNoBrowser, holdPort, installedAndStopped, PORT, restoreWeir, serverOn, startTray } from "./tray-world.mjs";
import { balloonLine, balloons, menu, status } from "./tray-words.mjs";
import { ensureSignedIn, installBuildUnderTest, wait } from "./windows-install.mjs";

const DEFAULT_PORT = 9347;
const MOVED_PORT = 9352;
const REFUSED_PORT = 9353;
const BOTH_PORT = 9354;
const BUSY_PORT = 9360;

const savedPort = (world) => world.machine.dataFile("port.txt");
const currentPort = (world) => world.machine.dataFile("current-port.txt");

/** Chooses Change port and types the port into the window it opens; the answer is left to the caller. */
async function askToMove(world, tray, from, to) {
  const { desktop } = world;
  await openMenu(world, tray);
  await wait(world, async () => ((await desktop.menu(tray.pid))?.items.find((item) => item.name === menu.changePort(from))?.enabled ? true : undefined), "Change port to be enabled", 30_000, 500);
  await desktop.clickItem(tray.pid, menu.changePort(from));
  const dialog = await wait(world, async () => (await desktop.window(tray.pid)) ?? undefined, "the window that asks for a port", 60_000, 500);
  // The default port is offered as an option; any other port is typed in.
  if (from === DEFAULT_PORT) assert.ok(dialog.options.some((option) => option.selected && option.name.includes(String(from))), `the window does not offer the default port, ${from}`);
  else assert.equal(dialog.edits[0], String(from), `the window offers the port Weir is on (${dialog.edits[0]})`);
  await desktop.setWindowText(tray.pid, String(to));
  return { dialog, accept: () => desktop.pressInWindow(tray.pid, "Restart on this port"), cancel: () => desktop.pressInWindow(tray.pid, "Cancel") };
}

async function expectStoppedCleanly(tray, pid, mark) {
  const lines = (await tray.log()).slice(mark);
  assert.ok(lines.some((line) => line.startsWith(`Bundled server host pid=${pid} stopped cleanly in`)), `server ${pid} was not stopped cleanly`);
  assert.ok(!lines.some((line) => line.includes("killing it")), "a server was killed");
}

/** tray-change-port: a move to a free port is saved and answers; one that cannot be made goes back and saves nothing; both failing ends Stopped. */
export async function trayChangePort(world) {
  const { machine, desktop } = world;
  expectRowsOfAServerEndedOnPurpose(world, "the server cannot take the port it was moved to, and the tray starts it again where it was; it is ended once on purpose");
  await installedAndStopped(world);
  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  const facts = [];
  try {
    let pid = await tray.ready();
    await ensureSignedIn(world);

    // The window checks what is typed: a port another program holds is refused in words, and Cancel changes nothing.
    const holdingBusy = await holdPort(world, BUSY_PORT);
    try {
      const busy = await askToMove(world, tray, PORT, BUSY_PORT);
      await busy.accept();
      const refusal = await wait(world, async () => (await desktop.window(tray.pid))?.texts.find((text) => text.includes("already using port")), "the window to refuse a port another program holds", 30_000, 500);
      assert.equal(refusal, `Another program on this computer is already using port ${BUSY_PORT}. Choose a different number.`);
      await busy.cancel();
      await tray.line("Change port: closed without a new port.", { what: "the tray to log that the window was closed" });
    } finally {
      await holdingBusy.release();
    }
    assert.equal(await serverOn(PORT).ready(), true, "Weir stopped answering although the window was cancelled");
    facts.push(`the window refused port ${BUSY_PORT}, held by another program, in words, and Cancel changed nothing`);

    // A move that works.
    let mark = await tray.lineCount();
    const move = await askToMove(world, tray, PORT, MOVED_PORT);
    await move.accept();
    await tray.line(`Change port: Weir is healthy on http://127.0.0.1:${MOVED_PORT}/ and port ${MOVED_PORT} is saved.`, { what: "the move to succeed", after: mark });
        await expectStoppedCleanly(tray, pid, mark);
    assert.equal(await savedPort(world), String(MOVED_PORT), "port.txt does not name the new port");
    assert.equal(await currentPort(world), String(MOVED_PORT), "current-port.txt does not name the new port");
    await wait(world, async () => ((await serverOn(MOVED_PORT).ready()) ? true : undefined), `Weir to answer on port ${MOVED_PORT}`, 60_000, 500);
    assert.equal(await serverOn(PORT).ready(), false, "Weir still answers on the old port");
    await tray.line(balloonLine("info", "Weir", balloons.portMoved(MOVED_PORT)), { what: "the balloon that tells the person the new address" });
    await expectNoBrowser(world, "by a port change, which only announces the new address", PORT, MOVED_PORT);
    await wait(world, async () => ((await statusLine(world, tray)) === status.running(MOVED_PORT) ? true : undefined), "the menu to show the new address", 60_000, 500);
    await openMenu(world, tray);
    assert.ok((await desktop.menu(tray.pid)).items.some((item) => item.name === menu.changePort(MOVED_PORT)), "the menu does not offer Change port at the new port");
    await desktop.closeMenu();
    await desktop.clickBalloon(tray.pid);
    await tray.line(`Opening Weir in browser on port ${MOVED_PORT} (source=port-change-balloon)`, { what: "a click on the balloon to open Weir at the new port" });
    await browserOn(world, MOVED_PORT);
    await closeBrowsers(world, PORT, MOVED_PORT);
    pid = await tray.serverPid();
    facts.push(`Change port to ${MOVED_PORT} stopped the server cleanly, saved port.txt and current-port.txt, answered on the new port only, showed it in the menu, and opened no browser until the balloon was clicked`);

    // A move the server cannot make: another program takes the port in the moment between the tray's saying it will use it and the server's trying.
    mark = await tray.lineCount();
    const refused = await askToMove(world, tray, MOVED_PORT, REFUSED_PORT);
    const taking = atLogLine(world, tray, `Change port: restarting the server on port ${REFUSED_PORT} (was ${MOVED_PORT}).`, () => holdPort(world, REFUSED_PORT), mark);
    await refused.accept();
    const holder = await taking.done;
    try {
      await tray.line(`Change port: going back to port ${MOVED_PORT}.`, { what: "the tray to go back", after: mark });
      await wait(world, async () => ((await serverOn(MOVED_PORT).ready()) ? true : undefined), `Weir to answer on port ${MOVED_PORT} again`, 120_000, 500);
      await tray.line(balloonLine("warning", "Weir", balloons.portMoveFailed(REFUSED_PORT, MOVED_PORT)), { what: "the balloon that tells the person Weir stayed where it was", after: mark });
    } finally {
      await holder.release();
    }
    assert.equal(await savedPort(world), String(MOVED_PORT), "a move that failed saved its port");
    assert.equal(await currentPort(world), String(MOVED_PORT), "a move that failed changed current-port.txt");
    assert.equal(await serverOn(REFUSED_PORT).ready(), false, "Weir answers on a port it could not take");
    facts.push(`a move to port ${REFUSED_PORT}, taken by another program as the server tried it, went back to port ${MOVED_PORT}, saved nothing, said so in a balloon and ended Running`);

    // A move that fails with the old port taken as well: Weir is stopped, and says so, and Restart Weir is the way back.
    mark = await tray.lineCount();
    const oldPid = await tray.serverPid();
    const both = await askToMove(world, tray, MOVED_PORT, BOTH_PORT);
    const takingNew = atLogLine(world, tray, `Change port: restarting the server on port ${BOTH_PORT} (was ${MOVED_PORT}).`, () => holdPort(world, BOTH_PORT), mark);
    const takingOld = atLogLine(world, tray, `Bundled server host pid=${oldPid} stopped cleanly in`, () => holdPort(world, MOVED_PORT), mark);
    await both.accept();
    const holders = [await takingNew.done, await takingOld.done];
    try {
      await tray.line(`Change port: going back to port ${MOVED_PORT}.`, { what: "the tray to try the old port", after: mark });
      await wait(world, async () => ((await statusLine(world, tray)) === "Weir - Stopped - choose Restart Weir" ? true : undefined), "the menu to say Weir is stopped", 120_000, 1000);
      const said = (await tray.log()).slice(mark).filter((line) => line.startsWith("Balloon "));
      assert.equal(said.length, 1, `the person was told ${said.length} things: ${said.join(" | ")}`);
      assert.ok(said[0].includes("has stopped"), `the balloon does not say Weir stopped: ${said[0]}`);
      assert.equal(await savedPort(world), String(MOVED_PORT), "port.txt was changed");
    } finally {
      for (const held of holders) await held.release();
    }
    const restartMark = await tray.lineCount();
    await choose(world, tray, menu.restart);
    await tray.line("Restart Weir chosen.", { what: "the tray to restart", after: restartMark });
    await wait(world, async () => ((await serverOn(MOVED_PORT).ready()) ? true : undefined), `Weir to answer on port ${MOVED_PORT} after Restart Weir`, 120_000, 500);
    // The watchdog is watching again after the failed move and the restart: a server that is ended is started again.
    const watched = await tray.serverPid();
    const afterRestart = await tray.lineCount();
    await machine.killProcess(watched);
    await wait(world, async () => failedStarts((await tray.log()).slice(afterRestart)).at(0), "the tray to notice the server ended after the failed move", 60_000, 500);
    await tray.readyAfter(watched, MOVED_PORT);
    facts.push(`with the old port taken too, Weir ended Stopped with one balloon saying so, and Restart Weir brought it back on port ${MOVED_PORT}, where the watchdog started it again when it was ended`);
  } finally {
    await tray.end();
    await machine.removeFromDataFolder("port.txt", "current-port.txt");
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}

/** tray-lan-access: Allow other devices asks Windows for the rule and restarts the server for the network; Only allow this PC takes it back; both stop the server cleanly. */
export async function trayLanAccess(world) {
  const { machine } = world;
  await installedAndStopped(world);
  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  const facts = [];
  try {
    let pid = await tray.ready();
    await ensureSignedIn(world);
    const loopback = (addresses) => addresses.every((address) => ["127.0.0.1", "::1"].includes(address));
    // Every interface is "0.0.0.0", or "::" when it listens on both families.
    const beyondThisPc = (addresses) => addresses.some((address) => ["0.0.0.0", "::"].includes(address));
    assert.ok(loopback(await machine.listeningAddresses(PORT)), "Weir listens beyond this PC before anything was allowed");

    let mark = await tray.lineCount();
    await choose(world, tray, menu.allowOtherDevices);
    await tray.line(balloonLine("info", "Weir", balloons.lanOpened), { what: "the tray to tell the person other devices can reach Weir", after: mark, ceilingMs: 180_000 });
    let next = await tray.readyAfter(pid);
    const log = (await tray.log()).slice(mark);
    assert.ok(lineWith(log, "LAN access: restarting the server so that other devices on the network can connect (it was: only this PC can connect)."), "the tray does not say why it restarted the server");
    await expectStoppedCleanly(tray, pid, mark);
    const open = await machine.listeningAddresses(PORT);
    assert.ok(beyondThisPc(open), `Weir listens on ${open.join(", ")}, not on every interface`);
    assert.equal(await machine.firewallRuleExists(), true, "Windows Firewall has no rule for Weir after the elevated step");
    await choose(world, tray, menu.copyAddress);
    const machineName = process.env.COMPUTERNAME;
    await wait(world, async () => ((await world.desktop.clipboardText()).trim().toLowerCase() === `http://${machineName}:${PORT}`.toLowerCase() ? true : undefined), "Copy address to put this PC's name on the clipboard", 30_000, 500);
    facts.push(`Copy address then put http://${machineName}:${PORT} on the clipboard, and Allow other devices made the Windows Firewall rule through the elevated step, stopped server ${pid} cleanly, and the new server listens on ${open.join(", ")}`);
    pid = next;

    // The choice is final: a new tray listens for the network without asking again.
    await tray.end();
    const again = await startTray(world, ["--port", String(PORT), "--no-browser"]);
    try {
      pid = await again.ready();
      assert.ok(beyondThisPc(await machine.listeningAddresses(PORT)), "the saved choice for other devices was not kept across a start");
      mark = await again.lineCount();
      await choose(world, again, menu.onlyThisPc);
      await again.line(balloonLine("info", "Weir", balloons.lanClosed), { what: "the tray to tell the person only this PC can reach Weir", after: mark });
      next = await again.readyAfter(pid);
      await expectStoppedCleanly(again, pid, mark);
      assert.ok(loopback(await machine.listeningAddresses(PORT)), "Weir still listens beyond this PC after Only allow this PC");
      facts.push("a new tray kept the choice for other devices, and Only allow this PC restarted the server for this PC alone, stopping it cleanly");
      await expectNoBrowser(world, "by a change of who may connect", PORT);
    } finally {
      await again.end();
    }
    // The commands the first-run question and the uninstall run: the rule is made and taken away, and the uninstall takes it away itself.
    const removed = await machine.runTray(["--remove-firewall"]);
    assert.equal(removed.exitCode, 0, `Weir.exe --remove-firewall exited ${removed.exitCode}: ${removed.stderr}`);
    assert.equal(await machine.firewallRuleExists(), false, "the Weir firewall rule is still there after --remove-firewall");
    const configured = await machine.runTray(["--configure-firewall"]);
    assert.equal(configured.exitCode, 0, `Weir.exe --configure-firewall exited ${configured.exitCode}: ${configured.stderr}`);
    assert.equal(await machine.firewallRuleExists(), true, "there is no Weir firewall rule after --configure-firewall");
    await machine.stopWeir();
    const logBefore = (await machine.readText(machine.paths.trayLog)).length;
    const uninstall = await machine.uninstall();
    assert.equal(uninstall.exitCode, 0, `the uninstaller exited ${uninstall.exitCode}`);
    await wait(world, async () => ((await machine.exists(machine.paths.trayExe)) ? undefined : true), "the uninstaller to remove Weir.exe", 120_000, 1000);
    assert.equal(await machine.firewallRuleExists(), false, "the uninstall left the Weir firewall rule");
    assert.ok((await machine.readText(machine.paths.trayLog)).slice(logBefore).includes("Uninstall: removed the Weir firewall rule."), "the uninstall did not log that it removed the rule");
    facts.push("--remove-firewall and --configure-firewall took the rule away and made it, and the uninstall removed it itself because it ran as an administrator");
    await installBuildUnderTest(world);
  } finally {
    await tray.end();
    await machine.removeFromDataFolder("lan-access");
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}
