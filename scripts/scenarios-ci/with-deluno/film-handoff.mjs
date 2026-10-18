// deluno-film-handoff, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: a film Deluno hands to Weir is followed through both applications.
// The VM version follows the films Deluno's own suite made. Here nothing else makes one, so the scenario makes it the way a person does: a film is
// added to Deluno and its release sent to qBittorrent, which downloads it from the release source; Deluno hands the finished download to Weir,
// Weir cleans it, and Deluno imports the cleaned file. The checks are the VM's: Weir shows the cleaned file as processed with Deluno's imported
// answer recorded against it, and Deluno shows its own import done.
//
// The conversation between the two applications is kept as a recording (real-release-folder): the hand-off of a qBittorrent release folder, what
// Deluno asked about it, Weir's report that it was cleaned, and the outcome Deluno told Weir.
import assert from "node:assert/strict";

import { wait } from "../workflow-support.mjs";
import { ANSWER_CEILING_MS, reportingWhereItStopped, waitForDownload, waitForHandoff, weirFiles, weirFilesFor } from "./handoffs.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

export { weirFilesFor };

const POLL_MS = 2000;

/**
 * A film that went all the way through: made, sent, downloaded, handed to Weir, cleaned, imported by Deluno and the answer recorded in Weir.
 * Made once for the run and shared by the scenarios that start from a finished hand-off; a failure is not remembered, so the next scenario tries again.
 * @returns {Promise<{film: object, downloaded: object, handoff: object, copies: object[]}>}
 */
export function followedFilm(world) {
  world.followedFilm ??= (async () => {
    await syncedWorkflows(world);
    const film = await world.deluno.makeFilm({ box: world.box, title: `Scn${world.runId}Film`, year: 2020 });
    return reportingWhereItStopped(world, film, () => follow(world, film));
  })().catch((error) => {
    world.followedFilm = undefined;
    throw error;
  });
  return world.followedFilm;
}

export async function delunoFilmHandoff(world) {
  const { film, downloaded, copies } = await followedFilm(world);
  return (
    `Followed Deluno's hand-off of ${film.release} through both applications: qBittorrent finished it (${downloaded.size} bytes), Deluno handed it to Weir, ` +
    `Weir shows ${copies.map((copy) => copy.relative_path).join(", ")} as processed with Deluno's imported answer recorded against it, and Deluno's own record says imported.`
  );
}

async function follow(world, film) {
  await world.deluno.sendFilm(film);
  const followed = await followToImport(world, film);
  world.deluno.keepConversation(followed.handoff, {
    state: "real-release-folder",
    note: "A film in a qBittorrent release folder (the film and an .nfo), handed to Weir by a real Deluno and cleaned: the hand-off, what Deluno asked about it, Weir's report that it was cleaned and the outcome Deluno told Weir, as the proxy between them saw it.",
  });
  return followed;
}

/**
 * From a film that has been sent to Deluno's download client: the download, the hand-off, Weir's cleaned file shown as processed, Deluno's own
 * import, and its imported answer recorded in Weir.
 */
export async function followToImport(world, film) {
  const downloaded = await waitForDownload(world, film);
  const handoff = await waitForHandoff(world, film, (found) => found?.status === "completed" && found.importJobId, `the hand-off of ${film.release} to settle`);
  assert.equal(handoff.mediaType, "movies", `Deluno handed over a film (it reads ${handoff.mediaType})`);

  const copies = await wait(
    world,
    async () => {
      const found = weirFilesFor(handoff, await weirFiles(world));
      return found.length > 0 ? found : undefined;
    },
    `Weir to show the cleaned file for Deluno's hand-off of ${film.release}`,
    ANSWER_CEILING_MS,
    POLL_MS,
  );
  for (const copy of copies) assert.equal(copy.status, "processed", `Weir shows ${copy.relative_path} as processed (it reads ${copy.status})`);

  // Deluno's own record of the import, and then its answer reaching Weir, come after the hand-off settles.
  await wait(
    world,
    async () => ((await world.deluno.handoffOf(film.release))?.outcome?.outcome === "imported" ? true : undefined),
    `Deluno to show its own import of ${film.release} done`,
    ANSWER_CEILING_MS,
    POLL_MS,
  );
  const answered = await wait(
    world,
    async () => {
      const found = weirFilesFor(handoff, await weirFiles(world));
      return found.length > 0 && found.every((copy) => copy.handback?.outcome === "imported") ? found : undefined;
    },
    "Weir to have Deluno's imported answer recorded against the cleaned file",
    ANSWER_CEILING_MS,
    POLL_MS,
  );
  return { film, downloaded, handoff, copies: answered };
}
