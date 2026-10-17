// update-requests, ported from scripts/scenarios/Weir.Scenarios.Tray.ps1: the buttons System > About offers for updates work only through the
// real tray. Weir offers them only while the tray is alive; each press leaves a flag file, the tray takes it, and the update state comes to rest
// with an answer. Run against the release feed on GitHub, as the VM did.
import assert from "node:assert/strict";

import { lineWith, linesAfter } from "./tray-log.mjs";
import { ensureWeir, wait } from "./windows-install.mjs";
import { restoreWeir } from "./tray-world.mjs";

const TRAY_ALIVE_CEILING_MS = 90_000;
const CHECK_CEILING_MS = 180_000;
const DOWNLOAD_CEILING_MS = 600_000;
const RESTING = ["idle", "failed", "downloaded"];

export async function updateRequests(world) {
  await ensureWeir(world);
  const { weir, machine } = world;
  const trayLog = async (from) => linesAfter(await machine.readText(machine.paths.trayLog), from);
  const logLength = async () => (await machine.readText(machine.paths.trayLog)).length;
  const state = () => weir.get("/suite/update-state");
  const atRest = (what, ceilingMs) => wait(world, async () => ((await state()).state && RESTING.includes((await state()).state) ? state() : undefined), what, ceilingMs, 1000);

  const alive = await wait(world, async () => ((await state()).tray_running === true ? state() : undefined), "the tray to say it is alive", TRAY_ALIVE_CEILING_MS, 1000);
  assert.equal(alive.tray_running, true, "Weir counts the tray alive, so the buttons are offered");

  // A tray set to install by itself must not restart Weir under the run: only the person's own buttons are pressed.
  const original = await weir.get("/suite/update-settings");
  const settings = (mode) => ({ mode, check_on_startup: Boolean(original.check_on_startup), check_interval_minutes: Number(original.check_interval_minutes) });
  await weir.write("PUT", "/suite/update-settings", settings("NotifyOnly"));
  let checkedText;
  let downloadText;
  try {
    let from = await logLength();
    const check = await weir.write("POST", "/suite/check-update");
    assert.ok(check.status === 200 && check.json.state === "checking", `Check for updates was answered ${check.status} with state ${check.json?.state}, not 200 with the step under way`);
    await wait(world, async () => lineWith(await trayLog(from), "Check-now flag detected"), "the tray to take the check request", 60_000, 500);
    const checked = await atRest("the check to come to an answer", CHECK_CEILING_MS);
    assert.ok(!(checked.state === "failed" && !checked.failure), "a failed check does not say why");
    checkedText = checked.state === "failed" ? `failed with "${checked.failure}"` : checked.state === "downloaded" ? `found and downloaded ${checked.pending_version}` : "found nothing newer";

    if (checked.state === "downloaded") {
      downloadText = "not pressed: the check had already downloaded the update";
    } else {
      from = await logLength();
      const download = await weir.write("POST", "/suite/download-update");
      assert.ok(download.status === 200 && download.json.state === "downloading", `Download update was answered ${download.status} with state ${download.json?.state}, not 200 with the step under way`);
      await wait(world, async () => lineWith(await trayLog(from), "Download-now flag detected"), "the tray to take the download request", 60_000, 500);
      const downloaded = await atRest("the download request to come to an answer", DOWNLOAD_CEILING_MS);
      assert.ok(!(downloaded.state === "failed" && !downloaded.failure), "a failed download does not say why");
      downloadText = downloaded.state === "downloaded" ? `downloaded ${downloaded.pending_version}` : `answered "${downloaded.failure}"`;
      // Restart and apply is pressed only with nothing downloaded: with an update downloaded it would install it over the build under test.
      if (downloaded.state !== "downloaded") {
        const apply = await weir.write("POST", "/suite/apply-update");
        assert.equal(apply.status, 409, `Restart and apply with nothing downloaded was answered ${apply.status}, not refused with 409`);
      }
    }
  } finally {
    await weir.write("PUT", "/suite/update-settings", settings(original.mode));
    // A downloaded update would be installed by the next Quit: the build under test stays.
    await machine.stopWeir();
    await machine.discardDownloadedUpdate();
    await restoreWeir(world);
  }
  return `The tray was alive; Check for updates made the tray log "Check-now flag detected" and the check ${checkedText}; Download update made it log "Download-now flag detected" and ${downloadText}.`;
}
