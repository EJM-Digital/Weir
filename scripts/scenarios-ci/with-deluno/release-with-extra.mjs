// deluno-release-with-extra, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: a release whose folder holds a film and an extra under the
// workflow's minimum size. Deluno leaves a file below the minimum out of the hand-off (it asks Weir for the minimum), so the extra stays in the
// download client's folder, and Weir neither writes it to its output nor fails over it. The VM version looked for such a release among the ones
// Deluno's suite made; here the scenario makes it, and reads the extra where it stays, in the folder qBittorrent downloaded to.
//
// The conversation is kept as a recording (real-release-with-extra): the one hand-off in which Deluno lists the files it names (`sourceFiles`).
import assert from "node:assert/strict";

import { FOLDERS } from "./deluno-world.mjs";
import { followToImport } from "./film-handoff.mjs";
import { reportingWhereItStopped, weirRowsAbout } from "./handoffs.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

const EXTRA = { path: "Extras/Featurette.mkv", bytes: 2_000_000 };
const EXTRA_NAME = "Featurette.mkv";

export async function delunoReleaseWithExtra(world) {
  const { workflows } = await syncedWorkflows(world);
  const film = await world.deluno.makeFilm({ box: world.box, title: "ScnExtra", year: 2020, extras: [EXTRA] });
  return reportingWhereItStopped(world, film, async () => {
    await world.deluno.sendFilm(film);
    const { handoff, copies } = await followToImport(world, film);

    // What Deluno sent Weir, as the proxy saw it: its own list of hand-offs does not show the files it named.
    const [sent] = await world.deluno.exchanges.where("to-weir", (exchange) => exchange.request.method === "POST" && exchange.request.body?.handoffId === handoff.id);
    assert.ok(sent, "the proxy saw Deluno hand the release to Weir");
    const listed = sent.request.body.sourceFiles;
    assert.ok(Array.isArray(listed) && listed.length === 1, `Deluno's hand-off names the one file at or above the minimum (it lists ${JSON.stringify(listed)})`);
    assert.ok(!listed.some((file) => String(file).includes(EXTRA_NAME)), `Deluno's hand-off does not name the extra (it lists ${JSON.stringify(listed)})`);

    const workflow = workflows.find((candidate) => candidate.id === copies[0].library_id);
    assert.ok(workflow && workflow.min_file_size_mb > 0, "the workflow the film was cleaned in has a minimum size, so there is an extra under it");
    assert.ok(EXTRA.bytes < workflow.min_file_size_mb * 1024 * 1024, `the extra (${EXTRA.bytes} bytes) is under the workflow's ${workflow.min_file_size_mb} MB minimum`);

    const kept = `${FOLDERS.downloads}/${film.release}/${EXTRA.path}`;
    assert.ok(await world.box.exists(kept), `the extra is still in the release folder where qBittorrent left it (${kept})`);
    assert.equal(await world.box.sizeOf(kept), EXTRA.bytes, "the extra is as it was downloaded");

    const rows = await weirRowsAbout(world, "Featurette");
    const notLeftAlone = rows.filter((row) => row.status !== "skipped");
    assert.equal(notLeftAlone.length, 0, `Weir either never saw the extra or left it alone as skipped (it shows ${notLeftAlone.map((row) => `${row.relative_path}: ${row.status}`).join(", ")})`);
    assert.deepEqual(await world.box.findNamed("/weir", EXTRA_NAME), [], "Weir wrote nothing named for the extra into any output folder");

    const { status_counts: counts } = await world.weir.get(`/processing/files?limit=1&library_id=${workflow.id}`);
    assert.ok(counts.processing_failed === 0 && counts.rejected === 0, `nothing in '${workflow.name}' needs the person (failed ${counts.processing_failed}, rejected ${counts.rejected})`);

    world.deluno.keepConversation(handoff, {
      state: "real-release-with-extra",
      note: "A qBittorrent release folder holding a film and an extra under Weir's minimum size, handed to Weir by a real Deluno: sourceFiles names only the film, and the extra stays in the download. The hand-off, Deluno's questions about it, Weir's report and the outcome, as the proxy between them saw it.",
    });
    return (
      `In '${workflow.name}', Deluno handed over ${film.release} naming only the film (${listed.join(", ")}); the ${EXTRA.bytes}-byte extra under the ${workflow.min_file_size_mb} MB minimum ` +
      `is still in qBittorrent's folder, Weir wrote nothing for it to its output and ${rows.length === 0 ? "never saw it" : "left it alone as skipped"}, and nothing in the workflow needs the person.`
    );
  });
}
