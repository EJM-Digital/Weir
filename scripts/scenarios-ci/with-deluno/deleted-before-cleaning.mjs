// deluno-deleted-before-cleaning (#1018): Deluno hands Weir a release, and the download is deleted from its folder before Weir starts on it. Weir is
// paused so the hand-off waits its turn, which is when a person (or qBittorrent) can take the files away; once it resumes, Weir reports the hand-off
// failed because the download is gone, and Deluno shows it failed in Weir's words.
//
// The conversation is kept as a recording (real-deleted-before-cleaning), the status questions Deluno asks while the hand-off waits included.
import assert from "node:assert/strict";

import { wait } from "../workflow-support.mjs";
import { FOLDERS } from "./deluno-world.mjs";
import { ANSWER_CEILING_MS, reportingWhereItStopped, waitForDownload, waitForHandoff } from "./handoffs.mjs";

// A download that is gone is looked for again before Weir tells Deluno: a share that dropped for a moment is not a deleted download. The file stays
// listed for ten minutes after a scan last saw it and is looked at again a minute after that, so the report comes about eleven minutes in.
const GONE_CEILING_MS = 16 * 60_000;
// Deluno keeps a hand-off whose download Weir found gone as its own state, in its own words, not as a failure.
const SOURCE_GONE = "source-gone";
import { failsOnPurpose, waitForReport } from "./failure-support.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

const pause = (world, paused) => world.weir.write("PUT", "/pause", { paused, scan_while_paused: true });

export async function delunoDeletedBeforeCleaning(world) {
  await syncedWorkflows(world);
  const film = failsOnPurpose(world, await world.deluno.makeFilm({ box: world.box, title: "ScnGone", year: 2020 }));
  const paused = await pause(world, true);
  assert.equal(paused.status, 200, `Weir paused (answered ${paused.status}: ${paused.text})`);
  try {
    return await reportingWhereItStopped(world, film, async () => {
      await world.deluno.sendFilm(film);
      await waitForDownload(world, film);
      // Weir has taken the hand-off in and holds it: Deluno's call was answered, and nothing is cleaned while paused.
      const accepted = await wait(
        world,
        async () => (await world.deluno.exchanges.where("to-weir", (exchange) => exchange.request.method === "POST" && JSON.stringify(exchange.request.body ?? "").includes(film.release)))[0],
        `Weir to take in Deluno's hand-off of ${film.release}`,
        ANSWER_CEILING_MS,
        2000,
      );
      assert.equal(accepted.reply.status, 200, `Weir accepted the hand-off while paused (answered ${accepted.reply.status}: ${JSON.stringify(accepted.reply.body)})`);

      const folder = `${FOLDERS.downloads}/${film.release}`;
      await world.box.removeTree(folder);
      assert.equal(await world.box.exists(`${folder}/${film.release}.mkv`), false, "the download is deleted from its folder");
      const resumed = await pause(world, false);
      assert.equal(resumed.status, 200, `Weir resumed (answered ${resumed.status}: ${resumed.text})`);

      const handoff = await waitForHandoff(world, film, (found) => found?.status === SOURCE_GONE, `Deluno to show the hand-off of ${film.release} as source-gone`, { failureExpected: true, ceilingMs: GONE_CEILING_MS });
      const report = await waitForReport(world, handoff, (body) => body.status === "failed", `Weir's report of ${film.release} to reach Deluno`);
      const sent = report.request.body;
      assert.equal(sent.failureClass, "source_gone", `Weir reported the download gone (failure class ${sent.failureClass}: ${sent.message})`);
      assert.ok(report.reply.status >= 200 && report.reply.status < 300, `Deluno accepted the report (it answered ${report.reply.status})`);
      assert.match(String(handoff.failureMessage), /The download was gone before .* could clean it./, `Deluno says the download was gone (it shows "${handoff.failureMessage}")`);
      assert.equal(await world.box.exists(`${folder}/${film.release}.mkv`), false, "Weir did not bring the deleted download back");

      world.deluno.keepConversation(handoff, {
        state: "real-deleted-before-cleaning",
        note: "A release handed to Weir by a real Deluno while Weir was paused, and deleted from its folder before Weir started on it: Deluno's questions while it waited, then Weir's failed report (source_gone) and Deluno's reply, as the proxy between them saw it.",
      });
      return `Weir held Deluno's hand-off of ${film.release} while paused; once the download was deleted and Weir resumed it reported "${sent.message}" (failure class source_gone) and Deluno shows the hand-off as ${handoff.status} ("${handoff.failureMessage}").`;
    });
  } finally {
    await pause(world, false);
  }
}
