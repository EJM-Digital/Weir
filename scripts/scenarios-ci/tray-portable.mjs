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
import { ensureSignedIn } from "./windows-install.mjs";

/** What the tray of a copy that is not an install logs in place of starting its update checks (apps/tray/Weir.Tray/TrayUpdates.cs). */
export const NOT_INSTALLED_LINE = "Velopack: not installed (dev mode), skipping update checks.";

/** What Weir answers a request to update, from a copy that has no tray able to take it (apps/web/src/pages/system/tabs/about/update-actions.tsx says the same). */
export const CANNOT_UPDATE_DETAIL = "The Weir tray isn't running, so Weir can't update itself from here.";

const UPDATE_STARTS = [/^Checking for updates/, /^Update available/, /^No update available/, /^Downloading update/, /^Update downloaded/];

/** tray-portable: a copy of the install's files, run from a folder with no Update.exe beside it, checks for no update and says it cannot update. */
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
    assert.equal(await machine.exists(join(machine.paths.runtimeHome, "tray-heartbeat.json")), false, "the copy wrote a heartbeat, which tells Weir a tray is there to update it");
    assert.deepEqual(await machine.processes(), [], "the install's own tray or server runs although only the copy was started");
    const state = await weir.get("/suite/update-state");
    assert.equal(state.tray_running, false, "Weir counts the copy as a tray that can take an update request");
    facts.push(`the copy (Weir.exe in ${folder}) ran Weir on port ${PORT}, logged "${NOT_INSTALLED_LINE}", started no update check and wrote no heartbeat, so Weir counts no tray able to update it`);

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
