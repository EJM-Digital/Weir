// node --test scripts/scenarios-in-ci-deluno-world.test.mjs
// What the Deluno world asks of Deluno, qBittorrent, the release source and the recording proxy after the setup: the films and shows it
// sends, the library it makes for TV, the recordings it writes, and the answers of the two applications' clients that are not a success.
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, test } from "node:test";

import { DelunoWorld, FILM_BITRATE, FILM_FOLDER, FILM_SECONDS, FOLDERS, TV_CATEGORY } from "./scenarios-ci/with-deluno/deluno-world.mjs";
import { DelunoApi } from "./scenarios-ci/with-deluno/deluno-api.mjs";
import { QbittorrentApi } from "./scenarios-ci/with-deluno/qbittorrent-api.mjs";

const realFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = realFetch;
});

/** Answers each request from `routes` (keyed "METHOD path") and remembers what was asked. */
function answers(routes) {
  const asked = [];
  globalThis.fetch = async (url, options = {}) => {
    const { origin, pathname, search } = new URL(url);
    const key = `${options.method ?? "GET"} ${pathname}${search}`;
    asked.push({ origin, key, body: options.body });
    const route = routes[key];
    if (route === undefined) throw new Error(`No stand-in for ${key}`);
    const { status = 200, body = {}, text, headers } = typeof route === "function" ? route(options) : route;
    return new Response(text ?? JSON.stringify(body), { status, headers });
  };
  return asked;
}

/** A Deluno that records every call made to it and answers with the next id in a list. */
function fakeDeluno({ handoffs = [], version = "1.2.3" } = {}) {
  const calls = [];
  let id = 0;
  return {
    calls,
    must: async (method, route, body) => {
      calls.push({ method, route, body });
      if (route === "/api/system/status") return { version };
      return { id: `${++id}`, name: "Weir" };
    },
    makeApiKey: async () => "deluno_key",
    handoffs: async () => handoffs,
  };
}

function worldWith({ deluno = fakeDeluno(), weir, qbittorrent } = {}) {
  const categories = [];
  return {
    categories,
    world: new DelunoWorld({
      deluno,
      qbittorrent: qbittorrent ?? { createCategory: async (...made) => categories.push(made) },
      weir: weir ?? { write: async () => ({ status: 200, json: {}, text: "" }) },
      library: { id: "library-1" },
      indexer: { id: "indexer-1", name: "Scenario feed" },
      client: { id: "client-1" },
    }),
  };
}

test("a film is made in Weir's container and the release source makes a release of it", async () => {
  const asked = answers({ "POST /releases": (options) => ({ body: { release: "Film.2020.1080p", received: JSON.parse(options.body) } }) });
  const made = [];
  const box = { makeFilm: async (options) => made.push(["film", options]), makeAudioOnly: async (options) => made.push(["audio", options]) };
  const { world } = worldWith();

  const release = await world.makeFilm({ box, title: "Film", year: 2020, zeros: true, extras: [{ path: "a.nfo", bytes: 5 }], episodes: [1, 2] });
  await world.makeFilm({ box, title: "Sound", year: 2021, content: "audio-only" });

  assert.deepEqual(made, [
    ["film", { path: `${FILM_FOLDER}/Film.mkv`, seconds: FILM_SECONDS, videoBitrate: FILM_BITRATE }],
    ["audio", { path: `${FILM_FOLDER}/Sound.mkv`, seconds: FILM_SECONDS }],
  ]);
  assert.deepEqual(release.received, { source: `${FILM_FOLDER}/Film.mkv`, title: "Film", year: 2020, zeros: true, extras: [{ path: "a.nfo", bytes: 5 }], episodes: [1, 2] });
  assert.equal(asked[0].origin, "http://localhost:18765");
});

test("a release source that cannot make the release stops the scenario with what it said", async () => {
  answers({ "POST /releases": { status: 500, text: "no space left" } });
  const { world } = worldWith();
  await assert.rejects(world.makeFilm({ box: { makeFilm: async () => {} }, title: "Film", year: 2020 }), /The release source made the release \(answered 500: no space left\)/);
});

test("a film is added to Deluno and its release sent to qBittorrent as a forced grab", async () => {
  const deluno = fakeDeluno();
  const { world } = worldWith({ deluno });

  const movie = await world.sendFilm({ title: "Film", year: 2020, release: "Film.2020.1080p", url: "http://fixture/Film.torrent", size: 123 });

  assert.equal(movie.id, "1");
  assert.deepEqual(deluno.calls[0], { method: "POST", route: "/api/movies", body: { title: "Film", releaseYear: 2020, imdbId: null, monitored: true } });
  assert.equal(deluno.calls[1].route, "/api/movies/1/grab");
  assert.deepEqual(
    [deluno.calls[1].body.releaseName, deluno.calls[1].body.indexerId, deluno.calls[1].body.downloadUrl, deluno.calls[1].body.sizeBytes, deluno.calls[1].body.force],
    ["Film.2020.1080p", "indexer-1", "http://fixture/Film.torrent", 123, true],
  );
});

test("the TV library is made once, with its own category, profile, routing and workflow, and a show is sent into it", async () => {
  const deluno = fakeDeluno();
  const weirCalls = [];
  const weir = {
    write: async (method, route) => {
      weirCalls.push(`${method} ${route}`);
      return route.endsWith("/webhook-secret") ? { status: 200, json: { webhook_url_path: "/hook", webhook_secret: "shh" }, text: "" } : { status: 201, json: { id: 4 }, text: "" };
    },
  };
  const { world, categories } = worldWith({ deluno, weir });

  const first = await world.tvLibrary();
  const second = await world.tvLibrary();
  const series = await world.sendSeries({ title: "Show", year: 2019, release: "Show.S01E01", url: "http://fixture/Show.torrent", size: 9 });

  assert.equal(first, second);
  assert.deepEqual(categories, [[TV_CATEGORY, FOLDERS.tvDownloads]]);
  assert.equal(weirCalls.filter((call) => call === "POST /media-managers/connections").length, 1);
  const routes = deluno.calls.map((call) => `${call.method} ${call.route}`);
  assert.ok(routes.includes("POST /api/quality-profiles") && routes.includes(`PUT /api/libraries/${first.id}/routing`) && routes.includes(`PUT /api/libraries/${first.id}/workflow`));
  const workflow = deluno.calls.find((call) => call.route === `/api/libraries/${first.id}/workflow`);
  assert.equal(workflow.body.processorOutputPath, FOLDERS.tvWeirOutput);
  assert.equal(deluno.calls.find((call) => call.route === "/api/integrations/processors/connections").body.authHeaderName, "X-Webhook-Secret");
  assert.deepEqual(deluno.calls.find((call) => call.route === "/api/series").body, { title: "Show", startYear: 2019, monitored: true, libraryId: first.id });
  assert.equal(series.id, deluno.calls.find((call) => call.route.startsWith("/api/series/")).route.split("/")[3]);
  assert.match(deluno.calls.at(-1).route, new RegExp(`^/api/series/${series.id}/grab\\?libraryId=${first.id}$`));
});

test("the newest hand-off of a release is the one found, and none is undefined", async () => {
  const handoffs = [
    { id: "a", releaseName: "Film.2020", createdUtc: "2026-01-01T00:00:00Z" },
    { id: "b", releaseName: "Film.2020.repack", createdUtc: "2026-01-02T00:00:00Z" },
    { id: "c", releaseName: "Other", createdUtc: "2026-01-03T00:00:00Z" },
  ];
  const { world } = worldWith({ deluno: fakeDeluno({ handoffs }) });
  assert.equal((await world.handoffOf("Film.2020")).id, "b");
  assert.equal(await world.handoffOf("Missing"), undefined);
});

test("the conversations a run asks to keep are written under the version Deluno reports", async () => {
  const exchange = (id, route, requestPath) => ({ route, request: { method: "POST", path: requestPath, headers: {}, body: { id } }, reply: { status: 200, headers: {}, body: {} } });
  answers({ "GET /exchanges?after=0": { body: { exchanges: [exchange("h-1", "to-weir", "/api/v1/intake/handoffs/h-1")] } } });
  const { world } = worldWith({ deluno: fakeDeluno({ version: "9.8.7" }) });
  world.keepConversation({ id: "h-1" }, { state: "plain-success", note: "A film refined" });
  const folder = mkdtempSync(path.join(tmpdir(), "weir-recordings-"));
  try {
    const files = await world.writeRecordings(folder, new Date("2026-10-23T01:02:03Z"));

    assert.deepEqual(files, [path.join(folder, "9.8.7", "plain-success.json")]);
    const written = JSON.parse(readFileSync(files[0], "utf8"));
    assert.deepEqual([written.app, written.version, written.state, written.recordedAt, written.exchanges.length], ["deluno", "9.8.7", "plain-success", "2026-10-23T01:02:03.000Z", 1]);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("Deluno's client says what was asked of it when it refuses, and how to start when the first account exists already", async () => {
  answers({
    "GET /api/auth/bootstrap-status": { body: { requiresSetup: false } },
    "POST /api/refused": { status: 409, text: "x".repeat(900) },
    "GET /api/integrations/processors/handoffs?take=200": { body: [{ id: "h" }] },
  });
  const deluno = new DelunoApi("http://localhost:5099");
  await assert.rejects(deluno.createFirstAccount({ username: "u", displayName: "U", password: "p" }), /A new Deluno must offer to create the first account\./);
  await assert.rejects(deluno.must("POST", "/api/refused", {}), (error) => error.message === `Deluno POST /api/refused answered 409: ${"x".repeat(600)}`);
  assert.deepEqual(await deluno.handoffs(), [{ id: "h" }]);
});

test("qBittorrent's client lists categories and torrents, and says so when it is refused", async () => {
  const hash = "abc123";
  let refuse = false;
  answers({
    "POST /api/v2/auth/login": { text: "Ok.", headers: { "Set-Cookie": "SID=1; path=/" } },
    "GET /api/v2/torrents/categories": () => (refuse ? { status: 403, text: "x".repeat(400) } : { body: { movies: {} } }),
    [`GET /api/v2/torrents/info?hashes=${hash}`]: { body: [{ hash, state: "uploading" }] },
    "GET /api/v2/torrents/info?hashes=none": { body: [] },
  });
  const client = await QbittorrentApi.signIn("http://localhost:18081", { username: "u", password: "p" });

  assert.deepEqual(await client.categories(), { movies: {} });
  assert.equal((await client.torrent(hash)).state, "uploading");
  assert.equal(await client.torrent("none"), undefined);
  refuse = true;
  await assert.rejects(client.categories(), (error) => error.message === `qBittorrent GET /api/v2/torrents/categories answered 403: ${"x".repeat(300)}`);
});
