// What the Weir-only scenarios share: a workflow of their own over folders in the container, the film every release is a copy of, a release
// dropped into a watched folder, a look at that folder, and the judgements of a cleaned film. All of it is stateless; what a run keeps (its id,
// the film once made) lives on the world the scenarios are given.
//
// The film is made by the ffmpeg in the image and cleaned by the same ffmpeg, in folders inside the container (the bind-mounted form is the
// `media` scenario of docker-compose-smoke.mjs). The ceilings are hang guards: every wait ends the moment its condition holds.
import assert from "node:assert/strict";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { cleanedFilmProblems } from "./film.mjs";

const FILM_SECONDS = 30;
export const MASTER_FILM = "Big.Buck.Bunny.2008";
const LOOK = "/processing/jobs/watched-folder-remux-scan-dispatch/enqueue";
const ALL_JOB_STATUSES = ["pending", "leased", "completed", "failed", "handler_ok_finalize_failed", "cancelled"];

export const root = (world) => `/tmp/weir-scenarios/${world.runId}`;
export const wait = (world, check, what, ceilingMs, intervalMs = 1000) => pollUntil(check, { what, ceilingMs, intervalMs, clock: world.clock });

/** The film every release in the run is a copy of, made once. */
export async function masterFilm(world) {
  if (!world.film) {
    const path = `${root(world)}/source/${MASTER_FILM}.mkv`;
    await world.box.makeFilm({ path, seconds: FILM_SECONDS });
    world.film = { path, summary: await world.box.probe(path), sha256: await world.box.sha256(path) };
  }
  return world.film;
}

/** A movie workflow with no manager over folders of its own, keeping its originals so the scenario can see them. */
export async function makeWorkflow(world, { name, folder, minimumMegabytes = 1, readyAfterSeconds = 0 }) {
  const base = `${root(world)}/${folder}`;
  const folders = { watched: `${base}/watched`, work: `${base}/work`, output: `${base}/output` };
  await world.box.ensureFolders(...Object.values(folders));
  const created = await world.weir.createLibrary({
    name: `${name} ${world.runId}`,
    media_type: "movie",
    watched_folder: folders.watched,
    work_folder: folders.work,
    output_folder: folders.output,
    ready_after_seconds: readyAfterSeconds,
    min_file_size_mb: minimumMegabytes,
    skip_access_tests: true,
    remove_original_after_success: false,
    retry_backoff_seconds: 1,
    minimum_free_disk_space_mb: 0,
  });
  return { id: created.id, ...folders };
}

export function releaseIn(workflow, name) {
  const folder = `${workflow.watched}/${name}`;
  return { name, folder, film: `${folder}/${name}.mkv`, nfo: `${folder}/${name}.nfo`, relative: `${name}/${name}.mkv`, output: `${workflow.output}/${name}/${name}.mkv` };
}

export const filesOf = async (world, workflow) => (await world.weir.get(`/processing/files?limit=1000&library_id=${workflow.id}`)).files;
export const activity = async (world, eventType) => (await world.weir.get(`/activity/recent?limit=100&event_type=${eventType}`)).items;

async function jobsOfKind(world, kindPrefix, statuses) {
  const { jobs } = await world.weir.get(`/processing/jobs/inspection?limit=100&${statuses.map((status) => `status=${status}`).join("&")}`);
  return jobs.filter((job) => job.job_kind.startsWith(kindPrefix));
}

/** The cleaning passes there are, in any state, or only in the given ones. */
export async function remuxJobCount(world, statuses = ALL_JOB_STATUSES) {
  return (await jobsOfKind(world, "processing.file.remux_pass", statuses)).length;
}

/** The passes Weir has started: one that is running or has ended, not one only waiting. */
export const startedRemuxJobCount = (world) => remuxJobCount(world, ["leased", "completed", "failed"]);

/** How many of Weir's looks at its watched folders have finished: each one asked for, or made by the schedule, adds one when it is done. */
export async function finishedLookCount(world) {
  return (await jobsOfKind(world, "processing.watched_folder.remux_scan_dispatch", ["completed"])).length;
}

/** Asks Weir to look at a workflow's watched folder, and to queue what it finds when `queueFiles` (as a periodic scan does). */
export async function startLook(world, workflow, { queueFiles = false } = {}) {
  const look = await world.weir.write("POST", LOOK, { media_scope: "movie", library_id: workflow.id, enqueue_remux_jobs: queueFiles });
  assert.equal(look.status, 200, `Asking Weir to look at the watched folder was answered ${look.status}: ${look.text}`);
}

/** Asks for a look and waits for it to finish, so what it did (or left alone) can be judged. */
export async function lookAndWait(world, workflow, options) {
  const before = await finishedLookCount(world);
  await startLook(world, workflow, options);
  await wait(world, async () => ((await finishedLookCount(world)) > before ? true : undefined), "the look at the watched folder to finish", 60_000);
}

/** Drops a release into the workflow's watched folder, asks Weir to look, and waits for the film to be cleaned. */
export async function dropAndClean(world, workflow, name) {
  const release = releaseIn(workflow, name);
  await world.box.dropRelease({ film: (await masterFilm(world)).path, folder: release.folder, name });
  const began = Date.now();
  await startLook(world, workflow, { queueFiles: true });
  const row = await waitForProcessed(world, workflow, release, `Weir to clean ${release.relative}`);
  return { release, row, seconds: Math.round((Date.now() - began) / 1000) };
}

export function waitForProcessed(world, workflow, release, what) {
  return wait(world, async () => (await filesOf(world, workflow)).find((file) => file.relative_path === release.relative && file.status === "processed"), what, 180_000);
}

/** The cleaned film is where it should be and is what a cleaned film must be. */
export async function expectCleaned(world, outputPath) {
  assert.ok(await world.box.exists(outputPath), `the cleaned film is not at ${outputPath}`);
  const cleaned = await world.box.probe(outputPath);
  const problems = cleanedFilmProblems((await masterFilm(world)).summary, cleaned);
  assert.deepEqual(problems, [], `the cleaned film is wrong: ${problems.join("; ")}`);
  return cleaned;
}

export async function expectOriginalsKept(world, release) {
  assert.ok(await world.box.exists(release.film), `the original film is still where it was (${release.film})`);
  assert.ok(await world.box.exists(release.nfo), `its .nfo is still beside it (${release.nfo})`);
  assert.equal(await world.box.sha256(release.film), (await masterFilm(world)).sha256, "the original film is byte-for-byte what was dropped in");
}
