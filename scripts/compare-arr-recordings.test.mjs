import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { maskPath, normalise, problems } from "./compare-arr-recordings.mjs";

const recording = (app, version, state, reply, extra = {}) => ({
  app,
  version,
  state,
  recordedAt: "2026-10-17T10:00:00Z",
  note: "n",
  exchanges: [{ request: { method: "GET", path: "/api/v3/queue?pageSize=200" }, reply: { status: 200, body: reply, ...extra } }],
});

function folder(app, version, states) {
  const root = mkdtempSync(path.join(tmpdir(), "arr-compare-"));
  const dir = path.join(root, app, version);
  mkdirSync(dir, { recursive: true });
  for (const [state, document] of Object.entries(states)) writeFileSync(path.join(dir, `${state}.json`), JSON.stringify(document));
  return root;
}

const queue = (id, size) => ({ totalRecords: 1, records: [{ id, size, title: "x", added: "2026-10-17T10:00:00Z", status: "queued" }] });

test("a fresh recording that differs only in time, ids and sizes agrees", () => {
  const before = folder("sonarr", "4.0.20.3014", { "queue-with-download": recording("sonarr", "4.0.20.3014", "queue-with-download", queue(12, 100)) });
  const now = folder("sonarr", "4.0.21.3100", {
    "queue-with-download": { ...recording("sonarr", "4.0.21.3100", "queue-with-download", queue(97, 4_000)), recordedAt: "2027-01-01T00:00:00Z" },
  });
  assert.deepEqual(problems("sonarr", now, before), []);
});

test("a changed member value, a missing member and a changed status are each named", () => {
  const before = folder("radarr", "6.4.4.1", { queue: recording("radarr", "6.4.4.1", "queue", queue(1, 1)) });
  const renamed = queue(1, 1);
  renamed.records[0].status = "downloading";
  assert.match(problems("radarr", folder("radarr", "6.4.4.1", { queue: recording("radarr", "6.4.4.1", "queue", renamed) }), before)[0], /status/);
  const missing = queue(1, 1);
  delete missing.records[0].title;
  assert.match(problems("radarr", folder("radarr", "6.4.4.1", { queue: recording("radarr", "6.4.4.1", "queue", missing) }), before)[0], /title/);
  const refused = recording("radarr", "6.4.4.1", "queue", queue(1, 1));
  refused.exchanges[0].reply.status = 401;
  assert.match(problems("radarr", folder("radarr", "6.4.4.1", { queue: refused }), before)[0], /status/);
});

test("a state that appears or disappears is named", () => {
  const before = folder("sonarr", "1.0.0.1", { a: recording("sonarr", "1.0.0.1", "a", []), b: recording("sonarr", "1.0.0.1", "b", []) });
  const now = folder("sonarr", "1.0.0.1", { a: recording("sonarr", "1.0.0.1", "a", []), c: recording("sonarr", "1.0.0.1", "c", []) });
  assert.deepEqual(problems("sonarr", now, before), ["sonarr/b: recorded before, not recorded now.", "sonarr/c: recorded now, not checked in."]);
});

test("numbers in a path and a query are set aside, text is not", () => {
  assert.equal(maskPath("/api/v3/queue/12?removeFromClient=true&seriesId=3"), "/api/v3/queue/<id>?removeFromClient=true&seriesId=<n>");
  assert.deepEqual(normalise({ id: 5, path: "/tv/Show/S01E01.mkv", added: "2026-10-17T10:00:00Z" }), {
    added: "<string>",
    id: "<number>",
    path: "/tv/Show/S01E01.mkv",
  });
});

test("how far along a command is, and the free space on a disk, do not make two runs differ", () => {
  const command = (status, extra) => ({ name: "RescanSeries", commandName: "Rescan Series", body: { seriesId: 1 }, status, id: 3, ...extra });
  const before = folder("sonarr", "1.0.0.1", { rescan: recording("sonarr", "1.0.0.1", "rescan", command("queued", { queued: "2026-10-17T10:00:00Z" })) });
  const now = folder("sonarr", "1.0.0.1", { rescan: recording("sonarr", "1.0.0.1", "rescan", command("started", { started: "2026-10-17T10:00:01Z", duration: "00:00:00.04" })) });
  assert.deepEqual(problems("sonarr", now, before), []);
  assert.deepEqual(normalise({ path: "/tv", freeSpace: 90611236864 }), { freeSpace: "<number>", path: "/tv" });
  const changed = folder("sonarr", "1.0.0.1", { rescan: recording("sonarr", "1.0.0.1", "rescan", command("queued", { body: { seriesId: 1, extra: true } })) });
  assert.match(problems("sonarr", changed, before)[0], /body/);
});

test("a download client checked in at two versions is compared with the version of the same major release", () => {
  const states = (version) => ({ test: recording("qbittorrent", version, "test", { save_path: "/downloads" }) });
  const root = mkdtempSync(path.join(tmpdir(), "client-compare-"));
  for (const version of ["4.6.7", "5.1.4"]) {
    mkdirSync(path.join(root, "qbittorrent", version), { recursive: true });
    writeFileSync(path.join(root, "qbittorrent", version, "test.json"), JSON.stringify(states(version).test));
  }
  const newer = folder("qbittorrent", "5.2.0", { test: recording("qbittorrent", "5.2.0", "test", { save_path: "/downloads" }) });
  assert.deepEqual(problems("qbittorrent", newer, root), []);
  const changed = folder("qbittorrent", "5.2.0", { test: recording("qbittorrent", "5.2.0", "test", { save_path: "/downloads", added_setting: true }) });
  assert.match(problems("qbittorrent", changed, root)[0], /added_setting/);
  const other = folder("qbittorrent", "6.0.0", { test: recording("qbittorrent", "6.0.0", "test", { save_path: "/downloads" }) });
  assert.match(problems("qbittorrent", other, root)[0], /same release line/);
});

test("the date a client sets a cookie to expire and the id of its container do not make two runs differ", () => {
  const reply = (expires, container) => ({
    status: 200,
    headers: { "set-cookie": `_session_id=<scrubbed>; Expires=${expires}; Path=/` },
    body: { host_whitelist: [container], listen_random_port: 51765, ssl_listen_port: 63165 },
  });
  const make = (expires, container, port) => {
    const document = recording("deluge", "2.2.0", "test", {});
    document.exchanges[0].reply = reply(expires, container);
    document.exchanges[0].reply.body.ssl_listen_port = port;
    return folder("deluge", "2.2.0", { test: document });
  };
  assert.deepEqual(problems("deluge", make("Sat, 17 Oct 2026 12:04:48 GMT", "25665a35c441", 22069), make("Sun, 18 Oct 2026 01:00:00 GMT", "6bd60e1a28a9", 63165)), []);
});
