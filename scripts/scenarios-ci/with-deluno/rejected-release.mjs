// deluno-rejected-release (#1018, FL-38): Deluno hands Weir a release Weir rejects by its rules (a file with sound and no picture), in a workflow set
// to reject a bad release. Weir tells Deluno it was rejected and leaves the download in place; Deluno refuses that release and says it will remove the
// download; and Weir's Activity says so in plain words.
//
// The conversation is kept as a recording (real-rejected-audio-only).
import assert from "node:assert/strict";

import { activity, wait } from "../workflow-support.mjs";
import { changeWorkflow, failsOnPurpose, waitForReport } from "./failure-support.mjs";
import { ANSWER_CEILING_MS, reportingWhereItStopped, waitForDownload, waitForHandoff, waitForWeirRows } from "./handoffs.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

// The connection is named for the machine it is on, so the manager is "Deluno on <machine>" in Weir's words.
const REMOVE_SENTENCE = /will remove the download; Weir left it in place./;

export async function delunoRejectedRelease(world) {
  const { workflows } = await syncedWorkflows(world);
  const workflow = workflows[0];
  const film = failsOnPurpose(world, await world.deluno.makeFilm({ box: world.box, title: "ScnReject", year: 2020, content: "audio-only" }));
  const policy = workflow.failure_policy;
  await changeWorkflow(world, workflow, { failure_policy: "reject" });
  try {
    return await reportingWhereItStopped(world, film, async () => {
      await world.deluno.sendFilm(film);
      await waitForDownload(world, film);
      const handoff = await waitForHandoff(world, film, (found) => Boolean(found), `Deluno to hand ${film.release} to Weir`, { failureExpected: true });

      const report = await waitForReport(world, handoff, (body) => body.status === "failed", `Weir's report of ${film.release} to reach Deluno`);
      const sent = report.request.body;
      assert.equal(sent.disposition, "rejected", `Weir reported the release rejected (it said disposition ${sent.disposition})`);
      assert.equal(sent.sourceRemoved, false, "Weir said it did not remove the download");
      assert.ok(report.reply.status >= 200 && report.reply.status < 300, `Deluno accepted the rejection (it answered ${report.reply.status}: ${JSON.stringify(report.reply.body)})`);

      const rows = await waitForWeirRows(world, film.title, (found) => found.some((row) => row.status === "rejected"), `Weir to show ${film.release} as rejected`);
      const rejected = await wait(
        world,
        async () => (await activity(world, "processing.file_rejected")).find((item) => String(item.title).includes(film.release) || JSON.stringify(item).includes(film.title)),
        `Weir's Activity to say it rejected ${film.release}`,
        ANSWER_CEILING_MS,
        2000,
      );
      assert.match(JSON.stringify(rejected), REMOVE_SENTENCE, `Weir's Activity says Deluno will remove the download and Weir left it in place (it says ${JSON.stringify(rejected)})`);

      const blocked = await wait(
        world,
        async () => {
          const list = await world.deluno.deluno.must("GET", "/api/blocked-releases");
          return list.find((entry) => JSON.stringify(entry).includes(film.release));
        },
        `Deluno to refuse ${film.release}`,
        ANSWER_CEILING_MS,
        2000,
      );
      await wait(world, async () => ((await world.deluno.qbittorrent.torrent(film.infohash)) ? undefined : true), `Deluno to have qBittorrent forget ${film.release}`, ANSWER_CEILING_MS, 2000);

      world.deluno.keepConversation(handoff, {
        state: "real-rejected-audio-only",
        note: "A release whose film has sound and no picture, handed to Weir by a real Deluno in a workflow set to reject: Weir reports it rejected and leaves the download; Deluno refuses the release. The hand-off, Deluno's questions about it, Weir's report and Deluno's reply, as the proxy between them saw it.",
      });
      return (
        `Weir rejected ${film.release} (${rows.map((row) => `${row.relative_path}: ${row.status}`).join(", ")}) and reported it to Deluno rejected with the download left in place; ` +
        `Weir's Activity says "${rejected.detail ?? rejected.title}"; Deluno refused the release (${blocked.id ?? "on its blocklist"}) and qBittorrent no longer has the torrent.`
      );
    });
  } finally {
    await changeWorkflow(world, workflow, { failure_policy: policy });
  }
}
