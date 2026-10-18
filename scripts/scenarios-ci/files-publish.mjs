// publish-across-volumes, keep-original-across-volumes and full-disk: Weir's file lifecycle between two real NTFS volumes (a virtual disk made
// for the scenario) and on a small one that really fills. The server is the installed one, the media is made and cleaned by the ffmpeg the
// package carries, and the volumes are what a person's work folder, watched folder, library and output drives are.
import assert from "node:assert/strict";
import { watch } from "node:fs";
import { mkdir, rm } from "node:fs/promises";
import { basename, dirname, join } from "node:path";

import { cleanedFilmProblems } from "./film.mjs";
import { exists, makeFilm, probe, sha256, sizeOf, treeOf } from "./files-media.mjs";
import { crashAt, expectCleanedFilm, masterFilm, placeFilm } from "./files-swap.mjs";
import { FILM_PATH } from "./files-swap-plan.mjs";
import { expectCleanLogs, filesWorld, makeLibrary, newServer, requestClean, scanUntil, wait } from "./files-world.mjs";
import { megabytes } from "./files-volumes.mjs";
import { dropRelease, isDone, makeWorkflow, waitDone, waitWaitingForRoom } from "./files-workflow.mjs";

const BIG_VOLUME_MB = 128;
const SMALL_VOLUME_MB = 160;

/** Records every change to the output folder, and what the file at the final name looks like at the moment of each. */
function watchFinalName(folder, finalPath) {
  const seen = [];
  const watcher = watch(folder, { recursive: true }, (eventType, name) => {
    if (!name) return;
    const path = join(folder, name);
    if (path.toLowerCase() === finalPath.toLowerCase()) {
      sizeOf(path).then(
        (bytes) => seen.push({ eventType, name, bytes }),
        () => seen.push({ eventType, name, bytes: null }),
      );
    } else seen.push({ eventType, name, hidden: basename(name).startsWith(".") });
  });
  return { seen, stop: () => watcher.close() };
}

// --- publish-across-volumes -----------------------------------------------------------------------------------------------------

/**
 * publish-across-volumes: with the watched folder and work folder on C: and the output on another real volume, a cleaned film appears at its
 * final name only when complete, and an unchanged film (whose hard link the other volume refuses) is copied, checked, and leaves no .link behind.
 */
export async function publishAcrossVolumes(world) {
  const { volumes, tools } = await filesWorld(world);
  const volume = await volumes.create({ sizeMb: BIG_VOLUME_MB });
  const server = await newServer(world, "publish");
  await server.start();
  await server.waitReady();
  const weir = await server.signIn();
  const base = join(world.scratch, "publish");
  const workflow = await makeWorkflow(world, weir, { name: "Scenario publish", watched: join(base, "watched"), work: join(base, "work"), output: volume.path("output") });
  const master = await masterFilm(world);
  const facts = [];

  // A film with tracks to remove is written to the work folder on C: and published onto the other volume.
  const finalFilm = join(workflow.output, "Cross.Volume.Film.2019", "Cross.Volume.Film.2019.mkv");
  await mkdir(dirname(finalFilm), { recursive: true });
  const watching = watchFinalName(workflow.output, finalFilm);
  let cleaned;
  try {
    const release = await dropRelease(world, weir, workflow, "Cross.Volume.Film.2019", master.path);
    await waitDone(world, weir, workflow, release);
    cleaned = await probe(tools, release.output);
  } finally {
    watching.stop();
  }
  assert.deepEqual(cleanedFilmProblems(master.summary, cleaned), [], "the film published on the other volume is not a cleaned film");
  const finalSize = await sizeOf(finalFilm);
  const atFinalName = watching.seen.filter((event) => event.name !== undefined && join(workflow.output, event.name).toLowerCase() === finalFilm.toLowerCase());
  assert.ok(atFinalName.length > 0, "the watcher on the output folder never saw the film appear at its final name");
  for (const event of atFinalName) assert.equal(event.bytes, finalSize, `the film was seen at its final name with ${event.bytes} bytes, not whole (${finalSize}); event ${event.eventType}`);
  assert.deepEqual(await treeOf(workflow.output), ["Cross.Volume.Film.2019/Cross.Volume.Film.2019.mkv"], "the output folder holds more than the film (a partial copy is left)");
  assert.deepEqual(await treeOf(workflow.work), [], "a staged copy is left in the work folder");
  facts.push(`a cleaned film written on C: was published onto ${volume.root} and seen at its final name ${atFinalName.length} time(s), always whole (${finalSize} bytes), with no partial copy left`);

  // A film that already matches the rules is not remuxed: the other volume refuses a hard link, so Weir copies it and checks the copy.
  const plain = join(base, "source", "Plain.Film.2020.mkv");
  await makeFilm(tools, { path: plain, seconds: 10, audio: ["eng"], defaultAudio: true });
  const plainRelease = await dropRelease(world, weir, workflow, "Plain.Film.2020", plain);
  await waitDone(world, weir, workflow, plainRelease);
  assert.equal(await sha256(plainRelease.output), await sha256(plain), "the unchanged film on the other volume is not the film that was dropped");
  assert.equal(await sha256(plainRelease.film), await sha256(plain), "the unchanged film's source in the watched folder changed");
  const left = (await treeOf(workflow.output)).filter((file) => file.endsWith(".link") || file.endsWith(".partial"));
  assert.deepEqual(left, [], "a hidden link or partial copy is left in the output folder");
  facts.push("an unchanged film was copied onto the other volume where a hard link is refused, byte for byte, with no .link or .partial left");

  await expectCleanLogs(world, server);
  await server.kill();
  return `${facts.join("; ")}.`;
}

// --- keep-original-across-volumes -------------------------------------------------------------------------------------------------

/** keep-original-across-volumes: the original is copied to an originals folder on another volume, checked, then deleted; a crash in the middle loses neither file. */
export async function keepOriginalAcrossVolumes(world) {
  const { volumes } = await filesWorld(world);
  const volume = await volumes.create({ sizeMb: BIG_VOLUME_MB });
  const master = await masterFilm(world);
  const lines = [];

  // A clean that is not interrupted.
  const server = await newServer(world, "keepacross");
  await server.start();
  await server.waitReady();
  const weir = await server.signIn();
  const folder = join(world.scratch, "keepacross", "library");
  const originals = volume.path("originals-straight");
  const film = join(folder, FILM_PATH);
  await placeFilm(world, film);
  const library = await makeLibrary(world, weir, { name: `Scenario keep across ${world.runId}`, base: join(world.scratch, "keepacross"), libraryFolder: folder, keepOriginal: true, originalsFolder: originals });
  await scanUntil(world, weir, library, film, "needs_cleaning");
  await requestClean(weir, library, film);
  await wait(world, async () => ((await exists(join(originals, FILM_PATH))) && !(await exists(join(folder, "Film (2020)", "Film (2020).weir-bak.mkv"))) ? true : undefined), "the original to arrive on the other volume", 180_000);
  await expectCleanedFilm(world, film);
  assert.equal(await sha256(join(originals, FILM_PATH)), master.sha256, "the original kept on the other volume is not the film as it was");
  assert.deepEqual(await treeOf(folder), [FILM_PATH], "the library holds more than the cleaned film");
  await expectCleanLogs(world, server);
  await server.kill();
  lines.push(`a clean left the original on ${volume.root} byte for byte as it was, and only the cleaned film in the library`);

  // The server ended between the steps of the copy, the check and the delete.
  for (const point of ["CommitRecorded", "OriginalMovedAside", "CommittedRecorded", "BackupDealtWith"]) {
    lines.push(
      await crashAt(world, point, {
        keepOriginal: true,
        name: `keepacross-${point}`,
        libraryFolder: join(world.scratch, `keepacross-${point}`, "library"),
        elsewhere: volume.path(`originals-${point}`),
      }),
    );
  }
  return `${lines.join("; ")}.`;
}

// --- full-disk --------------------------------------------------------------------------------------------------------------------

/**
 * full-disk: on a small volume that really fills, a pass waits for room and writes nothing, records nothing as done and leaves nothing behind;
 * the same for an unchanged film (the unchanged-publish check); and when room comes back both complete.
 */
export async function fullDisk(world) {
  const { volumes, tools } = await filesWorld(world);
  const volume = await volumes.create({ sizeMb: SMALL_VOLUME_MB });
  const server = await newServer(world, "fulldisk");
  await server.start();
  await server.waitReady();
  const weir = await server.signIn();
  const base = join(world.scratch, "fulldisk");

  const big = join(base, "source", "Full.Disk.Film.2020.mkv");
  await makeFilm(tools, { path: big, seconds: 24, videoBitrate: "6M" });
  const bigBytes = await sizeOf(big);
  const bigFilm = { summary: await probe(tools, big) };
  const MEGABYTE = 1024 * 1024;
  assert.ok(bigBytes > 8 * MEGABYTE && bigBytes < 40 * MEGABYTE, `the film for the full disk is ${bigBytes} bytes, not between 8 and 40 MB`);
  const plain = join(base, "source", "Plain.Film.2021.mkv");
  await makeFilm(tools, { path: plain, seconds: 10, audio: ["eng"], defaultAudio: true });

  // The work folder and the output are on the small volume. The workflow keeps most of the film's size free: there is room to start, and the
  // staged copy of the film takes the room away.
  const minimumFreeMb = Math.ceil(megabytes(bigBytes) * 0.9);
  const workflow = await makeWorkflow(world, weir, { name: "Scenario full disk", watched: join(base, "watched"), work: volume.path("work"), output: volume.path("output"), minimumFreeMb });
  const ballast = await volume.leaveFree(Math.round(bigBytes * 1.5));
  const facts = [];

  const held = await dropRelease(world, weir, workflow, "Full.Disk.Film.2020", big);
  const row = await waitWaitingForRoom(world, weir, workflow, held);
  assert.ok(!isDone(row), `the film is recorded as ${row.status} although the drive filled`);
  assert.deepEqual(await treeOf(workflow.output), [], "something was published although the drive was full");
  assert.deepEqual(await treeOf(workflow.work), [], "the staged copy was left in the work folder");
  assert.equal(await sha256(held.film), await sha256(big), "the film in the watched folder changed");
  facts.push(`a ${Math.round(megabytes(bigBytes))} MB film that filled the drive during the pass was held ("${String(row.status_reason).slice(0, 60)}..."), nothing was published, no staged copy was left and the original was untouched`);

  await rm(ballast);
  const again = await weir.write("POST", `/processing/files/${row.id}/requeue`, {});
  assert.equal(again.status, 200, `Process again was answered ${again.status}: ${again.text}`);
  await waitDone(world, weir, workflow, held);
  await expectCleanedFilm(world, held.output, bigFilm);
  assert.deepEqual(await treeOf(volume.root), ["output/Full.Disk.Film.2020/Full.Disk.Film.2020.mkv"], "the small volume holds more than the published film once the pass is done");
  facts.push("with room back, Process again cleaned and published it");

  // An unchanged film is checked for room before it is copied.
  const leftover = await volume.leaveFree(Math.round(bigBytes * 0.5), "ballast-2.bin");
  const unchanged = await dropRelease(world, weir, workflow, "Plain.Film.2021", plain);
  const plainRow = await waitWaitingForRoom(world, weir, workflow, unchanged);
  assert.ok(!isDone(plainRow), `the unchanged film is recorded as ${plainRow.status} although the drive is full`);
  assert.equal(await exists(unchanged.output), false, "an unchanged film was copied onto a drive without room");
  assert.deepEqual((await treeOf(volume.root)).filter((file) => file.endsWith(".link") || file.endsWith(".partial")), [], "a partial copy is left on the full drive");
  await rm(leftover);
  const second = await weir.write("POST", `/processing/files/${plainRow.id}/requeue`, {});
  assert.equal(second.status, 200, `Process again was answered ${second.status}: ${second.text}`);
  await waitDone(world, weir, workflow, unchanged);
  assert.equal(await sha256(unchanged.output), await sha256(plain), "the unchanged film copied once room came back is not the film that was dropped");
  facts.push("an unchanged film was held the same way, copied nothing onto the full drive, and was copied whole once room came back");

  await expectCleanLogs(world, server);
  await server.kill();
  return `${facts.join("; ")}.`;
}
