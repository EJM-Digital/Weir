// The recording proxy of the with-deluno shard and the recordings made from what it saw (scenarios-ci/with-deluno/recorder, recordings.mjs).
// The proxy runs for real between two small servers on free ports; the recordings are judged against the format of
// apps/server/tests/fixtures/recorded.
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, test } from "node:test";

import { SCRUBBED, bodyOf, keptHeaders } from "./scenarios-ci/with-deluno/recorder/exchange.mjs";
import { startRecorder } from "./scenarios-ci/with-deluno/recorder/proxy.mjs";
import { RecordingSet, conversationOf, connectionExchanges, mentions, withoutRepeats, writeRecordings } from "./scenarios-ci/with-deluno/recordings.mjs";

// --- what a recording keeps ---------------------------------------------------------------------------------------------

test("a recording keeps the headers a replay needs and scrubs every secret", () => {
  const kept = keptHeaders(
    { Host: "weir:9347", "Content-Length": "12", "Content-Type": "application/json", "X-Webhook-Secret": "hunter2", "X-Api-Key": "deluno_abc", "Idempotency-Key": "k:1", Authorization: "Bearer t", "User-Agent": "x" },
    ["content-type", "idempotency-key", "x-webhook-secret", "x-api-key", "authorization"],
  );
  assert.deepEqual(kept, { "content-type": "application/json", "x-webhook-secret": SCRUBBED, "x-api-key": SCRUBBED, "idempotency-key": "k:1", authorization: SCRUBBED });
  assert.equal(JSON.stringify(kept).includes("hunter2"), false);
});

test("a body is JSON when it says so and parses, text when it is anything else, and nothing when it is empty", () => {
  assert.deepEqual(bodyOf(Buffer.from('{"a":1}'), "application/json; charset=utf-8"), { body: { a: 1 } });
  assert.deepEqual(bodyOf(Buffer.from("not json"), "application/json"), { bodyText: "not json" });
  assert.deepEqual(bodyOf(Buffer.from("plain"), "text/plain"), { bodyText: "plain" });
  assert.deepEqual(bodyOf(Buffer.alloc(0), "application/json"), {});
});

test("the name of the machine an application runs on is not kept, wherever the body names it", () => {
  const body = bodyOf(Buffer.from(JSON.stringify({ product: "Weir", machine_name: "3f2a9c1d7b44", nested: [{ instanceName: "a-host" }] })), "application/json").body;
  assert.deepEqual(body, { product: "Weir", machine_name: SCRUBBED, nested: [{ instanceName: SCRUBBED }] });
});

// --- the proxy, between two servers ---------------------------------------------------------------------------------------

const closing = [];
const listen = (handler) =>
  new Promise((resolve) => {
    const server = http.createServer(handler);
    closing.push(server);
    server.listen(0, () => resolve(server.address().port));
  });
after(() => closing.forEach((server) => server.close()));

const seenByDeluno = [];
const seenByWeir = [];
let proxy;
let ports;

before(async () => {
  const delunoPort = await listen((request, response) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => {
      seenByDeluno.push({ method: request.method, url: request.url, headers: request.headers, body: Buffer.concat(chunks).toString() });
      response.writeHead(request.url.startsWith("/missing") ? 404 : 202, { "content-type": "application/json; charset=utf-8", "retry-after": "5", "x-noise": "1" });
      response.end(JSON.stringify({ status: "ok", echo: request.url }));
    });
  });
  const weirPort = await listen((request, response) => {
    seenByWeir.push({ method: request.method, url: request.url });
    response.writeHead(200, { "content-type": "text/plain" });
    response.end("ready");
  });
  ports = { toDeluno: await freePort(), toWeir: await freePort(), control: await freePort() };
  proxy = await startRecorder({ deluno: `http://localhost:${delunoPort}`, weir: `http://localhost:${weirPort}`, ports });
  closing.push(...proxy.servers);
});

function freePort() {
  return new Promise((resolve) => {
    const server = http.createServer().listen(0, () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

const control = async (after = 0) => (await fetch(`http://localhost:${ports.control}/exchanges?after=${after}`)).json();

test("a call through the proxy reaches the application unchanged, the reply comes back unchanged, and the exchange is written down", async () => {
  const sent = await fetch(`http://localhost:${ports.toDeluno}/api/integrations/processors/events?x=1`, {
    method: "POST",
    headers: { "content-type": "application/json", "x-api-key": "deluno_secret", "accept-encoding": "gzip" },
    body: JSON.stringify({ handoffId: "h1", status: "failed" }),
  });
  assert.equal(sent.status, 202);
  assert.deepEqual(await sent.json(), { status: "ok", echo: "/api/integrations/processors/events?x=1" });
  assert.equal(sent.headers.get("retry-after"), "5");

  const reached = seenByDeluno.at(-1);
  assert.deepEqual([reached.method, reached.url, reached.body], ["POST", "/api/integrations/processors/events?x=1", '{"handoffId":"h1","status":"failed"}']);
  assert.equal(reached.headers["x-api-key"], "deluno_secret", "the application got the real key");
  assert.equal(reached.headers["accept-encoding"], "identity", "the application is not asked to compress what the proxy must read");

  const { exchanges } = await control();
  const exchange = exchanges.find((entry) => entry.request.path.startsWith("/api/integrations/processors/events"));
  assert.equal(exchange.route, "to-deluno");
  assert.deepEqual(exchange.request, { method: "POST", path: "/api/integrations/processors/events?x=1", headers: { "content-type": "application/json", "x-api-key": SCRUBBED }, body: { handoffId: "h1", status: "failed" } });
  assert.deepEqual(exchange.reply, { status: 202, headers: { "content-type": "application/json; charset=utf-8", "retry-after": "5" }, body: { status: "ok", echo: "/api/integrations/processors/events?x=1" } });
  assert.equal(JSON.stringify(exchanges).includes("deluno_secret"), false, "no secret is in what was kept");
});

test("a call in the other direction is recorded as going to Weir, with a text reply as text", async () => {
  const reply = await fetch(`http://localhost:${ports.toWeir}/ready`);
  assert.equal(await reply.text(), "ready");
  const { exchanges } = await control();
  const exchange = exchanges.find((entry) => entry.route === "to-weir");
  assert.deepEqual(exchange.reply, { status: 200, headers: { "content-type": "text/plain" }, bodyText: "ready" });
  assert.deepEqual(exchange.request, { method: "GET", path: "/ready", headers: {} });
});

test("a refusal passes through as the refusal it is, and exchanges are listed after the number a caller last saw", async () => {
  assert.equal((await fetch(`http://localhost:${ports.toDeluno}/missing`)).status, 404);
  const all = await control();
  const numbers = all.exchanges.map((exchange) => exchange.seq);
  assert.deepEqual(numbers, [...numbers].sort((a, b) => a - b), "in order");
  const later = await control(numbers[0]);
  assert.deepEqual(later.exchanges.map((exchange) => exchange.seq), numbers.slice(1));
  assert.equal(later.next, numbers.at(-1));
  assert.equal(all.exchanges.at(-1).reply.status, 404);
});

test("an application that cannot be reached is answered 502 and recorded as such, not left hanging", async () => {
  const dead = await startRecorder({ deluno: "http://localhost:1", weir: "http://localhost:1", ports: { toDeluno: await freePort(), toWeir: await freePort(), control: await freePort() } });
  closing.push(...dead.servers);
  const port = dead.servers[0].address().port;
  const reply = await fetch(`http://localhost:${port}/api/x`);
  assert.equal(reply.status, 502);
  assert.match(await reply.text(), /could not reach Deluno/);
  assert.equal(dead.log[0].reply.status, 502);
});

// --- recordings ---------------------------------------------------------------------------------------------------------------

const ex = (seq, route, method, path, { body, status = 200, reply, headers = {} } = {}) => ({
  seq,
  route,
  at: "2026-10-18T10:00:00.000Z",
  request: { method, path, headers, ...(body ? { body } : {}) },
  reply: { status, headers: {}, ...(reply ? { body: reply } : {}) },
});
const MANIFEST = ex(1, "to-deluno", "GET", "/api/integrations/external/manifest", { reply: { product: "Deluno", version: "1.0.0-rc.30.33" }, headers: { "x-api-key": SCRUBBED } });
const CAPABILITIES = ex(2, "to-weir", "GET", "/api/v1/intake/capabilities", { reply: { capabilities: [] } });
const HEALTH = ex(3, "to-deluno", "GET", "/api/integrations/external/health", { reply: { ok: true } });
const READY = ex(4, "to-weir", "GET", "/ready");
const HAND = ex(5, "to-weir", "POST", "/api/v1/intake/webhook/deluno", { body: { handoffId: "aaa", releaseName: "R.A" }, reply: { status: "ok" } });
const ASK = ex(6, "to-weir", "GET", "/api/v1/intake/handoffs/deluno/aaa", { reply: { state: "queued" } });
const ASK_AGAIN = ex(7, "to-weir", "GET", "/api/v1/intake/handoffs/deluno/aaa", { reply: { state: "queued" } });
const OTHER = ex(8, "to-weir", "POST", "/api/v1/intake/webhook/deluno", { body: { handoffId: "bbb", releaseName: "R.B" }, reply: { status: "ok" } });
const REPORT = ex(9, "to-deluno", "POST", "/api/integrations/processors/events", { body: { handoffId: "aaa", status: "failed" }, status: 202, reply: { status: "failed" } });
const ASK_DONE = ex(10, "to-weir", "GET", "/api/v1/intake/handoffs/deluno/aaa", { reply: { state: "failed" } });
const LOG = [MANIFEST, CAPABILITIES, HEALTH, READY, HAND, ASK, ASK_AGAIN, OTHER, REPORT, ASK_DONE];

test("an exchange is about a hand-off when its id is in the path, the request or the reply", () => {
  assert.equal(mentions(ASK, "aaa"), true);
  assert.equal(mentions(HAND, "aaa"), true);
  assert.equal(mentions(REPORT, "aaa"), true);
  assert.equal(mentions(OTHER, "aaa"), false);
  assert.equal(mentions(ex(1, "to-weir", "GET", "/x", { reply: { handoffId: "aaa" } }), "aaa"), true);
});

test("the same question answered the same way twice in a row is kept once, and a changed answer is kept", () => {
  assert.deepEqual(withoutRepeats([ASK, ASK_AGAIN, ASK_DONE]).map((entry) => entry.seq), [6, 10]);
  assert.deepEqual(withoutRepeats([ASK, REPORT, ASK_AGAIN]).map((entry) => entry.seq), [6, 9, 7], "not a repeat when something else came between");
});

test("a conversation is what was said about one hand-off, in order, without the others", () => {
  assert.deepEqual(conversationOf(LOG, "aaa").map((entry) => entry.seq), [5, 6, 9, 10]);
  assert.deepEqual(conversationOf(LOG, "bbb").map((entry) => entry.seq), [8]);
});

test("the connection is the first time each call was made that belongs to no hand-off, without the heartbeats", () => {
  assert.deepEqual(connectionExchanges(LOG, ["aaa", "bbb"]).map((entry) => entry.seq), [1, 2]);
});

test("what a run keeps becomes recordings in the checked-in format, one per state, under the version Deluno reported", () => {
  const set = new RecordingSet();
  set.keepConnection({ state: "real-connection", note: "connecting" });
  set.keepConversation({ state: "real-failed", note: "a failure", handoffId: "aaa" });
  const { version, recordings } = set.build(LOG, "2026-10-18T11:00:00.000Z", "1.0.0-rc.30.33");
  assert.equal(version, "1.0.0-rc.30.33");
  assert.deepEqual(recordings.map((recording) => recording.state), ["real-connection", "real-failed"]);
  const failed = recordings[1];
  assert.deepEqual(Object.keys(failed), ["app", "version", "state", "recordedAt", "note", "exchanges"]);
  assert.deepEqual([failed.app, failed.version, failed.recordedAt, failed.note], ["deluno", "1.0.0-rc.30.33", "2026-10-18T11:00:00.000Z", "a failure"]);
  assert.deepEqual(Object.keys(failed.exchanges[0]), ["request", "reply"], "the proxy's own numbering is not in a recording");
  assert.deepEqual(failed.exchanges.map((entry) => entry.request.path), [HAND, ASK, REPORT, ASK_DONE].map((entry) => entry.request.path));
});

test("a run that kept nothing builds nothing, even if Weir never read the manifest", () => {
  assert.deepEqual(new RecordingSet().build([], "now", "1.0.0"), { version: "1.0.0", recordings: [] });
});

test("a conversation the proxy saw nothing of, a version no folder can be named for and a bad or repeated state are each refused, saying what", () => {
  const empty = new RecordingSet();
  empty.keepConversation({ state: "real-none", note: "x", handoffId: "zzz" });
  assert.throws(() => empty.build(LOG, "now", "1.0.0"), /saw nothing for real-none \(hand-off zzz\)/);
  const blind = new RecordingSet();
  blind.keepConnection({ state: "real-connection", note: "x" });
  assert.throws(() => blind.build(LOG, "now", "../etc"), /not one a folder can be named for/);
  const set = new RecordingSet();
  assert.throws(() => set.keepConnection({ state: "Real Connection", note: "x" }), /lower-case words/);
  set.keepConnection({ state: "real-connection", note: "x" });
  assert.throws(() => set.keepConnection({ state: "real-connection", note: "y" }), /already wanted/);
});

test("recordings are written as <version>/<state>.json in two-space JSON with a final newline", () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-recordings-test-"));
  try {
    const set = new RecordingSet();
    set.keepConversation({ state: "real-failed", note: "a failure", handoffId: "aaa" });
    const { recordings } = set.build(LOG, "2026-10-18T11:00:00.000Z", "1.0.0-rc.30.33");
    const [file] = writeRecordings(folder, recordings);
    assert.equal(file, join(folder, "1.0.0-rc.30.33", "real-failed.json"));
    const text = readFileSync(file, "utf8");
    assert.equal(text, `${JSON.stringify(recordings[0], null, 2)}\n`);
    assert.match(text, /^\{\n {2}"app": "deluno",\n {2}"version": "1.0.0-rc.30.33",/);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});
