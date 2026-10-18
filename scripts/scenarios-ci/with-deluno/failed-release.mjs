// deluno-failed-release (#1018): Deluno hands Weir a release Weir cannot read (a file of zero bytes, as long as a film). Weir fails it and tells Deluno;
// Deluno shows the hand-off failed in Weir's words, and the download is left where it is for a person to look at.
//
// The conversation is kept as a recording (real-failed-unreadable): the hand-off, Deluno's questions about it, Weir's failed report and Deluno's reply.
import assert from "node:assert/strict";

import { FOLDERS } from "./deluno-world.mjs";
import { reportingWhereItStopped, waitForDownload, waitForHandoff, weirRowsAbout } from "./handoffs.mjs";
import { failsOnPurpose, reportsAbout, waitForReport } from "./failure-support.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

export async function delunoFailedRelease(world) {
  await syncedWorkflows(world);
  const film = failsOnPurpose(world, await world.deluno.makeFilm({ box: world.box, title: "ScnFail", year: 2020, zeros: true }));
  return reportingWhereItStopped(world, film, async () => {
    await world.deluno.sendFilm(film);
    await waitForDownload(world, film);
    const handoff = await waitForHandoff(world, film, (found) => found?.status === "failed", `Deluno to show the hand-off of ${film.release} as failed`, { failureExpected: true });

    const report = await waitForReport(world, handoff, (body) => body.status === "failed", `Weir's failed report of ${film.release} to reach Deluno`);
    assert.ok(report.reply.status >= 200 && report.reply.status < 300, `Deluno accepted Weir's failed report (it answered ${report.reply.status})`);
    const said = report.request.body.message;
    assert.ok(said, "Weir's report says in words why it failed");
    assert.ok(String(handoff.failureMessage).includes(said), `Deluno shows the hand-off failed in Weir's words (Weir said "${said}"; Deluno shows "${handoff.failureMessage}")`);
    assert.equal((await reportsAbout(world, handoff)).filter((exchange) => exchange.request.body.status === "completed").length, 0, "Weir never reported the unreadable file as finished");

    const rows = await weirRowsAbout(world, film.title);
    assert.ok(rows.length > 0 && rows.every((row) => row.status !== "processed"), `Weir shows the file as not cleaned (${rows.map((row) => `${row.relative_path}: ${row.status}`).join(", ")})`);
    const original = `${FOLDERS.downloads}/${film.release}/${film.release}.mkv`;
    assert.ok(await world.box.exists(original), `the download is left where it is (${original})`);
    assert.ok(await world.deluno.qbittorrent.torrent(film.infohash), "qBittorrent still has the torrent");

    world.deluno.keepConversation(handoff, {
      state: "real-failed-unreadable",
      note: "A release whose film is a file of zero bytes, handed to Weir by a real Deluno: Weir cannot read it and reports it failed. The hand-off, Deluno's questions about it, Weir's report and Deluno's reply, as the proxy between them saw it.",
    });
    return (
      `Deluno's hand-off of ${film.release} failed in Weir's words ("${said}"; failure class ${report.request.body.failureClass ?? "none"}, disposition ${report.request.body.disposition ?? "none"}); ` +
      `Weir shows ${rows.map((row) => `${row.relative_path} as ${row.status}`).join(", ")}, the download is still in qBittorrent's folder and Weir never called it finished.`
    );
  });
}
