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
