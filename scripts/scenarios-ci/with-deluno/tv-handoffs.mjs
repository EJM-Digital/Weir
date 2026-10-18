// deluno-tv-episode and deluno-season-pack (#1017, INT-DEL-02): a show's episode, and a season pack of two episodes, that a real Deluno hands to Weir.
// The VM scenarios handed over films only. Here Deluno gets a TV library of its own, Weir sets up a TV workflow from it, and a show is added by
// title and its release sent to qBittorrent as "Send anyway" does (Deluno has no internet here, so it knows no episodes: the hand-off is of the
// download, as it is for a release a person chose by hand). Weir cleans each episode into the TV output folder, and Deluno shows the hand-off done.
//
// The conversation of each is kept as a recording (real-tv-episode, real-season-pack).
import assert from "node:assert/strict";

import { wait } from "../workflow-support.mjs";
import { FOLDERS } from "./deluno-world.mjs";
import { ANSWER_CEILING_MS, reportingWhereItStopped, waitForDownload, waitForHandoff, weirFiles, weirFilesFor } from "./handoffs.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

const POLL_MS = 2000;
const TV = "tv";

/** Weir's TV workflow, once it has set it up from Deluno's TV library: linked to Deluno, with its folders from Deluno. */
async function tvWorkflow(world) {
  const { connection } = await syncedWorkflows(world);
  await world.deluno.tvLibrary();
  return wait(
    world,
    async () => {
      await world.weir.write("POST", `/media-managers/connections/${connection.id}/test`, {});
      return (await world.weir.get("/processing/libraries")).find(
        (workflow) => workflow.media_type === TV && workflow.folders_synced_from_connection_id === connection.id && workflow.watched_folder === FOLDERS.tvDownloads,
      );
    },
    "Weir to set up its TV workflow from Deluno's TV library",
    120_000,
    POLL_MS,
  );
}

async function followShow(world, { title, episodes, state, note }) {
  const workflow = await tvWorkflow(world);
  const release = await world.deluno.makeFilm({ box: world.box, title, year: 2020, episodes });
  return reportingWhereItStopped(world, release, async () => {
    await world.deluno.sendSeries(release);
    const downloaded = await waitForDownload(world, release);
    const handoff = await waitForHandoff(world, release, (found) => found?.status === "completed", `the hand-off of ${release.release} to settle`);
    assert.equal(handoff.mediaType, TV, `Deluno handed over TV (it reads ${handoff.mediaType})`);
    const copies = await wait(
      world,
      async () => {
        const found = weirFilesFor(handoff, await weirFiles(world));
        return found.length === episodes.length && found.every((copy) => copy.status === "processed") ? found : undefined;
      },
      `Weir to show ${episodes.length} cleaned episode(s) for Deluno's hand-off of ${release.release}`,
      ANSWER_CEILING_MS,
      POLL_MS,
    );
    assert.ok(copies.every((copy) => copy.library_id === workflow.id), "Weir cleaned the episodes in the TV workflow");
    for (const copy of copies) {
      assert.ok(String(copy.handback?.output_path).startsWith(`${FOLDERS.tvWeirOutput}/`), `the cleaned episode is in the TV output folder (${copy.handback?.output_path})`);
      assert.ok(await world.box.exists(copy.handback.output_path), `the cleaned episode is where Weir says (${copy.handback.output_path})`);
    }
    world.deluno.keepConversation(handoff, { state, note });
    return (
      `Deluno handed ${release.release} (${downloaded.size} bytes) to Weir as TV; Weir cleaned ${copies.length} episode(s) in its TV workflow into ${FOLDERS.tvWeirOutput} ` +
      `(${copies.map((copy) => copy.relative_path).join(", ")}) and Deluno shows the hand-off ${handoff.status}.`
    );
  });
}

export const delunoTvEpisode = (world) =>
  followShow(world, {
    title: "ScnShow",
    episodes: [1],
    state: "real-tv-episode",
    note: "One episode of a show in a qBittorrent release folder, handed to Weir by a real Deluno and cleaned in Weir's TV workflow: the hand-off, Deluno's questions about it, Weir's report and the outcome, as the proxy between them saw it.",
  });

export const delunoSeasonPack = (world) =>
  followShow(world, {
    title: "ScnPack",
    episodes: [1, 2],
    state: "real-season-pack",
    note: "A season pack of two episodes in a qBittorrent release folder, handed to Weir by a real Deluno and cleaned in Weir's TV workflow: the hand-off, Deluno's questions about it, Weir's one report for the pack and the outcome, as the proxy between them saw it.",
  });
