// node --test scripts/scenarios-in-ci-servers-last.test.mjs
// The last branches of the stand-in servers of the scenarios that run in CI (#1060): the release source, the recording proxy and the GitHub
// feed, each run for real on 127.0.0.1, and their start-up as a program run the way the container runs it.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import http from "node:http";
import net from "node:net";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

import { REPOSITORY } from "./repository.mjs";
import { bodyOf, keptHeaders } from "./scenarios-ci/with-deluno/recorder/exchange.mjs";
import { RecordingSet, connectionExchanges, mentions } from "./scenarios-ci/with-deluno/recordings.mjs";
import { GithubStandIn } from "./scenarios-ci/github-stand-in.mjs";
import { createFixture, settingsFrom as fixtureSettings } from "./scenarios-ci/with-deluno/fixture/server.mjs";
import { startRecorder, couldNotReach, settingsFrom as recorderSettings } from "./scenarios-ci/with-deluno/recorder/proxy.mjs";

const folders = [];
const folder = (prefix) => {
  const made = mkdtempSync(join(tmpdir(), prefix));
  folders.push(made);
  return made;
};
after(() => {
  for (const made of folders) rmSync(made, { recursive: true, force: true });
});

const listening = (server) => new Promise((done) => server.listen(0, "127.0.0.1", () => done(server.address().port)));
const closed = (server) => new Promise((done) => server.close(done));

async function freePort() {
  const server = net.createServer();
  const port = await listening(server);
  await closed(server);
  return port;
}

/** Runs a script of the scenarios as a program, as the container does, with the environment given, until its standard input ends; resolves when it has printed `ready`. */
async function running(script, env, ready, use) {
  const path = fileURLToPath(new URL(script, import.meta.url));
  const entry = `process.argv[1] = ${JSON.stringify(path)};\nprocess.stdin.on("end", () => process.exit(0)).resume();\nawait import(${JSON.stringify(pathToFileURL(path).href)});`;
  const child = spawn(process.execPath, ["--input-type=module", "-e", entry], { env: { ...process.env, ...env }, stdio: ["pipe", "pipe", "inherit"] });
  const exited = new Promise((done) => child.once("exit", done));
  let said = "";
  await new Promise((done, fail) => {
    child.stdout.on("data", (chunk) => {
      said += chunk;
      if (said.includes(ready)) done();
    });
    child.once("exit", () => fail(new Error(`${script} ended before it said "${ready}": ${said}`)));
  });
  try {
    await use(said);
  } finally {
    child.stdin.end();
    assert.equal(await exited, 0);
  }
}

// --- the release source ---------------------------------------------------------------------------------------------------------------

test("the release source takes its port, folders and address from the environment, and has the container's own when it is given none", () => {
  assert.deepEqual(fixtureSettings({}), { port: 8765, host: undefined, work: "/work", publicBase: "http://fixture:8765" });
  assert.deepEqual(fixtureSettings({ FIXTURE_PORT: "9000", FIXTURE_HOST: "127.0.0.1", FIXTURE_WORK: "/films", FIXTURE_PUBLIC_BASE: "http://seed:9000" }), { port: 9000, host: "127.0.0.1", work: "/films", publicBase: "http://seed:9000" });
});

test("the release source, started as a program, answers its health check on the address it was given", async () => {
  const port = await freePort();
  await running("./scenarios-ci/with-deluno/fixture/server.mjs", { FIXTURE_PORT: String(port), FIXTURE_HOST: "127.0.0.1", FIXTURE_WORK: folder("weir-fixture-start-") }, "fixture listening on", async (said) => {
    assert.match(said, new RegExp(`fixture listening on ${port}`));
    assert.equal((await fetch(`http://127.0.0.1:${port}/health`)).status, 200);
  });
});

async function withFixture(use) {
  const work = folder("weir-fixture-last-");
  mkdirSync(join(work, "source"), { recursive: true });
  writeFileSync(join(work, "source", "x.mkv"), "film");
  const server = createFixture({ work, publicBase: "http://fixture:8765" });
  const port = await listening(server);
  try {
    await use({ post: (body, text = JSON.stringify(body)) => fetch(`http://127.0.0.1:${port}/releases`, { method: "POST", body: text }), source: join(work, "source", "x.mkv") });
  } finally {
    await closed(server);
  }
}

test("a release asked for with a bad list of episodes is refused in words, as plain text", async () => {
  await withFixture(async ({ post, source }) => {
    const answer = await post({ source, title: "ScnBad", year: 2020, episodes: [0] });
    assert.equal(answer.status, 400);
    assert.match(answer.headers.get("content-type"), /^text\/plain/);
    assert.equal(await answer.text(), "each episode is a number from 1 to 99");
  });
});

test("a release asked for with no title or no source is refused, and one that is not JSON is answered 500 with what was wrong", async () => {
  await withFixture(async ({ post, source }) => {
    assert.equal((await post({ source, year: 2020 })).status, 400);
    const noSource = await post({ title: "ScnNoSource", year: 2020 });
    assert.equal(noSource.status, 400);
    assert.match(await noSource.text(), /no film at undefined/);
    const broken = await post(undefined, "{not json");
    assert.equal(broken.status, 500);
    assert.match(await broken.text(), /JSON/);
  });
});

// --- the recording proxy --------------------------------------------------------------------------------------------------------------

test("the recording proxy takes its ports, applications and address from the environment, and has the container's own when it is given none", () => {
  assert.deepEqual(recorderSettings({}), {
    ports: { toDeluno: 8081, toWeir: 8082, control: 8083 },
    deluno: "http://deluno:8080",
    weir: "http://weir:9347",
    host: undefined,
  });
  assert.deepEqual(
    recorderSettings({ RECORDER_TO_DELUNO_PORT: "1", RECORDER_TO_WEIR_PORT: "2", RECORDER_CONTROL_PORT: "3", RECORDER_DELUNO: "http://d:4", RECORDER_WEIR: "http://w:5", RECORDER_HOST: "127.0.0.1" }),
    { ports: { toDeluno: 1, toWeir: 2, control: 3 }, deluno: "http://d:4", weir: "http://w:5", host: "127.0.0.1" },
  );
});

test("the recording proxy, started as a program, answers on its control port", async () => {
  const [toDeluno, toWeir, control] = [await freePort(), await freePort(), await freePort()];
  const env = { RECORDER_TO_DELUNO_PORT: String(toDeluno), RECORDER_TO_WEIR_PORT: String(toWeir), RECORDER_CONTROL_PORT: String(control), RECORDER_HOST: "127.0.0.1" };
  await running("./scenarios-ci/with-deluno/recorder/proxy.mjs", env, "recording proxy:", async (said) => {
    assert.match(said, new RegExp(`control at :${control}`));
    assert.equal((await fetch(`http://127.0.0.1:${control}/health`)).status, 200);
  });
});

test("what the proxy says when an application cannot be reached names the application, and the error's code or else its message", () => {
  assert.equal(couldNotReach("to-deluno", { code: "ECONNREFUSED", message: "x" }), "The recording proxy could not reach Deluno: ECONNREFUSED");
  assert.equal(couldNotReach("to-weir", new Error("it broke")), "The recording proxy could not reach Weir: it broke");
});

test("the control port answers a health check, lists every exchange when it is not told where to start, and refuses any other address", async () => {
  const recorder = await startRecorder({ deluno: "http://127.0.0.1:1", weir: "http://127.0.0.1:1", ports: { toDeluno: await freePort(), toWeir: await freePort(), control: await freePort() }, host: "127.0.0.1" });
  const controlPort = recorder.servers[2].address().port;
  try {
    assert.equal((await fetch(`http://127.0.0.1:${controlPort}/health`)).status, 200);
    assert.deepEqual(await (await fetch(`http://127.0.0.1:${controlPort}/exchanges`)).json(), { next: 0, exchanges: [] });
    assert.deepEqual(await (await fetch(`http://127.0.0.1:${controlPort}/exchanges?after=7`)).json(), { next: 7, exchanges: [] });
    const other = await fetch(`http://127.0.0.1:${controlPort}/nothing`);
    assert.equal(other.status, 404);
    assert.equal(await other.text(), "not here");
  } finally {
    await Promise.all(recorder.servers.map(closed));
  }
});

// --- what the proxy keeps of an exchange -----------------------------------------------------------------------------------------------

test("a header that arrives as a list is kept as one line, and a body with no content type is text", () => {
  assert.deepEqual(keptHeaders({ "Content-Type": ["application/json", "charset=utf-8"] }, ["content-type"]), { "content-type": "application/json, charset=utf-8" });
  assert.deepEqual(bodyOf(Buffer.from("plain words"), undefined), { bodyText: "plain words" });
});

test("an exchange with no headers and no bodies is about no hand-off, and a call made twice in the connection is kept once", () => {
  const bare = { seq: 1, route: "to-deluno", request: { method: "GET", path: "/api/manifest" }, reply: { status: 200 } };
  assert.equal(mentions(bare, "abc"), false);
  assert.deepEqual(connectionExchanges([bare, { ...bare, seq: 2 }], []).map((exchange) => exchange.seq), [1]);
});

test("a connection the proxy saw nothing of is refused without a hand-off to name, and a version with nothing in it is refused", () => {
  const set = new RecordingSet();
  set.keepConnection({ state: "real-connection", note: "n" });
  assert.throws(() => set.build([], "now", "1.0.0"), (error) => error.message === "The recording proxy saw nothing for real-connection.");
  assert.throws(() => set.build([], "now", undefined), /not one a folder can be named for: undefined/);
});

// --- the GitHub feed ------------------------------------------------------------------------------------------------------------------

const releases = { Assets: [{ PackageId: "Weir", Version: "1.0.1", Type: "Full", FileName: "Weir-1.0.1-full.nupkg", SHA256: "AA", Size: 8 }] };

async function withFeed(use) {
  const made = folder("weir-feed-last-");
  writeFileSync(resolve(made, "Weir-1.0.1-full.nupkg"), "full package bytes");
  const feed = new GithubStandIn({
    folder: made,
    current: { tag: "v1.0.1", releasesJson: JSON.stringify(releases) },
    previous: { tag: "v1.0.0", releasesJson: JSON.stringify({ Assets: [] }) },
    updated: "2026-10-17T10:00:00Z",
  });
  const port = await feed.listen({ port: 0 });
  try {
    await use({ feed, port, made });
  } finally {
    await feed.close();
  }
}

/** One request to the feed with the Host header it is given (fetch does not let a request choose its own). */
const ask = (port, path, { method = "GET", host = `github.com:${port}` } = {}) =>
  new Promise((done, fail) => {
    const request = http.request({ host: "127.0.0.1", port, path, method, headers: { host } }, (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => (body += chunk));
      response.on("end", () => done({ status: response.statusCode, text: async () => body }));
    });
    request.on("error", fail);
    request.end();
  });

test("the feed says which mode it is in", async () => {
  await withFeed(async ({ feed }) => {
    assert.equal(feed.mode, "newer");
    feed.use("corrupt");
    assert.equal(feed.mode, "corrupt");
  });
});

test("a feed that was never started closes at once", async () => {
  const feed = new GithubStandIn({ folder: ".", current: { tag: "v1", releasesJson: "{}" }, previous: { tag: "v0", releasesJson: "{}" }, updated: "t" });
  await feed.close();
});

test("a package the feed lists and cannot read is answered 500 with the reason", async () => {
  await withFeed(async ({ port, made }) => {
    rmSync(resolve(made, "Weir-1.0.1-full.nupkg"));
    const answer = await ask(port, `/${REPOSITORY}/releases/download/v1.0.1/Weir-1.0.1-full.nupkg`);
    assert.equal(answer.status, 500);
    assert.match(await answer.text(), /ENOENT/);
  });
});

test("an address that is no release page or download is not found, and a HEAD request is answered without a body", async () => {
  await withFeed(async ({ feed, port }) => {
    assert.equal((await ask(port, "/nothing-here")).status, 404);
    const head = await ask(port, "/nothing-here", { method: "HEAD" });
    assert.equal(head.status, 404);
    assert.equal(await head.text(), "");
    assert.equal(feed.requests.at(-1).status, 404);
  });
});

test("the API address is not found, and of the release the machine runs only its feed is there", async () => {
  await withFeed(async ({ port }) => {
    assert.equal((await ask(port, `/repos/${REPOSITORY}/releases`, { host: "api.github.com" })).status, 404);
    const download = (file) => `/${REPOSITORY}/releases/download/v1.0.0/${file}`;
    assert.deepEqual(JSON.parse(await (await ask(port, download("releases.win.json"))).text()), { Assets: [] });
    assert.equal((await ask(port, download("Weir-1.0.0-full.nupkg"))).status, 404);
  });
});

test("a request that names no host is answered like any other, and recorded with no host", async () => {
  await withFeed(async ({ feed, port }) => {
    const answer = await new Promise((done, fail) => {
      const socket = net.connect(port, "127.0.0.1", () => socket.write("GET /nothing-here HTTP/1.0\r\n\r\n"));
      let text = "";
      socket.on("data", (chunk) => (text += chunk));
      socket.on("end", () => done(text));
      socket.on("error", fail);
    });
    assert.match(answer, /^HTTP\/1\.1 404/);
    assert.deepEqual(feed.requests.at(-1), { host: "", path: "/nothing-here", status: 404 });
  });
});

test("the feed served over TLS reads the certificate it is given, so one that is no certificate is refused", async () => {
  const feed = new GithubStandIn({ folder: ".", current: { tag: "v1", releasesJson: "{}" }, previous: { tag: "v0", releasesJson: "{}" }, updated: "t" });
  assert.throws(() => feed.listen({ tls: { pfx: Buffer.from("not a certificate"), passphrase: "x" }, port: 0 }), Error);
});
