// A movie workflow with no manager, a release dropped into its watched folder, and the waits on what Weir does with it, for the scenarios
// that run their own server over folders on real volumes and shares.
import assert from "node:assert/strict";
import { copyFile, mkdir } from "node:fs/promises";
import { join } from "node:path";

import { wait } from "./files-world.mjs";

const LOOK = "/processing/jobs/watched-folder-remux-scan-dispatch/enqueue";
const DONE = ["processed", "passed_through"];
export const OUTPUT_DRIVE_WAIT = "Waiting: the output drive has less than";

/** The rows of a workflow's files, as the Files screen lists them. */
export const filesOf = async (weir, workflow) => (await weir.get(`/processing/files?limit=1000&library_id=${workflow.id}`)).files;
export const rowFor = async (weir, workflow, relative) => (await filesOf(weir, workflow)).find((row) => row.relative_path === relative);
export const isDone = (row) => Boolean(row) && DONE.includes(row.status);

/** A movie workflow with no manager over the folders given, keeping its originals so a scenario can compare them. */
export async function makeWorkflow(world, weir, { name, watched, work, output, minimumFreeMb = 0, skipAccessTests = true }) {
  // The folders exist before the workflow does, as a person makes them; a share's folders are made on the share.
  await world.machine.ensureFolders(watched, work, output);
  const created = await weir.createLibrary({
    name: `${name} ${world.runId}`,
    media_type: "movie",
    watched_folder: watched,
    work_folder: work,
    output_folder: output,
    ready_after_seconds: 0,
    min_file_size_mb: 1,
    skip_access_tests: skipAccessTests,
    remove_original_after_success: false,
    retry_backoff_seconds: 1,
    minimum_free_disk_space_mb: minimumFreeMb,
  });
  return { id: created.id, name: created.name, watched, work, output, minimumFreeMb };
}

/** Asks Weir to look at a workflow's watched folder and queue what it finds. */
export async function askToLook(weir, workflow) {
  const look = await weir.write("POST", LOOK, { media_scope: "movie", library_id: workflow.id, enqueue_remux_jobs: true });
  assert.equal(look.status, 200, `asking Weir to look at the watched folder was answered ${look.status}: ${look.text}`);
}

/** Puts a copy of a film in the watched folder as a release of its own, and describes where it is and where its output will be. */
export async function placeRelease(workflow, name, source) {
  const folder = join(workflow.watched, name);
  const film = join(folder, `${name}.mkv`);
  await mkdir(folder, { recursive: true });
  await copyFile(source, film);
  return { name, film, relative: `${name}/${name}.mkv`, output: join(workflow.output, name, `${name}.mkv`) };
}

/** Drops a release and asks Weir to look at it. */
export async function dropRelease(world, weir, workflow, name, source) {
  const release = await placeRelease(workflow, name, source);
  await askToLook(weir, workflow);
  return release;
}

export async function waitDone(world, weir, workflow, release) {
  try {
    return await wait(world, async () => {
      const row = await rowFor(weir, workflow, release.relative);
      return isDone(row) ? row : undefined;
    }, `Weir to finish ${release.relative}`, 240_000);
  } catch (error) {
    // What Weir says about the file is the first thing a person would look at.
    const row = await rowFor(weir, workflow, release.relative).catch(() => undefined);
    const activity = await weir.get("/activity/recent?limit=5").then((found) => found.items.map((item) => item.title), () => []);
    throw new Error(`${error.message} The file reads: ${JSON.stringify(row ? { status: row.status, reason: row.status_reason } : null)}. Activity: ${JSON.stringify(activity)}`, { cause: error });
  }
}

/** Waits until the file is on hold for want of room, and returns its row. */
export const waitWaitingForRoom = (world, weir, workflow, release) =>
  wait(world, async () => {
    const row = await rowFor(weir, workflow, release.relative);
    return row && String(row.status_reason ?? "").includes(OUTPUT_DRIVE_WAIT) ? row : undefined;
  }, `${release.relative} to be held for want of room`, 240_000);

/** How many of Weir's looks at its watched folders have ended, whether they found their folders or not. */
export async function finishedLooks(weir) {
  const { jobs } = await weir.get("/processing/jobs/inspection?limit=100&status=completed&status=failed");
  return jobs.filter((job) => job.job_kind.startsWith("processing.watched_folder.remux_scan_dispatch")).length;
}

/** Asks for a look and waits for it to finish, so what it did (or left alone) can be judged. */
export async function lookAndWait(world, weir, workflow) {
  const before = await finishedLooks(weir);
  await askToLook(weir, workflow);
  await wait(world, async () => ((await finishedLooks(weir)) > before ? true : undefined), "the look at the watched folder to finish", 180_000);
}
