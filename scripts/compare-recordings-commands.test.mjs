// node --test scripts/compare-recordings-commands.test.mjs
// The two recording comparers (Arr and download clients, Deluno) as commands over folders made for the test, and the
// odd recordings they have to read.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import { normaliseExchange, problems } from "./compare-arr-recordings.mjs";
import { kindOf, problems as delunoProblems, shapeOf } from "./compare-deluno-recordings.mjs";

const arrScript = fileURLToPath(new URL("./compare-arr-recordings.mjs", import.meta.url));
const delunoScript = fileURLToPath(new URL("./compare-deluno-recordings.mjs", import.meta.url));

const roots = [];
after(() => roots.forEach((root) => rmSync(root, { recursive: true, force: true })));

/** A folder holding `{ "<app>/<version>/<state>.json": document }`. */
function folderOf(files) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-recordings-"));
  roots.push(root);
  for (const [relative, document] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
    writeFileSync(path.join(root, relative), typeof document === "string" ? document : JSON.stringify(document));
  }
  return root;
}

const exchange = (reply, request = { method: "GET", path: "/api/v3/queue" }) => ({ request, reply });
const recording = (...exchanges) => ({ exchanges });
const run = (script, ...args) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });

test("a location header is compared without the number of the command it names, other headers as they are", () => {
  const withLocation = normaliseExchange(exchange({ status: 201, headers: { location: "/api/v3/command/17" } }));
  assert.equal(withLocation.reply.headers.location, "/api/v3/command/<id>");
  const without = normaliseExchange(exchange({ status: 200, headers: { etag: "x" } }));
  assert.deepEqual(without.reply.headers, { etag: "x" });
});

test("a reply without headers or body is read as empty, and a reply's text is masked", () => {
  const bare = normaliseExchange({ request: { method: "GET", path: "/a" }, reply: { status: 204 } });
  assert.deepEqual([bare.reply.headers, bare.reply.body, bare.reply.bodyText], [{}, null, null]);
  assert.equal(normaliseExchange(exchange({ status: 200, bodyText: "at 2026-10-17T10:00:00Z" })).reply.bodyText, "at <time>");
});

test("a difference is named by where it is, whatever kind of value changed", () => {
  const recorded = folderOf({
    "sonarr/4.0.0.1/queue.json": recording(exchange({ status: 200, body: { items: [1, 2], mode: "a", gone: null, kind: [] } })),
  });
  const fresh = folderOf({
    "sonarr/4.0.0.1/queue.json": recording(exchange({ status: 200, body: { items: [1, 3], mode: "a", gone: { now: 1 }, kind: {} } })),
  });
  const found = problems("sonarr", fresh, recorded);
  assert.equal(found.length, 1);
  assert.match(found[0], /sonarr\/queue: exchanges\.0\.reply\.body\.gone: null became \{"now":1\}/);
});

test("an exchange count that changed, a missing side, and several fresh versions are each reported", () => {
  const one = recording(exchange({ status: 200 }));
  const two = recording(exchange({ status: 200 }), exchange({ status: 200 }));
  const recorded = folderOf({ "sonarr/4.0.0.1/queue.json": one, "radarr/5.0.0.1/queue.json": one });
  const fresh = folderOf({ "sonarr/4.0.0.1/queue.json": two, "radarr/5.0.0.1/queue.json": one, "radarr/5.1.0.1/queue.json": one });
  assert.deepEqual(problems("sonarr", fresh, recorded), ["sonarr/queue: 1 exchange(s) before, 2 now."]);
  assert.match(problems("radarr", fresh, recorded)[0], /radarr: expected one fresh version folder/);
  assert.deepEqual(problems("deluge", fresh, recorded), ["deluge: the recorder wrote nothing."]);
  assert.match(problems("transmission", folderOf({ "transmission/4.0.0/q.json": one }), recorded)[0], /transmission: nothing is checked in under /);
});

test("versions are ordered part by part, a shorter version name counting its missing parts as zero", () => {
  const sample = recording(exchange({ status: 200, body: { v: "x" } }));
  const changed = recording(exchange({ status: 200, body: { v: "y" } }));
  const recorded = folderOf({
    "qbittorrent/5.2/queue.json": sample,
    "qbittorrent/5.2.0/queue.json": changed,
    "qbittorrent/5.10.0/queue.json": sample,
    "qbittorrent/5.9.1/queue.json": changed,
  });
  // 5.20.0 has none of its own; the newest 5.x checked in is 5.10.0, whose body is "x".
  assert.deepEqual(problems("qbittorrent", folderOf({ "qbittorrent/5.20.0/queue.json": sample }), recorded), []);
  assert.equal(problems("qbittorrent", folderOf({ "qbittorrent/5.20.0/queue.json": changed }), recorded).length, 1);
});

test("the Arr comparer says where it was misused, what differs, and when all agrees", () => {
  const same = recording(exchange({ status: 200, body: { id: 1 } }));
  const recorded = folderOf({ "sonarr/4.0.0.1/queue.json": same, "radarr/5.0.0.1/queue.json": same });
  const fresh = folderOf({ "sonarr/4.0.0.1/queue.json": recording(exchange({ status: 200, body: { id: 2 } })), "radarr/5.0.0.1/queue.json": same });
  const worse = folderOf({ "sonarr/4.0.0.1/queue.json": recording(exchange({ status: 500 })), "radarr/5.0.0.1/queue.json": same });

  for (const args of [[], [fresh, recorded, "--apps", "plex"]]) {
    const refused = run(arrScript, ...args);
    assert.equal(refused.status, 2, args.join(" "));
    assert.match(refused.stderr, /Usage: node scripts\/compare-arr-recordings\.mjs <fresh folder>/);
  }
  const agreed = run(arrScript, fresh, recorded);
  assert.equal(agreed.status, 0, agreed.stderr);
  assert.match(agreed.stdout, /Every fresh recording matches the one checked in/);
  const only = run(arrScript, worse, recorded, "--apps", "radarr");
  assert.equal(only.status, 0, only.stderr);
  const differs = run(arrScript, worse, recorded);
  assert.equal(differs.status, 1);
  assert.match(differs.stderr, /differ from the ones checked in[\s\S]*sonarr\/queue: exchanges\.0\.reply\.body: \{"id":"<number>"\} became null/);
});

test("the Deluno comparer keeps a decision's boolean, and reads an exchange with no request or reply, a path or a text body", () => {
  assert.deepEqual(shapeOf({ enqueued: true, delivered: true }), { delivered: "boolean", enqueued: true });
  const empty = JSON.parse(kindOf({}));
  assert.deepEqual([empty.path, empty.reply], ["", "null"]);
  assert.equal(JSON.parse(kindOf({ request: { method: "GET", path: "/a" }, reply: { status: 200, bodyText: "words" } })).reply, "text");
});

test("a folder that is not there or a file among the versions is no recording", () => {
  const recorded = folderOf({ "1.0.0/real-a.json": recording(exchange({ status: 200 })), "notes.txt": "a file, not a version" });
  const fresh = folderOf({ "1.0.1/real-a.json": recording(exchange({ status: 200 })), "readme.txt": "also a file" });
  assert.deepEqual(delunoProblems(fresh, recorded), []);
  assert.match(delunoProblems(path.join(fresh, "absent"), recorded)[0], /wrote no real-\* recordings/);
});

test("the Deluno comparer says where it was misused, what differs, and when all agrees", () => {
  const recorded = folderOf({ "1.0.0/real-a.json": recording(exchange({ status: 200 })) });
  const same = folderOf({ "1.0.1/real-a.json": recording(exchange({ status: 200 })) });
  const different = folderOf({ "1.0.1/real-a.json": recording(exchange({ status: 500 })) });

  const refused = run(delunoScript);
  assert.equal(refused.status, 2);
  assert.match(refused.stderr, /Usage: node scripts\/compare-deluno-recordings\.mjs <fresh folder>/);
  const agreed = run(delunoScript, same, recorded);
  assert.equal(agreed.status, 0, agreed.stderr);
  assert.match(agreed.stdout, /says what the one checked in says/);
  const differs = run(delunoScript, different, recorded);
  assert.equal(differs.status, 1);
  assert.match(differs.stderr, /differs from the one checked in:\n {2}real-a: this was said before and is not now/);
});
