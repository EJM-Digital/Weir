// deluno-film-handoff, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: a film Deluno hands to Weir is followed through both applications.
// The VM version follows the films Deluno's own suite made. Here nothing else makes one, so the scenario makes it the way a person does: a film is
// added to Deluno and its release sent to qBittorrent, which downloads it from the release source; Deluno hands the finished download to Weir,
// Weir cleans it, and Deluno imports the cleaned file. The checks are the VM's: Weir shows the cleaned file as processed with Deluno's imported
// answer recorded against it, and Deluno shows its own import done.
import assert from "node:assert/strict";

import { wait } from "../workflow-support.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

const DOWNLOAD_CEILING_MS = 8 * 60_000;
const HANDOFF_CEILING_MS = 10 * 60_000;
const ANSWER_CEILING_MS = 3 * 60_000;
const lastPart = (path) => String(path).replace(/[\\/]+$/, "").split(/[\\/]/).at(-1);

/** The Weir files a Deluno hand-off ended up as: the output path's last part is the cleaned file's name, or the release folder's. */
export function weirFilesFor(handoff, files) {
  if (!handoff.outputPath) return [];
  const leaf = lastPart(handoff.outputPath);
  return files.filter((file) => {
    const copy = file.handback?.output_path;
    if (!copy) return false;
    const parts = String(copy).replace(/[\\/]+$/, "").split(/[\\/]/);
    return parts.at(-1) === leaf || parts.at(-2) === leaf;
  });
}

/** What each application says about the film, for the message of a scenario that stopped: where it stopped is the first thing to know. */
async function whereItStopped(world, film) {
  try {
    const files = (await world.weir.get("/processing/files?limit=1000")).files.filter((file) => file.relative_path.includes(film.title));
    const rows = files.map((file) => ({ path: file.relative_path, status: file.status, reason: file.status_reason, failure: file.failure_class, handback: file.handback }));
    const activity = (await world.weir.get("/activity/recent?limit=15")).items.map((item) => `${item.result}: ${item.title}`);
    const handoff = await world.deluno.handoffOf(film.release);
    return `Weir shows ${JSON.stringify(rows)}; its recent Activity: ${JSON.stringify(activity)}; Deluno shows ${JSON.stringify(handoff)}.`;
  } catch (error) {
    return `(Where it stopped could not be read: ${error.message})`;
  }
}

export async function delunoFilmHandoff(world) {
  await syncedWorkflows(world);
  const film = await world.deluno.makeFilm({ box: world.box, title: `Scn${world.runId}Film`, year: 2020 });
  try {
    return await follow(world, film);
  } catch (error) {
    throw new Error(`${error.message} ${await whereItStopped(world, film)}`, { cause: error });
  }
}

async function follow(world, film) {
  await world.deluno.sendFilm(film);

  const downloaded = await wait(
    world,
    async () => {
      const torrent = await world.deluno.qbittorrent.torrent(film.infohash);
      return torrent && torrent.progress >= 1 && torrent.amount_left === 0 ? torrent : undefined;
    },
    `qBittorrent to finish ${film.release}`,
    DOWNLOAD_CEILING_MS,
    2000,
  );
  const handoff = await wait(
    world,
    async () => {
      const found = await world.deluno.handoffOf(film.release);
      if (found?.status === "failed") throw new Error(`Deluno shows the hand-off of ${film.release} failed: ${found.failureMessage}`);
      return found?.status === "completed" && found.importJobId ? found : undefined;
    },
    `the hand-off of ${film.release} to settle`,
    HANDOFF_CEILING_MS,
    2000,
  );
  assert.equal(handoff.mediaType, "movies", `Deluno handed over a film (it reads ${handoff.mediaType})`);

  const copies = await wait(
    world,
    async () => {
      const found = weirFilesFor(handoff, (await world.weir.get("/processing/files?limit=1000")).files);
      return found.length > 0 ? found : undefined;
    },
    `Weir to show the cleaned file for Deluno's hand-off of ${film.release}`,
    ANSWER_CEILING_MS,
    2000,
  );
  for (const copy of copies) assert.equal(copy.status, "processed", `Weir shows ${copy.relative_path} as processed (it reads ${copy.status})`);

  // Deluno's own record of the import, and then its answer reaching Weir, come after the hand-off settles.
  await wait(
    world,
    async () => ((await world.deluno.handoffOf(film.release))?.outcome?.outcome === "imported" ? true : undefined),
    `Deluno to show its own import of ${film.release} done`,
    ANSWER_CEILING_MS,
    2000,
  );
  await wait(
    world,
    async () => {
      const found = weirFilesFor(handoff, (await world.weir.get("/processing/files?limit=1000")).files);
      return found.length > 0 && found.every((copy) => copy.handback?.outcome === "imported") ? found : undefined;
    },
    "Weir to have Deluno's imported answer recorded against the cleaned file",
    ANSWER_CEILING_MS,
    2000,
  );
  return (
    `Followed Deluno's hand-off of ${film.release} through both applications: qBittorrent finished it (${downloaded.size} bytes), Deluno handed it to Weir, ` +
    `Weir shows ${copies.map((copy) => copy.relative_path).join(", ")} as processed with Deluno's imported answer recorded against it, and Deluno's own record says imported.`
  );
}
