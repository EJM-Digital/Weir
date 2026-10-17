// deluno-film-handoff, judged against stand-ins for Deluno, qBittorrent and Weir: it must pass when the film goes through both applications, and
// fail, saying where, for each place it can stop or go wrong. The release source it relies on has its own tests (scenarios-in-ci-fixture.test.mjs).
import assert from "node:assert/strict";
import { test } from "node:test";

import { runScenarios } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";
import { weirFilesFor } from "./scenarios-ci/with-deluno/film-handoff.mjs";

function fakeClock() {
  let now = 0;
  return { now: () => now, sleep: async (ms) => void (now += ms) };
}

const RELEASE = "Scna1b2c3Film.2020.1080p.WEB-DL.x264-WEIR";
const OUTPUT = `/weir/Ready/Movies/${RELEASE}`;

/**
 * A world in which the film goes through. Each part can be told to go wrong.
 * @param {object} [wrong]
 * @param {boolean} [wrong.neverFinishes] qBittorrent never finishes the download.
 * @param {object} [wrong.handoff] Fields of Deluno's hand-off that replace the good ones.
 * @param {boolean} [wrong.noImportedOutcome] Deluno never says it imported the film.
 * @param {boolean} [wrong.weirHasNoFile] Weir never shows the cleaned file.
 * @param {string} [wrong.fileStatus] The status Weir shows for the cleaned file.
 * @param {string} [wrong.answerRecorded] The answer Weir has recorded against the file.
 */
function world(wrong = {}) {
  const sent = [];
  const handoff = { releaseName: RELEASE, mediaType: "movies", status: "completed", importJobId: "job-1", outputPath: OUTPUT, createdUtc: "2026-10-17T10:00:00Z", outcome: wrong.noImportedOutcome ? null : { outcome: "imported" }, ...wrong.handoff };
  const file = { id: 1, relative_path: `${RELEASE}/${RELEASE}.mkv`, status: wrong.fileStatus ?? "processed", handback: { output_path: `${OUTPUT}/${RELEASE}.mkv`, outcome: wrong.answerRecorded ?? "imported" } };
  const calls = [];
  const weir = {
    write: async (method, path) => (calls.push(`${method} ${path}`), { status: 200, json: {}, text: "" }),
    get: async (path) => {
      if (path.startsWith("/system/log")) return { items: [], next_cursor: null };
      if (path === "/processing/libraries") return [{ id: 3, name: "Movies", folders_synced_from_connection_id: 7, manager_connection_ids: [7], watched_folder: "/downloads/completed/Movies", output_folder: "/weir/Ready/Movies" }];
      if (path.startsWith("/activity/recent")) return { items: [{ result: "failed", title: "Cleaning failed" }] };
      if (path.startsWith("/processing/files")) return { files: wrong.weirHasNoFile ? [] : [file] };
      throw new Error(`The stand-in has no route ${path}`);
    },
  };
  const deluno = {
    connectWeir: async () => ({ id: 7, how: "connected Weir to Deluno" }),
    makeFilm: async ({ title, year }) => (calls.push(`makeFilm ${title} ${year}`), { title, year, release: RELEASE, infohash: "abc123", size: 56_000_000, url: `http://fixture:8765/dl/${RELEASE}.torrent` }),
    sendFilm: async (film) => void sent.push(film.release),
    handoffOf: async () => handoff,
    qbittorrent: { torrent: async (hash) => (hash === "abc123" ? { progress: wrong.neverFinishes ? 0.4 : 1, amount_left: wrong.neverFinishes ? 30_000_000 : 0, size: 56_000_000 } : undefined) },
  };
  return { calls, sent, world: { weir, deluno, box: {}, runId: "a1b2c3", clock: fakeClock() } };
}

const run = (stand) => runScenarios({ ids: ["deluno-film-handoff"], registry: SHARDS["with-deluno"].scenarios, world: stand.world }).then(([result]) => result);

test("a film Deluno sends, qBittorrent downloads, Deluno hands to Weir and imports is followed through both applications", async () => {
  const stand = world();
  const result = await run(stand);
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, new RegExp(`qBittorrent finished it \\(56000000 bytes\\)`));
  assert.match(result.detail, /as processed with Deluno's imported answer recorded against it, and Deluno's own record says imported/);
  assert.deepEqual(stand.sent, [RELEASE]);
  assert.ok(stand.calls.includes("POST /media-managers/connections/7/test"), "Weir was set up from Deluno first");
});

test("a download qBittorrent never finishes fails the scenario, saying which release", async () => {
  const result = await run(world({ neverFinishes: true }));
  assert.equal(result.status, "failed");
  assert.match(result.detail, new RegExp(`qBittorrent to finish ${RELEASE} did not happen`));
});

test("a hand-off that never settles fails the scenario", async () => {
  const result = await run(world({ handoff: { status: "failed", importJobId: null } }));
  assert.equal(result.status, "failed");
  assert.match(result.detail, /the hand-off of .* to settle did not happen/);
});

test("a hand-off that is not a film fails the scenario", async () => {
  const result = await run(world({ handoff: { mediaType: "tv" } }));
  assert.match(result.detail, /Deluno handed over a film \(it reads tv\)/);
});

test("a scenario that stops says where: what Weir, its Activity and Deluno show about the film", async () => {
  const result = await run(world({ handoff: { status: "failed", importJobId: null } }));
  assert.ok(result.detail.includes(`Weir shows [{"path":"${RELEASE}/${RELEASE}.mkv","status":"processed"`), result.detail);
  assert.ok(result.detail.includes(`its recent Activity: ["failed: Cleaning failed"]; Deluno shows {"releaseName":"${RELEASE}"`), result.detail);
});

test("Weir that shows no cleaned file for the hand-off fails the scenario", async () => {
  const result = await run(world({ weirHasNoFile: true }));
  assert.match(result.detail, /Weir to show the cleaned file for Deluno's hand-off of .* did not happen/);
});

test("a cleaned file Weir does not show as processed fails the scenario", async () => {
  const result = await run(world({ fileStatus: "processing_failed" }));
  assert.match(result.detail, /as processed \(it reads processing_failed\)/);
});

test("Deluno that never says it imported the film fails the scenario", async () => {
  const result = await run(world({ noImportedOutcome: true }));
  assert.match(result.detail, /Deluno to show its own import of .* done did not happen/);
});

test("Deluno's imported answer that Weir never records fails the scenario", async () => {
  const result = await run(world({ answerRecorded: "pending" }));
  assert.match(result.detail, /imported answer recorded against the cleaned file did not happen/);
});

test("Weir's files for a hand-off are found by the last part of its output path, or the release folder's", () => {
  const handoff = { outputPath: `${OUTPUT}/` };
  const files = [
    { id: 1, handback: { output_path: `${OUTPUT}/${RELEASE}.mkv` } },
    { id: 2, handback: { output_path: `/weir/Ready/Movies/${RELEASE}` } },
    { id: 3, handback: { output_path: "/weir/Ready/Movies/Other.Film.2019/Other.Film.2019.mkv" } },
    { id: 4, handback: null },
    { id: 5 },
  ];
  assert.deepEqual(weirFilesFor(handoff, files).map((file) => file.id), [1, 2]);
  assert.deepEqual(weirFilesFor({ outputPath: `C:\\Ready\\${RELEASE}` }, files).map((file) => file.id), [1, 2]);
  assert.deepEqual(weirFilesFor({ outputPath: "" }, files), []);
});
