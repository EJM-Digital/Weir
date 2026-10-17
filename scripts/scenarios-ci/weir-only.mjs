// The scenarios that need no manager, ported from scripts/scenarios/Weir.Scenarios.WeirOnly.ps1 to run against the Docker image: a film
// dropped into a Weir-only workflow, and Process again. Each makes the workflow it uses, named for the run, so it runs alone and reads nothing
// another scenario left.
//
// The film is made by the ffmpeg in the image and cleaned by the same ffmpeg, in folders inside the container (the bind-mounted form is the
// `media` scenario of docker-compose-smoke.mjs). The ceilings below are hang guards: every wait ends the moment its condition holds.
import assert from "node:assert/strict";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { cleanedFilmProblems } from "./film.mjs";

const FILM_SECONDS = 30;
const MASTER_FILM = "Big.Buck.Bunny.2008";
// What a person sees, and what the server reports for it.
const COMPLETED = "processing.file_remux_pass_completed";
const SKIPPED_REPEAT = "processing.file_skipped_repeat";
const REMUX_JOB_STATUSES = ["pending", "leased", "completed", "failed", "handler_ok_finalize_failed", "cancelled"];

const root = (world) => `/tmp/weir-scenarios/${world.runId}`;
const wait = (world, check, what, ceilingMs, intervalMs = 1000) => pollUntil(check, { what, ceilingMs, intervalMs, clock: world.clock });

/** The film every release in the run is a copy of, made once. */
async function masterFilm(world) {
  if (!world.film) {
    const path = `${root(world)}/source/${MASTER_FILM}.mkv`;
    await world.box.makeFilm({ path, seconds: FILM_SECONDS });
    world.film = { path, summary: await world.box.probe(path), sha256: await world.box.sha256(path) };
  }
  return world.film;
}

/** A movie workflow with no manager over folders of its own, keeping its originals so the scenario can see them. */
async function makeWorkflow(world, { name, folder }) {
  const base = `${root(world)}/${folder}`;
  const folders = { watched: `${base}/watched`, work: `${base}/work`, output: `${base}/output` };
  await world.box.ensureFolders(...Object.values(folders));
  const created = await world.weir.createLibrary({
    name: `${name} ${world.runId}`,
    media_type: "movie",
    watched_folder: folders.watched,
    work_folder: folders.work,
    output_folder: folders.output,
    ready_after_seconds: 0,
    min_file_size_mb: 1,
    skip_access_tests: true,
    remove_original_after_success: false,
    retry_backoff_seconds: 1,
    minimum_free_disk_space_mb: 0,
  });
  return { id: created.id, ...folders };
}

function releaseIn(workflow, name) {
  const folder = `${workflow.watched}/${name}`;
  return { name, folder, film: `${folder}/${name}.mkv`, nfo: `${folder}/${name}.nfo`, relative: `${name}/${name}.mkv`, output: `${workflow.output}/${name}/${name}.mkv` };
}

const filesOf = async (world, workflow) => (await world.weir.get(`/processing/files?limit=1000&library_id=${workflow.id}`)).files;
const activity = async (world, eventType) => (await world.weir.get(`/activity/recent?limit=100&event_type=${eventType}`)).items;

async function remuxJobCount(world) {
  const statuses = REMUX_JOB_STATUSES.map((status) => `status=${status}`).join("&");
  const { jobs } = await world.weir.get(`/processing/jobs/inspection?limit=100&${statuses}`);
  return jobs.filter((job) => job.job_kind.startsWith("processing.file.remux_pass")).length;
}

/** Drops a release into the workflow's watched folder, asks Weir to look, and waits for the film to be cleaned. */
async function dropAndClean(world, workflow, name) {
  const release = releaseIn(workflow, name);
  await world.box.dropRelease({ film: (await masterFilm(world)).path, folder: release.folder, name });
  const began = Date.now();
  const look = await world.weir.write("POST", "/processing/jobs/watched-folder-remux-scan-dispatch/enqueue", {
    media_scope: "movie",
    library_id: workflow.id,
    enqueue_remux_jobs: true,
  });
  assert.equal(look.status, 200, `Asking Weir to look at the watched folder was answered ${look.status}: ${look.text}`);
  const row = await wait(
    world,
    async () => (await filesOf(world, workflow)).find((file) => file.relative_path === release.relative && file.status === "processed"),
    `Weir to clean ${release.relative}`,
    180_000,
  );
  return { release, row, seconds: Math.round((Date.now() - began) / 1000) };
}

/** The cleaned film is where it should be and is what a cleaned film must be. */
async function expectCleaned(world, outputPath) {
  assert.ok(await world.box.exists(outputPath), `the cleaned film is not at ${outputPath}`);
  const cleaned = await world.box.probe(outputPath);
  const problems = cleanedFilmProblems((await masterFilm(world)).summary, cleaned);
  assert.deepEqual(problems, [], `the cleaned film is wrong: ${problems.join("; ")}`);
  return cleaned;
}

async function expectOriginalsKept(world, release) {
  assert.ok(await world.box.exists(release.film), `the original film is still where it was (${release.film})`);
  assert.ok(await world.box.exists(release.nfo), `its .nfo is still beside it (${release.nfo})`);
  assert.equal(await world.box.sha256(release.film), (await masterFilm(world)).sha256, "the original film is byte-for-byte what was dropped in");
}

/** weir-only-film: a film with three audio languages and two subtitle tracks, dropped into a Weir-only workflow, is cleaned. */
async function weirOnlyFilm(world) {
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
async function processAgain(world) {
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

/** The scenarios ported to run against the Docker image, by catalogue id. */
export const SCENARIOS = { "weir-only-film": weirOnlyFilm, "process-again": processAgain };
