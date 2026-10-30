// node --test scripts/scenarios-in-ci-stand-ins-last.test.mjs
// The last branches of the stand-ins for Weir, a Windows runner and a fresh Weir, and of the small helpers of the scenarios that run in CI
// (#1060): what a stand-in refuses, what it does when asked at an odd moment, and the waits that see a stale answer first.
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { FakeFreshWorld } from "./scenarios-ci/fake-fresh-world.mjs";
import { FakeWindows } from "./scenarios-ci/fake-windows.mjs";
import { FakeWorld } from "./scenarios-ci/fake-world.mjs";
import { catalogIds, loadHomes, problemsWithHomes } from "./scenarios-ci/homes.mjs";
import { cleanedFilmProblems, filmArguments } from "./scenarios-ci/film.mjs";
import { processAgain } from "./scenarios-ci/film-scenarios.mjs";
import { problemsWithImages } from "./scenarios-ci/images.mjs";
import { runScenarios } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";
import { UsageError, main } from "./scenarios-in-ci.mjs";
import { differencesInEntries, zipEntries } from "./scenarios-ci/zip-entries.mjs";
import { lookAndWait, makeWorkflow } from "./scenarios-ci/workflow-support.mjs";

// --- a Windows runner -----------------------------------------------------------------------------------------------------------------

test("a runner with nothing installed has no processes, no files and no logs, and uninstalling it writes the first line of the tray's log", async () => {
  const stand = new FakeWindows();
  const { machine, paths } = stand;
  assert.deepEqual(await machine.processes(), []);
  assert.deepEqual(await machine.listeningAddresses(), []);
  assert.equal(await machine.readText(join(paths.runtimeHome, "missing.txt")), "");
  assert.equal(await machine.serverLogs(), "");
  await machine.uninstall();
  assert.equal(await machine.readText(paths.trayLog), "Velopack: before uninstall\n");
});

test("a server that is not running answers nothing, a wrong sign-in is a 401, and a route the stand-in does not have is refused", async () => {
  const stand = new FakeWindows();
  await assert.rejects(() => stand.browser().bootstrapStatus(), /connection refused/);
  stand.machine.startTray(9347);
  const browser = stand.browser();
  await browser.bootstrap("owner", "a-password");
  await assert.rejects(() => browser.login("owner", "wrong"), /expected HTTP 200, got 401/);
  await assert.rejects(() => browser.write("GET", "/nowhere", {}), /does not take GET \/nowhere/);
  await assert.rejects(() => browser.get("/nowhere"), /does not answer \/nowhere/);
});

test("a worker that takes a job just after the job list is first read empty holds it from the next read", async () => {
  const stand = new FakeWindows({ jobStartsLate: true });
  stand.machine.startTray(9347);
  assert.deepEqual(stand.jobsInFlight(), []);
  assert.equal(stand.jobsInFlight().length, 1);
});

// --- Weir and the container -----------------------------------------------------------------------------------------------------------

test("the stand-in for Weir refuses a file it does not have, a route it does not answer and a call it does not take", async () => {
  const fake = new FakeWorld();
  await assert.rejects(() => fake.box.probe("/missing.mkv"), /There is no file \/missing\.mkv\./);
  await assert.rejects(() => fake.weir.get("/nowhere"), /no route \/nowhere/);
  await assert.rejects(() => fake.weir.write("POST", "/nowhere", {}), /no POST route \/nowhere/);
});

test("a file written that is not the tray's pause request is kept as text", async () => {
  const fake = new FakeWorld();
  await fake.box.writeFileAtomically("/tmp/note.txt", "text");
  assert.equal(await fake.box.exists("/tmp/note.txt"), true);
  assert.equal(await fake.box.sha256("/tmp/note.txt"), "text");
});

test("a second look at a folder leaves a film Weir has already cleaned as it was", async () => {
  const fake = new FakeWorld();
  const workflow = await fake.weir.createLibrary({ name: "Films", watched_folder: "/watch", output_folder: "/out" });
  await fake.box.makeFilm({ path: "/master.mkv" });
  await fake.box.dropRelease({ film: "/master.mkv", folder: "/watch/R", name: "R" });
  const look = () => fake.weir.write("POST", "/x/watched-folder-remux-scan-dispatch/enqueue", { library_id: workflow.id, enqueue_remux_jobs: true });
  await look();
  await look();
  const { files } = await fake.weir.get(`/processing/files?library_id=${workflow.id}`);
  assert.deepEqual(files.map((file) => file.status), ["processed"]);
});

test("the stand-in for a fresh Weir refuses a route it does not answer, and a second first account", async () => {
  const fresh = new FakeFreshWorld();
  await assert.rejects(() => fresh.weir.get("/nowhere"), /no route \/nowhere/);
  await fresh.createAccount();
  await assert.rejects(() => fresh.createAccount(), /must offer to create the first account/);
});

// --- the waits that see a stale answer first -----------------------------------------------------------------------------------------

/** The first `times` reads whose path starts with `prefix` are answered with `stale`; the stand-in answers the rest. */
function staleAtFirst(world, prefix, stale, times = 1) {
  const real = world.weir.get;
  let seen = 0;
  world.weir.get = async (path) => (path.startsWith(prefix) && seen++ < times ? stale : real(path));
}

test("Weir's look at a folder is waited for when its end is not yet shown", async () => {
  const world = new FakeWorld().asWorld();
  const workflow = await makeWorkflow(world, { name: "Looks", folder: "looks" });
  staleAtFirst(world, "/processing/jobs/inspection", { jobs: [] }, 2);
  await lookAndWait(world, workflow);
});

test("Process again waits for the Skipped line in Activity when it is not yet there", async () => {
  const world = new FakeWorld().asWorld();
  const stale = { items: [] };
  const real = world.weir.get;
  let skipReads = 0;
  world.weir.get = async (path) => (path.includes("event_type=processing.file_skipped_repeat") && ++skipReads === 2 ? stale : real(path));
  const detail = await processAgain(world);
  assert.match(detail, /Skipped: already done/);
  assert.ok(skipReads >= 3);
});

// --- the small helpers ----------------------------------------------------------------------------------------------------------------

test("a film can be made with its first audio track as the default one", () => {
  const args = filmArguments({ path: "film.mkv", defaultAudio: true });
  assert.deepEqual(args.slice(args.indexOf("-disposition:a:0"), args.indexOf("-disposition:a:0") + 2), ["-disposition:a:0", "default"]);
  assert.equal(filmArguments({ path: "film.mkv" }).includes("-disposition:a:0"), false);
});

test("a cleaned film with no audio left is reported as keeping none", () => {
  const original = { video: 1, audio: ["eng", "fre"], subtitles: [], seconds: 30, bytes: 10 };
  const problems = cleanedFilmProblems(original, { video: 1, audio: [], subtitles: [], seconds: 30, bytes: 5 });
  assert.deepEqual(problems, ["only the English audio should be kept (kept: none; it had eng,fre)"]);
});

test("a pinned image with no reference is a problem that says so", () => {
  assert.deepEqual(problemsWithImages({ DELUNO: { label: "Deluno build" } }), ['DELUNO: "undefined" is not a reference with a sha256 digest.']);
});

test("the warnings a scenario that fails on purpose expects are none when it made no film to fail", () => {
  const { expectedLogs } = SHARDS["with-deluno"].scenarios["deluno-failed-release"];
  assert.equal(expectedLogs({})({ title: "anything" }), false);
});

test("a pass in CI that names no commit is a pass without a full commit", () => {
  const broken = structuredClone(loadHomes());
  broken.scenarios.find((scenario) => scenario.id === "process-again").passedInCi = [{ run: 1, ref: "main" }];
  const catalog = catalogIds(readFileSync(new URL("./scenarios/Weir.Scenarios.Catalog.ps1", import.meta.url), "utf8"));
  assert.match(problemsWithHomes(broken, catalog).join("; "), /process-again: a pass needs its run number, its full commit and its ref/);
});

test("a command that fails for any reason but its usage fails loudly instead of printing the usage", async () => {
  const shards = { runLinuxShard: async () => { throw new Error("Docker is not there"); }, runWindowsShardHere: async () => [] };
  await assert.rejects(() => main(["weir-only", "--candidate", "weir:test"], { env: {}, platform: "linux", shards, log: () => {}, error: () => {} }), /Docker is not there/);
  assert.ok(new UsageError("x") instanceof Error);
});

// --- a zip file -----------------------------------------------------------------------------------------------------------------------

/** A zip file that is only an end of directory record saying what it is given, after `before` bytes. */
function zipWith({ count, directoryBytes, directoryAt, before = Buffer.alloc(0) }) {
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(count, 10);
  end.writeUInt32LE(directoryBytes, 12);
  end.writeUInt32LE(directoryAt, 16);
  const folder = mkdtempSync(join(tmpdir(), "weir-zip-last-"));
  const path = join(folder, "a.zip");
  writeFileSync(path, Buffer.concat([before, end]));
  return { path, remove: () => rmSync(folder, { recursive: true, force: true }) };
}

test("a zip64 file is refused, and so is a directory whose entries are not entries", async () => {
  const big = zipWith({ count: 0xffff, directoryBytes: 0, directoryAt: 0 });
  const damaged = zipWith({ count: 1, directoryBytes: 46, directoryAt: 0, before: Buffer.alloc(46) });
  try {
    await assert.rejects(() => zipEntries(big.path), /is a zip64 file, which this does not read/);
    await assert.rejects(() => zipEntries(damaged.path), /has a damaged directory at entry 0/);
    assert.deepEqual(differencesInEntries(new Map(), new Map()), []);
  } finally {
    big.remove();
    damaged.remove();
  }
});

test("runScenarios is what judges a scenario that uses the stand-ins above", async () => {
  const [result] = await runScenarios({ ids: ["quiet"], registry: { quiet: async () => "It did what it should." }, world: new FakeWorld().asWorld() });
  assert.equal(result.status, "passed");
});
