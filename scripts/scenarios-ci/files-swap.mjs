// library-clean-in-place and library-swap-crash: a real film in a real library folder on NTFS, cleaned in place by the installed server with
// the ffmpeg the package carries, and then the server ended by its process id at each point of the swap and started again. Windows cannot
// replace a file that another program holds open, so what a held file does is read here and not on Linux.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, rm } from "node:fs/promises";
import { dirname, join } from "node:path";

import { cleanedFilmProblems } from "./film.mjs";
import { exists, makeFilm, probe, sha256, treeOf } from "./files-media.mjs";
import { FILM_PATH, KEPT_BESIDE_PATH, POINTS, commits, filesAfterRecovery, filesWhenStopped } from "./files-swap-plan.mjs";
import { expectCleanLogs, filesWorld, makeLibrary, newServer, requestClean, rowOf, scanUntil, wait } from "./files-world.mjs";

const HELD_FILE = "swap-held-at.txt";
const HOLD_SETTING = "WEIR_LIBRARY_SWAP_HOLD_AT";
const FILM_SECONDS = 30;
// The points the kept original is also followed through: before the commit, at it, and after it.
const KEEPING_POINTS = ["CommitRecorded", "CommittedRecorded", "FinishedRecorded"];

/** The film every library holds a copy of, made once by the package's own ffmpeg. */
export async function masterFilm(world) {
  if (world.master) return world.master;
  const { tools } = await filesWorld(world);
  const path = join(world.scratch, "master", "Film (2020).mkv");
  await makeFilm(tools, { path, seconds: FILM_SECONDS });
  world.master = { path, summary: await probe(tools, path), sha256: await sha256(path) };
  return world.master;
}

/** A copy of the film at this path, made as a person's library holds it. */
export async function placeFilm(world, path) {
  const master = await masterFilm(world);
  await mkdir(dirname(path), { recursive: true });
  await copyFile(master.path, path);
  return master;
}

/** The film at the path is what a cleaned film must be: the English audio only, as long, and smaller. Returns its summary. `original` is the film it was cleaned from, the master film unless given. */
export async function expectCleanedFilm(world, path, original) {
  const { tools } = await filesWorld(world);
  const master = original ?? (await masterFilm(world));
  assert.ok(await exists(path), `the cleaned film is not at ${path}`);
  const cleaned = await probe(tools, path);
  const problems = cleanedFilmProblems(master.summary, cleaned);
  assert.deepEqual(problems, [], `the film at ${path} is wrong: ${problems.join("; ")}`);
  return cleaned;
}

/** Waits until the library lists the film as matching its rules, and, unless it was already cleaned when the server stopped, as cleaned by Weir. */
async function waitCleaned(world, weir, library, path, { markedCleaned = true } = {}) {
  return wait(world, async () => {
    const row = await rowOf(weir, library, path);
    return row?.status === "matches" && (row.cleaned_at || !markedCleaned) ? row : undefined;
  }, `${path} to be cleaned`, 180_000);
}

// --- library-clean-in-place -------------------------------------------------------------------------------------------------------

/** A program that plays the file: it reads it and lets others read it, but nothing may rename or replace it until it is told to let go (its standard input closes). */
function holdOpen(path) {
  const script = `$f = [IO.File]::Open($env:SCENARIO_HELD, 'Open', 'Read', 'Read'); [Console]::Out.WriteLine('held'); [Console]::Out.Flush(); [Console]::In.ReadToEnd() | Out-Null; $f.Close()`;
  const child = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], { env: { ...process.env, SCENARIO_HELD: path }, stdio: ["pipe", "pipe", "inherit"], windowsHide: true });
  const held = new Promise((resolve, reject) => {
    child.stdout.on("data", (chunk) => String(chunk).includes("held") && resolve());
    child.on("exit", () => reject(new Error("the program that holds the file ended before it held it")));
  });
  return { held, release: () => new Promise((done) => (child.on("exit", done), child.stdin.end())) };
}

/** library-clean-in-place: Clean replaces the file at its own path, with or without the original kept; a file in use is left alone and tried later. */
export async function libraryCleanInPlace(world) {
  const server = await newServer(world, "clean");
  await server.start();
  await server.waitReady();
  const weir = await server.signIn();
  const base = join(world.scratch, "clean");
  const facts = [];

  // The film is cleaned in the path it has, and nothing else is left in the library.
  const plain = join(base, "plain", "library");
  const plainFilm = join(plain, FILM_PATH);
  await placeFilm(world, plainFilm);
  const library = await makeLibrary(world, weir, { name: `Scenario clean ${world.runId}`, base: join(base, "plain"), libraryFolder: plain });
  await scanUntil(world, weir, library, plainFilm, "needs_cleaning");
  await requestClean(weir, library, plainFilm);
  await waitCleaned(world, weir, library, plainFilm);
  const cleaned = await expectCleanedFilm(world, plainFilm);
  assert.deepEqual(await treeOf(plain), [FILM_PATH], "the library holds more than the cleaned film (a temporary copy, a backup or an original is left)");
  facts.push(`the film was cleaned at its own path to ${cleaned.bytes} bytes with audio ${cleaned.audio.join("/")}, and nothing but it is left in the library`);

  // With "keep the original", the original is under .weir-originals and is the film as it was.
  const keeping = join(base, "keep", "library");
  const keepingFilm = join(keeping, FILM_PATH);
  const master = await placeFilm(world, keepingFilm);
  const keepLibrary = await makeLibrary(world, weir, { name: `Scenario keep ${world.runId}`, base: join(base, "keep"), libraryFolder: keeping, keepOriginal: true });
  await scanUntil(world, weir, keepLibrary, keepingFilm, "needs_cleaning");
  await requestClean(weir, keepLibrary, keepingFilm);
  await waitCleaned(world, weir, keepLibrary, keepingFilm);
  await expectCleanedFilm(world, keepingFilm);
  assert.deepEqual(await treeOf(keeping), [FILM_PATH, KEPT_BESIDE_PATH].sort(), "the library should hold the cleaned film and the kept original, and nothing else");
  assert.equal(await sha256(join(keeping, KEPT_BESIDE_PATH)), master.sha256, "the kept original is not the film as it was");
  facts.push("with the original kept, it is under .weir-originals byte for byte as it was");

  // A file another program holds open cannot be replaced on Windows: Weir leaves it exactly as it is and books another look.
  const busy = join(base, "busy", "library");
  const busyFilm = join(busy, FILM_PATH);
  await placeFilm(world, busyFilm);
  const busyLibrary = await makeLibrary(world, weir, { name: `Scenario busy ${world.runId}`, base: join(base, "busy"), libraryFolder: busy });
  await scanUntil(world, weir, busyLibrary, busyFilm, "needs_cleaning");
  const holder = holdOpen(busyFilm);
  try {
    await holder.held;
    await requestClean(weir, busyLibrary, busyFilm);
    await wait(world, async () => (/Library (clean|swap) postponed.*in use/.test(await server.console()) ? true : undefined), "Weir to say the file is in use", 120_000);
  } finally {
    await holder.release();
  }
  assert.equal(await sha256(busyFilm), master.sha256, "the film that was in use was changed");
  assert.deepEqual(await treeOf(busy), [FILM_PATH], "Weir left a temporary copy or a backup beside the film that was in use");
  const job = (await weir.get("/processing/jobs/inspection?limit=100&status=pending&status=leased")).jobs.find((entry) => entry.job_kind === "processing.library.clean.v1");
  assert.ok(job, "the clean of the film that was in use is not booked to be tried again");
  facts.push("a film held open by another program was left byte for byte as it was, with nothing beside it, and its clean is booked for a later look");

  await expectCleanLogs(world, server);
  await server.kill();
  return `${facts.join("; ")}.`;
}

// --- library-swap-crash -----------------------------------------------------------------------------------------------------------

async function waitHeldAt(world, server, point) {
  const file = join(server.home, HELD_FILE);
  return wait(world, async () => {
    try {
      const held = (await readFile(file, "utf8")).trim();
      return held.length > 0 ? held : undefined;
    } catch {
      return undefined;
    }
  }, `the swap to stop at ${point}`, 180_000, 250);
}

const treesOf = async (library, elsewhere) => ({ library: await treeOf(library), elsewhere: elsewhere ? await treeOf(elsewhere) : [] });

/**
 * One crash: the server is ended by its process id with the swap held at `point`, started again with no workers so only start-up recovery has
 * touched anything, and then started as it runs on a real install so the interrupted clean finishes. Returns what it showed, in words.
 * `elsewhere` is the originals folder when the original is kept on another volume.
 */
export async function crashAt(world, point, { keepOriginal, name, libraryFolder, elsewhere }) {
  const keptElsewhere = Boolean(elsewhere);
  expectRecoveryRows(world);
  const server = await newServer(world, name);
  await server.start({ environment: "development", extra: { [HOLD_SETTING]: point } });
  await server.waitReady();
  const weir = await server.signIn();
  const film = join(libraryFolder, FILM_PATH);
  const master = await placeFilm(world, film);
  const library = await makeLibrary(world, weir, {
    name: `Scenario swap ${point} ${world.runId}`,
    base: join(world.scratch, name),
    libraryFolder,
    keepOriginal,
    originalsFolder: elsewhere,
  });
  await scanUntil(world, weir, library, film, "needs_cleaning");
  await requestClean(weir, library, film);
  assert.equal(await waitHeldAt(world, server, point), point, `the swap stopped somewhere other than ${point}`);

  const pid = server.pid;
  await server.kill();
  assert.deepEqual(await treesOf(libraryFolder, elsewhere), filesWhenStopped(point, { keepOriginal, keptElsewhere }), `the files when the server was ended at ${point}`);
  const stoppedSha = (await exists(film)) ? await sha256(film) : null;

  // Start again with no workers: only the start-up recovery has touched anything when the files are looked at.
  await rm(join(server.home, HELD_FILE), { force: true });
  await server.start({ environment: "development", workers: 0 });
  await server.waitReady();
  assert.deepEqual(await treesOf(libraryFolder, elsewhere), filesAfterRecovery(point, { keepOriginal, keptElsewhere }), `the files once Weir had started again after ${point}`);
  const recoveredSha = await sha256(film);
  if (commits(point)) {
    assert.equal(recoveredSha, stoppedSha, `the cleaned film changed during recovery after ${point}`);
    assert.notEqual(recoveredSha, master.sha256, `the film at ${point} is the original although the swap had committed`);
    if (keepOriginal) {
      const kept = elsewhere ? join(elsewhere, FILM_PATH) : join(libraryFolder, KEPT_BESIDE_PATH);
      assert.equal(await sha256(kept), master.sha256, `the kept original after ${point} is not the film as it was`);
    }
  } else {
    assert.equal(recoveredSha, master.sha256, `the original was not put back as it was after ${point}`);
  }
  await expectCleanedOrOriginal(world, film);
  await server.kill();

  // Started as on a real install, the clean that was interrupted finishes, and the library ends as a clean leaves it.
  await server.start({ environment: "development" });
  await server.waitReady();
  const again = await server.signInAgain();
  await waitCleaned(world, again, library, film, { markedCleaned: !commits(point) });
  await expectCleanedFilm(world, film);
  const finalPlan = filesAfterRecovery("FinishedRecorded", { keepOriginal, keptElsewhere });
  assert.deepEqual(await treesOf(libraryFolder, elsewhere), finalPlan, `the files once the clean had finished after ${point}`);
  if (keepOriginal) {
    const kept = elsewhere ? join(elsewhere, FILM_PATH) : join(libraryFolder, KEPT_BESIDE_PATH);
    assert.equal(await sha256(kept), master.sha256, `the kept original at the end of ${point} is not the film as it was`);
  }
  await expectCleanLogs(world, server);
  await server.kill();
  return `${point}${keepOriginal ? " (original kept)" : ""}: server ${pid} ended there, ${commits(point) ? "the cleaned film was left in place" : "the original was put back as it was"}, nothing was left over, and the interrupted clean finished once Weir ran normally`;
}

/** Weir tells the person, in System > Logs, what it put right when it started after the crash; those rows are what the scenario causes on purpose. */
function expectRecoveryRows(world) {
  const why = "the server was ended in the middle of a swap, and Weir says what it put right when it starts again";
  world.logExpectations.push({ pattern: /^Weir startup recovered interrupted work/, options: { times: 2, why } });
  world.logExpectations.push({ pattern: /^Library swap sweep finished/, options: { times: 2, why } });
  world.logExpectations.push({ pattern: /^Weir (restored a library file from its backup|finished a library clean|kept)/, options: { times: 2, why } });
}

/** Whichever film is at the path plays: a probe finds one video track. */
async function expectCleanedOrOriginal(world, path) {
  const { tools } = await filesWorld(world);
  const summary = await probe(tools, path);
  assert.equal(summary.video, 1, `the film at ${path} has ${summary.video} video tracks after recovery`);
  assert.ok(summary.seconds > FILM_SECONDS - 2, `the film at ${path} is only ${summary.seconds} s long after recovery`);
}

/** library-swap-crash: the server ended at every point of a library clean leaves one intact film under its name and nothing else, and finishes the job. */
export async function librarySwapCrash(world) {
  const lines = [];
  for (const point of POINTS) {
    lines.push(await crashAt(world, point, { keepOriginal: false, name: `swap-${point}`, libraryFolder: join(world.scratch, `swap-${point}`, "library") }));
  }
  for (const point of KEEPING_POINTS) {
    lines.push(await crashAt(world, point, { keepOriginal: true, name: `swapkeep-${point}`, libraryFolder: join(world.scratch, `swapkeep-${point}`, "library") }));
  }
  return `${POINTS.length + KEEPING_POINTS.length} crashes of a real server on NTFS: ${lines.join("; ")}.`;
}
