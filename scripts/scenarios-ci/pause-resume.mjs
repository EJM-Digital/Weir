// pause-resume, ported from scripts/scenarios/Weir.Scenarios.WeirOnly.ps1: the server's half of Pause and resume. The tray asks by writing
// pause-request.json in Weir's data folder, and the server's watcher for that file is the same on Linux, so the request is written there, in the
// container, the way the tray writes it. The tray's own side stays with the Windows shards.
import assert from "node:assert/strict";

import { activity, expectCleaned, expectOriginalsKept, filesOf, lookAndWait, makeWorkflow, masterFilm, releaseIn, startedRemuxJobCount, startLook, wait, waitForProcessed } from "./workflow-support.mjs";

const REQUEST_FILE = "/data/weir/pause-request.json";
const PAUSED = "system.processing_paused";
const RESUMED = "system.processing_resumed";
const PAUSE_REASON = "Processing is paused";
const BY_THE_TRAY = /The tray/;

/** Asks Weir to pause or resume the way the tray does: a request file, written whole so the server never reads half of it. */
async function writeRequest(world, paused) {
  await world.box.writeFileAtomically(REQUEST_FILE, JSON.stringify({ paused, requested_at: new Date().toISOString() }));
}

const pauseState = (world) => world.weir.get("/pause");

const waitUntilResumed = (world) => wait(world, async () => ((await pauseState(world)).paused ? undefined : true), "Weir to resume on the tray's request", 30_000);

/** Resumes Weir on the tray's request when it is paused, as an earlier run that failed while paused would leave it. */
async function resumeIfPaused(world) {
  if (!(await pauseState(world)).paused) return;
  await writeRequest(world, false);
  await waitUntilResumed(world);
}

const trayLines = async (world, eventType) => (await activity(world, eventType)).filter((item) => BY_THE_TRAY.test(JSON.stringify(item)));

async function pauseAndResume(world, workflow) {
  assert.equal((await pauseState(world)).paused, false, "Weir is not paused to begin with");

  await writeRequest(world, true);
  const paused = await wait(world, async () => ((await pauseState(world)).paused ? pauseState(world) : undefined), "Weir to pause on the tray's request", 30_000);
  assert.equal(paused.scan_while_paused, true, 'the pause left "keep looking for new files" as it was');
  await wait(world, async () => ((await world.box.exists(REQUEST_FILE)) ? undefined : true), "Weir to take the request file away", 30_000);

  const startedBefore = await startedRemuxJobCount(world);
  const release = releaseIn(workflow, "Paused.Film.2018");
  await world.box.dropRelease({ film: (await masterFilm(world)).path, folder: release.folder, name: release.name });
  await startLook(world, workflow, { queueFiles: true });
  const held = await wait(world, async () => (await filesOf(world, workflow)).find((file) => file.relative_path === release.relative), "Weir to find the film and hold it", 90_000);
  assert.ok(held.status !== "processed" && held.status !== "processing", `Weir found the film and is holding it (status ${held.status}: ${held.status_reason})`);
  assert.ok(String(held.status_reason).startsWith(PAUSE_REASON), `the reason it waits is the pause ("${held.status_reason}")`);
  // Two more looks go by, each a chance for Weir to start the film: it does not, and the film and its files are as they were.
  await lookAndWait(world, workflow, { queueFiles: true });
  await lookAndWait(world, workflow, { queueFiles: true });
  assert.equal(await startedRemuxJobCount(world), startedBefore, "Weir started no pass while paused");
  assert.equal(await world.box.exists(release.output), false, "Weir wrote nothing to the output folder while paused");
  assert.ok((await world.box.exists(release.film)) && (await world.box.exists(release.nfo)), "Weir removed nothing from the watched folder while paused");

  await writeRequest(world, false);
  await waitUntilResumed(world);
  const began = Date.now();
  // The look a periodic scan would make is asked for at once, rather than after a guessed wait for the schedule.
  await startLook(world, workflow, { queueFiles: true });
  await waitForProcessed(world, workflow, release, "the waiting film to be cleaned once Weir resumed");
  const seconds = Math.round((Date.now() - began) / 1000);
  await expectCleaned(world, release.output);
  assert.equal(await startedRemuxJobCount(world), startedBefore + 1, "exactly one pass ran for it");
  await expectOriginalsKept(world, release);

  assert.ok((await trayLines(world, PAUSED)).length >= 1 && (await trayLines(world, RESUMED)).length >= 1, "Activity shows Paused and Resumed, by the tray");
  return (
    `Pause and resume requests in the tray's file were taken and deleted; a film dropped in during the pause was found and held ("${held.status_reason}") ` +
    `with nothing cleaned, written or removed through two more looks at the folder, then cleaned once after the resume and a look at the folder, in ${seconds} s; Activity shows both by the tray.`
  );
}

export async function pauseResume(world) {
  const workflow = await makeWorkflow(world, { name: "Scenario pause", folder: "pause" });
  await resumeIfPaused(world);
  try {
    return await pauseAndResume(world, workflow);
  } finally {
    // Whatever happens, Weir is not left paused for the next scenario. A failure here must not hide the failure of the scenario.
    await resumeIfPaused(world).catch(() => {});
  }
}
