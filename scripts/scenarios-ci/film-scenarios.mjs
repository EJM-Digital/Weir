// The scenarios of a film cleaned in a Weir-only workflow, ported from scripts/scenarios/Weir.Scenarios.WeirOnly.ps1 to run against the Docker
// image: a film dropped into a workflow with no manager, and Process again. Each makes the workflow it uses, named for the run, so it runs
// alone and reads nothing another scenario left.
import assert from "node:assert/strict";

import { MASTER_FILM, activity, dropAndClean, expectCleaned, expectOriginalsKept, filesOf, makeWorkflow, masterFilm, remuxJobCount, wait } from "./workflow-support.mjs";

// What a person sees, and what the server reports for it.
const COMPLETED = "processing.file_remux_pass_completed";
const SKIPPED_REPEAT = "processing.file_skipped_repeat";

/** weir-only-film: a film with three audio languages and two subtitle tracks, dropped into a Weir-only workflow, is cleaned. */
export async function weirOnlyFilm(world) {
  const workflow = await makeWorkflow(world, { name: "Scenario films", folder: "films" });
  const done = await dropAndClean(world, workflow, MASTER_FILM);
  const cleaned = await expectCleaned(world, done.release.output);
  await expectOriginalsKept(world, done.release);
  const said = (await activity(world, COMPLETED)).filter((item) => item.library_id === workflow.id && item.title.includes(`${MASTER_FILM}.mkv`));
  assert.ok(said.length >= 1, "Activity does not say the film was processed");
  const { summary } = await masterFilm(world);
  return (
    `A ${Math.round(summary.seconds)} s film (${summary.bytes} bytes, audio ${summary.audio.join("/")}, ${summary.subtitles.length} subtitle tracks) dropped into a Weir-only workflow ` +
    `was cleaned in ${done.seconds} s to ${cleaned.bytes} bytes with audio ${cleaned.audio.join("/")} and no subtitles; the original and its .nfo are untouched; Activity says it was processed.`
  );
}

/** process-again: asking Weir to process a cleaned film again skips it, says so, writes nothing and makes no job. */
export async function processAgain(world) {
  const workflow = await makeWorkflow(world, { name: "Scenario again", folder: "again" });
  const cleaned = await dropAndClean(world, workflow, "Retry.Film.2017");
  await expectCleaned(world, cleaned.release.output);
  const fileName = `${cleaned.release.name}.mkv`;
  const skipLinesBefore = (await activity(world, SKIPPED_REPEAT)).length;
  const jobsBefore = await remuxJobCount(world);
  const writtenBefore = await world.box.modified(cleaned.release.output);

  const answer = await world.weir.write("POST", `/processing/files/${cleaned.row.id}/requeue`, {});
  assert.equal(answer.status, 200, `Process again was answered 200 (it was ${answer.status}: ${answer.text})`);
  const { requeued, skipped, detail } = answer.json;
  assert.ok(requeued === 0 && skipped === 1, `nothing was queued and one file was skipped (requeued ${requeued}, skipped ${skipped})`);
  assert.ok(
    detail.startsWith("Weir already cleaned this file, so it skipped it. Already done: cleaned on "),
    `the answer says Weir already cleaned this file and skipped it: ${detail}`,
  );
  const line = await wait(
    world,
    async () => {
      const found = await activity(world, SKIPPED_REPEAT);
      return found.length > skipLinesBefore ? found.find((item) => item.trigger === "manual") : undefined;
    },
    "the Skipped line in Activity",
    60_000,
  );
  assert.ok(line.title === `Skipped: already done (${fileName})` && line.result === "skipped", `Activity says "${line.title}"`);
  assert.equal(await world.box.modified(cleaned.release.output), writtenBefore, "the cleaned file was not written a second time");
  assert.equal(await remuxJobCount(world), jobsBefore, "no second job was made");
  const row = (await filesOf(world, workflow)).find((file) => file.id === cleaned.row.id);
  assert.equal(row.status, "processed", `the file still reads processed (it reads ${row.status})`);
  return `Process again answered: "${detail}"; Activity gained "${line.title}"; the output and the job count were unchanged.`;
}
