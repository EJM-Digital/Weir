// The weekly drift check on the real Deluno and Weir recordings: what it ignores (ids, times, how often Deluno asked) and what it reports.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";

import { kindOf, problems, shapeOf } from "./compare-deluno-recordings.mjs";

const folders = [];
after(() => folders.forEach((folder) => rmSync(folder, { recursive: true, force: true })));

/** A folder holding recordings, `{ version: { state: exchanges } }`. */
function folderOf(versions) {
  const root = mkdtempSync(join(tmpdir(), "weir-deluno-compare-"));
  folders.push(root);
  for (const [version, states] of Object.entries(versions)) {
    mkdirSync(join(root, version), { recursive: true });
    for (const [state, exchanges] of Object.entries(states)) writeFileSync(join(root, version, `${state}.json`), JSON.stringify({ app: "deluno", version, state, exchanges }));
  }
  return root;
}

const hand = (id, extra = {}) => ({
  request: { method: "POST", path: "/api/v1/intake/webhook/deluno", headers: {}, body: { handoffId: id, releaseName: `Rel.${id}`, mediaType: "movies", sourcePath: "/downloads/x", ...extra } },
  reply: { status: 200, headers: {}, body: { status: "ok", enqueued: "processing.file.remux_pass.v1" } },
});
const ask = (id, state) => ({ request: { method: "GET", path: `/api/v1/intake/handoffs/deluno/${id}`, headers: {} }, reply: { status: 200, headers: {}, body: { handoffId: id, state, queuePosition: 1 } } });
const report = (id, status, extra = {}) => ({ request: { method: "POST", path: "/api/integrations/processors/events", headers: {}, body: { handoffId: id, status, message: "words", ...extra } }, reply: { status: 202, headers: {}, body: { status } } });

const OLD = folderOf({ "1.0.0-rc.30.33": { "real-failed": [hand("01a137341a3c7198ae8f4dcabd658222"), ask("01a137341a3c7198ae8f4dcabd658222", "queued"), report("01a137341a3c7198ae8f4dcabd658222", "failed")] } });

test("ids, texts, the state a poll found and how often Deluno asked are not drift", () => {
  const now = folderOf({
    "1.0.0-rc.30.34": { "real-failed": [hand("01b237341a3c7198ae8f4dcabd6582ff", { releaseName: "Another.One" }), ask("01b237341a3c7198ae8f4dcabd6582ff", "working"), ask("01b237341a3c7198ae8f4dcabd6582ff", "queued"), report("01b237341a3c7198ae8f4dcabd6582ff", "failed", { message: "other words" })] },
  });
  assert.deepEqual(problems(now, OLD), []);
});

test("a state that holds Deluno's status poll in one run and not in the next is not drift, either way round", () => {
  const id = "01a137341a3c7198ae8f4dcabd658222";
  const withoutPoll = folderOf({ v: { "real-failed": [hand(id), report(id, "failed")] } });
  assert.deepEqual(problems(withoutPoll, OLD), []);
  assert.deepEqual(problems(OLD, withoutPoll), []);
});

test("a decision that changed, a member that went, a new member, a new status and a new route are each reported", () => {
  const id = "01a137341a3c7198ae8f4dcabd658222";
  const changed = problems(folderOf({ v: { "real-failed": [hand(id), ask(id, "queued"), report(id, "completed")] } }), OLD);
  assert.ok(changed.some((line) => line.includes("said before and is not now")) && changed.some((line) => line.includes("said now and was not before")), changed.join("\n"));
  const lost = problems(folderOf({ v: { "real-failed": [hand(id, { releaseName: undefined }), ask(id, "queued"), report(id, "failed")] } }), OLD);
  assert.ok(lost.length > 0, "a member that went");
  const added = problems(folderOf({ v: { "real-failed": [hand(id, { season: 1 }), ask(id, "queued"), report(id, "failed")] } }), OLD);
  assert.ok(added.length > 0, "a member that came");
  const route = { request: { method: "GET", path: "/api/v1/intake/somewhere", headers: {} }, reply: { status: 200, headers: {}, body: {} } };
  assert.ok(problems(folderOf({ v: { "real-failed": [hand(id), ask(id, "queued"), report(id, "failed"), route] } }), OLD).some((line) => line.includes("/somewhere")));
});

test("a state recorded before and not now, and one recorded now and not checked in, are reported by name", () => {
  const id = "01a137341a3c7198ae8f4dcabd658222";
  const found = problems(folderOf({ v: { "real-other": [hand(id)] } }), OLD);
  assert.deepEqual(found.map((line) => line.split(":")[0]), ["real-failed", "real-other"]);
});

test("a run that wrote nothing, and nothing checked in, are each a problem", () => {
  assert.match(problems(folderOf({}), OLD)[0], /wrote no real-\* recordings/);
  assert.match(problems(OLD, folderOf({}))[0], /nothing is checked in/);
});

test("the shape of a value keeps the words of a decision and the type of anything else", () => {
  assert.deepEqual(shapeOf({ status: "failed", message: "x", n: 3, ok: true, list: [{ a: 1 }, { a: 2 }], none: null }), { list: [{ a: "number" }], message: "string", n: "number", none: "null", ok: "boolean", status: "failed" });
  assert.equal(JSON.parse(kindOf(ask("01a137341a3c7198ae8f4dcabd658222", "queued"))).path, "/api/v1/intake/handoffs/deluno/<id>");
});
