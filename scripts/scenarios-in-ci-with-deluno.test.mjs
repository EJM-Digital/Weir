// The with-deluno shard: the images it starts are pinned by digest in one file and named by variable everywhere else, the way Deluno,
// qBittorrent and Weir are set up is the sequence of calls a person's setup makes, and workflows-from-deluno is judged against stand-ins.
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { afterEach, test } from "node:test";
import { fileURLToPath } from "node:url";

import { imageEnvironment, imageVariable, loadImages, overrideVariable, problemsWithImages, withOverrides } from "./scenarios-ci/images.mjs";
import { runScenarios } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";
import { QbittorrentApi } from "./scenarios-ci/with-deluno/qbittorrent-api.mjs";
import { DelunoWorld, FOLDERS, newLogins, qbittorrentPasswordHash } from "./scenarios-ci/with-deluno/deluno-world.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(repository, path), "utf8");
const images = loadImages();

// --- the images ---------------------------------------------------------------------------------------------------------

test("Deluno, qBittorrent and the release source's runtime are pinned by digest, with a label saying what was pinned", () => {
  assert.deepEqual(problemsWithImages(images), []);
  assert.match(images.DELUNO.reference, /^ghcr\.io\/[^/]+\/deluno@sha256:[0-9a-f]{64}$/);
  assert.match(images.DELUNO.label, /1\.0\.0-rc\.30\.33/);
  assert.match(images.QBITTORRENT.reference, /^ghcr\.io\/linuxserver\/qbittorrent@sha256:[0-9a-f]{64}$/);
});

test("a reference without a digest, a tag in place of one, or a pin without a label is a problem", () => {
  const problems = problemsWithImages({
    DELUNO: { label: "Deluno", reference: "ghcr.io/example/deluno:latest" },
    QBIT: { label: "", reference: "ghcr.io/linuxserver/qbittorrent@sha256:abc" },
    "bad name": { label: "Fine", reference: `x@sha256:${"a".repeat(64)}` },
  }).join("\n");
  assert.match(problems, /DELUNO: "ghcr.io\/example\/deluno:latest" is not a reference with a sha256 digest/);
  assert.match(problems, /QBIT: .*not a reference with a sha256 digest/);
  assert.match(problems, /QBIT: it needs a label/);
  assert.match(problems, /bad name: the name must be capital letters/);
});

test("Compose is given each pinned reference under a variable of its name", () => {
  assert.deepEqual(imageEnvironment(images), {
    SCENARIO_DELUNO_IMAGE: images.DELUNO.reference,
    SCENARIO_QBITTORRENT_IMAGE: images.QBITTORRENT.reference,
    SCENARIO_NODE_IMAGE: images.NODE.reference,
  });
  assert.equal(imageVariable("QBITTORRENT"), "SCENARIO_QBITTORRENT_IMAGE");
});

function filesBelow(folder) {
  return readdirSync(join(repository, folder)).flatMap((name) => {
    const path = join(folder, name);
    if (name === "node_modules") return [];
    return statSync(join(repository, path)).isDirectory() ? filesBelow(path) : [path];
  });
}

test("an image can be replaced for one run, by a reference with a digest, and by nothing else", () => {
  const newest = `registry.example/deluno@sha256:${"a".repeat(64)}`;
  const replaced = withOverrides(images, { [overrideVariable("DELUNO")]: newest });
  assert.equal(replaced.DELUNO.reference, newest);
  assert.match(replaced.DELUNO.label, /replaced for this run/);
  assert.deepEqual(replaced.QBITTORRENT, images.QBITTORRENT, "the others are as pinned");
  assert.deepEqual(withOverrides(images, {}), images);
  assert.throws(() => withOverrides(images, { [overrideVariable("DELUNO")]: "registry.example/deluno:latest" }), /must be a reference with a sha256 digest/);
  assert.equal(imageEnvironment(replaced)[imageVariable("DELUNO")], newest);
});

test("no digest is written anywhere in the scenarios but images.json", () => {
  const shipped = filesBelow("scripts/scenarios-ci").filter((path) => !path.endsWith("images.json") && !path.endsWith("package-lock.json"));
  for (const path of shipped) assert.doesNotMatch(read(path), /sha256:[0-9a-f]{12}/, `${path.replaceAll("\\", "/")} names a digest`);
  assert.doesNotMatch(read("scripts/scenarios-in-ci.mjs"), /sha256:[0-9a-f]{12}/);
});

test("every image the Compose file starts is Weir's own build or a pinned one, and every pinned one is started", () => {
  const compose = read("scripts/scenarios-ci/with-deluno/compose.yaml");
  const used = [...compose.matchAll(/^\s+image:\s*(\S+)\s*$/gm)].map((match) => match[1]);
  const allowed = ["${WEIR_DOCKER_IMAGE:?}", ...Object.keys(images).map((name) => `\${${imageVariable(name)}:?}`)];
  assert.deepEqual(used.filter((image) => !allowed.includes(image)), [], "an image that is not Weir's build and not in images.json");
  for (const name of Object.keys(images)) assert.ok(used.includes(`\${${imageVariable(name)}:?}`), `${name} is pinned but never started`);
});

test("Weir and the applications beside it share the download and output folders at the same path in every container", () => {
  const compose = read("scripts/scenarios-ci/with-deluno/compose.yaml");
  for (const folder of ["downloads:/downloads", "weir-output:/weir"]) {
    assert.ok(compose.split(folder).length - 1 >= 3, `${folder} is mounted by Weir, Deluno and the setup step`);
  }
  assert.match(compose, /Server__AllowLan: "true"/);
});

// --- qBittorrent's login ------------------------------------------------------------------------------------------------

test("a password is held as qBittorrent holds it: the PBKDF2 of a recorded login reproduces", () => {
  // The settings file of Deluno's end-to-end stack: a login qBittorrent accepts, with the password it was made from.
  const salt = Buffer.from("AQIDBAUGBwgJCgsMDQ4PEA==", "base64");
  assert.equal(
    qbittorrentPasswordHash("e2e-qbittorrent-pass", salt),
    "AQIDBAUGBwgJCgsMDQ4PEA==:ON20e5tSdh0HLVq7+CPB6duwMB6sky6lbjOsMp0F4vW0o7OHqtLhAbvPdWeYxdTeSC979BZQg41m2IfhrN7GAw==",
  );
});

test("each run makes logins of its own, and the hash given to Compose is the one of the password given to qBittorrent", () => {
  const [one, two] = [newLogins(), newLogins()];
  assert.notEqual(one.qbittorrent.password, two.qbittorrent.password);
  const [salt] = one.qbittorrent.pbkdf2.split(":");
  assert.equal(qbittorrentPasswordHash(one.qbittorrent.password, Buffer.from(salt, "base64")), one.qbittorrent.pbkdf2);
});

// --- the setup, against stand-ins for the applications ------------------------------------------------------------------

const realFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = realFetch;
});

/** Answers the routes of Deluno and qBittorrent that the setup calls, and records each request. */
function standIns({ clientTest = 200, indexerTest = 200 } = {}) {
  const requests = [];
  globalThis.fetch = async (url, options = {}) => {
    const { origin, pathname } = new URL(url);
    const method = options.method ?? "GET";
    const body = options.body ? (String(options.headers?.["Content-Type"]).includes("json") ? JSON.parse(options.body) : options.body) : undefined;
    requests.push({ origin, method, path: pathname, body, authorization: options.headers?.Authorization, cookie: options.headers?.Cookie });
    const json = (value, status = 200, headers = {}) => new Response(JSON.stringify(value), { status, headers });
    const key = `${method} ${pathname}`;
    if (pathname === "/health" || pathname === "/") return new Response("ok");
    if (key === "GET /api/auth/bootstrap-status") return json({ requiresSetup: true });
    if (key === "POST /api/auth/bootstrap") return json({});
    if (key === "POST /api/auth/login") return json({ accessToken: "token-1" });
    if (key === "POST /api/v2/auth/login") return new Response("Ok.", { status: 200, headers: { "Set-Cookie": "SID=abc; HttpOnly; path=/" } });
    if (pathname.startsWith("/api/v2/")) return new Response("{}", { status: 200 });
    if (key === "POST /api/download-clients") return json({ id: "client-1" });
    if (key === "POST /api/download-clients/client-1/test") return json({}, clientTest);
    if (key === "POST /api/quality-profiles") return json({ id: "profile-1" });
    if (key === "POST /api/libraries") return json({ id: "library-1" });
    if (key === "POST /api/indexers") return json({ id: "indexer-1", name: "Scenario feed" });
    if (key === "POST /api/indexers/indexer-1/test") return json({}, indexerTest);
    if (key === "PUT /api/libraries/library-1/routing") return json({});
    if (key === "PUT /api/settings/automation") return json({});
    if (key === "POST /api/api-keys") return json({ apiKey: "deluno_key" });
    if (key === "POST /api/integrations/processors/connections") return json({ id: "processor-1", name: "Weir" });
    if (key === "PUT /api/libraries/library-1/workflow") return json({});
    throw new Error(`The stand-in has no route ${key}`);
  };
  return requests;
}

const LOGINS = { deluno: { username: "u", displayName: "U", password: "p1" }, qbittorrent: { username: "q", password: "p2", pbkdf2: "s:h" } };
/** A clock that moves only when something sleeps, so a wait that never ends costs no time. */
function fakeClock() {
  let now = 0;
  return { now: () => now, sleep: async (ms) => void (now += ms) };
}
const instantClock = fakeClock();

test("Deluno is set up as a person sets it up: its account, qBittorrent's folders and category, the client and its test, a profile and the movies library", async () => {
  const requests = standIns();
  await DelunoWorld.setUp({ weir: {}, logins: LOGINS, clock: instantClock });
  const steps = requests.filter((request) => !["/health", "/"].includes(request.path)).map((request) => `${request.method} ${request.path}`);
  assert.deepEqual(steps, [
    "GET /api/auth/bootstrap-status",
    "POST /api/auth/bootstrap",
    "POST /api/auth/login",
    "POST /api/v2/auth/login",
    "POST /api/v2/app/setPreferences",
    "POST /api/v2/torrents/createCategory",
    "POST /api/download-clients",
    "POST /api/download-clients/client-1/test",
    "POST /api/quality-profiles",
    "POST /api/libraries",
    "POST /api/indexers",
    "POST /api/indexers/indexer-1/test",
    "PUT /api/libraries/library-1/routing",
    "PUT /api/settings/automation",
  ]);
  const client = requests.find((request) => request.path === "/api/download-clients");
  assert.deepEqual(
    { host: client.body.host, port: client.body.port, protocol: client.body.protocol, username: client.body.username, authorization: client.authorization },
    { host: "qbittorrent", port: 8080, protocol: "qbittorrent", username: "q", authorization: "Bearer token-1" },
  );
  assert.match(requests.find((request) => request.path === "/api/v2/torrents/createCategory").body, /savePath=%2Fdownloads%2Fcompleted%2FMovies/);
  assert.equal(requests.find((request) => request.path === "/api/v2/app/setPreferences").cookie, "SID=abc");
  const library = requests.find((request) => request.path === "/api/libraries").body;
  assert.deepEqual([library.rootPath, library.downloadsPath, library.qualityProfileId], [FOLDERS.library, FOLDERS.downloads, "profile-1"]);
});

test("the release source is added to Deluno as a Torznab indexer, tested, and the library is routed to it and to qBittorrent, with automatic jobs on", async () => {
  const requests = standIns();
  await DelunoWorld.setUp({ weir: {}, logins: LOGINS, clock: instantClock });
  const indexer = requests.find((request) => request.path === "/api/indexers").body;
  assert.deepEqual([indexer.protocol, indexer.baseUrl, indexer.automaticSearchEnabled], ["torznab", "http://fixture:8765", false]);
  const routing = requests.find((request) => request.path === "/api/libraries/library-1/routing").body;
  assert.deepEqual([routing.sources[0].indexerId, routing.downloadClients[0].downloadClientId], ["indexer-1", "client-1"]);
  assert.deepEqual(requests.find((request) => request.path === "/api/settings/automation").body, { isEnabled: true });
});

test("an indexer whose test fails stops the setup, saying what Deluno answered", async () => {
  standIns({ indexerTest: 500 });
  await assert.rejects(() => DelunoWorld.setUp({ weir: {}, logins: LOGINS, clock: instantClock }), /Deluno's test of the release source failed: 500/);
});

test("qBittorrent signing in with nothing to say (204) is a sign-in, and a refusal or a missing cookie is not", async () => {
  const answer = (status, text, cookie) => async () => new Response(text === "" ? null : text, { status, headers: cookie ? { "Set-Cookie": `${cookie}=abc; path=/` } : {} });
  for (const name of ["SID", "QBT_SID_8080"]) {
    globalThis.fetch = answer(204, "", name);
    assert.ok(await QbittorrentApi.signIn("http://q", { username: "u", password: "p" }));
  }
  globalThis.fetch = answer(200, "Fails.", "SID");
  await assert.rejects(() => QbittorrentApi.signIn("http://q", { username: "u", password: "p" }), /would not sign in: 200 Fails/);
  globalThis.fetch = answer(204, "", null);
  await assert.rejects(() => QbittorrentApi.signIn("http://q", { username: "u", password: "p" }), /would not sign in: 204/);
});

test("a download client whose test fails stops the setup, saying what Deluno answered", async () => {
  standIns({ clientTest: 502 });
  await assert.rejects(() => DelunoWorld.setUp({ weir: {}, logins: LOGINS, clock: instantClock }), /Deluno's test of qBittorrent failed: 502/);
});

test("Weir and Deluno are connected once: a key made in Deluno, Weir's secret given to Deluno as the library's processor", async () => {
  const requests = standIns();
  const weirCalls = [];
  const weir = {
    write: async (method, path, fields) => {
      weirCalls.push([method, path, fields]);
      if (path === "/media-managers/connections") return { status: 201, json: { id: 7 }, text: "" };
      return { status: 200, json: { webhook_url_path: "/api/v1/hooks/abc", header_name: "X-Webhook-Secret", webhook_secret: "shh" }, text: "" };
    },
  };
  const world = await DelunoWorld.setUp({ weir, logins: LOGINS, clock: instantClock });
  const first = await world.connectWeir();
  const second = await world.connectWeir();
  assert.equal(first, second, "a second call is the same connection, not a second one");
  assert.equal(first.id, 7);
  assert.deepEqual(weirCalls, [
    ["POST", "/media-managers/connections", { kind: "deluno", base_url: "http://recorder:8081", api_key: "deluno_key", enabled: true }],
    ["POST", "/media-managers/connections/7/webhook-secret", undefined],
  ]);
  const processor = requests.find((request) => request.path === "/api/integrations/processors/connections").body;
  assert.deepEqual([processor.submissionUrl, processor.authHeaderName, processor.secret], ["http://recorder:8082/api/v1/hooks/abc", "X-Webhook-Secret", "shh"]);
  const workflow = requests.find((request) => request.method === "PUT" && request.path === "/api/libraries/library-1/workflow").body;
  assert.deepEqual([workflow.importWorkflow, workflow.processorConnectionId, workflow.processorOutputPath], ["refine-before-import", "processor-1", FOLDERS.weirOutput]);
});

// --- workflows-from-deluno ----------------------------------------------------------------------------------------------

function worldWith({ workflows, ready = true, linkedAfter = 0 }) {
  const calls = [];
  let tests = 0;
  const weir = {
    write: async (method, path) => (calls.push(`${method} ${path}`), tests++, { status: 200, json: {}, text: "" }),
    get: async (path) => {
      calls.push(`GET ${path}`);
      if (path === "/processing/libraries") return tests > linkedAfter ? workflows : [{ id: 1, name: "Movies", watched_folder: "/x", output_folder: "/y" }];
      return { ready };
    },
  };
  const deluno = { connectWeir: async () => ({ id: 7, how: "connected Weir to Deluno" }) };
  return { calls, world: { weir, deluno, runId: "a1b2c3", clock: fakeClock() } };
}

const linkedWorkflow = { id: 3, name: "Movies", folders_synced_from_connection_id: 7, manager_connection_ids: [7], watched_folder: FOLDERS.downloads, output_folder: FOLDERS.weirOutput };
const run = (world) => runScenarios({ ids: ["workflows-from-deluno"], registry: SHARDS["with-deluno"].scenarios, world: { ...world, weir: { ...world.weir, get: async (path) => (path.startsWith("/system/log") ? { items: [], next_cursor: null } : world.weir.get(path)) } } });

test("Weir sets up a workflow from Deluno's library, linked, with Deluno's folders and a ready folder chain", async () => {
  const { world, calls } = worldWith({ workflows: [linkedWorkflow] });
  const [result] = await run(world);
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, new RegExp(`watched ${FOLDERS.downloads.replaceAll("/", "\\/")}, output ${FOLDERS.weirOutput.replaceAll("/", "\\/")}`));
  assert.ok(calls.includes("POST /media-managers/connections/7/test"), "Weir was asked to look at Deluno now");
  assert.ok(calls.includes("GET /processing/libraries/3/folder-chain"));
});

test("a workflow that appears after Weir looks again is waited for", async () => {
  const { world } = worldWith({ workflows: [linkedWorkflow], linkedAfter: 3 });
  const [result] = await run(world);
  assert.equal(result.status, "passed", result.detail);
});

test("Weir that never sets up a workflow from Deluno fails the scenario", async () => {
  const { world } = worldWith({ workflows: [] });
  const [result] = await run(world);
  assert.equal(result.status, "failed");
  assert.match(result.detail, /Weir to set up its workflows from Deluno did not happen/);
});

test("a workflow not linked to Deluno, or whose folder chain is not ready, fails the scenario", async () => {
  const unlinked = await run(worldWith({ workflows: [{ ...linkedWorkflow, manager_connection_ids: [] }] }).world);
  assert.match(unlinked[0].detail, /'Movies' is linked to Deluno/);
  const notReady = await run(worldWith({ workflows: [linkedWorkflow], ready: false }).world);
  assert.match(notReady[0].detail, /the folder chain for 'Movies' is ready/);
});

test("the shard starts Deluno, qBittorrent and the recording proxy beside Weir, with an account, and keeps their logs", () => {
  const shard = SHARDS["with-deluno"];
  assert.deepEqual([shard.account, shard.browser], [true, false]);
  assert.deepEqual(shard.beside.services, ["deluno", "qbittorrent", "recorder"]);
  assert.deepEqual(shard.beside.files, ["scripts/scenarios-ci/with-deluno/compose.yaml"]);
  const started = shard.beside.start();
  assert.deepEqual(started.pull.sort(), Object.values(images).map((image) => image.reference).sort());
  assert.equal(started.environment.SCENARIO_QBITTORRENT_USERNAME, started.logins.qbittorrent.username);
  assert.equal(started.environment.SCENARIO_QBITTORRENT_PBKDF2, started.logins.qbittorrent.pbkdf2);
  for (const file of shard.beside.files) assert.ok(statSync(join(repository, file)).isFile(), relative(repository, join(repository, file)));
});
