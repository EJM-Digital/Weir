// tray-portable: a Weir.exe that is not an install. Its files are copied to a folder of their own with no Update.exe beside them, as a person
// copies them to a USB stick or a developer builds them. It runs Weir, and it does not pretend it can update: it checks for nothing, tells the
// server nothing about itself, says so in its log, and the menu's Check for updates opens System > About. Written for CI: the VM suite only
// ever ran the installed tray.
import assert from "node:assert/strict";
import { cp } from "node:fs/promises";
import { join } from "node:path";

import { choose } from "./tray-actions.mjs";
import { lineWith, linesWith, serverProcessIds } from "./tray-log.mjs";
import { menu } from "./tray-words.mjs";
import { browserOn, closeBrowsers, expectNoBrowser, installedAndStopped, PORT, restoreWeir, startTray } from "./tray-world.mjs";
import { ensureSignedIn, wait } from "./windows-install.mjs";

/** What the tray of a copy that is not an install logs in place of starting its update checks (apps/tray/Weir.Tray/TrayUpdates.cs). */
export const NOT_INSTALLED_LINE = "Velopack: not installed (portable or development copy), skipping update checks.";

/** What Weir tells a person who asks a copy that was not installed to update (apps/server/src/Weir.Infrastructure/Runtime/SystemServices.cs), on the refusal and in the update state the page shows. */
export const CANNOT_UPDATE_DETAIL =
  "This copy of Weir wasn't installed with the installer, so it can't update itself. To get a newer version, download the newest Weir-win-Setup.exe from the Weir releases page on GitHub and run it.";

/**
 * How long the copy's heartbeat may take to appear: the tray writes it right after the "not installed" line, and when that first write is refused
 * (a virus scanner holds the folder) the next beat, 30 s on (TrayUpdates.HeartbeatInterval), writes it. Every wait ends the moment the file is there.
 */
export const HEARTBEAT_CEILING_MS = 60_000;

const UPDATE_STARTS =[/^Checking for updates/, /^Update available/, /^No update available/, /^Downloading update/, /^Update downloaded/];

/** What the copy's heartbeat says. The tray logs that it is not an install and then writes the file, so a log line is no proof the file is there yet. */
export async function copyHeartbeat(world) {
  const file = join(world.machine.paths.runtimeHome, "tray-heartbeat.json");
  const read = async () => ((await world.machine.exists(file)) ? JSON.parse(await world.machine.readText(file)) : undefined);
  return wait(world, read, "the copy's tray to write its heartbeat", HEARTBEAT_CEILING_MS, 250);
}

/** tray-portable: a copy of the install's files, run from a folder with no Update.exe beside it, checks for no update and says it cannot update. Its heartbeat says it is alive and not installed. */
export async function trayPortable(world) {
  const { machine, weir } = world;
  await installedAndStopped(world);
  const folder = join(world.scratch, "portable-weir");
  await cp(join(machine.paths.installRoot, "current"), folder, { recursive: true });
  assert.equal(await machine.exists(join(world.scratch, "Update.exe")), false, "the folder the copy sits in holds an Update.exe, which would make it an install");
  const executable = join(folder, "Weir.exe");
  // A heartbeat from the install that ran before would look like the copy's own.
  await machine.removeFromDataFolder("tray-heartbeat.json", "update-state.json");

  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"], undefined, executable);
  const facts = [];
  try {
    await tray.ready();
    await ensureSignedIn(world);
    await tray.line(NOT_INSTALLED_LINE, { what: "the tray to say it is not an install and skips its update checks" });
    const log = await tray.log();
    for (const started of UPDATE_STARTS) assert.equal(linesWith(log, started).length, 0, `the copy started an update check although it is not an install: ${lineWith(log, started)}`);
    const heartbeat = await copyHeartbeat(world);
    assert.equal(heartbeat.installed, false, "the copy's heartbeat says it was installed, which tells Weir a tray is there to update it");
    assert.deepEqual(await machine.processes(), [], "the install's own tray or server runs although only the copy was started");
    const state = await weir.get("/suite/update-state");
    assert.equal(state.tray_running, false, "Weir counts the copy as a tray that can take an update request");
    assert.equal(state.cannot_update_reason, CANNOT_UPDATE_DETAIL, `System > About is told "${state.cannot_update_reason}"`);
    facts.push(`the copy (Weir.exe in ${folder}) ran Weir on port ${PORT}, logged "${NOT_INSTALLED_LINE}", started no update check and wrote a heartbeat that says it is not installed, so Weir counts no tray able to update it and tells a person why`);

    for (const [route, step] of [["/suite/check-update", "Check for updates"], ["/suite/download-update", "Download update"], ["/suite/apply-update", "Restart and apply"]]) {
      const refused = await weir.write("POST", route);
      assert.equal(refused.status, 409, `${step} was answered ${refused.status}, not refused with 409`);
      assert.equal(refused.json.detail, CANNOT_UPDATE_DETAIL, `${step} was refused with "${refused.json.detail}"`);
    }
    facts.push(`Check for updates, Download update and Restart and apply were each refused with 409 and "${CANNOT_UPDATE_DETAIL}"`);

    await choose(world, tray, menu.checkForUpdates);
    await tray.line("(source=tray-update-settings)", { what: "Check for updates to open System > About" });
    const about = await browserOn(world, PORT, "/system?tab=about");
    assert.equal(linesWith(await tray.log(), /^Checking for updates/).length, 0, "Check for updates started a check in a copy that is not an install");
    facts.push(`the menu's Check for updates opened ${about.commandLine.match(/http\S+/)[0]} and checked nothing`);
    await closeBrowsers(world, PORT);
    await expectNoBrowser(world, "after the browser was closed", PORT);
  } finally {
    // The copy's own tray and the servers it started, by process id: machine.stopWeir only knows the install.
    await machine.killProcess(tray.pid);
    for (const pid of serverProcessIds(await tray.log())) await machine.killProcess(pid);
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}
