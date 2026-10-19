// What the with-deluno scenarios share about a film that goes from Deluno through qBittorrent to Weir and back: the waits, which of Weir's files a
// hand-off ended up as, and the picture of where a film stopped. All of it is stateless; a run keeps what it made on the world it is given.
import { wait } from "../workflow-support.mjs";

export const DOWNLOAD_CEILING_MS = 8 * 60_000;
export const HANDOFF_CEILING_MS = 10 * 60_000;
export const ANSWER_CEILING_MS = 3 * 60_000;
const POLL_MS = 2000;

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

/** Every file Weir lists. */
export const weirFiles = async (world) => (await world.weir.get("/processing/files?limit=1000")).files;

/** The Weir rows about a release, by its folder or file name. */
export const weirRowsAbout = async (world, release) => (await weirFiles(world)).filter((file) => String(file.relative_path).includes(release));

/** What each application says about the film, for the message of a scenario that stopped: where it stopped is the first thing to know. */
export async function whereItStopped(world, film) {
  try {
    const rows = (await weirRowsAbout(world, film.title)).map((file) => ({ path: file.relative_path, status: file.status, reason: file.status_reason, failure: file.failure_class, handback: file.handback }));
    const activity = (await world.weir.get("/activity/recent?limit=15")).items.map((item) => `${item.result}: ${item.title}`);
    const handoff = await world.deluno.handoffOf(film.release);
    return `Weir shows ${JSON.stringify(rows)}; its recent Activity: ${JSON.stringify(activity)}; Deluno shows ${JSON.stringify(handoff)}.`;
  } catch (error) {
    return `(Where it stopped could not be read: ${error.message})`;
  }
}

/** Runs a scenario's steps, and when one throws adds where the film stopped to what it said. */
export async function reportingWhereItStopped(world, film, steps) {
  try {
    return await steps();
  } catch (error) {
    throw new Error(`${error.message} ${await whereItStopped(world, film)}`, { cause: error });
  }
}

/** Waits for qBittorrent to have all of the film's release. Returns qBittorrent's own record of the torrent. */
export function waitForDownload(world, film) {
  return wait(
    world,
    async () => {
      const torrent = await world.deluno.qbittorrent.torrent(film.infohash);
      return torrent && torrent.progress >= 1 && torrent.amount_left === 0 ? torrent : undefined;
    },
    `qBittorrent to finish ${film.release}`,
    DOWNLOAD_CEILING_MS,
    POLL_MS,
  );
}

/**
 * Waits for Deluno to show a hand-off for the release that `settled` accepts, and returns it. A hand-off Deluno shows as failed stops the wait at
 * once unless the scenario is waiting for exactly that (`failureExpected`).
 */
export function waitForHandoff(world, film, settled, what, { failureExpected = false, ceilingMs = HANDOFF_CEILING_MS } = {}) {
  return wait(
    world,
    async () => {
      const found = await world.deluno.handoffOf(film.release);
      if (found?.status === "failed" && !failureExpected) throw new Error(`Deluno shows the hand-off of ${film.release} failed: ${found.failureMessage}`);
      return settled(found) ? found : undefined;
    },
    what,
    ceilingMs,
    POLL_MS,
  );
}

/** Waits for Weir to show rows that satisfy `accepted` for the release, and returns them. */
export function waitForWeirRows(world, release, accepted, what, ceilingMs = ANSWER_CEILING_MS) {
  return wait(
    world,
    async () => {
      const rows = await weirRowsAbout(world, release);
      return accepted(rows) ? rows : undefined;
    },
    what,
    ceilingMs,
    POLL_MS,
  );
}
